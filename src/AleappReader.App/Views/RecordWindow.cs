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
        Open(d, $"{r.Artifact.Name} · record #{r.RowId}", 760);
    }

    /// <summary>A photo enlarged, or a video or audio with its player, beside its details and the message it came with.</summary>
    public static void ShowMedia(MainViewModel main, MediaRef m, MediaUse? use)
    {
        var d = new DetailsViewModel(main);
        d.ShowMedia(m, use);
        Open(d, m.Name, 1000);
    }

    static void Open(DetailsViewModel d, string title, double width)
    {
        var head = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
        head.Children.Add(new TextBlock { Text = d.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock { Text = d.Subtitle, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush") });
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(new RecordDetailsView());
        new Window
        {
            Title = title, Width = width, Height = 860, DataContext = d, Content = body,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White,
        }.Show();
    }
}
