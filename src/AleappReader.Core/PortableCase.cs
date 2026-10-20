using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>Which files of the extraction a portable case carries: none, the source files of its artifacts, or every
/// file the parsing read (the ones ALEAPP copied under data/).</summary>
public enum FileSystemScope { None, Sources, AllRead }

/// <summary>What goes into a portable case: the artifacts (null = every one), only their tagged records, the media
/// files, the extraction's own files.</summary>
/// <param name="TaggedFiles">Also the files tagged in Extraction Files or the gallery (a file inside an archive brings
/// the archive), whatever <paramref name="FileSystem"/> says.</param>
public sealed record PortableSelection(IReadOnlySet<string>? Tables = null, bool TaggedOnly = false, bool Media = true,
                                       FileSystemScope FileSystem = FileSystemScope.None, bool TaggedFiles = false)
{
    public string Describe() =>
        (Tables is null ? "all artifacts" : $"{Tables.Count} selected artifacts") + (TaggedOnly ? ", tagged records only" : "") +
        (Media ? "" : ", without media files") +
        FileSystem switch
        {
            FileSystemScope.Sources => ", with the artifacts' source files from the extraction",
            FileSystemScope.AllRead => ", with every file the parsing read from the extraction",
            _ => "",
        } + (TaggedFiles ? ", with the tagged files of the extraction" : "");

    /// <summary>What the portable case's file system holds, for its manifest; null when it holds none.</summary>
    public string? FileSystemContent() =>
        string.Join(" and ", new[]
        {
            FileSystem switch { FileSystemScope.Sources => "source files of the included artifacts", FileSystemScope.AllRead => "every file the parsing read", _ => null },
            TaggedFiles ? "tagged files" : null,
        }.OfType<string>()) is { Length: > 0 } s ? s : null;
}

/// <summary>An extraction file for a portable case, as a path in the extraction: read from the extraction, from
/// ALEAPP's copy when the extraction does not have it, or missing from both (Size -1).</summary>
public sealed record PortableFile(string Rel, long Size, bool FromExtraction)
{
    public string Origin => FromExtraction ? "extraction" : Size < 0 ? "missing" : "ALEAPP copy";
}

/// <summary>A portable case checked file by file against its SHA256SUMS.txt, and its signature: Problems lists the files
/// modified, missing or added; SignatureValid is null when the package is not signed; Root is the SHA-256 of
/// SHA256SUMS.txt, which names the content whatever the zip is called or wherever it is.</summary>
public sealed record PackageCheck(IReadOnlyList<string> Problems, string? Fingerprint, bool? SignatureValid, string Root)
{
    /// <summary>SHA-256 of each entry as SHA256SUMS.txt lists it.</summary>
    public IReadOnlyDictionary<string, string> Sums { get; init; } = new Dictionary<string, string>();
    public bool Intact => Problems.Count == 0 && SignatureValid != false;

    public string Describe() => Intact
        ? "Portable case intact: every file matches SHA256SUMS.txt" + (SignatureValid == true
            ? $", which is signed by the key with fingerprint {Fingerprint}. Compare it with the fingerprint the examiner gave you by another channel."
            : ". It is not signed, so who made it is not established by the package.")
        : $"Portable case ALTERED: {Problems.Count} problem(s); its content is not what was packaged.\n" + string.Join("\n", Problems.Take(30)) +
          (Problems.Count > 30 ? $"\n… and {Problems.Count - 30} more" : "");
}

/// <summary>
/// Portable case: one zip with everything the reader needs (Case/: manifest, database, timeline, media,
/// the copies of the source files, tags; SHA256SUMS.txt over every Case/ file) and the reader beside it, which opens
/// the zip it sits next to. The examiner's paths stay out; the data source is recorded as it was when the case was made.
/// </summary>
public static partial class PortableCase
{
    public const string HashFile = "SHA256SUMS.txt";
    /// <summary>The ECDSA P-256 signature (SHA-256, base64) of SHA256SUMS.txt's bytes, and the public key that checks it.</summary>
    public const string SignatureFile = "SHA256SUMS.txt.sig";
    public const string PublicKeyFile = "examiner-public-key.pem";
    public const string CaseDir = "Case";
    public const string ReaderDir = "Reader";
    /// <summary>Where a portable case keeps the extraction's files, under Case/, at their paths in the extraction.</summary>
    public const string ExtractionDir = "extraction";

