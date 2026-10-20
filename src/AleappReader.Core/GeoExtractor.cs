using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

public sealed record GeoPoint(double Lat, double Lon, string Artifact, string TableName, long RowId, string Time, string Label, long? Epoch = null, string? Media = null);

/// <summary>Finds positions in any artifact that has latitude/longitude columns, paired by header name.</summary>
public static partial class GeoExtractor
{
    [GeneratedRegex(@"(^|[^a-z])(lat|latitude)([^a-z]|$)", RegexOptions.IgnoreCase)] private static partial Regex LatRx();
    [GeneratedRegex(@"(^|[^a-z])(lon|lng|longitude)([^a-z]|$)", RegexOptions.IgnoreCase)] private static partial Regex LonRx();

    [GeneratedRegex(@"delta|accuracy|uncertainty|precision", RegexOptions.IgnoreCase)] private static partial Regex NotCoordRx();
    [GeneratedRegex(@"[-_ ]?\d+$")] private static partial Regex OrdinalRx();

    /// <summary>Lat/lon column pairs: "To Latitude" pairs with "To Longitude" (same text around the word). A trailing
    /// column number is ignored (iLEAPP's "zAsset-Latitude-47" pairs with "zAsset-Longitude-49"); deltas and
    /// accuracies are not coordinates.</summary>
    public static IReadOnlyList<(ColumnInfo Lat, ColumnInfo Lon)> GeoPairs(ArtifactInfo a)
    {
        var lons = a.Columns.Where(c => LonRx().IsMatch(c.Display) && !NotCoordRx().IsMatch(c.Display)).ToList();
        var pairs = new List<(ColumnInfo, ColumnInfo)>();
        foreach (var lat in a.Columns.Where(c => LatRx().IsMatch(c.Display) && !NotCoordRx().IsMatch(c.Display)))
        {
            var key = Key(LatRx().Replace(lat.Display, "$1#$3"));
            var lon = lons.FirstOrDefault(l => Key(LonRx().Replace(l.Display, "$1#$3")) == key);
            if (lon is not null) { pairs.Add((lat, lon)); lons.Remove(lon); }
        }
        return pairs;
    }

    static string Key(string s) => OrdinalRx().Replace(s.Trim(), "").ToLowerInvariant();

    public static IEnumerable<ArtifactInfo> GeoArtifacts(LavaManifest m) => m.Artifacts.Where(a => GeoPairs(a).Count > 0);

    // ponytail: hard cap per artifact (the page clusters markers); page the query if a source ever exceeds it.
    public static IReadOnlyList<GeoPoint> Extract(SqliteConnection con, ArtifactInfo a, int maxRows = 500_000)
    {
        var pairs = GeoPairs(a);
        if (pairs.Count == 0) return [];
        PageResult page;
        try { page = ArtifactQuery.Query(con, a, new PageRequest(0, maxRows)); }
        catch (SqliteException) { return []; }

        var cols = page.Columns.ToList();
        var timeCol = cols.FirstOrDefault(c => c.IsDateTime);
        var geoCols = pairs.SelectMany(p => new[] { p.Lat, p.Lon }).ToHashSet();
        var labelIdx = cols.Select((c, i) => (c, i)).Where(x => !geoCols.Contains(x.c) && x.c != timeCol && !x.c.IsMedia)
            .Take(3).Select(x => x.i).ToList();
        var iTime = timeCol is null ? -1 : cols.IndexOf(timeCol);
        var iMedia = cols.FindIndex(c => c.IsMedia);

        var points = new List<GeoPoint>();
        foreach (var row in page.Rows)
        {
            var time = iTime >= 0 ? ValueFormatter.Format(row.Values[iTime], timeCol!) : "";
            long? epoch = iTime >= 0 && ValueFormatter.TryEpoch(row.Values[iTime], out var t) ? t.ToUnixTimeSeconds() : null;
            var label = string.Join(" | ", labelIdx.Select(i => ValueFormatter.Format(row.Values[i], cols[i])).Where(s => s != ""));
            var media = iMedia >= 0 ? MediaResolver.RefIds(row.Values[iMedia]).FirstOrDefault() : null;
            foreach (var (latCol, lonCol) in pairs)
            {
                int iLat = cols.IndexOf(latCol), iLon = cols.IndexOf(lonCol);
                if (iLat < 0 || iLon < 0 || Coord(row.Values[iLat], row.Values[iLon]) is not var (lat, lon)) continue;
                var name = pairs.Count > 1 ? $"{a.Name} ({latCol.Display})" : a.Name;
                points.Add(new GeoPoint(lat, lon, name, a.TableName, row.RowId, time, label, epoch, media));
            }
        }
        return points;
    }

    /// <summary>A valid position from two cells: both numbers in range, and not 0,0 (an unset position).</summary>
    public static (double Lat, double Lon)? Coord(object? lat, object? lon) =>
        TryCoord(lat, out var la) && TryCoord(lon, out var lo) && la is >= -90 and <= 90 && lo is >= -180 and <= 180 && !(la == 0 && lo == 0)
            ? (la, lo) : null;

    [GeneratedRegex(@"^(msg|message)[ _]?id$", RegexOptions.IgnoreCase)] public static partial Regex MessageIdRx();

    /// <summary>Positions kept in a sibling artifact of the chat's module and keyed by the same message ID, e.g. WeChat's
    /// "Shared Locations" beside "Messages": message ID (as formatted) → position, with that artifact's record.</summary>
    public static Dictionary<string, GeoPoint> ByMessageId(SqliteConnection con, LavaManifest m, ArtifactInfo chat)
    {
        var map = new Dictionary<string, GeoPoint>();
        if (!chat.Columns.Any(c => MessageIdRx().IsMatch(c.Display))) return map;
        foreach (var b in m.Artifacts.Where(b => b != chat && b.Module == chat.Module && b.Module != "" && GeoPairs(b).Count > 0))
        {
            if (b.Columns.FirstOrDefault(c => MessageIdRx().IsMatch(c.Display)) is not { } idCol) continue;
            try
            {
                var points = Extract(con, b).ToLookup(p => p.RowId);
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT rowid, {ArtifactQuery.Q(idCol.Sql)} FROM {ArtifactQuery.Q(b.TableName)}";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (ValueFormatter.Format(r.IsDBNull(1) ? null : r.GetValue(1), idCol) is { Length: > 0 } id && points[r.GetInt64(0)].FirstOrDefault() is { } p)
                        map.TryAdd(id, p);
            }
            catch (SqliteException) { } // a sibling without the column: the chat just shows no positions from it
        }
        return map;
    }

    static bool TryCoord(object? v, out double d)
    {
        d = 0;
        switch (v)
        {
            case double x: d = x; return double.IsFinite(x);
            case long l: d = l; return true;
            case string s: return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);
            default: return false;
        }
    }
}
