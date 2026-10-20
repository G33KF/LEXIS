using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>The parts of the case overview read from the LEAPP's device information and the artifacts: the last SIM card
/// and the dates of the device's wipes, setups, activations and restores, in plain words. Values as stored.</summary>
public static class DeviceOverview
{
    static readonly Regex SimColumn = new(@"number|msisdn|mdn|imsi|icc|carrier|operator|display name|iso|slot", RegexOptions.IgnoreCase);
    static readonly Regex LifecycleFact = new(@"wipe|obliterat|bootstrap|setup|factory|reset|restore|activation|migrat|first boot", RegexOptions.IgnoreCase);
    static readonly Regex LifecycleArtifact = new(@"factory reset|setup wizard|wipe|restore|migration|activation", RegexOptions.IgnoreCase);

    /// <summary>
    /// The last SIM card the device recorded. From the device information: of the SIM records ("Slot 1 -> ICCID: … |
    /// MSISDN: … | Last Update: …") the one updated last, then the other SIM facts (last IMSI, phone number…) not already
    /// in it. Without any, the last row of a SIM artifact that has an ICCID column.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> LastSim(IReadOnlyList<DeviceFact> facts, SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var sim = facts.Where(f => LeappDeviceInfo.Of(f.Category, f.Label) == LeappDeviceInfo.Section.Sim).ToList();
        var records = sim.Where(f => f.Value.Contains(" | ")).Select(f => f.Value.Split(" | ").Select(p => p.Split(": ", 2)).Where(p => p.Length == 2)
            .SelectMany(p => p[0].Split(" -> ") is [var slot, var key] ? new[] { ("SIM slot", slot.Replace("Slot", "").Trim()), (key, p[1]) } : [(p[0], p[1])]).ToList()).ToList();
        var last = records.OrderBy(r => r.Where(p => p.Item1.Contains("update", StringComparison.OrdinalIgnoreCase))
                .Select(p => DateTimeOffset.TryParse(p.Item2, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : DateTimeOffset.MinValue)
                .DefaultIfEmpty(DateTimeOffset.MinValue).Max())
            .LastOrDefault() ?? [];
        var shown = last.Select(p => (p.Item1, Dated(p.Item2))).ToList();
        foreach (var f in sim.Where(f => !f.Value.Contains(" | ")))
            if (!shown.Any(s => string.Equals(s.Item2, f.Value, StringComparison.OrdinalIgnoreCase) || s.Item2.StartsWith(f.Value + " ", StringComparison.OrdinalIgnoreCase)))
                shown.Add((f.Label, f.Shown));
        if (shown.Count > 0) return shown;

        // any artifact with an ICCID column ("siminfo" is not a word the SIM names match), those named for SIM cards first; else one
        // named for SIM cards with an IMSI or a phone number (Google's Constellation verifications). Its latest row when it has a date, else its last
        foreach (var iccid in new[] { true, false })
            foreach (var a in artifacts.Where(a => a.RecordCount is > 0 and <= 10_000 && (iccid || LeappDeviceInfo.IsSim(a.Name)))
                         .OrderByDescending(a => a.Name.Contains("sim", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var cols = ArtifactQuery.ExistingColumns(con, a).ToList();
                    if (!cols.Any(c => Regex.IsMatch(c.Display, iccid ? @"\bicc" : @"imsi|msisdn|number", RegexOptions.IgnoreCase))) continue;
                    var t = cols.FindIndex(c => c.IsDateTime);
                    var rows = ArtifactQuery.All(con, a).ToList();
                    // ponytail: dates compared as stored text, which orders one artifact's dates since they are all written alike
                    if ((t < 0 ? rows.LastOrDefault() : rows.MaxBy(r => Convert.ToString(r.Values[t], CultureInfo.InvariantCulture), StringComparer.Ordinal)) is not { } row) continue;
                    return cols.Select((c, i) => (c.Display, Value: ValueFormatter.Format(row.Values[i], c).Trim(), Keep: i == t || SimColumn.IsMatch(c.Display)))
                        .Where(x => x.Value != "" && x.Keep).Select(x => (x.Display, x.Value)).ToList();
                }
                catch (SqliteException) { } // an unreadable artifact tells nothing
            }
        return [];
    }

    // what a fact or an artifact about the device's life records, in plain words; the first that matches
    static readonly (Regex Pattern, string Label)[] Meanings =
    [
        (new(@"wipe|obliterat|erase|factory", RegexOptions.IgnoreCase), "Wiped (factory reset)"),
        (new(@"activation", RegexOptions.IgnoreCase), "Activated"),
        (new(@"restore", RegexOptions.IgnoreCase), "Restored from a backup"),
        (new(@"migrat", RegexOptions.IgnoreCase), "Data migrated (after an update or a restore)"),
        (new(@"setup|bootstrap|first boot", RegexOptions.IgnoreCase), "Set up"),
        (new(@"reset", RegexOptions.IgnoreCase), "Reset"),
    ];
    static readonly Regex WipeReason = new(@"factory|wipe|reset|recovery", RegexOptions.IgnoreCase);

    /// <summary>The plain words for what a text about the device's life says happened, or null.</summary>
    public static string? Meaning(string text) => Meanings.FirstOrDefault(m => m.Pattern.IsMatch(text)).Label;

    /// <summary>
    /// When the device was wiped, set up, activated or restored, each said in plain words with when and where it is
    /// recorded: the device information's facts about it, then the artifacts about factory resets, setup wizards, wipes,
    /// restores, migrations or activations (each date when there are up to three, else the first and the last). A boot
    /// reason history counts only for its restarts after a factory reset, a wipe or a recovery: a plain restart is not one; its reason is said with each date.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Lifecycle(IReadOnlyList<DeviceFact> facts, SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var list = facts.Where(f => LifecycleFact.IsMatch(f.Category) || LifecycleFact.IsMatch(f.Label))
            .Select(f => (Label: Meaning(f.Label) ?? Meaning(f.Category) ?? f.Label, Value: $"{Dated(f.Value)}  ({(f.Label.StartsWith('.') ? $"{f.Category} {f.Label}" : f.Label)})")).ToList();
        foreach (var a in artifacts.Where(a => a.RecordCount is > 0 and <= 1_000_000 && !a.Name.Contains("FSEvents", StringComparison.OrdinalIgnoreCase)
                     && (LifecycleArtifact.IsMatch(a.Name) || a.Category.Contains("wipe", StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, a).ToList();
                if (cols.FindIndex(c => c.IsDateTime) is var t && t < 0) continue;
                var dates = new List<(string When, string Text)>();
                // the boot reason history and the persistent properties' matched lines: only their factory resets, wipes and recoveries,
                // none when the reason is not found (a plain restart is not a wipe)
                var reason = -1;
                if (a.Name.Contains("boot reason", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("persistent properties", StringComparison.OrdinalIgnoreCase))
                    if ((reason = cols.FindIndex(c => !c.IsDateTime && Regex.IsMatch(c.Display, @"\breason\b|^event$", RegexOptions.IgnoreCase))) < 0) continue;
                foreach (var r in ArtifactQuery.All(con, a).Take(100_000))
                {
                    var when = ValueFormatter.Format(r.Values[t], cols[t]).Trim();
                    if (when == "") continue;
                    if (reason < 0) dates.Add((when, ""));
                    else if (ValueFormatter.Format(r.Values[reason], cols[reason]).Trim() is var why && WipeReason.IsMatch(why)) dates.Add((when, why));
                }
                if (dates.Count == 0) continue;
                dates.Sort((x, y) => string.CompareOrdinal(x.When, y.When));
                var label = reason >= 0 ? dates.Any(d => Meanings[0].Pattern.IsMatch(d.Text)) ? "Wiped (factory reset)" : "Restarted into recovery" : Meaning(a.Name) ?? Meaning(a.Category) ?? a.Name;
                var shown = dates.Count <= 3 ? string.Join("\n", dates.Select(d => d.Text == "" ? d.When : $"{d.When} ({d.Text})"))
                    : $"{dates[0].When}  →  {dates[^1].When}  ({dates.Count:N0} entries)";
                list.Add((label, $"{shown}\n  in {a.Name}"));
            }
            catch (SqliteException) { }
        }
        return list;
    }

    /// <summary>
    /// The device's Android users and profiles (the owner, a Secure Folder, a work profile…), one entry per user id, from
    /// ALEAPP's "Android Users and Profiles": the name, the type, when it was created, last logged in and last entered the
    /// foreground, as stored, and the record status when it is not a user record.
    /// </summary>
    public static IReadOnlyList<(string Label, string Value)> Users(SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        if (artifacts.FirstOrDefault(a => a.Name.Equals("Android Users and Profiles", StringComparison.OrdinalIgnoreCase)) is not { } a) return [];
        try
        {
            var cols = ArtifactQuery.ExistingColumns(con, a).ToList();
            int Col(string header) => cols.FindIndex(c => c.Display.StartsWith(header, StringComparison.OrdinalIgnoreCase));
            var id = Col("User ID");
            if (id < 0) return [];
            var parts = new[] { ("", Col("User Name")), ("", Col("User Type")), ("Created ", Col("Created")), ("Last logged in ", Col("Last Logged In")),
                ("Last entered foreground ", Col("Last Entered Foreground")), ("", Col("Record Status")) };
            return ArtifactQuery.All(con, a).Select(r =>
                {
                    string V(int i) => i < 0 ? "" : ValueFormatter.Format(r.Values[i], cols[i]).Trim();
                    var lines = parts.Select(p => (p.Item1, Value: V(p.Item2))).Where(p => p.Value != "" && p.Value != "User record").Select(p => p.Item1 + p.Value);
                    return (Id: V(id), Value: string.Join("\n", lines));
                })
                .Where(u => u.Id != "").Distinct()
                .OrderBy(u => int.TryParse(u.Id, out var n) ? n : int.MaxValue).ThenBy(u => u.Id, StringComparer.Ordinal)
                .Select(u => ($"User {u.Id}", u.Value)).ToList();
        }
        catch (SqliteException) { return []; }
    }

    /// <summary>A date written as text with its zone also in the display zone; anything else as stored.</summary>
    static string Dated(string value) => ValueFormatter.DateReading(value) is { } d && d.StartsWith("in the display zone") ? $"{value} ({d})" : value;
}
