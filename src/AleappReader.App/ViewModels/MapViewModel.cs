using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>One layer of the map: an artifact's positions, on one device; <see cref="Color"/> is the device's colour
/// on the joint map (null: the page picks one per source).</summary>
public partial class MapSource(ArtifactInfo artifact, int count, CaseSession session, string device = "", string? color = null) : ObservableObject
{
    public ArtifactInfo Artifact { get; } = artifact;
    public int Count { get; } = count;
    public CaseSession Session { get; } = session;
    public string Title => Device == "" ? Artifact.Name : $"{Device} · {Artifact.Name}";
    public string Device { get; } = device;
    public string? Color { get; } = color;
    public string Swatch => Color ?? "Transparent";
    [ObservableProperty] private bool isVisible = true;
}

/// <summary>A position listed for a spot on the map.</summary>
public sealed record SpotRow(int Src, long RowId, string When, string Source, string Label);

/// <summary>Every coordinate pair found in the case, grouped by source artifact (and by device on the joint map). The
/// view renders it with Leaflet.</summary>
public partial class MapViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly List<(GeoPoint P, int Src)> _points = [];
    readonly HashSet<(int, string)> _media = [];
    readonly List<int> _deviceOf = []; // per source, its device
    List<Device> _deviceList = [];
    bool _joint;
    int _devices;

    public ObservableCollection<MapSource> Sources { get; } = [];
    public bool OnlineTiles
    {
        get => Settings.Current.OnlineMapTiles;
        set { Settings.Current.OnlineMapTiles = value; Settings.Current.Save(); OnPropertyChanged(); PointsChanged?.Invoke(); }
    }
    [ObservableProperty] private string info = "Loading locations…";
    public string Title { get; }

    /// <summary>The period shown (calendar days in the display zone): positions without a time are left out while one is set.</summary>
    [ObservableProperty] private DateTime? from;
    [ObservableProperty] private DateTime? to;
    /// <summary>Joins the positions in time order, per device, into a numbered route of stops.</summary>
    [ObservableProperty] private bool route;
    partial void OnFromChanged(DateTime? value) => Changed();
    partial void OnToChanged(DateTime? value) => Changed();
    partial void OnRouteChanged(bool value) => PointsChanged?.Invoke();

    /// <summary>The points shown changed: the page re-renders, and the zones are found again on them.</summary>
    void Changed()
    {
        PointsChanged?.Invoke();
        if (ShowZones) _ = FindZonesAsync();
    }

    // ---- zones of interest: the dated positions shown, in circles of the radius set, drawn on the map ----

    const int MaxZones = 1000;
    CancellationTokenSource _zoneCts = new();
    IReadOnlyList<Zone> _zones = [];
    double _zoneRadius;
    public double ZoneRadius => _zoneRadius;

    /// <summary>Draws the zones of interest of the positions shown (sources ticked, period): circles of <see cref="Radius"/>
    /// metres, each with its visits and, across devices, the periods in which more than one device was there.</summary>
    [ObservableProperty] private bool showZones;
    [ObservableProperty] private double radius = 200;
    [ObservableProperty] private double gapMinutes = 60;
    [ObservableProperty] private double togetherMinutes = 10;
    [ObservableProperty] private bool onlyShared;
    [ObservableProperty] private string zonesInfo = "";
    [ObservableProperty] private ZoneRow? selectedZone;
    public ObservableCollection<ZoneRow> ZoneRows { get; } = [];
    public bool Joint => _joint;
    /// <summary>Raised to centre the map on a zone.</summary>
    public event Action<Zone>? ZoneFocused;

    partial void OnShowZonesChanged(bool value) { if (value) _ = FindZonesAsync(); else { _zoneCts.Cancel(); _zones = []; ZoneRows.Clear(); ZonesInfo = ""; PointsChanged?.Invoke(); } }
    partial void OnOnlySharedChanged(bool value) { if (ShowZones) _ = FindZonesAsync(); }
    partial void OnSelectedZoneChanged(ZoneRow? value) { if (value is not null) ZoneFocused?.Invoke(value.Zone); }

    [RelayCommand]
    Task FindZones() => ShowZones ? FindZonesAsync() : Task.CompletedTask;

    async Task FindZonesAsync()
    {
        if (IsCoPresence) return; // the co-presences are the zones, found by their own view
        if (Radius <= 0 || GapMinutes < 0 || TogetherMinutes < 0) { ZonesInfo = "The radius must be above 0, the times 0 or more."; return; }
        _zoneCts.Cancel();
        var cts = _zoneCts = new CancellationTokenSource();
        var (r, gap, tog) = (Radius, TimeSpan.FromMinutes(GapMinutes), TimeSpan.FromMinutes(TogetherMinutes));
        var perDevice = Enumerable.Range(0, Math.Max(1, _deviceList.Count)).Select(_ => new List<GeoPoint>()).ToList();
        foreach (var (p, src) in Shown()) if (p.Epoch is not null) perDevice[_deviceOf[src]].Add(p);
        ZonesInfo = "Finding the zones…";
        try
        {
            var zones = await Task.Run(() => Zones.Find([.. perDevice], r, gap, tog, ct: cts.Token), cts.Token);
            var shown = zones.Where(z => !OnlyShared || z.Devices.Count > 1).Take(MaxZones).ToList();
            (_zones, _zoneRadius) = (shown, r);
            ZoneRows.Clear();
            foreach (var z in shown) ZoneRows.Add(ZoneRow.Of(z, _deviceList));
            ZonesInfo = $"{zones.Count:N0} zones of {r:N0} m from {zones.Sum(z => z.Positions):N0} dated positions" +
                        (_joint ? $"; {zones.Count(z => z.Overlaps.Count > 0):N0} with devices there together" : "") +
                        (zones.Count > shown.Count ? $"; {shown.Count:N0} shown" : "") +
                        ". Numbered by the positions in them, most first. Positions near each other are not proof that devices or their users met.";
            PointsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>JSON for the page: [lat, lon, radius m, number, text, devices together].</summary>
    public string ZonesJson() => JsonSerializer.Serialize(_zones.Select(z => new object[] { z.Lat, z.Lon, _zoneRadius, z.Number,
        $"{(IsCoPresence ? "Co-presence" : "Zone")} {z.Number}\n" + ZoneRow.Of(z, _deviceList).Summary, z.Overlaps.Count > 0 }));

    [RelayCommand]
    void ClearPeriod() => (From, To) = (null, null);

    /// <summary>Raised when the visible point set changes; the view re-renders.</summary>
    public event Action? PointsChanged;

    /// <summary>The position the map opens on instead of fitting all points (e.g. a location shared in a chat).</summary>
    public GeoPoint? Focus { get; }

    public MapViewModel(MainViewModel main, CaseSession s, ArtifactInfo? only = null, GeoPoint? focus = null)
    {
        _main = main;
        Focus = focus;
        Title = "Locations";
        _ = LoadAsync([new Device(s, "", "")], only);
    }

    /// <summary>The joint map: one layer per device and artifact, each device in its colour.</summary>
    public MapViewModel(MainViewModel main, IReadOnlyList<Device> devices, GeoPoint? focus = null)
    {
        _main = main;
        Focus = focus;
        Title = "Joint Map";
        _ = LoadAsync([.. devices], null);
    }

    /// <summary>The co-presences on a map: only the positions in them, each co-presence a numbered circle listed as a zone.</summary>
    public MapViewModel(MainViewModel main, IReadOnlyList<Device> devices, IReadOnlyList<CoPresenceGroup> groups, double radius, GeoPoint? focus = null)
    {
        _main = main;
        Focus = focus;
        Title = "Co-Presence Map";
        IsCoPresence = true;
        var list = devices.ToList();
        Add(list, [.. groups.SelectMany(g => g.Points).GroupBy(p => (p.Device, p.Point.TableName))
            .Select(g => (D: list[g.Key.Device], A: list[g.Key.Device].Session.Manifest.Artifacts.FirstOrDefault(a => a.TableName == g.Key.TableName),
                          P: (IReadOnlyList<GeoPoint>)[.. g.Select(p => p.Point)]))
            .Where(x => x.A is not null).Select(x => (x.D, x.A!, x.P))]);
        (_zones, _zoneRadius) = ([.. groups.Select(g => g.ToZone())], radius);
        foreach (var z in _zones) ZoneRows.Add(ZoneRow.Of(z, _deviceList));
        ZonesInfo = $"{_zones.Count:N0} co-presences, circles of {radius:N0} m. Positions near each other are not proof that devices or their users met.";
        ShowZones = true;
    }

    /// <summary>The map of the co-presences found by the Co-presence view.</summary>
    public bool IsCoPresence { get; }

    public string FocusJson() => Focus is { } f ? JsonSerializer.Serialize(new[] { f.Lat, f.Lon }) : "null";

    async Task LoadAsync(List<Device> devices, ArtifactInfo? only)
    {
        var groups = await Task.Run(() => devices.SelectMany(d =>
        {
            using var con = d.Session.OpenDb();
            return (only is not null ? [only] : GeoExtractor.GeoArtifacts(d.Session.Manifest).ToList()).Select(a =>
            {
                try { return (d, a, GeoExtractor.Extract(con, a)); }
                catch (Microsoft.Data.Sqlite.SqliteException) { return (d, a, (IReadOnlyList<GeoPoint>)[]); }
            }).ToList();
        }).ToList());
        Add(devices, groups);
    }

    /// <summary>Adds a layer per device and artifact with positions, and shows them.</summary>
    void Add(List<Device> devices, List<(Device, ArtifactInfo, IReadOnlyList<GeoPoint>)> groups)
    {
        var joint = devices.Count > 1;
        _deviceList = devices;
        foreach (var (d, a, pts) in groups.Where(g => g.Item3.Count > 0))
        {
            _deviceOf.Add(devices.IndexOf(d));
            var src = joint ? new MapSource(a, pts.Count, d.Session, d.Label, d.Color) : new MapSource(a, pts.Count, d.Session);
            src.PropertyChanged += (_, _) => Changed();
            _points.AddRange(pts.Select(p => (p, Sources.Count)));
            foreach (var p in pts) if (p.Media is { } m) _media.Add((Sources.Count, m));
            Sources.Add(src);
        }
        (_joint, _devices) = (joint, devices.Count);
        OnPropertyChanged(nameof(Joint));
        Changed();
    }

    /// <summary>The positions shown: of the sources ticked, in the period (then only those with a time).</summary>
    List<(GeoPoint P, int Src)> Shown()
    {
        var (from, to) = ValueFormatter.Days(From, To);
        var period = from is not null || to is not null;
        return _points.Where(x => Sources[x.Src].IsVisible &&
            (!period || x.P.Epoch is { } e && e >= (from ?? long.MinValue) && e <= (to ?? long.MaxValue))).ToList();
    }

    /// <summary>JSON array for the page script: [lat, lon, sourceIndex, rowId, label, colour or null, media ref or null,
    /// epoch or null, time text, device].</summary>
    public string VisiblePointsJson()
    {
        var period = From is not null || To is not null;
        var shown = Shown();
        Info = $"{shown.Count:N0} of {_points.Count:N0} locations from {Sources.Count} sources" + (_joint ? $" on {_devices} devices" : "") +
               (period ? " in the period (positions without a time are left out)" : "");
        return JsonSerializer.Serialize(shown.Select(x => new object?[] { x.P.Lat, x.P.Lon, x.Src, x.P.RowId,
            $"{(Sources[x.Src].Device is { Length: > 0 } dev ? dev + "\n" : "")}{x.P.Artifact}\n{x.P.Time}\n{x.P.Label}".Trim(), Sources[x.Src].Color,
            x.P.Media, x.P.Epoch, x.P.Time, Sources[x.Src].Device }));
    }

    readonly Dictionary<(int, string), byte[]?> _thumbs = [];

    /// <summary>A position's picture as a small PNG for its marker, decoded in memory (nothing is written to disk but a
    /// video's passing temp copy, as in the gallery); only for a media reference of a point on this map. Null: no picture.</summary>
    public byte[]? Thumbnail(int src, string refId)
    {
        lock (_thumbs)
            if (_thumbs.TryGetValue((src, refId), out var known)) return known;
        byte[]? png = null;
        if (src >= 0 && src < Sources.Count && _media.Contains((src, refId)))
        {
            var s = Sources[src].Session;
            using var con = s.OpenDb();
            if (new MediaResolver(s).Get(con, refId) is { } m &&
                (m.IsImage ? GalleryViewModel.Thumb(s, m, 64) : m.Kind == "video" ? GalleryViewModel.VideoThumb(s, m, 64) : null) is BitmapSource bmp)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                png = ms.ToArray();
            }
        }
        lock (_thumbs) _thumbs[(src, refId)] = png;
        return png;
    }

    /// <summary>The positions of a group clicked on the map at one spot, in time order; empty when the list is closed.</summary>
    public ObservableCollection<SpotRow> Spot { get; } = [];
    [ObservableProperty] private string spotTitle = "";
    [ObservableProperty] private SpotRow? selectedSpot;
    partial void OnSelectedSpotChanged(SpotRow? value) { if (value is not null) SelectPoint(value.Src, value.RowId); }

    /// <summary>Lists the positions of a group at one spot: each with when it was recorded, its source and its text.</summary>
    public void ShowSpot(IReadOnlyList<(int Src, long Row)> keys)
    {
        var wanted = keys.ToHashSet();
        var rows = _points.Where(x => wanted.Contains((x.Src, x.P.RowId)))
            .OrderBy(x => x.P.Epoch ?? long.MaxValue)
            .Select(x => new SpotRow(x.Src, x.P.RowId, x.P.Time is { Length: > 0 } t ? t : "(no time)", Sources[x.Src].Title, x.P.Label))
            .ToList();
        Spot.Clear();
        foreach (var r in rows) Spot.Add(r);
        var at = _points.FirstOrDefault(x => wanted.Contains((x.Src, x.P.RowId))).P;
        SpotTitle = at is null ? "" : FormattableString.Invariant($"{rows.Count:N0} positions at {at.Lat:0.######}, {at.Lon:0.######}");
    }

    [RelayCommand]
    void CloseSpot() { Spot.Clear(); SpotTitle = ""; }

    public void SelectPoint(int sourceIndex, long rowId)
    {
        if (sourceIndex < 0 || sourceIndex >= Sources.Count) return;
        _main.Details.ShowRecord(Sources[sourceIndex].Session, Sources[sourceIndex].Artifact, rowId);
    }
}
