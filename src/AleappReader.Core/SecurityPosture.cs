using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>
/// Android: settings that weaken the device's defences, and how old its security patch was, read from any name/value
/// artifact of the report (Settings Global, Settings Secure, Settings System, Build Info). The idea follows MVT's
/// dangerous-settings check; the list and the code are this project's.
/// iOS: the iOS version and how old its major release was (iOS Information, iTunes Backup Information), and the
/// passcode changes lockdownd logged. Each hit is a record the examiner can open.
/// </summary>
public static class SecurityPosture
{
    public const string Collection = "Device security posture";
    const string PatchKey = "ro.build.version.security_patch";
    static readonly string[] IosVersionKeys = ["ProductVersion", "Product Version"];

    static readonly Dictionary<string, (string Value, string Meaning)> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adb_enabled"] = ("1", "USB debugging enabled"),
        ["adb_wifi_enabled"] = ("1", "wireless debugging enabled"),
        ["development_settings_enabled"] = ("1", "developer options enabled"),
        ["install_non_market_apps"] = ("1", "installation from unknown sources allowed"),
        ["package_verifier_enable"] = ("0", "Play Protect app verification disabled"),
        ["package_verifier_user_consent"] = ("-1", "Play Protect scanning declined by the user"),
        ["verifier_verify_adb_installs"] = ("0", "apps installed over USB are not verified"),
        ["upload_apk_enable"] = ("0", "unknown apps are not sent for scanning"),
        ["mock_location"] = ("1", "mock locations allowed"),
    };

    /// <summary>Every dangerous setting, and the security patch level with its age at the time the report was made.</summary>
    public static IReadOnlyList<IocHit> Check(CaseSession s)
    {
        var hits = new List<IocHit>();
        var reportTime = s.Manifest.ParserInfo.TryGetValue("start_timestamp", out var ts) && long.TryParse(ts, out var t)
            ? DateTimeOffset.FromUnixTimeSeconds(t) : (DateTimeOffset?)null;
        var ios = s.Manifest.IsIos;
        using var con = s.OpenDb();
        foreach (var a in s.Manifest.Artifacts)
        {
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, a);
                if (ios && a.Name == "Lockdownd Events" && Index(cols, "Event") is var ev and >= 0)
                {
                    foreach (var row in ArtifactQuery.All(con, a))
                        if (row.Values[ev] is "Device passcode changed")
                            hits.Add(new IocHit(a, row.RowId, cols[ev].Display, "Device passcode changed",
                                new Indicator("passcode change", "lockdownd logged a passcode change (set, changed or removed: the log does not say which)", Collection, "built-in")));
                    continue;
                }
                var key = Index(cols, "Name", "Key", "Property Key", "Property");
                var value = Index(cols, "Value", "Property Value");
                if (key < 0 || value < 0) continue;
                foreach (var row in ArtifactQuery.All(con, a))
                {
                    if (row.Values[key] is not string k) continue;
                    var v = Convert.ToString(row.Values[value], CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (ios)
                    {
                        if (IosVersionKeys.Contains(k, StringComparer.OrdinalIgnoreCase) && IosAge(v, reportTime) is { } age)
                            hits.Add(new IocHit(a, row.RowId, cols[value].Display, $"{k} = {v}", new Indicator("iOS version", age, Collection, "built-in")));
                    }
                    else if (Dangerous.TryGetValue(k, out var d) && v == d.Value)
                        hits.Add(new IocHit(a, row.RowId, cols[value].Display, $"{k} = {v}", new Indicator("dangerous setting", d.Meaning, Collection, "built-in")));
                    else if (k.Equals(PatchKey, StringComparison.OrdinalIgnoreCase) &&
                             DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var patch))
                        hits.Add(new IocHit(a, row.RowId, cols[value].Display, $"{k} = {v}",
                            new Indicator("security patch level", PatchAge(patch, reportTime), Collection, "built-in")));
                }
            }
            catch (SqliteException) { } // table missing from a partial case
        }
        return hits;
    }

    /// <summary>"iOS 15.4: its major release came out in September 2021, 3 years before the report". Apple ships a major
    /// version each September (iOS 7 in 2013 to iOS 18 in 2024, then iOS 26 in 2025): the age says how many major
    /// versions behind the device was, not which minor updates or patches it lacked.</summary>
    public static string? IosAge(string version, DateTimeOffset? report)
    {
        if (!int.TryParse(version.Split('.')[0], out var major) || major < 3 || major is > 18 and < 26 || major > 40) return null;
        var year = major <= 18 ? 2006 + major : 1999 + major;
        var head = $"iOS {version}: its major release came out in September {year}";
        if (report is not { } r) return head;
        var years = r.Year - year - (r.Month < 9 ? 1 : 0);
        return years <= 0 ? $"{head}, the current major version when the report was made" : $"{head}, {years} year(s) before the report";
    }

    static int Index(IReadOnlyList<ColumnInfo> cols, params string[] names)
    {
        for (int i = 0; i < cols.Count; i++)
            if (names.Contains(cols[i].Display, StringComparer.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>"patch of 2023-05-01, 29 months older than the report": age is against the report run, the nearest date
    /// the case records to the extraction, so it is an upper bound on how far behind the device was.</summary>
    static string PatchAge(DateTime patch, DateTimeOffset? report)
    {
        if (report is not { } r) return $"patch of {patch:yyyy-MM-dd}";
        var months = (r.Year - patch.Year) * 12 + r.Month - patch.Month;
        return $"patch of {patch:yyyy-MM-dd}, {months} months older than the report";
    }
}
