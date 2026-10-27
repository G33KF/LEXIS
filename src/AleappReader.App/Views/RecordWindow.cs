using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;
using AleappReader.Core;

namespace AleappReader.App.Views;

/// <summary>A record, or a media file, in its own window: every field, its media and where it comes from on the device.</summary>
public static class RecordWindow
{
    public static void Show(MainViewModel main, RecordItem r)
    {
        if (main.SessionOf(r.Artifact) is not { } s) return;
        var d = new DetailsViewModel(main);
        d.ShowRecord(s, r.Artifact, r.Columns, r.RowId, r.Values);
        Open(d, Body(d, new RecordDetailsView()), $"{r.Artifact.Name} · record #{r.RowId}", 760);
    }

    /// <summary>A photo to zoom, a video or audio with its player, or a PDF, at full size beside its details and the
    /// messages it went through (<paramref name="history"/>, <paramref name="copies"/>: the gallery's).</summary>
    public static void ShowMedia(MainViewModel main, MediaRef m, MediaUse? use, IReadOnlyList<MediaUse>? history = null, IReadOnlyList<MediaUse>? copies = null)
    {
        var d = new DetailsViewModel(main);
        d.ShowMedia(m, use, history, copies);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380), MinWidth = 200 });
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush") };
        var details = Body(d, new RecordDetailsView { Preview = false });
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(details, 2);
        grid.Children.Add(new MediaViewer(m));
        grid.Children.Add(splitter);
        grid.Children.Add(details);
        Open(d, grid, m.Name, 1280);
    }

    static DockPanel Body(DetailsViewModel d, UIElement view)
    {
        var head = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        head.Children.Add(new TextBlock { Text = d.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock { Text = d.Subtitle, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush") });
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(view);
        return body;
    }

    static void Open(DetailsViewModel d, UIElement content, string title, double width)
    {
        var area = SystemParameters.WorkArea;
        new Window
        {
            Title = title, Width = Math.Min(width, area.Width - 40), Height = Math.Min(860, area.Height - 40), DataContext = d, Content = content,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
        }.Show();
    }
}
