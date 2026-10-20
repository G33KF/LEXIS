using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>Every dated record of the case in time order, filterable by artifacts and period; events are read as the grid scrolls.
/// Opened on several devices it is the joint timeline: their events merged, each marked with its device's colour.</summary>
public partial class TimelineViewModel : ObservableObject, IRefreshable
{
    static readonly TimelineEvent Loading = new(0, new ArtifactInfo { Category = "", Name = "", TableName = "", Columns = [] }, new ColumnInfo("", "", null), -1, [], []);
    readonly MainViewModel _main;
    readonly IReadOnlyList<Device> _devices;
    int _version;

    public bool IsJoint => _devices.Count > 1;
    public string Title => IsJoint ? "Joint Timeline" : "Timeline";
    /// <summary>The artifacts with dated records, each with its event count and whether its events are shown.</summary>
    public ObservableCollection<ActivityChoice> Activities { get; } = [];
    public System.ComponentModel.ICollectionView ActivitiesView { get; }
    /// <summary>The devices of a joint timeline, each with its events shown or not.</summary>
    public IReadOnlyList<DeviceChoice> DeviceChoices { get; }
    /// <summary>Activity histogram of the filtered events: one bar per day that has any.</summary>
    [ObservableProperty] private IReadOnlyList<DayBar> days = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EmptyText))] private VirtualRows<TimelineEvent>? events;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PeriodText))] private DateTime? from;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PeriodText))] private DateTime? to;
    /// <summary>Narrows the artifact list in the picker (not the events).</summary>
    [ObservableProperty] private string activityFilter = "";
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private bool descending;
    [ObservableProperty] private TimelineEvent? selected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CountText))] private long total;

    public string CountText => Total == 0 ? "No events" : $"{Total:N0} events";
    public string EmptyText => Events is null || Total > 0 ? "" : Filter != "" || Shown() is not null || From is not null || To is not null || DeviceChoices.Any(c => !c.IsChecked) ? "No events match the filters" : "No events";
    public string ActivitiesText => Activities.Count(a => a.IsChecked) is var n && n == Activities.Count ? "All artifacts" : $"{n} of {Activities.Count} artifacts";
    public string PeriodText => (From, To) switch
    {
        (null, null) => "Whole period",
        _ => $"{From?.ToString("yyyy-MM-dd") ?? "…"} → {To?.ToString("yyyy-MM-dd") ?? "…"}",
    };

    public TimelineViewModel(MainViewModel main, IReadOnlyList<Device> devices)
    {
        _main = main;
        _devices = [.. devices];
        DeviceChoices = [.. _devices.Select(d => new DeviceChoice(d, ApplyFilter))];
        ActivitiesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Activities);
        ActivitiesView.Filter = o => ActivityFilter == "" || ((ActivityChoice)o).Name.Contains(ActivityFilter, StringComparison.OrdinalIgnoreCase);
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        var sessions = _devices.Select(d => d.Session).ToList();
        foreach (var (a, n) in await Task.Run(() => Timeline.Activities(sessions))) Activities.Add(new ActivityChoice(a, n, ActivityToggled));
        OnPropertyChanged(nameof(ActivitiesText));
        await LoadAsync();
    }

    public void Refresh() => _ = LoadAsync();

    async Task LoadAsync()
    {
        var version = ++_version;
        var (f, a, d) = (Filter, Shown(), Descending);
        var (from, to) = ValueFormatter.Days(From, To);
        int[]? dev = DeviceChoices.All(c => c.IsChecked) ? null : [.. Enumerable.Range(0, _devices.Count).Where(i => DeviceChoices[i].IsChecked)];
        var sessions = _devices.Select(x => x.Session).ToList();
        Timeline.View view;
        IReadOnlyList<DayCount> perDay;
        try { (view, perDay) = await Task.Run(() => { var v = Timeline.Open(sessions, f, a, d, dev, from, to); return (v, v.PerDay()); }); }
        catch (Microsoft.Data.Sqlite.SqliteException e) { _main.Status = "Timeline: " + e.Message; return; }
        if (version != _version) return;
        Total = view.Count;
        _tags.Clear();
        Func<long, int, IReadOnlyList<TimelineEvent>> fetch = (start, n) => Mark(view.Fetch(start, n));
        Events = new VirtualRows<TimelineEvent>(view.Count, fetch, () => Loading with { });
        var max = Math.Max(1, perDay.Count == 0 ? 1 : perDay.Max(x => x.Count));
        Days = perDay.Select(x => new DayBar(x, 2 + 38.0 * x.Count / max, $"{x.Day:yyyy-MM-dd ddd}: {x.Count:N0} events")).ToList();
    }

    // ponytail: tags read when a page loads; a row tagged afterwards shows its dot once the timeline reloads
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, string), Dictionary<long, TagEntry>> _tags = new();

    IReadOnlyList<TimelineEvent> Mark(IReadOnlyList<TimelineEvent> page) => page.Select(e =>
    {
        var tag = _tags.GetOrAdd((e.Device, e.Artifact.TableName), k => _devices[k.Item1].Session.Tags.ForTable(k.Item2)).GetValueOrDefault(e.RowId)?.Tag;
        return IsJoint ? e with { DeviceLabel = _devices[e.Device].Label, DeviceColor = _devices[e.Device].Color, Tag = tag } : e with { Tag = tag };
    }).ToList();

    partial void OnFromChanged(DateTime? value) => ApplyFilter();
    partial void OnToChanged(DateTime? value) => ApplyFilter();
    partial void OnActivityFilterChanged(string value) => ActivitiesView.Refresh();

    /// <summary>The artifacts whose events are shown, or null when all are.</summary>
    HashSet<string>? Shown() => Activities.All(x => x.IsChecked) ? null : [.. Activities.Where(x => x.IsChecked).Select(x => x.Name)];

    void ActivityToggled()
    {
        if (_bulk) return;
        OnPropertyChanged(nameof(ActivitiesText));
        ApplyFilter();
    }

    bool _bulk;
    /// <summary>Checks or clears every artifact the picker lists (all of them when its search is empty).</summary>
    [RelayCommand]
    void CheckActivities(string? on)
    {
        _bulk = true;
        foreach (ActivityChoice x in ActivitiesView) x.IsChecked = on == "True";
        _bulk = false;
        ActivityToggled();
    }

    [RelayCommand]
    void ClearPeriod() => (From, To) = (null, null);
    partial void OnDescendingChanged(bool value) => ApplyFilter();

    partial void OnSelectedChanged(TimelineEvent? value)
    {
        if (value is { IsPlaceholder: false }) _main.Details.ShowEvent(value.Session ?? _devices[0].Session, value);
    }

    [RelayCommand]
    void ApplyFilter() => _ = LoadAsync();

    /// <summary>Selects the day's first event, which scrolls the grid to it.</summary>
    [RelayCommand]
    async Task GoToDay(DayBar? bar)
    {
        if (bar is not null && Events is { } ev && await ev.GetAsync((int)bar.Day.Position) is { } e) Selected = e;
    }
}

public sealed record DayBar(DayCount Day, double Height, string Tip);

/// <summary>An artifact in the timeline's picker: shown or not, with its number of events.</summary>
public sealed partial class ActivityChoice(string name, long count, Action toggled) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = $"{name} ({count:N0})";
    [ObservableProperty] private bool isChecked = true;
    partial void OnIsCheckedChanged(bool value) => toggled();
}

/// <summary>A device of the case in an analysis's picker: used or not, all used to start.</summary>
public sealed partial class DeviceChoice(Device device, Action toggled) : ObservableObject
{
    public Device Device { get; } = device;
    [ObservableProperty] private bool isChecked = true;
    partial void OnIsCheckedChanged(bool value) => toggled();
}
