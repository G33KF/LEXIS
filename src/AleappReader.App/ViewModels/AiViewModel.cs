using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>What the AI is asked about: records (null ids: every record of the artifact) and media.</summary>
public sealed record AiScope(string Text, IReadOnlyList<AiSelection> Records, IReadOnlyList<MediaRef> Media);

/// <summary>The AI window of one case: summary, questions, translation and thematic review of records; image
/// classification and audio transcription; questions to the Claude CLI. Each answer is labelled and kept in the case's
/// AI base with its citations, each send in its log; nothing here writes to the evidence, the tags or the notes.</summary>
public partial class AiViewModel : ObservableObject
{
    static readonly string[] VisionMimes = ["image/jpeg", "image/png", "image/gif", "image/webp"];
    readonly MainViewModel _main;
    readonly CaseSession _s;
    readonly AiScope _scope;
    readonly AiStore _store;
    CancellationTokenSource? _cts;
    static AiSettings Ai => Settings.Current.Ai;

    public string ScopeText => _scope.Text;
    public string StoreText => "AI base and send log of this case: " + _store.Path;
    public bool HasRecords => _scope.Records.Count > 0;
    public IReadOnlyList<MediaRef> Images { get; }
    public IReadOnlyList<MediaRef> Audio { get; }
    public bool HasImages => Images.Count > 0;
    public bool HasAudio => Audio.Count > 0;
    public string ImagesText => $"Classify {Images.Count:N0} images";
    public string AudioText => $"Transcribe {Audio.Count:N0} audio files";
    public bool ClaudeAllowed => Ai.ClaudeAllowed;
    public string TextModel => Describe(Ai.Text);
    public string VisionModel => Describe(Ai.Vision);
    public string TranscriptionModel => Describe(Ai.Transcription);
    public IReadOnlyList<AiTheme> Themes => Ai.Themes;

