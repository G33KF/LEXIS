using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>One part of a file's text (a page, sheet or slide) as extracted, with its AI translation under it when there is one.</summary>
public sealed record TextPart(string Part, string Method, string Text, string? Translation = null, string? TranslationLabel = null, bool TranslationStale = false)
{
    public string Head => (Part == "" ? "Text" : Part) + " · " + Method;
    public bool HasTranslation => Translation is not null;
}

/// <summary>One AI analysis of the item, as the AI Analysis tab lists it; <see cref="Editing"/> while the examiner corrects it.</summary>
public sealed partial class AnalysisRow(ItemAnalysis a) : ObservableObject
{
    public ItemAnalysis A { get; } = a;
    public string Head => $"{A.Kind} · {A.Result.Model} · {ValueFormatter.FormatTime(A.Result.At)}";
    public string Label => A.Result.Label;
    public string? Revised => A.Revision?.EditedAt is { } t ? $"Corrected by the examiner · {ValueFormatter.FormatTime(t)} (the model's text is kept)" : null;
    public string? ExcludedOn => A.Revision?.ExcludedAt is { } t ? $"Excluded · {ValueFormatter.FormatTime(t)}" : null;
    [ObservableProperty] private bool editing;
    [ObservableProperty] private string draft = "";
}

/// <summary>"Analysis Results" (the item summary) or the analyses of one kind.</summary>
public sealed record AnalysisGroup(string Name, IReadOnlyList<AnalysisRow> Rows, bool IsSummary = false)
{
    public string Header => IsSummary ? Name : $"{Name} ({Rows.Count})";
}

/// <summary>The details pane's Text Extraction and AI Analysis tabs: shown only when the item has content for them.</summary>
public partial class DetailsViewModel
{
    public ObservableCollection<TextPart> Texts { get; } = [];
    public ObservableCollection<AnalysisGroup> Groups { get; } = [];
    [ObservableProperty] private AnalysisGroup? group;
    /// <summary>0 Details, 1 Text Extraction, 2 AI Analysis; the tab chosen stays for the next item that has it.</summary>
    [ObservableProperty] private int tab;
    [ObservableProperty] private bool showExcluded;
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private string summaryLabel = "";
    [ObservableProperty] private bool summaryStale;
    [ObservableProperty] private string aiStatus = "";

    List<ItemAnalysis> _analyses = [];
    (string Key, string Rel)? _text; // the file whose text is shown, and its AI key
    DateTimeOffset _shownAt;
    int _wanted;
    bool _fixing;

    public bool HasTexts => Texts.Count > 0;
    public bool HasAnalyses => _analyses.Count > 0;
    public string TextTabHeader => $"Text Extraction ({Texts.Count})";
    public string AiTabHeader => $"AI Analysis ({_analyses.Count(a => !a.Excluded)})";
    public string TranslateLabel => $"Translate into {Settings.Current.Ai.TranslateTo} (AI)";

    partial void OnTabChanged(int value) { if (!_fixing) _wanted = value; }
    partial void OnShowExcludedChanged(bool value) => FillGroups();

    /// <summary>The tab chosen when this item has it, else Details; the headers as now.</summary>
    void FixTab()
    {
        OnPropertyChanged(nameof(HasTexts));
        OnPropertyChanged(nameof(HasAnalyses));
        OnPropertyChanged(nameof(TextTabHeader));
        OnPropertyChanged(nameof(AiTabHeader));
        _fixing = true;
        Tab = _wanted == 1 && HasTexts || _wanted == 2 && HasAnalyses ? _wanted : 0;
        _fixing = false;
    }

    void ClearTabs()
    {
        Texts.Clear();
        Groups.Clear();
        _analyses = [];
        _text = null;
        (SummaryText, SummaryLabel, SummaryStale, AiStatus) = ("", "", false, "");
        _shownAt = DateTimeOffset.Now;
        FixTab();
    }

