using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>A position recorded on one device of an investigation case, with its time.</summary>
public sealed record DevicePoint(int Device, GeoPoint Point, long Epoch);

/// <summary>Two positions of two devices close in space and in time: the distance between them and the time apart.
/// It says the records are near each other, not that the devices or their users met.</summary>
public sealed record CoPresence(DevicePoint A, DevicePoint B, double Meters, long Seconds);

/// <summary>
/// Positions of two or more devices joined by co-presences: each is within the radius and the time window of a position
/// of another device, directly or through others of the group. Numbered by time. It says the records are near each
/// other, not that the devices or their users met.
/// </summary>
public sealed record CoPresenceGroup(int Number, IReadOnlyList<DevicePoint> Points, double MaxMeters)
{
    public IReadOnlyList<int> Devices => [.. Points.Select(p => p.Device).Distinct().Order()];
    public long From => Points.Min(p => p.Epoch);
    public long To => Points.Max(p => p.Epoch);
    public double Lat => Points.Average(p => p.Point.Lat);
    public double Lon => Points.Average(p => p.Point.Lon);

    /// <summary>The group as a zone of the map: a visit per device, and the period of the group as the time together.</summary>
    public Zone ToZone() => new(Number, Lat, Lon,
        [.. Points.GroupBy(p => p.Device).OrderBy(g => g.Key).Select(g => new ZoneVisit(g.Key, g.Min(p => p.Epoch), g.Max(p => p.Epoch), g.Count()))],
        [new ZoneOverlap(Devices, From, To)]);
}

/// <summary>One record an identifier was found in: the device, the artifact, the row, the field and the value as stored,
/// and the name the record gives, if any.</summary>
public sealed record ContactSource(int Device, ArtifactInfo Artifact, long RowId, string Field, string Value, string Name);

/// <summary>An identifier found on more than one device: a phone number (also from a WhatsApp JID), an e-mail
/// address, a WhatsApp LID or group, or an app's own account ID or username (compared only within that app).
/// Key is what is shown: for a phone number the first international form recorded, else the number as written.</summary>
public sealed record CommonContact(string Key, string Kind, IReadOnlyList<ContactSource> Sources)
{
    public int Devices => Sources.Select(s => s.Device).Distinct().Count();
}

