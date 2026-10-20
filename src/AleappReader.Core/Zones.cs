namespace AleappReader.Core;

/// <summary>A device's stay in a zone: consecutive positions of that device all in the zone, none further apart in time
/// than the gap allowed; a visit of one position lasts an instant.</summary>
public sealed record ZoneVisit(int Device, long From, long To, int Positions);

/// <summary>A period in which two or more devices were recorded in the same zone: from the later start to the earlier
/// end of their visits (or, for visits within the tolerance of each other, the span between them).</summary>
public sealed record ZoneOverlap(IReadOnlyList<int> Devices, long From, long To);

/// <param name="Lat">The zone's centre: the first position that fell in no earlier zone.</param>
public sealed record Zone(int Number, double Lat, double Lon, IReadOnlyList<ZoneVisit> Visits, IReadOnlyList<ZoneOverlap> Overlaps)
{
    public int Positions => Visits.Sum(v => v.Positions);
    public IReadOnlyList<int> Devices => [.. Visits.Select(v => v.Device).Distinct().Order()];
}

/// <summary>
/// Zones of interest: the dated positions of one or more devices, within a period, gathered into circles of a radius the
/// examiner sets. Each position joins the zone of the nearest centre within the radius, else starts a zone of its own.
/// A zone lists every visit of every device, and the periods in which more than one device was there.
/// </summary>
public static class Zones
{
    public static IReadOnlyList<Zone> Find(IReadOnlyList<IReadOnlyList<GeoPoint>> perDevice, double radius, TimeSpan maxGap,
        TimeSpan together, long? from = null, long? to = null, CancellationToken ct = default)
    {
        var pts = perDevice.SelectMany((list, d) => list.Where(p => p.Epoch is { } e && e >= (from ?? long.MinValue) && e <= (to ?? long.MaxValue))
                .Select(p => (Device: d, p.Lat, p.Lon, Epoch: p.Epoch!.Value)))
            .OrderBy(p => p.Epoch).ThenBy(p => p.Device).ToList();

        // centres in a grid of cells about one radius wide, so a position looks only at the centres of the cells around it
        var dLat = radius / 111_320.0;
        var centres = new List<(double Lat, double Lon)>();
        var grid = new Dictionary<(long, long), List<int>>();
        (long, long) Cell(double lat, double lon, long row) =>
            (row, (long)Math.Floor(lon / (dLat / Math.Max(0.01, Math.Cos((row + 0.5) * dLat * Math.PI / 180)))));
        var zoneOf = new int[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (lat, lon) = (pts[i].Lat, pts[i].Lon);
            var row = (long)Math.Floor(lat / dLat);
            var (best, bestM) = (-1, double.MaxValue);
            for (long r = row - 1; r <= row + 1; r++)
            {
                var (_, col) = Cell(lat, lon, r);
                for (long c = col - 1; c <= col + 1; c++)
                    if (grid.TryGetValue((r, c), out var ids))
                        foreach (var z in ids)
                        {
                            var m = CrossDevice.Meters(lat, lon, centres[z].Lat, centres[z].Lon);
                            if (m <= radius && m < bestM) (best, bestM) = (z, m);
                        }
            }
            if (best < 0)
            {
                best = centres.Count;
                centres.Add((lat, lon));
                var key = Cell(lat, lon, row);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
                list.Add(best);
            }
            zoneOf[i] = best;
        }

        // visits: per device in time order, a run of positions in one zone, broken by a position elsewhere or a long gap
        var visits = new List<ZoneVisit>[centres.Count];
        for (int z = 0; z < centres.Count; z++) visits[z] = [];
        var gap = (long)maxGap.TotalSeconds;
        foreach (var dev in Enumerable.Range(0, pts.Count).GroupBy(i => pts[i].Device))
        {
            int start = -1, prev = -1, n = 0;
            foreach (var i in dev)
            {
                if (prev >= 0 && (zoneOf[i] != zoneOf[prev] || pts[i].Epoch - pts[prev].Epoch > gap))
                {
                    visits[zoneOf[prev]].Add(new ZoneVisit(dev.Key, pts[start].Epoch, pts[prev].Epoch, n));
                    (start, n) = (-1, 0);
                }
                if (start < 0) start = i;
                (prev, n) = (i, n + 1);
            }
            if (prev >= 0) visits[zoneOf[prev]].Add(new ZoneVisit(dev.Key, pts[start].Epoch, pts[prev].Epoch, n));
        }

        var tol = (long)together.TotalSeconds;
        var zones = Enumerable.Range(0, centres.Count).Select(z => (z, v: visits[z].OrderBy(v => v.From).ToList()))
            .OrderByDescending(x => x.v.Sum(v => v.Positions)).ThenBy(x => x.z)
            .Select((x, n) => new Zone(n + 1, centres[x.z].Lat, centres[x.z].Lon, x.v, Overlaps(x.v, tol))).ToList();
        return zones;
    }

    static List<ZoneOverlap> Overlaps(List<ZoneVisit> v, long tol)
    {
        var pairs = new List<ZoneOverlap>();
        for (int i = 0; i < v.Count; i++)
            for (int j = i + 1; j < v.Count && v[j].From <= v[i].To + tol; j++) // sorted by start: later ones start too late
            {
                if (v[j].Device == v[i].Device || v[i].From > v[j].To + tol) continue;
                var (a, b) = (Math.Max(v[i].From, v[j].From), Math.Min(v[i].To, v[j].To));
                pairs.Add(new ZoneOverlap([.. new[] { v[i].Device, v[j].Device }.Order()], Math.Min(a, b), Math.Max(a, b)));
            }
        // overlapping periods merge into one, with every device in them
        var merged = new List<ZoneOverlap>();
        foreach (var p in pairs.OrderBy(p => p.From))
            if (merged.Count > 0 && p.From <= merged[^1].To)
                merged[^1] = new ZoneOverlap([.. merged[^1].Devices.Union(p.Devices).Order()], merged[^1].From, Math.Max(merged[^1].To, p.To));
            else merged.Add(p);
        return merged;
    }
}