    /// <summary>The artifacts a selection takes: the chosen ones, and with TaggedOnly only those with tags.</summary>
    static List<ArtifactInfo> Chosen(CaseSession s, PortableSelection sel)
    {
        var tagged = sel.TaggedOnly ? s.Tags.All().Select(t => t.TableName).ToHashSet() : null;
        return s.Manifest.Artifacts.Where(a => (sel.Tables?.Contains(a.TableName) ?? true) && (tagged?.Contains(a.TableName) ?? true)).ToList();
    }

    /// <summary>The extraction files the selection carries, with -wal/-shm/-journal beside each when there is one, and
    /// their sizes, for the estimate and the build. A source in neither the extraction nor data/ is listed as missing.</summary>
    public static IReadOnlyList<PortableFile> FileSystemFiles(CaseSession s, PortableSelection sel, CancellationToken ct = default)
    {
        if (sel.FileSystem == FileSystemScope.None && !sel.TaggedFiles) return [];
        var x = s.Extraction;
        var data = Path.Combine(s.RootDir, "data");
        IEnumerable<string> wanted = sel.FileSystem == FileSystemScope.Sources
            ? Chosen(s, sel).SelectMany(EvidenceOrigin.RecordedSources).Select(p => p.Replace('\\', '/').TrimStart('/'))
            : sel.FileSystem == FileSystemScope.AllRead && Directory.Exists(data)
                ? Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(data, f).Replace('\\', '/'))
            : [];
        // tagged files: a file inside an archive brings the archive, which the recipient explores in turn; a tag on a
        // report's own media file (no path in the extraction) adds nothing, the media are carried with the case
        var tagged = sel.TaggedFiles
            ? s.Tags.FileTags().Keys.Select(k => k.IndexOf(ExtractionFiles.Inside, StringComparison.Ordinal) is var cut and > 0 ? k[..cut] : k).ToHashSet(StringComparer.Ordinal)
            : [];
        var list = new List<PortableFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in wanted.Concat(tagged).Where(p => p != "" && !p.Split('/').Contains("..") && !p.Contains(':')))
            foreach (var rel in new[] { source, source + "-wal", source + "-shm", source + "-journal" })
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(rel)) continue;
                if (x?.SizeOf(rel) is long size and >= 0) list.Add(new(rel, size, true));
                else if (s.ResolveInCase(Path.Combine("data", rel)) is { } c && File.Exists(c)) list.Add(new(rel, new FileInfo(c).Length, false));
                else if (rel == source && !tagged.Contains(source)) list.Add(new(rel, -1, false));
            }
        return list;
    }

    /// <param name="reader">The reader to put beside the zip: a single-file exe, or a build folder (copied to Reader\ with
    /// "Open Case.cmd" that opens the zip); null for none.</param>
    /// <param name="signer">The examiner's key, which signs SHA256SUMS.txt; its public half goes into the package.</param>
    public static void Build(CaseSession s, string zipPath, PortableSelection sel, string? reader, string readerExe = "AleappReader.exe",
        IProgress<string>? log = null, CancellationToken ct = default, IProgress<(long Done, long Total)>? bytes = null, ECDsa? signer = null)
    {
        var staging = Path.Combine(Path.GetTempPath(), "AleappReader", "pc_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var (caseFiles, counts) = sel.Tables is null && !sel.TaggedOnly ? (CaseFilesAll(s, sel.Media), null) : Stage(s, staging, sel, log);
            var fs = FileSystemFiles(s, sel, ct);
            caseFiles.Insert(0, (WriteManifest(s, staging, counts, sel.Describe(), sel.FileSystemContent(), fs), Path.GetFileName(s.ManifestPath)));
            // written aside and moved in place when complete, so a cancelled or failed build leaves no half-written zip
            var part = zipPath + ".part";
            File.Delete(part);
            try
            {
                using (var zip = ZipFile.Open(part, ZipArchiveMode.Create))
                {
                    var sums = new StringBuilder();
                    long total = caseFiles.Sum(f => new FileInfo(f.Item1).Length) + fs.Where(f => f.Size > 0).Sum(f => f.Size), done = 0;
                    void Wrote(int n) => bytes?.Report((done += n, total));
                    foreach (var (src, rel) in caseFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = $"{CaseDir}/{rel.Replace('\\', '/')}";
                        log?.Report("Adding " + name);
                        using var input = File.OpenRead(src);
                        sums.Append(AddHashed(zip, input, name, File.GetLastWriteTime(src), Wrote, ct)).Append("  ").Append(name).Append('\n');
                    }
                    AddFileSystem(s, zip, fs, sums, log, Wrote, ct);
                    Text(zip, HashFile, sums.ToString());
                    if (signer is not null)
                    {
                        Text(zip, SignatureFile, Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(sums.ToString()), HashAlgorithmName.SHA256)));
                        Text(zip, PublicKeyFile, signer.ExportSubjectPublicKeyInfoPem());
                        log?.Report("Signed by the key with fingerprint " + Fingerprint(signer));
                    }
                }
                File.Move(part, zipPath, overwrite: true);
            }
            finally { try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            if (reader is not null) PlaceReader(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, Path.GetFileName(zipPath), reader, readerExe, log, ct);
            log?.Report("Portable case written: " + zipPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static void PlaceReader(string dir, string zipName, string reader, string readerExe, IProgress<string>? log, CancellationToken ct)
    {
        if (File.Exists(reader))
        {
            var dest = Path.Combine(dir, Path.GetFileName(reader));
            log?.Report("Adding the reader: " + dest);
            if (!string.Equals(Path.GetFullPath(reader), dest, StringComparison.OrdinalIgnoreCase)) File.Copy(reader, dest, true);
            return;
        }
        var target = Path.Combine(dir, ReaderDir);
        if (!Directory.Exists(reader) || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(reader)), target, StringComparison.OrdinalIgnoreCase)) return;
        log?.Report("Adding the reader folder");
        foreach (var f in Directory.EnumerateFiles(reader, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, Path.GetRelativePath(reader, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
        File.WriteAllText(Path.Combine(dir, "Open Case.cmd"), $"@echo off\r\nstart \"\" \"%~dp0{ReaderDir}\\{readerExe}\" \"%~dp0{zipName}\"\r\n");
    }

    /// <summary>The portable case zip in a folder (one holding SHA256SUMS.txt), for a reader shipped beside it; null when
    /// there is none or more than one, so the reader never opens one of several cases by guessing.</summary>
    public static string? FindBeside(string dir)
    {
        var found = Directory.EnumerateFiles(dir, "*.zip").Where(z =>
        {
            try { using var zip = ZipFile.OpenRead(z); return zip.GetEntry(HashFile) is not null; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
        }).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    static List<(string Src, string Rel)> CaseFilesAll(CaseSession s, bool media)
    {
        var files = new List<(string, string)> { (s.DbPath, Path.GetFileName(s.DbPath)) };
        if (s.HasTimeline) files.Add((s.TimelinePath, "_Timeline/tl.db"));
        foreach (var dir in media ? ["media", "data"] : new[] { "data" })
        {
            var full = Path.Combine(s.RootDir, dir);
            if (Directory.Exists(full))
                files.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => (f, Path.GetRelativePath(s.RootDir, f))));
        }
        if (File.Exists(s.Tags.Path)) files.Add((s.Tags.Path, TagStore.FileName));
        return files;
    }

    /// <summary>Copies the selected artifacts' rows (rowids kept, so tags still match), ALEAPP's copies of their source
    /// files and the media they reference.</summary>
    static (List<(string, string)> Files, Dictionary<string, long> Counts) Stage(CaseSession s, string staging, PortableSelection sel, IProgress<string>? log)
    {
        var tagged = sel.TaggedOnly
            ? s.Tags.All().GroupBy(t => t.TableName).ToDictionary(g => g.Key, g => g.Select(t => t.RowId).ToHashSet())
            : null;
        var arts = Chosen(s, sel);
        var dbPath = Path.Combine(staging, Path.GetFileName(s.DbPath));
        var mediaIds = new HashSet<string>();
        var counts = new Dictionary<string, long>();
        var files = new List<(string, string)> { (dbPath, Path.GetFileName(dbPath)) };

        using var src = s.OpenDb();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        dst.Open();
        using var tx = dst.BeginTransaction();
        foreach (var art in arts)
        {
            log?.Report("Copying records: " + art.Name);
            Exec(dst, CreateSql(src, art.TableName) ?? throw new InvalidDataException("No table " + art.TableName));
            var cols = ArtifactQuery.ExistingColumns(src, art);
            using var ins = dst.CreateCommand();
            ins.CommandText = $"INSERT INTO {ArtifactQuery.Q(art.TableName)} (rowid, {string.Join(",", cols.Select(c => ArtifactQuery.Q(c.Sql)))}) " +
                              $"VALUES (@rowid, {string.Join(",", cols.Select((_, i) => "@p" + i))})";
            long n = 0;
            foreach (var row in ArtifactQuery.All(src, art, rowIds: tagged?[art.TableName]))
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("@rowid", row.RowId);
                for (int i = 0; i < cols.Count; i++)
                {
                    ins.Parameters.AddWithValue("@p" + i, row.Values[i] ?? DBNull.Value);
                    if (sel.Media && cols[i].IsMedia) mediaIds.UnionWith(MediaResolver.RefIds(row.Values[i]));
                }
                ins.ExecuteNonQuery();
                n++;
            }
            counts[art.TableName] = n;
        }

        // Media tables: same schema as the source, only the referenced rows.
        var mediaFiles = new List<string>();
        if (CreateSql(src, "_lava_media_items") is { } itemsSql && CreateSql(src, "_lava_media_references") is { } refsSql)
        {
            Exec(dst, itemsSql);
            Exec(dst, refsSql);
            if (CreateSql(src, "_lava_media_info") is { } viewSql) Exec(dst, viewSql);
            var resolver = new MediaResolver(s);
            foreach (var id in mediaIds)
            {
                CopyRow(src, dst, "_lava_media_references", "id", id);
                if (resolver.Get(src, id) is not { } m) continue;
                using var q = src.CreateCommand();
                q.CommandText = "SELECT media_item_id FROM _lava_media_references WHERE id=@id";
                q.Parameters.AddWithValue("@id", id);
                if (q.ExecuteScalar() is string itemId) CopyRow(src, dst, "_lava_media_items", "id", itemId);
                if (m.Exists) mediaFiles.Add(m.LocalFile());
            }
        }
        tx.Commit();

        // The media files and ALEAPP's copies of the source files (for their SHA-256), at the same place in the case.
        var copies = arts.SelectMany(EvidenceOrigin.RecordedSources)
            .Select(p => s.ResolveInCase(Path.Combine("data", p.Replace('\\', '/').TrimStart('/')))).OfType<string>().Where(File.Exists);
        foreach (var f in mediaFiles.Concat(copies).Distinct(StringComparer.OrdinalIgnoreCase))
            files.Add((f, Path.GetRelativePath(s.RootDir, f)));
        if (File.Exists(s.Tags.Path))
        {   // only the chosen artifacts' tags and notes travel, and the files' tags when the tagged files do
            var tags = Path.Combine(staging, TagStore.FileName);
            using (var tagSrc = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = s.Tags.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var tagDst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tags, Pooling = false }.ToString()))
            {
                tagSrc.Open();
                tagDst.Open();
                tagSrc.BackupDatabase(tagDst);
                using var cmd = tagDst.CreateCommand();
                cmd.CommandText = "DELETE FROM tags WHERE table_name NOT IN (SELECT value FROM json_each($t)) AND NOT ($f AND substr(table_name, 1, 5) = 'file:')";
                cmd.Parameters.AddWithValue("$f", sel.TaggedFiles);
                cmd.Parameters.AddWithValue("$t", System.Text.Json.JsonSerializer.Serialize(arts.Select(a => a.TableName)));
                cmd.ExecuteNonQuery();
            }
            files.Add((tags, TagStore.FileName));
        }
        return (files, counts);
    }

    [GeneratedRegex(@"^([A-Za-z]:[\\/]|\\\\)")]
    private static partial Regex WindowsPath();

    /// <summary>The manifest of the package: only the copied artifacts with their new record counts, no path of the
    /// examiner's machine (a path keeps only its file or folder name), the data source frozen in portable_case.</summary>
    static string WriteManifest(CaseSession s, string staging, IReadOnlyDictionary<string, long>? counts, string content,
                                string? fsContent = null, IReadOnlyList<PortableFile>? fs = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(s.ManifestPath))!.AsObject();
        if (counts is not null && root["artifacts"] is JsonObject cats)
        {
            foreach (var cat in cats.ToList())
            {
                if (cat.Value is not JsonArray arr) continue;
                foreach (var a in arr.ToList())
                {
                    var t = a?["tablename"]?.GetValue<string>();
                    if (t is null || !counts.TryGetValue(t, out var c)) arr.Remove(a);
                    else a!["record_count"] = c;
                }
                if (arr.Count == 0) cats.Remove(cat.Key);
            }
        }
        var provenance = EvidenceOrigin.Provenance(s); // read while the extraction's full path is still known
        foreach (var (key, value) in root.ToList())
            if (value is JsonValue v && v.TryGetValue<string>(out var str) && WindowsPath().IsMatch(str))
                root[key] = Path.GetFileName(str.TrimEnd('\\', '/'));
        root["portable_case"] = new JsonObject
        {
            ["content"] = content,
            ["created_utc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["source_manifest"] = Path.GetFileName(s.ManifestPath),
            ["provenance"] = new JsonArray(provenance.Select(p => (JsonNode)new JsonObject
                { ["group"] = p.Group, ["name"] = p.Name, ["value"] = p.Value }).ToArray()),
        };
        if (fsContent is not null)
            root["portable_case"]!["file_system"] = new JsonObject
            {
                ["scope"] = fsContent,
                ["folder"] = $"{CaseDir}/{ExtractionDir}",
                ["files"] = new JsonArray((fs ?? []).Select(f => (JsonNode)new JsonObject
                    { ["path"] = f.Rel, ["device_path"] = EvidenceOrigin.DevicePath(f.Rel), ["size"] = f.Size, ["read_from"] = f.Origin }).ToArray()),
            };
        var path = Path.Combine(staging, Path.GetFileName(s.ManifestPath));
        File.WriteAllText(path, root.ToJsonString(new() { WriteIndented = true }));
        return path;
    }

    static string? CreateSql(SqliteConnection con, string name)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteScalar() as string;
    }

    static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void CopyRow(SqliteConnection src, SqliteConnection dst, string table, string keyCol, string key)
    {
        using var q = src.CreateCommand();
        q.CommandText = $"SELECT * FROM {ArtifactQuery.Q(table)} WHERE {ArtifactQuery.Q(keyCol)}=@k";
        q.Parameters.AddWithValue("@k", key);
        using var r = q.ExecuteReader();
        if (!r.Read()) return;
        using var ins = dst.CreateCommand();
        var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        ins.CommandText = $"INSERT OR IGNORE INTO {ArtifactQuery.Q(table)} ({string.Join(",", names.Select(ArtifactQuery.Q))}) " +
                          $"VALUES ({string.Join(",", names.Select((_, i) => "@p" + i))})";
        for (int i = 0; i < names.Count; i++) ins.Parameters.AddWithValue("@p" + i, r.IsDBNull(i) ? DBNull.Value : r.GetValue(i));
        ins.ExecuteNonQuery();
    }

    /// <summary>The extraction files, at their paths in the extraction under Case/extraction/, read once (one pass over a
    /// tar.gz). Each one ALEAPP also copied must have the same SHA-256 as that copy: any difference, or a file the
    /// extraction lists but cannot give, stops the build, so no portable case carries files that disagree.</summary>
    static void AddFileSystem(CaseSession s, ZipArchive zip, IReadOnlyList<PortableFile> fs, StringBuilder sums, IProgress<string>? log,
                              Action<int> wrote, CancellationToken ct)
    {
        if (fs.Count == 0) return;
        var different = new List<string>();
        void Add(string rel, Stream src, bool check)
        {
            var name = $"{CaseDir}/{ExtractionDir}/{rel}";
            log?.Report("Adding " + name);
            var sha = AddHashed(zip, src, name, null, wrote, ct);
            sums.Append(sha).Append("  ").Append(name).Append('\n');
            if (check && s.ResolveInCase(Path.Combine("data", rel)) is { } copy && File.Exists(copy))
            {
                using var f = File.OpenRead(copy);
                if (Convert.ToHexStringLower(SHA256.HashData(f)) != sha) different.Add(EvidenceOrigin.DevicePath(rel));
            }
        }
        var unreadable = new List<string>();
        if (s.Extraction is { } x) x.Each(fs.Where(f => f.FromExtraction).Select(f => f.Rel), (rel, st) => Add(rel, st, true), ct, unreadable.Add);
        foreach (var f in fs.Where(f => !f.FromExtraction && f.Size >= 0))
        {
            using var copy = File.OpenRead(s.ResolveInCase(Path.Combine("data", f.Rel))!);
            Add(f.Rel, copy, false);
        }
        if (unreadable.Count > 0)
            throw new IOException($"{unreadable.Count} file(s) of the extraction cannot be read, the portable case was not written: {EvidenceOrigin.Join(unreadable.Select(EvidenceOrigin.DevicePath).ToList())}");
        if (different.Count > 0)
            throw new InvalidDataException($"{different.Count} file(s) of the extraction differ from ALEAPP's copies, the portable case was not written: {EvidenceOrigin.Join(different)}");
    }

    static string AddHashed(ZipArchive zip, Stream input, string name, DateTime? modified, Action<int>? wrote = null, CancellationToken ct = default)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        if (modified is { } m) entry.LastWriteTime = m;
        using var output = entry.Open();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested(); // a single video can take minutes
            sha.AppendData(buf, 0, n);
            output.Write(buf, 0, n);
            wrote?.Invoke(n);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    static void Text(ZipArchive zip, string name, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    /// <summary>A key's fingerprint: the SHA-256 of its public key (SubjectPublicKeyInfo), in groups of four.</summary>
    public static string Fingerprint(ECDsa key) =>
        string.Join(" ", Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).Chunk(4).Select(c => new string(c)));

    /// <summary>
    /// Checks a portable case zip, read-only and without unpacking it: every entry against SHA256SUMS.txt (modified,
    /// missing, added or repeated) and the signature of SHA256SUMS.txt with the public key it carries. Null when the
    /// zip is not a portable case.
    /// </summary>
    public static PackageCheck? Check(string zipPath, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry(HashFile) is not { } sumsEntry) return null;
        byte[] Bytes(ZipArchiveEntry e) { using var st = e.Open(); using var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray(); }
        var sumsBytes = Bytes(sumsEntry);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(sumsBytes).Split('\n'))
            if (line.IndexOf("  ", StringComparison.Ordinal) is > 0 and var sep) expected.TryAdd(line[(sep + 2)..].TrimEnd('\r'), line[..sep]);

        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (e.FullName.EndsWith('/') || e.FullName is HashFile or SignatureFile or PublicKeyFile && zip.Entries.Count(x => x.FullName == e.FullName) == 1) continue;
            if (!expected.TryGetValue(e.FullName, out var sha)) { problems.Add("Added: " + e.FullName); continue; }
            if (!seen.Add(e.FullName)) { problems.Add("Repeated: " + e.FullName); continue; }
            using var st = e.Open();
            if (Convert.ToHexStringLower(SHA256.HashData(st)) != sha) problems.Add("Modified: " + e.FullName);
        }
        problems.AddRange(expected.Keys.Where(k => !seen.Contains(k)).Select(k => "Missing: " + k));

        string? fingerprint = null;
        bool? valid = null;
        var (sig, pub) = (zip.GetEntry(SignatureFile), zip.GetEntry(PublicKeyFile));
        if (sig is not null || pub is not null)
        {
            valid = false;
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(Encoding.UTF8.GetString(Bytes(pub ?? throw new FormatException("public key missing"))));
                fingerprint = Fingerprint(key);
                valid = key.VerifyData(sumsBytes, Convert.FromBase64String(Encoding.UTF8.GetString(Bytes(sig ?? throw new FormatException("signature missing"))).Trim()),
                    HashAlgorithmName.SHA256);
                if (valid == false) problems.Add("Signature: it does not match SHA256SUMS.txt");
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException) { problems.Add("Signature: " + ex.Message); }
        }
        return new PackageCheck(problems, fingerprint, valid, Convert.ToHexStringLower(SHA256.HashData(sumsBytes))) { Sums = expected };
    }

    /// <summary>A report's media files (media/ and _HTML/media/ folders), which a portable case leaves in the zip.</summary>
    public static bool IsMedia(string entry) => entry.Contains("/media/", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith('/');

    /// <summary>Unpacks a zip into <paramref name="dir"/>, leaving out the entries under <paramref name="skip"/> and those
    /// <paramref name="defer"/> picks (both read from the zip when needed), refusing any entry that would land outside the
    /// folder. Returns the deferred entries by the full path they would have had.</summary>
    public static Dictionary<string, string> Extract(string zipPath, string dir, string? skip, Func<string, bool>? defer = null)
    {
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var deferred = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            if (skip is not null && e.FullName.StartsWith(skip, StringComparison.Ordinal)) continue;
            var dest = Path.GetFullPath(Path.Combine(root, e.FullName));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("An entry of the zip points outside its folder: " + e.FullName);
            if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
            if (defer?.Invoke(e.FullName) == true) { deferred.TryAdd(dest, e.FullName); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            e.ExtractToFile(dest, overwrite: false);
        }
        return deferred;
    }
}

