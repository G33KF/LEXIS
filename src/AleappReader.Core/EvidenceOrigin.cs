using System.IO.Compression;
using System.Security.Cryptography;

namespace AleappReader.Core;

/// <summary>Where a record's file sits in the forensic extraction, as opposed to the working copy ALEAPP made of it.</summary>
public static class EvidenceOrigin
{
    /// <summary>The extraction ALEAPP read (param_input), as given to it, or where the examiner located it on this
    /// machine, or the extraction files a portable case carries; "" when it is not a full path (a portable case keeps
    /// only its name), so nothing is looked up relative to the working directory.</summary>
    public static string InputPath(CaseSession s) => s.LocatedExtraction
        ?? (!s.Manifest.PortableFileSystem ? RecordedInputPath(s)
            : s.PackagePath is { } zip ? $"{zip} › {PortableCase.CaseDir}/{PortableCase.ExtractionDir}" // read inside the zip
            : Path.Combine(s.RootDir, PortableCase.ExtractionDir));

    /// <summary>The extraction's path as ALEAPP recorded it, wherever it is now.</summary>
    public static string RecordedInputPath(CaseSession s) =>
        s.Manifest.Params.GetValueOrDefault("input") is { } p && Path.IsPathRooted(p) ? p : "";

    /// <summary>
    /// Whether <paramref name="candidate"/> (a zip or folder) is the extraction this report was made from: the recorded
    /// source files that ALEAPP kept a copy of under data/ must be in it with the same SHA-256. <paramref name="sample"/>
    /// files spread over the list are checked (0: all). Nothing is written; the zip is opened read-only.
    /// </summary>
    public static ExtractionCheck VerifyExtraction(CaseSession s, string candidate, int sample = 40, CancellationToken ct = default)
    {
        using var x = ExtractionFiles.Open(candidate) ?? throw new IOException("Not a readable zip or folder: " + candidate);
        var withCopy = s.Manifest.Artifacts.SelectMany(RecordedSources)
            .Select(p => p.Replace('\\', '/').TrimStart('/')).Where(p => p != "" && !p.Split('/').Contains("..")).Distinct()
            .Select(p => (Rel: p, Copy: s.ResolveInCase(Path.Combine("data", p)))).Where(p => p.Copy is not null && File.Exists(p.Copy)).ToList();
        var picked = sample <= 0 || withCopy.Count <= sample ? withCopy
            : Enumerable.Range(0, sample).Select(i => withCopy[(int)((long)i * withCopy.Count / sample)]).ToList();
        var different = new List<string>();
        var absent = new List<string>();
        foreach (var (rel, copy) in picked)
        {
            ct.ThrowIfCancellationRequested();
            if (!x.Contains(rel)) { absent.Add(DevicePath(rel)); continue; }
            string expected;
            using (var f = File.OpenRead(copy!)) expected = Sha(f);
            if (x.Read(rel, Sha) != expected) different.Add(DevicePath(rel));
        }
        return new ExtractionCheck(picked.Count, withCopy.Count, different, absent);
    }

    /// <summary>The extraction's own file or folder name, e.g. EXTRACTION_FFS.zip.</summary>
    public static string ExtractionName(CaseSession s) => Path.GetFileName((s.Manifest.Params.GetValueOrDefault("input") ?? "").TrimEnd('\\', '/'));

    /// <summary>Top-level folders of an Android or iOS file system: what follows the extraction's container folder
    /// ("Dump/", "filesystem1/"...).</summary>
    static readonly HashSet<string> DeviceRoots = ["data", "system", "sdcard", "storage", "mnt", "vendor", "product", "apex",
        "cache", "efs", "persist", "odm", "private", "var", "System", "Library", "Applications", "usr"];

