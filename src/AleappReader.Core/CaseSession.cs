using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>An opened ALEAPP case: the manifest, its folder and read-only access to its databases.</summary>
public sealed class CaseSession : IDisposable
{
    public static readonly string[] ManifestNames = ["_lava_data.lava", "_lava_data.json"];

    public string ManifestPath { get; }
    public string RootDir { get; }
    public LavaManifest Manifest { get; }
    public string DbPath { get; }
    public string TimelinePath => Path.Combine(RootDir, "_Timeline", "tl.db");
    public bool HasTimeline => File.Exists(TimelinePath);
    /// <summary>Set when the case came out of a portable case zip; the temp folder is deleted on dispose.</summary>
    public string? ExtractedTempDir { get; private init; }
    public TagStore Tags { get; }
    /// <summary>The zip the case was opened from, null for a folder.</summary>
    public string? PackagePath { get; }
    /// <summary>The portable case checked against SHA256SUMS.txt and its signature when it was opened; null when the
    /// zip is not a portable case or the case is a folder.</summary>
    public PackageCheck? Package { get; private init; }
    /// <summary>A portable case's media, left in the zip and read from it when shown.</summary>
    public DeferredFiles? Deferred { get; private init; }

    Lazy<ExtractionFiles?> _extraction;
    readonly List<ExtractionFiles> _replaced = []; // views may still read one: disposed with the session
    /// <summary>The extraction the report was made from, listed on first use (slow for big zips: call off the UI thread).</summary>
    public ExtractionFiles? Extraction => _extraction.Value;

    readonly string _locatedFile;
    /// <summary>Where the examiner said the extraction is now, on this machine; null when not set. Kept under
    /// %LOCALAPPDATA%, never in the case or a portable case, so an examiner's path never travels with the report.</summary>
    public string? LocatedExtraction { get; private set; }

