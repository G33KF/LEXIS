using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AleappReader.App.ViewModels;
using AleappReader.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AleappReader.App.Views;

/// <summary>One media file at full size: a picture to zoom (wheel, buttons, double-click) and pan (drag), a video or
/// audio with its player, a PDF in the sandboxed browser; any other type is offered to its default program.</summary>
public sealed class MediaViewer : UserControl
{
    readonly MediaRef _m;
    readonly DockPanel _root = new();
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    readonly Image _img = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll };
    readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, Margin = new Thickness(6, 0, 0, 0) };
    double _scale = 1;
    bool _fit = true; // the picture follows the window until zoomed
    Point? _drag;
    (double H, double V) _from;

    public MediaViewer(MediaRef m)
    {
        _m = m;
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Style = (Style)FindResource("FlatButton"), Content = text, ToolTip = tip, Focusable = false, Margin = new Thickness(2, 0, 0, 0) };
            b.Click += (_, _) => act();
            return b;
        }
        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        var files = new StackPanel { Orientation = Orientation.Horizontal, IsEnabled = m.Exists };
        files.Children.Add(B("Open with default program", "Opens the file in its program on this computer (programs and scripts are only shown in the folder)", () => DetailsViewModel.OpenMedia(m)));
        files.Children.Add(B("Show in folder", "Shows the file in Explorer", () => DetailsViewModel.ShowInFolder(m)));
        DockPanel.SetDock(files, Dock.Right);
        bar.Children.Add(files);
        DockPanel.SetDock(bar, Dock.Top);
        _root.Children.Add(bar);
        Content = _root;
        if (!m.Exists) { Note("Media file not present in the report"); return; }
        if (m.IsImage)
        {
            var zoom = new StackPanel { Orientation = Orientation.Horizontal };
            zoom.Children.Add(B("−", "Zoom out (mouse wheel)", () => Zoom(0.8, Center())));
            zoom.Children.Add(B("+", "Zoom in (mouse wheel)", () => Zoom(1.25, Center())));
            zoom.Children.Add(B("Fit", "Whole picture in the window (double-click)", Fit));
            zoom.Children.Add(B("100%", "Actual size (double-click)", () => { _fit = false; Apply(1); }));
            zoom.Children.Add(_zoom);
            bar.Children.Add(zoom);
            _scroll.Content = _img;
            _scroll.Background = (Brush)FindResource("PanelBrush");
            _root.Children.Add(_scroll);
            _scroll.SizeChanged += (_, _) => { if (_fit) Fit(); };
            _scroll.PreviewMouseWheel += (_, e) => { e.Handled = true; Zoom(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(_scroll)); };
            _img.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) { if (_fit) { _fit = false; Apply(1); } else Fit(); return; }
                _drag = e.GetPosition(_scroll);
                _from = (_scroll.HorizontalOffset, _scroll.VerticalOffset);
                _img.CaptureMouse();
            };
            _img.MouseMove += (_, e) =>
            {
                if (_drag is not { } d) return;
                var p = e.GetPosition(_scroll);
                _scroll.ScrollToHorizontalOffset(_from.H - (p.X - d.X));
                _scroll.ScrollToVerticalOffset(_from.V - (p.Y - d.Y));
            };
            _img.MouseLeftButtonUp += (_, _) => { _drag = null; _img.ReleaseMouseCapture(); };
            LoadImage();
        }
        else if (m.Kind is "video" or "audio")
        {
            var player = new PlayerView { Media = m, Margin = new Thickness(8, 0, 8, 8) };
            _root.Children.Add(player);
            _root.SizeChanged += (_, _) => player.VideoHeight = Math.Max(120, _root.ActualHeight - 90);
        }
        else if (DetailsViewModel.IsPdf(m))
        {
            var web = new WebView2();
            _root.Children.Add(web);
            web.Loaded += async (_, _) => { if (web.CoreWebView2 is null) await ShowPdf(web); };
            Unloaded += (_, _) => web.Dispose();
        }
        else Note($"LEXIS cannot show {(Path.GetExtension(m.FullPath ?? m.Name) is { Length: > 0 } x ? x : "this type of")} files: “Open with default program” opens it in its program on this computer.");
    }

    void Note(string text) => _root.Children.Add(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(16),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    });

    async void LoadImage()
    {
        _zoom.Text = "Loading…";
        var src = await Task.Run(() => FullSize(_m));
        if (src is null)
        {
            _root.Children.Remove(_scroll);
            _zoom.Text = "";
            Note("This picture cannot be decoded here (a HEIC needs the HEIF extension from the Microsoft Store): “Open with default program”.");
            return;
        }
        _img.Source = src;
        Fit();
    }

    /// <summary>Decoded at its own size, up to 8192 pixels wide (a 200 MP photo would take ~800 MB in memory).</summary>
    static ImageSource? FullSize(MediaRef m)
    {
        var width = 0;
        try
        {
            using var s = m.OpenRead();
            width = BitmapDecoder.Create(s, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException
                                      or UnauthorizedAccessException or COMException) { }
        return GalleryViewModel.Thumb(m, Math.Min(width, 8192));
    }

    void Apply(double scale)
    {
        if (_img.Source is not BitmapSource b) return;
        _scale = Math.Clamp(scale, 0.02, 16);
        (_img.Width, _img.Height) = (b.PixelWidth * _scale, b.PixelHeight * _scale);
        _zoom.Text = $"{_scale * 100:0}%";
    }

    /// <summary>The whole picture in the window, never enlarged past its size.</summary>
    void Fit()
    {
        _fit = true;
        if (_img.Source is BitmapSource b && _scroll.ActualWidth > 0)
            Apply(Math.Min(1, Math.Min((_scroll.ActualWidth - 4) / b.PixelWidth, (_scroll.ActualHeight - 4) / b.PixelHeight)));
    }

    Point Center() => new(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);

    /// <summary>Zooms keeping the point <paramref name="at"/> (in the viewer) on the same spot of the picture.</summary>
    void Zoom(double factor, Point at)
    {
        if (_img.Source is null) return;
        var (x, y) = ((_scroll.HorizontalOffset + at.X) / _scale, (_scroll.VerticalOffset + at.Y) / _scale);
        _fit = false;
        Apply(_scale * factor);
        _scroll.UpdateLayout();
        _scroll.ScrollToHorizontalOffset(x * _scale - at.X);
        _scroll.ScrollToVerticalOffset(y * _scale - at.Y);
    }

    async Task ShowPdf(WebView2 web)
    {
        try
        {
            var uri = new Uri(await Task.Run(_m.LocalFile)).AbsoluteUri;
            await Sandbox(web, u => u == uri);
            web.CoreWebView2!.Navigate(uri);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException
                                      or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _root.Children.Remove(web);
            Note($"The PDF cannot be shown here ({e.Message}): “Open with default program”.");
        }
    }

    /// <summary>A browser for evidence, not the web: scripts, menus and dev tools off, no new windows, and every
    /// request but those <paramref name="allowed"/> answered locally with 403, so nothing ever leaves the computer.</summary>
    public static async Task Sandbox(WebView2 view, Func<string, bool> allowed)
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "WebView2");
        await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
        var core = view.CoreWebView2!;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.NavigationStarting += (_, a) => a.Cancel = !allowed(a.Uri);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, a) =>
        {
            if (!allowed(a.Request.Uri)) a.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
    }
}