    /// <summary>The text extracted from a file of the extraction (<paramref name="rel"/>), part by part, with each part's translation.</summary>
    void ShowTexts(CaseSession s, string rel, string key)
    {
        try
        {
            var parts = TextStore.For(s).Parts(rel);
            var tr = AiText.PartTranslations(AiStore.For(s), key, parts.Select(p => (p.Part, p.Text)));
            foreach (var p in parts.Where(p => p.Text.Trim() != ""))
                Texts.Add(tr.TryGetValue(p.Part, out var t)
                    ? new TextPart(p.Part, p.Method, p.Text, t.Result.Output.Trim(), $"AI translation · {t.Result.Model} · {ValueFormatter.FormatTime(t.Result.At)}", t.Stale)
                    : new TextPart(p.Part, p.Method, p.Text));
            _text = (key, rel);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { } // text base unreadable
        FixTab();
    }

    /// <summary>The item's analyses into their groups: the summary first when there are two or more, then one group per kind.</summary>
    void FillGroups()
    {
        var keep = Group?.Name;
        Groups.Clear();
        var shown = _analyses.Where(a => ShowExcluded || !a.Excluded).ToList();
        if (_analyses.Count(a => !a.Excluded) >= 2) Groups.Add(new AnalysisGroup("Analysis Results", [], true));
        foreach (var g in shown.GroupBy(a => a.Kind)) Groups.Add(new AnalysisGroup(g.Key, [.. g.Select(a => new AnalysisRow(a))]));
        Group = Groups.FirstOrDefault(g => g.Name == keep) ?? Groups.FirstOrDefault();
        FixTab();
    }

    /// <summary>Loads the item's analyses and summary; after an AI run (<paramref name="ran"/>) a new analysis of the item
    /// gets its summary written again.</summary>
    async Task LoadAnalysesAsync(bool ran = false)
    {
        if (_s is not { } s || _aiKeys.Length == 0) { FixTab(); return; }
        var (keys, shown) = (_aiKeys.Select(k => k.Key).ToList(), _shown);
        var (list, sum) = await Task.Run(() =>
        {
            try
            {
                var store = AiStore.For(s);
                return (AiText.Analyses(store, keys),
                        store.Citing(keys[0]).Where(r => r.Kind == AiText.ItemSummary).MaxBy(r => r.Id)); // the item's own, not an attachment's
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException) { return ([], null); }
        });
        if (shown != _shown) return;
        _analyses = list;
        SummaryText = sum?.Output.Trim() ?? "";
        SummaryLabel = sum?.Label ?? "";
        SummaryStale = sum is not null && AiText.IsStale(sum, list);
        FillGroups();
        var included = list.Where(a => !a.Excluded).ToList();
        // written again on its own only with a model on this machine: a remote one is asked for, with Update, never by surprise
        if (ran && included.Count >= 2 && (sum is null || SummaryStale) && AiState.Instance.Text
            && Settings.Current.Ai.ProviderOf(Settings.Current.Ai.Text)?.Provider.Where != "remote"
            && included.Max(a => a.Revision?.Last ?? a.Result.At) > _shownAt) // something about this item changed while it was shown
            await WriteSummaryAsync();
    }

    /// <summary>The item summary written again from the included analyses, with the text model, sent and kept like any AI result.</summary>
    [RelayCommand]
    async Task WriteSummaryAsync()
    {
        var ai = Settings.Current.Ai;
        if (!ai.Enabled) { AiStatus = "AI is off in AI settings."; return; }
        if (_s is not { } s || ai.ProviderOf(ai.Text) is not { } p || ai.Text.Model == "") { AiStatus = "Choose a text model in AI settings."; return; }
        var used = _analyses.Where(a => !a.Excluded).ToList();
        if (used.Count == 0 || _writing) return;
        var shown = _shown;
        var item = $"{Title}\n{Subtitle}\n" + string.Join("\n", Fields.Where(f => !f.Header && !f.IsAi && f.Value != "").Take(20)
            .Select(f => $"{f.Name}: {(f.Value.Length > 300 ? f.Value[..300] + "…" : f.Value)}"));
        var prompt = AiText.ItemSummaryPrompt(item, used, ai.AnswerLanguage);
        if (!AiViewModel.Confirm(p.Provider, "The analyses of " + Title)) return;
        AiStatus = $"Writing the summary with {ai.Text.Model}…";
        _writing = true;
        try
        {
            var store = AiStore.For(s);
            var keys = _aiKeys.Take(1).Select(k => k.Key).ToList();
            var all = _analyses;
            await Task.Run(async () =>
            {
                var client = p.Client();
                var id = store.LogSend(p.Provider, ai.Text.Model, AiText.ItemSummary, prompt);
                var output = await client.Chat(ai.Text.Model, AiText.SystemIn(ai.AnswerLanguage), prompt, null, default, await client.ContextOf(ai.Text.Model, default));
                store.Save(p.Provider, ai.Text.Model, AiText.ItemSummary, AiText.PromptOf(prompt), AiText.SummaryScope(all), output, keys, [], id);
            });
            AiStatus = "";
        }
        catch (Exception e) when (e is AiException or IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
                                       or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AiStatus = "Summary not written: " + e.Message;
            return;
        }
        finally { _writing = false; }
        if (shown == _shown) await LoadAnalysesAsync();
    }

    bool _writing; // one summary at a time: two refreshes in a row would write it twice

    [RelayCommand]
    void Correct(AnalysisRow? r)
    {
        if (r is null) return;
        r.Draft = r.A.Shown;
        r.Editing = true;
    }

    [RelayCommand] static void CancelCorrection(AnalysisRow? r) { if (r is not null) r.Editing = false; }

    [RelayCommand] Task SaveCorrection(AnalysisRow? r) => Revise(r, AiStore.Edit, r?.Draft.Trim() ?? "");
    [RelayCommand] Task Exclude(AnalysisRow? r) => Revise(r, AiStore.Exclude);
    [RelayCommand] Task Restore(AnalysisRow? r) => Revise(r, AiStore.Restore);

    async Task Revise(AnalysisRow? r, string action, string text = "")
    {
        if (r is null || _s is not { } s || action == AiStore.Edit && (text == "" || text == r.A.Shown)) { if (r is not null) r.Editing = false; return; }
        try { await Task.Run(() => AiStore.For(s).Revise(r.A.Result.Id, action, text)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { AiStatus = "Not saved: " + e.Message; return; }
        await LoadAnalysesAsync();
    }

    /// <summary>Every part of the file's text without an up-to-date translation, translated with the text model into the language set.</summary>
    [RelayCommand]
    async Task TranslateText()
    {
        var ai = Settings.Current.Ai;
        if (_s is not { } s || _text is not { } t) return;
        if (!ai.Enabled) { AiStatus = "AI is off in AI settings."; return; }
        if (ai.ProviderOf(ai.Text) is not { } p || ai.Text.Model == "") { AiStatus = "Choose a text model in AI settings."; return; }
        var todo = Texts.Where(x => !x.HasTranslation || x.TranslationStale).ToList();
        if (todo.Count == 0 || !AiViewModel.Confirm(p.Provider, $"The text of {Title}")) return;
        var shown = _shown;
        try
        {
            var store = AiStore.For(s);
            var client = p.Client();
            var n = 0;
            foreach (var part in todo)
            {
                AiStatus = $"Translating {++n} of {todo.Count}…";
                var prompt = AiText.TranslateText(part.Text, ai.TranslateTo);
                await Task.Run(async () =>
                {
                    var id = store.LogSend(p.Provider, ai.Text.Model, AiText.TextTranslation, prompt);
                    var output = await client.Chat(ai.Text.Model, "You translate text for a forensic examiner. Only the translation.", prompt, null, default,
                                                   await client.ContextOf(ai.Text.Model, default));
                    store.Save(p.Provider, ai.Text.Model, AiText.TextTranslation, AiText.PromptOf(prompt), AiText.PartScope(part.Part, part.Text), output, [t.Key], [], id);
                });
                if (shown != _shown) return;
            }
            AiStatus = "";
        }
        catch (Exception e) when (e is AiException or IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
                                       or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AiStatus = "Translation stopped: " + e.Message;
        }
        if (shown != _shown) return;
        Texts.Clear();
        ShowTexts(s, t.Rel, t.Key);
        await LoadAnalysesAsync(ran: true);
    }
}
