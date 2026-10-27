using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>One SIM card the device recorded: its identifiers as stored (several values one per line), the first and the
/// last time a source dates it, each with the source, and every source that names it. The MCC is the first three digits of
/// the IMSI (ITU-T E.212) or a stored one; the MNC and the country only as a source stores them (an MNC has two or three digits,
/// so the IMSI alone does not say where it ends).</summary>
public sealed record SimCard(string Iccid, string Number, string Imsi, string Carrier, string Slot, string FirstSeen, string LastSeen, string Sources,
    string Mcc = "", string Mnc = "", string Country = "")
{
    /// <summary>The card's title: its carrier's first name, else its number, else its ICCID.</summary>
    public string Title => new[] { Carrier, Number, Iccid }.Select(x => x.Split('\n')[0]).FirstOrDefault(x => x != "") ?? "SIM card";
}

/// <summary>
/// Every SIM card the device recorded, merged from the LEAPP's device information (the SIM records "Slot 1 -> ICCID: … |
/// MSISDN: … | Last Update: …", the last known ICCID, IMSI or phone number) and the artifacts with an ICCID or an IMSI column,
/// or a phone number column in one named for SIM cards or subscribers (Android siminfo, Google's Constellation
/// verifications, iOS CellularUsage subscriber info). A card is its ICCID; a record without one joins the card with the
/// same IMSI, else the only card with the same number, else stands as its own card. Dates only where a source records one.
/// </summary>
public static class SimHistory
{
    static readonly Regex Iccid = new(@"\bicc", RegexOptions.IgnoreCase);
    static readonly Regex Imsi = new(@"\bimsi\b", RegexOptions.IgnoreCase);
    static readonly Regex Number = new(@"^((sim |reported )?phone ?number|number|msisdn|mdn|line number)$", RegexOptions.IgnoreCase);
    static readonly Regex Carrier = new(@"carrier name|operator|^carrier$", RegexOptions.IgnoreCase);
    static readonly Regex DisplayName = new(@"display name", RegexOptions.IgnoreCase);
    static readonly Regex Slot = new(@"\bslot\b", RegexOptions.IgnoreCase);
    static readonly Regex SimName = new(@"\bsim|subscri", RegexOptions.IgnoreCase);

    static readonly Regex Mcc = new(@"^mcc( ?string)?$", RegexOptions.IgnoreCase);
    static readonly Regex Mnc = new(@"^mnc( ?string)?$", RegexOptions.IgnoreCase);
    static readonly Regex Country = new(@"^(iso( country)?( code)?|country( iso)?)$", RegexOptions.IgnoreCase);

    sealed record Seen(string Iccid, string Number, string Imsi, string Carrier, string Slot, string When, DateTimeOffset? At, string Source,
        string Mcc = "", string Mnc = "", string Country = "");

