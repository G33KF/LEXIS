using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core.Tests;

public class CoreTests : IDisposable
{
    readonly SyntheticCase _c = new();
    public void Dispose() => _c.Dispose();

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Manifest_is_read_generically_from_a_nested_report_folder()
    {
        using var s = CaseSession.Open(_c.Root); // finds ALEAPP_Reports_test/_lava_data.lava
        var m = s.Manifest;
        Assert.Equal(["Contacts", "Locations", "Messages"], m.ByCategory.Select(g => g.Key));
        var chat = m.Artifacts.Single(a => a.Name == "Chat Messages");
        Assert.Equal(["Timestamp", "Thread ID", "Body", "Direction", "From", "Media", "Message ID", "Account ID"], chat.Columns.Select(c => c.Display));
        Assert.True(chat.Column("timestamp")!.IsDateTime);
        Assert.True(chat.Column("Media")!.IsMedia);
        Assert.Equal("thread_id", chat.Conversation!.Discriminator);
        Assert.Equal("Sent", chat.Conversation.SentValue);
        Assert.Equal("ALEAPP", m.ParserInfo["leapp_name"]);
        Assert.Equal("zip", m.Params["type"]);
        Assert.True(s.HasTimeline);
    }

    [Fact]
    public void Query_pages_sorts_and_filters_including_reserved_words_and_datetime_text()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");

        var all = ArtifactQuery.Query(con, chat, new PageRequest(0, 2));
        Assert.Equal(4, all.Total);
        Assert.Equal(2, all.Rows.Count);

        var sorted = ArtifactQuery.Query(con, chat, new PageRequest(SortColumn: "from", Descending: true));
        Assert.Equal("+393333333333", sorted.Rows[0].Values[4]);