/// <summary>Analyses across the devices of an investigation case.</summary>
public static partial class CrossDevice
{
    [GeneratedRegex(@"^\+?[\d\s().\-/]+$")] private static partial Regex PhoneLike();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex MailLike();
    [GeneratedRegex(@"(?<![a-z])(phones?|numbers?|mobile|tel|telephone|msisdn|line ?1 ?number|e-?mails?|e-?mail address)(?![a-z])", RegexOptions.IgnoreCase)] private static partial Regex IdColumn();
    /// <summary>Columns holding an app's own account identifiers: JIDs, LIDs, user/sender/member IDs, usernames, handles.
    /// Message, call, post or chat IDs are the app's own row keys and are not read.</summary>
    [GeneratedRegex(@"(?<![a-z])(jid|lid|uid|uuid|aci|username|user name|handle|screen ?name|((user|sender|recipient|receiver|peer|contact|participant|author|member|actor|account|profile|facebook|wechat|telegram|signal|instagram|tiktok|snapchat|skype|discord|kik)[ _]?(id|uuid)))(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex AppIdColumn();
    /// <summary>Columns naming a party of a message or call; read only for values that say what they are (JID, LID, e-mail, +number).</summary>
    [GeneratedRegex(@"(?<![a-z])(sender|recipients?|receiver|caller|callee|remote|participants?|members?|creator|from|to)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex PartyColumn();
    [GeneratedRegex(@"(?<![a-z])(name|display ?name|full ?name|first ?name|contact)(?![a-z])", RegexOptions.IgnoreCase)] private static partial Regex NameColumn();
    [GeneratedRegex(@"^(\d{6,15})(:\d+)?@(s\.whatsapp\.net|c\.us)$", RegexOptions.IgnoreCase)] private static partial Regex WaJid();
    [GeneratedRegex(@"^(\d{6,20})(:\d+)?@lid$", RegexOptions.IgnoreCase)] private static partial Regex WaLid();
    [GeneratedRegex(@"^[\d\-]{6,40}@g\.us$", RegexOptions.IgnoreCase)] private static partial Regex WaGroup();
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)] private static partial Regex Uuid();
    [GeneratedRegex(@"^@?[A-Za-z0-9][A-Za-z0-9._\-]{2,63}$")] private static partial Regex Handle();
    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")] private static partial Regex Ipv4();
    /// <summary>Artifacts of calls, SMS/MMS and messages: their numbers are read even when written without a country code.</summary>
    [GeneratedRegex(@"call|sms|mms|messag|chat|voicemail|dial", RegexOptions.IgnoreCase)] private static partial Regex Communication();
    /// <summary>The column of an SMS or call naming the other party's number.</summary>
    [GeneratedRegex(@"(?<![a-z])address(?![a-z])", RegexOptions.IgnoreCase)] private static partial Regex AddressColumn();

    static readonly string[] Apps = ["WhatsApp", "Telegram", "Signal", "Instagram", "Facebook", "Messenger", "TikTok", "Snapchat", "Viber",
        "WeChat", "Threema", "Discord", "Skype", "Twitter", "Kik", "Wire", "Zalo", "Teams", "Slack", "Line", "imo"];

    /// <summary>The app an artifact belongs to, from its name, module or category; null when none of the known apps.</summary>
    public static string? AppOf(ArtifactInfo a) =>
        Apps.FirstOrDefault(app => new[] { a.Name, a.Module, a.Category }.Any(t => Regex.IsMatch(t ?? "", $@"(?<![a-z]){app}(?![a-z])", RegexOptions.IgnoreCase)));

    /// <summary>
    /// The identifier a value names, as a key compared across devices and its kind; null when it names none.
    /// <paramref name="app"/> namespaces app IDs and usernames (the same number in two apps is two accounts);
    /// <paramref name="national"/> also reads numbers written without an international prefix in a phone column.
    /// </summary>
    public static (string Key, string Kind)? Identify(string v, bool phoneColumn, bool appColumn, string? app, bool national)
    {
        v = v.Trim();
        if (v == "") return null;
        // a JID is the phone number of the account: same key as the number in the contacts
        if (WaJid().Match(v) is { Success: true } j) return PhoneKey("+" + j.Groups[1].Value, false) is { } p ? (p, "phone") : null;
        if (WaLid().Match(v) is { Success: true } l) return ("whatsapp-lid:" + l.Groups[1].Value, "WhatsApp LID");
        if (WaGroup().IsMatch(v)) return ("whatsapp-group:" + v.ToLowerInvariant(), "WhatsApp group");
        if (MailLike().IsMatch(v)) return (v.ToLowerInvariant(), "e-mail");
        if ((phoneColumn || v.StartsWith('+')) && PhoneKey(v, phoneColumn && national) is { } n) return (n, "phone");
        if (!appColumn || app is null) return null;
        if (Uuid().IsMatch(v)) return ($"{app.ToLowerInvariant()}:{v.ToLowerInvariant()}", $"{app} ID");
        if (v.All(char.IsAsciiDigit) || v.StartsWith('-') && v[1..].All(char.IsAsciiDigit))
            return v.TrimStart('-').Length >= 5 ? ($"{app.ToLowerInvariant()}:{v}", $"{app} ID") : null; // short numbers are row ids
        return Handle().IsMatch(v) ? ($"{app.ToLowerInvariant()}:@{v.TrimStart('@').ToLowerInvariant()}", $"{app} username") : null;
    }

    /// <summary>
    /// The digits of a phone number written in international form ("+" or "00") or, when <paramref name="national"/>,
    /// without one: 8 to 15 digits once spaces, dashes, dots, slashes and brackets go; null when not phone-shaped.
    /// </summary>
    public static string? PhoneDigits(string raw, bool national)
    {
        var v = raw.Trim();
        if (!PhoneLike().IsMatch(v) || Ipv4().IsMatch(v)) return null;
        var d = string.Concat(v.Where(char.IsAsciiDigit));
        if (v.StartsWith('+')) { }
        else if (d.StartsWith("00", StringComparison.Ordinal)) d = d[2..];
        else if (!national) return null;
        return d.Length is >= 8 and <= 15 ? d : null;
    }

    /// <summary>
    /// The key a phone number is compared on: its last 9 digits, so a number written with its country code on one
    /// device and without it on another (with or without the trunk 0) is the same number.
    /// </summary>
    // ponytail: two numbers of different countries ending in the same 9 digits merge; libphonenumber if that bites
    public static string? PhoneKey(string raw, bool national) =>
        PhoneDigits(raw, national) is { } d ? "tel:" + (d.Length > 9 ? d[^9..] : d) : null;

    /// <summary>A phone number as shown: the first international form recorded ("+" and its digits), else the first as written.</summary>
    public static string PhoneShown(IEnumerable<string> values)
    {
        var all = values.ToList();
        foreach (var v in all)
            if (PhoneDigits(v, false) is { } d) return "+" + d;
        if (all.Select(v => WaJid().Match(v.Trim())).FirstOrDefault(m => m.Success) is { } j) return "+" + j.Groups[1].Value;
        return all.Select(v => PhoneDigits(v, true)).FirstOrDefault(d => d is not null) ?? all.FirstOrDefault() ?? "";
    }

    /// <summary>Haversine distance in metres.</summary>
    public static double Meters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        double Rad(double x) => x * Math.PI / 180;
        var a = Math.Pow(Math.Sin(Rad(lat2 - lat1) / 2), 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Pow(Math.Sin(Rad(lon2 - lon1) / 2), 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// Every pair of positions of two different devices within <paramref name="meters"/> of each other and
    /// <paramref name="window"/> apart in time, oldest first; at most <paramref name="max"/> (Truncated says whether
    /// there were more). Positions without a time are left out.
    /// </summary>
    public static (IReadOnlyList<CoPresence> Hits, bool Truncated) CoPresences(IReadOnlyList<IReadOnlyList<GeoPoint>> perDevice, double meters,
        TimeSpan window, int max = 10_000, CancellationToken ct = default)
    {
        var dev = perDevice.Select((pts, i) => pts.Where(p => p.Epoch is not null).Select(p => new DevicePoint(i, p, p.Epoch!.Value))
            .OrderBy(p => p.Epoch).ToArray()).ToArray();
        var w = (long)window.TotalSeconds;
        var hits = new List<CoPresence>();
        for (int i = 0; i < dev.Length; i++)
            for (int j = i + 1; j < dev.Length; j++)
            {
                var b = dev[j];
                var start = 0;
                foreach (var a in dev[i])
                {
                    ct.ThrowIfCancellationRequested();
                    while (start < b.Length && b[start].Epoch < a.Epoch - w) start++; // a is in time order: the window only moves forward
                    for (int k = start; k < b.Length && b[k].Epoch <= a.Epoch + w; k++)
                    {
                        var m = Meters(a.Point.Lat, a.Point.Lon, b[k].Point.Lat, b[k].Point.Lon);
                        if (m > meters) continue;
                        if (hits.Count == max) return (Sorted(hits), true);
                        hits.Add(new CoPresence(a, b[k], m, Math.Abs(b[k].Epoch - a.Epoch)));
                    }
                }
            }
        return (Sorted(hits), false);
        static List<CoPresence> Sorted(List<CoPresence> h) => [.. h.OrderBy(x => Math.Min(x.A.Epoch, x.B.Epoch))];
    }

    /// <summary>
    /// The co-presences joined into groups: positions linked by a co-presence, directly or through others, make one group,
    /// so devices close to each other at the same time make one group of all of them. Groups are in time order; those
    /// of fewer than <paramref name="minDevices"/> devices are left out.
    /// </summary>
    // ponytail: a pair recorded together without a break chains into one long group; split by a gap if that bites
    public static IReadOnlyList<CoPresenceGroup> Groups(IReadOnlyList<CoPresence> hits, int minDevices = 2)
    {
        var index = new Dictionary<DevicePoint, int>();
        var parent = new List<int>();
        int Id(DevicePoint p)
        {
            if (!index.TryGetValue(p, out var i)) { index[p] = i = parent.Count; parent.Add(i); }
            return i;
        }
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        foreach (var h in hits)
        {
            var (a, b) = (Root(Id(h.A)), Root(Id(h.B)));
            if (a != b) parent[a] = b;
        }
        var maxOf = new Dictionary<int, double>();
        foreach (var h in hits) { var r = Root(index[h.A]); maxOf[r] = Math.Max(maxOf.GetValueOrDefault(r), h.Meters); }
        return index.GroupBy(kv => Root(kv.Value))
            .Select(g => (Points: g.Select(kv => kv.Key).OrderBy(p => p.Epoch).ThenBy(p => p.Device).ToList(), Max: maxOf[g.Key]))
            .Where(g => g.Points.Select(p => p.Device).Distinct().Count() >= minDevices)
            .OrderBy(g => g.Points[0].Epoch)
            .Select((g, i) => new CoPresenceGroup(i + 1, g.Points, g.Max)).ToList();
    }

    /// <summary>
    /// Identifiers found on more than one device, each with the records it was found in:
    /// <list type="bullet">
    /// <item>in contact artifacts (name or category containing "contact") and in those of calls, SMS/MMS and messages:
    /// phone numbers, with or without a country code (<see cref="PhoneKey"/>), and e-mail addresses;</item>
    /// <item>in any artifact of a known app (<see cref="AppOf"/>): WhatsApp JIDs (as the phone number), LIDs and groups,
    /// e-mail addresses and "+" numbers in party columns (sender, recipient, caller…), and the app's account IDs and
    /// usernames, compared only with the same app's.</item>
    /// </list>
    /// Outside the contacts one record per artifact, field and identifier is kept (a sender is on every message).
    /// </summary>
    public static IReadOnlyList<CommonContact> CommonContacts(IReadOnlyList<CaseSession> devices, CancellationToken ct = default)
    {
        var found = new Dictionary<string, (string Kind, List<ContactSource> Sources)>(StringComparer.Ordinal);
        for (int d = 0; d < devices.Count; d++)
        {
            using var con = devices[d].OpenDb();
            foreach (var a in devices[d].Manifest.Artifacts)
            {
                var contacts = a.Conversation is null &&
                               (a.Name.Contains("contact", StringComparison.OrdinalIgnoreCase) || a.Category.Contains("contact", StringComparison.OrdinalIgnoreCase));
                var app = AppOf(a);
                var comms = !contacts && (a.Conversation is not null || Communication().IsMatch(a.Name) || Communication().IsMatch(a.Category));
                if (!contacts && !comms && app is null) continue;
                IReadOnlyList<ColumnInfo> cols;
                try { cols = ArtifactQuery.ExistingColumns(con, a); }
                catch (SqliteException) { continue; }
                bool Phone(ColumnInfo col) => col.Type == "phonenumber" || IdColumn().IsMatch(col.Display) || (contacts || comms) && AddressColumn().IsMatch(col.Display);
                var idCols = Enumerable.Range(0, cols.Count).Where(c => !cols[c].IsMedia).Select(c => (c, phone: Phone(cols[c]),
                        appId: AppIdColumn().IsMatch(cols[c].Display), party: PartyColumn().IsMatch(cols[c].Display)))
                    .Where(x => x.phone || x.appId || x.party).ToList();
                if (idCols.Count == 0) continue;
                var nameCol = Enumerable.Range(0, cols.Count).FirstOrDefault(c => idCols.All(x => x.c != c) && NameColumn().IsMatch(cols[c].Display), -1);
                var seen = new HashSet<(int, string)>();
                foreach (var r in ArtifactQuery.All(con, a))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = nameCol < 0 ? "" : ValueFormatter.Format(r.Values[nameCol], cols[nameCol]);
                    foreach (var (c, phone, appId, _) in idCols)
                    {
                        // a cell may hold several numbers or addresses
                        foreach (var v in ValueFormatter.Format(r.Values[c], cols[c]).Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            // national numbers only in contacts, calls and messages: elsewhere a "number" column may be anything
                            if (Identify(v, phone, appId, app, contacts || comms) is not { } id) continue;
                            var (key, kind) = id;
                            if (!contacts && !seen.Add((c, key))) continue;
                            if (!found.TryGetValue(key, out var e)) found[key] = e = (kind, []);
                            e.Sources.Add(new ContactSource(d, a, r.RowId, cols[c].Display, v, name));
                        }
                    }
                }
            }
        }
        return found.Select(kv => new CommonContact(kv.Value.Kind == "phone" ? PhoneShown(kv.Value.Sources.Select(s => s.Value)) : kv.Key,
                kv.Value.Kind, kv.Value.Sources))
            .Where(c => c.Devices > 1).OrderByDescending(c => c.Devices).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
    }
}
