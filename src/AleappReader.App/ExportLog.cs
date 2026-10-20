using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App;

public sealed record ExportEntry(DateTime Time, string What, string Source, string Destination, string Items)
{
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>Where this session exported data to: every export of the reader adds a line, shown in the Exports window.
/// Kept for the session only; the window saves it as CSV.</summary>
public static class ExportLog
{
    public static ObservableCollection<ExportEntry> Entries { get; } = [];

    public static void Add(string what, string destination, string items = "", CaseSession? s = null)
    {
        var main = (Application.Current.MainWindow as MainWindow)?.ViewModel;
        var source = (s is not null ? main?.DeviceOf(s)?.Label ?? s.RootDir : main?.SelectedTab?.Header) ?? "";
        var e = new ExportEntry(DateTime.Now, what, source, destination, items);
        Application.Current.Dispatcher.Invoke(() => Entries.Insert(0, e));
    }
}

/// <summary>The session's exports: when, what, from which device, to where; a double-click shows the file in Explorer.</summary>
public sealed class ExportLogWindow : Window
{
    static ExportLogWindow? _open;

    public static void ShowLog()
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new ExportLogWindow { Owner = Application.Current.MainWindow };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    ExportLogWindow()
    {
        Title = "Exports of this session";
        (Width, Height, ShowInTaskbar) = (980, 420, false);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new DataGrid { ItemsSource = ExportLog.Entries, IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (h, p, w) in new[] { ("Time", "TimeText", 140.0), ("What", "What", 220), ("Device / view", "Source", 160), ("Items", "Items", 110), ("Exported to", "Destination", 0) })
            grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new Binding(p), Width = w > 0 ? new DataGridLength(w) : new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.MouseDoubleClick += (_, _) => Reveal(grid.SelectedItem as ExportEntry);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        bar.Children.Add(Button("Show in folder", () => Reveal(grid.SelectedItem as ExportEntry)));
        bar.Children.Add(Button("Copy path", () => { if (grid.SelectedItem is ExportEntry e) Clipboard.SetText(e.Destination); }));
        bar.Children.Add(Button("Save the log…", Save));
        bar.Children.Add(new TextBlock { Text = "Every export of this session, newest first. Kept until LEXIS closes.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.7 });
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(grid);
        Content = dock;
    }

    static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3) };
        b.Click += (_, _) => click();
        return b;
    }

    static void Reveal(ExportEntry? e)
    {
        if (e is null) return;
        if (File.Exists(e.Destination)) Process.Start("explorer.exe", $"/select,\"{e.Destination}\"");
        else if (Directory.Exists(e.Destination)) Process.Start("explorer.exe", $"\"{e.Destination}\"");
        else MessageBox.Show("No longer there: " + e.Destination, "Exports", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    static void Save()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"LEXIS exports {DateTime.Now:yyyy-MM-dd HHmm}.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("Time,What,Device / view,Items,Exported to\r\n");
        foreach (var e in ExportLog.Entries.Reverse())
            sb.Append(string.Join(',', new[] { e.TimeText, e.What, e.Source, e.Items, e.Destination }.Select(CsvExporter.Cell))).Append("\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }
}

/// <summary>"Export selected rows… / Export all rows…" on the right-click menu of every table of the reader that has
/// no export of its own: the columns shown, as the table shows them, to CSV or HTML.</summary>
public static class GridExport
{
    const string Mark = "grid-export";

    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((o, _) => { if (o is DataGrid { ContextMenu: null } g) g.ContextMenu = new ContextMenu(); }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((o, _) =>
        {
            if (o is not ContextMenu { PlacementTarget: DataGrid g, ItemsSource: null } menu) return;
            foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, Mark)).ToList()) menu.Items.Remove(old);
            if (menu.Items.OfType<MenuItem>().Any(i => i.Header is string h && h.StartsWith("Export", StringComparison.Ordinal))) return;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator { Tag = Mark });
            var sel = g.SelectedItems.Count;
            menu.Items.Add(Item($"Export {sel:N0} selected row(s)…", sel > 0, () => Export(g, g.SelectedItems)));
            menu.Items.Add(Item($"Export all {g.Items.Count:N0} rows…", g.Items.Count > 0, () => Export(g, g.Items)));
        }));
    }

    static MenuItem Item(string header, bool enabled, Action click)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled, Tag = Mark };
        m.Click += (_, _) => click();
        return m;
    }

    static void Export(DataGrid g, IList items)
    {
        var title = (Application.Current.MainWindow as MainWindow)?.ViewModel.SelectedTab?.Header ?? "Table";
        if (Window.GetWindow(g) is { } w && w != Application.Current.MainWindow) title = w.Title;
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|HTML (*.html)|*.html", FileName = HtmlReportExporter.SafeName(title) + " - rows" };
        if (dlg.ShowDialog() != true) return;
        var cols = g.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
            .Select(c => (Header: Header(c), Binding: BindingOf(c))).Where(c => c.Binding is not null && c.Header != "").ToList();
        var rows = items.Cast<object>().Where(i => i != CollectionView.NewItemPlaceholder)
            .Select(i => cols.Select(c => Evaluate(c.Binding!, i)).ToList()).ToList();
        if (dlg.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder($"<!doctype html><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
                "<style>body{font-family:Segoe UI,sans-serif;font-size:13px}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:3px 6px;vertical-align:top;text-align:left}th{background:#f0f0f0}</style>" +
                $"<h2>{WebUtility.HtmlEncode(title)}</h2><p>{rows.Count:N0} rows exported by LEXIS on {DateTime.Now:yyyy-MM-dd HH:mm:ss}.</p><table><tr>");
            foreach (var c in cols) sb.Append("<th>").Append(WebUtility.HtmlEncode(c.Header)).Append("</th>");
            sb.Append("</tr>");
            foreach (var r in rows) sb.Append("<tr>").AppendJoin("", r.Select(v => $"<td>{WebUtility.HtmlEncode(v)}</td>")).Append("</tr>");
            File.WriteAllText(dlg.FileName, sb.Append("</table>").ToString(), new UTF8Encoding(false));
        }
        else
        {
            var sb = new StringBuilder(string.Join(',', cols.Select(c => CsvExporter.Cell(c.Header)))).Append("\r\n");
            foreach (var r in rows) sb.Append(string.Join(',', r.Select(CsvExporter.Cell))).Append("\r\n");
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        ExportLog.Add($"{title}: table rows", dlg.FileName, $"{rows.Count:N0} rows");
    }

    static string Header(DataGridColumn c) => c.Header switch
    {
        string s when s.Any(ch => ch < '') => s, // an icon glyph alone is no name
        TextBlock t => t.Text,
        _ => "",
    };

    static BindingBase? BindingOf(DataGridColumn c) =>
        c.ClipboardContentBinding ?? (c is DataGridBoundColumn b ? b.Binding : c.SortMemberPath is { Length: > 0 } p ? new Binding(p) : null);

    /// <summary>The binding's value for one row, as the cell would show it (its path, converter and format).</summary>
    static string Evaluate(BindingBase binding, object item)
    {
        if (binding is not Binding b) return "";
        var copy = new Binding { Path = b.Path, Converter = b.Converter, ConverterParameter = b.ConverterParameter, StringFormat = b.StringFormat, Source = item, Mode = BindingMode.OneTime };
        var probe = new TextBlock();
        BindingOperations.SetBinding(probe, TextBlock.TextProperty, copy);
        return probe.Text;
    }
}