    [ObservableProperty] private AiTheme? theme;
    [ObservableProperty] private string question = "";
    [ObservableProperty] private string language = Ai.TranslateTo;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Idle))] private bool busy;
    [ObservableProperty] private string status = "";
    public bool Idle => !Busy;

    public ObservableCollection<AiResult> Results { get; } = [];
    public ObservableCollection<AiSend> Sends { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelected), nameof(UnknownText))] private AiResult? selected;
    [ObservableProperty] private AiSend? selectedSend;
    public bool HasSelected => Selected is not null;
    public string EmptyText => Results.Count > 0 ? "Choose a result on the left." : "No AI results for this case yet. Each answer is kept here with its prompt and citations.";
    public bool HasNothing => !HasRecords && !HasImages && !HasAudio && !ClaudeAllowed;
    public string SkippedText => _scope.Media.Count(m => m.IsImage) - Images.Count is > 0 and var k
        ? $"{k:N0} images left out: not copied out of the extraction yet, or not JPEG, PNG, GIF or WebP." : "";
    public const string Caveat = "AI output · review aid, not a finding: check every cited record before relying on it.";
    public string UnknownText => Selected is { Unknown.Count: > 0 } r
        ? $"Cites {r.Unknown.Count} record(s) that were not sent to it, so not from the case as given: {string.Join(" ", r.Unknown)}" : "";

    public AiViewModel(MainViewModel main, CaseSession s, AiScope scope)
    {
        (_main, _s, _scope, _store) = (main, s, scope, AiStore.For(s));
        Images = scope.Media.Where(m => m.IsImage && m.Exists && VisionMimes.Contains(Mime(m))).ToList();
        Audio = scope.Media.Where(m => m.Kind == "audio" && m.Exists).ToList();
        theme = Themes.FirstOrDefault();
        Reload();
    }

    static string Mime(MediaRef m) => m.MimeType is { Length: > 0 } t ? t.ToLowerInvariant() : Path.GetExtension(m.FullPath ?? m.Name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "",
    };

    static string Describe(AiRole r) => Ai.ProviderOf(r) is { } p && r.Model != ""
        ? $"{r.Model} · {p.Name} · {p.Provider.WhereText}" : "not set (AI settings)";

    void Reload()
    {
        Results.Clear();
        foreach (var r in _store.Results()) Results.Add(r);
        OnPropertyChanged(nameof(EmptyText));
        Sends.Clear();
        foreach (var x in _store.Sends().Reverse()) Sends.Add(x);
    }

    /// <summary>The client and model of a job, or null with the reason in the status.</summary>
    (AiClient Client, AiProvider Provider, string Model)? Role(AiRole r, string job)
    {
        if (Ai.ProviderOf(r) is not { } p || r.Model == "") { Status = $"Choose a {job} model in AI settings."; return null; }
        return (p.Client(), p.Provider, r.Model);
    }

    /// <summary>A provider outside this machine and your network is named and confirmed before anything is sent.</summary>
    static bool Confirm(AiProvider p, string what) =>
        p.Where != "remote" || MessageBox.Show(Application.Current.MainWindow,
            $"{p.Name} ({p.Address}) is not on this machine or your network.\n\n{what} will be sent to it and logged.\n\nSend?",
            "AI · data leaves your network", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    async Task Run(Func<CancellationToken, Task> job)
    {
        if (!Ai.Enabled) { Status = "AI is off in AI settings."; return; }
        _cts = new CancellationTokenSource();
        Busy = true;
        try { await job(_cts.Token); }
        catch (OperationCanceledException) { Status = "Stopped. Finished batches are kept; running it again resumes."; }
        catch (Exception e) when (e is AiException or IOException or UnauthorizedAccessException or InvalidOperationException or UriFormatException
                                       or Microsoft.Data.Sqlite.SqliteException or System.ComponentModel.Win32Exception)
        {
            Status = "Failed: " + e.Message;
        }
        finally
        {
            Busy = false;
            _cts.Dispose();
            _cts = null;
            Reload();
            _main.RefreshAiViews();
        }
    }

    [RelayCommand] void Cancel() { _stopped = true; _cts?.Cancel(); }

    /// <summary>A complete analysis is running: its steps were confirmed together at its start.</summary>
    bool _started, _stopped;

    /// <summary>💀 The categories to analyse and the objective are chosen in a small window; the analysis then runs in an
    /// AI window of its own, over every record and media file of those categories.</summary>
    [RelayCommand]
    async Task CompleteAnalysis()
    {
        if (!Ai.Enabled) { Status = "AI is off in AI settings."; return; }
        if (Views.CompleteAnalysisWindow.Ask(_s, Question.Trim()) is not { } req) return;
        Status = "Reading the media of those categories…";
        var media = req.Describe || req.Transcribe ? await Task.Run(() => MediaOf(req.Artifacts)) : [];
        Status = "";
        var groups = string.Join(", ", req.Artifacts.Select(MacroCategories.Of).Distinct().Select(m => m.Name));
        var scope = new AiScope($"Complete analysis · {req.Artifacts.Count:N0} artifacts ({groups}) · objective: {req.Objective}",
                                [.. req.Artifacts.Select(a => new AiSelection(a))], media);
        if (_main.OpenAi(_s, scope) is { } vm) await vm.Analyze(req);
    }

    /// <summary>The files the records of these artifacts point to, once each.</summary>
    List<MediaRef> MediaOf(IEnumerable<ArtifactInfo> artifacts)
    {
        using var con = _s.OpenDb();
        var resolver = new MediaResolver(_s);
        var list = new List<MediaRef>();
        foreach (var a in artifacts.Where(a => a.Columns.Any(c => c.IsMedia)))
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, a);
                var idx = Enumerable.Range(0, cols.Count).Where(i => cols[i].IsMedia).ToList();
                foreach (var row in ArtifactQuery.All(con, a))
                    foreach (var i in idx) list.AddRange(resolver.Resolve(con, row.Values[i]));
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { } // table missing from a partial case
        return [.. list.DistinctBy(m => m.RefId)];
    }

    /// <summary>The complete analysis: what was asked first (translation, image description, transcription: each record's
    /// and file's own result shows in its details), then the records that bear on the objective picked batch by batch, then
    /// a cited report over them and the media analyses; or, when asked, Claude explores the case through LEXIS's tools.</summary>
    public async Task Analyze(Views.CompleteAnalysisRequest req)
    {
        Question = req.Objective;
        var r = req.Claude ? null : Role(Ai.Text, "text");
        if (!req.Claude && r is null) return;
        var records = await Task.Run(() => _scope.Records.Sum(x => (long)AiText.RowIds(_s, x).Count));
        var steps = new List<string>();
        if (req.Translate) steps.Add($"translate {records:N0} records into {Ai.TranslateTo}");
        if (req.Describe && HasImages) steps.Add($"describe {Images.Count:N0} pictures and read their text");
        if (req.Transcribe && HasAudio) steps.Add($"transcribe {Audio.Count:N0} audio files");
        steps.Add(req.Claude ? "Claude CLI explores the case and writes the report (leaves this machine)"
                             : $"read {records:N0} records in about {(records + Ai.MaxRecordsPerRequest - 1) / Math.Max(1, Ai.MaxRecordsPerRequest):N0} requests, keep those that bear on the objective, write the report");
        if (MessageBox.Show(Application.Current.MainWindow,
                $"Complete analysis of {ScopeText}\n\n{string.Join("\n", steps.Select((x, i) => $"{i + 1}. {x}"))}\n\n" +
                "It can take long. Stopped, it resumes where it left off. Start?",
                "AI · complete analysis", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _started = true;
        _stopped = false;
        try
        {
            if (req.Translate) { Language = Ai.TranslateTo; await Translate(); }
            if (!_stopped && req.Describe && HasImages) await DescribeImages();
            if (!_stopped && req.Transcribe && HasAudio) await Transcribe();
            if (_stopped) return;
            var web = req.Web ? await WebReference("The complete analysis", [WebLookup.Clean(req.Objective)]) : "";
            if (req.Claude)
            {
                await Run(ct => ClaudeExplore("Complete analysis", AiText.Analyze(req.Objective, "", Ai.AnswerLanguage, web).Split("\n\nRECORDS")[0] +
                    $"\n\nAnalyse the tables: {string.Join(", ", _scope.Records.Select(x => x.Artifact.TableName))}; read them all through the lexis tools.", ct));
                return;
            }
            await Run(async ct =>
            {
                var context = await r!.Value.Client.ContextOf(r.Value.Model, ct);
                var (hits, resumed, tooLong) = await Triage("Complete analysis: " + req.Objective,
                    "- anything that helps answer the objective: facts, people, accounts, places, times, amounts, plans, contradictions",
                    r.Value, context, await Batches(ct), ct);
                var picked = hits.Where(h => h.Known).Select(h => h.Cite.Trim('[', ']')).Select(c => (Table: c[..c.LastIndexOf('#')], Id: long.Parse(c[(c.LastIndexOf('#') + 1)..])))
                    .GroupBy(x => x.Table).Select(g => new AiSelection(_scope.Records.First(s => s.Artifact.TableName == g.Key).Artifact, [.. g.Select(x => x.Id)])).ToList();
                var (media, mediaKeys) = MediaAnalyses(AiText.Budget(context, 0) / 3);
                if (picked.Count == 0 && media == "")
                {
                    Status = "No record bears on the objective" + (tooLong > 0 ? $"; {tooLong:N0} records too long, not read" : "") + ".";
                    _store.Save(r.Value.Provider, r.Value.Model, "Complete analysis", "Objective: " + req.Objective, ScopeText, AiText.NotInRecords, [], [], 0);
                    return;
                }
                Status = $"{hits.Count(h => h.Known):N0} records bear on the objective: writing the report…";
                var system = AiText.SystemIn(Ai.AnswerLanguage);
                var x = await Task.Run(() => AiSteps.Run(_s, r.Value.Client, r.Value.Model, system, picked,
                    t => AiText.Analyze(req.Objective, t, Ai.AnswerLanguage, web, media), true, Math.Max(1, Ai.MaxRecordsPerRequest), context,
                    (step, text) => _store.LogSend(r.Value.Provider, r.Value.Model, "Complete analysis" + step, text),
                    st => Application.Current.Dispatcher.Invoke(() => Status = st), ct, alsoSent: mediaKeys), ct);
                var res = _store.Save(r.Value.Provider, r.Value.Model, "Complete analysis", "Objective: " + req.Objective,
                    $"{ScopeText} ({hits.Count(h => h.Known):N0} relevant records of {records:N0}" + (resumed > 0 ? $", {resumed:N0} batches resumed" : "") + ")",
                    x.Output, x.Known, x.Unknown, x.Steps == 1 ? x.SendId : 0);
                Status = (x.Uncited > 0 ? $"{x.Uncited:N0} lines have no source and are marked NO SOURCE. " : "") +
                         (tooLong > 0 ? $"{tooLong:N0} records too long were not read. " : "") + "Done. Review aid, not a finding.";
                Reload();
                Selected = Results.FirstOrDefault(y => y.Id == res.Id);
            });
        }
        finally { _started = false; }
    }

    /// <summary>What the AI already read in the scope's pictures and audio, newest of each, one line per file
    /// ("[media#id] (kind) text"), within <paramref name="budget"/> characters; and their keys.</summary>
    (string Text, List<string> Keys) MediaAnalyses(int budget)
    {
        var keys = _scope.Media.Select(m => AiText.Key("media", m.RefId)).ToHashSet();
        var sb = new StringBuilder();
        var used = new List<string>();
        foreach (var x in _store.MediaResults())
            foreach (var k in x.Cited.Where(keys.Contains))
            {
                if (!keys.Remove(k)) continue; // the newest of each file only
                var line = $"{k} ({x.Kind.Replace(" item", "")}) {x.Output.Replace(k, "").Replace('\n', ' ').Trim()}";
                if (sb.Length + line.Length > budget) return (sb.ToString(), used); // ponytail: newest first until full, no relevance pass
                sb.AppendLine(line);
                used.Add(k);
            }
        return (sb.ToString().TrimEnd(), used);
    }

    [RelayCommand]
    void OpenSettings()
    {
        _main.OpenAiSettingsCommand.Execute(null);
        OnPropertyChanged(string.Empty); // models, roles and the Claude section as now set
    }

    /// <summary>A request over the records in scope, answered with citations, in steps of at most the records set in AI
    /// settings (fewer when the model's context is smaller). <paramref name="rolling"/>: each step sends the answer so far
    /// with the next records and gets it back updated (summary, question); else each step is answered on its own and the
    /// parts are joined (translation).</summary>
    Task Ask(string kind, Func<string, string> prompt, bool rolling = true, int carry = 0, bool allColumns = false) => Run(async ct =>
    {
        if (Role(Ai.Text, "text") is not { } r) return;
        if (!Confirm(r.Provider, ScopeText)) return;
        Status = "Reading the records…";
        var system = AiText.SystemIn(Ai.AnswerLanguage);
        var context = await r.Client.ContextOf(r.Model, ct);
        var max = Math.Max(1, Ai.MaxRecordsPerRequest);
        var pending = _scope.Records.ToList();
        var total = await Task.Run(() => pending.Sum(x => (long)AiText.RowIds(_s, x).Count), ct);
        if (total == 0) { Status = "No records to send."; return; }
        var steps = (total + max - 1) / max;
        if (steps > 10 && !_started && MessageBox.Show(Application.Current.MainWindow,
                $"{total:N0} records at most {max:N0} per request: about {steps:N0} requests to {r.Model}. It can take long.\n\nStart?",
                "AI · " + kind, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var x = await Task.Run(() => AiSteps.Run(_s, r.Client, r.Model, system, pending, prompt, rolling, max, context,
            (step, text) => _store.LogSend(r.Provider, r.Model, kind + step, text),
            st => Application.Current.Dispatcher.Invoke(() => Status = st), ct, carry, allColumns), ct);
        if (x.Steps == 0) { Status = "No record fits one request of this model."; return; }
        var scope = ScopeText + (x.Steps > 1 ? $" ({x.Sent.Count:N0} records in {x.Steps} steps of at most {max})" : "") +
                    (x.TooLong > 0 ? $" ({x.TooLong:N0} records too long for the model, not sent)" : "");
        var res = _store.Save(r.Provider, r.Model, kind, AiText.PromptOf(x.LastUser), scope, x.Output, x.Known, x.Unknown, x.Steps == 1 ? x.SendId : 0);
        Status = (x.Uncited > 0 ? $"{x.Uncited:N0} lines have no source and are marked NO SOURCE. " : "") +
                 (x.TooLong > 0 ? $"{x.TooLong:N0} records too long for the model were not sent. " : "") + "Done.";
        Reload();
        Selected = Results.FirstOrDefault(x => x.Id == res.Id);
    });

    [RelayCommand] Task Summarize() => Ask("Summary", t => AiText.Summarize(t, Ai.AnswerLanguage));

    /// <summary>Every column of each record, technical ones included, explained; shown in each record's details. With the
    /// web lookup on, what the data format means is first searched online (format words only, shown and confirmed).</summary>
    [RelayCommand]
    async Task Explain()
    {
        var web = Ai.Enabled && (Ai.ExplainWebLookup || WebSearch) ? await WebReference("Explain", await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            return WebLookup.Queries(_scope.Records.Select(x => x.Artifact).Distinct()
                .Select(a => (a, ArtifactQuery.ExistingColumns(con, a).Where(c => !c.IsMedia).Select(c => c.Display))));
        })) : "";
        await Ask("Explanation", t => AiText.Explain(t, Ai.AnswerLanguage, web), rolling: false, allColumns: true);
    }

    /// <summary>Questions and Explain may search the web first (shown and confirmed each time).</summary>
    [ObservableProperty] private bool webSearch;

    static readonly System.Net.Http.HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 LEXIS" } } };

    /// <summary>Search results for <paramref name="queries"/>, after the examiner sees exactly what will be searched;
    /// empty when declined or nothing to search. Each search is logged with the sends.</summary>
    async Task<string> WebReference(string purpose, IReadOnlyList<string> queries)
    {
        if (queries.Count == 0) return "";
        var url = Ai.WebSearchUrl;
        if (MessageBox.Show(Application.Current.MainWindow,
                $"{purpose} will search the web ({new Uri(url.Replace("{q}", "")).Host}) for:\n\n{string.Join("\n", queries.Select(q => "• " + q))}\n\n" +
                "Only these words leave this machine (words with digits or @ are already dropped). The searches are logged.\n\n" +
                "Search? (No: go on without the web)",
                "AI · web search", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return "";
        Status = "Searching the web…";
        // ponytail: the send log takes a provider; the search engine is logged as one, its kind unused
        _store.LogSend(new AiProvider("Web search", AiKind.OpenAi, url), "", purpose + ": web search", string.Join("\n", queries));
        return await WebLookup.Reference(Web, url, queries, CancellationToken.None);
    }

    [RelayCommand]
    async Task AskQuestion()
    {
        if (Question.Trim() == "") { Status = "Write the question first."; return; }
        var q = Question.Trim();
        var web = Ai.Enabled && WebSearch ? await WebReference("The question", [WebLookup.Clean(q)]) : "";
        await Ask("Question", t => AiText.Ask(q, t, Ai.AnswerLanguage, web), rolling: false); // each part answers from its own records: no drift
    }

    [RelayCommand]
    Task Translate()
    {
        var lang = Language.Trim() is { Length: > 0 } l ? l : "English";
        // each step carries the last 10 messages of the one before with their translation: the thread goes on
        return Ask("Translation", t => AiText.Translate(lang, t), rolling: false, carry: 10);
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    /// <summary>The records in batches, each marked done once answered, so a stopped review resumes (a batch is known by
    /// its exact rows); a batch over the size limit is halved. Then one result with every candidate of this run's batches.</summary>
    [RelayCommand]
    Task ThematicReview() => Run(async ct =>
    {
        if (Theme is not { } t) { Status = "Add a theme in AI settings."; return; }
        if (Role(Ai.Text, "text") is not { } r) return;
        Status = "Reading the records…";
        var context = await r.Client.ContextOf(r.Model, ct);
        var batches = await Batches(ct);
        var records = batches.Sum(b => b.Ids.Length);
        if (records == 0) { Status = "No records to send."; return; }
        var what = $"{records:N0} records in {batches.Count:N0} requests to {r.Model} ({r.Provider.Name}, {r.Provider.WhereText})";
        if (r.Provider.Where == "remote" ? !Confirm(r.Provider, what)
            : batches.Count > 200 && MessageBox.Show(Application.Current.MainWindow, $"The thematic review will send {what}. It can take long; stopped, it resumes.\n\nStart?",
                  "AI · thematic review", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var (all, resumed, tooLong) = await Triage(t.Name, t.Indicators, r, context, batches, ct);
        var output2 = all.Count == 0 ? "No candidates." : string.Join("\n", all.Select(h => $"{h.Cite} {h.Reason}" + (h.Known ? "" : "   (NOT A RECORD SENT)")));
        if (tooLong > 0) output2 += $"\n\n{tooLong:N0} records too long for one request were not reviewed.";
        var res = _store.Save(r.Provider, r.Model, "Thematic review", $"Theme: {t.Name}\nIndicators:\n{t.Indicators}", $"{t.Name} · {ScopeText}", output2,
                              [.. all.Where(h => h.Known).Select(h => h.Cite)], [.. all.Where(h => !h.Known).Select(h => h.Cite)], 0);
        Status = $"{all.Count(h => h.Known):N0} candidates to check" + (resumed > 0 ? $" ({resumed:N0} batches resumed from an earlier run)" : "") +
                 (tooLong > 0 ? $"; {tooLong:N0} records too long, not reviewed" : "") + ". Review aid, not a finding.";
        Reload();
        Selected = Results.FirstOrDefault(x => x.Id == res.Id);
    });

    /// <summary>The records in scope in batches of at most the records per request set in AI settings.</summary>
    async Task<Queue<(ArtifactInfo Artifact, long[] Ids)>> Batches(CancellationToken ct)
    {
        var batch = Math.Max(1, Ai.MaxRecordsPerRequest);
        return new(await Task.Run(() => _scope.Records.SelectMany(x => AiText.RowIds(_s, x).Chunk(batch).Select(ids => (x.Artifact, ids))).ToList(), ct));
    }

    /// <summary>The records that match the indicators, batch by batch, each marked done once answered, so a stopped run
    /// resumes (a batch is known by its exact rows); a batch over the size limit is halved.</summary>
    async Task<(List<ThemeHit> Hits, int Resumed, int TooLong)> Triage(string name, string indicators,
        (AiClient Client, AiProvider Provider, string Model) r, int? context, Queue<(ArtifactInfo Artifact, long[] Ids)> batches, CancellationToken ct)
    {
        var prefix = $"theme|{Hash(name + "\n" + indicators)}|{r.Provider.Name}|{r.Model}|";
        var runKeys = new HashSet<string>();
        int n = 0, resumed = 0, tooLong = 0;
        while (batches.TryDequeue(out var b))
        {
            ct.ThrowIfCancellationRequested();
            var (a, ids) = b;
            var key = $"{prefix}{a.TableName}|{Hash(string.Join(",", ids))}";
            n++;
            if (_store.IsDone(key)) { resumed++; runKeys.Add(key); continue; }
            Status = $"Batch {n:N0} of {n + batches.Count:N0} · {a.Name}";
            var (text, keys, omitted) = await Task.Run(() => AiText.Records(_s, [new AiSelection(a, ids)],
                AiText.Budget(context, AiText.Theme(name, indicators, "", Ai.AnswerLanguage).Length + 2_000), ct), ct);
            if (omitted > 0)
            {
                if (ids.Length > 1) { batches.Enqueue((a, ids[..(ids.Length / 2)])); batches.Enqueue((a, ids[(ids.Length / 2)..])); }
                else tooLong++;
                continue;
            }
            runKeys.Add(key);
            if (keys.Count == 0) { _store.MarkDone(key); continue; }
            var user = AiText.Theme(name, indicators, text, Ai.AnswerLanguage);
            var sendId = _store.LogSend(r.Provider, r.Model, "Thematic review: " + name, user);
            var output = await r.Client.Chat(r.Model, AiText.SystemIn(Ai.AnswerLanguage), user, null, ct, context);
            var hits = AiText.ParseTheme(output, keys);
            _store.Save(r.Provider, r.Model, "Thematic review item", AiText.PromptOf(user), key, output,
                        [.. hits.Where(h => h.Known).Select(h => h.Cite).Distinct()], [.. hits.Where(h => !h.Known).Select(h => h.Cite).Distinct()], sendId);
            _store.MarkDone(key);
        }
        var items = _store.Results("Thematic review item").Where(x => runKeys.Contains(x.Scope)).ToList();
        return ([.. items.SelectMany(x => AiText.ParseTheme(x.Output, x.Cited.ToHashSet())).DistinctBy(h => h.Cite)], resumed, tooLong);
    }

    [RelayCommand] Task ClassifyImages() => Look("Image classification", AiText.Vision(Ai.VisionCategories, Ai.AnswerLanguage), Hash(Ai.VisionCategories), true);

    /// <summary>A description of each image and its text (OCR), as written and translated.</summary>
    [RelayCommand] Task DescribeImages() => Look("Image description", AiText.Describe(Ai.AnswerLanguage), "describe|" + Hash(Ai.AnswerLanguage), false);

    /// <summary>Each image to the vision model with <paramref name="prompt"/>, one item each (done items are skipped,
    /// so a stopped run resumes), then one result over them all.</summary>
    Task Look(string kind, string prompt, string tag, bool oneLine) => Run(async ct =>
    {
        if (Role(Ai.Vision, "vision") is not { } r) return;
        if (!Confirm(r.Provider, $"{Images.Count:N0} images")) return;
        var prefix = $"vision|{r.Provider.Name}|{r.Model}|{tag}|";
        var context = await r.Client.ContextOf(r.Model, ct);
        int n = 0;
        foreach (var m in Images)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var key = prefix + m.RefId;
            if (_store.IsDone(key)) continue;
            Status = $"Image {n:N0} of {Images.Count:N0}";
            var bytes = await Task.Run(m.ReadAllBytes, ct);
            if (bytes.Length > 20_000_000) { _store.MarkDone(key); continue; }
            var img = new AiImage(m.Name, Mime(m), bytes);
            var sendId = _store.LogSend(r.Provider, r.Model, kind, prompt + "\n" + img.Describe());
            var output = await r.Client.Chat(r.Model, "You describe images for a forensic examiner. Only what is visible.", prompt, [img], ct, context);
            var head = $"{AiText.Key("media", m.RefId)} {m.Name}";
            _store.Save(r.Provider, r.Model, kind + " item", prompt, key, oneLine ? $"{head}: {output.Replace('\n', ' ')}" : $"{head}\n{output.Trim()}",
                        [AiText.Key("media", m.RefId)], [], sendId);
            _store.MarkDone(key);
        }
        var refs = Images.Select(m => prefix + m.RefId).ToHashSet();
        var items = _store.Results(kind + " item").Where(x => refs.Contains(x.Scope)).OrderBy(x => x.Output).ToList();
        var res = _store.Save(r.Provider, r.Model, kind, prompt, $"{items.Count:N0} images · {ScopeText}",
                              string.Join(oneLine ? "\n" : "\n\n", items.Select(x => x.Output)), [.. items.SelectMany(x => x.Cited)], [], 0);
        Status = $"{items.Count:N0} images {(oneLine ? "classified" : "described")}. Review aid, not a finding.";
        Reload();
        Selected = Results.FirstOrDefault(x => x.Id == res.Id);
    });

    [RelayCommand]
    Task Transcribe() => Run(async ct =>
    {
        if (Role(Ai.Transcription, "transcription") is not { } r || r.Provider.Kind == AiKind.Ollama || r.Provider.Name == AiSettingsViewModel.LmStudioName)
        {
            // a chat server cannot transcribe, whatever model it has loaded: offer whisper.cpp instead
            var a = MessageBox.Show(Application.Current.MainWindow,
                (Ai.ProviderOf(Ai.Transcription) is null ? "Transcription needs a speech-to-text engine: none is chosen yet." : AiClient.NoTranscription) + "\n\n" +
                "Whisper (whisper.cpp) runs on this machine: download whisper-cli (the Windows zip of a release) and a ggml model " +
                "(e.g. ggml-small.bin), put them in one folder, then give whisper-cli.exe in AI settings › Transcription.\n\n" +
                "Yes: open AI settings.   No: open the whisper.cpp download page.",
                "Transcription", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (a == MessageBoxResult.Yes) _main.OpenAiSettingsCommand.Execute(null);
            else if (a == MessageBoxResult.No) Process.Start(new ProcessStartInfo(WhisperCpp.Downloads) { UseShellExecute = true });
            Status = "Transcription needs whisper.cpp or a speech-to-text server.";
            return;
        }
        if (!Confirm(r.Provider, $"{Audio.Count:N0} audio files")) return;
        int n = 0;
        foreach (var m in Audio)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var key = $"transcribe|{r.Provider.Name}|{r.Model}|{m.RefId}";
            if (_store.IsDone(key)) continue;
            Status = $"Audio {n:N0} of {Audio.Count:N0}";
            var bytes = await Task.Run(m.ReadAllBytes, ct);
            var sendId = _store.LogSend(r.Provider, r.Model, "Transcription",
                $"audio {m.Name} ({bytes.Length:N0} bytes, sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()})");
            var (text, lang) = await r.Client.Transcribe(r.Model, m.Name, bytes, ct);
            _store.Save(r.Provider, r.Model, "Transcription", "Speech to text", $"{m.Name} {AiText.Key("media", m.RefId)}",
                        (text == "" ? "(no speech recognised)" : text) + (lang is null ? "" : $"\n\nLanguage: {lang}"), [AiText.Key("media", m.RefId)], [], sendId);
            _store.MarkDone(key);
        }
        Status = "Done.";
    });

    /// <summary>Claude CLI, only when allowed in settings and confirmed now: it reads the case through LEXIS's read-only
    /// tools, and what it reads goes to Anthropic. Citations count as known when a tool answer of this run held them.</summary>
    [RelayCommand]
    Task AskClaude()
    {
        if (Question.Trim() == "") { Status = "Write the question first."; return Task.CompletedTask; }
        return Run(ct => ClaudeExplore("Claude CLI", "Question: " + Question.Trim(), ct));
    }

    async Task ClaudeExplore(string kind, string task, CancellationToken ct)
    {
        if (!Ai.ClaudeAllowed) { Status = "The Claude CLI is not allowed in AI settings."; return; }
        if (Environment.ProcessPath is not { } self) { Status = "Cannot find this program's path for the MCP server."; return; }
        var claude = new AiProvider("Claude CLI", AiKind.ClaudeCli, Ai.ClaudePath);
        var device = _main.DeviceOf(_s)?.Label;
        if (MessageBox.Show(Application.Current.MainWindow,
                $"Claude CLI will read {(device is null ? "this case" : device)} through LEXIS's read-only tools: search, records, tables, " +
                "timeline and the media list (not the media files). It can read any record of the case, not only the selection.\n\n" +
                "Everything it reads is sent to Anthropic and leaves this machine. Every tool answer is logged in the case's AI log.\n\nRun it now?",
                "Claude CLI · data leaves this machine", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var prompt = AiText.SystemIn(Ai.AnswerLanguage) + "\n\nThe case is reached only through the lexis tools." +
                     (HasRecords ? $"\nThe examiner is looking at: {ScopeText} (tables {string.Join(", ", _scope.Records.Select(x => x.Artifact.TableName))})." : "") +
                     "\n\n" + task;
        var run = Guid.NewGuid().ToString("N");
        var sendId = _store.LogSend(claude, "claude", $"Claude CLI prompt {run}", prompt);
        Status = "Claude is reading the case…";
        var output = await ClaudeCli.Run(Ai.ClaudePath, prompt, self, ["--mcp", _s.ManifestPath, "--ai-db", _store.Path, "--run", run], ct);
        var read = string.Join("\n", _store.Sends().Where(x => x.Purpose.StartsWith(McpServer.Purpose(run))).Select(x => x.Sent));
        var (_, cites) = AiText.Check(output, new HashSet<string>());
        output = AiText.MarkUncited(output, cites.Where(read.Contains).ToHashSet());
        var res = _store.Save(claude, "claude", kind, AiText.PromptOf(task), ScopeText, output,
                              [.. cites.Where(read.Contains)], [.. cites.Where(c => !read.Contains(c))], sendId);
        Status = "Done.";
        Reload();
        Selected = Results.FirstOrDefault(x => x.Id == res.Id);
    }

    /// <summary>Opens a cited record (or shows a cited media file in the details).</summary>
    [RelayCommand]
    void OpenCite(string? cite)
    {
        if (cite is null || cite.Length < 4) return;
        var body = cite.Trim('[', ']');
        var i = body.LastIndexOf('#');
        if (i <= 0) return;
        var (table, row) = (body[..i], body[(i + 1)..]);
        if (table == "media")
        {
            if (_scope.Media.FirstOrDefault(m => m.RefId == row) is { } m) _main.Details.ShowMedia(m);
            return;
        }
        if (_s.Manifest.Artifacts.FirstOrDefault(a => a.TableName == table) is { } art && long.TryParse(row, out var id))
            _main.NavigateTo(art, selectRowId: id);
    }

    [RelayCommand]
    void CopyResult()
    {
        if (Selected is not { } r) return;
        Clipboard.SetText($"{r.Label}\n{Caveat}\nPrompt: {r.Prompt}\nScope: {r.Scope}\n\n{r.Output}" +
                          (r.Unknown.Count > 0 ? $"\n\nCitations not among the records sent: {string.Join(" ", r.Unknown)}" : ""));
    }
}