    /// <summary>Points the case at the extraction's new location (null: back to the recorded one).</summary>
    public void LocateExtraction(string? path)
    {
        if (path is null) File.Delete(_locatedFile);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_locatedFile)!);
            File.WriteAllText(_locatedFile, Path.GetFullPath(path));
        }
        LocatedExtraction = path is null ? null : Path.GetFullPath(path);
        lock (_replaced)
        {
            if (_extraction.IsValueCreated && _extraction.Value is { } old) _replaced.Add(old);
            _extraction = new Lazy<ExtractionFiles?>(() => ExtractionFiles.Open(this));
        }
    }

    /// <summary>Opens the encrypted backup this case was made from with its password, for this session only: the
    /// password is not kept or written anywhere. A wrong password throws CryptographicException.</summary>
    public void UnlockExtraction(string password)
    {
        var x = ExtractionFiles.Open(EvidenceOrigin.InputPath(this), password) ?? throw new IOException("The extraction is not reachable.");
        lock (_replaced)
        {
            if (_extraction.IsValueCreated && _extraction.Value is { } old) _replaced.Add(old);
            _extraction = new Lazy<ExtractionFiles?>(x);
        }
    }

    readonly string _historyFile;
    /// <summary>The searches run on this case, newest first; kept under %LOCALAPPDATA% on this machine, never in the case.</summary>
    public IReadOnlyList<string> SearchHistory
    {
        get
        {
            try { return File.Exists(_historyFile) ? File.ReadAllLines(_historyFile).Where(l => l != "").ToList() : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    public void AddSearch(string query)
    {
        query = query.Trim().ReplaceLineEndings(" ");
        if (query == "") return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile)!);
            File.WriteAllLines(_historyFile, SearchHistory.Where(q => q != query).Prepend(query).Take(50));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // history is a convenience
    }

    public void RemoveSearch(string query)
    {
        try { if (File.Exists(_historyFile)) File.WriteAllLines(_historyFile, SearchHistory.Where(q => q != query)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void ClearSearchHistory()
    {
        try { File.Delete(_historyFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>When set, text searches and filters leave out the technical columns (<see cref="TechnicalColumns"/>).</summary>
    public bool SkipTechnical { get; set; }
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyCollection<string>> _technical = new();

    /// <summary>The columns a text search skips in this artifact: none unless <see cref="SkipTechnical"/>.</summary>
    public IReadOnlyCollection<string>? SearchSkips(ArtifactInfo a)
    {
        if (!SkipTechnical) return null;
        return _technical.GetOrAdd(a.TableName, _ =>
        {
            try { using var con = OpenDb(); return TechnicalColumns.Of(con, a); }
            catch (SqliteException) { return []; }
        });
    }

    CaseSession(string manifestPath, string? tempDir, string? zipPath = null, PackageCheck? package = null)
    {
        (PackagePath, Package) = (zipPath is null ? null : Path.GetFullPath(zipPath), package);
        ManifestPath = Path.GetFullPath(manifestPath);
        RootDir = Path.GetDirectoryName(ManifestPath)!;
        Manifest = LavaManifest.Load(ManifestPath);
        // LAVA resolves the database relative to the manifest, so a moved case still opens.
        DbPath = Path.Combine(RootDir, Path.GetFileName(Manifest.DbName));
        if (!File.Exists(DbPath)) throw new FileNotFoundException("LAVA database not found beside the manifest.", DbPath);
        ExtractedTempDir = tempDir;
        Tags = zipPath is null ? new TagStore(RootDir) : TagStore.ForPackage(zipPath, RootDir, package?.Root);
        _extraction = new Lazy<ExtractionFiles?>(() => ExtractionFiles.Open(this));
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(zipPath ?? RootDir).ToLowerInvariant())))[..16];
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader");
        _locatedFile = Path.Combine(local, "extraction", key + ".txt");
        _historyFile = Path.Combine(local, "searches", key + ".txt");
        try { LocatedExtraction = File.Exists(_locatedFile) ? File.ReadAllText(_locatedFile).Trim() is { Length: > 0 } p ? p : null : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Opens a case from a manifest file, a report folder (searched recursively) or a zip. A portable case zip is
    /// checked against SHA256SUMS.txt and its signature first (<see cref="Package"/>) and unpacked without the extraction's
    /// files, which are read from the zip.</summary>
    public static CaseSession Open(string path, CancellationToken ct = default)
    {
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var check = PortableCase.Check(path, ct);
            var temp = Path.Combine(Path.GetTempPath(), "AleappReader", Guid.NewGuid().ToString("N"));
            try
            {
                var deferred = PortableCase.Extract(path, temp, check is null ? null : $"{PortableCase.CaseDir}/{PortableCase.ExtractionDir}/",
                    check is null ? null : PortableCase.IsMedia);
                var manifest = FindManifest(temp) ?? throw new FileNotFoundException("No LAVA manifest inside the zip.", path);
                return new CaseSession(manifest, temp, path, check) { Deferred = check is null ? null : new DeferredFiles(path, deferred, check.Sums) };
            }
            catch
            {
                try { Directory.Delete(temp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        if (File.Exists(path)) return new CaseSession(path, null);
        if (Directory.Exists(path))
            return new CaseSession(FindManifest(path) ?? throw new FileNotFoundException(
                "No _lava_data.lava found in this folder. Select an ALEAPP report folder.", path), null);
        throw new FileNotFoundException("Path not found.", path);
    }

    /// <summary>Newest manifest in the folder or below it (ALEAPP nests reports in ALEAPP_Reports_*).</summary>
    public static string? FindManifest(string dir) =>
        ManifestNames.SelectMany(n => Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    /// <summary>Evidence databases are only ever opened read-only.</summary>
    public SqliteConnection OpenDb() => OpenReadOnly(DbPath);

    public static SqliteConnection OpenReadOnly(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var con = new SqliteConnection(cs.ToString());
        con.Open();
        con.CreateFunction<string, string?, bool>("regexp", ArtifactQuery.RegexMatch, isDeterministic: true); // "/regex/" filters
        return con;
    }

    /// <summary>Resolves a path stored relative to the case root, refusing anything that escapes it.</summary>
    public string? ResolveInCase(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(RootDir, relative));
        var root = RootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;
        // Older manifests store absolute paths from the machine that ran ALEAPP: fall back to the relative tail.
        var marker = relative.Replace('\\', '/').IndexOf("/media/", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? ResolveInCase(relative[(marker + 1)..]) : null;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (_extraction.IsValueCreated) _extraction.Value?.Dispose();
        Deferred?.Dispose();
        lock (_replaced) foreach (var x in _replaced) x.Dispose();
        if (ExtractedTempDir is null) return;
        try { Directory.Delete(ExtractedTempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
