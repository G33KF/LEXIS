using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace AleappReader.App.Views;

/// <summary>Leaflet in WebView2. Case data reaches the page only as JSON and is rendered with textContent, never as HTML.</summary>
public partial class MapView : UserControl
{
    // ponytail: Leaflet comes from unpkg (version-pinned, no SRI); vendor it beside the exe for fully offline use.
    const string Html = """
        <!doctype html><html><head><meta charset="utf-8">
        <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css">
        <link rel="stylesheet" href="https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.css">
        <link rel="stylesheet" href="https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.Default.css">
        <style>html,body,#m{height:100%;margin:0;background:#eef1f5;font:12px Segoe UI}.lbl{white-space:pre-wrap;max-width:280px}
        .th{position:relative;width:40px;height:40px;box-sizing:border-box;border:2px solid var(--c);border-radius:4px;background:#fff;overflow:hidden}
        .th img{width:100%;height:100%;object-fit:cover;display:block}
        .th.none{border:none;background:none;overflow:visible}
        .th.none::after{content:'';position:absolute;left:13px;top:13px;width:10px;height:10px;border:2px solid var(--c);border-radius:7px;background:#fff}
        .num{background:none;border:none;box-shadow:none;padding:0;font-weight:600;color:#222}.num::before{display:none}</style>
        <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
        <script src="https://unpkg.com/leaflet.markercluster@1.5.3/dist/leaflet.markercluster.js"></script>
        </head><body><div id="m"></div><script>
        const map = L.map('m', {preferCanvas:true}).setView([20,0], 2);
        const tiles = L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom:19, attribution:'&copy; OpenStreetMap contributors'});
        let layer = null;
        const colors = ['#1f7ae0','#e8590c','#2b8a3e','#ae3ec9','#c92a2a','#0c8599','#e67700','#5f3dc4'];
        let focusMk = null;
        let routeLayer = null, tracks = [];
        // a position from a picture or video shows the picture (served from memory by the app); a dot when it has none
        function thumb(src, media, color, at) {
          const box = document.createElement('div'); box.className = 'th'; box.style.setProperty('--c', color);
          const img = document.createElement('img');
          img.onerror = () => { img.remove(); box.className = 'th none'; };
          img.src = 'https://aleappreader.thumb/' + src + '/' + encodeURIComponent(media);
          box.appendChild(img);
          return L.marker(at, {icon: L.divIcon({html: box, className: '', iconSize: [40, 40]})});
        }
        // the route: positions in time order, per device; those closer than ~30 px at this zoom make one stop, so a
        // zoomed-out map shows few stops and a zoomed-in one each position
        function drawRoute() {
          if (routeLayer) { map.removeLayer(routeLayer); routeLayer = null; }
          if (!tracks.length) return;
          routeLayer = L.layerGroup();
          for (const t of tracks) {
            const stops = [];
            for (const [lat, lon, time] of t.pts) {
              const px = map.latLngToLayerPoint([lat, lon]), last = stops[stops.length - 1];
              if (last && last.px.distanceTo(px) < 30) { last.to = time; last.n++; }
              else stops.push({lat, lon, px, from: time, to: time, n: 1});
            }
            L.polyline(stops.map(s => [s.lat, s.lon]), {color: t.color, weight: 3, opacity: 0.75}).addTo(routeLayer);
            stops.forEach((s, i) => {
              const mk = L.circleMarker([s.lat, s.lon], {radius: 9, color: t.color, weight: 2, fillColor: '#fff', fillOpacity: 1});
              const div = document.createElement('div'); div.className = 'lbl';
              div.textContent = (t.name ? t.name + '\n' : '') + 'Stop ' + (i + 1) + (s.n > 1 ? ' (' + s.n + ' positions)' : '') +
                '\n' + (s.from === s.to ? s.from : 'From ' + s.from + '\nTo ' + s.to);
              mk.bindPopup(div);
              if (stops.length <= 400) mk.bindTooltip(String(i + 1), {permanent: true, direction: 'center', className: 'num'});
              mk.addTo(routeLayer);
            });
          }
          routeLayer.addTo(map);
        }
        map.on('zoomend', drawRoute);
        // zones of interest: a circle of the radius set, numbered, darker where devices were there together
        let zoneLayer = null, zonesShown = false; // while zones are drawn, a re-render keeps the view where the examiner is
        function drawZones(zones) {
          if (zoneLayer) { map.removeLayer(zoneLayer); zoneLayer = null; }
          if (!zones.length) return;
          zoneLayer = L.layerGroup();
          for (const [lat, lon, radius, n, text, together] of zones) {
            const c = L.circle([lat, lon], {radius, color: together ? '#c92a2a' : '#5f3dc4', weight: 2, fillOpacity: together ? 0.25 : 0.12});
            const div = document.createElement('div'); div.className = 'lbl'; div.textContent = text;
            c.bindPopup(div);
            c.on('click', () => window.chrome.webview.postMessage({zone: n}));
            c.bindTooltip(String(n), {permanent: zones.length <= 300, direction: 'center', className: 'num'});
            c.addTo(zoneLayer);
          }
          zoneLayer.addTo(map);
        }
        function focusZone(lat, lon, radius) { map.fitBounds(L.latLng(lat, lon).toBounds(radius * 3), {maxZoom: 18}); }
        function render(points, online, focus, route, zones) {
          if (online) tiles.addTo(map); else tiles.remove();
          if (layer) map.removeLayer(layer);
          // a group of positions at one spot (or still grouped at the closest zoom) is listed beside the map, with when
          // each was recorded, instead of spreading into dots that look like further places
          layer = L.markerClusterGroup({chunkedLoading:true, zoomToBoundsOnClick:false, spiderfyOnMaxZoom:false});
          layer.on('clusterclick', e => {
            const b = e.layer.getBounds();
            if (b.getNorthEast().distanceTo(b.getSouthWest()) < 5 || map.getZoom() >= 19)
              window.chrome.webview.postMessage({spot: e.layer.getAllChildMarkers().map(m => m.sr)});
            else e.layer.zoomToBounds({padding:[20,20], maxZoom:19});
          });
          const byDevice = new Map();
          if (route)
            for (const [lat, lon, src, row, label, col, media, epoch, time, dev] of points)
              if (epoch !== null) {
                if (!byDevice.has(dev)) byDevice.set(dev, {name: dev, color: col || '#c92a2a', all: []});
                byDevice.get(dev).all.push([lat, lon, time, epoch]);
              }
          tracks = [...byDevice.values()].map(t => ({name: t.name, color: t.color,
            pts: t.all.sort((a, b) => a[3] - b[3]).filter((p, i, a) => i === 0 || p[0] !== a[i - 1][0] || p[1] !== a[i - 1][1])}));
          for (const [lat, lon, src, row, label, col, media] of points) {
            const color = col || colors[src % colors.length];
            const mk = media ? thumb(src, media, color, [lat, lon])
                             : L.circleMarker([lat, lon], {radius:6, color, weight:2, fillOpacity:0.6});
            const div = document.createElement('div'); div.className = 'lbl'; div.textContent = label;
            mk.bindPopup(div);
            mk.sr = [src, row];
            mk.on('click', () => window.chrome.webview.postMessage({s:src, r:row}));
            layer.addLayer(mk);
          }
          map.addLayer(layer);
          if (focusMk) { map.removeLayer(focusMk); focusMk = null; }
          if (focus) { focusMk = L.circleMarker(focus, {radius:12, color:'#c92a2a', weight:3, fill:false}).addTo(map); map.setView(focus, 16); }
          else if (points.length && !zonesShown) map.fitBounds(layer.getBounds(), {maxZoom:15, padding:[20,20]});
          zonesShown = zones.length > 0;
          drawRoute();
          drawZones(zones);
        }
        window.chrome.webview.postMessage({ready:true});
        </script></body></html>
        """;

