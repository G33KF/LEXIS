using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>A position of a device in a co-presence: its record, time and place.</summary>
public sealed record PointEnd(Device Device, ArtifactInfo? Artifact, GeoPoint Point)
{
    public string Label => Device.Label;
    public string Color => Device.Color;
    public string Time => Point.Time;
    public string Source => Point.Artifact;
    public string Position => FormattableString.Invariant($"{Point.Lat:0.000000}, {Point.Lon:0.000000}");
}

/// <summary>A co-presence: devices whose positions are close in space and time, with every position of the group.</summary>
public sealed record CoPresenceGroupRow(CoPresenceGroup Group, IReadOnlyList<PointEnd> Points, string Devices)
{
    public int Number => Group.Number;
    public int DeviceCount => Group.Devices.Count;
    public int Positions => Points.Count;
    public long FromEpoch => Group.From;
    public string From => Time(Group.From);
    public string To => Time(Group.To);
    public string Span => Group.To - Group.From is var s && s >= 3600 ? $"{s / 3600} h {s % 3600 / 60} min" : s >= 60 ? $"{s / 60} min {s % 60} s" : $"{s} s";
    public string Distance => Group.MaxMeters < 1000 ? $"{Group.MaxMeters:N0} m" : $"{Group.MaxMeters / 1000:N2} km";
    public string Centre => FormattableString.Invariant($"{Group.Lat:0.000000}, {Group.Lon:0.000000}");

    static string Time(long epoch) => ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(epoch));

}

