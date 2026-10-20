using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AleappReader.App.ViewModels;

namespace AleappReader.App.Views;

/// <summary>
/// The zones of interest of a map in a window of their own: one row each with its first and last time, searchable and
/// sortable; the zone picked is centred on the map, and its visits are listed underneath.
/// </summary>
public sealed class ZonesWindow : Window
{
    public ZonesWindow(MapViewModel vm, Window owner)
    {
        Title = vm.IsCoPresence ? "Co-presences" : "Zones of interest";
        (Owner, Width, Height, ShowInTaskbar, DataContext) = (owner, 900, 560, false, vm);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var view = new CollectionViewSource { Source = vm.ZoneRows }.View;
        var search = new TextBox { Margin = new Thickness(0, 0, 8, 0), MinWidth = 260,
            ToolTip = "Zone number, coordinates, date or device; every word must match" };
        if (TryFindResource("SearchBox") is Style st) search.Style = st;
        search.TextChanged += (_, _) =>
        {
            var words = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            view.Filter = words.Length == 0 ? null : o => o is ZoneRow z && words.All(w =>
                $"{z.Zone.Number} {z.Position} {z.FirstSeen} {z.LastSeen} {z.Devices} {string.Join(" ", z.Lines.Select(l => l.Text))}"
                    .Contains(w, StringComparison.OrdinalIgnoreCase));
        };
        var info = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        info.SetBinding(TextBlock.TextProperty, nameof(MapViewModel.ZonesInfo));
        if (TryFindResource("MutedBrush") is System.Windows.Media.Brush muted) info.Foreground = muted;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(search, Dock.Left);
        top.Children.Add(search);
        top.Children.Add(info);

        var grid = new DataGrid
        {
            ItemsSource = view, AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            CanUserAddRows = false,
        };
        void Col(string header, string path, string? sortBy = null, bool fill = false)
        {
            var c = new DataGridTextColumn { Header = header, Binding = new Binding(path), SortMemberPath = sortBy ?? path };
            if (fill) c.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            grid.Columns.Add(c);
        }
        Col(vm.IsCoPresence ? "Co-presence" : "Zone", "Zone.Number");
        Col("First seen", nameof(ZoneRow.FirstSeen), nameof(ZoneRow.First));
        Col("Last seen", nameof(ZoneRow.LastSeen), nameof(ZoneRow.Last));
        Col("Visits", nameof(ZoneRow.Visits));
        Col("Positions", nameof(ZoneRow.Positions));
        if (vm.Joint)
        {
            Col("Devices", nameof(ZoneRow.Devices));
            Col("Together", nameof(ZoneRow.Together));
        }
        Col("Centre", nameof(ZoneRow.Position), fill: true);
        grid.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,
            new Binding(nameof(MapViewModel.SelectedZone)) { Mode = BindingMode.TwoWay });
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is { } s) grid.ScrollIntoView(s); };

        // the visits of the zone picked, and the periods in which devices were there together
        var lines = new ItemsControl { Focusable = false, Margin = new Thickness(4) };
        lines.SetBinding(ItemsControl.ItemsSourceProperty, $"{nameof(MapViewModel.SelectedZone)}.{nameof(ZoneRow.Lines)}");
        var line = new FrameworkElementFactory(typeof(TextBlock));
        line.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        line.SetValue(MarginProperty, new Thickness(0, 1, 0, 1));
        line.SetBinding(TextBlock.TextProperty, new MultiBinding
        {
            StringFormat = "{0}  {1}", Bindings = { new Binding(nameof(ZoneLine.Label)), new Binding(nameof(ZoneLine.Text)) },
        });
        lines.ItemTemplate = new DataTemplate { VisualTree = line };
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 2), TextWrapping = TextWrapping.Wrap };
        title.SetBinding(TextBlock.TextProperty, $"{nameof(MapViewModel.SelectedZone)}.{nameof(ZoneRow.Summary)}");
        var details = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        details.Children.Add(title);
        details.Children.Add(new ScrollViewer { Content = lines, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 80 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, Margin = new Thickness(0, 4, 0, 4) };
        if (TryFindResource("BorderBrush") is System.Windows.Media.Brush border) splitter.Background = border;
        Grid.SetRow(grid, 1);
        Grid.SetRow(splitter, 2);
        Grid.SetRow(details, 3);
        foreach (var e in new UIElement[] { top, grid, splitter, details }) root.Children.Add(e);
        Content = root;

        Loaded += (_, _) => search.Focus();
    }
}
