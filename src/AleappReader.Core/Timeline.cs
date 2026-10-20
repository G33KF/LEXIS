using System.Runtime.CompilerServices;

namespace AleappReader.Core;

/// <summary>One dated field of one record: the time held in <see cref="Column"/> of row <see cref="RowId"/> of the artifact.</summary>
public sealed record TimelineEvent(long Epoch, ArtifactInfo Artifact, ColumnInfo Column, long RowId,
    IReadOnlyList<ColumnInfo> Columns, object?[] Values)
{
    /// <summary>The device the record is on: its position among the sessions the timeline was opened on.</summary>
    public int Device { get; init; }
    public CaseSession? Session { get; init; }
    /// <summary>The device's label and colour in an investigation case; empty for a single report.</summary>
    public string DeviceLabel { get; init; } = "";
    public string DeviceColor { get; init; } = "Transparent";
    /// <summary>The record's tag, if it has one.</summary>
    public string? Tag { get; init; }
    /// <summary>A row still loading (RowId -1).</summary>
    public bool IsPlaceholder => RowId < 0;
    public string Timestamp => IsPlaceholder ? "…" : ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(Epoch));
    public string Activity => Artifact.Name;
    /// <summary>Which of the record's times this is, e.g. "Message Time" or "Last Modified".</summary>
    public string EventType => Column.Display;
    /// <summary>The record's first filled-in fields besides this time, for the timeline row; JSON, XML, plist and HTML
    /// values read as their content, not as stored.</summary>
    public IReadOnlyList<Highlight> Highlights => Columns.Select((c, i) => (c, i, V: ValueFormatter.Display(Values[i], c)))
        .Where(x => x.c != Column && !x.c.IsMedia && x.V is not ("" or "None"))
        .Take(5).Select(x => new Highlight(x.c.Display, StructuredValue.Brief(Values[x.i]) ?? (x.V.Length > 120 ? x.V[..120] + "…" : x.V))).ToList();
    public string Summary => string.Join(" | ", Highlights.Select(h => $"{h.Name}: {h.Value}"));
}

public sealed record Highlight(string Name, string Value);

/// <summary>One day of the activity histogram: how many events, and where the day starts in the timeline.</summary>
public sealed record DayCount(DateOnly Day, int Count, long Position);

/// <summary>
/// The case timeline, built from the records themselves: one event per non-empty datetime column of every row of
/// every artifact, ordered by that time. ALEAPP's _Timeline/tl.db is not used: its key is a record's first column,
/// which is often not a time.
/// </summary>
public static class Timeline
{
    internal readonly record struct Entry(long Epoch, int Art, int Col, long RowId);
    internal sealed record Index(ArtifactInfo[] Arts, ColumnInfo[][] Cols, Entry[] Sorted);

    // ponytail: whole index in memory (24 bytes an event); page from SQL with a temp table if cases reach tens of millions
    static readonly ConditionalWeakTable<CaseSession, Index> Cache = new();

