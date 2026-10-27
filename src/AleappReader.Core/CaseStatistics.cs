using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>The exchanges of the device with one other party through one means (calls, SMS, an app's chats): how many,
/// how many the device made or sent, and the last one. One-to-one only: group chats are not counted.</summary>
public sealed record Interaction(string Kind, string Key, string Contact, string Name, int Count, int Sent, long Last, bool LastSent)
{
    public string Display => Name != "" && !string.Equals(Name, Contact, StringComparison.OrdinalIgnoreCase) ? $"{Name}  ({Contact})" : Contact;
}

/// <summary>A name with a number: positions of an artifact, events of a type of data.</summary>
public sealed record Tally(string Name, long Count);

/// <summary>An album of the gallery in one artifact: its pictures and videos, and the filter that opens them.</summary>
public sealed record Album(string Name, long Count, ArtifactInfo Artifact, ColumnFilter Filter);

/// <summary>A place the device was recorded at, as a zone of positions: its centre, positions, visits and first and last time.</summary>
public sealed record Place(double Lat, double Lon, int Positions, int Visits, long First, long Last);

/// <summary>The positions one artifact holds.</summary>
public sealed record PositionSource(string Name, IReadOnlyList<GeoPoint> Points);

/// <summary>The times (Unix seconds) of the user's records of one type of data (calls, chats, photos, web history…).</summary>
public sealed record ActivitySource(string Type, long[] Epochs);

/// <summary>When the user was active: events per day and per weekday and hour (Monday first), in the display time zone.</summary>
public sealed record ActivityMap(IReadOnlyDictionary<DateOnly, int> Days, int[] WeekHours, long Total)
{
    public int At(DayOfWeek d, int hour) => WeekHours[((int)d + 6) % 7 * 24 + hour];
}

/// <summary>The figures of the Statistics view, read from the artifacts: values as stored, nothing inferred.</summary>
public sealed record CaseStatistics(IReadOnlyList<Interaction> Interactions, IReadOnlyList<PositionSource> Positions);

public static class Statistics
{
    const int MaxRows = 500_000; // ponytail: an artifact bigger than this is read no further; SQL GROUP BY if cases need it

    // a call log's columns, each role by the first pattern a column matches: the other party's number or id, its name, the direction
    static readonly Regex[] PartyColumns = [.. new[] { @"^(phone ?number|number|partner|talker|remote ?(party|number|handle|id)?|other party|caller|callee)$",
        @"^(recipient|sender|contact) ?(handle|number|id)$", @"^(address|handle|jid|msisdn)$" }.Select(p => new Regex(p, RegexOptions.IgnoreCase))];
    static readonly Regex[] NameColumns = [.. new[] { @"^(contact ?(full ?)?name|display ?name|cached ?name|caller ?name|name)$", @"^conversation name$" }
        .Select(p => new Regex(p, RegexOptions.IgnoreCase))];
    static readonly Regex[] DirectionColumns = [.. new[] { @"^(call )?direction$", @"^(call )?type$" }.Select(p => new Regex(p, RegexOptions.IgnoreCase))];
    static readonly Regex NameColumn = new(@"(?<![a-z])((contact|display|full) ?name|name)(?![a-z])", RegexOptions.IgnoreCase);
    static readonly Regex Outgoing = new(@"^(out\w*|sent|dialed|dialled|1|true|yes|from me)$", RegexOptions.IgnoreCase);
    static readonly Regex Jid = new(@"^\d{6,15}(:\d+)?@(s\.whatsapp\.net|c\.us)$", RegexOptions.IgnoreCase);
    static readonly Regex Group = ConversationItem.GroupId;

    static readonly ConditionalWeakTable<CaseSession, Task<CaseStatistics>> Cache = new();

    /// <summary>The figures of a report, worked out once in the background (the Statistics view and the overview share them).</summary>
    public static Task<CaseStatistics> Of(CaseSession s) => Cache.GetValue(s, x => Task.Run(() => Compute(x)));

    /// <summary>The parties and the positions; the user's activity is <see cref="UserEvents"/>.</summary>
    public static CaseStatistics Compute(CaseSession s, CancellationToken ct = default)
    {
        using var con = s.OpenDb();
        var arts = s.Manifest.Artifacts.Where(a => a.RecordCount > 0).ToList();
        var interactions = Interactions(con, s, arts, ct);
        var sources = new List<PositionSource>();
        foreach (var a in GeoExtractor.GeoArtifacts(s.Manifest).Where(a => a.RecordCount > 0))
        {
            ct.ThrowIfCancellationRequested();
            try { if (GeoExtractor.Extract(con, a) is { Count: > 0 } p) sources.Add(new PositionSource(a.Name, p)); }
            catch (SqliteException) { }
        }
        return new CaseStatistics(interactions, [.. sources.OrderByDescending(x => x.Points.Count)]);
    }

    /// <summary>The ten zones of 200 m with most of the positions given.</summary>
    public static IReadOnlyList<Place> Places(IReadOnlyList<GeoPoint> points, CancellationToken ct = default) =>
        [.. Zones.Find([points], 200, TimeSpan.FromHours(2), TimeSpan.Zero, ct: ct).OrderByDescending(z => z.Positions).Take(10)
            .Select(z => new Place(z.Lat, z.Lon, z.Positions, z.Visits.Count, z.Visits.Min(v => v.From), z.Visits.Max(v => v.To)))];

    static readonly Regex AlbumColumn = new(@"^(bucket display name|album title|album|album name)$", RegexOptions.IgnoreCase);

    /// <summary>The gallery's albums with most pictures and videos, each in one artifact: the album column (Android's media
    /// store bucket, iOS's album title) of the artifacts of images and videos, values as stored, with the filter that opens
    /// the album's rows.</summary>
    public static IReadOnlyList<Album> Albums(SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var count = new Dictionary<(ArtifactInfo, string), (string Sql, long N)>();
        foreach (var a in artifacts.Where(a => MacroCategories.Classify(a) is { Macro: var m, Type: "Images" or "Videos" } && m == MacroCategories.Media))
            Rows(con, a, cols =>
            {
                var i = cols.FindIndex(c => AlbumColumn.IsMatch(c.Display.Trim()));
                return v =>
                {
                    if (i >= 0 && v[i]?.ToString() is { } raw && raw.Trim() != "")
                        count[(a, raw)] = (cols[i].Sql, count.GetValueOrDefault((a, raw)).N + 1);
                };
            });
        return [.. count.OrderByDescending(x => x.Value.N).Take(24)
            .Select(x => new Album(x.Key.Item2.Trim(), x.Value.N, x.Key.Item1, new ColumnFilter(x.Value.Sql, "equals", x.Key.Item2)))];
    }

    /// <summary>Each row of an artifact to the handler <paramref name="make"/> builds once from its columns.</summary>
    static void Rows(SqliteConnection con, ArtifactInfo a, Func<List<ColumnInfo>, Action<object?[]>> make)
    {
        try
        {
            var cols = ArtifactQuery.ExistingColumns(con, a).ToList();
            var each = make(cols);
            foreach (var r in ArtifactQuery.All(con, a).Take(MaxRows)) each(r.Values);
        }
        catch (SqliteException) { } // an unreadable artifact counts nothing
    }

    /// <summary>
    /// The one-to-one exchanges per means and party. Calls: each call log row, the party from its number column (else its
    /// name), outgoing when its direction or type says so. Messages: each chat of an artifact with a conversation view
    /// that has one party besides the device (a chat with more, or a WhatsApp/WeChat group id, is a group). A phone number
    /// is one party whatever its prefix (its last nine digits); a name is found in the contacts when the record has none.
    /// </summary>
    public static IReadOnlyList<Interaction> Interactions(SqliteConnection con, CaseSession s, IReadOnlyList<ArtifactInfo> arts, CancellationToken ct = default)
    {
        var acc = new Dictionary<(string Kind, string Key), (string Contact, string Name, int Count, int Sent, long Last, bool LastSent)>();
        void Add(string kind, string contact, string name, bool sent, long epoch)
        {
            contact = contact.Trim();
            if (contact == "" || Group.IsMatch(contact)) return;
            if (Jid.IsMatch(contact)) contact = "+" + contact.Split('@')[0].Split(':')[0]; // a WhatsApp JID is the number of the account
            var key = ContactDirectory.TelKey(contact) ?? contact.ToLowerInvariant();
            if (!acc.TryGetValue((kind, key), out var e)) e = (contact, "", 0, 0, long.MinValue, false);
            if (e.Name == "" && name.Trim() != contact) e.Name = name.Trim();
            e.Count++;
            if (sent) e.Sent++;
            if (epoch >= e.Last) (e.Last, e.LastSent) = (epoch, sent);
            acc[(kind, key)] = e;
        }

        var calls = new List<(string Kind, ArtifactInfo A)>();
        foreach (var a in arts.Where(a => a.RecordCount <= MaxRows))
        {
            ct.ThrowIfCancellationRequested();
            var (macro, type) = MacroCategories.Classify(a);
            var app = CrossDevice.AppOf(a);
            if (a.Conversation is { } cv && macro == MacroCategories.Messages && type != "Emails")
                Chats(con, a, cv, app ?? (type == "SMS / MMS" ? "SMS / MMS" : a.Category), Add);
            else if (macro == MacroCategories.Calls && type == "Call Log")
                calls.Add((app is null ? "Calls" : $"{app} calls", a));
        }
        // a phone keeps its calls in one log, which other artifacts copy (iOS: Call History, its Group Call view, interactionC,
        // Siri, Biome): of each kind's logs the biggest with a party column is read, so a call counts once
        foreach (var kind in calls.GroupBy(c => c.Kind))
            kind.Select(c => c.A).OrderByDescending(a => a.RecordCount).FirstOrDefault(a => Calls(con, a, kind.Key, Add));

        ContactDirectory? dir = null;
        var list = new List<Interaction>();
        foreach (var ((kind, key), e) in acc)
        {
            var name = e.Name;
            if (name == "" && key.StartsWith("tel:") && arts.FirstOrDefault() is { } first)
            {
                dir ??= new ContactDirectory(con, s, first);
                name = dir.Find(e.Contact)?.Fields.FirstOrDefault(f => NameColumn.IsMatch(f.Field) && f.Value.Trim() != "").Value ?? "";
            }
            list.Add(new Interaction(kind, key, e.Contact, name, e.Count, e.Sent, e.Last, e.LastSent));
        }
        return list;
    }

    /// <summary>Reads a call log; false when it has no column naming the other party.</summary>
    static bool Calls(SqliteConnection con, ArtifactInfo a, string kind, Action<string, string, string, bool, long> add)
    {
        try { if (Pick(ArtifactQuery.ExistingColumns(con, a).ToList(), PartyColumns) < 0) return false; }
        catch (SqliteException) { return false; }
        Rows(con, a, cols =>
        {
            // the party: the first of its columns the row fills (WhatsApp leaves the number empty and keeps the contact's id)
            var parties = PartyColumns.Select(rx => Pick(cols, [rx])).Where(i => i >= 0).ToList();
            var (iName, iDir, iTime) = (Pick(cols, NameColumns), Pick(cols, DirectionColumns), cols.FindIndex(c => c.IsDateTime));
            return v =>
            {
                string F(int i) => i < 0 ? "" : ValueFormatter.Format(v[i], cols[i]).Trim();
                var party = parties.Select(F).FirstOrDefault(x => x != "") ?? F(iName);
                add(kind, party, F(iName), Outgoing.IsMatch(F(iDir)) || F(iDir).StartsWith("out", StringComparison.OrdinalIgnoreCase),
                    iTime >= 0 && ValueFormatter.TryEpoch(v[iTime], out var t) ? t.ToUnixTimeSeconds() : long.MinValue);
            };
        });
        return true;
    }

    static int Pick(List<ColumnInfo> cols, Regex[] roles) =>
        roles.Select(rx => cols.FindIndex(c => !c.IsDateTime && !c.IsMedia && rx.IsMatch(c.Display.Trim()))).FirstOrDefault(i => i >= 0, -1);

    static void Chats(SqliteConnection con, ArtifactInfo a, ConversationView cv, string kind, Action<string, string, string, bool, long> add)
    {
        var rows = new List<(string Key, string Label, string Sender, bool Sent, long Epoch)>();
        Rows(con, a, cols =>
        {
            int Ix(string? name) => a.Column(name) is { } c ? cols.IndexOf(c) : -1;
            var (iKey, iLabel, iSender, iDir, iTime) = (Ix(cv.Discriminator), Ix(cv.LabelColumn), Ix(cv.Sender), Ix(cv.Direction), Ix(cv.Time));
            return v =>
            {
                string F(int i) => i < 0 ? "" : ValueFormatter.Format(v[i], cols[i]).Trim();
                rows.Add((F(iKey), F(iLabel), F(iSender), iDir >= 0 && cv.IsSent(F(iDir)),
                    iTime >= 0 && ValueFormatter.TryEpoch(v[iTime], out var t) ? t.ToUnixTimeSeconds() : long.MinValue));
            };
        });
        foreach (var chat in rows.GroupBy(r => r.Key))
        {
            var senders = chat.Where(r => !r.Sent && r.Sender != "").Select(r => r.Sender).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (senders.Count > 1 || Group.IsMatch(chat.Key)) continue; // a group chat
            var label = chat.Select(r => r.Label).FirstOrDefault(l => l != "") ?? "";
            // the party: its number when the chat names one (the thread id of an SMS, a WhatsApp JID), else the chat's name
            var party = new[] { chat.Key, label, senders.FirstOrDefault() ?? "" }.FirstOrDefault(x => ContactDirectory.TelKey(x.Split('@')[0]) is not null) is { } num
                ? num.Split('@')[0] : label != "" ? label : senders.FirstOrDefault() ?? chat.Key;
            var name = label != party ? label : senders.FirstOrDefault(x => x != party) ?? "";
            foreach (var r in chat) add(kind, party, name, r.Sent, r.Epoch);
        }
    }

    static readonly ConditionalWeakTable<CaseSession, Task<IReadOnlyList<ActivitySource>>> Events = new();

    /// <summary>The user's records per type of data (<see cref="IsUserActivity"/>), each at its first date, read once in the background.</summary>
    public static Task<IReadOnlyList<ActivitySource>> UserEvents(CaseSession s) => Events.GetValue(s, x => Task.Run(() => ReadUserEvents(x)));

    static IReadOnlyList<ActivitySource> ReadUserEvents(CaseSession s)
    {
        using var con = s.OpenDb();
        var byType = new Dictionary<string, List<long>>();
        foreach (var a in s.Manifest.Artifacts.Where(a => a.RecordCount > 0 && IsUserActivity(a)))
        {
            var type = MacroCategories.Classify(a).Type;
            if (!byType.TryGetValue(type, out var list)) byType[type] = list = [];
            Rows(con, a, cols =>
            {
                var i = cols.FindIndex(c => c.IsDateTime);
                return v => { if (i >= 0 && ValueFormatter.TryEpoch(v[i], out var t)) list.Add(t.ToUnixTimeSeconds()); };
            });
        }
        return [.. byType.Where(x => x.Value.Count > 0).OrderByDescending(x => x.Value.Count).Select(x => new ActivitySource(x.Key, [.. x.Value]))];
    }

    /// <summary>Events per day and per weekday and hour in the display zone, of the days from <paramref name="from"/> to <paramref name="to"/> (null: no limit).</summary>
    public static ActivityMap Activity(IEnumerable<long> epochs, DateOnly? from = null, DateOnly? to = null)
    {
        var days = new Dictionary<DateOnly, int>();
        var hours = new int[7 * 24];
        long total = 0;
        foreach (var epoch in epochs)
        {
            var t = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(epoch), ValueFormatter.Zone);
            var day = DateOnly.FromDateTime(t.DateTime);
            if (day < from || day > to) continue;
            days[day] = days.GetValueOrDefault(day) + 1;
            hours[((int)t.DayOfWeek + 6) % 7 * 24 + t.Hour]++;
            total++;
        }
        return new ActivityMap(days, hours, total);
    }

    /// <summary>What the user does rather than what the system logs: calls, messages, media, web, social, notes, calendar, purchases.</summary>
    public static bool IsUserActivity(ArtifactInfo a) => MacroCategories.Of(a) is var m && (m == MacroCategories.Calls || m == MacroCategories.Messages
        || m == MacroCategories.Media || m == MacroCategories.Web || m == MacroCategories.Social || m == MacroCategories.Memos
        || m == MacroCategories.Calendar || m == MacroCategories.Finance);
}
