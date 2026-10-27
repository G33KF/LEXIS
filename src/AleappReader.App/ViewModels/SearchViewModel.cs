using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>A value of a facet with its number of results; a click shows only those (again: all).</summary>
public sealed record FacetValue(string Facet, string Value, int Count, bool IsChosen)
{
    public string Label => $"{(Value == "" ? "(none)" : Value)} ({Count:N0})";
}

public sealed record FacetGroup(string Title, IReadOnlyList<FacetValue> Values);

/// <summary>A category of the case, searched or not.</summary>
public partial class CategoryChoice(string category, int artifacts, Action changed) : ObservableObject
{
    public string Category { get; } = category;
    public string Label { get; } = artifacts < 0 ? category : $"{category} ({artifacts})";
    [ObservableProperty] private bool isChecked = true;
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>Search across the artifact tables of the chosen categories of every open device, the text of the extraction's
/// files (read once and kept, <see cref="TextStore"/>) and the AI results: one row per record, file part or result,
/// narrowed by text, by each column's ▾ and by facets with counts; a row opens its record, file or database.</summary>
public partial class SearchViewModel : ObservableObject, IColumnFilters
{
    const int RowsPerArtifact = 5000;
    readonly MainViewModel _main;
    readonly IReadOnlyList<Device> _devices;
    CancellationTokenSource _cts = new();
    List<SearchRow> _all = [];
    readonly List<ColumnFilter> _columnFilters = [];
    readonly Dictionary<string, string> _facets = [];
    bool _ready;

