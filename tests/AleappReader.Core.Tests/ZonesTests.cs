using AleappReader.Core;
using Xunit;

namespace AleappReader.Core.Tests;

public class ZonesTests
{
    static GeoPoint P(double lat, double lon, long? t) => new(lat, lon, "a", "t", 1, "", "", t);

    // made-up positions: a "home" and a "work" about 13 km apart
    static readonly IReadOnlyList<IReadOnlyList<GeoPoint>> Devices =
    [
        [P(45, 9, 0), P(45, 9, 600), P(45, 9, 1200), P(45.001, 9, 1800), P(45.1, 9.1, 4000), P(45.1, 9.1, 4600), P(45, 9, 9000), P(45, 9, null)],
        [P(45.1, 9.1, 4300), P(45.1005, 9.1, 20000)],
    ];

    [Fact]
    public void Positions_gather_into_zones_with_their_visits_and_the_times_devices_were_there_together()
    {
        var zones = Zones.Find(Devices, 200, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
        Assert.Equal(2, zones.Count);
        var (home, work) = (zones[0], zones[1]);
        Assert.Equal((1, 45.0, 9.0), (home.Number, home.Lat, home.Lon));
        Assert.Equal([new ZoneVisit(0, 0, 1800, 4), new ZoneVisit(0, 9000, 9000, 1)], home.Visits);
        Assert.Empty(home.Overlaps);
        Assert.Equal([new ZoneVisit(0, 4000, 4600, 2), new ZoneVisit(1, 4300, 4300, 1), new ZoneVisit(1, 20000, 20000, 1)], work.Visits);
        var together = Assert.Single(work.Overlaps);
        Assert.Equal([0, 1], together.Devices);
        Assert.Equal((4300L, 4300L), (together.From, together.To));
        Assert.Equal([0, 1], work.Devices);
    }

    [Fact]
    public void A_hundred_thousand_positions_take_seconds_not_minutes()
    {
        var rnd = new Random(1);
        var pts = Enumerable.Range(0, 100_000).Select(i => P(45 + rnd.NextDouble() * 0.2, 9 + rnd.NextDouble() * 0.3, i * 60L)).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var zones = Zones.Find([pts], 200, TimeSpan.FromHours(1), TimeSpan.Zero);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{clock.Elapsed}");
        Assert.Equal(100_000, zones.Sum(z => z.Positions));
        Assert.All(zones.Take(50), z => Assert.True(z.Visits.Count > 0));
    }

    [Fact]
    public void Visits_within_the_tolerance_count_as_together_and_the_period_limits_the_positions()
    {
        var near = Zones.Find([[P(45, 9, 0)], [P(45, 9, 300)]], 100, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
        Assert.Equal(new[] { (0L, 300L) }, near[0].Overlaps.Select(o => (o.From, o.To)));
        Assert.Empty(Zones.Find([[P(45, 9, 0)], [P(45, 9, 3000)]], 100, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10))[0].Overlaps);

        var later = Zones.Find(Devices, 200, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), from: 4000);
        Assert.Equal([new ZoneVisit(0, 9000, 9000, 1)], later.Single(z => z.Lat == 45.0).Visits);
    }
}
