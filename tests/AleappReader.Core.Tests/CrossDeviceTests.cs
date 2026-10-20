namespace AleappReader.Core.Tests;

public sealed class CrossDeviceTests : IDisposable
{
    readonly SyntheticCase _a = new(), _b = new();
    public void Dispose() { _a.Dispose(); _b.Dispose(); }

    [Theory]
    [InlineData("+39 333 111 1111", false, "tel:331111111")]
    [InlineData("0039 333-111-1111", false, "tel:331111111")]
    [InlineData("333 111 1111", true, "tel:331111111")]     // the same number without its country code
    [InlineData("06 1234 5678", true, "tel:612345678")]
    [InlineData("+39 06 1234 5678", false, "tel:612345678")]
    [InlineData("07700 900123", true, "tel:700900123")]     // UK trunk 0 and +44 alike
    [InlineData("+44 7700 900123", false, "tel:700900123")]
    [InlineData("333 111 1111", false, null)]               // national number where only international ones are read
    [InlineData("12345", true, null)]                       // too short for a phone number
    [InlineData("192.168.100.1", true, null)]               // an IP address
    [InlineData("anna@example.com", true, null)]
    [InlineData("call me", true, null)]
    public void Phone_numbers_compare_with_or_without_their_country_code(string raw, bool national, string? expected) =>
        Assert.Equal(expected, CrossDevice.PhoneKey(raw, national));

    [Fact]
    public void A_phone_number_shows_its_international_form_when_any_record_has_it()
    {
        Assert.Equal("+393331111111", CrossDevice.PhoneShown(["333 111 1111", "+39 333 111 1111"]));
        Assert.Equal("3331111111", CrossDevice.PhoneShown(["333 111 1111"]));
    }

    static GeoPoint P(double lat, double lon, long? epoch) => new(lat, lon, "Trips", "get_trips", 1, "", "", epoch);

    [Fact]
    public void Co_presence_keeps_pairs_close_in_space_and_time_only()
    {
        const long t = SyntheticCase.T0;
        var a = new[] { P(45.4642, 9.19, t), P(41.9028, 12.4964, t + 7200), P(45.4642, 9.19, null) };
        var b = new[]
        {
            P(45.4645, 9.1902, t + 300),   // ~35 m, 5 min: close
            P(45.4642, 9.19, t + 3600),    // same place, an hour later: outside the window
            P(45.0703, 7.6869, t),         // Turin, same time: too far
            P(41.9028, 12.4964, t + 7100), // Rome, 100 s before a's Rome point
        };
        var (hits, truncated) = CrossDevice.CoPresences([a, b], 100, TimeSpan.FromMinutes(10));
        Assert.False(truncated);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => { Assert.Equal(0, h.A.Device); Assert.Equal(1, h.B.Device); });
        Assert.Equal(300, hits[0].Seconds);
        Assert.InRange(hits[0].Meters, 20, 50);
        Assert.Equal(100, hits[1].Seconds);
        Assert.Equal(0, hits[1].Meters, 3);

