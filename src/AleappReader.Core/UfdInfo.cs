using System.Security.Cryptography;

namespace AleappReader.Core;

/// <summary>A .ufd acquisition file (INI text written beside the extraction): case, device and acquisition
/// details and the SHA-256 recorded for each dump, all as stored.</summary>
public sealed class UfdInfo
{
    public string Path { get; }
    readonly List<(string Section, string Key, string Value)> _entries = [];

    UfdInfo(string path)
    {
        Path = path;
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (line.IndexOf('=') is > 0 and var eq) _entries.Add((section, line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
    }

    public static UfdInfo Parse(string path) => new(path);

    public string Get(string section, string key) =>
        _entries.FirstOrDefault(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                                     e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? "";

    public IEnumerable<(string Key, string Value)> Section(string section) =>
        _entries.Where(e => e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).Select(e => (e.Key, e.Value));

    /// <summary>The extraction file this UFD describes, in the UFD's own folder.</summary>
    public string? Extraction =>
        new[] { Get("Dumps", "FileDump") }.Concat(Section("SHA256").Select(e => e.Key)).Where(n => n != "" && n.IndexOfAny(['/', '\\']) < 0)
            .Select(n => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, n)).FirstOrDefault(File.Exists);

    /// <summary>The SHA-256 recorded for a dump file name, or "".</summary>
    public string Sha256Of(string fileName) => Get("SHA256", fileName);

    /// <summary>The .ufd beside an extraction file that names it (or the only .ufd there), or null.</summary>
    public static UfdInfo? ForExtraction(string? extraction)
    {
        if (string.IsNullOrWhiteSpace(extraction) || !File.Exists(extraction)) return null;
        try
        {
            var ufds = Directory.GetFiles(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(extraction))!, "*.ufd").Select(Parse).ToList();
            var name = System.IO.Path.GetFileName(extraction);
            return ufds.FirstOrDefault(u => u.Sha256Of(name) != "" || u.Get("Dumps", "FileDump").Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? (ufds.Count == 1 ? ufds[0] : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Recomputes the extraction's SHA-256 (read-only) and compares it with the one in the UFD.</summary>
    public static async Task<(string Computed, bool Matches)> VerifyAsync(UfdInfo ufd, string extraction, CancellationToken ct = default)
    {
        await using var f = new FileStream(extraction, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
        return (sha, sha.Equals(ufd.Sha256Of(System.IO.Path.GetFileName(extraction)), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The details worth showing, grouped as Device / Extraction / Case, labelled for a reader.</summary>
    public IReadOnlyList<(string Group, string Name, string Value)> Summary()
    {
        (string, string, string, string)[] map =
        [
            ("Device", "DeviceInfo", "Vendor", "Vendor"),
            ("Device", "General", "FullName", "Model"),
            ("Device", "DeviceInfo", "Model", "Model number"),
            ("Device", "DeviceInfo", "OS", "Operating system"),
            ("Device", "DeviceInfo", "SecurityPatchLevel", "Security patch"),
            ("Device", "DeviceInfo", "IMEI1", "IMEI 1"),
            ("Device", "DeviceInfo", "IMEI2", "IMEI 2"),
            ("Device", "DeviceInfo", "Chipset", "Chipset"),
            ("Extraction", "General", "ExtractionType", "Extraction type"),
            ("Extraction", "Image", "ExtractionMethod", "Method"),
            ("Extraction", "General", "Date", "Started"),
            ("Extraction", "General", "EndTime", "Ended"),
            ("Extraction", "ExtractionStatus", "ExtractionStatus", "Status"),
            ("Extraction", "General", "AcquisitionTool", "Tool"),
            ("Extraction", "General", "Version", "Tool version"),
            ("Extraction", "General", "ConnectionType", "Connection"),
            ("Extraction", "General", "UnitId", "Unit ID"),
        ];
        var list = map.Select(m => (m.Item1, m.Item4, Get(m.Item2, m.Item3))).Where(x => x.Item3 != "").ToList();
        list.AddRange(Section("SHA256").Select(e => ("Extraction", $"SHA-256 of {e.Key} (UFD)", e.Value)));
        list.Add(("Extraction", "UFD file", System.IO.Path.GetFileName(Path)));
        list.AddRange(Section("Crime Case").Where(e => e.Value != "").Select(e => ("Case", e.Key, e.Value)));
        return list;
    }
}
