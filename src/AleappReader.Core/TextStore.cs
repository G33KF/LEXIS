using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AleappReader.Core;

/// <summary>A file of the extraction as the text base knows it: its kind and type, and what reading it gave.</summary>
public sealed record TextFile(string Path, long Size, string Sha256, string Kind, string Type, string Status, int Pages, string Method);

/// <summary>A part of a file holding the searched text: a PDF page, a document's XML part, a database row.</summary>
public sealed record TextHit(long Id, TextFile File, string Part, string Text);

/// <summary>
/// Text read once from the extraction's files (PDF pages, Office documents, property lists, text files, database rows)
/// and kept, so no later search or session reads the file again; OCR adds its text here. A database of its own
/// (_reader_text.db), placed like the AI base: in the report folder beside the tags, else in
/// %LOCALAPPDATA%\AleappReader\text keyed like them. Never in the evidence database.
/// </summary>
public sealed class TextStore(string path)
{
    public const string FileName = "_reader_text.db";
    public const string HasText = "text", Scanned = "no text (scanned)", PartlyScanned = "text, some pages scanned", NoText = "no text",
        NotSupported = "not supported", TooLarge = "too large", Unreadable = "unreadable";
    public string Path => path;
    bool _schema;

    public static TextStore For(CaseSession s) => new(
        string.Equals(System.IO.Path.GetDirectoryName(s.Tags.Path), s.RootDir, StringComparison.OrdinalIgnoreCase) && s.PackagePath is null
            ? System.IO.Path.Combine(s.RootDir, FileName) : TagStore.LocalPath("text", s.Tags.Path));

