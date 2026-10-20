using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>Plays an audio or video file: play, pause, stop, a time bar to seek, position and length. Collapsed when
/// the file is not a playable one present on disk. Compact: the controls only (an audio file in a chat bubble).</summary>
public sealed class PlayerView : UserControl
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(nameof(Media), typeof(MediaRef), typeof(PlayerView),
        new PropertyMetadata(null, (d, _) => ((PlayerView)d).Load()));
    public MediaRef? Media { get => (MediaRef?)GetValue(MediaProperty); set => SetValue(MediaProperty, value); }

    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(nameof(VideoHeight), typeof(double), typeof(PlayerView),
        new PropertyMetadata(double.PositiveInfinity));
    public double VideoHeight { get => (double)GetValue(VideoHeightProperty); set => SetValue(VideoHeightProperty, value); }

    /// <summary>Plays audio only (a chat bubble: a video there opens in its window instead).</summary>
    public bool AudioOnly { get; set; }

    readonly MediaElement _me = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true };
    readonly Slider _seek = new() { Minimum = 0, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80, Margin = new Thickness(6, 0, 6, 0) };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Text = "0:00" };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _moving; // the bar follows the playback: not a seek

    public PlayerView()
    {
        _me.SetBinding(MaxHeightProperty, new System.Windows.Data.Binding(nameof(VideoHeight)) { Source = this });
        Button B(string glyph, string tip, Action act)
        {
            var b = new Button { Style = (Style)FindResource("FlatButton"), ToolTip = tip, Focusable = false,
                Content = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph } };
            b.Click += (_, _) => act();
            return b;
        }
        var bar = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(B("", "Play", () => { if (Open()) { _me.Play(); _tick.Start(); } }));
        buttons.Children.Add(B("", "Pause", () => _me.Pause()));
        buttons.Children.Add(B("", "Stop", () => { _me.Stop(); Show(); }));
        DockPanel.SetDock(buttons, Dock.Left);
        DockPanel.SetDock(_time, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_time);
        bar.Children.Add(_seek);
        var root = new StackPanel();
        root.Children.Add(_me);
        root.Children.Add(bar);
        Content = root;
        _me.MediaOpened += (_, _) =>
        {
            _seek.Maximum = _me.NaturalDuration.HasTimeSpan ? _me.NaturalDuration.TimeSpan.TotalSeconds : 0;
            Show();
        };
        _me.MediaEnded += (_, _) => { _me.Stop(); _tick.Stop(); Show(); };
        _me.MediaFailed += (_, e) => _time.Text = "Cannot play: " + e.ErrorException.Message;
        _tick.Tick += (_, _) => Show();
        _seek.ValueChanged += (_, e) => { if (!_moving) { _me.Position = TimeSpan.FromSeconds(e.NewValue); Show(); } };
        Unloaded += (_, _) => { _tick.Stop(); _me.Close(); };
        Visibility = Visibility.Collapsed;
    }

    static string T(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Show()
    {
        _moving = true;
        _seek.Value = Math.Min(_seek.Maximum, _me.Position.TotalSeconds);
        _moving = false;
        _time.Text = $"{T(_me.Position)} / {(_me.NaturalDuration.HasTimeSpan ? T(_me.NaturalDuration.TimeSpan) : "?")}";
    }

    void Load()
    {
        _tick.Stop();
        _seek.Maximum = 0;
        _time.Text = "0:00";
        _me.Source = null;
        var m = Media;
        var playable = m is { Exists: true } && (m.Kind == "audio" || m.Kind == "video" && !AudioOnly);
        _me.Visibility = m?.Kind == "video" ? Visibility.Visible : Visibility.Collapsed;
        Visibility = playable ? Visibility.Visible : Visibility.Collapsed;
        if (playable && m!.Kind == "video" && Open()) { _me.Play(); _me.Pause(); } // shows the first frame and the length
    }

    /// <summary>The file opened in the player, on first play for audio: a portable case's is written out of the zip then.</summary>
    bool Open()
    {
        if (_me.Source is not null) return true;
        try { _me.Source = Media is { } m ? new Uri(m.LocalFile()) : null; }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { _time.Text = "Cannot read the file: " + e.Message; }
        return _me.Source is not null;
    }
}