    static Index Build(CaseSession s)
    {
        using var con = s.OpenDb();
        var arts = new List<ArtifactInfo>();
        var cols = new List<ColumnInfo[]>();
        var entries = new List<Entry>();
        foreach (var a in s.Manifest.Artifacts)
        {
            var dt = ArtifactQuery.ExistingColumns(con, a).Where(c => c.IsDateTime).ToArray();
            if (dt.Length == 0) continue;
            for (int c = 0; c < dt.Length; c++)
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT rowid, {ArtifactQuery.Q(dt[c].Sql)} FROM {ArtifactQuery.Q(a.TableName)} " +
                                  $"WHERE {ArtifactQuery.Q(dt[c].Sql)} IS NOT NULL AND {ArtifactQuery.Q(dt[c].Sql)} <> 0";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (ValueFormatter.TryEpoch(r.GetValue(1), out var t))
                        entries.Add(new Entry(t.ToUnixTimeSeconds(), arts.Count, c, r.GetInt64(0)));
            }
            arts.Add(a);
            cols.Add(dt);
        }
        entries.Sort((x, y) => x.Epoch != y.Epoch ? x.Epoch.CompareTo(y.Epoch)
                             : x.Art != y.Art ? x.Art.CompareTo(y.Art) : x.RowId.CompareTo(y.RowId));
        return new Index([.. arts], [.. cols], [.. entries]);
    }

    static Index Get(CaseSession s) => Cache.GetValue(s, Build);

    /// <summary>Artifacts that carry dated records, with their event counts (over every session given).</summary>
    public static IReadOnlyList<(string Activity, long Count)> Activities(params IReadOnlyList<CaseSession> sessions) =>
        sessions.Select(Get).SelectMany(ix => ix.Sorted.Select(e => ix.Arts[e.Art].Name)).GroupBy(n => n).Select(g => (g.Key, (long)g.Count()))
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A page of events. The filter matches the artifact name, the event type or any value of the record.</summary>
    public static (long Total, IReadOnlyList<TimelineEvent> Events) Query(CaseSession s, int offset, int limit,
        string? filter = null, string? activity = null, bool descending = false)
    {
        var v = Open([s], filter, activity is null ? null : [activity], descending);
        return (v.Count, v.Fetch(offset, limit));
    }

    public static View Open(CaseSession s, string? filter = null, bool descending = false) => Open([s], filter, null, descending);

    /// <summary>The events matching a filter, worked out once; the grid then reads any window of them by position. With
    /// several sessions (the devices of an investigation case) their events are merged in time order, each keeping its
    /// device; <paramref name="devices"/> keeps those of them (null: all). <paramref name="activities"/> keeps those artifacts (null: all),
    /// <paramref name="from"/> and <paramref name="to"/> the events in that span (Unix seconds, both included).</summary>
    public static View Open(IReadOnlyList<CaseSession> sessions, string? filter = null, IReadOnlyCollection<string>? activities = null,
        bool descending = false, IReadOnlyCollection<int>? devices = null, long? from = null, long? to = null)
    {
        var all = new List<Ref>();
        for (int d = 0; d < sessions.Count; d++)
        {
            if (devices is not null && !devices.Contains(d)) continue;
            var (s, ix) = (sessions[d], Get(sessions[d]));
            IEnumerable<Entry> ev = ix.Sorted;
            if (activities is not null) { var keep = ix.Arts.Select(a => activities.Contains(a.Name)).ToArray(); ev = ev.Where(e => keep[e.Art]); }
            if (from is not null || to is not null) ev = ev.Where(e => e.Epoch >= (from ?? long.MinValue) && e.Epoch <= (to ?? long.MaxValue));
            if (!string.IsNullOrWhiteSpace(filter))
            {
                using var con = s.OpenDb();
                var f = filter.Trim();
                bool Has(string v) => ArtifactQuery.TextMatches(f, v);
                var rows = ix.Arts.Select(a => Has(a.Name) ? null : ArtifactQuery.MatchingRowIds(con, a, f, s.SearchSkips(a))).ToArray();
                ev = ev.Where(e => rows[e.Art] is not { } ids || ids.Contains(e.RowId) || Has(ix.Cols[e.Art][e.Col].Display));
            }
            var dev = d;
            all.AddRange(ev.Select(e => new Ref(dev, e)));
        }
        // each device's events are already in order: a stable sort on time alone keeps them, devices in case order
        var merged = sessions.Count == 1 ? [.. all] : all.OrderBy(r => r.E.Epoch).ToArray();
        return new View(sessions.Select(s => (s, Get(s))).ToArray(), merged, descending);
    }

    internal readonly record struct Ref(int Dev, Entry E);

    /// <summary>Filtered events, read by position.</summary>
    public sealed class View
    {
        readonly (CaseSession S, Index Ix)[] _src;
        readonly Ref[] _ev; // ascending
        readonly bool _desc;
        internal View((CaseSession, Index)[] src, Ref[] ev, bool desc) => (_src, _ev, _desc) = (src, ev, desc);

        public long Count => _ev.Length;

        /// <summary>Display position of the first event at or after <paramref name="epoch"/> (ascending views).</summary>
        public long LowerBound(long epoch)
        {
            int lo = 0, hi = _ev.Length;
            while (lo < hi) { var m = (lo + hi) / 2; if (_ev[m].E.Epoch < epoch) lo = m + 1; else hi = m; }
            return _desc ? _ev.Length - lo : lo;
        }

        /// <summary>Events per calendar day in the display zone, oldest day first, each with the display position of
        /// its first event, for the activity histogram.</summary>
        public IReadOnlyList<DayCount> PerDay()
        {
            var days = new List<DayCount>();
            for (int i = 0; i < _ev.Length;)
            {
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(_ev[i].E.Epoch), ValueFormatter.Zone).DateTime);
                int j = i;
                while (j < _ev.Length && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(_ev[j].E.Epoch), ValueFormatter.Zone).DateTime) == day) j++;
                days.Add(new DayCount(day, j - i, _desc ? _ev.Length - j : i));
                i = j;
            }
            return days;
        }

        /// <summary>Events at positions [start, start + count) in display order.</summary>
        public IReadOnlyList<TimelineEvent> Fetch(long start, int count)
        {
            var n = (int)Math.Max(0, Math.Min(count, _ev.Length - start));
            var page = Enumerable.Range(0, n).Select(i => _ev[_desc ? _ev.Length - 1 - (start + i) : start + i]).ToList();
            return Events(_src, page);
        }
    }

    static IReadOnlyList<TimelineEvent> Events((CaseSession S, Index Ix)[] src, List<Ref> page)
    {
        if (page.Count == 0) return [];
        var records = new Dictionary<(int Dev, int Art), (IReadOnlyList<ColumnInfo> Columns, Dictionary<long, object?[]> Rows)>();
        foreach (var dev in page.GroupBy(r => r.Dev))
        {
            var (s, ix) = src[dev.Key];
            using var con = s.OpenDb();
            foreach (var g in dev.GroupBy(r => r.E.Art))
            {
                var res = ArtifactQuery.Query(con, ix.Arts[g.Key], new PageRequest(0, int.MaxValue, RowIds: g.Select(r => r.E.RowId).Distinct().ToList()));
                records[(dev.Key, g.Key)] = (res.Columns, res.Rows.ToDictionary(r => r.RowId, r => r.Values));
            }
        }
        return page.Select(r =>
        {
            var (e, (s, ix)) = (r.E, src[r.Dev]);
            var (cols, rows) = records[(r.Dev, e.Art)];
            return new TimelineEvent(e.Epoch, ix.Arts[e.Art], ix.Cols[e.Art][e.Col], e.RowId, cols,
                rows.GetValueOrDefault(e.RowId) ?? new object?[cols.Count]) { Device = r.Dev, Session = s };
        }).ToList();
    }
}