    SqliteConnection Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        con.Open();
        con.CreateFunction<string, string?, bool>("regexp", ArtifactQuery.RegexMatch, isDeterministic: true);
        if (_schema) return con;
        _schema = true;
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY, size INTEGER, sha256 TEXT, kind TEXT, type TEXT, status TEXT,
              pages INTEGER, method TEXT, at INTEGER);
            CREATE TABLE IF NOT EXISTS parts(id INTEGER PRIMARY KEY, path TEXT, part TEXT, method TEXT, text TEXT);
            CREATE INDEX IF NOT EXISTS parts_path ON parts(path);
            """;
        cmd.ExecuteNonQuery();
        return con;
    }

    /// <summary>The files already read, by path, with the size they had.</summary>
    public IReadOnlyDictionary<string, long> Known()
    {
        if (!File.Exists(path)) return new Dictionary<string, long>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT path, size FROM files";
        using var r = cmd.ExecuteReader();
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    SqliteConnection? _con;
    SqliteTransaction? _tx;
    int _pending;

    /// <summary>Until disposed, saves go into transactions of 500 files: one write to disk per batch, not per file.</summary>
    public IDisposable Batch()
    {
        _con = Open();
        _tx = _con.BeginTransaction();
        return new BatchEnd(this);
    }

    sealed class BatchEnd(TextStore s) : IDisposable
    {
        public void Dispose()
        {
            s._tx?.Commit();
            s._con?.Dispose();
            (s._tx, s._con) = (null, null);
        }
    }

    /// <summary>Records a file and replaces its text parts.</summary>
    public void Save(TextFile f, IReadOnlyList<(string Part, string Text)> parts)
    {
        var own = _con is null;
        var con = _con ?? Open();
        var tx = _tx ?? con.BeginTransaction();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM parts WHERE path=@p;
                INSERT OR REPLACE INTO files(path,size,sha256,kind,type,status,pages,method,at) VALUES(@p,@s,@h,@k,@t,@st,@n,@m,@at)
                """;
            cmd.Parameters.AddWithValue("@p", f.Path);
            cmd.Parameters.AddWithValue("@s", f.Size);
            cmd.Parameters.AddWithValue("@h", f.Sha256);
            cmd.Parameters.AddWithValue("@k", f.Kind);
            cmd.Parameters.AddWithValue("@t", f.Type);
            cmd.Parameters.AddWithValue("@st", f.Status);
            cmd.Parameters.AddWithValue("@n", f.Pages);
            cmd.Parameters.AddWithValue("@m", f.Method);
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        using (var ins = con.CreateCommand())
        {
            ins.CommandText = "INSERT INTO parts(path,part,method,text) VALUES(@p,@part,@m,@t)";
            var part = ins.Parameters.Add("@part", SqliteType.Text);
            var text = ins.Parameters.Add("@t", SqliteType.Text);
            ins.Parameters.AddWithValue("@p", f.Path);
            ins.Parameters.AddWithValue("@m", f.Method);
            foreach (var (p, t) in parts)
            {
                part.Value = p;
                text.Value = t;
                ins.ExecuteNonQuery();
            }
        }
        if (own) { tx.Commit(); con.Dispose(); }
        else if (++_pending % 500 == 0) { tx.Commit(); tx.Dispose(); _tx = con.BeginTransaction(); }
    }

    /// <summary>The files recorded with a status, e.g. the PDFs whose pages are scanned images.</summary>
    public IReadOnlyList<TextFile> Files(Func<string, bool> status)
    {
        if (!File.Exists(path)) return [];
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT path,size,sha256,kind,type,status,pages,method FROM files";
        using var r = cmd.ExecuteReader();
        var list = new List<TextFile>();
        while (r.Read()) if (status(r.GetString(5))) list.Add(ReadFile(r, 0));
        return list;
    }

    static TextFile ReadFile(SqliteDataReader r, int at) => new(r.GetString(at), r.GetInt64(at + 1), r.GetString(at + 2), r.GetString(at + 3),
        r.GetString(at + 4), r.GetString(at + 5), r.GetInt32(at + 6), r.GetString(at + 7));

    /// <summary>The parts whose text matches the query (<see cref="SearchQuery"/> syntax), of files of these kinds (null: all).</summary>
    public IReadOnlyList<TextHit> Search(string query, IReadOnlyCollection<string>? kinds = null, int max = 5000, CancellationToken ct = default)
    {
        var q = SearchQuery.Parse(query);
        if (q.Terms.Count == 0 || !File.Exists(path)) return [];
        using var con = Open();
        using var cmd = con.CreateCommand();
        var where = q.Sql(cmd, [new ColumnInfo("text", "Text", null)]);
        var kindSql = kinds is null ? "" : " AND kind IN (" + string.Join(",", kinds.Select((k, i) =>
        {
            cmd.Parameters.AddWithValue("@k" + i, k);
            return "@k" + i;
        })) + ")";
        cmd.CommandText = $"""
            SELECT * FROM (SELECT f.path,f.size,f.sha256,f.kind,f.type,f.status,f.pages,p.method,p.id,p.part,p.text
              FROM parts p JOIN files f ON f.path=p.path) WHERE {where}{kindSql} LIMIT {max}
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<TextHit>();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new TextHit(r.GetInt64(8), ReadFile(r, 0), r.GetString(9), r.GetString(10)));
        }
        return list;
    }
}

/// <summary>Reads the text of the extraction's files into the <see cref="TextStore"/>: each file once, the ones already
/// read (same path and size) skipped. PDF pages (PdfPig), Office and OpenDocument files (their XML), property lists,
/// text, JSON and XML files, SQLite rows. A PDF page with images and no text is marked scanned, for OCR.</summary>
public static partial class ContentIndex
{
    /// <summary>The kinds of file whose text can be read.</summary>
    public static readonly IReadOnlyList<string> Kinds = [RawFiles.Documents, RawFiles.Databases, RawFiles.Plists, RawFiles.Structured, RawFiles.PlainText];
    public const long MaxBytes = 200L << 20;
    // ponytail: rows past this per database are not read; raise it, or read the database in the SQLite reader.
    public const int MaxRowsPerDatabase = 200_000;

    /// <summary>Reads the files of <paramref name="kinds"/> not read yet. Files named like nothing (most Android databases)
    /// are sniffed by their first bytes. Returns how many files were read now.</summary>
    public static int Build(TextStore store, ExtractionFiles x, IReadOnlyCollection<string> kinds,
                            IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var known = store.Known();
        var all = x.Files().Where(f => !f.Path.Contains(ExtractionFiles.Inside)).ToList();
        var sizes = all.ToDictionary(f => f.Path, f => f.Size, StringComparer.Ordinal);
        bool Sidecar(string p) => p.EndsWith("-wal", StringComparison.Ordinal) || p.EndsWith("-shm", StringComparison.Ordinal) || p.EndsWith("-journal", StringComparison.Ordinal);
        var todo = all.Where(f => !Sidecar(f.Path) && !(known.TryGetValue(f.Path, out var size) && size == f.Size) &&
                                  RawFiles.Category(f.Path, "") is var byName && (kinds.Contains(byName) || byName == RawFiles.Other))
                      .Select(f => f.Path).ToList();
        if (todo.Count == 0) return 0;
        var paths = todo.Concat(kinds.Contains(RawFiles.Databases)
            ? todo.SelectMany(p => new[] { p + "-wal", p + "-shm", p + "-journal" }).Where(sizes.ContainsKey) : []).ToList();
        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LEXIS-text", Guid.NewGuid().ToString("N"));
        var databases = new List<(string Rel, string Copy)>();
        var sidecars = new Dictionary<string, string>(StringComparer.Ordinal);
        int done = 0, read = 0;
        using var batch = store.Batch();
        void Next() => progress?.Report((++done, todo.Count));
        try
        {
            x.Each(paths, (rel, s) =>
            {
                ct.ThrowIfCancellationRequested();
                var size = sizes.GetValueOrDefault(rel);
                if (Sidecar(rel)) { sidecars[rel] = CopyTo(s, temp); return; }
                var head = new byte[4096];
                var n = s.ReadAtLeast(head, head.Length, false);
                var type = RawFiles.Sniff(head.AsSpan(0, n));
                var kind = RawFiles.Category(rel, type);
                if (!kinds.Contains(kind))
                {
                    // not wanted now: kept as read only when there is no text to read in it at all, so a later choice reads it
                    if (!Kinds.Contains(kind)) store.Save(new TextFile(rel, size, "", kind, type, TextStore.NoText, 0, ""), []);
                    Next();
                    return;
                }
                if (type == "SQLite database")
                {
                    databases.Add((rel, CopyTo(Prefixed(head, n, s), temp)));
                    return; // read after the pass, with its -wal and -journal
                }
                if (size > MaxBytes) store.Save(new TextFile(rel, size, "", kind, type, TextStore.TooLarge, 0, ""), []);
                else
                {
                    using var ms = new MemoryStream();
                    Prefixed(head, n, s).CopyTo(ms);
                    var bytes = ms.ToArray();
                    var (status, pages, method, parts) = Read(rel, type, kind, bytes);
                    store.Save(new TextFile(rel, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), kind, type, status, pages, method), parts);
                }
                read++;
                Next();
            }, ct, _ => Next());
            foreach (var (rel, copy) in databases)
            {
                ct.ThrowIfCancellationRequested();
                string? Side(string suffix) => sidecars.GetValueOrDefault(rel + suffix);
                TextFile f;
                List<(string, string)> parts = [];
                string sha;
                using (var fs = File.OpenRead(copy)) sha = Convert.ToHexStringLower(SHA256.HashData(fs));
                try
                {
                    using var db = SqliteFile.Open(copy, System.IO.Path.GetFileName(rel), Side("-wal"), Side("-shm"), Side("-journal"));
                    parts = Rows(db, ct);
                    f = new TextFile(rel, sizes.GetValueOrDefault(rel), sha, RawFiles.Databases, "SQLite database", parts.Count > 0 ? TextStore.HasText : TextStore.NoText,
                                     0, "SQLite rows" + (db.HasWal ? " (with the -wal)" : ""));
                }
                catch (SqliteException e) { f = new TextFile(rel, sizes.GetValueOrDefault(rel), sha, RawFiles.Databases, "SQLite database", TextStore.Unreadable + ": " + e.Message, 0, ""); }
                store.Save(f, parts);
                read++;
                Next();
            }
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return read;
    }

    /// <summary>The stream with the head already read from it put back in front.</summary>
    static Stream Prefixed(byte[] head, int n, Stream rest)
    {
        var ms = new MemoryStream();
        ms.Write(head, 0, n);
        rest.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }

    static string CopyTo(Stream s, string dir)
    {
        Directory.CreateDirectory(dir);
        var to = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N"));
        using var o = File.Create(to);
        s.CopyTo(o);
        return to;
    }

    /// <summary>The text of one file: status, pages, how it was read, and its parts.</summary>
    public static (string Status, int Pages, string Method, List<(string Part, string Text)> Parts) Read(string rel, string type, string kind, byte[] bytes)
    {
        List<(string, string)> parts = [];
        try
        {
            if (type == "PDF document") return Pdf(bytes);
            if (type.StartsWith("ZIP archive") && kind == RawFiles.Documents)
            {
                parts = Office(bytes);
                return (parts.Count > 0 ? TextStore.HasText : TextStore.NotSupported, 0, "document XML", parts);
            }
            if (type is "Binary plist" or "XML plist")
            {
                if (PlistTree.Read(bytes) is { } root) parts.Add(("", Leaves(root)));
                return (parts.Count > 0 ? TextStore.HasText : TextStore.Unreadable, 0, "property list", parts);
            }
            if (RawFiles.AsText(bytes) is { } text)
            {
                parts.Add(("", text));
                return (text.Trim() == "" ? TextStore.NoText : TextStore.HasText, 0, "text", parts);
            }
            return (TextStore.NotSupported, 0, "", parts); // legacy .doc/.xls, iWork, binary XML
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (TextStore.Unreadable + ": " + e.Message, 0, "", []); // damaged or encrypted: the status says so
        }
    }

    static (string, int, string, List<(string, string)>) Pdf(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        List<(string, string)> parts = [];
        int scanned = 0;
        foreach (var page in doc.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page);
            if (text.Trim() != "") parts.Add(($"page {page.Number}", text));
            else if (page.GetImages().Any()) scanned++;
        }
        var status = scanned == 0 ? (parts.Count > 0 ? TextStore.HasText : TextStore.NoText)
                   : parts.Count == 0 ? TextStore.Scanned : TextStore.PartlyScanned;
        return (status, doc.NumberOfPages, "PDF text (PdfPig)", parts);
    }

    // The parts of a Word, Excel, PowerPoint or OpenDocument file that hold its text.
    [GeneratedRegex(@"^(word/(document|header\d*|footer\d*|footnotes|endnotes|comments)|xl/sharedStrings|xl/worksheets/sheet\d+|ppt/(slides|notesSlides)/\w+\d+|content)\.xml$")]
    private static partial Regex TextPart();

    static List<(string, string)> Office(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        List<(string, string)> parts = [];
        foreach (var e in zip.Entries.Where(e => TextPart().IsMatch(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = e.Open();
            if (XmlText(s) is { Length: > 0 } t) parts.Add((e.FullName[..^4], t));
        }
        return parts;
    }

    /// <summary>The text nodes of an XML part, a line per paragraph or row. No DTDs or external entities: the file is untrusted.</summary>
    static string XmlText(Stream s)
    {
        var sb = new StringBuilder();
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (r.Read())
        {
            if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace) sb.Append(r.Value);
            else if (r.NodeType == XmlNodeType.Element && r.LocalName is "tab" or "c" or "tc" or "table-cell") sb.Append('\t');
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName is "p" or "row" or "tr" or "si" or "h" or "table-row") sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    static string Leaves(PlistNode root)
    {
        var sb = new StringBuilder();
        void W(PlistNode n)
        {
            if (n.Children.Count == 0 && n.Value != "") sb.Append(n.Path == "" ? n.Key : n.Path).Append(" = ").Append(n.Value).Append('\n');
            foreach (var c in n.Children) W(c);
        }
        W(root);
        return sb.ToString();
    }

    /// <summary>Each row with text as "column: value" lines, its part "table #rowid".</summary>
    static List<(string, string)> Rows(SqliteFile db, CancellationToken ct)
    {
        List<(string, string)> parts = [];
        var tables = db.Objects(true).Where(o => o.Type == "table" && !o.Name.StartsWith("sqlite_", StringComparison.Ordinal)).ToList();
        using var con = db.Connect(true);
        foreach (var t in tables)
        {
            try
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT {(t.HasRowId ? "rowid, " : "")}* FROM {ArtifactQuery.Q(t.Name)}";
                using var r = cmd.ExecuteReader();
                var first = t.HasRowId ? 1 : 0;
                long n = 0;
                while (r.Read() && parts.Count < MaxRowsPerDatabase)
                {
                    ct.ThrowIfCancellationRequested();
                    n++;
                    var sb = new StringBuilder();
                    for (int i = first; i < r.FieldCount; i++)
                    {
                        if (r.IsDBNull(i)) continue;
                        var v = r.GetValue(i) switch
                        {
                            string s => s,
                            byte[] b when b.Length <= 65536 => RawFiles.AsText(b),
                            byte[] => null,
                            var o => Convert.ToString(o, CultureInfo.InvariantCulture),
                        };
                        if (!string.IsNullOrWhiteSpace(v)) sb.Append(r.GetName(i)).Append(": ").Append(v).Append('\n');
                    }
                    if (sb.Length > 0) parts.Add(($"{t.Name} #{(t.HasRowId ? r.GetInt64(0) : n)}", sb.ToString()));
                }
            }
            catch (SqliteException) { } // a virtual table whose module is missing
        }
        return parts;
    }
}
