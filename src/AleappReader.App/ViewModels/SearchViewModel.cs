using System.Collections.ObjectModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>An artifact under one group: the whole query, or one of its terms (Filter = the query narrowed to it).</summary>
public sealed record SearchHitItem(SearchHit Hit, string Group, long Count, string Filter, string Preview, CaseSession Session, string Device = "")
{
    public string Title => Device == "" ? Hit.Artifact.Name : $"{Device} · {Hit.Artifact.Name}";
    public string Category => Hit.Artifact.Category;
}

/// <summary>Search across every artifact table of every open device; results grouped by the query, then by each of
/// its terms (and by device in an investigation case); a hit opens the artifact filtered so it shows the same records.</summary>
public partial class SearchViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly IReadOnlyList<Device> _devices;
    CancellationTokenSource _cts = new();

    public string Term { get; }
    public ObservableCollection<SearchHitItem> Hits { get; } = [];
    public ListCollectionView HitsView { get; }
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

    public SearchViewModel(MainViewModel main, IReadOnlyList<Device> devices, string term)
    {
        _main = main;
        _devices = [.. devices];
        Term = term;
        HitsView = new ListCollectionView(Hits);
        HitsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchHitItem.Group)));
        DeviceChoices = [.. _devices.Select(d => new DeviceChoice(d, () => { if (Term != "") _ = RunAsync(); }))];
        if (term != "") { foreach (var d in _devices) d.Session.AddSearch(term); _ = RunAsync(); }
        else Info = "Type in the search box and press Enter: a b (both), a | b (either), -a (not), \"exact phrase\", /regex/.";
        history = _devices[0].Session.SearchHistory;
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

    /// <summary>Takes the selected results off the list (the records stay in the case; searching again brings them back).</summary>
    [RelayCommand]
    void RemoveHits(System.Collections.IList? selected)
    {
        foreach (var h in selected?.OfType<SearchHitItem>().ToList() ?? []) Hits.Remove(h);
        Info = $"{Term}: {Hits.Where(h => h.Filter == Term).Sum(h => h.Count):N0} records in the {Hits.Count(h => h.Filter == Term)} results kept";
    }

    int _run;

    async Task RunAsync()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var (ct, n) = (_cts.Token, ++_run);
        Hits.Clear();
        var devices = DeviceChoices.Where(c => c.IsChecked).Select(c => c.Device).ToList();
        if (devices.Count == 0) { Info = "Check at least one device to search."; Running = false; _main.SetSearchCount(this, 0, 0); return; }
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
                found.Add((dev, await Task.Run(() => GlobalSearch.Run(dev.Session, Term, 3, prog, ct))));
                if (n != _run) return;
            }
            var query = SearchQuery.Parse(Term);
            var terms = query.Terms;
            var all = found.SelectMany(f => f.Hits).ToList();
            foreach (var (dev, hits) in found)
            {
                var label = multi ? dev.Label : "";
                string Group(string g, long count) => multi ? $"{dev.Label} · {g} · {count:N0} records" : $"{g} · {count:N0} records";
                var whole = Group(Term, hits.Sum(h => h.Total));
                foreach (var h in hits.OrderByDescending(h => h.Total))
                    Hits.Add(new SearchHitItem(h, whole, h.Total, Term, Snippet(h, query), dev.Session, label));
                if (query.HasAlternatives)
                    for (int t = 0; t < terms.Count; t++)
                    {
                        var narrowed = $"{Term} {terms[t]}";
                        var group = Group(terms[t].ToString()!, hits.Sum(h => h.TermCounts[t]));
                        foreach (var h in hits.Where(h => h.TermCounts[t] > 0).OrderByDescending(h => h.TermCounts[t]))
                            Hits.Add(new SearchHitItem(h, group, h.TermCounts[t], narrowed, Snippet(h, SearchQuery.Parse(narrowed)), dev.Session, label));
                    }
            }
            Info = $"{Term}: {all.Sum(h => h.Total):N0} records in {all.Count} artifacts" +
                   (multi ? $" of {found.Count(f => f.Hits.Count > 0)} of {devices.Count} devices searched" +
                            (devices.Count < _devices.Count ? $" ({_devices.Count - devices.Count} left out)" : "") : "") +
                   (SkipTechnical ? " · technical fields excluded" : "");
            _main.SetSearchCount(this, all.Count, all.Sum(h => h.Total));
        }
        catch (OperationCanceledException) { if (n == _run) Info = "Search cancelled."; }
        finally { if (n == _run) Running = false; }
    }

    /// <summary>The first cell of the first hit that holds a term of the query.</summary>
    static string Snippet(SearchHit h, SearchQuery query)
    {
        var terms = query.Terms;
        foreach (var r in h.Rows)
            for (int i = 0; i < h.Columns.Count; i++)
            {
                var v = ValueFormatter.Format(r.Values[i], h.Columns[i]);
                if (terms.FirstOrDefault(t => t.Matches(v)) is not { } t) continue;
                var at = t.Regex ? 0 : v.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase);
                var start = Math.Max(0, at - 40);
                return $"{h.Columns[i].Display}: {(start > 0 ? "…" : "")}{v.Substring(start, Math.Min(v.Length - start, 120))}";
            }
        return "";
    }

    [RelayCommand(CanExecute = nameof(Running))]
    void Cancel() => _cts.Cancel();

    [RelayCommand]
    void Open(SearchHitItem? item)
    {
        if (item is not null) _main.NavigateTo(item.Hit.Artifact, item.Filter);
    }

    // the advanced form: composed into the typed syntax, so the search box and history show what ran
    public static IReadOnlyList<string> Modes { get; } = ["All of the words", "Any of the words", "The exact phrase", "Regular expression"];
    public bool AdvOpen => Term == "";
    [ObservableProperty] private string advWords = "";
    [ObservableProperty] private int advMode;
    [ObservableProperty] private bool advWhole;
    [ObservableProperty] private string advExclude = "";

    [RelayCommand]
    void SearchAdvanced()
    {
        var q = SearchQuery.Compose(AdvWords, (SearchQuery.Mode)AdvMode, AdvWhole, AdvExclude);
        if (q is null) Info = "Not a valid regular expression.";
        else if (q != "") _main.SearchFor(q, _devices);
    }

    [RelayCommand]
    void Rerun(string? query)
    {
        if (!string.IsNullOrWhiteSpace(query)) _main.SearchFor(query, _devices);
    }

    bool NotRunning() => !Running;

    /// <summary>Tags every record behind the chosen results (all of them when none is chosen), each on its device.</summary>
    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task Tag(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } p || p[0] is not string tag) return;
        var chosen = (p[1] as System.Collections.IList)?.OfType<SearchHitItem>().ToList() is { Count: > 0 } sel ? sel
                   : Hits.Where(h => h.Filter == Term).ToList();
        Running = true;
        Info = $"Tagging {chosen.Sum(h => h.Count):N0} records as {tag}…";
        try
        {
            var records = await Task.Run(() => chosen.GroupBy(h => h.Session).SelectMany(g =>
            {
                using var con = g.Key.OpenDb();
                return g.SelectMany(h => ArtifactQuery.MatchingRowIds(con, h.Hit.Artifact, h.Filter, g.Key.SearchSkips(h.Hit.Artifact))
                    .Select(id => (h.Hit.Artifact, id))).ToList();
            }).Distinct().ToList());
            _main.SetTag(records, tag);
            Info = $"Tagged {records.Count:N0} records as {tag} in {chosen.Select(h => h.Hit.Artifact).Distinct().Count()} artifacts.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { Info = "Cannot tag: " + e.Message; }
        finally { Running = false; }
    }
}
