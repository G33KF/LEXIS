using System.Net;
using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>One identifier the LEAPP noted about the device while parsing (its device_info call): category, label,
/// value, and the artifact it came from.</summary>
public sealed record DeviceFact(string Category, string Label, string Value, string Source)
{
    /// <summary>The value as the summary shows it: <see cref="LeappDeviceInfo.Readable"/>.</summary>
    public string Shown => LeappDeviceInfo.Readable(Label, Value);
}

/// <summary>
/// The device information ALEAPP and iLEAPP collect while they parse (model, OS version, IMEI, phone numbers, SIM
/// cards, accounts, backups…), read back from the report's _HTML/_Script_Logs/DeviceInfo.html, the only place the LAVA
/// output keeps it. The file is the LEAPP's own fixed layout: a "--- category ---" heading, then one item per label,
/// or a nested list when a label has several values.
/// </summary>
public static partial class LeappDeviceInfo
{
    public enum Section { Device, Owner, Sim }

    [GeneratedRegex(@"<b>--- <u>(.*?)\s*</u>---</b>")]
    private static partial Regex Category();

    // <li><b>Label:</b> value <span title="src" ...><i>(Source: artifact)</i></span></li>
    [GeneratedRegex(@"^<li><b>(.*?):</b> (.*) <span title=""[^""]*""[^>]*><i>\(Source: (.*?)\)</i></span></li>$")]
    private static partial Regex Single();

    [GeneratedRegex(@"^<li><b>(.*?):</b><ul>$")]
    private static partial Regex ListHead();

    [GeneratedRegex(@"^<li>(.*) <span title=""[^""]*""[^>]*><i>\(Source: (.*?)\)</i></span></li>$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\b(sim|sim cards?|iccid|imsi|msisdn|carrier|cellular|phone numbers?|mcc|mnc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SimWords();

    [GeneratedRegex(@"\b(accounts?|owner|apple ?id|dsid|e-?mail address|user ?name)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerWords();

    /// <summary>The facts in the report folder, or none when the report keeps no DeviceInfo.html (a portable case).</summary>
    public static IReadOnlyList<DeviceFact> Read(string reportDir)
    {
        var path = Path.Combine(reportDir, "_HTML", "_Script_Logs", "DeviceInfo.html");
        try { return File.Exists(path) && new FileInfo(path).Length < 8 << 20 ? Parse(File.ReadAllText(path)) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // ponytail: line by line, as the LEAPP writes it; a value with a line break in it is dropped
    public static IReadOnlyList<DeviceFact> Parse(string html)
    {
        var facts = new List<DeviceFact>();
        string category = "", listLabel = "";
        foreach (var raw in html.Split('\n'))
        {
            var line = raw.Trim();
            Match m;
            if ((m = Category().Match(line)).Success) (category, listLabel) = (Text(m.Groups[1].Value), "");
            else if ((m = Single().Match(line)).Success) facts.Add(new(category, Text(m.Groups[1].Value), Text(m.Groups[2].Value), Text(m.Groups[3].Value)));
            else if ((m = ListHead().Match(line)).Success) listLabel = Text(m.Groups[1].Value);
            else if (line == "</ul></li>") listLabel = "";
            else if (listLabel != "" && (m = ListItem().Match(line)).Success) facts.Add(new(category, listLabel, Text(m.Groups[1].Value), Text(m.Groups[2].Value)));
        }
        return facts.Where(f => f.Value != "").ToList();
    }

    static string Text(string s) => WebUtility.HtmlDecode(s).Trim();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex PyDict();

    [GeneratedRegex(@"'(\w+)'\s*:\s*'([^']*)'")]
    private static partial Regex PyPair();

    /// <summary>A value the LEAPP wrote as a Python list of pairs, one line per pair: [{'second': '35…', 'first': '1:kOne'}]
    /// under "IMEIs" reads "IMEI 1: 35…". "Key: value | Key: value" (a SIM card) reads one part per line, a date in it that
    /// states its zone followed by its reading in the display zone. Anything else as stored.</summary>
    public static string Readable(string label, string value)
    {
        var v = value.Trim();
        if (v.StartsWith("[{") && v.EndsWith("}]"))
        {
            var name = Regex.Replace(label, @"(?<=[A-Z])s$", "");
            var lines = PyDict().Matches(v).Select(d => PyPair().Matches(d.Groups[1].Value).ToDictionary(p => p.Groups[1].Value, p => p.Groups[2].Value))
                .Select(d => d.TryGetValue("first", out var slot) && d.TryGetValue("second", out var id) ? $"{name} {slot.Split(':')[0]}: {id}" : null)
                .ToList();
            if (lines.Count > 0 && lines.All(l => l is not null)) return string.Join("\n", lines);
        }
        if (!v.Contains(" | ")) return value;
        return string.Join("\n", v.Split(" | ").Select(part =>
        {
            var at = part.IndexOf(": ", StringComparison.Ordinal);
            var reading = at < 0 ? null : ValueFormatter.DateReading(part[(at + 2)..]);
            return reading is not null && reading.StartsWith("in the display zone") ? $"{part} ({reading})" : part;
        }));
    }

    /// <summary>Where a fact belongs in the case summary: the SIM cards and numbers, the owner and accounts, or the device.</summary>
    public static Section Of(string category, string label) =>
        DeviceIds().IsMatch(label) ? Section.Device
        : SimWords().IsMatch(category) || SimWords().IsMatch(label) ? Section.Sim
        : OwnerWords().IsMatch(category) || OwnerWords().IsMatch(label) ? Section.Owner
        : Section.Device;

    [GeneratedRegex(@"\b(imei|imeis|meid|serial)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIds();

    [GeneratedRegex(@"\b(boot|reboots?|factory reset|reset|setup|activation|shutdowns?|power ?(on|off))\b", RegexOptions.IgnoreCase)]
    private static partial Regex LifecycleWords();

    /// <summary>An artifact about the device's starts, resets, setup or activation, by its name.</summary>
    public static bool IsLifecycle(string artifactName) => LifecycleWords().IsMatch(artifactName);

    /// <summary>An artifact about SIM cards or subscriptions, by its name.</summary>
    public static bool IsSim(string artifactName) => SimWords().IsMatch(artifactName);

    /// <summary>An artifact about accounts on the device, by its name.</summary>
    public static bool IsOwner(string artifactName) => OwnerWords().IsMatch(artifactName);
}
