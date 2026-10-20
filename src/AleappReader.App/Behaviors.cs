using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AleappReader.App.ViewModels;
using AleappReader.Core;

namespace AleappReader.App;

/// <summary>A list whose columns take a condition each from their header's ▾ menu: an artifact table (in the database)
/// or the chat list (in memory). Raises PropertyChanged("ColumnFilterCount") when they change.</summary>
public interface IColumnFilters : INotifyPropertyChanged
{
    ColumnFilter? FilterOf(string sqlColumn);
    void SetColumnFilter(string sqlColumn, ColumnFilter? f);
}

/// <summary>Builds DataGrid columns from an artifact's manifest columns; sorting goes to the database. On an artifact
/// table each header has a ▾ menu: sort, filter on that column, hide, fit. <see cref="HeaderMenusProperty"/> gives
/// the same menu to a grid's own XAML columns (matched on SortMemberPath).</summary>
public static class DynamicColumns
{
    public static readonly DependencyProperty HeaderMenusProperty = DependencyProperty.RegisterAttached(
        "HeaderMenus", typeof(IReadOnlyList<ColumnInfo>), typeof(DynamicColumns), new PropertyMetadata(null, OnHeaderMenusChanged));
    public static void SetHeaderMenus(DependencyObject d, IReadOnlyList<ColumnInfo>? v) => d.SetValue(HeaderMenusProperty, v);
    public static IReadOnlyList<ColumnInfo>? GetHeaderMenus(DependencyObject d) => (IReadOnlyList<ColumnInfo>?)d.GetValue(HeaderMenusProperty);

