using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core;

/// <summary>Read-only view of an extraction (the report's param_input, or where the examiner located it): a zip, a
/// tar or tar.gz, a folder, or an iTunes/Finder backup folder (files named by domain and path through Manifest.db).
/// Nothing is ever written to it; a file is copied out, read-only, only when someone opens it.</summary>
public sealed class ExtractionFiles : IDisposable
{
    public static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".avif", ".tif", ".tiff",
        ".mp4", ".mov", ".3gp", ".mkv", ".webm", ".avi", ".mp3", ".m4a", ".aac", ".amr", ".opus", ".ogg", ".wav",
    };

    /// <summary>One indexed file: a zip entry, a tar member (its data offset), or a backup's hashed file (with its wrapped
    /// key in an encrypted backup; an empty key when the backup records none, so the file reads as unreadable).</summary>
    sealed record Entry(long Size, ZipArchiveEntry? Zip = null, long TarData = -1, string? File = null, byte[]? Key = null);

    readonly Dictionary<int, byte[]>? _classKeys; // an unlocked encrypted backup's class keys, in memory only

    readonly ZipArchive? _zip;
    readonly string? _dir;  // a plain folder: read straight from disk, not indexed
    readonly string? _tar;
    readonly bool _gz;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal); // every file: Android paths are case-sensitive
    readonly ILookup<string, string> _byName;
    readonly string _cache;
    readonly Lock _lock = new(); // ZipArchive is not thread-safe

    /// <summary>Media files, as paths relative to the extraction root with '/' separators.</summary>
    public IReadOnlyList<string> Media { get; }
    public string Name { get; }
    /// <summary>What it is: "zip", "tar", "tar.gz", "iTunes backup", "encrypted iTunes backup" or "folder".</summary>
    public string Kind { get; }
    /// <summary>An encrypted backup opened without its password: browsed as the folder of encrypted files it is on disk.</summary>
    public bool Locked { get; private init; }

    ExtractionFiles(string input, string kind, ZipArchive? zip = null, string? dir = null, string? tar = null, Dictionary<string, Entry>? index = null,
                    Dictionary<int, byte[]>? classKeys = null, string prefix = "")
    {
        (Kind, _zip, _dir, _tar, _gz, _classKeys) = (kind, zip, dir, tar, kind == "tar.gz", classKeys);
        Name = Path.GetFileName(input.TrimEnd('\\', '/'));
        if (zip is not null)
            foreach (var e in zip.Entries)
                if (e.FullName.StartsWith(prefix, StringComparison.Ordinal) && Normalize(e.FullName[prefix.Length..]) is { Length: > 0 } rel && !rel.EndsWith('/'))
                    _entries.TryAdd(rel, new Entry(e.Length, Zip: e));
        if (index is not null) _entries = index;
        IEnumerable<string> all = dir is null ? _entries.Keys
            : Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Select(f => Normalize(Path.GetRelativePath(dir, f)));
        Media = all.Where(r => MediaExtensions.Contains(Path.GetExtension(r))).ToList();
        _byName = Media.ToLookup(m => Path.GetFileName(m).ToLowerInvariant());
        _cache = Path.Combine(Path.GetTempPath(), "AleappReader", "extraction", Hash(input));
    }

    /// <summary>The extraction of this case, or null when it is not reachable from this machine. A portable case zip
    /// carrying the file system is read in place, under Case/extraction/, never unpacked.</summary>
    public static ExtractionFiles? Open(CaseSession s)
    {
        if (s.LocatedExtraction is not null || !s.Manifest.PortableFileSystem || s.PackagePath is not { } package) return Open(EvidenceOrigin.InputPath(s));
        try { return new ExtractionFiles(package, "portable case", zip: ZipFile.OpenRead(package), prefix: $"{PortableCase.CaseDir}/{PortableCase.ExtractionDir}/"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>A zip, tar, tar.gz, backup or folder opened read-only, or null when it is none of them or cannot be read.
    /// An encrypted backup needs <paramref name="password"/>; without it, it opens Locked. A wrong password throws
    /// CryptographicException.</summary>
    public static ExtractionFiles? Open(string input, string? password = null)
    {
        try
        {
            if (File.Exists(input))
            {
                var name = input.ToLowerInvariant();
                if (name.EndsWith(".zip")) return new ExtractionFiles(input, "zip", zip: ZipFile.OpenRead(input));
                if (name.EndsWith(".tar")) return new ExtractionFiles(input, "tar", tar: input, index: TarIndex(input, false));
                if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return new ExtractionFiles(input, "tar.gz", tar: input, index: TarIndex(input, true));
                return null;
            }
            if (!Directory.Exists(input)) return null;
            if (BackupIndex(input) is { } backup) return new ExtractionFiles(input, "iTunes backup", index: backup);
            if (!ItunesBackup.IsEncrypted(input)) return new ExtractionFiles(input, "folder", dir: input);
            if (password is null) return new ExtractionFiles(input, "encrypted iTunes backup", dir: input) { Locked = true };
            var (index, keys) = EncryptedBackupIndex(input, password);
            return new ExtractionFiles(input, "encrypted iTunes backup", index: index, classKeys: keys);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
                                      or Microsoft.Data.Sqlite.SqliteException) { }
        return null;
    }

    /// <summary>Every regular file of a tar and where its data starts; a tar.gz is decompressed once to list it.</summary>
    static Dictionary<string, Entry> TarIndex(string path, bool gz)
    {
        using var fs = File.OpenRead(path);
        using var src = gz ? new GZipStream(fs, CompressionMode.Decompress) : (Stream)fs;
        using var r = new TarReader(src);
        var index = new Dictionary<string, Entry>(StringComparer.Ordinal);
        while (r.GetNextEntry() is { } e)
            if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                index.TryAdd(Normalize(e.Name), new Entry(e.Length, TarData: gz ? -1 : e.DataOffset));
        return index;
    }

    /// <summary>An unencrypted iTunes/Finder backup: Manifest.db maps "domain/relativePath" to the file stored as
    /// xx/&lt;fileID&gt;. Null for any other folder, and for an encrypted backup (its Manifest.db is not SQLite).</summary>
    static Dictionary<string, Entry>? BackupIndex(string dir)
    {
        var db = Path.Combine(dir, "Manifest.db");
        if (!File.Exists(db)) return null;
        var head = new byte[16];
        using (var f = File.OpenRead(db)) f.ReadExactly(head);
        if (!head.AsSpan().SequenceEqual("SQLite format 3\0"u8)) return null;
        var index = new Dictionary<string, Entry>(StringComparer.Ordinal);
        using var con = CaseSession.OpenReadOnly(db);
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT fileID, domain, relativePath FROM Files WHERE flags = 1";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var id = rd.GetString(0);
            if (id.Length < 3 || id.Any(c => !char.IsAsciiHexDigit(c))) continue; // a file id names a file in the backup, nothing else
            var file = new FileInfo(Path.Combine(dir, id[..2], id));
            if (file.Exists) index.TryAdd(Normalize($"{rd.GetString(1)}/{rd.GetString(2)}"), new Entry(file.Length, File: file.FullName));
        }
        return index;
    }

    /// <summary>An encrypted backup: Manifest.db is decrypted to a temp file only while it is listed, then deleted; each
    /// file keeps its wrapped key and recorded size.</summary>
    static (Dictionary<string, Entry>, Dictionary<int, byte[]>) EncryptedBackupIndex(string dir, string password)
    {
        var plist = ItunesBackup.Manifest(dir) ?? throw new InvalidDataException("Manifest.plist cannot be read.");
        if (plist.GetValueOrDefault("BackupKeyBag") is not byte[] keybag || plist.GetValueOrDefault("ManifestKey") is not byte[] manifestKey)
            throw new InvalidDataException("Manifest.plist has no keybag or no manifest key.");
        var keys = ItunesBackup.ClassKeys(keybag, password);
        var tmp = Path.Combine(Path.GetTempPath(), "AleappReader", "backup", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        try
        {
            using (var src = File.OpenRead(Path.Combine(dir, "Manifest.db")))
            using (var plain = ItunesBackup.Decrypt(src, ItunesBackup.UnwrapClassed(keys, manifestKey), src.Length))
            using (var dst = File.Create(tmp))
            {
                plain.CopyTo(dst);
                if (dst.Length > 0)
                {
                    dst.Position = dst.Length - 1;
                    if (dst.ReadByte() is var pad and > 0 and <= 16) dst.SetLength(dst.Length - pad); // PKCS#7
                }
            }
            var index = new Dictionary<string, Entry>(StringComparer.Ordinal);
            using var con = CaseSession.OpenReadOnly(tmp);
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT fileID, domain, relativePath, file FROM Files WHERE flags = 1";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var id = rd.GetString(0);
                if (id.Length < 3 || id.Any(c => !char.IsAsciiHexDigit(c))) continue;
                var file = new FileInfo(Path.Combine(dir, id[..2], id));
                if (!file.Exists) continue;
                var rec = rd.IsDBNull(3) ? null : ItunesBackup.FileRecord((byte[])rd[3]);
                index.TryAdd(Normalize($"{rd.GetString(1)}/{rd.GetString(2)}"),
                             rec is { } r ? new Entry(r.Size, File: file.FullName, Key: r.Key) : new Entry(file.Length, File: file.FullName, Key: []));
            }
            return (index, keys);
        }
        finally { File.Delete(tmp); }
    }

    static string Normalize(string p)
    {
        p = p.Replace('\\', '/');
        while (p.StartsWith("./")) p = p[2..];
        return p.TrimStart('/');
    }
    static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>The media file a record value names: a full device path ("/storage/emulated/0/DCIM/x.jpg") or a bare
    /// file name. Candidates with the same name are ranked by how many trailing folders they share with the value and
    /// with the other values of the record (e.g. a "DCIM/100APPLE" directory column); an unresolved tie gives null,
    /// so a record never shows somebody else's photo.</summary>
    public string? Find(string value, IEnumerable<string> context)
    {
        var v = Normalize(value.Trim());
        if (!MediaExtensions.Contains(Path.GetExtension(v))) return null;
        var hits = _byName[Path.GetFileName(v).ToLowerInvariant()].ToList();
        if (hits.Count <= 1) return hits.FirstOrDefault();
        var dirs = context.Select(c => Normalize(c.Trim()).TrimEnd('/')).Where(c => c.Contains('/') && c != v).ToList();
        var ranked = hits.Select(h => (h, score: SharedTail(h, v) + dirs.Count(d => Path.GetDirectoryName(h)!.Replace('\\', '/')
                .EndsWith(d, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.score).ToList();
        return ranked[0].score > 1 && ranked[0].score > ranked[1].score ? ranked[0].h : null;
    }

    static int SharedTail(string a, string b)
    {
        var x = a.Split('/'); var y = b.Split('/');
        int n = 0;
        while (n < x.Length && n < y.Length && string.Equals(x[^(n + 1)], y[^(n + 1)], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>Every file of the extraction, relative with '/' separators.</summary>
    public IEnumerable<string> AllPaths() => _dir is null ? _entries.Keys.ToList()
        : Directory.EnumerateFileSystemEntries(_dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(f => Normalize(Path.GetRelativePath(_dir, f)));

    /// <summary>Every file with its size, for the file browser.</summary>
    public IReadOnlyList<(string Path, long Size)> Files() => _dir is null ? _entries.Select(e => (e.Key, e.Value.Size)).ToList()
        : new DirectoryInfo(_dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Select(f => (Normalize(Path.GetRelativePath(_dir, f.FullName)), f.Length)).ToList();

    /// <summary>The file's bytes, for thumbnails; null when it is larger than <paramref name="max"/> or unreadable.</summary>
    public byte[]? Read(string rel, long max)
    {
        var size = SizeOf(rel);
        if (size < 0 || size > max) return null;
        return Read(rel, s => { var ms = new MemoryStream((int)size); s.CopyTo(ms); return ms.ToArray(); });
    }

    /// <summary>Runs <paramref name="use"/> on a read-only stream of the file; default when it cannot be opened.</summary>
    public T? Read<T>(string rel, Func<Stream, T> use)
    {
        try { return Open(rel, use); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { return default; }
    }

    /// <summary>Whether the extraction has this file.</summary>
    public bool Contains(string rel) => SplitInside(rel) is not null ? InsideSize(rel) >= 0
        : _dir is null ? _entries.ContainsKey(rel) : InDir(rel) is { } p && File.Exists(p);

    /// <summary>The file's size; -1 when the extraction does not have it.</summary>
    public long SizeOf(string rel) => SplitInside(rel) is not null ? InsideSize(rel) :
        _dir is null ? _entries.GetValueOrDefault(rel)?.Size ?? -1 : InDir(rel) is { } p && new FileInfo(p) is { Exists: true } f ? f.Length : -1;

    /// <summary>The file under the extraction folder; null for a path that would leave it ("..", rooted).</summary>
    string? InDir(string rel)
    {
        var root = Path.GetFullPath(_dir!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // separators made native: a "\\?\" path, as ALEAPP records its input, is not normalised by Windows
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Runs <paramref name="use"/> on a read-only stream of the file; a missing or unreadable file throws.</summary>
    // ponytail: the zip lock is held for the whole read; a multi-GB entry stalls thumbnails meanwhile
    // ponytail: a tar.gz member is found by decompressing from the start; Each() reads many in one pass
    public T Open<T>(string rel, Func<Stream, T> use)
    {
        if (SplitInside(rel) is var (archive, entry)) return OpenInside(archive, entry, use);
        if (_dir is not null)
        {
            using var f = File.OpenRead(InDir(rel) ?? throw new FileNotFoundException("Outside the extraction.", rel));
            return use(f);
        }
        if (!_entries.TryGetValue(rel, out var e)) throw new FileNotFoundException("Not in the extraction.", rel);
        if (e.Zip is not null)
            lock (_lock)
            {
                using var z = e.Zip.Open();
                return use(z);
            }
        if (e.File is not null)
        {
            using var f = File.OpenRead(e.File);
            if (e.Key is null) return use(f);
            using var plain = ItunesBackup.Decrypt(f, ItunesBackup.UnwrapClassed(_classKeys!, e.Key), e.Size);
            return use(plain);
        }
        if (!_gz)
        {
            using var slice = new Slice(File.OpenRead(_tar!), e.TarData, e.Size);
            return use(slice);
        }
        T? result = default;
        var found = false;
        Each([rel], (_, s) => { result = use(s); found = true; });
        return found ? result! : throw new FileNotFoundException("Not readable in the extraction.", rel);
    }

    /// <summary>Runs <paramref name="use"/> on each of <paramref name="paths"/> it can read, in the extraction's own
    /// order (one pass over a tar.gz); <paramref name="failed"/> gets those it cannot.</summary>
    public void Each(IEnumerable<string> paths, Action<string, Stream> use, CancellationToken ct = default, Action<string>? failed = null)
    {
        var all = paths.ToList();
        var oneByOne = _gz ? all.Where(p => SplitInside(p) is not null).ToList() : all; // a tar.gz's own files in one pass
        foreach (var rel in oneByOne)
        {
            ct.ThrowIfCancellationRequested();
            try { Open(rel, s => { use(rel, s); return 0; }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { failed?.Invoke(rel); }
        }
        if (!_gz) return;
        var left = all.Except(oneByOne).ToHashSet(StringComparer.Ordinal);
        if (left.Count == 0) return;
        try
        {
            using var fs = File.OpenRead(_tar!);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new TarReader(gz);
            while (left.Count > 0 && r.GetNextEntry() is { } e)
            {
                ct.ThrowIfCancellationRequested();
                if (e.DataStream is not null && left.Remove(Normalize(e.Name))) use(Normalize(e.Name), e.DataStream);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { }
        foreach (var rel in left) failed?.Invoke(rel);
    }

    /// <summary>The extraction's content manifest: one "&lt;sha256&gt;  &lt;path&gt;" line per file, paths relative, NFC and
    /// '/'-separated, sorted by their UTF-8 bytes. Its SHA-256 is the root: it names the content, whatever folder or zip
    /// the extraction sits in. Files that cannot be read are returned apart, never left out silently.</summary>
    public (string Manifest, string Root, IReadOnlyList<string> Unreadable) ContentManifest(IProgress<(int, int)>? progress = null, CancellationToken ct = default)
    {
        var files = _dir is null ? _entries.Keys.ToList()
            : Directory.EnumerateFiles(_dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
                .Select(f => Normalize(Path.GetRelativePath(_dir, f))).ToList();
        var lines = new List<(byte[] Key, string Line)>();
        var unreadable = new List<string>();
        var done = 0;
        Each(files, (rel, s) =>
        {
            var path = rel.Normalize(NormalizationForm.FormC);
            lines.Add((Encoding.UTF8.GetBytes(path), $"{Convert.ToHexStringLower(SHA256.HashData(s))}  {path}\n"));
            progress?.Report((++done, files.Count));
        }, ct, rel => { unreadable.Add(rel); progress?.Report((++done, files.Count)); });
        lines.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        var manifest = string.Concat(lines.Select(l => l.Line));
        return (manifest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), unreadable);
    }

    /// <summary>A read-only copy of the file in the temp folder, for viewers that need a path; the evidence is never
    /// handed to another program.</summary>
    public string? CopyOut(string rel)
    {
        var name = string.Concat(Path.GetFileName(rel).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var target = Path.Combine(_cache, Hash(rel), name is "" or "." or ".." ? "file" : name);
        if (File.Exists(target)) return target;
        var tmp = target + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Open(rel, s => { using var dst = File.Create(tmp); s.CopyTo(dst); return 0; });
            File.Move(tmp, target, true);
            File.SetAttributes(target, FileAttributes.ReadOnly);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { return null; }
        finally { try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    // ---- archives inside the extraction (zip, also APK/JAR/Office; tar; tar.gz; gz), browsed in place ----

    /// <summary>Separates an archive of the extraction from a file inside it: "sdcard/x.zip!/a/b.txt". Archives nest.</summary>
    public const string Inside = "!/";
    readonly HashSet<string> _archives = new(StringComparer.Ordinal);      // archives listed, so their files can be read
    readonly Dictionary<string, long> _inside = new(StringComparer.Ordinal); // their files, with their size

    /// <summary>The archive and the file inside it, for a path of a file in an archive listed by <see cref="ArchiveFiles"/>;
    /// null for any other path (a real folder named "x!" stays a folder).</summary>
    (string Archive, string Entry)? SplitInside(string rel)
    {
        lock (_inside)
        {
            if (_archives.Count == 0) return null;
            for (var i = rel.LastIndexOf(Inside, StringComparison.Ordinal); i > 0; i = rel.LastIndexOf(Inside, i - 1, StringComparison.Ordinal))
                if (_archives.Contains(rel[..i])) return (rel[..i], rel[(i + Inside.Length)..]);
            return null;
        }
    }

    long InsideSize(string rel) { lock (_inside) return _inside.TryGetValue(rel, out var n) ? n : -1; }

    /// <summary>What kind of archive the bytes start: "zip", "tar", "tar.gz" (by its name), "gz", or null.</summary>
    public static string? ArchiveKind(ReadOnlySpan<byte> head, string name)
    {
        if (head is [0x50, 0x4B, 0x03, 0x04, ..] or [0x50, 0x4B, 0x05, 0x06, ..]) return "zip";
        if (IsTar(head)) return "tar";
        if (head is [0x1F, 0x8B, ..])
            return name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? "tar.gz" : "gz";
        return null;
    }

    static bool IsTar(ReadOnlySpan<byte> head) => head.Length >= 262 && head.Slice(257, 5).SequenceEqual("ustar"u8);

    static byte[] Head(Stream s, int max)
    {
        var b = new byte[max];
        int got = 0, n;
        while (got < max && (n = s.Read(b, got, max - got)) > 0) got += n;
        return b[..got];
    }

    /// <summary>The file's kind of archive by its first bytes ("gz" holding a tar is "tar.gz"); null when it is not one.</summary>
    public string? ArchiveKind(string rel)
    {
        var kind = Read(rel, s => ArchiveKind(Head(s, 512), rel));
        if (kind == "gz" && Read(rel, s => { using var g = new GZipStream(s, CompressionMode.Decompress); return IsTar(Head(g, 512)); })) kind = "tar.gz";
        return kind;
    }

    /// <summary>
    /// The files inside an archive of the extraction, as "archive!/name" paths that then read like any other file
    /// (archives inside it can be listed in turn); empty when it is not an archive or cannot be read. The archive is read
    /// from its read-only copy (<see cref="CopyOut"/>): nothing is unpacked, the extraction is not touched.
    /// </summary>
    // ponytail: 7-Zip and RAR are not read (no reader in .NET); add a library if they turn up
    public IReadOnlyList<(string Path, long Size)> ArchiveFiles(string rel)
    {
        var list = new List<(string, long)>();
        try
        {
            if (ArchiveKind(rel) is not { } kind || CopyOut(rel) is not { } copy) return [];
            using var f = File.OpenRead(copy);
            switch (kind)
            {
                case "zip":
                    using (var z = new ZipArchive(f, ZipArchiveMode.Read))
                        foreach (var e in z.Entries)
                            if (!e.FullName.EndsWith('/')) list.Add((rel + Inside + Normalize(e.FullName), e.Length));
                    break;
                case "gz": // one file: its name without ".gz", its size from the trailer (modulo 4 GB)
                    var size = -1L;
                    if (f.Length >= 4) { f.Seek(-4, SeekOrigin.End); size = BitConverter.ToUInt32(Head(f, 4)); }
                    var name = Path.GetFileName(rel);
                    list.Add((rel + Inside + (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name + ".out"), size));
                    break;
                default:
                    using (Stream src = kind == "tar" ? f : new GZipStream(f, CompressionMode.Decompress))
                    using (var r = new TarReader(src))
                        while (r.GetNextEntry() is { } e)
                            if (e.DataStream is not null) list.Add((rel + Inside + Normalize(e.Name), e.Length));
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException) { }
        lock (_inside)
        {
            if (list.Count > 0) _archives.Add(rel);
            foreach (var (p, s) in list) _inside[p] = s;
        }
        return list;
    }

    T OpenInside<T>(string archive, string entry, Func<Stream, T> use)
    {
        var kind = ArchiveKind(archive) ?? throw new InvalidDataException("Not an archive.");
        var copy = CopyOut(archive) ?? throw new FileNotFoundException("The archive cannot be read.", archive);
        using var f = File.OpenRead(copy);
        switch (kind)
        {
            case "zip":
            {
                using var z = new ZipArchive(f, ZipArchiveMode.Read);
                var e = z.Entries.FirstOrDefault(x => Normalize(x.FullName) == entry) ?? throw new FileNotFoundException("Not in the archive.", entry);
                using var s = e.Open();
                return use(s);
            }
            case "gz":
            {
                using var g = new GZipStream(f, CompressionMode.Decompress);
                return use(g);
            }
            default:
            {
                using Stream src = kind == "tar" ? f : new GZipStream(f, CompressionMode.Decompress);
                using var r = new TarReader(src);
                while (r.GetNextEntry() is { } e)
                    if (e.DataStream is { } ds && Normalize(e.Name) == entry) return use(ds);
                throw new FileNotFoundException("Not in the archive.", entry);
            }
        }
    }

    /// <summary>A gallery/details entry for an extraction file; FullPath is set once it has been copied out.</summary>
    public MediaRef ToMedia(string rel, string? copied = null) =>
        new("", Path.GetFileName(rel), null, rel, null, copied, "", EvidenceOrigin.DevicePath(Path.GetDirectoryName(rel)));

    public void Dispose() => _zip?.Dispose();

    /// <summary>A read-only window on a tar member's bytes; owns the file stream it reads.</summary>
    sealed class Slice(Stream inner, long start, long length) : Stream
    {
        long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, length); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= length) return 0;
            inner.Position = start + _pos;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, length - _pos));
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => length + offset };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