    const string Host = "aleappreader.map", ThumbHost = "aleappreader.thumb";
    MapViewModel? _vm;
    bool _ready;

    public MapView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) { _vm.PointsChanged -= Render; _vm.ZoneFocused -= FocusZone; _vm.PropertyChanged -= OnVmChanged; }
            _zonesWindow?.Close();
            _vm = e.NewValue as MapViewModel;
            if (_vm is not null) { _vm.PointsChanged += Render; _vm.ZoneFocused += FocusZone; _vm.PropertyChanged += OnVmChanged; }
            if (_vm is { IsCoPresence: true }) Dispatcher.BeginInvoke(ShowZones, System.Windows.Threading.DispatcherPriority.Loaded);
            Render();
        };
        Loaded += async (_, _) => await InitAsync();
        Unloaded += (_, _) => _zonesWindow?.Close();
    }

    async Task InitAsync()
    {
        if (Web.CoreWebView2 is not null) return;
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "WebView2");
            await Web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
            var core = Web.CoreWebView2!;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, a) => a.Handled = true; // no popups out of the map
            core.WebMessageReceived += OnMessage;
            // marker pictures: answered from memory, never fetched from anywhere (the host does not exist)
            core.AddWebResourceRequestedFilter($"https://{ThumbHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnThumbnail;
            // Served from a virtual https host, not NavigateToString: tile servers refuse requests without a Referer
            // (OpenStreetMap answers "Access blocked"), and a data: page sends none.
            var dir = Path.Combine(data, "..", "map");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "map.html"), Html);
            core.SetVirtualHostNameToFolderMapping(Host, dir, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate($"https://{Host}/map.html");
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Web.Visibility = Visibility.Collapsed;
            WebError.Visibility = Visibility.Visible;
            WebError.Text = "The map needs the Microsoft Edge WebView2 runtime: " + e.Message;
        }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("ready", out _)) { _ready = true; Render(); return; }
        if (root.TryGetProperty("spot", out var spot) && spot.ValueKind == JsonValueKind.Array)
        {
            _vm?.ShowSpot([.. spot.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() == 2
                    && x[0].TryGetInt32(out _) && x[1].TryGetInt64(out _)).Select(x => (x[0].GetInt32(), x[1].GetInt64()))]);
            return;
        }
        if (root.TryGetProperty("zone", out var zone) && zone.TryGetInt32(out var n) && _vm?.ZoneRows.FirstOrDefault(z => z.Zone.Number == n) is { } picked)
        {
            ShowZones();
            _vm.SelectedZone = picked;
            return;
        }
        if (root.TryGetProperty("s", out var s) && root.TryGetProperty("r", out var r) &&
            s.TryGetInt32(out var src) && r.TryGetInt64(out var row))
            _vm?.SelectPoint(src, row);
    }

    async void OnThumbnail(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var (core, vm) = (Web.CoreWebView2, _vm);
        var deferral = e.GetDeferral();
        byte[]? png = null;
        try
        {
            var parts = new Uri(e.Request.Uri).AbsolutePath.Trim('/').Split('/', 2);
            if (vm is not null && parts.Length == 2 && int.TryParse(parts[0], out var src))
                png = await Task.Run(() => vm.Thumbnail(src, Uri.UnescapeDataString(parts[1])));
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                      or COMException or UriFormatException or Microsoft.Data.Sqlite.SqliteException) { }
        e.Response = png is null ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
            : core.Environment.CreateWebResourceResponse(new MemoryStream(png), 200, "OK", "Content-Type: image/png");
        deferral.Complete();
    }

    void Render()
    {
        if (!_ready || _vm is null || Web.CoreWebView2 is null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"render({_vm.VisiblePointsJson()}, {(_vm.OnlineTiles ? "true" : "false")}, {_vm.FocusJson()}, {(_vm.Route ? "true" : "false")}, {_vm.ZonesJson()})");
    }

    ZonesWindow? _zonesWindow;

    /// <summary>The zones' window opens when the zones are turned on, and closes with them.</summary>
    void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapViewModel.ShowZones)) return;
        if (_vm!.ShowZones) ShowZones(); else _zonesWindow?.Close();
    }

    void ShowZones()
    {
        if (_vm is null) return;
        if (_zonesWindow is null)
        {
            _zonesWindow = new ZonesWindow(_vm, Window.GetWindow(this));
            _zonesWindow.Closed += (_, _) => _zonesWindow = null;
            _zonesWindow.Show();
        }
        else _zonesWindow.Activate();
    }

    void ZonesList_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is { ShowZones: false }) _vm.ShowZones = true; else ShowZones();
    }

    void Splitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => ZonesPanel.MaxHeight = double.PositiveInfinity;

    void FocusZone(Core.Zone z)
    {
        if (!_ready || _vm is null || Web.CoreWebView2 is null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(FormattableString.Invariant($"focusZone({z.Lat}, {z.Lon}, {_vm.ZoneRadius})"));
    }
}
