using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AleappReader.App.ViewModels;
using AleappReader.App.Views;
using AleappReader.Core;

namespace AleappReader.App;

/// <summary>A record shown in a grid: an artifact row or a timeline event.</summary>
public sealed record RecordItem(ArtifactInfo Artifact, long RowId, IReadOnlyList<ColumnInfo> Columns, object?[] Values, string Text);

/// <summary>
/// Right-click menu (Tags ▸, check, details, copy, export), double-click for the full record, and the target of the tag
/// shortcuts, for any grid of records (artifact tables, timeline).
/// </summary>
public static class RecordGrid
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RecordGrid), new PropertyMetadata(false, OnEnabled));
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    static MainViewModel Main => ((MainWindow)Application.Current.MainWindow).ViewModel;

    static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ListBox l && e.NewValue is true) // chat bubbles, gallery tiles
        {
            l.ContextMenu = new ContextMenu();
            l.ContextMenu.Opened += (_, _) => BuildList(l);
            l.PreviewMouseRightButtonDown += (_, a) =>
            {
                if (a.OriginalSource is DependencyObject o && ItemsControl.ContainerFromElement(l, o) is ListBoxItem { IsSelected: false } item)
                    l.SelectedItem = item.DataContext;
            };
            l.PreviewKeyDown += (_, a) => a.Handled |= a.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && ToggleChecks(l.DataContext, l.SelectedItems);
            l.MouseDoubleClick += (_, a) =>
            {
                if (l.DataContext is GalleryViewModel gv && a.OriginalSource is DependencyObject o && ItemsControl.ContainerFromElement(l, o) is ListBoxItem { DataContext: GalleryItem g })
                    gv.Open(g);
            };
            return;
        }
        if (d is not DataGrid g || e.NewValue is not true) return;
        g.ContextMenu = new ContextMenu();
        g.ContextMenu.Opened += (_, _) => Build(g);
        // right-click acts on the row under the mouse unless it is already part of the selection
        g.PreviewMouseRightButtonDown += (_, a) =>
        {
            if (a.OriginalSource is DependencyObject o && ItemsControl.ContainerFromElement(g, o) is DataGridRow { IsSelected: false } row)
                g.SelectedItem = row.Item;
        };
        g.MouseDoubleClick += (_, a) =>
        {
            if (a.OriginalSource is not DependencyObject o || ItemsControl.ContainerFromElement(g, o) is not DataGridRow row) return;
            if (g.DataContext is ViewModels.ArtifactViewModel { CanOpenChat: true } chats) chats.OpenChatCommand.Execute(null); // a chat list opens the chat
            else if (Of(g, row.Item) is { } r) RecordWindow.Show(Main, r);
        };
        // Ctrl+A would read every row of the view to select it; "Check all" marks a whole view instead.
        g.PreviewKeyDown += (_, a) => a.Handled |= a.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control ||
            a.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && a.OriginalSource is not TextBox && ToggleChecks(g.DataContext, g.SelectedItems);
        g.SelectionChanged += (_, _) => { if (g.SelectedItem is { } it && g.SelectedItems.Count == 1) g.ScrollIntoView(it); };
    }

    /// <summary>Space: the selected items' boxes are checked, or unchecked when all of them already are.</summary>
    static bool ToggleChecks(object? context, System.Collections.IList selected)
    {
        var items = selected.Cast<object>().ToList();
        switch (context)
        {
            case ArtifactViewModel vm when items.OfType<GridRow>().Where(r => !r.IsPlaceholder).ToList() is { Count: > 0 } rows:
                vm.Check(rows.Select(r => r.RowId), !rows.All(r => r.IsChecked));
                return true;
            case ChatViewModel c when items.OfType<MessageItem>().Select(m => m.RowId).Concat(items.OfType<GridRow>().Select(r => r.RowId)).ToList() is { Count: > 0 } ids:
                c.Check(ids, !ids.All(c.CheckedIds.Contains));
                return true;
            case ArtifactViewModel vm when items.OfType<GalleryItem>().ToList() is { Count: > 0 } tiles:
                vm.Check(tiles.Select(t => t.RowId), !tiles.All(t => t.IsChecked));
                return true;
            case GalleryViewModel when items.OfType<GalleryItem>().ToList() is { Count: > 0 } tiles:
                var on = !tiles.All(t => t.IsChecked);
                tiles.ForEach(t => t.IsChecked = on);
                return true;
            default:
                return false;
        }
    }

    static RecordItem? Of(DataGrid g, object? item) => item switch
    {
        GridRow { IsPlaceholder: false } r when g.DataContext is ArtifactViewModel vm =>
            new(vm.Artifact, r.RowId, vm.Columns, r.Raw, string.Join("\t", r.Cells)),
        GridRow r when g.DataContext is ChatViewModel c =>
            new(c.Artifact, r.RowId, c.Columns, r.Raw, string.Join("\t", r.Cells)),
        TimelineEvent { IsPlaceholder: false } e =>
            new(e.Artifact, e.RowId, e.Columns, e.Values, $"{(e.DeviceLabel != "" ? e.DeviceLabel + "\t" : "")}{e.Timestamp}\t{e.Activity}\t{e.EventType}\t{e.Summary}"),
        _ => null,
    };

    /// <summary>The selected records; a selected chat of a chat list stands for all its messages.</summary>
    public static List<RecordItem> Selected(DataGrid g) => g.DataContext is ConversationViewModel cv
        ? [.. g.SelectedItems.OfType<ConversationItem>().SelectMany(c => Messages(cv.Artifact, cv.Columns, c.Messages))]
        : g.SelectedItems.Cast<object>().Select(i => Of(g, i)).OfType<RecordItem>().ToList();

    static IEnumerable<RecordItem> Messages(ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, IEnumerable<MessageItem> msgs) =>
        msgs.Select(m => new RecordItem(a, m.RowId, cols, m.Raw, $"{m.Time}\t{m.Sender}\t{m.Text}"));

    /// <summary>The records selected where the keyboard focus is: a record grid, a chat list, or chat bubbles.</summary>
    public static List<RecordItem> FocusedRecords()
    {
        for (var o = Keyboard.FocusedElement as DependencyObject; o is not null; o = o is Visual ? VisualTreeHelper.GetParent(o) : null)
        {
            if (o is DataGrid g && GetEnabled(g)) return Selected(g);
            if (o is ListBox { DataContext: ChatViewModel c } l && GetEnabled(l))
                return [.. Messages(c.Artifact, c.Columns, c.Chat.Messages.Where(l.SelectedItems.OfType<MessageItem>().ToHashSet().Contains))];
            // media tiles: an artifact's stand for their records (the gallery's tag the files: FocusedGallery)
            if (o is ListBox { DataContext: ArtifactViewModel av } t && GetEnabled(t))
                return [.. t.SelectedItems.OfType<GalleryItem>().Where(i => i.RowId >= 0).Select(i => i.RowId).Distinct()
                    .Select(id => new RecordItem(av.Artifact, id, av.Columns, [], ""))];
        }
        return [];
    }

    /// <summary>The gallery tiles selected where the keyboard focus is, when it is in the gallery.</summary>
    public static (GalleryViewModel View, List<GalleryItem> Items)? FocusedGallery()
    {
        for (var o = Keyboard.FocusedElement as DependencyObject; o is not null; o = o is Visual ? VisualTreeHelper.GetParent(o) : null)
            if (o is ListBox { DataContext: GalleryViewModel gv } l && GetEnabled(l)) return (gv, l.SelectedItems.OfType<GalleryItem>().ToList());
        return null;
    }

    static void Build(DataGrid g)
    {
        var menu = g.ContextMenu!;
        menu.Items.Clear();
        var recs = Selected(g);
        if (recs.Count == 0) { menu.Items.Add(new MenuItem { Header = "Select records first", IsEnabled = false }); return; }
        var n = recs.Count;
        var main = Main;
        menu.Items.Add(TagsMenu(tag => main.SetTag(recs.Select(r => (r.Artifact, r.RowId)), tag, true)));
        if (g.DataContext is ArtifactViewModel vm)
        {
            Add(menu, $"Check {n} record(s)", () => vm.Check(recs.Select(r => r.RowId), true));
            Add(menu, $"Uncheck {n} record(s)", () => vm.Check(recs.Select(r => r.RowId), false));
        }
        if (g.DataContext is ChatViewModel cv)
        {
            Add(menu, $"Check {n} message(s)", () => cv.Check(recs.Select(r => r.RowId), true));
            Add(menu, $"Uncheck {n} message(s)", () => cv.Check(recs.Select(r => r.RowId), false));
        }
        if (main.SessionOf(recs[0].Artifact) is { } s)
        {
            var mine = recs.Where(r => main.SessionOf(r.Artifact) == s).ToList(); // one device per AI run
            var tr = g.DataContext switch { ArtifactViewModel a => a.Translation, ChatViewModel c => c.Translation, _ => null };
            menu.Items.Add(AiMenu(main, s, new AiScope($"{mine.Count:N0} selected records" + (mine.All(r => r.Artifact == mine[0].Artifact) ? $" of {mine[0].Artifact.Name}" : ""),
                [.. mine.GroupBy(r => r.Artifact).Select(x => new AiSelection(x.Key, x.Select(r => r.RowId).ToList()))], []), tr));
        }
        menu.Items.Add(new Separator());
        if (g.DataContext is ConversationViewModel chats)
        {
            Add(menu, "Open chat", () => chats.OpenChatCommand.Execute(null), "Double-click");
            Add(menu, "Export chat as HTML…", () => chats.ExportChatCommand.Execute(null));
            Add(menu, $"Copy {n} message(s)", () => Clipboard.SetText(string.Join(Environment.NewLine, recs.Select(r => r.Text))));
            return;
        }
        Add(menu, "Show all details…", () => RecordWindow.Show(main, recs[0]), "Double-click");
        if (g.DataContext is TimelineViewModel) Add(menu, "Open in its artifact", () => main.NavigateTo(recs[0].Artifact, null, recs[0].RowId));
        Add(menu, $"Copy {n} record(s)", () => Clipboard.SetText(string.Join(Environment.NewLine, recs.Select(r => r.Text))), "Ctrl+C");
        if (EvidenceOrigin.RecordedSources(recs[0].Artifact) is { Length: > 0 } sources && recs.All(r => r.Artifact == recs[0].Artifact))
            Add(menu, "Export data source…", () => _ = main.Details.ExportSourceFiles(main.SessionOf(recs[0].Artifact), recs[0].Artifact.Name, sources));
        if (g.DataContext is ArtifactViewModel av)
            Add(menu, av.HasConversation ? $"Export {n} message(s) as HTML chat…" : $"Export {n} record(s)…",
                () => _ = av.ExportRows(recs.Select(r => r.RowId).ToList(), $"{n} selected records"));
    }

    /// <summary>Right-click on chat bubbles (tags, check, AI, copy) or gallery tiles (check, AI, copy the names).</summary>
    static void BuildList(ListBox l)
    {
        var menu = l.ContextMenu!;
        menu.Items.Clear();
        var main = Main;
        if (l.DataContext is ChatViewModel c && l.SelectedItems.OfType<MessageItem>().ToHashSet() is var sel && c.Chat.Messages.Where(sel.Contains).ToList() is { Count: > 0 } msgs)
        {
            var ids = msgs.Select(m => m.RowId).ToList();
            var n = ids.Count;
            menu.Items.Add(TagsMenu(tag => main.SetTag(ids.Select(id => (c.Artifact, id)), tag, true)));
            Add(menu, $"Check {n} message(s)", () => c.Check(ids, true));
            Add(menu, $"Uncheck {n} message(s)", () => c.Check(ids, false));
            menu.Items.Add(AiMenu(main, c.Session, c.ScopeOf(msgs, "selected "), c.Translation));
            menu.Items.Add(new Separator());
            Add(menu, $"Copy {n} message(s)", () => Clipboard.SetText(string.Join(Environment.NewLine, msgs.Select(m => $"{m.Time}\t{m.Sender}\t{m.Text}"))));
        }
        else if (l.DataContext is GalleryViewModel gv && l.SelectedItems.OfType<GalleryItem>().ToList() is { Count: > 0 } items)
        {
            var n = items.Count;
            menu.Items.Add(TagsMenu(tag => gv.TagFiles(items, tag)));
            Add(menu, $"Check {n} file(s)", () => items.ForEach(i => i.IsChecked = true));
            Add(menu, $"Uncheck {n} file(s)", () => items.ForEach(i => i.IsChecked = false));
            var ai = new MenuItem { Header = "AI" };
            ai.Items.Add(new MenuItem { Header = "Reading the files…", IsEnabled = false });
            menu.Items.Add(AiState.Instance.Enabled ? ai : Off(ai));
            if (AiState.Instance.Enabled) _ = FillAsync(); // files only in the extraction are copied out first, for the AI to read them
            async Task FillAsync()
            {
                var media = await gv.Readable(items.Select(i => i.Media));
                var built = AiMenu(main, gv.Session, new AiScope($"{n:N0} selected media files of the gallery", [], media), null);
                ai.Items.Clear();
                foreach (var x in built.Items.Cast<object>().ToList()) { built.Items.Remove(x); ai.Items.Add(x); }
            }
            menu.Items.Add(new Separator());
            Add(menu, $"Copy {n} file name(s)", () => Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.Media.SourcePath ?? i.Media.Name))));
        }
        else if (l.DataContext is ArtifactViewModel av && l.SelectedItems.OfType<GalleryItem>().ToList() is { Count: > 0 } tiles)
        {
            var ids = tiles.Select(t => t.RowId).Distinct().ToList();
            var n = ids.Count;
            var s = main.SessionOf(av.Artifact);
            menu.Items.Add(TagsMenu(tag => main.SetTag(ids.Select(id => (av.Artifact, id)), tag, true)));
            Add(menu, $"Check {n} record(s)", () => av.Check(ids, true));
            Add(menu, $"Uncheck {n} record(s)", () => av.Check(ids, false));
            if (s is not null)
                menu.Items.Add(AiMenu(main, s, new AiScope($"{tiles.Count:N0} selected media files of {av.Artifact.Name}", [], [.. tiles.Select(t => t.Media)]), null));
            menu.Items.Add(new Separator());
            Add(menu, $"Export {n} record(s)…", () => _ = av.ExportRows(ids, $"{n} selected records"));
            Add(menu, $"Copy {tiles.Count} file name(s)", () => Clipboard.SetText(string.Join(Environment.NewLine, tiles.Select(i => i.Media.SourcePath ?? i.Media.Name))));
        }
        else menu.Items.Add(new MenuItem { Header = "Select items first", IsEnabled = false });
    }

    /// <summary>"AI ▸" for what was right-clicked: records are translated (shown in place) or summarized; images described
    /// with their text read (OCR) or classified; audio transcribed; or the AI window opened on them for anything else.</summary>
    static MenuItem Off(MenuItem ai)
    {
        ai.Items.Clear();
        ai.IsEnabled = false;
        ai.Header = "AI (off: turn it on in AI settings)";
        return ai;
    }

    static MenuItem AiMenu(MainViewModel main, CaseSession s, AiScope scope, TranslationView? shown)
    {
        var ai = new MenuItem { Header = "AI" };
        if (!AiState.Instance.Enabled) return Off(ai);
        void Run(string header, Func<AiViewModel, Task> job) => Add(ai, header, () => { if (main.OpenAi(s, scope) is { } vm) _ = job(vm); });
        if (scope.Records.Count > 0)
        {
            Add(ai, $"Translate into {Settings.Current.Ai.TranslateTo}", () => _ = main.TranslateQuietly(s, scope, shown));
            Run("Summarize", vm => vm.SummarizeCommand.ExecuteAsync(null));
            Run("Explain (every technical value)", vm => vm.ExplainCommand.ExecuteAsync(null));
        }
        if (scope.Media.Count(m => m.IsImage) is > 0 and var images)
        {
            Run($"Describe and read the text (OCR) of {images} image(s)", vm => vm.DescribeImagesCommand.ExecuteAsync(null));
            Run($"Classify {images} image(s)", vm => vm.ClassifyImagesCommand.ExecuteAsync(null));
        }
        if (scope.Media.Count(m => m.Kind == "audio") is > 0 and var audio)
            Run($"Transcribe {audio} audio file(s)", vm => vm.TranscribeCommand.ExecuteAsync(null));
        if (ai.Items.Count > 0) ai.Items.Add(new Separator());
        Add(ai, "Open the AI window on them (questions, thematic review)…", () => main.OpenAi(s, scope));
        return ai;
    }

    static void Add(ItemsControl menu, string header, Action act, string gesture = "")
    {
        var m = new MenuItem { Header = header, InputGestureText = gesture };
        m.Click += (_, _) => act();
        menu.Items.Add(m);
    }

    /// <summary>"Tags ▸" with every tag (and its shortcut) and "Remove tag".</summary>
    public static MenuItem TagsMenu(Action<string?> apply)
    {
        var tags = new MenuItem { Header = "Tags" };
        foreach (var t in Settings.Current.Tags) Add(tags, t, () => apply(t), Settings.Current.TagShortcuts.GetValueOrDefault(t) ?? "");
        tags.Items.Add(new Separator());
        Add(tags, "Remove tag", () => apply(null));
        return tags;
    }

    /// <summary>The gesture a shortcut string stands for ("Ctrl+1"), or null when it is not a valid gesture.</summary>
    public static KeyGesture? Gesture(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return new KeyGestureConverter().ConvertFromString(null, CultureInfo.InvariantCulture, text) as KeyGesture; }
        catch (Exception e) when (e is NotSupportedException or ArgumentException) { return null; }
    }
}