    public static IReadOnlyList<SimCard> Collect(IReadOnlyList<DeviceFact> facts, SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var seen = new List<Seen>();
        foreach (var f in facts.Where(f => LeappDeviceInfo.Of(f.Category, f.Label) == LeappDeviceInfo.Section.Sim))
        {
            var parts = f.Value.Contains(" | ")
                ? f.Value.Split(" | ").Select(p => p.Split(": ", 2)).Where(p => p.Length == 2)
                    .SelectMany(p => p[0].Split(" -> ") is [var slot, var key] ? new[] { ("Slot", slot.Replace("Slot", "").Trim()), (key, p[1]) } : [(p[0], p[1])]).ToList()
                // siminfo logs "SIM Number & IMSI: <number> - <imsi>"
                : Imsi.IsMatch(f.Label) && f.Label.Contains("number", StringComparison.OrdinalIgnoreCase) && f.Value.Split(" - ") is [var n, var i]
                    ? [("Number", n), ("IMSI", i)]
                    : [(f.Label, f.Value)];
            var when = parts.FirstOrDefault(p => p.Item1.Contains("update", StringComparison.OrdinalIgnoreCase)).Item2 ?? "";
            Add(seen, k => parts.FirstOrDefault(p => k.IsMatch(p.Item1.Trim())).Item2, when, Parse(when), f.Source);
        }
        foreach (var a in artifacts.Where(a => a.RecordCount is > 0 and <= 10_000))
        {
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, a).ToList();
                int Col(Regex k) => cols.FindIndex(c => !c.IsDateTime && k.IsMatch(c.Display.Trim()));
                if (Col(Iccid) < 0 && Col(Imsi) < 0 && !(Col(Number) >= 0 && SimName.IsMatch(a.Name))) continue;
                var t = cols.FindIndex(c => c.IsDateTime);
                foreach (var row in ArtifactQuery.All(con, a))
                {
                    DateTimeOffset? at = t >= 0 && ValueFormatter.TryEpoch(row.Values[t], out var d) ? d : null;
                    Add(seen, k => Col(k) is var i and >= 0 ? ValueFormatter.Format(row.Values[i], cols[i]) : null,
                        t >= 0 ? ValueFormatter.Format(row.Values[t], cols[t]) : "", at, a.Name);
                }
            }
            catch (SqliteException) { } // an unreadable artifact tells nothing
        }
        return Merge(seen);
    }

    static void Add(List<Seen> seen, Func<Regex, string?> get, string when, DateTimeOffset? at, string source)
    {
        string V(Regex k) => get(k)?.Trim() ?? "";
        var s = new Seen(V(Iccid), V(Number), V(Imsi), V(Carrier) is { Length: > 0 } carrier ? carrier : V(DisplayName), V(Slot), when.Trim(), at, source,
            V(Mcc) is { Length: > 0 } mcc ? mcc : MccOf(V(Imsi)), V(Mnc), V(Country).ToUpperInvariant());
        if (s.Iccid != "" || s.Imsi != "" || s.Number != "") seen.Add(s);
    }

    /// <summary>The MCC of an IMSI: its first three digits, when it is all digits and long enough to be one.</summary>
    public static string MccOf(string imsi) => imsi.Length is >= 6 and <= 15 && imsi.All(char.IsAsciiDigit) ? imsi[..3] : "";

    static DateTimeOffset? Parse(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // ICCIDs compared on their digits (a trailing F pads some), numbers on their last nine digits (with or without the country code)
    static string IccidKey(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
    static string NumberKey(string s) => new string(s.Where(char.IsAsciiDigit).ToArray()) is var d && d.Length > 9 ? d[^9..] : d;

    static IReadOnlyList<SimCard> Merge(List<Seen> seen)
    {
        var cards = new List<List<Seen>>();
        var byIccid = new Dictionary<string, List<Seen>>();
        var byImsi = new Dictionary<string, List<Seen>>();
        var byNumber = new Dictionary<string, HashSet<List<Seen>>>();
        void Register(List<Seen> card, Seen s)
        {
            card.Add(s);
            if (s.Imsi != "") byImsi.TryAdd(s.Imsi, card);
            if (NumberKey(s.Number) is { Length: > 0 } n) (byNumber.TryGetValue(n, out var set) ? set : byNumber[n] = []).Add(card);
        }
        List<Seen> New() { var c = new List<Seen>(); cards.Add(c); return c; }
        // the records with an ICCID first, then those with an IMSI, then those with only a number
        foreach (var s in seen.OrderBy(s => s.Iccid != "" ? 0 : s.Imsi != "" ? 1 : 2))
        {
            List<Seen> card;
            if (IccidKey(s.Iccid) is { Length: > 0 } i)
                card = byIccid.TryGetValue(i, out var c) ? c : byIccid[i] = New();
            else if (s.Imsi != "")
                card = byImsi.TryGetValue(s.Imsi, out var c) ? c : New();
            else
                card = byNumber.TryGetValue(NumberKey(s.Number), out var set) && set.Count == 1 ? set.First() : New();
            Register(card, s);
        }
        static string All(IEnumerable<string> xs) => string.Join("\n", xs.Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase));
        static string At(Seen? s) => s is null ? "" : $"{s.When} ({s.Source})";
        // the cards in the order the device first recorded them, the undated after
        return cards.Select(c => (Card: c, Dated: c.Where(s => s.At is not null).OrderBy(s => s.At).ToList()))
            .OrderBy(x => x.Dated.Count == 0).ThenBy(x => x.Dated.FirstOrDefault()?.At)
            .Select(x => new SimCard(All(x.Card.Select(s => s.Iccid)), All(x.Card.Select(s => s.Number)), All(x.Card.Select(s => s.Imsi)),
                All(x.Card.Select(s => s.Carrier)), All(x.Card.Select(s => s.Slot)), At(x.Dated.FirstOrDefault()), At(x.Dated.LastOrDefault()),
                All(x.Card.Select(s => s.Source)), All(x.Card.Select(s => s.Mcc)), All(x.Card.Select(s => s.Mnc)), All(x.Card.Select(s => s.Country))))
            .ToList();
    }
}
