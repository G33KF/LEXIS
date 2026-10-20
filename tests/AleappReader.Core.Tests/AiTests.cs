using System.Text.Json.Nodes;

namespace AleappReader.Core.Tests;

public sealed class AiTests : IDisposable
{
    readonly SyntheticCase _c = new();
    readonly string _db = Path.Combine(Path.GetTempPath(), "AleappReaderTests", Guid.NewGuid().ToString("N") + "-ai.db");
    public void Dispose() { _c.Dispose(); try { File.Delete(_db); } catch (IOException) { } }

    [Theory]
    [InlineData("http://localhost:11434", "local")]
    [InlineData("http://127.0.0.1:1234/v1", "local")]
    [InlineData("http://[::1]:1234/v1", "local")]
    [InlineData("http://192.168.1.20:11434", "LAN")]
    [InlineData("http://10.0.0.5:1234/v1", "LAN")]
    [InlineData("http://172.20.0.5:1234/v1", "LAN")]
    [InlineData("http://gpu-box:11434", "LAN")]
    [InlineData("http://172.32.0.5:1234/v1", "remote")]
    [InlineData("https://api.openai.com/v1", "remote")]
    [InlineData("not a url", "remote")]
    public void Provider_location_is_judged_from_the_address(string address, string where) =>
        Assert.Equal(where, new AiProvider("x", AiKind.OpenAi, address).Where);

    /// <summary>Live check against a local model server, only when LEXIS_AI_URL and LEXIS_AI_MODEL are set
    /// (e.g. http://localhost:1235/v1 and google/gemma-4-e2b); synthetic records only.</summary>
    [Fact]
    public async Task Live_local_model_summarises_and_reviews_with_citations()
    {
        if (Environment.GetEnvironmentVariable("LEXIS_AI_URL") is not { Length: > 0 } url ||
            Environment.GetEnvironmentVariable("LEXIS_AI_MODEL") is not { Length: > 0 } model) return;
        using var s = CaseSession.Open(_c.Report);
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var client = new AiClient(new AiProvider("live", AiKind.OpenAi, url), null);
        Assert.Contains(await client.Models(), m => m.Id == model);
        var (text, keys, _) = AiText.Records(s, [new AiSelection(chat)]);

        var sys = AiText.SystemIn("Italian");
        // Two records a step: the second step carries the summary of the first two with the next two.
        var context = await client.ContextOf(model);
        var listed = (await client.Models()).Single(m => m.Id == model);
        Assert.True(context is >= 4096, $"context {context}");
        Assert.Equal(listed.Context ?? 4096, context); // the list and the fresh query agree
        var x = await AiSteps.Run(s, client, model, sys, [new AiSelection(chat)], t => AiText.Summarize(t, "Italian"), rolling: true, max: 2, context, (_, _) => 0);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lexis-live-ai.txt"),
            $"SUMMARY ({x.Steps} steps, context {context}, uncited {x.Uncited})\n{x.Output}\nknown {string.Join(" ", x.Known)} | unknown {string.Join(" ", x.Unknown)}\n");
        Assert.Equal(2, x.Steps);
        Assert.Equal(4, x.Sent.Count);
        Assert.True(x.Known.Count >= 3, $"cited {x.Known.Count} of 4");
        Assert.Equal(0, x.Uncited);