    public string Term { get; }
    /// <summary>The rows the filters keep (a new list each time: one reset of the grid, not one per row).</summary>
    [ObservableProperty] private IReadOnlyList<SearchRow> rows = [];
    public ObservableCollection<FacetGroup> FacetGroups { get; } = [];
    public IReadOnlyList<CategoryChoice> CategoryChoices { get; }
    /// <summary>Where to search: the report's records, the extraction's files (of the kinds checked), the AI results.</summary>
    [ObservableProperty] private bool searchReport = true;
    [ObservableProperty] private bool searchFiles;
    [ObservableProperty] private bool searchAi = true;
    public IReadOnlyList<CategoryChoice> FileKindChoices { get; }
    partial void OnSearchReportChanged(bool value) => Rerun();
    partial void OnSearchFilesChanged(bool value) => Rerun();
    partial void OnSearchAiChanged(bool value) => Rerun();
    void Rerun() { if (_ready && Term != "") _ = RunAsync(); }
    /// <summary>The results table's columns that take a condition from their header (Sql = the SearchRow property).</summary>
    public IReadOnlyList<ColumnInfo> ListColumns { get; } =
    [
        new("Origin", "Origin", null), new("Category", "Category", null), new("Kind", "Artifact / type", null),
        new("Date", "Date", "datetime"), new("App", "App / module", null), new("Device", "Device", null),
        new("Where", "Found in", null), new("Snippet", "Text", null), new("Path", "Source", null),
    ];
    [ObservableProperty] private string resultFilter = "";
    [ObservableProperty] private int columnFilterCount;
    public bool HasFilters => ColumnFilterCount > 0 || _facets.Count > 0 || ResultFilter != "";
    public string CountText => Rows.Count == _all.Count ? "" : $"{Rows.Count:N0} of {_all.Count:N0} results shown";
    /// <summary>The devices of an investigation case, each searched or not.</summary>
    public IReadOnlyList<DeviceChoice> DeviceChoices { get; }
    public bool IsMulti => _devices.Count > 1;
    public IReadOnlyList<Device> Devices => _devices;
    [ObservableProperty] private IReadOnlyList<string> history;
    public IReadOnlyList<string> TagChoices => Settings.Current.Tags;
    [ObservableProperty] private string info = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(TagCommand))] private bool running;

    public bool SkipTechnical
    {
        get => _devices[0].Session.SkipTechnical;
        set
        {
            if (SkipTechnical == value) return;
            foreach (var d in _devices) d.Session.SkipTechnical = value;
            OnPropertyChanged();
            _main.RefreshViews();
            if (Term != "") _ = RunAsync();
        }
    }

    /// <param name="categories">The categories searched (null: all), e.g. the one selected in the tree.</param>
    public SearchViewModel(MainViewModel main, IReadOnlyList<Device> devices, string term, IReadOnlyCollection<string>? categories = null)
    {
        _main = main;
        _devices = [.. devices];
        Term = term;
        DeviceChoices = [.. _devices.Select(d => new DeviceChoice(d, () => { if (Term != "") _ = RunAsync(); }))];
        CategoryChoices = [.. _devices.SelectMany(d => d.Session.Manifest.Artifacts).GroupBy(a => a.Category)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryChoice(g.Key, g.Select(a => a.Name).Distinct().Count(), () => { if (_ready && Term != "") _ = RunAsync(); })
                { IsChecked = categories is null || categories.Contains(g.Key) })];
        FileKindChoices = [.. ContentIndex.Kinds.Select(k => new CategoryChoice(k, -1, Rerun))];
        _ready = true;
        if (term != "") { foreach (var d in _devices) d.Session.AddSearch(term); _ = RunAsync(); }
        else Info = "Type in the search box and press Enter: a b (both), a | b (either), -a (not), \"exact phrase\", /regex/.";
        history = _devices[0].Session.SearchHistory;
    }

    [RelayCommand]
    void AllCategories(string? check)
    {
        var on = check != "none";
        if (CategoryChoices.All(c => c.IsChecked == on)) return;
        _ready = false; // one search for all the boxes, not one per box
        foreach (var c in CategoryChoices) c.IsChecked = on;
        _ready = true;
        if (Term != "") _ = RunAsync();
    }

    [RelayCommand]
    void ClearHistory()
    {
        foreach (var d in _devices) d.Session.ClearSearchHistory();
        History = [];
    }

    /// <summary>Forgets one of the recent searches.</summary>
    [RelayCommand]
    void RemoveHistory(string? query)
    {
        if (query is null) return;
        foreach (var d in _devices) d.Session.RemoveSearch(query);
        History = _devices[0].Session.SearchHistory;
    }

    int _run;
    IReadOnlyList<(Device D, IReadOnlyList<SearchHit> Hits)> _found = [];

    async Task RunAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var (ct, n) = (_cts.Token, ++_run);
        (_all, _found) = ([], []);
        ApplyFilter();
        var devices = DeviceChoices.Where(c => c.IsChecked).Select(c => c.Device).ToList();
        var categories = CategoryChoices.Where(c => SearchReport && c.IsChecked).Select(c => c.Category).ToHashSet();
        var kinds = FileKindChoices.Where(c => SearchFiles && c.IsChecked).Select(c => c.Category).ToHashSet();
        if (devices.Count == 0 || categories.Count == 0 && kinds.Count == 0 && !SearchAi)
        {
            Info = devices.Count == 0 ? "Check at least one device to search." : "Check at least one category or kind of file to search.";
            Running = false;
            _main.SetSearchCount(this, 0, 0);
            return;
        }
        Running = true;
        Info = $"Searching {Term}…";
        var multi = _devices.Count > 1;
        try
        {
            var found = new List<(Device D, IReadOnlyList<SearchHit> Hits)>();
            for (int d = 0; d < devices.Count; d++)
            {
                var (dev, at) = (devices[d], d);
                var prog = new Progress<(int Done, int Total)>(p => Progress = 100.0 * (at + (double)p.Done / Math.Max(1, p.Total)) / devices.Count);
                found.Add((dev, categories.Count == 0 ? [] : await Task.Run(() => GlobalSearch.Run(dev.Session, Term, RowsPerArtifact, prog, ct, categories))));
                if (n != _run) return;
            }
            var all = found.SelectMany(f => f.Hits).ToList();
            var rows = await Task.Run(() => found.SelectMany(f => f.Hits.SelectMany(h => GlobalSearch.Rows(h, Term, multi ? f.D.Label : ""))).ToList());
            if (n != _run) return;
            (_all, _found) = (rows, found); // the report's records shown while the files are read
            ApplyFilter();
            var noExtraction = 0;
            foreach (var dev in devices)
            {
                var label = multi ? dev.Label : "";
                if (kinds.Count > 0 && dev.Session.Extraction is null) noExtraction++;
                else if (kinds.Count > 0 && dev.Session.Extraction is { } x)
                {
                    var store = TextStore.For(dev.Session);
                    var prog = new Progress<(int Done, int Total)>(p =>
                    {
                        Progress = 100.0 * p.Done / Math.Max(1, p.Total);
                        Info = $"Reading the text of the extraction's files{(multi ? " of " + dev.Label : "")}: {p.Done:N0} of {p.Total:N0} (once: it is kept)…";
                    });
                    rows = [.. rows, .. await Task.Run(() =>
                    {
                        ContentIndex.Build(store, x, kinds, prog, ct);
                        return store.Search(Term, kinds, RowsPerArtifact, ct).Select(h => GlobalSearch.Row(h, Term, label))
                            .Concat(store.Files(st => st.Contains("scanned")).Where(f => kinds.Contains(f.Kind)).Select(f => GlobalSearch.Row(f, label))).ToList();
                    }, ct)];
                }
                if (SearchAi) rows = [.. rows, .. await Task.Run(() => GlobalSearch.Rows(AiStore.For(dev.Session), Term, label).ToList(), ct)];
                if (n != _run) return;
            }
            (_all, _found) = (rows, found);
            var cut = all.Count(h => h.Total > h.Rows.Count);
            int Count(string origin) => rows.Count(r => r.Origin == origin);
            Info = $"{Term}: {all.Sum(h => h.Total):N0} records in {all.Count} artifacts" +
                   (kinds.Count > 0 ? $" · {Count(SearchRow.FileContent):N0} file parts" + (Count(SearchRow.NeedsOcr) is > 0 and var o ? $" ({o:N0} scanned files need OCR)" : "") : "") +
                   (kinds.Count > 0 && noExtraction > 0 ? $" · {noExtraction} device(s) without their extraction" : "") +
                   (SearchAi && Count(SearchRow.AiAnalysis) is > 0 and var ai ? $" · {ai:N0} AI results" : "") +
                   (cut > 0 ? $" · the first {RowsPerArtifact:N0} of each are listed ({cut} artifacts have more)" : "") +
                   (multi ? $" of {found.Count(f => f.Hits.Count > 0)} of {devices.Count} devices searched" +
                            (devices.Count < _devices.Count ? $" ({_devices.Count - devices.Count} left out)" : "") : "") +
                   (categories.Count < CategoryChoices.Count ? $" · {categories.Count} of {CategoryChoices.Count} categories" : "") +
                   (SkipTechnical ? " · technical fields excluded" : "");
            ApplyFilter();
            _main.SetSearchCount(this, all.Count, all.Sum(h => h.Total));
        }
        catch (OperationCanceledException) { if (n == _run) Info = "Search cancelled."; }
        finally { if (n == _run) Running = false; }
    }

    partial void OnResultFilterChanged(string value) => ApplyFilter();

    /// <summary>The rows the text, the facets and the column conditions keep; each facet counted over the rows the
    /// other filters keep, so its numbers say what a click would show.</summary>
    void ApplyFilter()
    {
        Rows = [.. _all.Where(r => SearchRows.Keep(r, ResultFilter, _facets, _columnFilters))];
        FacetGroups.Clear();
        foreach (var f in SearchRows.Facets)
        {
            if (f == nameof(SearchRow.Device) && !IsMulti) continue;
            var others = _facets.Where(x => x.Key != f).ToDictionary(x => x.Key, x => x.Value);
            var values = SearchRows.Count(_all.Where(r => SearchRows.Keep(r, ResultFilter, others, _columnFilters)), f);
            if (values.Count > 1 || _facets.ContainsKey(f))
                FacetGroups.Add(new(ListColumns.First(c => c.Sql == f).Display,
                    [.. values.Select(v => new FacetValue(f, v.Value, v.Count, _facets.GetValueOrDefault(f) == v.Value))]));
        }
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasFilters));
    }

    [RelayCommand]
    void ChooseFacet(FacetValue? v)
    {
        if (v is null) return;
        if (v.IsChosen) _facets.Remove(v.Facet); else _facets[v.Facet] = v.Value;
        ApplyFilter();
    }

    [RelayCommand]
    void ClearFilters()
    {
        _facets.Clear();
        _columnFilters.Clear();
        ColumnFilterCount = 0;
        if (ResultFilter != "") ResultFilter = ""; else ApplyFilter();
    }

    public ColumnFilter? FilterOf(string sqlColumn) => _columnFilters.FirstOrDefault(f => f.Sql == sqlColumn);

    public void SetColumnFilter(string sqlColumn, ColumnFilter? f)
    {
        _columnFilters.RemoveAll(x => x.Sql == sqlColumn);
        if (f is not null) _columnFilters.Add(f);
        ColumnFilterCount = _columnFilters.Count;
        ApplyFilter();
    }

    [RelayCommand(CanExecute = nameof(Running))]
    void Cancel() => _cts.Cancel();

    /// <summary>A record in its artifact; a database in the SQLite reader; a picture, video, audio or PDF in its own
    /// window; any other file in its program (a read-only copy).</summary>
    [RelayCommand]
    async Task Open(SearchRow? row)
    {
        if (row?.Artifact is { } a) { _main.NavigateTo(a, Term, row.RowId); return; }
        if (row is null || row.Origin is not (SearchRow.FileContent or SearchRow.NeedsOcr)) return;
        var s = (_devices.FirstOrDefault(d => d.Label == row.Device) ?? _devices[0]).Session;
        if (s.Extraction is not { } x) return;
        var rel = row.Path;
        if (row.Category == RawFiles.Databases)
        {
            string? Side(string suffix) => x.Contains(rel + suffix) ? x.CopyOut(rel + suffix) : null;
            var (db, wal, shm, journal) = await Task.Run(() => (x.CopyOut(rel), Side("-wal"), Side("-shm"), Side("-journal")));
            if (db is not null) Views.SqliteWindow.Show(db, Path.GetFileName(rel), $"{EvidenceOrigin.DevicePath(rel)} · {row.Where} · in the extraction {EvidenceOrigin.ExtractionName(s)}", wal, shm, journal);
            return;
        }
        if (await Task.Run(() => x.CopyOut(rel)) is not { } copy) { Info = $"Could not read {rel}"; return; }
        var m = x.ToMedia(rel, copy);
        if (m.Kind == "file" && !DetailsViewModel.IsPdf(m)) DetailsViewModel.OpenMedia(m);
        else Views.RecordWindow.ShowMedia(_main, m, null);
    }

    // the advanced form: composed into the typed syntax, so the search box and history show what ran
    public static IReadOnlyList<string> Modes { get; } = ["All of the words", "Any of the words", "The exact phrase", "Regular expression"];
    public bool AdvOpen => Term == "";
    [ObservableProperty] private string advWords = "";
    [ObservableProperty] private int advMode;
    [ObservableProperty] private bool advWhole;
    [ObservableProperty] private string advExclude = "";

    // a keyword list from a text file, re-split when the separator changes
    [ObservableProperty] private string advSeparator = KeywordList.DefaultSeparator;
    [ObservableProperty] private string advListInfo = "";
    (string Text, string Encoding, string Name)? _listFile;
    IReadOnlyList<string> _advList = [];

    [RelayCommand]
    void LoadList()
    {
        var dlg = new OpenFileDialog { Title = "Keyword list (one per line, or separated)", Filter = "Text (*.txt;*.csv)|*.txt;*.csv|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (text, encoding) = KeywordList.Decode(File.ReadAllBytes(dlg.FileName));
            _listFile = (text, encoding, Path.GetFileName(dlg.FileName));
        }
        catch (IOException e) { Info = e.Message; return; }
        AdvMode = (int)SearchQuery.Mode.AnyWord;
        ParseList();
    }

    [RelayCommand]
    void ClearList() { _listFile = null; ParseList(); }

    partial void OnAdvSeparatorChanged(string value) => ParseList();

    void ParseList()
    {
        _advList = _listFile is { } f ? KeywordList.Parse(f.Text, AdvSeparator) : [];
        AdvListInfo = _listFile is { } g ? $"{_advList.Count} keywords from {g.Name} ({g.Encoding})" : "";
    }

    [RelayCommand]
    void SearchAdvanced()
    {
        var q = SearchQuery.Compose(AdvWords, (SearchQuery.Mode)AdvMode, AdvWhole, AdvExclude, _advList);
        if (q is null) Info = "Not a valid regular expression.";
        else if (q != "") _main.SearchFor(q, _devices);
    }

    [RelayCommand]
    void Rerun(string? query)
    {
        if (!string.IsNullOrWhiteSpace(query)) _main.SearchFor(query, _devices);
    }

    bool NotRunning() => !Running;

    /// <summary>Tags the chosen rows; with none chosen, the rows shown, or (no filter set) every record the search
    /// found, beyond the rows listed per artifact.</summary>
    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task Tag(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } p || p[0] is not string tag) return;
        var chosen = (p[1] as System.Collections.IList)?.OfType<SearchRow>().ToList() is { Count: > 0 } sel ? sel : null;
        Running = true;
        Info = $"Tagging as {tag}…";
        try
        {
            var records = chosen is null && !HasFilters
                ? await Task.Run(() => _found.SelectMany(f =>
                  {
                      using var con = f.D.Session.OpenDb();
                      return f.Hits.SelectMany(h => ArtifactQuery.MatchingRowIds(con, h.Artifact, Term, f.D.Session.SearchSkips(h.Artifact))
                          .Select(id => (h.Artifact, id))).ToList();
                  }).Distinct().ToList())
                : [.. (chosen ?? Rows).Where(r => r.Artifact is not null).Select(r => (r.Artifact!, r.RowId)).Distinct()];
            _main.SetTag(records, tag);
            Info = $"Tagged {records.Count:N0} records as {tag} in {records.Select(r => r.Item1).Distinct().Count()} artifacts.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { Info = "Cannot tag: " + e.Message; }
        finally { Running = false; }
    }
}
