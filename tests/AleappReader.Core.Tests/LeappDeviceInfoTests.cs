using AleappReader.Core;
using Xunit;

namespace AleappReader.Core.Tests;

public class LeappDeviceInfoTests
{
    // the layout write_device_info gives DeviceInfo.html, with made-up values
    const string Html = """
        <b>--- <u>Device Information </u>---</b><br>
        <ul>
        <li><b>Model:</b> X1 &amp; Co <span title="a/b.plist" style="cursor:help"><i>(Source: iOS Information)</i></span></li>
        <li><b>IMEIs:</b><ul>
        <li>350000000000001 <span title="c.db" style="cursor:help"><i>(Source: Cellular)</i></span></li>
        <li>350000000000002 <span title="c.db" style="cursor:help"><i>(Source: Cellular)</i></span></li>
        </ul></li>
        </ul>
        <b>--- <u>Cellular </u>---</b><br>
        <ul>
        <li><b>Last Known ICCID:</b> 8900000000000000001 <span title="d.plist" style="cursor:help"><i>(Source: SIM Info)</i></span></li>
        <li><b>Empty:</b>  <span title="d.plist" style="cursor:help"><i>(Source: SIM Info)</i></span></li>
        </ul>
        <b>--- <u>Accounts </u>---</b><br>
        <ul>
        <li><b>Apple ID:</b> someone@example.com <span title="e.plist" style="cursor:help"><i>(Source: Accounts)</i></span></li>
        </ul>
        """;

    [Fact]
    public void The_leapps_device_information_is_read_back_with_its_sources()
    {
        var facts = LeappDeviceInfo.Parse(Html);
        Assert.Equal(new DeviceFact("Device Information", "Model", "X1 & Co", "iOS Information"), facts[0]);
        Assert.Equal(["350000000000001", "350000000000002"], facts.Where(f => f.Label == "IMEIs").Select(f => f.Value));
        Assert.Equal(5, facts.Count); // the empty value is left out
        Assert.Empty(LeappDeviceInfo.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData("Cellular", "Last Known ICCID", LeappDeviceInfo.Section.Sim)]
    [InlineData("Cellular", "IMEI", LeappDeviceInfo.Section.Device)]
    [InlineData("Device Information", "Reported Phone Number", LeappDeviceInfo.Section.Sim)]
    [InlineData("Accounts", "Apple ID", LeappDeviceInfo.Section.Owner)]
    [InlineData("Device Information", "Serial Number", LeappDeviceInfo.Section.Device)]
    [InlineData("Network", "WiFi Address", LeappDeviceInfo.Section.Device)]
    public void Facts_go_to_their_section(string category, string label, LeappDeviceInfo.Section section) =>
        Assert.Equal(section, LeappDeviceInfo.Of(category, label));

    [Fact]
    public void Artifacts_are_picked_by_name()
    {
        Assert.True(LeappDeviceInfo.IsSim("SIM Info"));
        Assert.False(LeappDeviceInfo.IsSim("Simple Notes"));
        Assert.True(LeappDeviceInfo.IsOwner("Telegram - Accounts"));
        Assert.False(LeappDeviceInfo.IsOwner("Chrome - History"));
        Assert.True(LeappDeviceInfo.IsLifecycle("Factory Reset"));
        Assert.False(LeappDeviceInfo.IsLifecycle("Facebook"));
    }

    [Fact]
    public void Lists_and_sim_records_read_one_part_per_line()
    {
        Assert.Equal("IMEI 1: 111111111111111\nIMEI 2: 222222222222222",
            LeappDeviceInfo.Readable("IMEIs", "[{'second': '111111111111111', 'first': '1:kOne'}, {'second': '222222222222222', 'first': '2:kTwo'}]"));
        Assert.Equal("[{'a': 'b'}]", LeappDeviceInfo.Readable("IMEIs", "[{'a': 'b'}]"));
        Assert.Equal("Pixel 8", LeappDeviceInfo.Readable("Model", "Pixel 8"));
        const string sim = "ICCID: 8900000000000000000 | MSISDN: +10000000000 | Last Update: 2026-06-15 17:33:32+00:00";
        Assert.Equal("ICCID: 8900000000000000000\nMSISDN: +10000000000\nLast Update: 2026-06-15 17:33:32+00:00", LeappDeviceInfo.Readable("SIM", sim));
        try
        {
            ValueFormatter.Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
            Assert.EndsWith("Last Update: 2026-06-15 17:33:32+00:00 (in the display zone: 2026-06-15 19:33:32 (UTC+02:00))", LeappDeviceInfo.Readable("SIM", sim));
        }
        finally { ValueFormatter.Zone = TimeZoneInfo.Utc; }
    }
}