        var none = await client.Chat(model, sys, AiText.Ask("Which bank account received money?", text, "Italian"), null);
        // Not asserted: whether a model admits the records do not answer is the model's quality, recorded for the examiner.
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "lexis-live-ai.txt"),
            $"UNANSWERABLE (expected \"Not in the records\": {none.Contains("Not in the records")})\n{none}\n");

        var theme = await client.Chat(model, sys, AiText.Theme("Remote code execution", "- spreadsheet formulas that run commands", text, "Italian"), null);
        var hits = AiText.ParseTheme(theme, keys);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "lexis-live-ai.txt"),
            $"THEME\n{theme}\nhits {string.Join(" ", hits.Select(h => h.Cite + (h.Known ? "" : "?")))}\n");
        Assert.Contains(hits, h => h.Cite == "[get_chat#2]" && h.Known);
    }

    [Fact]
    public void Every_line_of_an_answer_must_cite_a_record_sent()
    {
        var sent = new HashSet<string> { "[get_chat#1]", "[get_chat#2]" };
        const string answer = "- 10:00 A greets B [get_chat#1]\n- B plans to meet at noon\n- see get_chat#2\n\nPeople:\n- A [get_chat#9]\nNot in the records.\n---";
        Assert.Equal(["- B plans to meet at noon", "- A [get_chat#9]"], AiText.Uncited(answer, sent));
        var marked = AiText.MarkUncited(answer, sent).Split('\n');
        Assert.Equal("⚠ NO SOURCE · - B plans to meet at noon", marked[1]);
        Assert.Equal("People:", marked[4]);
        Assert.Contains("These lines of your answer have no record key:\n- B plans", AiText.CiteAgain("Q", answer, AiText.Uncited(answer, sent)));
        Assert.Equal(["[get_chat#2]", "[get_chat#1]"], AiText.Check("B (acc1) [get_chat#2, get_chat#1]; [C# code]", sent).Known);
        Assert.Empty(AiText.Uncited("* B [get_chat#2; get_chat#1]", sent));
    }

    [Fact]
    public void Translations_are_read_back_by_record_the_newest_first_and_only_for_records_sent()
    {
        AiResult R(long id, string output, params string[] cited) =>
            new(id, DateTimeOffset.UnixEpoch, "Translation", "LM Studio", "local", "m", "", "", output, cited, []);
        var t = AiText.Translations([
            R(2, "[get_chat#1] Hello again\n⚠ NO SOURCE · [get_chat#2] free text\n[get_chat#9] not sent", "[get_chat#1]"),
            R(1, "[get_chat#1] Hello\n- [get_chat#2]: See you at noon", "[get_chat#1]", "[get_chat#2]"),
        ]);
        Assert.Equal("Hello again", t["[get_chat#1]"]);
        Assert.Equal("See you at noon", t["[get_chat#2]"]);
        Assert.False(t.ContainsKey("[get_chat#9]"));
        Assert.Contains("exactly as written", AiText.Describe("Italiano"));
    }

    [Fact]
    public void Complete_analysis_cites_media_analyses_given_in_the_prompt()
    {
        var p = AiText.Analyze("who paid?", "{\"key\":\"[chat#1]\"}", "English", media: "[media#a1] (Transcription) pay me");
        Assert.Contains("Objective of the examiner: who paid?", p);
        Assert.Contains("MEDIA ANALYSES", p);
        var sent = new HashSet<string> { "[chat#1]", "[media#a1]" };
        Assert.Empty(AiText.Uncited("Answer:\nAnna paid [chat#1]\n- the voice note asks for it [media#a1]", sent));
        Assert.Equal(["[chat#1]", "[media#a1]"], AiText.Check("x [chat#1] y [media#a1]", sent).Known);
    }

    [Fact]
    public void Notes_of_one_item_are_the_latest_of_each_kind_and_only_its_own()
    {
        AiResult R(long id, string kind, string output, params string[] cited) =>
            new(id, DateTimeOffset.UnixEpoch, kind, "LM Studio", "local", "m", "", "", output, cited, []);
        var notes = AiText.Notes([
            R(4, "Image description item", "[media#7] a.jpg\nDescription: a receipt\nText: TOTAL 5\nTranslation: NONE", "[media#7]"),
            R(3, "Explanation", "[get_chat#1] What it is: a message\n[get_chat#1] flag: read\n[get_chat#2] other record", "[get_chat#1]", "[get_chat#2]"),
            R(2, "Translation", "[get_chat#1] Hello", "[get_chat#1]"),
            R(1, "Translation", "[get_chat#1] older", "[get_chat#1]"),
            R(0, "Summary", "all of it [get_chat#1]", "[get_chat#1]"),
        ], "[get_chat#1]");
        Assert.Equal([("Explanation", "What it is: a message\nflag: read"), ("Translation", "Hello")], notes.Select(n => (n.Name, n.Text)));
        var image = AiText.Notes([R(4, "Image description item", "[media#7] a.jpg\nDescription: a receipt\nText: TOTAL 5\nTranslation: NONE", "[media#7]")], "[media#7]");
        Assert.Equal([("Description", "a receipt"), ("Text in the image (OCR)", "TOTAL 5")], image.Select(n => (n.Name, n.Text)));
    }

    [Fact]
    public void Claude_cli_is_always_remote() =>
        Assert.Equal("remote", new AiProvider("Claude", AiKind.ClaudeCli, "http://localhost").Where);

    [Fact]
    public void Records_carry_their_citation_keys_and_citations_are_checked_against_them()
    {
        using var s = CaseSession.Open(_c.Report);
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var (text, keys, omitted) = AiText.Records(s, [new AiSelection(chat, [1L, 3L])]);
        Assert.Equal(0, omitted);
        Assert.Equal(["[get_chat#1]", "[get_chat#3]"], keys.Order());
        // JSON Lines: each line a JSON object with its key and its fields by name
        var first = JsonNode.Parse(text.Split('\n')[0])!;
        Assert.Equal(("[get_chat#1]", "Chat Messages"), ((string?)first["key"], (string?)first["artifact"]));
        Assert.Contains("photo pair", (string?)JsonNode.Parse(text.Split('\n')[1])!["Body"]);

        // text in any script goes as written, not as \u escapes; the answer's language is said before and after the records
        var p = AiText.Summarize("""{"key":"[get_chat#1]","Body":"明天见"}""", "Italian");
        Assert.Contains("明天见", p);
        Assert.EndsWith("then answer in Italian. Words quoted from a record stay as written there.", p);
        Assert.Contains("ANSWER SO FAR", AiText.Continue(p, "- x [get_chat#1]", "{}"));
        Assert.EndsWith("Words quoted from a record stay as written there.", AiText.Continue(p, "- x [get_chat#1]", "{}"));
        Assert.DoesNotContain("明天见", AiText.PromptOf(p));
        Assert.Contains("match by meaning", AiText.Theme("Drugs", "- prices", "{}", "Italian"));

        var (known, unknown) = AiText.Check("A greets B [get_chat#1]; then [get_chat#2] and [get_chat#1] again.", keys);
        Assert.Equal(["[get_chat#1]"], known);
        Assert.Equal(["[get_chat#2]"], unknown); // not sent: invented
    }

    [Fact]
    public void Records_beyond_the_budget_are_counted_not_sent()
    {
        using var s = CaseSession.Open(_c.Report);
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        var (_, keys, omitted) = AiText.Records(s, [new AiSelection(chat, [1L, 2L, 3L, 4L])], budget: 150);
        Assert.Equal(4, keys.Count + omitted);
        Assert.True(omitted > 0);
        var (_, all, rest) = AiText.Records(s, [new AiSelection(chat), new AiSelection(chat, [1L, 2L])], budget: 150);
        Assert.Equal(6, all.Count + rest); // the whole table counted, and the second selection past the budget too
    }

    [Fact]
    public void Selection_without_ids_follows_the_view_filter()
    {
        using var s = CaseSession.Open(_c.Report);
        var chat = s.Manifest.Artifacts.Single(a => a.TableName == "get_chat");
        Assert.Equal([1L, 2L, 3L, 4L], AiText.RowIds(s, new AiSelection(chat)));
        Assert.Equal([4L], AiText.RowIds(s, new AiSelection(chat, Filter: "June")));
        var (_, keys, _) = AiText.Records(s, [new AiSelection(chat, Filter: "June")]);
        Assert.Equal(["[get_chat#4]"], keys);
    }

    [Fact]
    public void Theme_reply_lines_become_hits_marked_known_or_not()
    {
        var sent = new HashSet<string> { "[get_chat#1]", "[get_chat#2]" };
        var hits = AiText.ParseTheme("- [get_chat#2] — price talk: \"50 a piece\"\nNONE of the others\n[get_chat#9]: made up\ntext [get_chat#1] mid-line", sent);
        Assert.Equal(2, hits.Count);
        Assert.Equal(("[get_chat#2]", "price talk: \"50 a piece\"", true), (hits[0].Cite, hits[0].Reason, hits[0].Known));
        Assert.False(hits[1].Known);
        // brackets dropped (as gemma-4-e2b does): counted when it is a key sent, plain text otherwise
        var bare = AiText.ParseTheme("get_chat#1, greeting\nother#5 not a record\nC# code", sent);
        Assert.Equal(("[get_chat#1]", "greeting", true), (bare.Single().Cite, bare.Single().Reason, bare.Single().Known));
        Assert.Equal(["[get_chat#2]"], AiText.Check("as get_chat#2 says; see issue#7", sent).Known);
    }

    [Fact]
    public void Store_keeps_sends_results_and_resume_marks()
    {
        var store = new AiStore(_db);
        var p = new AiProvider("LM Studio", AiKind.OpenAi, "http://localhost:1234/v1");
        Assert.Empty(store.Results());
        var send = store.LogSend(p, "qwen", "Summarize", "[get_chat#1] …");
        var r = store.Save(p, "qwen", "Summary", "Summarise", "2 records of Chat Messages", "Hi [get_chat#1]", ["[get_chat#1]"], [], send);
        Assert.Equal("local", store.Sends().Single().Where);
        var back = store.Results().Single();
        Assert.Equal((r.Id, "qwen", "Summary", "local"), (back.Id, back.Model, back.Kind, back.Where));
        Assert.Equal(["[get_chat#1]"], back.Cited);
        Assert.StartsWith("AI-generated · qwen · LM Studio (local)", back.Label);
        Assert.False(store.IsDone("theme|x|1"));
        store.MarkDone("theme|x|1");
        Assert.True(store.IsDone("theme|x|1"));
    }

    [Fact]
    public async Task Mcp_server_answers_the_protocol_and_logs_every_tool_answer()
    {
        using var s = CaseSession.Open(_c.Report);
        var store = new AiStore(_db);
        var mcp = new McpServer(s, store, "run1");
        var input = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search","arguments":{"query":"June"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_record","arguments":{"table":"get_chat","row":2}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_record","arguments":{"table":"nope","row":1}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"bogus"}""",
            "not json",
            """[1,2]""",
            """{"jsonrpc":"2.0","id":7,"method":42}""",
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":{"x":1}}}""");
        var output = new StringWriter();
        await mcp.Run(new StringReader(input), output);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();

        Assert.Equal(10, replies.Count); // the notification gets none; malformed messages get an error, the server goes on
        Assert.Equal(-32700, (int)replies[7]["error"]!["code"]!);
        Assert.Equal(-32601, (int)replies[8]["error"]!["code"]!);
        Assert.Equal(-32603, (int)replies[9]["error"]!["code"]!);
        Assert.Equal(2, store.Sends().Count(x => x.Purpose.StartsWith(McpServer.Purpose("run1")))); // the error answer is not "read"
        Assert.Equal("lexis", (string?)replies[0]["result"]!["serverInfo"]!["name"]);
        Assert.Contains("search", replies[1]["result"]!["tools"]!.AsArray().Select(t => (string?)t!["name"]));
        Assert.Contains("""{"key":"[get_chat#4]","artifact":"Chat Messages",""", (string?)replies[2]["result"]!["content"]![0]!["text"]);
        Assert.Contains("Body: =cmd|calc", (string?)replies[3]["result"]!["content"]![0]!["text"]); // read_record: one field a line
        Assert.True((bool)replies[4]["result"]!["isError"]!);
        Assert.Equal(-32601, (int)replies[5]["error"]!["code"]!);
        Assert.Equal(-32700, (int)replies[6]["error"]!["code"]!);
        Assert.Equal(3, store.Sends().Count); // three tool answers left the machine
        Assert.All(store.Sends(), x => Assert.Equal("remote", x.Where));
    }

    [Fact]
    public async Task Mcp_for_another_program_shows_a_table_structure_and_logs_as_that_client()
    {
        using var s = CaseSession.Open(_c.Report);
        var store = new AiStore(_db);
        var run = McpServer.ExternalRun + "1";
        var output = new StringWriter();
        await new McpServer(s, store, run).Run(new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"describe_table","arguments":{"table":"get_chat"}}}"""), output);
        var text = (string?)JsonNode.Parse(output.ToString())!["result"]!["content"]![0]!["text"];
        Assert.StartsWith("get_chat · Chat Messages", text);
        Assert.Contains("columns:\n- ", text);
        Assert.Contains("\nchat: thread by ", text);
        Assert.DoesNotContain("=cmd|calc", text); // structure only, no record
        var send = Assert.Single(store.Sends());
        Assert.Equal(("MCP client (other program)", "remote"), (send.Provider, send.Where)); // its model may be anywhere
        Assert.StartsWith(McpServer.Purpose(run), send.Purpose);

        var cfg = JsonNode.Parse(McpServer.Config("lexis.exe", ["--mcp", "m", "--run", run], true))!;
        Assert.Equal("lexis.exe", (string?)cfg["mcpServers"]!["lexis"]!["command"]);
        Assert.Equal(run, (string?)cfg["mcpServers"]!["lexis"]!["args"]![3]);
    }

    [Fact]
    public async Task Mcp_inside_lexis_answers_on_loopback_with_the_token_and_through_the_bridge()
    {
        using var s = CaseSession.Open(_c.Report);
        var store = new AiStore(_db);
        McpServer? current = null;
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var server = new McpHttpServer(() => current, port, "t0ken");
        using var http = new HttpClient();
        const string call = """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_artifacts","arguments":{}}}""";
        StringContent Body(string b) => new(b, System.Text.Encoding.UTF8, "application/json");

        // no case open: the server starts and says so
        var idle = JsonNode.Parse(await (await http.PostAsync(server.Url, Body(call))).Content.ReadAsStringAsync())!;
        Assert.Equal(McpServer.NoCase, (string?)idle["result"]!["content"]![0]!["text"]);
        Assert.True((bool)idle["result"]!["isError"]!);

        current = new McpServer(s, store, McpServer.ExternalRun + "h");
        var ok = JsonNode.Parse(await (await http.PostAsync(server.Url, Body(call))).Content.ReadAsStringAsync())!;
        Assert.Equal(7, (int)ok["id"]!);
        Assert.Contains("get_chat", (string?)ok["result"]!["content"]![0]!["text"]);
        Assert.Single(store.Sends());

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.PostAsync($"http://127.0.0.1:{port}/mcp/wrong", Body(call))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Accepted,
            (await http.PostAsync(server.Url, Body("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""))).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, (await http.GetAsync(server.Url)).StatusCode);
        var page = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = Body(call) };
        page.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(page)).StatusCode);
        Assert.Single(store.Sends()); // none of those read anything

        // the bridge: stdio in, the open LEXIS answers; a notification gets nothing back
        var output = new StringWriter();
        await McpHttpServer.Bridge(new StringReader(call + "\n" + """{"jsonrpc":"2.0","method":"notifications/initialized"}"""), output, server.Url);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("get_chat", (string?)JsonNode.Parse(Assert.Single(lines))!["result"]!["content"]![0]!["text"]);

        // LEXIS closed: the bridge says so as an error answer
        server.Dispose();
        output = new StringWriter();
        await McpHttpServer.Bridge(new StringReader(call), output, server.Url);
        Assert.Contains("LEXIS is not open", (string?)JsonNode.Parse(output.ToString())!["error"]!["message"]);
    }

    [Fact]
    public void Adding_lexis_to_a_client_keeps_its_other_servers_and_the_file_as_it_was()
    {
        var file = Path.Combine(Path.GetDirectoryName(_db)!, "client-" + Guid.NewGuid().ToString("N"), "mcp.json");
        Assert.Null(McpClients.Add(file, McpClients.UrlEntry("http://127.0.0.1:1/mcp/a"))); // no file yet: made
        Assert.Equal("http://127.0.0.1:1/mcp/a", (string?)McpClients.Entry(file)!["url"]);

        const string before = """{"theme":"dark","mcpServers":{"other":{"command":"x"}}}""";
        File.WriteAllText(file, before);
        var backup = McpClients.Add(file, McpClients.BridgeEntry("C:\\LEXIS.exe"));
        McpClients.Add(file, McpClients.BridgeEntry("C:\\LEXIS2.exe")); // again: the backup stays the first one
        Assert.Equal(before, File.ReadAllText(backup!));
        var now = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("dark", (string?)now["theme"]);
        Assert.Equal("x", (string?)now["mcpServers"]!["other"]!["command"]);
        Assert.Equal("--mcp-bridge", (string?)now["mcpServers"]!["lexis"]!["args"]![0]);
        Assert.Equal("C:\\LEXIS2.exe", (string?)now["mcpServers"]!["lexis"]!["command"]);

        File.WriteAllText(file, "not json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => McpClients.Add(file, McpClients.UrlEntry("u")));
        Assert.Equal("not json", File.ReadAllText(file));
    }

    [Fact]
    public void Ai_base_sits_beside_the_case_and_mcp_logs_nowhere_else()
    {
        using var s = CaseSession.Open(_c.Report);
        var own = AiStore.For(s).Path;
        Assert.Equal(Path.Combine(s.RootDir, AiStore.FileName), own);
        Assert.True(AiStore.Allowed(s, own));
        Assert.True(AiStore.Allowed(s, Path.Combine(AiStore.Folder, "other.db")));
        Assert.False(AiStore.Allowed(s, Path.Combine(s.RootDir, "evidence.db")));
        Assert.False(AiStore.Allowed(s, Path.Combine(AiStore.Folder, "..", "x.db")));
        Assert.False(AiStore.Allowed(s, Path.Combine(AiStore.Folder, "x.txt")));
    }

    [Fact]
    public void Mcp_timeline_keeps_the_dates_asked()
    {
        using var s = CaseSession.Open(_c.Report);
        var mcp = new McpServer(s, new AiStore(_db));
        var r = mcp.Handle("tools/call", JsonNode.Parse("""{"name":"timeline","arguments":{"from":"2024-05-01T10:00:30Z","to":"2024-05-01T10:01:30Z"}}"""));
        var text = (string)r["content"]![0]!["text"]!;
        Assert.Contains("[get_chat#2]", text);
        Assert.DoesNotContain("[get_chat#1]", text);
        Assert.DoesNotContain("[get_chat#3]", text);
    }

    [Theory]
    [InlineData("415 Unsupported Media Type: ", true)]
    [InlineData("200 OK: {\"error\":\"Unexpected endpoint or method. (POST /v1/audio/transcriptions)\"}", true)]
    [InlineData("404 Not Found: ", true)]
    [InlineData("400 Bad Request: response_format", false)]
    public void A_chat_server_without_speech_to_text_is_named_as_such(string error, bool none) =>
        Assert.Equal(none, AiClient.NoSpeechEndpoint(error));

    /// <summary>A stand-in whisper-cli (a batch file) that writes the JSON whisper.cpp writes for -oj -of.</summary>
    [Fact]
    public async Task Whisper_cpp_transcribes_a_copy_and_reads_text_and_language()
    {
        var dir = Directory.CreateTempSubdirectory("lexis-whisper-test-");
        try
        {
            var exe = Path.Combine(dir.FullName, "whisper-cli.cmd");
            File.WriteAllText(exe, "@echo off\r\n:loop\r\nif \"%~1\"==\"\" goto end\r\nif \"%~1\"==\"-of\" set OUT=%~2\r\nshift\r\ngoto loop\r\n:end\r\n" +
                                   "> \"%OUT%.json\" echo {\"result\":{\"language\":\"it\"},\"transcription\":[{\"text\":\" first \"},{\"text\":\"second\"}]}\r\n");
            Assert.Throws<AiException>(() => WhisperCpp.Models(exe)); // no model yet
            File.WriteAllBytes(Path.Combine(dir.FullName, "ggml-tiny.bin"), [0]);
            Assert.Equal("ggml-tiny.bin", Assert.Single(WhisperCpp.Models(exe)).Id);

            var client = new AiClient(new AiProvider("whisper.cpp", AiKind.WhisperCpp, exe), null);
            Assert.Equal("local", new AiProvider("whisper.cpp", AiKind.WhisperCpp, exe).Where);
            var (text, lang) = await client.Transcribe("ggml-tiny.bin", "voice.wav", [1, 2, 3]);
            Assert.Equal(("first second", "it"), (text, lang));
            await Assert.ThrowsAsync<AiException>(() => client.Chat("ggml-tiny.bin", "s", "u"));
        }
        finally { dir.Delete(true); }
    }
}