        Assert.Equal(1, ArtifactQuery.Query(con, chat, new PageRequest(Filter: "100%")).Total);   // % is literal
        Assert.Equal(0, ArtifactQuery.Query(con, chat, new PageRequest(Filter: "1_0")).Total);    // _ is literal
        Assert.Equal(1, ArtifactQuery.Query(con, chat, new PageRequest(Filter: "2024-06")).Total); // epoch matched as date
        Assert.Equal(0, ArtifactQuery.Query(con, chat, new PageRequest(Filter: "x' OR 1=1 --")).Total);
        Assert.Equal(2, ArtifactQuery.Query(con, chat, new PageRequest(RowIds: [1, 3])).Total);
        Assert.Equal(0, ArtifactQuery.Query(con, chat, new PageRequest(RowIds: [])).Total);
    }

    [Fact]
    public void Column_filters_bind_values_and_ignore_unknown_columns()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        long Count(params ColumnFilter[] f) => ArtifactQuery.Query(con, chat, new PageRequest(ColumnFilters: f)).Total;

        static ColumnFilter F(string c, string op, string? v = null, string? v2 = null) => new(c, op, v, v2);
        Assert.Equal(2, Count(F("direction", "equals", "Sent")));
        Assert.Equal(1, Count(F("body", "contains", "100%")));
        Assert.Equal(3, Count(F("body", "notcontains", "June")));
        Assert.Equal(1, Count(F("media", "empty")));
        Assert.Equal(2, Count(F("timestamp", "between", "1714557650", "1714557800")));
        Assert.Equal(1, Count(F("timestamp", "between", "1717000000", null)));
        Assert.Equal(3, Count(F("msg_id", "between", "502", null)));         // numbers kept as text still compare
        Assert.Equal(0, Count(F("body", "between", null, "5000")));          // text is not a number (CAST would make it 0)
        Assert.Equal(1, Count(F("direction", "equals", "Sent"), F("body", "contains", "June")));
        Assert.Equal(0, Count(F("body", "equals", "x' OR 1=1 --")));

        var directions = ArtifactQuery.ValueCounts(con, chat, "Direction");
        Assert.Equal(2, directions.Single(x => x.Value == "Sent").Count);
        Assert.Equal(4, directions.Sum(x => x.Count));
        Assert.Empty(ArtifactQuery.ValueCounts(con, chat, "Browser Name"));
        Assert.Equal("Samsung Internet (Browser)", MacroCategories.BrowserLabel("Browser"));
        Assert.Equal("Chrome", MacroCategories.BrowserLabel("Chrome"));
        Assert.Equal(4, Count(F("1=1) OR (1", "equals", "x")));       // not a column: ignored, never SQL
    }

    [Fact]
    public void Cursor_reads_the_same_rows_as_paged_queries_on_a_read_only_database()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        foreach (var req in new PageRequest[] { new(), new(SortColumn: "from", Descending: true),
                     new(ColumnFilters: [new("direction", "equals", "Sent")]), new(Filter: "photo") })
        {
            using var cur = new RecordCursor(con, chat, req);
            var expected = ArtifactQuery.Query(con, chat, req with { Limit = 100 }).Rows.Select(r => r.RowId).ToList();
            Assert.Equal(expected.Count, cur.Count);
            Assert.Equal(expected, cur.RowIds());
            Assert.Equal(expected.Skip(1).Take(2), cur.Fetch(1, 2).Select(r => r.RowId));
            Assert.Equal(expected.Count - 1, cur.PositionOf(expected[^1]));
        }
        Assert.Equal([2L, 4L], ArtifactQuery.All(con, chat, columnFilters: [new("direction", "equals", "Sent")]).Select(r => r.RowId));
    }

    [Fact]
    public void Column_filters_hold_on_values_in_memory()
    {
        Assert.True(new ColumnFilter("c", "contains", "OB").Matches("Bob", false));
        Assert.False(new ColumnFilter("c", "notcontains", "ob").Matches("Bob", false));
        Assert.True(new ColumnFilter("c", "between", "3", null).Matches("1,200", false));
        Assert.False(new ColumnFilter("c", "between", "3", "10").Matches("2", false));
        Assert.False(new ColumnFilter("c", "between", "3", "10").Matches("", false));
        var t0 = SyntheticCase.T0.ToString(System.Globalization.CultureInfo.InvariantCulture); // 2024-05-01 10:00:00 UTC
        Assert.True(new ColumnFilter("t", "between", t0, null).Matches("2024-05-01 10:00:00 UTC", true));
        Assert.False(new ColumnFilter("t", "between", null, t0).Matches("2024-05-01 10:00:01 UTC", true));
        Assert.True(new ColumnFilter("t", "empty").Matches("", true));
    }

    [Fact]
    public void Datetime_cells_format_in_the_chosen_zone()
    {
        var col = new ColumnInfo("t", "T", "datetime");
        Assert.Equal("2024-05-01 10:00:00 UTC", ValueFormatter.Format(SyntheticCase.T0, col));
        try
        {
            ValueFormatter.Zone = TimeZoneInfo.CreateCustomTimeZone("p2", TimeSpan.FromHours(2), "p2", "p2");
            Assert.Equal("2024-05-01 12:00:00 (UTC+02:00)", ValueFormatter.Format(SyntheticCase.T0, col));
        }
        finally { ValueFormatter.Zone = TimeZoneInfo.Utc; }
        Assert.Equal("not a time", ValueFormatter.Format("not a time", col));
    }

    [Fact]
    public void Summer_time_is_applied_per_date_and_its_changes_lose_no_hour()
    {
        try
        {
            ValueFormatter.Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
            Assert.Equal("2026-01-15 13:00:00 (UTC+01:00)", ValueFormatter.FormatTime(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
            Assert.Equal("2026-07-15 14:00:00 (UTC+02:00)", ValueFormatter.FormatTime(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
            long U(int mo, int d, int h, int mi) => new DateTimeOffset(2026, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            // 29 March: 02:30 does not exist on the clock, it reads as 03:30 summer time
            Assert.Equal(U(3, 29, 1, 30), ValueFormatter.ToUnixSeconds(new DateTime(2026, 3, 29, 2, 30, 0)));
            // 25 October: 02:30 happens twice, the first as a lower bound and the second as an upper one
            Assert.Equal(U(10, 25, 0, 30), ValueFormatter.ToUnixSeconds(new DateTime(2026, 10, 25, 2, 30, 0)));
            Assert.Equal(U(10, 25, 1, 30), ValueFormatter.ToUnixSeconds(new DateTime(2026, 10, 25, 2, 30, 0), upper: true));
            // the day of the change is 23 and 25 hours long, each end at its own offset
            var (from, to) = ValueFormatter.Days(new DateTime(2026, 3, 29), new DateTime(2026, 3, 29));
            Assert.Equal((U(3, 28, 23, 0), U(3, 29, 22, 0) - 1), (from!.Value, to!.Value));
            (from, to) = ValueFormatter.Days(new DateTime(2026, 10, 25), new DateTime(2026, 10, 25));
            Assert.Equal((U(10, 24, 22, 0), U(10, 25, 23, 0) - 1), (from!.Value, to!.Value));
        }
        finally { ValueFormatter.Zone = TimeZoneInfo.Utc; }
    }

    [Fact]
    public void Dates_written_as_text_read_in_the_display_zone_only_when_their_zone_is_stated()
    {
        Assert.Null(ValueFormatter.DateReading("2026-07-15T12:00:00Z")); // display zone UTC: nothing to convert
        try
        {
            ValueFormatter.Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
            Assert.Equal("in the display zone: 2026-07-15 14:00:00 (UTC+02:00)", ValueFormatter.DateReading("2026-07-15T12:00:00Z"));
            Assert.Equal("in the display zone: 2026-07-15 14:00:00 (UTC+02:00)", ValueFormatter.DateReading("2026-07-15 12:00:00 UTC"));
            Assert.Equal("in the display zone: 2026-01-15 13:00:00 (UTC+01:00)", ValueFormatter.DateReading("2026-01-15T17:00:00.250+05:00"));
            Assert.Equal("no time zone in the text; read as UTC: 2026-07-15 14:00:00 (UTC+02:00)", ValueFormatter.DateReading("2026-07-15 12:00:00"));
            Assert.Equal("in the display zone: 2026-07-15 14:00:00 (UTC+02:00)", ValueFormatter.DateReading(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)));
            Assert.Null(ValueFormatter.DateReading("2026-07-15 14:00:00 (UTC+02:00)")); // already shown in the display zone
            Assert.Null(ValueFormatter.DateReading("15/07/2026 12:00")); // day and month order not known
            Assert.Null(ValueFormatter.DateReading("call me at 2026-07-15 12:00"));
            Assert.Null(ValueFormatter.DateReading("1752580800"));
            var node = new PlistNode("when", "string", "2026-07-15T12:00:00Z", "when");
            Assert.Equal("in the display zone: 2026-07-15 14:00:00 (UTC+02:00)", node.Reading);
        }
        finally { ValueFormatter.Zone = TimeZoneInfo.Utc; }
    }

    [Theory]
    [InlineData("2026 %E5%B9%B4 FIFA %E4%B8%96%E7%95%8C%E6%9D%AF", "2026 年 FIFA 世界杯")]
    [InlineData("q=caf%C3%A9+au+lait", "q=café au lait")]
    [InlineData("100% sure + done", "100% sure + done")]
    [InlineData("bad %C3%28 utf8", "bad %C3%28 utf8")]
    [InlineData("a%2520b", "a b")]
    [InlineData("nul %00 here", "nul %00 here")]
    public void Percent_encoded_text_reads_decoded(string stored, string shown)
    {
        Assert.Equal(shown, ValueFormatter.Readable(stored));
        Assert.Equal(shown, ValueFormatter.Display(stored, new ColumnInfo("q", "Q", "text")));
    }

    [Theory]
    [InlineData("Outgoing <i data-feather=\"phone-outgoing\" stroke=\"green\"></i>", "Outgoing")]
    [InlineData("Answered Externally <i data-feather=\"phone-forwarded\"></i>", "Answered Externally")]
    [InlineData("<i>not an icon</i>", "<i>not an icon</i>")]
    public void The_icons_of_the_LEAPP_html_report_are_left_out_on_screen(string stored, string shown) =>
        Assert.Equal(shown, ValueFormatter.Display(stored, new ColumnInfo("t", "Type", "text")));

    [Fact]
    public void A_bare_number_lists_its_plausible_timestamp_readings()
    {
        Assert.Contains("Apple Cocoa seconds (2001): 2024-05-01 10:00:00 UTC", ValueFormatter.TimestampCandidates("736250400"));
        Assert.Contains("WebKit/Chrome microseconds (1601): 2024-05-01 10:00:00 UTC", ValueFormatter.TimestampCandidates("13359031200000000"));
        Assert.Contains("Unix milliseconds: 2024-05-01 10:00:00 UTC", ValueFormatter.TimestampCandidates("1714557600000"));
        Assert.DoesNotContain("Unix seconds", ValueFormatter.TimestampCandidates("1714557600000"));
        Assert.Null(ValueFormatter.TimestampCandidates("42"));
        Assert.Null(ValueFormatter.TimestampCandidates("abc"));
    }

    [Fact]
    public void Media_resolves_single_and_list_refs_and_refuses_paths_outside_the_case()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var r = new MediaResolver(s);
        var one = r.Resolve(con, "ref1");
        Assert.Single(one);
        Assert.True(one[0].Exists);
        Assert.Equal("image", one[0].Kind);
        Assert.Equal(2, r.Resolve(con, "[\"ref1\",\"ref2\"]").Count);
        var evil = r.Resolve(con, "ref_evil").Single();
        Assert.Null(evil.FullPath);
        Assert.Equal(3, r.All(con).Count);
    }

    [Fact]
    public void Timeline_is_built_from_the_records_datetime_columns_in_time_order()
    {
        using var s = CaseSession.Open(_c.Report);
        Assert.Equal([("Chat Messages", 4L), ("Trips", 3L)], Timeline.Activities(s));
        var (total, ev) = Timeline.Query(s, 0, 10);
        Assert.Equal(7, total);                                          // tl.db's 3 rows are not the source
        Assert.Equal(ev.Select(e => e.Epoch).Order(), ev.Select(e => e.Epoch));
        Assert.Equal(SyntheticCase.T0, ev[0].Epoch);
        Assert.Equal("2024-05-01 10:00:00 UTC", ev[0].Timestamp);
        Assert.Equal("Timestamp", ev[0].EventType);
        Assert.Equal(1717236000, Timeline.Query(s, 0, 1, descending: true).Events[0].Epoch);
        Assert.Equal(3, Timeline.Query(s, 0, 10, activity: "Trips").Total);
        var rome = Timeline.Query(s, 0, 10, filter: "Rome");
        Assert.Equal(1, rome.Total);
        Assert.Equal("Trips", rome.Events[0].Activity);
        Assert.Equal(1, rome.Events[0].RowId);
        Assert.Contains("Body: Hello 100% sure", ev.Single(e => e.Activity == "Chat Messages" && e.RowId == 1).Summary);
        Assert.Equal(2, Timeline.Query(s, 2, 2).Events.Count);
    }

    [Fact]
    public void Events_per_day_give_the_display_position_of_each_days_first_event()
    {
        using var s = CaseSession.Open(_c.Report);
        DateOnly may = new(2024, 5, 1), june = new(2024, 6, 1);
        Assert.Equal([new DayCount(may, 6, 0), new DayCount(june, 1, 6)], Timeline.Open(s).PerDay());
        Assert.Equal([new DayCount(may, 6, 1), new DayCount(june, 1, 0)], Timeline.Open(s, descending: true).PerDay());
    }

    [Fact]
    public void Timeline_keeps_the_chosen_artifacts_and_period()
    {
        using var s = CaseSession.Open(_c.Report);
        Assert.Equal(7, Timeline.Open([s], activities: ["Trips", "Chat Messages"]).Count);
        Assert.Equal(0, Timeline.Open([s], activities: []).Count);
        Assert.Equal(6, Timeline.Open([s], to: 1717236000 - 1).Count);                       // May only
        Assert.Equal(1, Timeline.Open([s], from: 1717236000).Count);                         // bounds included
        Assert.Equal(1, Timeline.Open([s], activities: ["Chat Messages"], from: SyntheticCase.T0, to: SyntheticCase.T0).Count);
    }

    [Fact]
    public void A_slash_wrapped_filter_is_a_case_insensitive_regex_everywhere()
    {
        using var s = CaseSession.Open(_c.Report);
        Assert.Equal(2, Timeline.Query(s, 0, 10, filter: "/^(hello|june)\\b/").Total);      // Hello 100% sure, June note
        Assert.Equal(0, Timeline.Query(s, 0, 10, filter: "/^ello/").Total);
        Assert.Equal(1, Timeline.Query(s, 0, 10, filter: "/2024-06-01/").Total);            // datetime text, as plain filters
        var hit = Assert.Single(GlobalSearch.Run(s, "/^\\+39 333/"));                      // not the chats' +39333...
        Assert.Equal("Contacts", hit.Artifact.Name);
        Assert.Equal(0, Timeline.Query(s, 0, 10, filter: "/[unclosed/").Total);              // invalid: read as plain text
    }

    [Fact]
    public void Search_syntax_combines_terms_phrases_exclusions_and_regexes()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        long Count(string f) => ArtifactQuery.Query(con, chat, new PageRequest(Filter: f)).Total;

        Assert.Equal(1, Count("pair photo"));             // both, in any order
        Assert.Equal(1, Count("note June"));
        Assert.Equal(0, Count("\"note June\""));          // the exact phrase only
        Assert.Equal(2, Count("hello | june"));
        Assert.Equal(2, Count("-sent"));
        Assert.Equal(2, Count("-ref1"));                  // a row whose media cell is empty is still "not ref1"
        Assert.Equal(2, Count("/^june/ | photo"));
        Assert.Equal(1, Count("=cmd|calc"));              // a | inside a word is text
        Assert.Equal(1, Count("/hello 100/"));            // a regex may hold spaces
        Assert.Equal(1, Count("\"100%\" -june"));
        Assert.Equal(4, Count(""));
        Assert.True(ArtifactQuery.TextMatches("a -b", "a c"));
        Assert.False(ArtifactQuery.TextMatches("a -b", "a b"));
        Assert.Equal(["hello", "june", "\"two words\"", "/x y/"],
            SearchQuery.Parse("hello | june -sent \"two words\" /x y/").Terms.Select(t => t.ToString()));

        // the advanced form composes the same syntax
        Assert.Equal("hello june -sent", SearchQuery.Compose("hello  june", SearchQuery.Mode.AllWords, false, "sent"));
        Assert.Equal("hello | june", SearchQuery.Compose("hello june", SearchQuery.Mode.AnyWord, false, ""));
        Assert.Equal("\"two words\"", SearchQuery.Compose("two \"words\"", SearchQuery.Mode.ExactPhrase, false, ""));
        Assert.Equal("/x y/ -\"-a\"", SearchQuery.Compose(" x y ", SearchQuery.Mode.RegularExpression, false, "-a"));
        Assert.Null(SearchQuery.Compose("(", SearchQuery.Mode.RegularExpression, false, ""));
        Assert.Equal(1, Count(SearchQuery.Compose("hello", SearchQuery.Mode.AllWords, true, "")!));
        Assert.Equal(0, Count(SearchQuery.Compose("hell", SearchQuery.Mode.AllWords, true, "")!));
        var whole = SearchQuery.Parse(SearchQuery.Compose("a/b c", SearchQuery.Mode.ExactPhrase, true, ""));
        Assert.True(Assert.Single(whole.Terms).Regex);
        Assert.True(whole.Matches(["x a/b c y"]));
        Assert.False(whole.Matches(["xa/b c"]));

        Assert.Equal([1L, 1L, 2L], ArtifactQuery.TermCounts(con, chat, "hello | june | sent"));
        var hits = GlobalSearch.Run(s, "anna | rome");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(1, h.TermCounts.Sum()));
        Assert.Equal([4L, 4L], Assert.Single(GlobalSearch.Run(s, "acc1 2024")).TermCounts); // no "either": every term counts every record
    }

    [Fact]
    public void Technical_fields_are_left_out_on_request_by_their_values()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        Assert.Equal(["media", "msg_id", "thread_id"], TechnicalColumns.Of(con, chat).Order());  // not account_id (text), not from (numbers in a "From")
        Assert.Null(s.SearchSkips(chat));
        Assert.Single(GlobalSearch.Run(s, "501"));
        s.SkipTechnical = true;
        Assert.Empty(GlobalSearch.Run(s, "501"));
        Assert.Empty(GlobalSearch.Run(s, "ref1"));
        Assert.Equal(1, Timeline.Query(s, 0, 10, filter: "hello").Total);

        Assert.True(TechnicalColumns.Technical("Note", ["d41d8cd98f00b204e9800998ecf8427e", "a3f0c1b2d4e5f60718293a4b5c6d7e8f"]));
        Assert.False(TechnicalColumns.Technical("Body", ["/see/this", "a/b"]));                 // a path-like message is a message
        Assert.True(TechnicalColumns.Technical("File path", ["/private/var/a.db", "/private/var/b.db"]));
        Assert.False(TechnicalColumns.Technical("File path", ["holiday photos", "/private/var/b.db"]));
    }

    [Fact]
    public void Searches_are_remembered_per_case_newest_first_and_results_tag_in_bulk()
    {
        using var s = CaseSession.Open(_c.Report);
        try
        {
            s.AddSearch("rome");
            s.AddSearch("hello | june");
            s.AddSearch("rome ");
            Assert.Equal(["rome", "hello | june"], s.SearchHistory);
            s.RemoveSearch("rome");
            Assert.Equal(["hello | june"], s.SearchHistory);
        }
        finally { s.ClearSearchHistory(); }
        Assert.Empty(s.SearchHistory);

        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        s.Tags.SetMany("get_chat", ArtifactQuery.MatchingRowIds(con, chat, "hello | june"), "Evidence");
        Assert.Equal([1L, 4L], s.Tags.ForTable("get_chat").Keys.Order());
        s.Tags.SetMany("get_chat", [1L], null);
        Assert.Equal([4L], s.Tags.ForTable("get_chat").Keys);

        // a file no record names is tagged by its path, beside the records (counted, listed, removable)
        var before = s.Tags.All().Count;
        s.Tags.SetFiles(["/sdcard/DCIM/a.jpg", "/sdcard/DCIM/b.jpg"], "Evidence");
        Assert.Equal("Evidence", s.Tags.FileTags()["SDCARD/DCIM/A.JPG"]);  // keyed by the path in the extraction, any case
        Assert.Equal(before + 2, s.Tags.All().Count);
        s.Tags.SetFiles(["/sdcard/DCIM/a.jpg", "/sdcard/DCIM/b.jpg"], null);
        Assert.Empty(s.Tags.FileTags());
    }

    [Fact]
    public void Media_know_the_earliest_message_they_came_with_and_their_file_dates()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var uses = MediaUses.Build(con, s.Manifest);
        Assert.Equal(["ref1", "ref2", "ref_evil"], uses.Keys.Order(StringComparer.Ordinal));
        var r1 = uses["ref1"];                                   // in rows 2 and 3: the earlier one
        Assert.Equal((2L, "1", "Local User", (bool?)true, (long?)1714557660), (r1.RowId, r1.Chat, r1.Sender, r1.Sent, r1.Epoch));
        Assert.Equal(("Received", "+393333333333"), (uses["ref2"].Direction, uses["ref2"].Sender));
        var media = new MediaResolver(s);
        Assert.Equal((1714557600L, 1714557700L), (media.Get(con, "ref2")!.Created, media.Get(con, "ref2")!.Modified)); // ms scaled to s
        Assert.Null(media.Get(con, "ref1")!.Created);            // 0: unknown
    }

    [Fact]
    public void Geo_pairs_columns_by_name_and_drops_invalid_points()
    {
        using var s = CaseSession.Open(_c.Report);
        var trips = s.Manifest.Artifacts.Single(a => a.TableName == "get_trips");
        var pairs = GeoExtractor.GeoPairs(trips);
        Assert.Equal(2, pairs.Count);                         // "Long Name" is not a longitude
        Assert.Equal("To Longitude", pairs[1].Lon.Display);
        Assert.Single(GeoExtractor.GeoArtifacts(s.Manifest));
        using var con = s.OpenDb();
        var pts = GeoExtractor.Extract(con, trips);
        Assert.Equal(2, pts.Count);                           // 0,0 / non-numeric / out of range dropped
        Assert.Contains(pts, p => p.Lat == 41.9028 && p.Lon == 12.4964);
    }

    [Fact]
    public void Tags_live_outside_the_evidence_database()
    {
        var dbHash = Sha(Path.Combine(_c.Report, "_lava_artifacts.db"));
        using (var s = CaseSession.Open(_c.Report))
        {
            s.Tags.Set("get_chat", 2, "Evidence", "formula text");
            s.Tags.Set("get_chat", 2, "Suspicious", "updated");
            s.Tags.Set("get_trips", 1, "Important");
            Assert.Equal("Suspicious", s.Tags.ForTable("get_chat")[2].Tag);
            Assert.Equal(2, s.Tags.All().Count);
            s.Tags.Remove("get_trips", 1);
            Assert.Single(s.Tags.All());
            using var con = s.OpenDb();
            Assert.NotEmpty(GlobalSearch.Run(s, "Rome"));
        }
        Assert.Equal(dbHash, Sha(Path.Combine(_c.Report, "_lava_artifacts.db")));
        Assert.True(File.Exists(Path.Combine(_c.Report, TagStore.FileName)));
    }

    [Fact]
    public void Csv_quotes_and_neutralises_formulas()
    {
        Assert.Equal("'=cmd|calc", CsvExporter.Cell("=cmd|calc"));
        Assert.Equal("\"a,\"\"b\"\"\"", CsvExporter.Cell("a,\"b\""));
        Assert.Equal("plain", CsvExporter.Cell("plain"));
    }

    [Fact]
    public void Json_export_writes_each_row_keyed_by_column()
    {
        using var s = CaseSession.Open(_c.Report);
        var a = s.Manifest.Artifacts.First(x => x.Name == "Chat Messages");
        var path = Path.Combine(_c.Root, "x.json");
        using (var con = s.OpenDb())
            JsonExporter.Write(path, [new ExportSection(a.Name, ArtifactQuery.ExistingColumns(con, a), ArtifactQuery.All(con, a), null, a, "f")]);
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement[0];
        Assert.Equal(("Chat Messages", "f"), (doc.GetProperty("artifact").GetString(), doc.GetProperty("filter").GetString()));
        Assert.Contains(doc.GetProperty("rows").EnumerateArray(), r => r.GetProperty("Body").GetString() == "=cmd|calc");
    }

    [Theory]
    [InlineData("WeChat - Messages", "WeChat", "Messages › Instant Messages")]
    [InlineData("SMS Messages", "SMS & MMS", "Messages › SMS / MMS")]
    [InlineData("FCM Dump", "Firebase Cloud Messaging", "Application › Notifications")]
    [InlineData("Call logs", "Call Logs", "Calls › Call Log")]
    [InlineData("Calendar - Events", "Calendar", "Calendar › Calendar")]
    [InlineData("WeChat - Payments", "WeChat", "Finance & Purchase › Transfers & Payments")]
    [InlineData("bunq - Account Holders", "bunq", "Finance & Purchase › Transfers & Payments")]
    [InlineData("Google Password Manager - Saved Logins", "Accounts", "User Accounts & Details › Passwords")]
    [InlineData("WeChat - Shared Locations", "WeChat", "Location Related › Locations")]
    [InlineData("Gmail - App Emails", "Email", "Messages › Emails")]
    [InlineData("Web Visits", "Chromium", "Search & Web › Web History")]
    [InlineData("Chrome - Cookies", "Chromium", "Search & Web › Cookies")]
    [InlineData("WiFi Config Store", "WiFi Profiles", "Networks & Connections › Wireless Networks")]
    [InlineData("MMKV - Recovered Records", "MMKV", "Other › Other")]
    [InlineData("Biome - App Intents", "Biome", "Application › Application Usage")]
    [InlineData("PowerLog - Device Wake", "PowerLog", "System & Logs › Logs & Events")]
    [InlineData("Spotify - Tracks", "Spotify", "Media › Audio")]
    [InlineData("Biome - Battery Level", "Biome", "System & Logs › Logs & Events")]
    [InlineData("Biome - Audio Route", "Biome", "Media › Audio")]
    public void Artifacts_sort_into_groups_and_types(string name, string category, string expected)
    {
        var a = new ArtifactInfo { Category = category, Name = name, TableName = "t", Columns = [] };
        var (group, type) = MacroCategories.Classify(a);
        Assert.Equal(expected, $"{group.Name} › {type}");
    }

    [Fact]
    public void Applications_group_categories_reading_one_app_folder()
    {
        static ArtifactInfo A(string cat, string name, string src) => new() { Category = cat, Name = name, TableName = name, Columns = [], SourcePath = src };
        Assert.Equal("com.tencent.mm", MacroCategories.AppOf("/data/data/com.tencent.mm/MicroMsg/x.db"));
        Assert.Equal("ABC-1", MacroCategories.AppOf("/private/var/mobile/Containers/Shared/AppGroup/ABC-1/ChatStorage.sqlite"));
        Assert.Null(MacroCategories.AppOf("/data/data/com.android.providers.contacts/databases/contacts2.db")); // system store
        Assert.Null(MacroCategories.AppOf("/data/data/com.google.android.gms/databases/x.db"));
        Assert.Null(MacroCategories.AppOf("/data/system/packages.xml"));
        Assert.Null(MacroCategories.AppOf("/data/data/com.samsung.android.providers.contacts/databases/calllog.db"));
        Assert.Null(MacroCategories.AppOf("/data/data/com.sec.android.provider.logsprovider/databases/logs.db"));
        Assert.Equal("com.acme.weatherprovider", MacroCategories.AppOf("/data/data/com.acme.weatherprovider/databases/w.db")); // an app, whatever its name
        Assert.Equal("com.android.chrome", MacroCategories.AppOf("/data/data/com.android.chrome/app_chrome/Default/History"));
        var apps = MacroCategories.Applications([
            A("Chromium", "History", "Dump/data/data/com.android.chrome/app_chrome/Default/History"),
            A("Chromium", "Cookies", "Dump/data/data/com.android.chrome/app_chrome/Default/Cookies"),
            A("Chrome Sync", "Sync", "Dump/data/data/com.android.chrome/app_chrome/Default/Sync Data/x"),
            A("SQLite Journaling", "Journals", "Dump/data/data/com.a.b/databases/x-wal\nDump/data/data/com.c.d/databases/y-wal"),
            A("Contacts", "Contacts", "Dump/data/data/com.android.providers.contacts/databases/contacts2.db"),
            A("WeChat", "Messages", "Dump/data/data/com.tencent.mm/MicroMsg/EnMicroMsg.db"),
            A("Call Logs", "Call Logs", "Dump/data/data/com.vendor.dialer/databases/calls.db"), // a kind of data, not an app
        ]);
        Assert.Equal(["Chromium", "WeChat"], apps.Select(x => x.Name));  // largest category names the app
        Assert.Equal(3, apps[0].Artifacts.Count);                         // Chrome Sync joins Chromium: same folder
    }

    [Fact]
    public void Chat_messages_carry_the_locations_a_sibling_artifact_keeps_by_message_id()
    {
        var lava = Path.Combine(_c.Report, "_lava_data.lava");
        File.WriteAllText(lava, File.ReadAllText(lava).Replace("\"Contacts\": [{", """
            "Places": [{"artifact_key": "get_places", "name": "Shared Locations", "tablename": "get_places", "module": "chat",
              "column_map": {"lat": "Latitude", "lon": "Longitude", "mid": "Message ID"}, "record_count": 2}],
            "Contacts": [{
            """));
        using (var w = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_c.Report, "_lava_artifacts.db")};Pooling=False"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = "CREATE TABLE get_places (lat TEXT, lon TEXT, mid TEXT); INSERT INTO get_places VALUES ('45.4642','9.19','502'), ('0','0','503');";
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var ms = ConversationBuilder.Build(con, s, chat, 100).Conversations.SelectMany(c => c.Messages).ToList();

        var shared = Assert.Single(ms, m => m.Location is not null);
        Assert.Equal("=cmd|calc", shared.Text);                     // message 502; 503's 0,0 is no position
        Assert.Equal("45.4642, 9.19", shared.LocationText);
        Assert.Equal(("get_places", 1L), (shared.Location!.TableName, shared.Location.RowId));
        var page = Path.Combine(_c.Root, "loc.html");
        ChatHtmlExporter.Write(page, s, chat, ConversationBuilder.Build(con, s, chat, 100).Conversations.Single(c => c.Key == "1"));
        Assert.Contains("Location 45.4642, 9.19 (Shared Locations, record 1)", File.ReadAllText(page));
    }

    [Fact]
    public void System_messages_are_marked_and_an_empty_record_is_not_an_empty_bubble()
    {
        var lava = Path.Combine(_c.Report, "_lava_data.lava");
        File.WriteAllText(lava, File.ReadAllText(lava).Replace("\"account_id\": \"Account ID\"}", "\"account_id\": \"Account ID\", \"kind\": \"Message Type\", \"call\": \"Call Result\", \"sysev\": \"System Event\"}"));
        using (var w = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_c.Report, "_lava_artifacts.db")};Pooling=False"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = "ALTER TABLE get_chat ADD COLUMN kind TEXT; ALTER TABLE get_chat ADD COLUMN call TEXT; ALTER TABLE get_chat ADD COLUMN sysev TEXT; UPDATE get_chat SET kind = 'Text';" +
                              "INSERT INTO get_chat VALUES (1714557700,'1',NULL,'Sent','+393332222222',NULL,'505','acc1','System Message',NULL,NULL);" +
                              "INSERT INTO get_chat VALUES (1714557710,'1','','Received','+393331111111',NULL,'506','acc1','Text','','');" +
                              "INSERT INTO get_chat VALUES (1714557720,'1','','Sent','+393332222222',NULL,'507','acc1','59','Outgoing voice call · Not answered (outcome 1) · 00:00:00','');" +
                              "INSERT INTO get_chat VALUES (1714557730,'1','','Received','+393331111111',NULL,'508','acc1','10','','End-to-end encryption notice');";
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var one = ConversationBuilder.Build(con, s, chat, 100).Conversations.Single(c => c.Key == "1");
        var sys = one.Messages.Single(m => m.MessageId == "505");
        Assert.True(sys.IsSystem);
        Assert.Equal(("505", "System message · no text in the report"), (sys.MessageId, sys.Placeholder));
        Assert.Equal("Text · no text in the report", one.Messages.Single(m => m.MessageId == "506").Placeholder);
        Assert.Equal("", one.Messages.Single(m => m.MessageId == "501").Placeholder);
        var call = one.Messages.Single(m => m.MessageId == "507");
        Assert.Equal(("Outgoing voice call · Not answered (outcome 1) · 00:00:00", true, false), (call.Text, call.IsSent, call.IsSystem));
        var ev = one.Messages.Single(m => m.MessageId == "508");
        Assert.Equal(("System Message: End-to-end encryption notice", true), (ev.Text, ev.IsSystem));
        var page = Path.Combine(_c.Root, "sys.html");
        ChatHtmlExporter.Write(page, s, chat, one);
        Assert.Contains("<div class=\"msg sys\">", File.ReadAllText(page));
    }

    [Fact]
    public void Chat_report_links_replies_marks_records_and_carries_tags_only_when_asked()
    {
        var lava = Path.Combine(_c.Report, "_lava_data.lava");
        File.WriteAllText(lava, File.ReadAllText(lava).Replace("\"account_id\": \"Account ID\"}", "\"account_id\": \"Account ID\", \"reply\": \"Reply To Message ID\"}"));
        using (var w = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_c.Report, "_lava_artifacts.db")};Pooling=False"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = "ALTER TABLE get_chat ADD COLUMN reply TEXT; UPDATE get_chat SET reply = '501' WHERE msg_id = '502'; UPDATE get_chat SET reply = '999' WHERE msg_id = '503';";
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var chats = ConversationBuilder.Build(con, s, chat, 100).Conversations;
        var one = chats.Single(c => c.Key == "1");
        var reply = one.Messages.Single(m => m.MessageId == "502");
        Assert.Same(one.Messages.Single(m => m.MessageId == "501"), reply.ReplyTo);
        Assert.Equal("+393331111111: Hello 100% sure", reply.QuoteText);
        Assert.Equal("Reply to message 999 (not in this chat)", chats.Single(c => c.Key == "2").Messages.Single(m => m.MessageId == "503").QuoteText);

        s.Tags.Set("get_chat", 1, "Relevant", "check sender");
        string Page(ChatExportOptions? o)
        {
            var p = Path.Combine(_c.Root, $"r{Guid.NewGuid():N}.html");
            ChatHtmlExporter.Write(p, s, chat, one, null, o);
            return File.ReadAllText(p);
        }
        var plain = Page(null);
        Assert.Contains("<a href=\"#r1\"", plain);                       // the quote links to the quoted message's row
        Assert.Contains("id=\"r2\"", plain);
        Assert.Contains(" &#183; Message ID 502</div>", plain); // record reference: the source's own identifiers
        Assert.Contains("Not included", plain);
        Assert.DoesNotContain("Relevant", plain);
        Assert.DoesNotContain("class=\"chat telegram\"", plain);
        Assert.True(plain.IndexOf("Evidence and report", StringComparison.Ordinal) < plain.IndexOf("Participants", StringComparison.Ordinal));

        var tagged = Page(new ChatExportOptions(ChatTheme.Telegram, TagsAndNotes: true));
        Assert.Contains("Examiner tag: Relevant · Note: check sender", tagged);
        Assert.Contains("Included (1 tagged messages)", tagged);
        Assert.Contains("class=\"chat telegram\"", tagged);
        Assert.Contains("class=\"av\"", tagged);
    }

    [Fact]
    public void Chat_export_writes_one_page_per_chat_with_participants_and_evidence_source()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var set = ConversationBuilder.Build(con, s, chat, 100);
        Assert.Equal(2, set.Conversations.Count);
        var dir = Directory.CreateDirectory(Path.Combine(_c.Root, "chats")).FullName;
        var (pages, failed) = ChatHtmlExporter.WriteEach(dir, s, chat, set.Conversations);
        Assert.Empty(failed);
        Assert.Equal(2, pages.Count);
        var one = File.ReadAllText(pages.Single(p => p.EndsWith("+393331111111 (1).html")));
        var two = File.ReadAllText(pages.Single(p => p.EndsWith("+393333333333 (2).html")));

        Assert.Contains("class=\"msg out\"", one);
        Assert.Contains("class=\"msg in\"", one);
        Assert.Contains("Device user (sent messages)", one);
        Assert.Contains("Matched: +393331111111 = Phone Number &quot;333 111 1111&quot; (phone digits, prefix ignored) in Contacts", one);
        Assert.Contains("Anna Example", one);
        Assert.Contains("Source on the device: /data/b.png", one);    // contact picture, with its path on the phone
        Assert.Contains("2024-05-01 10:00:00", one);                    // first message in the header
        Assert.DoesNotContain("Matched:", two);                       // +393333333333 is held by two contacts: ambiguous
        Assert.Contains("2024-06-01", two);                             // last message of chat 2

        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("source db copy")));
        foreach (var page in new[] { one, two })
        {
            Assert.Contains("/data/data/com.example.chat/databases/chat.db", page);
            Assert.DoesNotContain("Dump/", page);
            Assert.Contains(sha, page);
            Assert.Contains("extraction.zip", page);
            Assert.DoesNotContain("C:/in/", page);                      // the examiner's paths stay out of the report
            Assert.Contains("class=\"pager\"", page);
            Assert.DoesNotContain("LEAPP module", page);               // processing origin is not the evidence origin
            Assert.DoesNotContain("get_chat #", page);
        }
        Assert.Contains("Message ID 502", one);
        Assert.Contains("(file not in case)", two);                     // ref_evil escapes the case and is never copied
        var files = Directory.GetFiles(Path.Combine(dir, "+393333333333 (2)_files"));
        Assert.Equal(2, files.Length);                                  // aaa.jpg once, bbb.png once
        Assert.DoesNotContain(files, f => f.EndsWith("win.ini"));
        Assert.Null(ContactDirectory.Key("12"));                        // too short to identify anyone
        Assert.Null(ContactDirectory.Key("123456"));                    // short number: a count or id, not a phone
        Assert.Equal("tel:331111111", ContactDirectory.TelKey("+39 333 111-1111"));
        var dirx = new ContactDirectory(con, s, chat);
        Assert.Null(dirx.Find("Anna Example"));                         // names are never matched, only identifiers
        Assert.Null(dirx.Find("5331111111"));                           // same last nine digits, different number
        Assert.Null(dirx.Find("+393333333333"));                        // two records hold it: ambiguous
        Assert.NotNull(dirx.Find("+393331111111"));
        Assert.Equal("_CON", ChatHtmlExporter.FileName("CON"));
        Assert.DoesNotContain(ChatHtmlExporter.FileName(new string('a', 79) + "😀"), char.IsSurrogate);

        var rows = Path.Combine(_c.Root, "rows.html");
        HtmlReportExporter.Write(rows, s, [new ExportSection(chat.Name, ArtifactQuery.ExistingColumns(con, chat),
            ArtifactQuery.All(con, chat, rowIds: [2]), Artifact: chat, Filter: "1 selected rows")], chat.Name);
        var r = File.ReadAllText(rows);
        Assert.Contains("=cmd|calc", r);
        Assert.DoesNotContain("Hello 100% sure", r);
        Assert.Contains("<img src=\"rows_files/", r);
    }

    [Fact]
    public void Chats_group_by_local_account_and_list_the_owner_first()
    {
        using var s = CaseSession.Open(_c.Report);
        using var con = s.OpenDb();
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        Assert.Equal("Account ID", ConversationBuilder.AccountOf(chat)!.Display);
        var acc = Assert.Single(ConversationBuilder.Accounts(con, chat));
        Assert.Equal(("acc1", 2L, 4L), (acc.Account, acc.Chats, acc.Messages));
        Assert.Empty(ConversationBuilder.Build(con, s, chat, 100, account: "other").Conversations);
        var one = ConversationBuilder.Build(con, s, chat, 100, account: "acc1").Conversations.Single(c => c.Key == "1");
        Assert.Equal("acc1", one.Account);
        Assert.True(one.Participants[0].IsOwner);
        Assert.Equal("acc1", one.Participants[0].Id);
        Assert.Equal("+393331111111", one.Participants[1].Name);
        Assert.Equal(2, one.ParticipantCount);
        Assert.Null(one.Participants[0].LookupId);                      // the local account is never matched to a contact
        Assert.Equal("+393331111111", one.Participants[1].LookupId);    // a phone-shaped name is an identifier
        Assert.NotEqual("", one.Participants[1].First);
        Assert.True(string.CompareOrdinal(one.Participants[1].First, one.Participants[1].Last) <= 0);
        Assert.Equal("/data/data/com.example.chat/databases/chat.db", EvidenceOrigin.Sources(chat).Single());
    }

    [Theory]
    [InlineData("Dump/data/data/com.x/db/a.db", "/data/data/com.x/db/a.db")]           // full file-system dump, Android
    [InlineData("filesystem1/private/var/mobile/a.db", "/private/var/mobile/a.db")]    // full file-system dump, iOS
    [InlineData("\\data\\media\\0\\DCIM\\a.jpg", "/data/media/0/DCIM/a.jpg")]
    [InlineData("extra/KeychainDump/k.plist", "extra/KeychainDump/k.plist")]          // not a device path: as recorded
    [InlineData("AppDomain-com.a/Library/Preferences/p.plist", "AppDomain-com.a/Library/Preferences/p.plist")] // iOS backup domain kept
    [InlineData("", "")]
    public void Source_paths_are_shown_as_paths_on_the_device(string recorded, string device) =>
        Assert.Equal(device, EvidenceOrigin.DevicePath(recorded));

    [Fact]
    public void Source_export_copies_the_extraction_file_and_its_wal_verified_and_falls_back_to_the_working_copy()
    {
        using (var fallback = CaseSession.Open(_c.Report)) // extraction not reachable: ALEAPP's data/ copy
        {
            var chat0 = fallback.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
            var (f0, m0) = EvidenceOrigin.ExportSources(fallback, "Chat", EvidenceOrigin.RecordedSources(chat0), Path.Combine(_c.Root, "out0"));
            Assert.False(Assert.Single(f0).FromExtraction);
            Assert.Empty(m0);
        }
        var zipPath = Path.Combine(_c.Root, "EXTRACTION_FFS.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("Dump/data/data/com.example.chat/databases/chat.db").Open())) w.Write("original evidence");
            using (var w = new StreamWriter(z.CreateEntry("Dump/data/data/com.example.chat/databases/chat.db-wal").Open())) w.Write("wal");
        }
        var lava = Path.Combine(_c.Report, "_lava_data.lava");
        File.WriteAllText(lava, File.ReadAllText(lava).Replace("C:/in/extraction.zip", zipPath.Replace('\\', '/')));

        using var s = CaseSession.Open(_c.Report);
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var dir = Path.Combine(_c.Root, "out");
        var (files, missing) = EvidenceOrigin.ExportSources(s, "Chat", [.. EvidenceOrigin.RecordedSources(chat), "Dump/../../outside.db", "Dump/gone.db", "C:/Windows/win.ini", "Dump/data/a:b.db"], dir);
        Assert.Equal(["/data/data/com.example.chat/databases/chat.db", "/data/data/com.example.chat/databases/chat.db-wal"], files.Select(f => f.DevicePath));
        Assert.All(files, f => Assert.True(f.FromExtraction));
        Assert.Equal("original evidence", File.ReadAllText(Path.Combine(dir, "data", "data", "com.example.chat", "databases", "chat.db")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original evidence"))), files[0].Sha256);
        Assert.Equal(4, missing.Count); // traversal, rooted and invalid paths refused, the absent file reported
        Assert.Empty(Directory.EnumerateFiles(dir, "*.part", SearchOption.AllDirectories));
        Assert.Contains($"{files[0].Sha256}  data/data/com.example.chat/databases/chat.db", File.ReadAllText(Path.Combine(dir, "SHA256SUMS.txt")));
        Assert.Contains("NOT EXPORTED", File.ReadAllText(Path.Combine(dir, "PROVENANCE.txt")));
        Assert.False(File.Exists(Path.Combine(_c.Root, "outside.db")));
    }

    [Fact]
    public void Tar_targz_and_iTunes_backups_read_like_a_zip()
    {
        (string Name, string Text)[] files = [("./filesystem1/private/var/mobile/sms.db", "sms"), ("filesystem1/DCIM/IMG_1.jpg", "jpeg bytes")];
        var zip = Path.Combine(_c.Root, "x.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var (n, t) in files) using (var w = new StreamWriter(z.CreateEntry(n.TrimStart('.', '/')).Open())) w.Write(t);
        void Tar(Stream to)
        {
            using var tw = new System.Formats.Tar.TarWriter(to, leaveOpen: true);
            tw.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, "./filesystem1/"));
            foreach (var (n, t) in files)
                tw.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, n) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(t)) });
        }
        var tar = Path.Combine(_c.Root, "x.tar");
        using (var f = File.Create(tar)) Tar(f);
        var tgz = Path.Combine(_c.Root, "x.tar.gz");
        using (var f = File.Create(tgz)) using (var gz = new System.IO.Compression.GZipStream(f, System.IO.Compression.CompressionLevel.Fastest)) Tar(gz);

        string? root = null;
        foreach (var (path, kind) in new[] { (zip, "zip"), (tar, "tar"), (tgz, "tar.gz") })
        {
            using var x = ExtractionFiles.Open(path)!;
            Assert.Equal(kind, x.Kind);
            Assert.Equal(["filesystem1/DCIM/IMG_1.jpg"], x.Media);
            Assert.Equal("sms", x.Read("filesystem1/private/var/mobile/sms.db", s => new StreamReader(s).ReadToEnd()));
            Assert.Equal("eg", Encoding.UTF8.GetString(RawFiles.ReadAt(x, "filesystem1/DCIM/IMG_1.jpg", 2, 2)));
            Assert.Equal(["filesystem1/DCIM/IMG_1.jpg"], RawFiles.Search(x, [.. x.Files().Select(f => f.Path)], "JPEG").Select(h => h.Path));
            var (_, r, unreadable) = x.ContentManifest();
            Assert.Empty(unreadable);
            Assert.Equal(root ??= r, r); // the same content names the same root, whatever the container
        }

        var backup = Path.Combine(_c.Root, "00008030-backup");
        var id = "3d0d7e5fb2ce288813306e4d4636395e047a3d28";
        Directory.CreateDirectory(Path.Combine(backup, id[..2]));
        File.WriteAllText(Path.Combine(backup, id[..2], id), "sms");
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(backup, "Manifest.db")};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE Files(fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER, file BLOB);" +
                              $"INSERT INTO Files VALUES('{id}','HomeDomain','Library/SMS/sms.db',1,NULL),('ab','HomeDomain','Library/SMS',2,NULL)," +
                              "('../../evil','HomeDomain','x.db',1,NULL);";
            cmd.ExecuteNonQuery();
        }
        using (var b = ExtractionFiles.Open(backup)!)
        {
            Assert.Equal("iTunes backup", b.Kind);
            Assert.Equal([("HomeDomain/Library/SMS/sms.db", 3L)], b.Files());
            Assert.Equal("sms", b.Read("HomeDomain/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
        }
        File.WriteAllBytes(Path.Combine(backup, "Manifest.db"), new byte[64]); // encrypted: not SQLite, browsed as a plain folder
        using (var b = ExtractionFiles.Open(backup)!)
        {
            Assert.Equal("folder", b.Kind);
            Assert.False(b.Contains("../x.zip")); // a real file beside the folder: never reached through it
            Assert.Null(b.Read("../x.zip", 1 << 20));
            Assert.Throws<FileNotFoundException>(() => b.Open("../x.zip", s => 0));
        }
    }

    [Fact]
    public void Archives_inside_the_extraction_are_browsed_in_place_and_nest()
    {
        byte[] Zip(params (string Name, byte[] Data)[] files)
        {
            var ms = new MemoryStream();
            using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (n, d) in files) using (var w = z.CreateEntry(n).Open()) w.Write(d);
            return ms.ToArray();
        }
        var inner = Zip(("deep/note.txt", "inner text"u8.ToArray()));
        var gz = new MemoryStream();
        using (var g = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) g.Write("{\"a\":1}"u8);
        var dir = Path.Combine(_c.Root, "nested-extraction");
        Directory.CreateDirectory(Path.Combine(dir, "sdcard", "x!"));
        File.WriteAllBytes(Path.Combine(dir, "sdcard", "outer.zip"), Zip(("a.txt", "outer text"u8.ToArray()), ("in/inner.zip", inner)));
        File.WriteAllBytes(Path.Combine(dir, "sdcard", "data.json.gz"), gz.ToArray());
        File.WriteAllText(Path.Combine(dir, "sdcard", "x!", "real.txt"), "real");

        using var x = ExtractionFiles.Open(dir)!;
        Assert.True(x.Contains("sdcard/x!/real.txt")); // a folder named "x!" is a folder, not an archive
        using (var longPath = ExtractionFiles.Open(@"\\?\" + Path.GetFullPath(dir))!) // as ALEAPP records its input
            Assert.Equal("real", longPath.Read("sdcard/x!/real.txt", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal("zip", x.ArchiveKind("sdcard/outer.zip"));
        Assert.Null(x.ArchiveKind("sdcard/x!/real.txt"));
        Assert.Empty(x.ArchiveFiles("sdcard/x!/real.txt"));
        Assert.False(x.Contains("sdcard/outer.zip!/a.txt")); // not listed yet
        Assert.Equal(["sdcard/outer.zip!/a.txt", "sdcard/outer.zip!/in/inner.zip"], x.ArchiveFiles("sdcard/outer.zip").Select(f => f.Path));
        Assert.Equal("outer text", x.Read("sdcard/outer.zip!/a.txt", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal(10, x.SizeOf("sdcard/outer.zip!/a.txt"));
        var deep = Assert.Single(x.ArchiveFiles("sdcard/outer.zip!/in/inner.zip"));
        Assert.Equal("sdcard/outer.zip!/in/inner.zip!/deep/note.txt", deep.Path);
        Assert.Equal("inner text", Encoding.UTF8.GetString(RawFiles.ReadAt(x, deep.Path, 0, 100)));
        Assert.Equal([deep.Path], RawFiles.Search(x, [deep.Path, "sdcard/outer.zip!/a.txt"], "INNER").Select(h => h.Path));
        Assert.Equal(("sdcard/data.json.gz!/data.json", 7L), Assert.Single(x.ArchiveFiles("sdcard/data.json.gz")));
        Assert.Equal("{\"a\":1}", x.Read("sdcard/data.json.gz!/data.json", s => new StreamReader(s).ReadToEnd()));
        Assert.EndsWith("note.txt", x.CopyOut(deep.Path));
        Assert.Equal(RawFiles.Structured, RawFiles.Category("sdcard/data.json", "Text"));
        Assert.Equal(RawFiles.Databases, RawFiles.Category("data/com.x/databases/msgstore", "SQLite database"));
        Assert.Equal(RawFiles.Pictures, RawFiles.Category("cache/0a1b2c", "JPEG image"));
        Assert.Equal(RawFiles.Documents, RawFiles.Category("Download/a.docx", "ZIP archive (also APK, JAR, Office)"));
        Assert.Equal(RawFiles.PlainText, RawFiles.Category("notes", "Text"));
        Assert.Equal("é", RawFiles.AsText([0xFF, 0xFE, 0xE9, 0x00]));
        Assert.Null(RawFiles.AsText([0x01, 0x00, 0x02]));
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", RawFiles.Hashes(new MemoryStream("abc"u8.ToArray())).Md5);
    }

    [Fact]
    public void A_moved_extraction_is_verified_against_the_working_copies_and_located_per_machine()
    {
        string Zip(string name, string content)
        {
            var p = Path.Combine(_c.Root, name);
            using var z = System.IO.Compression.ZipFile.Open(p, System.IO.Compression.ZipArchiveMode.Create);
            using var w = new StreamWriter(z.CreateEntry("Dump/data/data/com.example.chat/databases/chat.db").Open());
            w.Write(content);
            return p;
        }
        var same = Zip("moved.zip", "source db copy");
        var other = Zip("other.zip", "another phone");
        Directory.CreateDirectory(Path.Combine(_c.Root, "empty"));

        using var s = CaseSession.Open(_c.Report);
        try
        {
            Assert.Null(s.Extraction); // the recorded C:/in/extraction.zip is not here
            Assert.True(EvidenceOrigin.VerifyExtraction(s, same).Same);
            var diff = EvidenceOrigin.VerifyExtraction(s, other);
            Assert.Equal((1, 1, 0), (diff.Checked, diff.Different.Count, diff.Absent.Count));
            Assert.Single(EvidenceOrigin.VerifyExtraction(s, Path.Combine(_c.Root, "empty")).Absent);

            s.LocateExtraction(same);
            Assert.Equal(Path.GetFullPath(same), EvidenceOrigin.InputPath(s));
            Assert.Equal("C:/in/extraction.zip", EvidenceOrigin.RecordedInputPath(s));
            Assert.True(s.Extraction!.Contains("Dump/data/data/com.example.chat/databases/chat.db"));
            var used = EvidenceOrigin.UsedBy(s);
            var readers = used["Dump/data/data/com.example.chat/databases/chat.db"];
            Assert.Equal(readers, used["Dump/data/data/com.example.chat/databases/chat.db-wal"]); // SQLite reads its log with it
            var h = EvidenceOrigin.Hashes(s, readers[0]).Single(x => x.Path == "/data/data/com.example.chat/databases/chat.db");
            Assert.Equal((true, 14L), (h.FromExtraction, h.Size)); // read from the located zip, not the working copy
            using (var again = CaseSession.Open(_c.Report)) Assert.Equal(Path.GetFullPath(same), again.LocatedExtraction); // remembered
            Assert.DoesNotContain(Directory.EnumerateFiles(_c.Report, "*", SearchOption.AllDirectories),
                f => File.ReadAllText(f).Contains("moved.zip"));                                  // and never written into the case
        }
        finally { s.LocateExtraction(null); }
        Assert.Equal("C:/in/extraction.zip", EvidenceOrigin.InputPath(s));
    }

    [Fact]
    public void Source_hashes_are_read_from_the_extraction_not_the_working_copy()
    {
        var zipPath = Path.Combine(_c.Root, "EXTRACTION_FFS.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("Dump/data/data/com.example.chat/databases/chat.db").Open()))
            w.Write("original evidence");
        File.WriteAllText(Path.Combine(_c.Root, "EXTRACTION_FFS.ufd"), "[General]\nFullName=Acme Phone 1\n[SHA256]\nEXTRACTION_FFS.zip=ABC123\n");
        var lava = Path.Combine(_c.Report, "_lava_data.lava");
        File.WriteAllText(lava, File.ReadAllText(lava).Replace("C:/in/extraction.zip", zipPath.Replace('\\', '/')));

        using var s = CaseSession.Open(_c.Report);
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var h = Assert.Single(EvidenceOrigin.Hashes(s, chat));
        Assert.Equal("/data/data/com.example.chat/databases/chat.db", h.Path);
        Assert.True(h.FromExtraction);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original evidence"))), h.Sha256);
        Assert.Equal(17, h.Size);

        using var con = s.OpenDb();
        var page = Path.Combine(_c.Root, "chat.html");
        ChatHtmlExporter.Write(page, s, chat, ConversationBuilder.Build(con, s, chat, 100).Conversations[0], null);
        var html = File.ReadAllText(page);
        Assert.Contains("SHA-256 (read from the extraction)", html);
        Assert.Contains("<td>Extraction</td><td>EXTRACTION_FFS.zip</td>", html);
        Assert.Contains("<td>SHA-256 of EXTRACTION_FFS.zip (UFD)</td><td>ABC123</td>", html);
        Assert.DoesNotContain(_c.Root.Replace('\\', '/'), html.Replace('\\', '/'));
        Assert.Contains("Acme Phone 1", html);
    }

    [Fact]
    public async Task Ufd_beside_the_extraction_gives_device_case_and_a_verifiable_hash()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_c.Root, "EXTRACTION_FFS 01")).FullName;
        var zip = Path.Combine(dir, "EXTRACTION_FFS.zip");
        File.WriteAllText(zip, "zip bytes");
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("zip bytes")));
        File.WriteAllText(Path.Combine(dir, "EXTRACTION_FFS.ufd"), $"""
            [Crime Case]
            Case Identifier=C-1
            Examiner Name=Examiner A
            Location=
            [DeviceInfo]
            Vendor=Acme
            IMEI1=000000000000001
            [Dumps]
            FileDump=EXTRACTION_FFS.zip
            [General]
            FullName=Acme Phone 1
            ExtractionType=FullFileSystem
            Date=22/07/2026 10:00:00(UTC+2)
            [SHA256]
            EXTRACTION_FFS.zip={sha.ToUpperInvariant()}
            """);

        var ufd = UfdInfo.ForExtraction(zip)!;
        Assert.Equal(zip, ufd.Extraction);
        Assert.Equal("C-1", ufd.Get("crime case", "case identifier"));
        var sum = ufd.Summary();
        Assert.Contains(("Device", "Model", "Acme Phone 1"), sum);
        Assert.Contains(("Device", "IMEI 1", "000000000000001"), sum);
        Assert.Contains(("Extraction", "Started", "22/07/2026 10:00:00(UTC+2)"), sum);   // as stored
        Assert.Contains(("Case", "Examiner Name", "Examiner A"), sum);
        Assert.DoesNotContain(sum, x => x.Name == "Location");                          // empty values are left out
        Assert.Equal((sha, true), await UfdInfo.VerifyAsync(ufd, zip));
        File.AppendAllText(zip, "!");
        Assert.False((await UfdInfo.VerifyAsync(ufd, zip)).Matches);
        Assert.Null(UfdInfo.ForExtraction(Path.Combine(_c.Root, "missing.zip")));
    }

    [Fact]
    public void Portable_case_all_items_round_trips_with_valid_hashes()
    {
        string zip = Path.Combine(_c.Root, "all.zip");
        var readerDir = Path.Combine(_c.Root, "build");
        Directory.CreateDirectory(readerDir);
        File.WriteAllText(Path.Combine(readerDir, "AleappReader.exe"), "stub");
        using (var s = CaseSession.Open(_c.Report)) PortableCase.Build(s, zip, new PortableSelection(), readerDir);

        using var pc = CaseSession.Open(zip);
        Assert.Equal(3, pc.Manifest.Artifacts.Count);
        Assert.True(pc.HasTimeline);
        Assert.True(pc.Package!.Intact);
        Assert.Null(pc.Package.SignatureValid);                                          // not signed
        Assert.True(File.Exists(Path.Combine(_c.Root, "Reader", "AleappReader.exe")));   // the reader beside the zip, not in it
        Assert.Contains("all.zip", File.ReadAllText(Path.Combine(_c.Root, "Open Case.cmd")));
        Assert.Equal(zip, PortableCase.FindBeside(_c.Root));
        // The examiner's paths stay out; the data source is kept as it was, and source hashes come from the copies inside.
        Assert.Equal("extraction.zip", pc.Manifest.Params["input"]);
        Assert.DoesNotContain("C:/in", File.ReadAllText(pc.ManifestPath));
        Assert.Contains(EvidenceOrigin.Provenance(pc), p => p.Name == "Extraction" && p.Value == "extraction.zip");
        Assert.Equal("", EvidenceOrigin.InputPath(pc));
        Assert.NotNull(EvidenceOrigin.Hashes(pc, pc.Manifest.Artifacts.Single(a => a.TableName == "get_chat"))[0].Sha256);
        Assert.False(Directory.Exists(Path.Combine(pc.RootDir, "media"))); // media are read from the zip
        string media;
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
            media = z.Entries.First(e => e.FullName.StartsWith("Case/media/", StringComparison.Ordinal) && e.Name == "aaa.jpg").FullName;
        Rewrite(zip, media, "tamper");
        Rewrite(zip, "Case/extra.txt", "added");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update)) z.GetEntry("Case/_Timeline/tl.db")!.Delete();
        var check = PortableCase.Check(zip)!;
        Assert.False(check.Intact);
        Assert.Equal(["Added: Case/extra.txt", "Missing: Case/_Timeline/tl.db", "Modified: " + media], check.Problems.Order());
        Assert.StartsWith("Portable case ALTERED: 3 problem(s)", check.Describe());
    }

    /// <summary>Replaces (or adds) one entry of a zip, as someone altering a package would.</summary>
    static void Rewrite(string zip, string entry, string content)
    {
        using var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update);
        z.GetEntry(entry)?.Delete();
        using var w = new StreamWriter(z.CreateEntry(entry).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    static string ReadEntry(string zip, string entry)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        using var r = new StreamReader(z.GetEntry(entry)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void Portable_case_signature_names_the_key_and_catches_a_rewritten_hash_list()
    {
        string zip = Path.Combine(_c.Root, "signed.zip");
        using var examiner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var s = CaseSession.Open(_c.Report)) PortableCase.Build(s, zip, new PortableSelection(), null, signer: examiner);
        var ok = PortableCase.Check(zip)!;
        Assert.True(ok.Intact);
        Assert.True(ok.SignatureValid);
        Assert.Equal(PortableCase.Fingerprint(examiner), ok.Fingerprint);
        Assert.Contains(ok.Fingerprint!, ok.Describe());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ReadEntry(zip, PortableCase.HashFile)))), ok.Root);

        // a file changed and its line in SHA256SUMS.txt rewritten to match: only the signature can tell
        var sums = ReadEntry(zip, PortableCase.HashFile);
        var line = sums.Split('\n').First(l => l.EndsWith("_lava_data.lava"));
        var name = line[(line.IndexOf("  ") + 2)..];
        Rewrite(zip, name, "{}");
        Rewrite(zip, PortableCase.HashFile, sums.Replace(line, Convert.ToHexStringLower(SHA256.HashData("{}"u8)) + "  " + name));
        var forged = PortableCase.Check(zip)!;
        Assert.False(forged.Intact);
        Assert.False(forged.SignatureValid);
        Assert.Equal(["Signature: it does not match SHA256SUMS.txt"], forged.Problems);

        // re-signed with another key: intact, but the fingerprint is not the examiner's
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Rewrite(zip, PortableCase.SignatureFile, Convert.ToBase64String(other.SignData(Encoding.UTF8.GetBytes(ReadEntry(zip, PortableCase.HashFile)), HashAlgorithmName.SHA256)));
        Rewrite(zip, PortableCase.PublicKeyFile, other.ExportSubjectPublicKeyInfoPem());
        var resigned = PortableCase.Check(zip)!;
        Assert.True(resigned.Intact);
        Assert.NotEqual(PortableCase.Fingerprint(examiner), resigned.Fingerprint);

        using (var z = System.IO.Compression.ZipFile.Open(Path.Combine(_c.Root, "x.zip"), System.IO.Compression.ZipArchiveMode.Create)) z.CreateEntry("a.txt");
        Assert.Null(PortableCase.Check(Path.Combine(_c.Root, "x.zip")));                   // not a portable case
    }

    [Fact]
    public void Portable_case_tagged_only_keeps_rowids_and_referenced_media()
    {
        string zip = Path.Combine(_c.Root, "tagged.zip");
        using (var s = CaseSession.Open(_c.Report))
        {
            s.Tags.Set("get_chat", 3, "Evidence", "two photos");
            PortableCase.Build(s, zip, new PortableSelection(TaggedOnly: true), null);
        }
        using var pc = CaseSession.Open(zip);
        var chat = pc.Manifest.Artifacts.Single();
        Assert.Equal("get_chat", chat.TableName);
        Assert.Equal(1, chat.RecordCount);
        using var con = pc.OpenDb();
        var row = ArtifactQuery.Query(con, chat, new PageRequest()).Rows.Single();
        Assert.Equal(3, row.RowId);
        Assert.Equal("Evidence", pc.Tags.ForTable("get_chat")[3].Tag);
        Assert.False(pc.Tags.Path.StartsWith(pc.RootDir, StringComparison.OrdinalIgnoreCase)); // not in the temp copy
        pc.Tags.Set("get_chat", 3, "Important");
        using (var again = CaseSession.Open(zip)) Assert.Equal("Important", again.Tags.ForTable("get_chat")[3].Tag);
        File.Delete(pc.Tags.Path);
        var media = new MediaResolver(pc).Resolve(con, row.Values[5]);
        Assert.Equal(2, media.Count);
        Assert.All(media, m => Assert.True(m.Exists));
        Assert.True(pc.Package!.Intact);

        // The media stay in the zip: read from it, written out only for a player, and checked against SHA256SUMS.txt.
        var (first, second) = (media[0], media[1]);
        Assert.False(File.Exists(first.FullPath));
        Assert.Equal(first.ReadAllBytes().Length, first.Size);
        Assert.Equal(first.ReadAllBytes(), File.ReadAllBytes(first.LocalFile()));
        pc.Deferred!.Dispose(); // lets the zip be rewritten; the next read reopens it as it then is
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Update))
        {
            var name = z.Entries.Single(x => Path.GetFullPath(Path.Combine(pc.ExtractedTempDir!, x.FullName)) == second.FullPath).FullName;
            z.GetEntry(name)!.Delete();
            using var w = new StreamWriter(z.CreateEntry(name).Open());
            w.Write("swapped");
        }
        Assert.Throws<IOException>(() => second.ReadAllBytes());
    }

    [Fact]
    public void Portable_case_keeps_only_the_chosen_artifacts_with_all_their_records()
    {
        string zip = Path.Combine(_c.Root, "chosen.zip");
        using (var s = CaseSession.Open(_c.Report))
        {
            s.Tags.Set("get_chat", 1, "Evidence");
            s.Tags.Set("get_trips", 1, "Important", "note on a left-out artifact");
            PortableCase.Build(s, zip, new PortableSelection(new HashSet<string> { "get_chat" }, Media: false), null);
        }
        using var pc = CaseSession.Open(zip);
        var chat = pc.Manifest.Artifacts.Single();
        Assert.Equal(4, chat.RecordCount);
        Assert.Equal("get_chat", Assert.Single(pc.Tags.All()).TableName);  // the left-out artifact's tags stay home
        File.Copy(zip, Path.Combine(_c.Root, "second.zip"));
        Assert.Null(PortableCase.FindBeside(_c.Root));                       // two cases: no guessing
        Assert.False(Directory.Exists(Path.Combine(pc.RootDir, "media")));  // media left out
        Assert.True(pc.Package!.Intact);
    }

    [Fact]
    public void Portable_case_carries_the_extraction_files_checked_against_the_working_copies()
    {
        const string chat = "Dump/data/data/com.example.chat/databases/chat.db";
        string Zip(string name, string content)
        {
            var p = Path.Combine(_c.Root, name);
            using var z = System.IO.Compression.ZipFile.Open(p, System.IO.Compression.ZipArchiveMode.Create);
            using var w = new StreamWriter(z.CreateEntry(chat).Open());
            w.Write(content);
            return p;
        }
        var same = Zip("moved.zip", "source db copy");
        var other = Zip("other.zip", "another phone");
        string zip = Path.Combine(_c.Root, "fs.zip"), bad = Path.Combine(_c.Root, "bad.zip");
        var sel = new PortableSelection(new HashSet<string> { "get_chat" }, Media: false, FileSystem: FileSystemScope.Sources);
        using (var s = CaseSession.Open(_c.Report))
            try
            {
                s.LocateExtraction(same);
                var files = PortableCase.FileSystemFiles(s, sel);
                Assert.Contains(files, f => f.Rel == chat && f.FromExtraction && f.Size == 14);
                Assert.Empty(PortableCase.FileSystemFiles(s, sel with { FileSystem = FileSystemScope.None }));
                PortableCase.Build(s, zip, sel, null);
                s.LocateExtraction(other);
                Assert.Throws<InvalidDataException>(() => PortableCase.Build(s, bad, sel, null));
            }
            finally { s.LocateExtraction(null); }
        Assert.False(File.Exists(bad));       // a file that differs from ALEAPP's copy: nothing written
        Assert.False(File.Exists(bad + ".part"));

        using var pc = CaseSession.Open(zip);
        Assert.True(pc.Manifest.PortableFileSystem);
        Assert.Contains("\"file_system\"", File.ReadAllText(pc.ManifestPath));
        Assert.Equal(Path.GetFullPath(zip) + " › Case/extraction", EvidenceOrigin.InputPath(pc));
        Assert.False(Directory.Exists(Path.Combine(pc.RootDir, PortableCase.ExtractionDir)));  // read from the zip, not unpacked
        Assert.Equal("portable case", pc.Extraction!.Kind);
        Assert.Equal("source db copy", pc.Extraction.Read(chat, st =>new StreamReader(st).ReadToEnd()));
        Assert.True(pc.Package!.Intact);
    }

    [Fact]
    public void Portable_case_carries_the_tagged_files_of_the_extraction()
    {
        const string photo = "Dump/data/media/0/DCIM/a.jpg", archive = "Dump/sdcard/x.zip";
        var extraction = Path.Combine(_c.Root, "tagged.zip");
        using (var z = System.IO.Compression.ZipFile.Open(extraction, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { (photo, "jpeg bytes"), ("Dump/data/data/com.example.chat/databases/chat.db", "source db copy") })
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
            using var inner = new MemoryStream();
            using (var iz = new System.IO.Compression.ZipArchive(inner, System.IO.Compression.ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(iz.CreateEntry("in.txt").Open())) w.Write("inside");
            using var e = z.CreateEntry(archive).Open();
            inner.Position = 0;
            inner.CopyTo(e);
        }
        var zip = Path.Combine(_c.Root, "tagged-pc.zip");
        var sel = new PortableSelection(new HashSet<string> { "get_chat" }, Media: false, TaggedFiles: true);
        using (var s = CaseSession.Open(_c.Report))
            try
            {
                s.LocateExtraction(extraction);
                s.Tags.SetFiles(["/" + photo, archive + ExtractionFiles.Inside + "in.txt"], "Evidence"); // keyed alike whatever the slashes
                Assert.Equal("Evidence", s.Tags.FileTags()[photo]);
                var files = PortableCase.FileSystemFiles(s, sel).Select(f => f.Rel).ToList();
                Assert.Equal([photo, archive], files.Order());                // the archive, not the file inside; no source files
                PortableCase.Build(s, zip, sel, null);
            }
            finally { s.LocateExtraction(null); s.Tags.SetFiles([photo, archive + ExtractionFiles.Inside + "in.txt"], null); }

        using var pc = CaseSession.Open(zip);
        Assert.True(pc.Manifest.PortableFileSystem);
        Assert.Equal("jpeg bytes", pc.Extraction!.Read(photo, st => new StreamReader(st).ReadToEnd()));
        Assert.False(pc.Extraction.Contains("Dump/data/data/com.example.chat/databases/chat.db"));
        Assert.Equal("Evidence", pc.Tags.FileTags()[photo]);
        Assert.True(pc.Package!.Intact);
    }

    [Fact]
    public void Geo_pairs_ignore_column_numbers_and_skip_deltas()
    {
        ColumnInfo C(string d) => new(d.Replace(' ', '_'), d, null);
        var photos = new ArtifactInfo { Category = "Photos.sqlite", Name = "Ph", TableName = "t",
            Columns = [C("zAsset-Latitude-47"), C("zExtAttr-Latitude-48"), C("zAsset-Longitude-49"), C("zExtAttr-Longitude-50")] };
        var pairs = GeoExtractor.GeoPairs(photos);
        Assert.Equal(2, pairs.Count);
        Assert.Equal("zAsset-Longitude-49", pairs[0].Lon.Display);
        Assert.Equal("zExtAttr-Longitude-50", pairs[1].Lon.Display);
        var tiles = new ArtifactInfo { Category = "Locations", Name = "Tiles", TableName = "t",
            Columns = [C("Latitude"), C("Longitude"), C("Delta Latitude"), C("Delta Longitude")] };
        Assert.Single(GeoExtractor.GeoPairs(tiles));
    }

    [Fact]
    public void Extraction_media_is_found_by_path_or_name_and_copied_out_read_only()
    {
        var zipPath = Path.Combine(_c.Root, "EXTRACTION_FFS.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            foreach (var e in new[] { "Dump/data/media/0/DCIM/Camera/IMG_1.jpg", "Dump/data/media/0/Pictures/IMG_1.jpg",
                                      "Dump/data/media/0/DCIM/Camera/unique.heic", "Dump/data/x.db" })
                using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write(e);
        var lava = Path.Combine(_c.Report, "_lava_data.lava");
        File.WriteAllText(lava, File.ReadAllText(lava).Replace("C:/in/extraction.zip", zipPath.Replace('\\', '/')));

        using var s = CaseSession.Open(_c.Report);
        var x = s.Extraction!;
        Assert.Equal(3, x.Media.Count);
        Assert.Equal("Dump/data/media/0/DCIM/Camera/IMG_1.jpg", x.Find("/storage/emulated/0/DCIM/Camera/IMG_1.jpg", []));
        Assert.Null(x.Find("IMG_1.jpg", []));                                   // ambiguous: two folders
        Assert.Equal("Dump/data/media/0/Pictures/IMG_1.jpg", x.Find("IMG_1.jpg", ["0/Pictures"]));
        Assert.Equal("Dump/data/media/0/DCIM/Camera/unique.heic", x.Find("unique.heic", []));
        var copy = x.CopyOut("Dump/data/media/0/DCIM/Camera/unique.heic")!;
        Assert.Equal("Dump/data/media/0/DCIM/Camera/unique.heic", File.ReadAllText(copy));
        Assert.True(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(copy, FileAttributes.Normal);
        Directory.Delete(Path.GetDirectoryName(copy)!, true);
    }

    [Fact]
    public void Ileapp_checkout_is_found_beside_aleapp_and_takes_itunes_backups()
    {
        var root = Path.Combine(_c.Root, "repo");
        Directory.CreateDirectory(Path.Combine(root, "ILEAPP"));
        File.WriteAllText(Path.Combine(root, "ILEAPP", "ileapp.py"), "");
        var ios = AleappInstall.Detect(Path.Combine(root, "ILEAPP"), "ileapp");
        Assert.True(ios.IsSource);
        Assert.Equal("iLEAPP", ios.Name);
        Assert.False(AleappInstall.Detect(root).IsSource); // no ALEAPP here
        Assert.Contains("itunes", ExtractionRunner.InputTypes("ileapp"));
        Assert.DoesNotContain("itunes", ExtractionRunner.InputTypes("aleapp"));
        Assert.Contains("ab", ExtractionRunner.InputTypes("aleapp"));
        Assert.DoesNotContain("ab", ExtractionRunner.InputTypes("ileapp"));
    }

    [Fact]
    public void Gmail_html_body_is_read_from_the_source_store()
    {
        static byte[] Len(int field, byte[] v) => [(byte)(field << 3 | 2), (byte)v.Length, .. v];
        var html = "<p>Hi <b>there</b></p>";
        var proto = Len(6, Len(2, Len(3, Len(2, Encoding.UTF8.GetBytes(html)))));
        var zipped = new MemoryStream();
        zipped.WriteByte(1);
        using (var z = new System.IO.Compression.ZLibStream(zipped, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(proto);
        var rel = "Dump/data/data/com.google.android.gm/databases/bigTopDataDB.-1";
        var db = Path.Combine(_c.Report, "data", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE item_messages (row_id INTEGER, server_perm_id TEXT, zipped_message_proto BLOB); " +
                              "INSERT INTO item_messages VALUES (1, 'msg-f:1', $b)";
            cmd.Parameters.AddWithValue("$b", zipped.ToArray());
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_c.Report);
        var a = new ArtifactInfo { Category = "Email", Name = "Gmail - App Emails", TableName = "t", Module = "gmailEmails", Columns = [] };
        ColumnInfo[] cols = [new("email_id", "Email ID", null), new("source_file", "Source File", null)];
        Assert.Equal(html, EmailBody.Html(s, a, cols, ["msg-f:1", rel]));
        Assert.Null(EmailBody.Html(s, a, cols, ["missing", rel]));
    }
}

public class RunProgressTests
{
    [Fact]
    public void Counts_artifacts_from_the_LEAPP_log()
    {
        var p = new RunProgress();
        Assert.Equal("Reading the input", p.Phase);
        Assert.False(p.Feed("Processing file system"));
        Assert.True(p.Feed("[1/3] SMS messages [sms] artifact started at 10:00:00 UTC"));
        Assert.Equal("SMS messages [sms]", p.Current);
        p.Feed("SMS messages [sms] artifact completed in 1.2s");
        p.Feed("[2/3] Chrome [chrome] artifact started at 10:00:02 UTC");
        p.Feed("No file found");
        p.Feed("Chrome [chrome] artifact completed in 0.0s");
        p.Feed("[3/3] Bad [bad] artifact started at 10:00:03 UTC");
        p.Feed("Reading Bad artifact had errors!");
        p.Feed("Bad [bad] artifact failed after 0.4s");
        Assert.Equal((3, 3, 1, 1, ""), (p.Done, p.Total, p.NoFiles, p.Failed, p.Current));
        Assert.Equal("Writing the report", p.Phase);

        var snap = p.Snapshot();
        p.Feed("[1/3] Again [again] artifact started at 10:00:04 UTC");
        Assert.Equal("", snap.Current); // the UI's copy does not move under it

        var other = new RunProgress();
        Assert.True(other.Feed("[4/12] Photos [photos] processing"));
        Assert.Equal(12, other.Total);
    }

    [Fact]
    public void A_chat_list_opens_into_the_messages_of_its_app()
    {
        using var c = new SyntheticCase();
        using var s = CaseSession.Open(c.Report);
        ArtifactInfo List(string name, string category) => new() { Category = category, Name = name, TableName = "x", Columns = [] };
        var messages = s.Manifest.Artifacts.Single(a => a.Conversation is not null);
        Assert.Same(messages, ConversationBuilder.MessagesOf(s.Manifest.Artifacts, List("Chat - Conversations", messages.Category)));
        Assert.Null(ConversationBuilder.MessagesOf(s.Manifest.Artifacts, List("Chat - Contacts", messages.Category)));
        Assert.Null(ConversationBuilder.MessagesOf(s.Manifest.Artifacts, List("Mail - Threads", "Email")));
        Assert.Null(ConversationBuilder.MessagesOf(s.Manifest.Artifacts, messages));
    }

    [Fact]
    public void Accounts_are_listed_once_per_service_with_their_names_and_never_a_password()
    {
        using var c = new SyntheticCase();
        var db = Path.Combine(c.Report, "_lava_artifacts.db");
        using (var w = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE accts (account_type TEXT, account_name TEXT, password TEXT);
                INSERT INTO accts VALUES ('com.google', 'a@example.com', 'p1'), ('com.google', 'A@example.com', 'p1'), ('com.whatsapp', '+10000000000', NULL), ('com.google', '', NULL);
                CREATE TABLE prof (full_name TEXT, email TEXT, phone TEXT);
                INSERT INTO prof VALUES ('Ann Example', 'a@example.com', NULL), ('', '', '+20000000000');
                """;
            cmd.ExecuteNonQuery();
        }
        ArtifactInfo Art(string name, string table, params string[] cols) => new()
        {
            Category = "Accounts", Name = name, TableName = table, RecordCount = 4,
            Columns = cols.Select(x => new ColumnInfo(x, string.Join(' ', x.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..])), null)).ToList(),
        };
        using var con = CaseSession.OpenReadOnly(db);
        var list = AccountRoster.Collect(con, [Art("Accounts_de", "accts", "account_type", "account_name", "password"),
            Art("Chrome Autofill - Profiles", "prof", "full_name", "email", "phone"), Art("Browser History", "accts", "account_name")]);
        Assert.Equal(["Chrome Autofill|+20000000000|", "Chrome Autofill|a@example.com|Ann Example", "com.google|a@example.com|", "com.whatsapp|+10000000000|"],
            list.Select(e => $"{e.Service}|{e.Account}|{e.Names}"));
        Assert.DoesNotContain(list, e => e.Account == "p1" || e.Names.Contains("p1"));
        Assert.Equal("Accounts_de", list.Single(e => e.Service == "com.google").Sources);
    }

    [Fact]
    public void The_overview_shows_the_last_sim_and_the_wipe_setup_and_restore_dates()
    {
        using var c = new SyntheticCase();
        var db = Path.Combine(c.Report, "_lava_artifacts.db");
        using (var w = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE siminfo (number TEXT, imsi TEXT, carrier_name TEXT, icc_id TEXT, user_id TEXT);
                INSERT INTO siminfo VALUES ('+10000000001', '222010000000001', 'Old Carrier', '8939000000000000001', '0'), ('+10000000002', '222010000000002', 'New Carrier', '8939000000000000002', '0');
                CREATE TABLE migr (ts INTEGER, kind TEXT);
                INSERT INTO migr VALUES (1714557600, 'restore'), (1717236000, 'update'), (NULL, 'x');
                CREATE TABLE wx (field_id TEXT, value TEXT, account_uin TEXT);
                INSERT INTO wx VALUES ('2', 'wxid_example', '1'), ('4', 'Ann W', '1'), ('6', '+390000000000', '1'), ('9', 'other', '1');
                CREATE TABLE pp (ts INTEGER, event TEXT);
                INSERT INTO pp VALUES (1739303385, 'reboot'), (1745833132, 'reboot factory_reset');
                CREATE TABLE au (created INTEGER, last_logged_in INTEGER, last_entered_foreground INTEGER, user_id TEXT, user_name TEXT, user_type TEXT, record_status TEXT);
                INSERT INTO au VALUES (1714557600, 1717236000, NULL, '150', 'Secure Folder', 'android.os.usertype.profile.MANAGED', 'User record'), (1700000000, NULL, NULL, '0', 'Owner', 'android.os.usertype.full.SYSTEM', 'User record'), (NULL, NULL, NULL, '10', NULL, NULL, 'Index entry without returned user record (missing)');
                CREATE TABLE cst (verification_time INTEGER, phone_number TEXT, imsi TEXT, state TEXT, sim_slot TEXT);
                INSERT INTO cst VALUES (1777398122, '+10000000009', '222010000000009', '3', '0'), (1700000000, '+10000000008', '222010000000008', '3', '1');
                """;
            cmd.ExecuteNonQuery();
        }
        ArtifactInfo Art(string cat, string name, string table, (string Col, string? Type)[] cols) => new()
        {
            Category = cat, Name = name, TableName = table, RecordCount = 3,
            Columns = cols.Select(x => new ColumnInfo(x.Col, string.Join(' ', x.Col.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..])), x.Type)).ToList(),
        };
        var arts = new[]
        {
            Art("Device Information", "siminfo", "siminfo", [("number", null), ("imsi", null), ("carrier_name", null), ("icc_id", null), ("user_id", null)]),
            Art("OS Updates", "Migrations", "migr", [("ts", "datetime"), ("kind", null)]),
            Art("WeChat", "WeChat - Account", "wx", [("field_id", null), ("value", null), ("account_uin", null)]),
            Art("Wipe & Setup", "Persistent Properties - Matched Text Observations", "pp", [("ts", "datetime"), ("event", null)]),
            Art("Device Users", "Android Users and Profiles", "au", [("created", "datetime"), ("last_logged_in", "datetime"), ("last_entered_foreground", "datetime"), ("user_id", null), ("user_name", null), ("user_type", null), ("record_status", null)]),
            Art("Device Information", "Constellation SIM Verifications", "cst", [("verification_time", "datetime"), ("phone_number", null), ("imsi", null), ("state", null), ("sim_slot", null)]),
        };
        using var con = CaseSession.OpenReadOnly(db);
        // no SIM in the device information: the last row of the SIM artifact, its SIM columns only
        Assert.Equal(["Number=+10000000002", "Imsi=222010000000002", "Carrier Name=New Carrier", "Icc Id=8939000000000000002"],
            DeviceOverview.LastSim([], con, arts).Select(x => $"{x.Label}={x.Value}"));
        // without one with an ICCID: the latest row of an artifact named for SIM cards with an IMSI or a number, with its date
        Assert.Equal(["Verification Time=2026-04-28 17:42:02 UTC", "Phone Number=+10000000009", "Imsi=222010000000009", "Sim Slot=0"],
            DeviceOverview.LastSim([], con, arts.Where(a => a.Name != "siminfo")).Select(x => $"{x.Label}={x.Value}"));
        // SIM records in the device information: the one updated last, then the other SIM facts not in it
        DeviceFact F(string cat, string label, string value) => new(cat, label, value, "src");
        var facts = new[]
        {
            F("Cellular", "SIM Cards", "Slot 1 -> ICCID: 111 | MSISDN: +1 | Last Update: 2026-06-15 17:33:32+00:00"),
            F("Cellular", "SIM Cards", "Slot 1 -> ICCID: 222 | MSISDN: +2 | Last Update: 2026-07-01 08:00:00+00:00"),
            F("Cellular", "Last Known ICCID", "222"), F("Cellular", "Last Good IMSI", "333"),
            F("Wipe Indicators", ".obliterated", "2025-01-02 03:04:05+00:00"), F("Device Information", "Model", "X"),
        };
        Assert.Equal(["SIM slot=1", "ICCID=222", "MSISDN=+2", "Last Update=2026-07-01 08:00:00+00:00", "Last Good IMSI=333"],
            DeviceOverview.LastSim(facts, con, arts).Select(x => $"{x.Label}={x.Value}"));
        // the Android users by id, each with its name, type and dates as stored; the status only when it is not a user record
        Assert.Equal(["User 0=Owner\nandroid.os.usertype.full.SYSTEM\nCreated 2023-11-14 22:13:20 UTC", "User 10=Index entry without returned user record (missing)",
            "User 150=Secure Folder\nandroid.os.usertype.profile.MANAGED\nCreated 2024-05-01 10:00:00 UTC\nLast logged in 2024-06-01 10:00:00 UTC"],
            DeviceOverview.Users(con, arts).Select(x => $"{x.Label}={x.Value}"));
        var life = DeviceOverview.Lifecycle(facts, con, arts);
        // each said in plain words, with what it was found as; of the persistent properties' lines only the factory reset
        Assert.Equal(["Wiped (factory reset)", "Data migrated (after an update or a restore)", "Wiped (factory reset)"], life.Select(x => x.Label));
        Assert.Equal("2025-04-28 09:38:52 UTC (reboot factory_reset)\n  in Persistent Properties - Matched Text Observations", life[2].Value);
        Assert.EndsWith("(Wipe Indicators .obliterated)", life[0].Value);
        Assert.Equal("2024-05-01 10:00:00 UTC\n2024-06-01 10:00:00 UTC\n  in Migrations", life[1].Value);
        // a key-value account artifact (WeChat's userinfo): its id, name and number by field id, the other fields not
        var wx = AccountRoster.Collect(con, arts).Single(x => x.Service == "WeChat");
        Assert.Equal(("WeChat", "wxid_example", "Ann W"), (wx.Service, wx.Account, wx.Names));
        Assert.Equal(["WeChat ID=wxid_example", "Phone Number=+390000000000"], wx.Ids.Select(i => $"{i.Label}={i.Value}"));
    }

    [Fact]
    public void The_accounts_summary_lists_each_name_number_address_and_app_id_once_then_the_sites()
    {
        AccountEntry E(string service, string sources, string names, params (string Label, string Value)[] ids) =>
            new(service, ids[0].Value, names, sources, [.. ids.Select(i => new AccountId(i.Label, i.Value))]);
        var g = AccountRoster.Summarize([
            E("com.google", "Accounts_ce", "", ("Account Name", "a@example.com")),
            E("Google Account History", "Google Account History", "", ("Account Name", "b@example.com")),
            E("Chrome Autofill", "Chrome Autofill - Profiles", "Ann Example", ("Email", "a@example.com"), ("Phone Number", "+39 300 000 0000")),
            E("WhatsApp", "WhatsApp - User Profile", "Ann", ("Registration Screen Phone Number", "3000000000")),
            E("com.bunq", "Account Extras", "", ("Account Name", "c@example.com")),
            E("MonetaryAccountBank", "bunq - Monetary Accounts", "A. Example", ("User Id", "71565419"), ("Account Id", "123456")),
            E("CloudKit", "Account Data", "", ("Username", "d@example.com")),
            E("Game Center", "Account Data", "", ("Username", "d@example.com")),
            E("https://www.example.com/login", "Google Password Manager - Saved Logins", "", ("Username", "ann1")),
            E("https://login.example.com/", "Google Password Manager - Saved Logins", "", ("Username", "ann2")),
            E("android://h@com.example.app/", "Google Password Manager - Saved Logins", "", ("Username", "ann3")),
        ]);
        // value | found in | detail
        IEnumerable<string> Lines(string title) => g.Single(x => x.Title == title).Rows.Select(r => $"{r.Value} | {r.Where} | {r.Detail}");
        Assert.Equal(["Names and nicknames", "Phone numbers", "E-mail addresses", "User IDs and usernames in apps", "Sites and apps with a saved login (password manager)"],
            g.Select(x => x.Title));
        Assert.Equal(["A. Example | bunq | bunq - Monetary Accounts", "Ann Example | Chrome, WhatsApp | Chrome Autofill - Profiles, WhatsApp - User Profile"],
            Lines("Names and nicknames")); // "Ann" is in "Ann Example"
        Assert.Equal(["+39 300 000 0000 | Chrome, WhatsApp | Chrome Autofill - Profiles, WhatsApp - User Profile"], Lines("Phone numbers")); // the same number without its country code
        Assert.Equal(["a@example.com | Google, Chrome | Accounts_ce, Chrome Autofill - Profiles", "b@example.com | Google | Google Account History",
            "c@example.com | bunq | Account Extras", "d@example.com | Apple | Account Data"], Lines("E-mail addresses"));
        Assert.Equal(["User Id 71565419 | bunq | A. Example   ·   Account Id 123456"], Lines("User IDs and usernames in apps"));
        Assert.Equal(["app com.example.app |  | ann3", "example.com |  | ann1, ann2"], Lines("Sites and apps with a saved login (password manager)"));
    }

    [Fact]
    public void Statistics_count_one_to_one_parties_positions_and_user_activity()
    {
        using var c = new SyntheticCase();
        using var s = CaseSession.Open(c.Report);
        var st = Statistics.Compute(s);

        // two chats, one party each besides the device: one received and one sent message per party
        var parties = st.Interactions.OrderBy(i => i.Key).ToList();
        Assert.Equal([ContactDirectory.TelKey("+393331111111"), ContactDirectory.TelKey("+393333333333")], parties.Select(i => i.Key));
        Assert.All(parties, i => { Assert.Equal(2, i.Count); Assert.Equal(1, i.Sent); });
        Assert.Equal(SyntheticCase.T0 + 60, parties[0].Last);
        Assert.True(parties[0].LastSent);

        var trips = Assert.Single(st.Positions);
        Assert.Equal("Trips", trips.Name);
        Assert.NotEmpty(Statistics.Places(trips.Points));

        // user activity: one event per chat message; trips are not the user's doing
        var map = Statistics.Activity(Statistics.UserEvents(s).Result.SelectMany(e => e.Epochs));
        Assert.Equal(4, map.Total);
        Assert.Equal(4, map.WeekHours.Sum());
    }
}