/// <summary>
/// Positions of two or more devices close in space (within a radius) and in time (within a window), both set by the
/// examiner, joined into groups: devices near each other at the same time make one group of all of them. The devices and
/// the position artifacts used can be chosen. It shows where the records of the devices are near each other; it does
/// not say the devices, or the people using them, met: each position has its own source and accuracy.
/// </summary>
public partial class CoPresenceViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly IReadOnlyList<Device> _devices;
    List<IReadOnlyList<GeoPoint>>? _points;
    CancellationTokenSource _cts = new();
    bool _ran, _bulk;
    double _radiusRun;

    public ObservableCollection<CoPresenceGroupRow> Rows { get; } = [];
    public IReadOnlyList<DeviceChoice> DeviceChoices { get; }
    /// <summary>The artifacts with dated positions, each used or not, with their number of positions.</summary>
    public ObservableCollection<ActivityChoice> Sources { get; } = [];
    public System.ComponentModel.ICollectionView SourcesView { get; }
    public IReadOnlyList<int> MinChoices { get; }
    [ObservableProperty] private int minDevices = 2;
    [ObservableProperty] private string sourceFilter = "";
    [ObservableProperty] private double radius = 100;
    [ObservableProperty] private double minutes = 10;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool running;
    [ObservableProperty] private string info = "Set the radius and the time window, then Run. Only positions with a time are compared.";
    [ObservableProperty] private CoPresenceGroupRow? selected;
    [ObservableProperty] private PointEnd? selectedPoint;

    public string SourcesText => Sources.Count == 0 ? "All sources" : Sources.Count(s => s.IsChecked) is var n && n == Sources.Count ? "All sources" : $"{n} of {Sources.Count} sources";

    public CoPresenceViewModel(MainViewModel main, IReadOnlyList<Device> devices)
    {
        _main = main;
        _devices = [.. devices];
        DeviceChoices = [.. _devices.Select(d => new DeviceChoice(d, Rerun))];
        MinChoices = [.. Enumerable.Range(2, Math.Max(1, _devices.Count - 1))];
        SourcesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Sources);
        SourcesView.Filter = o => SourceFilter == "" || ((ActivityChoice)o).Name.Contains(SourceFilter, StringComparison.OrdinalIgnoreCase);
        _ = LoadAsync();
    }

    /// <summary>Reads the positions of every device once, and lists the artifacts they come from.</summary>
    async Task LoadAsync()
    {
        _points ??= await Task.Run(() => Positions(_devices));
        if (Sources.Count > 0) return;
        foreach (var g in _points.SelectMany(p => p).Where(p => p.Epoch is not null).GroupBy(p => p.Artifact).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            Sources.Add(new ActivityChoice(g.Key, g.Count(), SourceToggled));
        OnPropertyChanged(nameof(SourcesText));
    }

    bool CanRun() => !Running;

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Run()
    {
        if (Radius <= 0 || Minutes < 0) { Info = "The radius must be above 0 and the window 0 or more."; return; }
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var (r, w, min, ct) = (Radius, TimeSpan.FromMinutes(Minutes), MinDevices, _cts.Token);
        Running = true;
        Rows.Clear();
        try
        {
            Info = "Reading the positions of every device…";
            await LoadAsync();
            var used = Sources.Where(s => s.IsChecked).Select(s => s.Name).ToHashSet();
            var perDevice = _points!.Select((pts, d) => DeviceChoices[d].IsChecked ? (IReadOnlyList<GeoPoint>)[.. pts.Where(p => used.Contains(p.Artifact))] : []).ToList();
            var ((hits, truncated), groups) = await Task.Run(() =>
            {
                var h = CrossDevice.CoPresences(perDevice, r, w, ct: ct);
                return (h, CrossDevice.Groups(h.Hits, min));
            }, ct);
            foreach (var g in groups)
            {
                var points = g.Points.Select(End).ToList();
                Rows.Add(new CoPresenceGroupRow(g, points, string.Join(" + ", g.Devices.Select(d => _devices[d].Label))));
            }
            (_ran, _radiusRun) = (true, r);
            var timed = perDevice.Sum(p => p.Count(x => x.Epoch is not null));
            Info = $"{groups.Count:N0} co-presences of {(min == 2 ? "two or more" : $"{min} or more")} devices within {r:N0} m and {Minutes:N0} min, " +
                   $"from {hits.Count:N0} pairs of positions out of {timed:N0} dated positions" +
                   (truncated ? $" — only the first {hits.Count:N0} pairs are used: narrow the radius or the window" : "") +
                   ". Close records are not a meeting: check each position's source and accuracy.";
        }
        catch (OperationCanceledException) { Info = "Cancelled."; }
        finally { Running = false; }
    }

    PointEnd End(DevicePoint p) => new(_devices[p.Device], _devices[p.Device].Session.Manifest.Artifacts.FirstOrDefault(a => a.TableName == p.Point.TableName), p.Point);

    /// <summary>Every position of every device, one list per device.</summary>
    internal static List<IReadOnlyList<GeoPoint>> Positions(IReadOnlyList<Device> devices) => [.. devices.Select(d =>
    {
        using var con = d.Session.OpenDb();
        return (IReadOnlyList<GeoPoint>)GeoExtractor.GeoArtifacts(d.Session.Manifest).SelectMany(a =>
        {
            try { return GeoExtractor.Extract(con, a); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return (IReadOnlyList<GeoPoint>)[]; }
        }).ToList();
    })];

    /// <summary>A device, a source or the minimum changed: the analysis runs again once it has run.</summary>
    void Rerun()
    {
        if (_ran && !_bulk) _ = Run();
    }

    void SourceToggled()
    {
        if (_bulk) return;
        OnPropertyChanged(nameof(SourcesText));
        Rerun();
    }

    /// <summary>Checks or clears every source the picker lists (all of them when its search is empty).</summary>
    [RelayCommand]
    void CheckSources(string? on)
    {
        _bulk = true;
        foreach (ActivityChoice x in SourcesView) x.IsChecked = on == "True";
        _bulk = false;
        SourceToggled();
    }

    partial void OnMinDevicesChanged(int value) => Rerun();
    partial void OnSourceFilterChanged(string value) => SourcesView.Refresh();

    [RelayCommand]
    void Cancel() => _cts.Cancel();

    /// <summary>Every co-presence on the map of the devices, each a numbered circle listed in a window of its own; the map
    /// opens on the one selected.</summary>
    [RelayCommand]
    void ShowOnMap()
    {
        if (Rows.Count == 0) { Info = "Run first: the map shows the co-presences found."; return; }
        _main.ShowCoPresenceMap(_devices, [.. Rows.Select(r => r.Group)], _radiusRun, Selected?.Points[0].Point);
    }

    partial void OnSelectedChanged(CoPresenceGroupRow? value)
    {
        if (value is null) return;
        _main.Details.ShowFields($"Co-presence {value.Number}", $"{value.Devices} · {value.From} → {value.To}",
            [new("Devices", value.Devices), new("From", value.From), new("To", value.To), new("Positions", value.Positions.ToString("N0")),
             new("Largest distance between close positions", value.Distance), new("Centre", value.Centre),
             .. value.Points.Select(p => new KeyValuePair<string, string>($"{p.Label} · {p.Time}", $"{p.Position} · {p.Source}, record #{p.Point.RowId}")),
             new("Note", "Records near each other. It does not show that the devices or their users met.")]);
    }

    partial void OnSelectedPointChanged(PointEnd? value)
    {
        if (value?.Artifact is { } a) _main.Details.ShowRecord(value.Device.Session, a, value.Point.RowId);
    }

    [RelayCommand]
    void Open(PointEnd? e)
    {
        if ((e ?? SelectedPoint)?.Artifact is { } a) _main.NavigateTo(a, null, (e ?? SelectedPoint)!.Point.RowId);
    }
}