        var (one, cut) = CrossDevice.CoPresences([a, b], 100, TimeSpan.FromMinutes(10), max: 1);
        Assert.True(cut);
        Assert.Single(one);
        Assert.Empty(CrossDevice.CoPresences([a, []], 100, TimeSpan.FromHours(1)).Hits);
    }

    [Fact]
    public void Co_presences_join_into_groups_of_every_device_close_at_the_time()
    {
        const long t = SyntheticCase.T0;
        var a = new[] { P(45.4642, 9.19, t), P(41.9028, 12.4964, t + 7200) };
        var b = new[] { P(45.4645, 9.1902, t + 300), P(41.9028, 12.4964, t + 7100) };
        var c = new[] { P(45.4643, 9.1901, t + 120) };                 // near a and b in Milan, not in Rome
        var (hits, _) = CrossDevice.CoPresences([a, b, c], 100, TimeSpan.FromMinutes(10));
        var groups = CrossDevice.Groups(hits);
        Assert.Equal(2, groups.Count);
        Assert.Equal([0, 1, 2], groups[0].Devices);                   // one group of three devices, not three pairs
        Assert.Equal(3, groups[0].Points.Count);
        Assert.Equal((t, t + 300), (groups[0].From, groups[0].To));
        Assert.Equal([0, 1], groups[1].Devices);
        Assert.Equal([1, 2], groups.Select(g => g.Number));
        Assert.Equal([1], CrossDevice.Groups(hits, minDevices: 3).Select(g => g.Number));
    }

    [Fact]
    public void Investigation_case_round_trips_with_paths_relative_to_its_file()
    {
        var file = Path.Combine(_a.Root, "case" + LexisCase.Extension);
        var c = new LexisCase("Op. Test", "123/2026 RGNR", [
            new LexisDevice(_a.Report, "Phone A", LexisCase.Palette[0]),
            new LexisDevice(_b.Report, "Phone B", LexisCase.Palette[1])]);
        c.Save(file);

        var json = File.ReadAllText(file);
        Assert.Contains("\"ALEAPP_Reports_test\"", json);      // below the case folder: relative
        Assert.Contains(_b.Report.Replace("\\", "\\\\"), json); // elsewhere: absolute
        Assert.Contains("\"lexis\": \"investigation case\"", json);

        var back = LexisCase.Load(file);
        Assert.Equal(c.Name, back.Name);
        Assert.Equal(c.Number, back.Number);
        Assert.Equal(c.Devices.Select(d => (Path.GetFullPath(d.Path), d.Label, d.Color)), back.Devices.Select(d => (d.Path, d.Label, d.Color)));

        (c with { Devices = [c.Devices[0], c.Devices[1] with { Label = "phone a" }] }).Save(file);
        Assert.Throws<InvalidDataException>(() => LexisCase.Load(file));
        (c with { Devices = [] }).Save(file);
        Assert.Empty(LexisCase.Load(file).Devices); // a case may have no device yet
        File.WriteAllText(file, """{"lexis": "something else", "format_version": 1, "devices": []}""");
        Assert.Throws<InvalidDataException>(() => LexisCase.Load(file));
    }

    [Fact]
    public void Joint_timeline_merges_devices_in_time_order_and_filters_by_device()
    {
        using var a = CaseSession.Open(_a.Report);
        using var b = CaseSession.Open(_b.Report);
        var one = Timeline.Open(a).Count;
        Assert.True(one > 0);

        var joint = Timeline.Open([a, b]);
        Assert.Equal(2 * one, joint.Count);
        var ev = joint.Fetch(0, (int)joint.Count);
        Assert.True(ev.Zip(ev.Skip(1)).All(p => p.First.Epoch <= p.Second.Epoch));
        Assert.Equal(one, ev.Count(e => e.Device == 0 && e.Session == a));
        Assert.Equal(one, ev.Count(e => e.Device == 1 && e.Session == b));

        var onlyB = Timeline.Open([a, b], devices: [1]);
        Assert.Equal(one, onlyB.Count);
        Assert.All(onlyB.Fetch(0, (int)one), e => Assert.Same(b, e.Session));
        Assert.Equal(2 * one, Timeline.Activities(a, b).Sum(x => x.Count));
    }

    [Fact]
    public void Tags_of_two_devices_with_the_same_tables_do_not_mix()
    {
        using var a = CaseSession.Open(_a.Report);
        using var b = CaseSession.Open(_b.Report);
        a.Tags.Set("get_chat", 2, "Evidence");
        Assert.Equal("Evidence", a.Tags.ForTable("get_chat")[2].Tag);
        Assert.Empty(b.Tags.ForTable("get_chat"));
        Assert.Empty(b.Tags.All());
    }

    [Fact]
    public void Common_contacts_match_numbers_written_differently_on_each_device()
    {
        using var a = CaseSession.Open(_a.Report);
        using var b = CaseSession.Open(_b.Report);
        var common = CrossDevice.CommonContacts([a, b]);

        // each device holds "333 111 1111" in its contacts and +39 333 111 1111 in its chat, +39 333 222 2222 only in its
        // chat (a number not in the contacts), and +39 333 333 3333 twice in its contacts ("+39 333 333 3333", "3333333333") and once in its chat
        Assert.Equal(["+393331111111", "+393332222222", "+393333333333"], common.Select(c => c.Key));
        Assert.All(common, c => { Assert.Equal("phone", c.Kind); Assert.Equal(2, c.Devices); });
        Assert.Equal(["get_chat", "get_chat", "get_contacts", "get_contacts"], common[0].Sources.Select(s => s.Artifact.TableName).Order());
        Assert.Equal(["get_chat", "get_chat"], common[1].Sources.Select(s => s.Artifact.TableName));
        Assert.Equal(["", "Dup One", "Dup Two"], common[2].Sources.Where(s => s.Device == 0).Select(s => s.Name).Order());
        Assert.Equal(6, common[2].Sources.Count);

        Assert.Empty(CrossDevice.CommonContacts([a])); // one device: nothing is common
    }

    [Theory]
    [InlineData("393331234567@s.whatsapp.net", false, false, null, "tel:331234567", "phone")]       // JID = the account's number
    [InlineData("393331234567:12@s.whatsapp.net", false, false, null, "tel:331234567", "phone")]    // device suffix dropped
    [InlineData("123456789012345@lid", false, false, null, "whatsapp-lid:123456789012345", "WhatsApp LID")]
    [InlineData("393331234567-1600000000@g.us", false, false, null, "whatsapp-group:393331234567-1600000000@g.us", "WhatsApp group")]
    [InlineData("Some.One@Example.org", false, false, null, "some.one@example.org", "e-mail")]
    [InlineData("333 123 4567", true, false, null, "tel:331234567", "phone")]
    [InlineData("987654321", false, true, "Telegram", "telegram:987654321", "Telegram ID")]
    [InlineData("@Some_User", false, true, "Telegram", "telegram:@some_user", "Telegram username")]
    [InlineData("some_user", false, true, "Instagram", "instagram:@some_user", "Instagram username")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", false, true, "Signal", "signal:0f8fad5b-d9cb-469f-a165-70867728950e", "Signal ID")]
    public void Identifiers_are_keyed_by_what_they_name(string v, bool phoneCol, bool appCol, string? app, string key, string kind) =>
        Assert.Equal((key, kind), CrossDevice.Identify(v, phoneCol, appCol, app, true));

    [Theory]
    [InlineData("333 123 4567", false, false, "WhatsApp")] // a national number outside a phone column says nothing
    [InlineData("42", false, true, "Telegram")]            // short numbers are row ids
    [InlineData("Some One", false, true, "Telegram")]      // a display name, not a username
    [InlineData("some_user", false, false, "Telegram")]    // a username outside an account column
    [InlineData("some_user", false, true, null)]           // no app: an ID means nothing
    public void Values_that_name_no_account_are_left_out(string v, bool phoneCol, bool appCol, string? app) =>
        Assert.Null(CrossDevice.Identify(v, phoneCol, appCol, app, true));
}
