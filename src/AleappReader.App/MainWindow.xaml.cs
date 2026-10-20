using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AleappReader.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace AleappReader.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        RestoreLayout();
        Closing += (_, _) => SaveLayout();
        Closed += (_, _) => ViewModel.CloseCaseCommand.Execute(null);
        ViewModel.Details.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DetailsViewModel.Html)) _ = ShowHtmlAsync(); };
        ViewModel.Details.Media.CollectionChanged += (_, _) => _ = ShowHtmlAsync(); // a PDF among the media
    }

    // TreeView.SelectedItem is read-only, so selection flows one way: tree → view model.
    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        ViewModel.SelectedNode = e.NewValue as NodeViewModel;

    static NodeViewModel? NodeAt(DependencyObject? d)
    {
        while (d is not null and not TreeViewItem) d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as TreeViewItem)?.DataContext as NodeViewModel;
    }

    /// <summary>Right click on a device (rename, move to another case, overview) or on a case (edit, close).</summary>
    void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Tree.ContextMenu;
        menu.Items.Clear();
        MenuItem Item(string header, Action act) { var m = new MenuItem { Header = header }; m.Click += (_, _) => act(); return m; }
        switch (NodeAt(e.OriginalSource as DependencyObject))
        {
            case { Device: { } d, Case: { } c }:
                menu.Items.Add(Item("Open overview", () => ViewModel.SelectDevice(d)));
                menu.Items.Add(Item("Rename…", () => ViewModel.RenameDevice(d)));
                var move = new MenuItem { Header = "Move to case" };
                foreach (var other in ViewModel.Cases.Where(x => x != c)) move.Items.Add(Item(other.Title, () => ViewModel.MoveDeviceToCase(d, other)));
                if (move.Items.Count > 0) move.Items.Add(new Separator());
                move.Items.Add(Item("Another case file…", () => ViewModel.MoveDeviceToCaseFile(d)));
                menu.Items.Add(move);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(c.IsDefault ? "Remove" : "Remove from case…", () => ViewModel.RemoveDevice(d)));
                break;
            case { Kind: NodeKind.Case, Case: { IsDefault: false } c }:
                menu.Items.Add(Item("Edit case…", () => ViewModel.InvestigationCase(c)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove case and its devices", () => ViewModel.CloseOneCase(c))); // the case file and the reports stay on disk
                break;
            case { Kind: NodeKind.Case, Case: { Devices.Count: > 0 } c }: // NO CASE
                menu.Items.Add(Item("Make a case of these devices…", () => ViewModel.InvestigationCase(c)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove these devices", () => ViewModel.CloseOneCase(c)));
                break;
            default:
                e.Handled = true; // no menu
                break;
        }
    }

    // a device is dragged onto another device of its case (placed before it), onto a case node (moved to that case, or last in its own)
    Point _dragStart;
    NodeViewModel? _dragNode;

    void Tree_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragNode = NodeAt(e.OriginalSource as DependencyObject) is { Device: not null } n ? n : null;
    }

    void Tree_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragNode is not { Device: { } d } || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var v = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(v.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(v.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragNode = null;
        DragDrop.DoDragDrop(Tree, d, DragDropEffects.Move);
    }

    void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(Device)) is Device && NodeAt(e.OriginalSource as DependencyObject) is { Case: not null } t
                    && (t.Kind == NodeKind.Case || t.Device is not null) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void Tree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Device)) is not Device d || NodeAt(e.OriginalSource as DependencyObject) is not { Case: { } c } t) return;
        var own = ViewModel.CaseOf(d.Session);
        if (c != own && (t.Kind == NodeKind.Case || t.Device is not null)) ViewModel.MoveDeviceToCase(d, c);
        else if (t.Device is { } before) ViewModel.MoveDevice(d, before);
        else if (t.Kind == NodeKind.Case) ViewModel.MoveDevice(d, null);
    }

    GridLength _treeWidth = new(300);

    /// <summary>The window and the side panels as the examiner left them; the first time, maximized. A position no longer
    /// on any screen (a monitor unplugged) is dropped for the centre.</summary>
    void RestoreLayout()
    {
        if (Settings.Current.Window is not { } w) { WindowState = WindowState.Maximized; return; }
        TreeColumn.Width = _treeWidth = new GridLength(Math.Max(w.TreeWidth, TreeColumn.MinWidth));
        DetailsColumn.Width = new GridLength(Math.Max(w.DetailsWidth, DetailsColumn.MinWidth));
        if (w.Width >= MinWidth && w.Height >= MinHeight) { Width = w.Width; Height = w.Height; }
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (screen.IntersectsWith(new Rect(w.Left, w.Top, Width, Height)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = w.Left;
            Top = w.Top;
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    void SaveLayout()
    {
        var b = WindowState == WindowState.Normal || RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var tree = Tree.Visibility == Visibility.Visible ? TreeColumn.ActualWidth : _treeWidth.Value;
        static double F(double v) => double.IsFinite(v) ? v : 0; // JSON takes no NaN; 0 fails the checks when restoring
        Settings.Current.Window = new(F(b.Left), F(b.Top), F(b.Width), F(b.Height), WindowState == WindowState.Maximized, F(tree), F(DetailsColumn.ActualWidth));
        Settings.Current.Save();
    }

    /// <summary>Hides the navigation tree to a thin strip, or brings it back at the width it had.</summary>
    void TreeToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = Tree.Visibility == Visibility.Visible;
        if (open) _treeWidth = TreeColumn.Width;
        Tree.Visibility = TreeSplitter.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        TreeColumn.MinWidth = open ? 0 : 240;
        TreeColumn.Width = open ? new GridLength(0) : _treeWidth;
        TreeToggleGlyph.Text = open ? "" : ""; // chevron right / left
        TreeToggle.ToolTip = open ? "Show the navigation panel" : "Hide the navigation panel";
    }

    /// <summary>Middle click on a tab header closes it, as in a browser.</summary>
    void ViewTabs_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != ViewTabs; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is TabItem { DataContext: TabViewModel t })
            {
                ViewModel.CloseTabCommand.Execute(t);
                e.Handled = true;
                return;
            }
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Views.SettingsWindow.Show(this)) ViewModel.RebuildTree();
    }

    /// <summary>Tag shortcuts: the selected records of the focused table, or else the record in the details pane.</summary>
    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ViewModel.Session is null) return;
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            var box = FilterBox(ViewTabs) ?? SearchInput;
            box.Focus();
            box.SelectAll();
            e.Handled = true;
            return;
        }
        // in a text box the editing keys (Ctrl+C, Ctrl+V…) and plain typing stay the text box's
        var typing = System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;
        foreach (var (tag, keys) in Settings.Current.TagShortcuts)
        {
            // the digits of the numeric keypad stand for the digits above the letters
            if (RecordGrid.Gesture(keys) is not { } g || !(g.Matches(null, e) || e.Key is >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9
                    && g.Key == System.Windows.Input.Key.D0 + (e.Key - System.Windows.Input.Key.NumPad0) && g.Modifiers == System.Windows.Input.Keyboard.Modifiers)) continue;
            if (typing && (g.Modifiers is System.Windows.Input.ModifierKeys.None or System.Windows.Input.ModifierKeys.Shift ||
                           g.Key is System.Windows.Input.Key.A or System.Windows.Input.Key.C or System.Windows.Input.Key.V
                               or System.Windows.Input.Key.X or System.Windows.Input.Key.Y or System.Windows.Input.Key.Z)) return;
            if (RecordGrid.FocusedGallery() is { Items.Count: > 0 } fg)
            {
                ViewModel.Status = fg.View.TagFiles(fg.Items, tag) ? $"Tagged {fg.Items.Count} file(s) as \"{tag}\"" : $"Tag \"{tag}\" removed from {fg.Items.Count} file(s)";
                e.Handled = true;
                return;
            }
            var recs = RecordGrid.FocusedRecords().Select(r => (r.Artifact, r.RowId)).ToList();
            if (recs.Count == 0 && ViewModel.Details.TagKey is { } k) recs.Add(k);
            if (recs.Count == 0) { ViewModel.Status = "Select a record, a message or a media file a record names to tag it"; e.Handled = true; return; }
            ViewModel.Status = ViewModel.SetTag(recs, tag, true) ? $"Tagged {recs.Count} record(s) as \"{tag}\"" : $"Tag \"{tag}\" removed from {recs.Count} record(s)";
            e.Handled = true;
            return;
        }
    }

    /// <summary>The filter box of the view in front (styled SearchBox), if it has one.</summary>
    TextBox? FilterBox(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is TextBox { IsVisible: true } t && t.Style == FindResource("SearchBox")) return t;
            if (FilterBox(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>The file URI of the PDF shown in the preview: the one place the browser may load besides data: URIs.</summary>
    string? _pdfUri;

    /// <summary>Email HTML is evidence, not a web page: scripts off, and every request (remote images, tracking
    /// pixels, links) is answered locally with 403, so viewing a message never contacts its sender. A record without
    /// HTML whose media hold a PDF shows that PDF the same way, from the report's copy.</summary>
    async Task ShowHtmlAsync()
    {
        var html = ViewModel.Details.Html;
        var pdf = html is null ? ViewModel.Details.Pdf : null;
        string? pdfUri = null;
        try { pdfUri = pdf is null ? null : new Uri(pdf.LocalFile()).AbsoluteUri; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        DetailsView.ShowHtml(html is not null || pdfUri is not null);
        if (html is null && pdfUri is null) { _pdfUri = null; return; }
        if (html is null && pdfUri == _pdfUri && DetailsView.HtmlView.CoreWebView2 is not null) return; // shown already
        try
        {
            if (DetailsView.HtmlView.CoreWebView2 is null)
            {
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "WebView2");
                await DetailsView.HtmlView.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, data));
                var core = DetailsView.HtmlView.CoreWebView2!;
                core.Settings.IsScriptEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.NewWindowRequested += (_, a) => a.Handled = true;
                core.NavigationStarting += (_, a) => a.Cancel = !a.Uri.StartsWith("data:", StringComparison.Ordinal) && a.Uri != _pdfUri;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, a) =>
                {
                    if (!a.Request.Uri.StartsWith("data:", StringComparison.Ordinal) && a.Request.Uri != _pdfUri)
                        a.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                };
            }
            if (html is null) { _pdfUri = pdfUri; DetailsView.HtmlView.CoreWebView2!.Navigate(pdfUri); return; }
            _pdfUri = null;
            // ponytail: NavigateToString caps at 2 MB; the largest real body seen is ~120 KB, so longer ones are cut.
            if (ViewModel.Details.Html == html) DetailsView.HtmlView.NavigateToString(html.Length <= 600_000 ? html : html[..600_000]);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            DetailsView.ShowHtml(false); // no runtime: the text body is still in the fields
        }
    }
}