public sealed record ContactSourceRow(string Device, string Color, ContactSource Source)
{
    public string Artifact => Source.Artifact.Name;
}

public sealed record CommonContactRow(CommonContact Contact, IReadOnlyList<ContactSourceRow> Sources)
{
    public string Key => Contact.Key;
    public string Kind => Contact.Kind;
    public int Devices => Contact.Devices;
    public string Names => string.Join(" / ", Contact.Sources.Select(s => s.Name).Where(n => n != "").Distinct(StringComparer.OrdinalIgnoreCase));
    public string On => string.Join(", ", Sources.Select(s => s.Device).Distinct());
}

/// <summary>Identifiers found on more than one device (phone numbers, e-mail addresses, WhatsApp JIDs/LIDs/groups, app
/// account IDs and usernames), with every record each was found in. Phone numbers are compared with or without their
/// country code. The devices, the artifacts the identifiers come from, their type and a text filter narrow the list.</summary>
public partial class CommonContactsViewModel : ObservableObject
{
    const string AllKinds = "All types";
    readonly MainViewModel _main;
    readonly IReadOnlyList<Device> _devices;
    IReadOnlyList<CommonContact> _all = [];

    public ObservableCollection<CommonContactRow> Rows { get; } = [];
    public IReadOnlyList<DeviceChoice> DeviceChoices { get; }
    /// <summary>The artifacts the identifiers were found in, each used or not, with the number of records.</summary>
    public ObservableCollection<ActivityChoice> Sources { get; } = [];
    public System.ComponentModel.ICollectionView SourcesView { get; }
    public ObservableCollection<string> Kinds { get; } = [AllKinds];
    [ObservableProperty] private string kind = AllKinds;
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private string sourceFilter = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))] private bool running;
    [ObservableProperty] private string info = "Run compares the contacts, calls, messages and app accounts of every device.";
    [ObservableProperty] private CommonContactRow? selected;
    [ObservableProperty] private ContactSourceRow? selectedSource;

    public string SourcesText => Sources.Count == 0 ? "All sources" : Sources.Count(s => s.IsChecked) is var n && n == Sources.Count ? "All sources" : $"{n} of {Sources.Count} sources";

    public CommonContactsViewModel(MainViewModel main, IReadOnlyList<Device> devices)
    {
        _main = main;
        _devices = [.. devices];
        DeviceChoices = [.. _devices.Select(d => new DeviceChoice(d, Apply))];
        SourcesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Sources);
        SourcesView.Filter = o => SourceFilter == "" || ((ActivityChoice)o).Name.Contains(SourceFilter, StringComparison.OrdinalIgnoreCase);
    }

    bool CanRun() => !Running;

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Run()
    {
        Running = true;
        Rows.Clear();
        Info = "Comparing the contacts, calls, messages and app accounts of every device…";
        try
        {
            var sessions = _devices.Select(d => d.Session).ToList();
            _all = await Task.Run(() => CrossDevice.CommonContacts(sessions));
            _bulk = true;
            Sources.Clear();
            foreach (var g in _all.SelectMany(c => c.Sources).GroupBy(s => s.Artifact.Name).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                Sources.Add(new ActivityChoice(g.Key, g.Count(), SourceToggled));
            Kinds.Clear();
            foreach (var k in (string[])[AllKinds, .. _all.Select(c => c.Kind).Distinct().Order()]) Kinds.Add(k);
            Kind = AllKinds;
            _bulk = false;
            OnPropertyChanged(nameof(SourcesText));
            Apply();
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { Info = "Cannot compare: " + e.Message; }
        finally { Running = false; }
    }

    /// <summary>The identifiers still on more than one device once the unchecked devices and sources, the type and the
    /// text filter are applied; each keeps only the records that pass.</summary>
    void Apply()
    {
        if (_bulk) return;
        Rows.Clear();
        var devs = Enumerable.Range(0, _devices.Count).Where(i => DeviceChoices[i].IsChecked).ToHashSet();
        var arts = Sources.Where(s => s.IsChecked).Select(s => s.Name).ToHashSet();
        var text = Filter.Trim();
        bool Has(string v) => v.Contains(text, StringComparison.OrdinalIgnoreCase);
        var shown = new List<CommonContact>();
        foreach (var c in _all)
        {
            if (Kind != AllKinds && c.Kind != Kind) continue;
            var kept = c with { Sources = [.. c.Sources.Where(s => devs.Contains(s.Device) && arts.Contains(s.Artifact.Name))] };
            if (kept.Devices < 2) continue;
            if (text != "" && !Has(kept.Key) && !Has(kept.Kind) && !kept.Sources.Any(s => Has(s.Value) || Has(s.Name) || Has(s.Artifact.Name))) continue;
            shown.Add(kept);
            Rows.Add(new CommonContactRow(kept, kept.Sources.Select(s => new ContactSourceRow(_devices[s.Device].Label, _devices[s.Device].Color, s)).ToList()));
        }
        Info = _all.Count == 0 ? "No identifier is on more than one device."
            : $"{shown.Count:N0} of {_all.Count:N0} identifiers on more than one device" +
              (shown.Count == 0 ? "" : ": " + string.Join(", ", shown.GroupBy(c => c.Kind).Select(g => $"{g.Count():N0} {g.Key}"))) +
              ". Phone numbers match on their last 9 digits, with or without the country code; WhatsApp JIDs count as their number; " +
              "app IDs and usernames are compared only within the same app.";
    }

    bool _bulk;
    void SourceToggled()
    {
        if (_bulk) return;
        OnPropertyChanged(nameof(SourcesText));
        Apply();
    }

    /// <summary>Checks or clears every source the picker lists (all of them when its search is empty).</summary>
    [RelayCommand]
    void CheckSources(string? on)
    {
        _bulk = true;
        foreach (ActivityChoice x in SourcesView) x.IsChecked = on == "True";
        _bulk = false;
        SourceToggled();
    }

    partial void OnKindChanged(string value) => Apply();
    partial void OnFilterChanged(string value) => Apply();
    partial void OnSourceFilterChanged(string value) => SourcesView.Refresh();

    partial void OnSelectedSourceChanged(ContactSourceRow? value)
    {
        if (value is not null && _main.SessionOf(value.Source.Artifact) is { } s) _main.Details.ShowRecord(s, value.Source.Artifact, value.Source.RowId);
    }

    [RelayCommand]
    void Open(ContactSourceRow? row)
    {
        if ((row ?? SelectedSource) is { } r) _main.NavigateTo(r.Source.Artifact, null, r.Source.RowId);
    }
}

/// <summary>A line under a zone: a visit of a device, or a period of devices there together (Label empty: one device).</summary>
public sealed record ZoneLine(string Label, string Color, string Text, bool Together = false);

/// <summary>A zone of interest on the map: its visits, one line each, and the periods in which more than one device was
/// recorded there. Records near each other in a zone are not proof the devices or their users met.</summary>
public sealed record ZoneRow(Zone Zone, string Summary, IReadOnlyList<ZoneLine> Lines)
{
    public string Title => $"Zone {Zone.Number}";
    public string Position => FormattableString.Invariant($"{Zone.Lat:0.000000}, {Zone.Lon:0.000000}");
    public long First => Zone.Visits.Min(v => v.From);
    public long Last => Zone.Visits.Max(v => v.To);
    public string FirstSeen => Time(First);
    public string LastSeen => Time(Last);
    public int Visits => Zone.Visits.Count;
    public int Positions => Zone.Positions;
    public int Together => Zone.Overlaps.Count;
    /// <summary>The devices recorded in the zone, on the joint map; else empty.</summary>
    public string Devices { get; init; } = "";

    static string Time(long epoch) => ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(epoch));
    static string Span(long from, long to) => from == to ? $"At {Time(from)}" : $"From {Time(from)} to {Time(to)}";

    public static ZoneRow Of(Zone z, IReadOnlyList<Device> devices)
    {
        var joint = devices.Count > 1;
        var lines = new List<ZoneLine>();
        foreach (var o in z.Overlaps)
            lines.Add(new ZoneLine(string.Join(" + ", o.Devices.Select(d => devices[d].Label)), "#444", "Together · " + Span(o.From, o.To), true));
        foreach (var v in z.Visits)
            lines.Add(new ZoneLine(joint ? devices[v.Device].Label : "", joint ? devices[v.Device].Color : "Transparent",
                $"{Span(v.From, v.To)} ({v.Positions:N0} position{(v.Positions == 1 ? "" : "s")})"));
        var summary = $"First {Time(z.Visits.Min(v => v.From))} · last {Time(z.Visits.Max(v => v.To))}\n" +
                      $"{z.Visits.Count:N0} visit{(z.Visits.Count == 1 ? "" : "s")} · {z.Positions:N0} positions" +
                      (joint ? $" · {string.Join(", ", z.Devices.Select(d => devices[d].Label))}" : "") +
                      (z.Overlaps.Count > 0 ? $" · together {z.Overlaps.Count:N0}×" : "");
        return new ZoneRow(z, summary, lines) { Devices = joint ? string.Join(", ", z.Devices.Select(d => devices[d].Label)) : "" };
    }
}