/// <summary>Files of a portable case left in its zip (the media), read from it on demand: each read is checked against
/// its SHA-256 in SHA256SUMS.txt, so a zip changed after it was opened gives an error rather than other content. A
/// player that needs a file gets one written to the case's temporary folder, once.</summary>
public sealed class DeferredFiles(string zipPath, IReadOnlyDictionary<string, string> entries, IReadOnlyDictionary<string, string> sums) : IDisposable
{
    readonly Lock _lock = new();
    ZipArchive? _zip;

    public bool Has(string fullPath) => entries.ContainsKey(fullPath);

    public long Size(string fullPath)
    {
        lock (_lock) return Entry(fullPath).Length;
    }

    /// <summary>The file's bytes, checked, in memory.</summary>
    public Stream Open(string fullPath)
    {
        var ms = new MemoryStream();
        Copy(fullPath, ms);
        ms.Position = 0;
        return ms;
    }

    /// <summary>The file on disk, written (checked) on first use.</summary>
    public string Materialize(string fullPath)
    {
        lock (_lock)
        {
            if (File.Exists(fullPath)) return fullPath;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var part = fullPath + ".part";
            try
            {
                using (var f = File.Create(part)) Copy(fullPath, f);
                File.Move(part, fullPath);
            }
            finally { if (File.Exists(part)) File.Delete(part); }
            return fullPath;
        }
    }

    void Copy(string fullPath, Stream dest)
    {
        lock (_lock)
        {
            var e = Entry(fullPath);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var st = e.Open();
            var buf = new byte[1 << 16];
            for (int n; (n = st.Read(buf)) > 0;) { sha.AppendData(buf, 0, n); dest.Write(buf, 0, n); }
            if (!sums.TryGetValue(e.FullName, out var want) || Convert.ToHexStringLower(sha.GetHashAndReset()) != want)
                throw new IOException($"{e.FullName} in the portable case does not match SHA256SUMS.txt: the zip changed after it was opened.");
        }
    }

    ZipArchiveEntry Entry(string fullPath)
    {
        if (!entries.TryGetValue(fullPath, out var name)) throw new FileNotFoundException("Not in the portable case.", fullPath);
        _zip ??= ZipFile.OpenRead(zipPath);
        return _zip.GetEntry(name) ?? throw new FileNotFoundException("Not in the portable case.", name);
    }

    public void Dispose()
    {
        lock (_lock) { _zip?.Dispose(); _zip = null; }
    }
}