    static void OnHeaderMenusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g || e.NewValue is not IReadOnlyList<ColumnInfo> infos || g.DataContext is not IColumnFilters host) return;
        if (g.Columns.Count == 0) { g.Loaded += Once; return; } // bound before the XAML columns were added
        void Once(object? _, RoutedEventArgs __) { g.Loaded -= Once; OnHeaderMenusChanged(d, e); }
        foreach (var col in g.Columns)
            if (infos.FirstOrDefault(i => i.Sql == col.SortMemberPath) is { } info) col.Header = Header(g, host, col, info);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridColumn, ColumnInfo> Info = new();

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
        "Columns", typeof(IReadOnlyList<ColumnInfo>), typeof(DynamicColumns), new PropertyMetadata(null, OnColumnsChanged));
    public static void SetColumns(DependencyObject d, IReadOnlyList<ColumnInfo>? v) => d.SetValue(ColumnsProperty, v);
    public static IReadOnlyList<ColumnInfo>? GetColumns(DependencyObject d) => (IReadOnlyList<ColumnInfo>?)d.GetValue(ColumnsProperty);

    public static readonly DependencyProperty SortCommandProperty = DependencyProperty.RegisterAttached(
        "SortCommand", typeof(ICommand), typeof(DynamicColumns), new PropertyMetadata(null, OnSortCommandChanged));
    public static void SetSortCommand(DependencyObject d, ICommand? v) => d.SetValue(SortCommandProperty, v);
    public static ICommand? GetSortCommand(DependencyObject d) => (ICommand?)d.GetValue(SortCommandProperty);

    static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g) return;
        foreach (var c in g.Columns.Where(c => Info.TryGetValue(c, out _)).ToList()) g.Columns.Remove(c); // XAML columns stay
        if (e.NewValue is not IReadOnlyList<ColumnInfo> cols) return;
        for (int i = 0; i < cols.Count; i++)
        {
            DataGridColumn col = cols[i].IsMedia && g.DataContext is ArtifactViewModel ? MediaColumn(i) : new DataGridTextColumn
            {
                Header = cols[i].Display,
                Binding = new Binding($"Cells[{i}]"),
                SortMemberPath = $"Cells[{i}]", // client-side sort for fully loaded grids (chat table)
                MaxWidth = 520,
                Width = DataGridLength.Auto,
                ElementStyle = g.TryFindResource("WrapCell") as Style,
            };
            Info.Add(col, cols[i]);
            if (g.DataContext is ArtifactViewModel vm) col.Header = Header(g, vm, col, cols[i]);
            g.Columns.Add(col);
        }
        // An Auto width grows each time a wider row scrolls in, pushing the table sideways: the widths the first rows shown
        // give are kept, and a longer text wraps onto more lines of its row instead. The room left on the right is
        // shared among the columns by their width, again whenever the table is resized, until a column is resized by hand.
        var added = g.Columns.Where(c => Info.TryGetValue(c, out _)).ToList();
        var natural = new Dictionary<DataGridColumn, double>();
        var set = new Dictionary<DataGridColumn, double>();
        void Fill()
        {
            var spare = g.ActualWidth - g.RowHeaderActualWidth - SystemParameters.VerticalScrollBarWidth - 4
                        - g.Columns.Except(added).Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth)
                        - natural.Where(x => x.Key.Visibility == Visibility.Visible).Sum(x => x.Value);
            var total = natural.Where(x => x.Key.Visibility == Visibility.Visible).Sum(x => x.Value);
            foreach (var (c, w) in natural)
            {
                c.MaxWidth = double.PositiveInfinity;
                c.Width = new DataGridLength(set[c] = spare > 0 && total > 0 ? w + spare * w / total : w);
            }
        }
        void Resized(object? s, SizeChangedEventArgs a)
        {
            if (!added.All(g.Columns.Contains) || natural.Any(x => Math.Abs(x.Key.Width.Value - set[x.Key]) > 0.5 || !x.Key.Width.IsAbsolute))
                g.SizeChanged -= Resized; // another artifact now, or the examiner sized a column
            else if (a.WidthChanged) Fill();
        }
        void Freeze(object? s, EventArgs a)
        {
            if (!added.All(g.Columns.Contains)) { g.LayoutUpdated -= Freeze; return; } // the columns of another artifact now
            if (g.Items.Count == 0 || added.Any(c => c.ActualWidth <= 0)) return;
            g.LayoutUpdated -= Freeze;
            foreach (var c in added) natural[c] = c.ActualWidth;
            Fill();
            g.SizeChanged += Resized;
        }
        g.LayoutUpdated += Freeze;
    }

    /// <summary>A media column: the picture (or a video's frame) of the cell, small, beside its text; a profile picture
    /// sits on its contact's row.</summary>
    static DataGridTemplateColumn MediaColumn(int i)
    {
        var thumb = new MultiBinding { Converter = new CellThumbConverter() };
        thumb.Bindings.Add(new Binding($"Raw[{i}]"));
        thumb.Bindings.Add(new Binding("DataContext") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGrid), 1) });
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, thumb);
        image.SetValue(FrameworkElement.MaxHeightProperty, 48.0);
        image.SetValue(FrameworkElement.MaxWidthProperty, 64.0);
        image.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 6, 1));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding($"Cells[{i}]"));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(image);
        panel.AppendChild(text);
        return new DataGridTemplateColumn
        {
            CellTemplate = new DataTemplate { VisualTree = panel },
            ClipboardContentBinding = new Binding($"Cells[{i}]"),
            SortMemberPath = $"Cells[{i}]",
            MaxWidth = 520,
            Width = DataGridLength.Auto,
        };
    }

    sealed class CellThumbConverter : IMultiValueConverter
    {
        public object? Convert(object?[] values, Type t, object? p, System.Globalization.CultureInfo c) =>
            values is [var raw, ArtifactViewModel vm] ? vm.CellThumb(raw) : null;
        public object[] ConvertBack(object? v, Type[] t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    static FrameworkElement Header(DataGrid g, IColumnFilters vm, DataGridColumn col, ColumnInfo info)
    {
        var funnel = new TextBlock
        {
            Style = (Style)g.FindResource("Icon"), Text = "\uE71C", FontSize = 10, Margin = new Thickness(4, 0, 0, 0),
            Foreground = (Brush)g.FindResource("AccentBrush"), ToolTip = "Filtered",
            Visibility = vm.FilterOf(info.Sql) is null ? Visibility.Collapsed : Visibility.Visible,
        };
        vm.PropertyChanged += (_, e) => // "Clear column filters" in the toolbar
        {
            if (e.PropertyName == "ColumnFilterCount")
                funnel.Visibility = vm.FilterOf(info.Sql) is null ? Visibility.Collapsed : Visibility.Visible;
        };
        var arrow = new Button
        {
            Content = new TextBlock { Style = (Style)g.FindResource("Icon"), Text = "\uE70D", FontSize = 9 },
            Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(4, 0, 0, 0), Focusable = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            ToolTip = "Sort, filter, hide this column",
        };
        arrow.Click += (_, e) =>
        {
            e.Handled = true; // not a header click (that sorts)
            var menu = new ContextMenu { PlacementTarget = arrow };
            void Add(string text, Action act, bool enabled = true)
            {
                var m = new MenuItem { Header = text, IsEnabled = enabled };
                m.Click += (_, _) => act();
                menu.Items.Add(m);
            }
            void SortDir(bool desc)
            {
                foreach (var c in g.Columns) c.SortDirection = null;
                col.SortDirection = desc ? ListSortDirection.Descending : ListSortDirection.Ascending;
                if (vm is ArtifactViewModel a) { a.SortBy(info.Sql, desc); return; }
                g.Items.SortDescriptions.Clear(); // a list held in memory sorts in place
                g.Items.SortDescriptions.Add(new SortDescription(info.Sql, col.SortDirection.Value));
            }
            Add(info.IsDateTime ? "Sort oldest first" : info.Type == "number" ? "Sort smallest first" : "Sort A → Z", () => SortDir(false));
            Add(info.IsDateTime ? "Sort newest first" : info.Type == "number" ? "Sort largest first" : "Sort Z → A", () => SortDir(true));
            menu.Items.Add(new Separator());
            Add("Filter…", () =>
            {
                if (Views.ColumnFilterWindow.Ask(Window.GetWindow(g), info, vm.FilterOf(info.Sql)) is not { } r) return;
                vm.SetColumnFilter(info.Sql, r.Filter);
                funnel.Visibility = r.Filter is null ? Visibility.Collapsed : Visibility.Visible;
            });
            Add("Clear filter", () => { vm.SetColumnFilter(info.Sql, null); funnel.Visibility = Visibility.Collapsed; }, vm.FilterOf(info.Sql) is not null);
            menu.Items.Add(new Separator());
            Add("Fit width to content", () => col.Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells));
            Add("Hide column", () => col.Visibility = Visibility.Collapsed);
            Add("Show all columns", () => { foreach (var c in g.Columns) c.Visibility = Visibility.Visible; },
                g.Columns.Any(c => c.Visibility != Visibility.Visible));
            menu.IsOpen = true;
        };
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = info.Display, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(funnel);
        p.Children.Add(arrow);
        return p;
    }

    static void OnSortCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid g) return;
        g.Sorting -= OnSorting;
        if (e.NewValue is not null) g.Sorting += OnSorting;
    }

    static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // client-side sort would only order the current page
        var g = (DataGrid)sender;
        if (string.IsNullOrEmpty(e.Column.SortMemberPath)) return;
        foreach (var c in g.Columns) if (c != e.Column) c.SortDirection = null;
        e.Column.SortDirection = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        if (Info.TryGetValue(e.Column, out var info)) GetSortCommand(g)?.Execute(info.Sql);
    }
}

