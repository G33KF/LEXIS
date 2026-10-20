using Microsoft.Data.Sqlite;

namespace AleappReader.Core.Tests;

/// <summary>
/// Opens every real ALEAPP report found under ALEAP_RESULTS_FOLDER (outside git: case data stays local).
/// Passes vacuously when the folder is absent.
/// </summary>
public class RealCaseTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static IEnumerable<string> Manifests()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var dir = Path.Combine(d.FullName, "ALEAP_RESULTS_FOLDER");
            if (Directory.Exists(dir)) return Directory.EnumerateFiles(dir, "_lava_data.lava", SearchOption.AllDirectories);
        }
        return [];
    }

    [Fact]
    public void Every_artifact_of_every_real_case_is_queryable_and_counts_match()
    {
        foreach (var manifest in Manifests())
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(
                Path.Combine(Path.GetDirectoryName(manifest)!, "_lava_artifacts.db"))));
            using (var s = CaseSession.Open(manifest))
            using (var con = s.OpenDb())
            {
                Assert.NotEmpty(s.Manifest.Artifacts);
                foreach (var a in s.Manifest.Artifacts)
                {
                    var page = ArtifactQuery.Query(con, a, new PageRequest(0, 50));
                    Assert.True(page.Total == a.RecordCount, $"{a.Name}: manifest {a.RecordCount}, table {page.Total}");
                    Assert.Equal(a.Columns.Count, page.Columns.Count);
                    foreach (var row in page.Rows)
                        for (int i = 0; i < row.Values.Length; i++) ValueFormatter.Format(row.Values[i], page.Columns[i]);
                    if (a.Conversation is { } cv)
                        foreach (var col in new[] { cv.Discriminator, cv.Text, cv.Direction, cv.Time })
                            Assert.True(a.Column(col) is not null, $"{a.Name}: conversation column {col} not found");
                }
                var media = new MediaResolver(s).All(con);
                output.WriteLine($"{manifest}: {s.Manifest.Artifacts.Count} artifacts, {media.Count} media, " +
                    $"{GeoExtractor.GeoArtifacts(s.Manifest).Count()} geo artifacts, timeline {Timeline.Query(s, 0, 1).Total}");
                Assert.All(media, m => Assert.True(m.Exists, $"missing media {m.ExtractionPath}"));
                var geo = GeoExtractor.GeoArtifacts(s.Manifest).SelectMany(g => GeoExtractor.Extract(con, g)).ToList();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var zones = Zones.Find([geo], 200, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
                output.WriteLine($"  zones: {geo.Count} positions, {zones.Count} zones, {zones.Sum(z => z.Visits.Count)} visits, {clock.ElapsedMilliseconds} ms");
                Assert.Equal(geo.Count(p => p.Epoch is not null), zones.Sum(z => z.Positions));
                Assert.True(Timeline.Query(s, 0, 100).Total >= 0);
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(
                Path.Combine(Path.GetDirectoryName(manifest)!, "_lava_artifacts.db")))));
        }
    }

    /// <summary>The local model over one real chat (summary, sources asked again when lines lack them) and one real
    /// picture, on this machine only (LEXIS_AI_URL must be a local server; set LEXIS_AI_REAL=1). Reports measures
    /// only (lines, citations, format), never what the model wrote or what the records say.</summary>
    [Fact]
    public async Task Live_local_model_on_a_real_chat_and_picture()
    {
        if (Environment.GetEnvironmentVariable("LEXIS_AI_REAL") != "1" ||
            Environment.GetEnvironmentVariable("LEXIS_AI_URL") is not { Length: > 0 } url ||
            Environment.GetEnvironmentVariable("LEXIS_AI_MODEL") is not { Length: > 0 } model) return;
        var provider = new AiProvider("live", AiKind.OpenAi, url);
        Assert.NotEqual("remote", provider.Where); // real data never leaves the machine
        var client = new AiClient(provider, null);
        var report = new List<string>();
        var manifests = Manifests().Concat(new[] { "Ileap_GUI" }.SelectMany(n =>
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, n))) return Directory.EnumerateFiles(Path.Combine(d.FullName, n), "_lava_data.lava", SearchOption.AllDirectories);
            return [];
        }));
        foreach (var manifest in manifests)
        {
            using var s = CaseSession.Open(manifest);
            using var con = s.OpenDb();
            var tool = (manifest.Contains("iLEAPP") ? "iLEAPP " : "ALEAPP ") + Path.GetFileName(Path.GetDirectoryName(manifest));
            // The chat: the longest thread of 3–100 messages among the conversation artifacts (enough for several steps).
            var pick = s.Manifest.Artifacts.Where(a => a.Conversation is not null && a.RecordCount > 0)
                .SelectMany(a => ConversationBuilder.Build(con, s, a, 5000).Conversations.Where(c => c.Messages.Count is >= 3 and <= 100).Select(c => (a, c: (ConversationItem?)c)))
                .OrderByDescending(x => x.c!.Messages.Count).FirstOrDefault();
            if (pick.c is { } chat)
            {
                var sel = new AiSelection(pick.a, chat.Messages.Select(x => x.RowId).ToList());
                var sys = AiText.SystemIn("Italian");
                var context = await client.ContextOf(model);
                var t0 = DateTime.UtcNow;
                var x = await AiSteps.Run(s, client, model, sys, [sel], t => AiText.Summarize(t, "Italian"), rolling: true, max: 20, context, (_, _) => 0);
                var lines = x.Output.Split('\n').Count(l => l.Trim() != "");
                report.Add($"{tool} chat ({pick.a.Name}): {chat.Messages.Count} messages, {x.Sent.Count} sent in {x.Steps} steps of 20, {x.TooLong} too long; " +
                           $"summary {lines} lines, uncited {x.Uncited}, records cited {x.Known.Count}, not-sent citations {x.Unknown.Count}, {(DateTime.UtcNow - t0).TotalSeconds:0}s");
                var q = await AiSteps.Run(s, client, model, sys, [sel], t => AiText.Ask("Which bank account received a transfer?", t, "Italian"), rolling: false, max: 20, context, (_, _) => 0);
                report.Add($"{tool} unanswerable question in {q.Steps} steps: {(q.Output.Trim().TrimEnd('.').Equals("Not in the records", StringComparison.OrdinalIgnoreCase) ? "Not in the records" : $"answered ({q.Uncited} uncited lines, {q.Known.Count} records cited)")}");
            }
            else report.Add($"{tool}: no chat of 3–100 messages");
            // The picture: the first JPEG or PNG of the case.
            if (new MediaResolver(s).All(con).FirstOrDefault(m => m.Exists && (m.MimeType ?? "") is "image/jpeg" or "image/png") is { } pic)
            {
                const string cats = "weapons, drugs, identity documents, cash or payment cards, screenshots, vehicles, people, documents, other";
                try
                {
                    var r = await client.Chat(model, "You describe images for a forensic examiner. Only what is visible.", AiText.Vision(cats, "Italian"),
                        [new AiImage(pic.Name, pic.MimeType!, pic.ReadAllBytes())]);
                    var f = r.Trim().Split(';').Select(x => x.Trim()).ToArray();
                    report.Add($"{tool} picture ({pic.MimeType}, {pic.Size:N0} bytes): {r.Trim().Split('\n').Length} lines, {f.Length} fields, " +
                               $"category in list {cats.Split(", ").Contains(f[0].ToLowerInvariant())}, confidence {(f.Length > 1 && double.TryParse(f[1], System.Globalization.CultureInfo.InvariantCulture, out var c) && c is >= 0 and <= 1 ? "ok" : "not a number")}, reason {(f.Length > 2 ? f[2].Length : 0)} chars");
                }
                catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException) { report.Add($"{tool} picture: error {e.GetType().Name}"); }
            }
        }
        foreach (var l in report) output.WriteLine(l);
        if (Environment.GetEnvironmentVariable("LEXIS_AI_REPORT") is { Length: > 0 } path) File.WriteAllLines(path, report);
    }

    /// <summary>The built-in checks run on every real case without failing; prints counts per kind and artifact only,
    /// never a value.</summary>
    [Fact]
    public void Built_in_indicator_checks_run_on_real_cases()
    {
        foreach (var manifest in Manifests())
        {
            using var s = CaseSession.Open(manifest);
            var hits = SecurityPosture.Check(s)
                .Concat(RootTraces.Set().Scan(s))
                .Concat(IndicatorSet.Scan(s, (Func<string, string, IEnumerable<Indicator>>)CryptoAddresses.Match));
            if (s.Extraction is { } x) hits = hits.Concat(RootTraces.Set().ScanFiles(x.AllPaths()));
            foreach (var g in hits.GroupBy(h => (h.Indicator.Type, h.Where)).OrderBy(g => g.Key))
                output.WriteLine($"{g.Key.Type} in {g.Key.Where}: {g.Count()}");
        }
    }
}