    /// <summary>Where the file sits on the phone: "/data/data/com.x/db" for "Dump/data/data/com.x/db" as the LEAPP recorded
    /// it relative to the extraction. A path with no recognisable device root is returned as recorded.</summary>
    public static string DevicePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Replace('\\', '/').TrimStart('/');
        var segs = p.Split('/', 3);
        // an extraction folder ("Dump/data/…") goes; an iOS backup domain ("AppDomain-com.x/Library/…", "mobile/…") is part of the path
        if (segs.Length > 2 && !DeviceRoots.Contains(segs[0]) && DeviceRoots.Contains(segs[1]) && !segs[0].Contains("Domain") && segs[0] != "mobile")
            p = p[(segs[0].Length + 1)..];
        return DeviceRoots.Contains(p.Split('/')[0]) ? "/" + p : p;
    }

    /// <summary>iLEAPP's backup domains and the folders it records their files under (ILEAPP/scripts/search_files.py, domains);
    /// a key ending in "-" takes the bundle identifier after it.</summary>
    static readonly Dictionary<string, string> BackupDomains = new()
    {
        ["AppDomain-"] = "private/var/mobile/Containers/Data/Application", ["AppDomainGroup-"] = "private/var/mobile/Containers/Shared/AppGroup",
        ["AppDomainPlugin-"] = "private/var/mobile/Containers/Data/PluginKitPlugin", ["CameraRollDomain"] = "private/var/mobile",
        ["DatabaseDomain"] = "private/var/db", ["HealthDomain"] = "private/var/mobile", ["HomeDomain"] = "private/var/mobile",
        ["HomeKitDomain"] = "private/var/mobile", ["InstallDomain"] = "private/var/installd", ["KeyboardDomain"] = "private/var/mobile",
        ["KeychainDomain"] = "private/var/protected/trustd/private", ["ManagedPreferencesDomain"] = "private/var/Managed Preferences",
        ["MediaDomain"] = "private/var/mobile", ["MobileDeviceDomain"] = "private/var/MobileDevice", ["NetworkDomain"] = "private/var/networkd",
        ["ProtectedDomain"] = "private/var/protected", ["RootDomain"] = "private/var/root",
        ["SysContainerDomain-"] = "private/var/containers/Data/System", ["SysSharedContainerDomain-"] = "private/var/containers/Shared/SystemGroup",
        ["SystemPreferencesDomain"] = "private/var/preferences", ["TonesDomain"] = "private/var/mobile", ["WirelessDomain"] = "private/var/wireless",
    };

    /// <summary>The device path as iLEAPP records it: a backup's "CameraRollDomain/Media/x.jpg" is "/private/var/mobile/Media/x.jpg";
    /// any other path as <see cref="DevicePath"/> gives it.</summary>
    public static string RecordedPath(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/').TrimStart('/');
        var cut = p.IndexOf('/');
        var domain = cut < 0 ? p : p[..cut];
        var dash = domain.IndexOf('-') + 1;
        var root = BackupDomains.GetValueOrDefault(domain) ??
                   (dash > 0 && BackupDomains.TryGetValue(domain[..dash], out var r) ? r + "/" + domain[dash..] : null);
        return DevicePath(root is null ? p : cut < 0 ? root : root + p[cut..]);
    }

    /// <summary>The source files as the manifest records them, one per line.</summary>
    public static string[] RecordedSources(ArtifactInfo a) =>
        (a.SourcePath ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The artifact's distinct source files, as paths on the device.</summary>
    public static IReadOnlyList<string> Sources(ArtifactInfo a) => RecordedSources(a).Select(DevicePath).Distinct().ToList();

    /// <summary>What a report describes, never the examiner's machine: the device and acquisition (from the .ufd file
    /// beside the extraction, when there is one), the extraction by file name, the LEAPP that parsed it, the case.</summary>
    public static IReadOnlyList<(string Group, string Name, string Value)> Provenance(CaseSession s)
    {
        var m = s.Manifest;
        if (m.FrozenProvenance is { } frozen) return frozen; // a portable case: as recorded where the extraction was reachable
        var ufd = UfdInfo.ForExtraction(InputPath(s))?.Summary() ?? [];
        (string, string, string)[] all =
        [
            ..ufd.Where(x => x.Group == "Device"),
            ("Data source", "Extraction", ExtractionName(s)),
            ("Data source", "Extraction type", m.Params.GetValueOrDefault("type") ?? ""),
            ..ufd.Where(x => x.Group == "Extraction").Select(x => ("Data source", x.Name, x.Value)),
            ("Data source", "Parsed by", $"{m.ParserInfo.GetValueOrDefault("leapp_name")} {m.ParserInfo.GetValueOrDefault("leapp_version")}".Trim()),
            ..ufd.Where(x => x.Group == "Case"),
        ];
        return all.Where(x => x.Item3 != "").ToList();
    }

    /// <summary>Source paths as one field value: at most <paramref name="max"/> lines, then "+N more".</summary>
    public static string Join(IReadOnlyList<string> paths, int max = 5) =>
        string.Join("\n", paths.Take(max)) + (paths.Count > max ? $"\n+{paths.Count - max} more" : "");

    /// <summary>Each source file as a path in the extraction, with SHA-256 and size read from the extraction itself
    /// (any kind ExtractionFiles opens, read-only) or, when that is not reachable, from the copy ALEAPP kept under data/.</summary>
    public static IReadOnlyList<(string Path, string? Sha256, long Size, bool FromExtraction)> Hashes(CaseSession s, ArtifactInfo a) =>
        RecordedSources(a).Select(p => p.Replace('\\', '/').TrimStart('/')).Select(p =>
        {
            try
            {
                if (s.Extraction is { } x && x.Contains(p)) { var (sha, size) = x.Open(p, ShaSize); return (DevicePath(p), (string?)sha, size, true); }
                var copy = s.ResolveInCase(Path.Combine("data", p));
                if (copy is not null && File.Exists(copy)) { using var c = File.OpenRead(copy); var (sha, size) = ShaSize(c); return (DevicePath(p), sha, size, false); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
            return (DevicePath(p), null, -1L, false);
        }).ToList();

    /// <summary>Which artifacts read each file of the extraction: recorded sources, as paths in the extraction, and the
    /// -wal/-shm/-journal beside them, which SQLite reads with the database.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ArtifactInfo>> UsedBy(CaseSession s)
    {
        var used = new Dictionary<string, List<ArtifactInfo>>(StringComparer.Ordinal);
        foreach (var a in s.Manifest.Artifacts)
            foreach (var p in RecordedSources(a).Select(p => p.Replace('\\', '/').TrimStart('/')).Distinct())
                foreach (var f in new[] { p, p + "-wal", p + "-shm", p + "-journal" })
                {
                    if (!used.TryGetValue(f, out var list)) used[f] = list = [];
                    if (!list.Contains(a)) list.Add(a);
                }
        return used.ToDictionary(e => e.Key, e => (IReadOnlyList<ArtifactInfo>)e.Value, StringComparer.Ordinal);
    }

    static string Sha(Stream st) => Convert.ToHexStringLower(SHA256.HashData(st));

    static (string Sha, long Size) ShaSize(Stream st)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long size = 0;
        for (int n; (n = st.Read(buf)) > 0; size += n) h.AppendData(buf, 0, n);
        return (Convert.ToHexStringLower(h.GetHashAndReset()), size);
    }

    /// <summary>
    /// Copies recorded source files, with the -wal/-shm/-journal beside them when the extraction has them, under
    /// <paramref name="dir"/> at their device paths. Each file is read from the extraction (zip or folder, read-only), or
    /// else from ALEAPP's copy under data/; the copy is re-read and must match the SHA-256 taken while reading the
    /// original. Writes SHA256SUMS.txt and PROVENANCE.txt beside them. Returns the copies (sources first) and the
    /// sources that could not be read.
    /// </summary>
    public static (IReadOnlyList<ExportedSource> Files, IReadOnlyList<string> Missing) ExportSources(CaseSession s, string title,
        IEnumerable<string> recorded, string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sources = recorded.Select(p => p.Replace('\\', '/').TrimStart('/')).Where(p => p != "").Distinct().ToList();
        var files = new List<ExportedSource>();
        var missing = new List<(string Path, string Why)>();
        foreach (var rel in sources.Concat(sources.SelectMany(r => new[] { r + "-wal", r + "-shm", r + "-journal" })).Distinct())
        {
            try
            {
                var target = Path.GetFullPath(Path.Combine(root, DevicePath(rel).TrimStart('/')));
                // the manifest names the files: nothing outside the extraction is read, nothing outside dir is written
                if (rel.Split('/').Contains("..") || Path.IsPathRooted(rel) || rel.Contains(':') || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("not a path inside the extraction");
                if (CopyVerified(s, rel, target) is { } c) files.Add(new ExportedSource(DevicePath(rel), target, c.Sha, c.Size, c.FromExtraction));
                else if (sources.Contains(rel)) missing.Add((DevicePath(rel), "neither the extraction nor ALEAPP's working copy has it"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                // a bad source path is reported once, not again for each of its sidecars
                if (sources.Contains(rel) || e is not (ArgumentException or NotSupportedException)) missing.Add((DevicePath(rel), e.Message));
            }
        }
        Directory.CreateDirectory(root);
        // "\n" line ends, so sha256sum -c also works off Windows
        File.WriteAllText(Path.Combine(root, "SHA256SUMS.txt"),
            string.Concat(files.Select(f => $"{f.Sha256}  {Path.GetRelativePath(root, f.File).Replace('\\', '/')}\n")));
        string[] provenance =
        [
            $"Source files of: {title}",
            $"Exported (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
            .. Provenance(s).Select(p => $"{p.Name}: {p.Value}"),
            "",
            "Each file was read read-only and its copy re-read: the SHA-256 below is that of the file read (the original, or ALEAPP's working copy where marked) and of the copy.",
            .. files.Select(f => $"{f.DevicePath}\n  read from: {(f.FromExtraction ? "the extraction" : "ALEAPP's working copy (data/), the extraction was not reachable")}" +
                                 $"\n  size: {f.Size:N0} bytes\n  SHA-256: {f.Sha256}"),
            .. missing.Select(m => $"{m.Path}\n  NOT EXPORTED: {m.Why}"),
        ];
        File.WriteAllText(Path.Combine(root, "PROVENANCE.txt"), string.Join("\n", provenance) + "\n");
        return (files, missing.Select(m => m.Path).ToList());
    }

    /// <summary>Copies one file to a .part beside the target, checks it against the hash taken while reading, then
    /// renames it; a failed or mismatching copy leaves nothing behind. Null when neither the extraction nor ALEAPP's
    /// working copy has the file; a file the extraction has but cannot read throws instead of falling back.</summary>
    static (string Sha, long Size, bool FromExtraction)? CopyVerified(CaseSession s, string rel, string target)
    {
        var part = target + ".part";
        (string Sha, long Size) Copy(Stream src)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 20];
                for (int n; (n = src.Read(buf)) > 0; size += n) { hash.AppendData(buf, 0, n); dst.Write(buf, 0, n); }
            }
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), size);
        }
        try
        {
            (string Sha, long Size) done;
            bool fromExtraction;
            if (s.Extraction is { } x && x.Contains(rel)) (done, fromExtraction) = (x.Open(rel, Copy), true);
            else if (s.ResolveInCase(Path.Combine("data", rel)) is { } copy && File.Exists(copy))
            {
                using var f = File.OpenRead(copy);
                (done, fromExtraction) = (Copy(f), false);
            }
            else return null;
            using (var check = File.OpenRead(part))
                if (Sha(check) != done.Sha) throw new IOException("verification failed: the copy does not match the file read");
            File.Move(part, target, true);
            return (done.Sha, done.Size, fromExtraction);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
    }
}

/// <summary>A source file copied out of the extraction: where it was on the device, where the copy is, its hash.</summary>
public sealed record ExportedSource(string DevicePath, string File, string Sha256, long Size, bool FromExtraction);

/// <summary>A candidate extraction checked against ALEAPP's working copies: how many files were compared (of those
/// with a copy), which differ and which it lacks. Same when something was compared and nothing differs or is missing.</summary>
public sealed record ExtractionCheck(int Checked, int WithCopy, IReadOnlyList<string> Different, IReadOnlyList<string> Absent)
{
    public bool Same => Checked > 0 && Different.Count == 0 && Absent.Count == 0;
    public override string ToString() =>
        Checked == 0 ? "No source file has a working copy to compare with: the extraction cannot be confirmed."
        : Same ? $"Same extraction: {Checked:N0} of {WithCopy:N0} source files compared, all SHA-256 identical."
        : $"Different files: of {Checked:N0} compared, {Different.Count:N0} differ and {Absent.Count:N0} are missing.";
}