/// <summary>Full-size image for the details pane, decoded without locking the file.</summary>
public sealed class MediaImageConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is MediaRef { IsImage: true, Exists: true } m ? GalleryViewModel.Thumb(m, 600) : null;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Uri for MediaElement (video/audio only).</summary>
public sealed class MediaUriConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not MediaRef { Exists: true, Kind: "video" or "audio" } m) return null;
        try { return new Uri(m.LocalFile()); } // a portable case's is written out of the zip on first play
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return null; }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Media height that fits the preview: its visible height less the file-name row, at least 80.</summary>
public sealed class PreviewFitConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is double h ? Math.Max(80, h - 44) : 360.0;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>True when the row id (first value) is in the set (second value).</summary>
public sealed class InSetConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c) =>
        values is [long id, IReadOnlySet<long> set] && set.Contains(id);
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>A record's translation from (row id, translations by row, mode): its text, or with "translation" whether it
/// shows, or with "original" whether the original text shows (hidden only in Translated mode when a translation exists).</summary>
public sealed class TranslationConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c)
    {
        var text = values is [long id, IReadOnlyDictionary<long, string> d, ..] ? d.GetValueOrDefault(id) : null;
        var mode = values.Length > 2 ? values[2] as string : null;
        return p switch
        {
            "translation" => text is not null && mode is not (null or TranslationView.Original) ? Visibility.Visible : Visibility.Collapsed,
            "original" => text is not null && mode == TranslationView.Translated ? Visibility.Collapsed : Visibility.Visible,
            _ => text ?? "",
        };
    }
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>What the AI read in a file, from [media, TranslationView.ByMedia, Mode]: the text, or with "visibility" shown
/// when there is a note and translations are shown.</summary>
public sealed class MediaNoteConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c)
    {
        var text = values is [MediaRef m, IReadOnlyDictionary<string, string> d, ..] ? d.GetValueOrDefault(m.RefId) : null;
        var shown = values.Length > 2 && values[2] is string mode && mode != TranslationView.Original;
        return p is "visibility" ? text is { Length: > 0 } && shown ? Visibility.Visible : Visibility.Collapsed : text ?? "";
    }
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Carries a DataContext to what is not in the visual tree (a DataGrid column's visibility).</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));
    public object? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>The values as one array, for a command that needs two things from the view.</summary>
public sealed class ToArrayConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type t, object? p, CultureInfo c) => values.ToArray();
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>A ListBox that brings its selected item into view when the selection is set from the view model.</summary>
public static class ScrollToSelected
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(ScrollToSelected), new PropertyMetadata(false, (d, e) =>
        {
            if (d is ListBox l && (bool)e.NewValue) l.SelectionChanged += (_, _) => { if (l.SelectedItem is { } it) l.ScrollIntoView(it); };
        }));
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
}

