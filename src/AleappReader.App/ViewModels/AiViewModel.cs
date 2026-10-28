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
/// <summary><paramref name="About"/>: what the records are, from the case data (a chat's name, app, account, participants).</summary>
/// <summary>What the AI is given; <paramref name="Keys"/>: the item the data shown is about ([table#row], [media#id]), so its
/// explanation lands in that item's AI Analysis.</summary>
public sealed record AiScope(string Text, IReadOnlyList<AiSelection> Records, IReadOnlyList<MediaRef> Media, string About = "",
                             IReadOnlyList<string>? Keys = null);

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
    public bool CanTranslate => _scope.Records.Any(x => MacroCategories.Written(x.Artifact));
    public bool CanSummarize => MacroCategories.Summarizable([.. _scope.Records.Select(x => x.Artifact)]);
    public IReadOnlyList<MediaRef> Images { get; }
    public IReadOnlyList<MediaRef> Audio { get; }
    public bool HasImages => Images.Count > 0;
    public bool HasAudio => Audio.Count > 0;
    public IReadOnlyList<MediaRef> Videos { get; }
    public bool HasVideos => Videos.Count > 0;
    public string VideosText => $"Analyze {Videos.Count:N0} video{(Videos.Count == 1 ? "" : "s")}: frames and audio";
    public string VideoModel => Describe(Ai.VideoRole);
    public string ImagesText => $"Classify {Images.Count:N0} images";
    public string AudioText => $"Transcribe {Audio.Count:N0} audio files";
    public bool ClaudeAllowed => Ai.ClaudeExplores;
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
    const string AllKinds = "All results";
    /// <summary>The kinds of result in the AI base (summary, explanation, question…), to show one only.</summary>
    public ObservableCollection<string> Kinds { get; } = [AllKinds];
    [ObservableProperty] private string kindFilter = AllKinds;
    partial void OnKindFilterChanged(string value) => Reload();
    public ObservableCollection<AiSend> Sends { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelected), nameof(UnknownText))] private AiResult? selected;
    [ObservableProperty] private AiSend? selectedSend;
    public bool HasSelected => Selected is not null;
    public string EmptyText => Results.Count > 0 ? "Choose a result on the left." : "No AI results for this case yet. Each answer is kept here with its prompt and citations.";
    public bool HasNothing => !HasRecords && !HasImages && !HasAudio && !HasVideos && !ClaudeAllowed && !HasData;
    /// <summary>Data shown without records behind it (a field of the details, a file of the extraction), to explain.</summary>
    public bool HasData => _scope.About != "" && !HasRecords;
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
        Videos = scope.Media.Where(m => m.Kind == "video" && m.Exists).ToList();
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
        var all = _store.Results();
        foreach (var k in all.Select(r => r.Kind).Distinct().Order()) if (!Kinds.Contains(k)) Kinds.Add(k);
        foreach (var r in all.Where(r => KindFilter == AllKinds || r.Kind == KindFilter)) Results.Add(r);
        OnPropertyChanged(nameof(EmptyText));
        Sends.Clear();
        foreach (var x in _store.Sends().Reverse()) Sends.Add(x);
    }

    /// <summary>A new result selected; the kind filter goes back to all when it would hide it.</summary>
    void Show(AiResult res)
    {
        if (KindFilter != AllKinds && res.Kind != KindFilter) KindFilter = AllKinds;
        Reload();
        Selected = Results.FirstOrDefault(x => x.Id == res.Id);
    }

    /// <summary>The client and model of a job, or null with the reason in the status.</summary>
    (AiClient Client, AiProvider Provider, string Model)? Role(AiRole r, string job)
    {
        if (Ai.ProviderOf(r) is not { } p || r.Model == "") { Status = $"Choose a {job} model in AI settings."; return null; }
        return (p.Client(), p.Provider, r.Model);
    }

    /// <summary>A provider outside this machine and your network is named and confirmed before anything is sent.</summary>
    internal static bool Confirm(AiProvider p, string what) =>
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
    /// <summary>Why the complete analysis went on without the audio, written into its result.</summary>
    string? _audioLeftOut;

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
        // audio no engine here can transcribe: said now, and the analysis goes on without it only if the examiner agrees
        var audioWhy = _audioLeftOut = req.Transcribe && HasAudio ? AudioProblem() : null;
        if (audioWhy is not null && !SkipAudio(audioWhy)) return;
        var transcribe = req.Transcribe && HasAudio && audioWhy is null;
        if (transcribe) steps.Add($"transcribe {Audio.Count:N0} audio files");
        else if (audioWhy is not null) steps.Add($"{Audio.Count:N0} audio files left out, not processed: {audioWhy}");
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
            if (!_stopped && transcribe) await Transcribe();
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
                    $"{ScopeText} ({hits.Count(h => h.Known):N0} relevant records of {records:N0}" + (resumed > 0 ? $", {resumed:N0} batches resumed" : "") + ")" +
                    (_audioLeftOut is { } why ? $"; {Audio.Count:N0} audio files not transcribed: {why}" : ""),
                    x.Output, x.Known, x.Unknown, x.Steps == 1 ? x.SendId : 0);
                Status = (x.Uncited > 0 ? $"{x.Uncited:N0} lines have no source and are marked NO SOURCE. " : "") +
                         (tooLong > 0 ? $"{tooLong:N0} records too long were not read. " : "") + "Done. Review aid, not a finding.";
                Show(res);
            });
        }
        finally { _started = false; }
    }

    /// <summary>What the AI already read in the scope's pictures and audio, newest of each, one line per file
    /// ("[media#id] (kind) text"), within <paramref name="budget"/> characters; and their keys.</summary>
    (string Text, List<string> Keys) MediaAnalyses(int budget)
    {
        var sb = new StringBuilder();
        var used = new List<string>();
        foreach (var (k, said) in MediaRead())
        {
            var line = $"{k} {said}";
            if (sb.Length + line.Length > budget) break; // ponytail: newest first until full, no relevance pass
            sb.AppendLine(line);
            used.Add(k);
        }
        return (sb.ToString().TrimEnd(), used);
    }

    /// <summary>By [media#id], newest first, what the AI already read in each of the scope's files: "(kind) text".</summary>
    List<(string Key, string Said)> MediaRead()
    {
        var keys = _scope.Media.Select(m => AiText.Key("media", m.RefId)).ToHashSet();
        var list = new List<(string, string)>();
        foreach (var x in _store.MediaResults())
            foreach (var k in x.Cited.Where(keys.Contains))
                if (keys.Remove(k)) // the newest of each file only
                    list.Add((k, $"({x.Kind.Replace(" item", "")}) {x.Output.Replace(k, "").Replace('\n', ' ').Trim()}"));
        return list;
    }

    /// <summary>By record key, what the AI read in the files the record points to: it goes with the record as one more field.</summary>
    Dictionary<string, string> AttachmentNotes(IReadOnlyDictionary<string, string> read)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (read.Count == 0) return notes;
        using var con = _s.OpenDb();
        var resolver = new MediaResolver(_s);
        foreach (var sel in _scope.Records.Where(x => x.Artifact.Columns.Any(c => c.IsMedia)))
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, sel.Artifact);
                var idx = Enumerable.Range(0, cols.Count).Where(i => cols[i].IsMedia).ToList();
                foreach (var row in ArtifactQuery.All(con, sel.Artifact, sel.Filter, sel.Ids, sel.ColumnFilters))
                    if (idx.SelectMany(i => resolver.Resolve(con, row.Values[i])).Select(m => read.GetValueOrDefault(AiText.Key("media", m.RefId)))
                           .OfType<string>().Distinct().ToList() is { Count: > 0 } said)
                        notes[AiText.Key(sel.Artifact.TableName, row.RowId)] = string.Join(" | ", said);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { } // table missing from a partial case
        return notes;
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
    Task Ask(string kind, Func<string, string> prompt, bool rolling = true, int carry = 0, bool allColumns = false,
             IReadOnlyDictionary<string, string>? notes = null, string head = "") => Run(async ct =>
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
            st => Application.Current.Dispatcher.Invoke(() => Status = st), ct, carry, allColumns, parallel: Ai.ParallelRequests, notes: notes), ct);
        if (x.Steps == 0) { Status = "No record fits one request of this model."; return; }
        var scope = ScopeText + (x.Steps > 1 ? $" ({x.Sent.Count:N0} records in {x.Steps} steps of at most {max})" : "") +
                    (x.TooLong > 0 ? $" ({x.TooLong:N0} records too long for the model, not sent)" : "");
        var res = _store.Save(r.Provider, r.Model, kind, AiText.PromptOf(x.LastUser), scope, head == "" ? x.Output : head + "\n\n" + x.Output,
                              // one record sent: the answer is about it, cited or not, and shows in its details
                              x.Known.Count == 0 && x.Sent.Count == 1 ? [.. x.Sent] : x.Known, x.Unknown, x.Steps == 1 ? x.SendId : 0);
        Status = (x.Uncited > 0 ? $"{x.Uncited:N0} lines have no source and are marked NO SOURCE. " : "") +
                 (x.TooLong > 0 ? $"{x.TooLong:N0} records too long for the model were not sent. " : "") + "Done.";
        Show(res);
    });

    /// <summary>A summary of the records with what their pictures and audio show: files the AI has not read yet are read
    /// first when the examiner agrees and a model is set for them; each record then carries what was read in its files.</summary>
    [RelayCommand]
    async Task Summarize()
    {
        if (!Ai.Enabled) { Status = "AI is off in AI settings."; return; }
        var read = MediaRead().ToDictionary(x => x.Key, x => x.Said);
        bool Unread(MediaRef m) => !read.ContainsKey(AiText.Key("media", m.RefId));
        var images = Ai.Ready(Ai.Vision) ? Images.Count(Unread) : 0;
        var audio = AudioProblem() is null ? Audio.Count(Unread) : 0;
        if (images + audio > 0)
        {
            var what = string.Join(" and ", new[] { images > 0 ? $"{images:N0} pictures" : "", audio > 0 ? $"{audio:N0} audio files" : "" }.Where(w => w != ""));
            var a = MessageBox.Show(Application.Current.MainWindow,
                $"{what} of these records have not been read by the AI yet.\n\nRead them first, so the summary tells what they show or say?\n\n" +
                "Yes: read them, then summarise.   No: summarise the text and the files already read.",
                "AI · summary", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (a == MessageBoxResult.Cancel) return;
            if (a == MessageBoxResult.Yes)
            {
                _stopped = false;
                if (images > 0) await DescribeImages();
                if (!_stopped && audio > 0) await Transcribe();
                if (_stopped) return;
                read = MediaRead().ToDictionary(x => x.Key, x => x.Said);
            }
        }
        var notes = await Task.Run(() => AttachmentNotes(read));
        await Ask("Summary", t => AiText.Summarize(t, Ai.AnswerLanguage, _scope.About), notes: notes,
                  head: _scope.About == "" ? "" : "From the case data (not the AI):\n" + _scope.About);
    }

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

    /// <summary>Data shown that is not a record (the scope's <see cref="AiScope.About"/>: a file of the extraction, a
    /// field with its item) explained in one request. Nothing to cite: the result says what was sent.</summary>
    [RelayCommand]
    Task ExplainData() => Run(async ct =>
    {
        if (_scope.About == "") { Status = "Nothing to explain."; return; }
        if (Role(Ai.Text, "text") is not { } r) return;
        if (!Confirm(r.Provider, ScopeText)) return;
        var prompt = AiText.ExplainData(_scope.About, Ai.AnswerLanguage);
        var context = await r.Client.ContextOf(r.Model, ct);
        var id = _store.LogSend(r.Provider, r.Model, "Explanation", prompt);
        Status = $"Waiting for {r.Model}…";
        var output = await r.Client.Chat(r.Model, AiText.SystemIn(Ai.AnswerLanguage), prompt, null, ct, context);
        var res = _store.Save(r.Provider, r.Model, "Explanation", AiText.PromptOf(prompt), ScopeText, output, _scope.Keys ?? [], [], id);
        Status = "Done. Review aid, not a finding.";
        Show(res);
    });

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

    /// <summary>Each item through <paramref name="job"/>, at most the parallel requests of AI settings at once. Started on
    /// the UI thread, every job comes back to it after each await: only the requests overlap, the store, the status and
    /// the lists are touched by one thread. The first failure stops the others and is the one thrown.</summary>
    static async Task Each<T>(IEnumerable<T> items, CancellationToken ct, Func<T, CancellationToken, Task> job)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(Math.Max(1, Ai.ParallelRequests));
        var all = Task.WhenAll(items.Select(async x =>
        {
            await gate.WaitAsync(stop.Token);
            try { await job(x, stop.Token); }
            catch { stop.Cancel(); throw; }
            finally { gate.Release(); }
        }).ToList());
        try { await all; }
        catch when (all.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } first)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first); }
    }

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
        Show(res);
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
        int n = 0, resumed = 0, tooLong = 0, total = batches.Count;
        // in rounds: a batch too long comes back as two halves for the next round
        while (batches.Count > 0)
        {
            var round = batches.ToList();
            batches.Clear();
            await Each(round, ct, async (b, ct) =>
            {
                var (a, ids) = b;
                var key = $"{prefix}{a.TableName}|{Hash(string.Join(",", ids))}";
                n++;
                if (_store.IsDone(key)) { resumed++; runKeys.Add(key); return; }
                Status = $"Batch {n:N0} of {total:N0} · {a.Name}";
                var (text, keys, omitted) = await Task.Run(() => AiText.Records(_s, [new AiSelection(a, ids)],
                    AiText.Budget(context, AiText.Theme(name, indicators, "", Ai.AnswerLanguage).Length + 2_000), ct), ct);
                if (omitted > 0)
                {
                    if (ids.Length > 1) { batches.Enqueue((a, ids[..(ids.Length / 2)])); batches.Enqueue((a, ids[(ids.Length / 2)..])); total++; }
                    else tooLong++;
                    return;
                }
                runKeys.Add(key);
                if (keys.Count == 0) { _store.MarkDone(key); return; }
                var user = AiText.Theme(name, indicators, text, Ai.AnswerLanguage);
                var sendId = _store.LogSend(r.Provider, r.Model, "Thematic review: " + name, user);
                var output = await r.Client.Chat(r.Model, AiText.SystemIn(Ai.AnswerLanguage), user, null, ct, context);
                var hits = AiText.ParseTheme(output, keys);
                _store.Save(r.Provider, r.Model, "Thematic review item", AiText.PromptOf(user), key, output,
                            [.. hits.Where(h => h.Known).Select(h => h.Cite).Distinct()], [.. hits.Where(h => !h.Known).Select(h => h.Cite).Distinct()], sendId);
                _store.MarkDone(key);
            });
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
        await Each(Images, ct, async (m, ct) =>
        {
            n++;
            var key = prefix + m.RefId;
            if (_store.IsDone(key)) return;
            Status = $"Image {n:N0} of {Images.Count:N0}";
            var bytes = await Task.Run(m.ReadAllBytes, ct);
            if (bytes.Length > 20_000_000) { _store.MarkDone(key); return; }
            var img = new AiImage(m.Name, Mime(m), bytes);
            var sendId = _store.LogSend(r.Provider, r.Model, kind, prompt + "\n" + img.Describe());
            var output = await r.Client.Chat(r.Model, "You describe images for a forensic examiner. Only what is visible.", prompt, [img], ct, context);
            var head = $"{AiText.Key("media", m.RefId)} {m.Name}";
            _store.Save(r.Provider, r.Model, kind + " item", prompt, key, oneLine ? $"{head}: {output.Replace('\n', ' ')}" : $"{head}\n{output.Trim()}",
                        [AiText.Key("media", m.RefId)], [], sendId);
            _store.MarkDone(key);
        });
        var refs = Images.Select(m => prefix + m.RefId).ToHashSet();
        var items = _store.Results(kind + " item").Where(x => refs.Contains(x.Scope)).OrderBy(x => x.Output).ToList();
        var res = _store.Save(r.Provider, r.Model, kind, prompt, $"{items.Count:N0} images · {ScopeText}",
                              string.Join(oneLine ? "\n" : "\n\n", items.Select(x => x.Output)), [.. items.SelectMany(x => x.Cited)], [], 0);
        Status = $"{items.Count:N0} images {(oneLine ? "classified" : "described")}. Review aid, not a finding.";
        Show(res);
    });

    static readonly TimeSpan MaxVideoAudio = TimeSpan.FromHours(1); // ponytail: one request per video; chunk the audio if longer videos matter

    /// <summary>Each video read by LEXIS's ffmpeg: frames spaced as set in AI settings, each described by the video model,
    /// and its audio track transcribed; one result per video, a timeline by time, with the frames sent and their SHA-256.
    /// The frames and the audio are written to a temporary folder, deleted after; the video is only read.</summary>
    [RelayCommand]
    Task AnalyseVideos() => Run(async ct =>
    {
        if (Role(Ai.VideoRole, "vision (or video)") is not { } r) return;
        if (WhisperCpp.BundledFfmpeg is not { } ff) { Status = "LEXIS's ffmpeg could not be written to this computer."; return; }
        var speech = Ai.VideoTranscribe && AudioProblem() is null ? Role(Ai.Transcription, "transcription") : null;
        if (!Confirm(r.Provider, $"Frames of {Videos.Count:N0} videos")) return;
        if (speech is { } sp && sp.Provider.Where == "remote" && !Confirm(sp.Provider, $"The audio of {Videos.Count:N0} videos")) speech = null;
        var context = await r.Client.ContextOf(r.Model, ct);
        var every = Ai.VideoByPercent ? $"every {Ai.VideoPercent} %" : $"every {Ai.VideoSeconds} s";
        int n = 0, done = 0;
        AiResult? last = null;
        foreach (var m in Videos)
        {
            n++;
            var at = $"Video {n:N0} of {Videos.Count:N0}";
            var work = Directory.CreateTempSubdirectory("lexis-video-");
            try
            {
                Status = at + ": reading it";
                var file = await Task.Run(m.LocalFile, ct);
                // "Duration: N/A" (a recording never finalised, much WebM): one frame at its start, and its audio
                var d = await VideoFrames.DurationOf(ff, file, ct) ?? TimeSpan.Zero;
                var (times, capped) = VideoFrames.Times(d, Ai.VideoByPercent ? Ai.VideoPercent : null, Ai.VideoSeconds, Ai.VideoMaxFrames);
                var frames = await VideoFrames.Extract(ff, file, times, work.FullName, ct);
                if (frames.Count == 0 && speech is null) { Status = $"{m.Name}: ffmpeg could not take a frame from it."; continue; } // with audio: transcribed alone
                var seen = new List<(TimeSpan, string)>();
                var sent = new List<string>();
                long sendId = 0;
                foreach (var (t, jpg) in frames)
                {
                    Status = $"{at}: frame {seen.Count + 1} of {frames.Count}";
                    var bytes = await File.ReadAllBytesAsync(jpg, ct);
                    var img = new AiImage($"{m.Name} at {VideoFrames.Clock(t)}", "image/jpeg", bytes);
                    var prompt = VideoFrames.Prompt(t, Ai.AnswerLanguage);
                    sendId = _store.LogSend(r.Provider, r.Model, "Video frame", prompt + "\n" + img.Describe());
                    seen.Add((t, await r.Client.Chat(r.Model, "You describe video frames for a forensic examiner. Only what is visible.", prompt, [img], ct, context)));
                    sent.Add($"{VideoFrames.Clock(t)} sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
                }
                IReadOnlyList<(TimeSpan, string)> said = [];
                var heard = "";
                if (speech is not null && d > MaxVideoAudio)
                    heard = $"\n\nAudio not transcribed: the video is longer than {MaxVideoAudio.TotalMinutes:N0} minutes; transcribe its audio on its own.";
                else if (speech is { } s)
                {
                    Status = at + ": transcribing its audio";
                    try
                    {
                        var wav = Path.Combine(work.FullName, "audio.wav");
                        await WhisperCpp.ToWav(ff, file, wav, ct);
                        var bytes = await File.ReadAllBytesAsync(wav, ct);
                        _store.LogSend(s.Provider, s.Model, "Transcription",
                            $"audio track of {m.Name} as WAV ({bytes.Length:N0} bytes, sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()})");
                        var tr = await s.Client.Transcribe(s.Model, "audio.wav", bytes, ct);
                        said = tr.Segments.Count > 0 ? tr.Segments : tr.Text == "" ? [] : [(TimeSpan.Zero, tr.Text)];
                        heard = tr.Text == "" ? "\n\nAudio: no speech recognised." : tr.Language is { } l ? $"\n\nLanguage of the speech: {l}" : "";
                    }
                    catch (AiException e) { heard = $"\n\nAudio not transcribed: {e.Message}"; }
                }
                var key = AiText.Key("media", m.RefId);
                var output = $"{key} {m.Name}\n{VideoFrames.Timeline(seen, said)}{heard}\n\n" +
                             $"Frames sent ({frames.Count} of {times.Count}{(capped ? $", at most {Ai.VideoMaxFrames}, picked evenly" : "")}):\n{string.Join("\n", sent)}";
                last = _store.Save(r.Provider, r.Model, "Video analysis", VideoFrames.Prompt(frames.Count > 0 ? frames[0].At : TimeSpan.Zero, Ai.AnswerLanguage),
                                   $"{m.Name} · {frames.Count} frames {every} · {VideoFrames.Clock(d)}", output, [key], [], frames.Count == 1 ? sendId : 0);
                done++;
            }
            finally
            {
                // the frames and the audio are copies of evidence: retried, ffmpeg or an antivirus may still hold them a moment
                for (var i = 0; i < 5 && work.Exists; i++)
                {
                    try { work.Delete(true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(200, CancellationToken.None); }
                    work.Refresh();
                }
            }
        }
        if (last is null) return;
        Status = $"{done:N0} of {n:N0} videos analyzed. Review aid, not a finding.";
        Show(last);
    });

    /// <summary>Why the audio cannot be transcribed with the engine chosen for it, or null when it can be tried.</summary>
    static string? AudioProblem()
    {
        var (p, model) = (Ai.ProviderOf(Ai.Transcription), Ai.Transcription.Model);
        if (Ai.Transcription.Provider == AiSettings.WhisperName)
            return p is null ? "Whisper (whisper.cpp) is not set up: AI settings › Engines › whisper.cpp › Set up."
                 : !File.Exists(p.Address) ? $"Whisper (whisper.cpp) is not configured: {p.Address} is not there."
                 : model == "" ? "Whisper (whisper.cpp) has no model chosen in AI settings." : null;
        if (p is null || model == "") return "No engine is chosen for audio in AI settings.";
        return p.Kind is AiKind.Ollama or AiKind.ClaudeCli || p.Name == AiSettingsViewModel.LmStudioName
            ? $"The model chosen for audio ({model} on {p.Name}) does not process audio: it runs chat models only." : null;
    }

    /// <summary>Asked when the audio cannot be transcribed: go on without it, or stop.</summary>
    bool SkipAudio(string why) => MessageBox.Show(Application.Current.MainWindow,
        $"{why}\n\nContinue without the {Audio.Count:N0} audio files? They will not be processed.",
        "AI · audio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    [RelayCommand]
    Task Transcribe() => Run(async ct =>
    {
        var why = AudioProblem();
        if ((why is null ? Role(Ai.Transcription, "transcription") : null) is not { } r)
        {
            why ??= "No engine is chosen for audio in AI settings.";
            if (_started) { if (!SkipAudio(why)) _stopped = true; _audioLeftOut = why; Status = $"{Audio.Count:N0} audio files not transcribed: {why}"; return; }
            var a = MessageBox.Show(Application.Current.MainWindow,
                why + "\n\nWhisper (whisper.cpp) runs on this machine: set it up under AI settings › Engines › whisper.cpp.\n\n" +
                "Yes: open AI settings.   No: open the whisper.cpp download page.",
                "Transcription", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (a == MessageBoxResult.Yes) _main.OpenAiSettingsCommand.Execute(null);
            else if (a == MessageBoxResult.No) Process.Start(new ProcessStartInfo(WhisperCpp.Downloads) { UseShellExecute = true });
            Status = "Transcription: " + why;
            return;
        }
        if (!Confirm(r.Provider, $"{Audio.Count:N0} audio files")) return;
        var folders = SilkAudio.Folders(_s);
        int n = 0;
        string? noTranscription = null;
        try
        {
            await Each(Audio, ct, async (m, ct) =>
            {
                n++;
                var key = $"transcribe|{r.Provider.Name}|{r.Model}|{m.RefId}";
                if (_store.IsDone(key)) return;
                Status = $"Audio {n:N0} of {Audio.Count:N0}";
                // SILK (WeChat, QQ) goes as the WAV kept with the case: no engine reads SILK itself
                var (bytes, name, silk) = await Task.Run(() => SilkAudio.ForTranscription(m, m.AudioFolders ?? folders), ct);
                var sendId = _store.LogSend(r.Provider, r.Model, "Transcription",
                    $"audio {name} ({bytes.Length:N0} bytes, sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()})" +
                    (silk is null ? "" : $", decoded from SILK {m.Name} (sha256 {silk}) by {SilkAudio.Decoder}"));
                string text; string? lang;
                try { (text, lang) = await r.Client.Transcribe(r.Model, name, bytes, ct); }
                catch (AiException e) when (_started && e.Message == AiClient.NoTranscription)
                {
                    noTranscription = e.Message;
                    throw new OperationCanceledException(); // the others stop: asked once below
                }
                _store.Save(r.Provider, r.Model, "Transcription", "Speech to text", $"{m.Name} {AiText.Key("media", m.RefId)}",
                            (text == "" ? "(no speech recognised)" : text) + (lang is null ? "" : $"\n\nLanguage: {lang}"), [AiText.Key("media", m.RefId)], [], sendId);
                _store.MarkDone(key);
            });
        }
        catch (OperationCanceledException) when (noTranscription is { } left)
        {
            if (!SkipAudio(left)) _stopped = true;
            _audioLeftOut = left;
            Status = $"Audio not transcribed: {left}";
            return;
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
        if (!Ai.ClaudeExplores) { Status = "Choose the Claude CLI for the text job in AI settings."; return; }
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
        Show(res);
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