/// <summary>A tag's colour: the tags of the settings in order take the palette's colours, any other tag one by its name.</summary>
public sealed class TagColorConverter : IValueConverter
{
    static readonly Brush[] Palette = [.. new[] { "#D93025", "#E8A317", "#8E24AA", "#1A73E8", "#80868B", "#188038", "#00897B", "#D81B60",
        "#795548", "#3949AB" }.Select(h => { var b = (Brush)new BrushConverter().ConvertFromString(h)!; b.Freeze(); return b; })];

    public static Brush? Of(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        var i = Settings.Current.Tags.IndexOf(tag);
        return Palette[(i >= 0 ? i : (int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(tag) % 1000)) % Palette.Length];
    }

    public object? Convert(object? value, Type t, object? p, CultureInfo c) => Of(value as string);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>A message's tag from (row id, tags by row): its colour, or with "text" its name.</summary>
public sealed class TagOfConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type t, object? p, CultureInfo c)
    {
        var tag = values is [long id, IReadOnlyDictionary<long, string> d] ? d.GetValueOrDefault(id) : null;
        return p is "text" ? tag : p is "visibility" ? tag is null ? Visibility.Collapsed : Visibility.Visible : TagColorConverter.Of(tag);
    }
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>True when the value equals <see cref="Value"/>.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object? Value { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c) => Equals(value, Value);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is null or "" or false or 0) ^ (p as string == "invert") ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>The width of a card so the cards of a row fill the panel (the value, its width): as many as fit at the
/// minimum width the parameter gives, less the card's right margin.</summary>
public sealed class FillWidthConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var (width, min) = (value is double w ? w : 0, double.Parse(p as string ?? "400", CultureInfo.InvariantCulture));
        return width < min ? min : width / Math.Floor(width / min) - 10.5;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
