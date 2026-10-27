using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

/// <summary>One grid row: formatted cells for display, raw values for details and export.</summary>
public partial class GridRow(long rowId, string[] cells, object?[] raw, string? tag, bool isChecked = false,
    Action<GridRow>? onCheck = null, string recovered = "") : ObservableObject
{
    public long RowId { get; } = rowId;
    public string[] Cells { get; } = cells;
    public object?[] Raw { get; } = raw;
    /// <summary>Why the record is a recovered (deleted) one, "" for a live record.</summary>
    public string Recovered { get; } = recovered;
    /// <summary>A row still loading.</summary>
    public bool IsPlaceholder => RowId < 0;
    [ObservableProperty] private string? tag = tag;
    [ObservableProperty] private bool isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => onCheck?.Invoke(this);

    public static GridRow Placeholder(int columns) => new(-1, [.. Enumerable.Repeat("…", columns)], new object?[columns], null);
}

/// <summary>The saved AI translations of one table's records, shown instead of the original text, beside it, or not at all.</summary>
public partial class TranslationView(CaseSession s, ArtifactInfo a) : ObservableObject
{
    public const string Original = "Original", Translated = "Translated", Both = "Both";
    public static string[] Modes { get; } = [Original, Translated, Both];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Shown))] private string mode = Original;
    /// <summary>Translations show (Translated or Both): in a table, as a column beside the record's own.</summary>
    public bool Shown => Mode != Original;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CountText))] private IReadOnlyDictionary<long, string> byRow = new Dictionary<long, string>();
    public string CountText => ByRow.Count == 0 ? "no translations yet" : $"{ByRow.Count:N0} translated";
    /// <summary>What the AI read in each file (description, text in it and its translation, transcription), by media ref
    /// id, shown under the file with the translations.</summary>
    [ObservableProperty] private IReadOnlyDictionary<string, string> byMedia = new Dictionary<string, string>();

    partial void OnModeChanged(string value) => _ = LoadAsync(); // translations made since, in the AI window too

    public async Task LoadAsync()
    {
        var prefix = $"[{a.TableName}#";
        try
        {
            var store = AiStore.For(s);
            ByRow = await Task.Run(() => AiText.Translations(store.Results("Translation"))
                .Select(kv => (kv.Value, Id: kv.Key.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(kv.Key[prefix.Length..^1], out var id) ? id : -1))
                .Where(x => x.Id >= 0).ToDictionary(x => x.Id, x => x.Value));
            ByMedia = await Task.Run(() => store.MediaResults().SelectMany(r => r.Cited.Where(k => k.StartsWith("[media#", StringComparison.Ordinal)).Select(k => (k, r)))
                .GroupBy(x => x.k).ToDictionary(g => g.Key[7..^1], g => string.Join("\n", AiText.Notes(g.Select(x => x.r), g.Key).Select(n => $"{n.Name}: {n.Text}"))));
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { } // AI base unreadable: no translations shown
    }
}

/// <summary>Table view of any artifact, driven only by its manifest columns. Rows are read as the grid scrolls.</summary>
public partial class ArtifactViewModel : ObservableObject, IRefreshable, IDisposable, IColumnFilters
{
    readonly MainViewModel _main;
    readonly CaseSession _s;
    long? _selectOnLoad;
    RecordCursor? _cursor;
    int _version;
    readonly List<ColumnFilter> _columnFilters = [];
    // Every record is checked at first (_all); _flip holds the exceptions: checked = _all != _flip.Contains(id).
    // lock _flip: read while pages load on worker threads.
    bool _all = true;
    readonly HashSet<long> _flip = [];
    bool On(long id) => _all != _flip.Contains(id);

    public ArtifactInfo Artifact { get; }
    /// <summary>The artifact's source files on the device: the first in the header, all of them in its tooltip.</summary>
    public string SourceLine => EvidenceOrigin.Sources(Artifact) is { Count: > 0 } s ? s[0] + (s.Count > 1 ? $" (+{s.Count - 1} more)" : "") : "";
    public string SourcesTip => EvidenceOrigin.Join(EvidenceOrigin.Sources(Artifact), 40);
    public bool HasConversation => Artifact.Conversation is not null;
    public bool HasGeo => GeoExtractor.GeoPairs(Artifact).Count > 0;

    [ObservableProperty] private VirtualRows<GridRow>? rows;
    [ObservableProperty] private IReadOnlyList<ColumnInfo> columns = [];
    [ObservableProperty] private string filter = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CountText), nameof(EmptyText))] private long total;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CheckedText), nameof(HasChecked))] private int checkedCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasColumnFilters))] private int columnFilterCount;
    [ObservableProperty] private GridRow? selectedRow;
    [ObservableProperty] private int selectedTab;
    [ObservableProperty] private ConversationViewModel? conversation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EmptyText))] private bool loading;
    string? _sortColumn;
    bool _desc;

    public string CountText => Total == 0 ? "No records" : $"{Total:N0} records";
    public string EmptyText => Loading || Total > 0 ? "" : Filter != "" || HasColumnFilters ? "No records match the filters" : "No records";
    public string CheckedText => _all && _flip.Count == 0 ? "All checked" : $"{CheckedCount:N0} checked";
    public bool HasChecked => CheckedCount > 0;
    public bool HasColumnFilters => ColumnFilterCount > 0;

    public ArtifactViewModel(MainViewModel main, CaseSession s, ArtifactInfo artifact, string? filter = null, long? selectRowId = null,
        ColumnFilter? columnFilter = null)
    {
        _main = main;
        _s = s;
        Artifact = artifact;
        _selectOnLoad = selectRowId;
        if (columnFilter is not null) { _columnFilters.Add(columnFilter); ColumnFilterCount = 1; }
        this.filter = filter ?? "";
        Translation = new TranslationView(s, artifact);
        HasRecovered = ConversationBuilder.RecoveredReader(artifact.Columns) is not null;
        _chats = ConversationBuilder.MessagesOf(s.Manifest.Artifacts, artifact);
        _ = LoadAsync();
        _ = Translation.LoadAsync();
    }

    public TranslationView Translation { get; }
    /// <summary>The table marks recovered (deleted) records: the grid shows a column for the mark.</summary>
    public bool HasRecovered { get; }

    // A chat list (WeChat Conversations…) beside its app's messages: a row opens its chat and shows the chat's picture.
    readonly ArtifactInfo? _chats;
    Task<ConversationSet>? _chatSet;
    public bool CanOpenChat => _chats is not null;

    Task<ConversationSet> ChatSet() => _chatSet ??= Task.Run(() => { using var con = _s.OpenDb(); return ConversationBuilder.Build(con, _s, _chats!, 50000); });

    /// <summary>The chat whose ID is one of the row's values.</summary>
    static ConversationItem? ChatOf(ConversationSet set, GridRow r)
    {
        var values = r.Cells.Select(c => c.Trim()).Where(v => v != "").ToHashSet(StringComparer.Ordinal);
        return set.Conversations.FirstOrDefault(c => values.Contains(c.Key));
    }

    [RelayCommand(CanExecute = nameof(CanOpenChat))]
    async Task OpenChat()
    {
        if (SelectedRow is not { IsPlaceholder: false } r) { _main.Status = "Select a chat first."; return; }
        try
        {
            var set = await ChatSet();
            if (ChatOf(set, r) is { } c) _main.OpenChat(new ChatViewModel(_main, _s, _chats!, c, set.Columns, set.Truncated));
            else _main.Status = $"No chat of {_chats!.Name} has an ID in this row.";
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { _main.Status = $"Could not read {_chats!.Name}: {e.Message}"; }
    }

    async Task ShowChatPictureAsync(GridRow r, int shown)
    {
        MediaRef? pic;
        try
        {
            if (ChatOf(await ChatSet(), r) is not { } c) return;
            pic = await Task.Run(() => { using var con = _s.OpenDb(); return new ContactDirectory(con, _s, _chats!).Find(c.Key)?.Picture; });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return; } // messages or contacts unreadable: no picture
        if (pic is null || _main.Details.Shown != shown) return; // the details moved on
        var at = _main.Details.Fields.TakeWhile(f => !(f.Header && f.Name == "AI")).Count();
        _main.Details.Fields.Insert(at, new Field("Profile picture", pic.Name));
        _main.Details.Media.Add(pic);
    }

    public void Refresh() => _ = LoadAsync();

    async Task LoadAsync()
    {
        var version = ++_version;
        Loading = true;
        try
        {
            var req = new PageRequest(Filter: Filter, SortColumn: _sortColumn, Descending: _desc, ColumnFilters: [.. _columnFilters]);
            var cursor = await Task.Run(() => RecordCursor.Open(_s, Artifact, req with { SkipColumns = _s.SearchSkips(Artifact) }));
            if (version != _version) { _ = Task.Run(cursor.Dispose); return; }
            var old = _cursor;
            _cursor = cursor;
            if (old is not null) _ = Task.Run(old.Dispose);
            if (!Columns.SequenceEqual(cursor.Columns)) Columns = cursor.Columns;
            Total = cursor.Count;
            Recount();
            var cols = cursor.Columns;
            Rows = new VirtualRows<GridRow>(cursor.Count, (start, n) => Read(cursor, cols, start, n), () => GridRow.Placeholder(cols.Count));
            if (_selectOnLoad is { } id)
            {
                _selectOnLoad = null;
                var pos = await Task.Run(() => cursor.PositionOf(id));
                if (pos >= 0 && version == _version) SelectedRow = await Rows.GetAsync((int)pos);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { _main.Status = $"{Artifact.Name}: {e.Message}"; }
        finally { if (version == _version) Loading = false; }
    }

    List<GridRow> Read(RecordCursor cursor, IReadOnlyList<ColumnInfo> cols, long start, int n)
    {
        var tags = _s.Tags.ForTable(Artifact.TableName);
        var recovered = ConversationBuilder.RecoveredReader(cols);
        var rows = cursor.Fetch(start, n);
        bool[] isChecked;
        lock (_flip) isChecked = rows.Select(r => On(r.RowId)).ToArray();
        return rows.Select((r, k) => new GridRow(r.RowId,
            r.Values.Select((v, i) => ValueFormatter.Display(v, cols[i])).ToArray(), r.Values,
            tags.GetValueOrDefault(r.RowId)?.Tag, isChecked[k], OnCheck, recovered?.Invoke(r.Values) ?? "")).ToList();
    }

    void OnCheck(GridRow row)
    {
        lock (_flip) { if (row.IsChecked == _all) _flip.Remove(row.RowId); else _flip.Add(row.RowId); }
        Recount();
    }

    // ponytail: with all checked, exceptions outside the current filter still count against Total (an estimate until
    // an action resolves the exact ids).
    void Recount()
    {
        lock (_flip) CheckedCount = (int)Math.Max(0, _all ? Total - _flip.Count : _flip.Count);
        OnPropertyChanged(nameof(CheckedText));
    }

    /// <summary>The checked records of the current view (filters included), read from the database when all are checked.</summary>
    async Task<List<long>> CheckedIds()
    {
        bool all;
        HashSet<long> flip;
        lock (_flip) (all, flip) = (_all, [.. _flip]);
        if (!all) return [.. flip];
        if (_cursor is not { } c) return [];
        return await Task.Run(() => c.RowIds().Where(id => !flip.Contains(id)).ToList());
    }

    partial void OnSelectedRowChanged(GridRow? value)
    {
        if (value is not { IsPlaceholder: false }) return;
        _main.Details.ShowRecord(_s, Artifact, Columns, value.RowId, value.Raw);
        if (_chats is not null) _ = ShowChatPictureAsync(value, _main.Details.Shown);
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 1 && Conversation is null && HasConversation) Conversation = new ConversationViewModel(_main, _s, Artifact);
        if (value == 2) _ = LoadTilesAsync();
    }

    /// <summary>A table whose records carry pictures, videos or audio (not a chat, which has its own view) also shows as
    /// tiles; so does any table of the Media category, whose records name their files by path in the extraction. Not a
    /// list of contacts or accounts: their pictures are their profile pictures, shown on their rows in the table.</summary>
    public bool HasThumbnails => !HasConversation && MacroCategories.Of(Artifact) is var macro && macro != MacroCategories.Contacts && macro != MacroCategories.Accounts
        && !Artifact.Name.Contains("contact", StringComparison.OrdinalIgnoreCase)
        && (Artifact.Columns.Any(c => c.IsMedia) || macro == MacroCategories.Media);
    readonly Dictionary<string, System.Windows.Media.ImageSource?> _cellThumbs = [];

    /// <summary>The first picture (or a video's frame) a media cell refers to, small, for the table's media columns.
    /// ponytail: decoded on the UI thread as rows scroll in, cached per value; load in the background if scrolling stutters.</summary>
    public System.Windows.Media.ImageSource? CellThumb(object? raw)
    {
        if (raw?.ToString() is not { Length: > 0 } key) return null;
        if (_cellThumbs.TryGetValue(key, out var known)) return known;
        System.Windows.Media.ImageSource? thumb = null;
        try
        {
            using var con = _s.OpenDb();
            if (new MediaResolver(_s).Resolve(con, raw).FirstOrDefault(m => m.IsImage || m.Kind == "video") is { } m)
                thumb = m.IsImage ? GalleryViewModel.Thumb(_s, m, 64) : GalleryViewModel.VideoThumb(_s, m, 64);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                      or Microsoft.Data.Sqlite.SqliteException) { }
        return _cellThumbs[key] = thumb;
    }

    const int MaxTiles = 500; // ponytail: no paging (the wrap panel does not virtualize); page like the gallery if 500 is short
    public System.Collections.ObjectModel.ObservableCollection<GalleryItem> Tiles { get; } = [];
    [ObservableProperty] private string tilesText = "";
    [ObservableProperty] private GalleryItem? selectedTile;
    int _tiles; // a newer load stops the thumbnails of the tiles it replaced

    /// <summary>The media of the records in view (filters included), one tile per file, checked as their record is.</summary>
    async Task LoadTilesAsync()
    {
        var v = ++_tiles;
        var (filter, columnFilters, skip) = (Filter, _columnFilters.ToList(), _s.SearchSkips(Artifact));
        TilesText = "Reading the media…";
        var found = await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            var cols = ArtifactQuery.ExistingColumns(con, Artifact);
            var media = Enumerable.Range(0, cols.Count).Where(i => cols[i].IsMedia).ToList();
            var resolver = new MediaResolver(_s);
            var x = _s.Extraction;
            var list = new List<(long Row, MediaRef M)>();
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ArtifactQuery.All(con, Artifact, filter, columnFilters: columnFilters, skip: skip))
            {
                var n = list.Count;
                foreach (var i in media) list.AddRange(resolver.Resolve(con, row.Values[i]).Select(m => (row.RowId, m)));
                // none in the report: the files the record names by path, found in the extraction (read when shown, never copied for a tile)
                if (list.Count == n && x is not null)
                {
                    var texts = row.Values.OfType<string>().Where(t => ExtractionFiles.MediaExtensions.Contains(Path.GetExtension(t.Trim()))).ToList();
                    foreach (var rel in texts.Select(t => x.Find(t, texts)).OfType<string>().Where(named.Add))
                        list.Add((row.RowId, x.ToMedia(rel)));
                }
                if (list.Count >= MaxTiles) break;
            }
            return list;
        });
        if (v != _tiles) return;
        Tiles.Clear();
        var tags = _s.Tags.ForTable(Artifact.TableName);
        lock (_flip) foreach (var (row, m) in found) Tiles.Add(new GalleryItem(m, [], On(row), OnTileCheck) { RowId = row, Tag = tags.GetValueOrDefault(row)?.Tag });
        TilesText = found.Count == 0 ? "No pictures, videos or audio in the records in view"
            : $"{found.Count:N0} files of the records in view" + (found.Count >= MaxTiles ? $" (the first {MaxTiles:N0}: filter the table to see others)" : "");
        _ = GalleryViewModel.LoadThumbnailsAsync(_s, Tiles.ToList(), () => v != _tiles);
    }

    void OnTileCheck(GalleryItem t) => Check([t.RowId], t.IsChecked);

    partial void OnSelectedTileChanged(GalleryItem? value)
    {
        if (value is not null) _main.Details.ShowRecord(_s, Artifact, value.RowId);
    }

    [RelayCommand]
    void ApplyFilter() => _ = LoadAsync();

    [RelayCommand]
    void ClearFilter()
    {
        Filter = "";
        ApplyFilter();
    }

    /// <summary>Header click: ascending, then descending on the same column.</summary>
    [RelayCommand]
    void Sort(string sqlColumn) => SortBy(sqlColumn, _sortColumn == sqlColumn && !_desc);

    public void SortBy(string sqlColumn, bool descending)
    {
        (_sortColumn, _desc) = (sqlColumn, descending);
        _ = LoadAsync();
    }

    public ColumnFilter? FilterOf(string sqlColumn) => _columnFilters.FirstOrDefault(f => f.Sql == sqlColumn);

    /// <summary>Sets (or with null clears) the condition on one column.</summary>
    public void SetColumnFilter(string sqlColumn, ColumnFilter? f)
    {
        _columnFilters.RemoveAll(x => x.Sql == sqlColumn);
        if (f is not null) _columnFilters.Add(f);
        ColumnFilterCount = _columnFilters.Count;
        _ = LoadAsync();
    }

    [RelayCommand]
    void ClearColumnFilters()
    {
        _columnFilters.Clear();
        ColumnFilterCount = 0;
        _ = LoadAsync();
    }

    /// <summary>Checks or unchecks records by id (the boxes of rows in memory follow).</summary>
    public void Check(IEnumerable<long> rowIds, bool on)
    {
        lock (_flip)
        {
            foreach (var id in rowIds) if (on == _all) _flip.Remove(id); else _flip.Add(id);
            foreach (var r in Rows?.Loaded ?? []) r.IsChecked = On(r.RowId);
        }
        SyncTiles();
        Recount();
    }

    void SetAll(bool on)
    {
        lock (_flip)
        {
            _all = on;
            _flip.Clear();
            foreach (var r in Rows?.Loaded ?? []) r.IsChecked = on;
        }
        SyncTiles();
        Recount();
    }

    /// <summary>Every tile of a record shows the record's box.</summary>
    void SyncTiles()
    {
        bool[] on;
        lock (_flip) on = Tiles.Select(t => On(t.RowId)).ToArray();
        for (var i = 0; i < on.Length; i++) Tiles[i].IsChecked = on[i]; // unchanged values raise nothing: no loop back
    }

    [RelayCommand] void CheckAll() => SetAll(true);
    [RelayCommand] void ClearChecks() => SetAll(false);

    [RelayCommand]
    async Task ExportChecked()
    {
        var ids = await CheckedIds();
        if (ids.Count > 0) await ExportRows(ids, $"{ids.Count} checked records");
        else _main.Status = "Nothing checked";
    }

    public void UpdateTag(IReadOnlyCollection<long> rowIds, string? tag)
    {
        foreach (var row in Rows?.Loaded.Where(r => rowIds.Contains(r.RowId)) ?? []) row.Tag = tag;
        foreach (var t in Tiles.Where(t => rowIds.Contains(t.RowId))) t.Tag = tag;
    }

    [RelayCommand]
    void ShowOnMap() => _main.NavigateToMap(Artifact);

    /// <summary>AI over the checked records, or else every record in view (filters included).</summary>
    [RelayCommand]
    async Task Ai() { if (await Scope() is { } sc) _main.OpenAi(_s, sc); }

    /// <summary>The checked records translated in place (Original / Translated / Both), without the AI window.</summary>
    [RelayCommand]
    async Task Translate() { if (await Scope() is { } sc) await _main.TranslateQuietly(_s, sc, Translation); }

    [RelayCommand]
    async Task Summarize() { if (await Scope() is { } sc) _ = _main.OpenAi(_s, sc)?.SummarizeCommand.ExecuteAsync(null); }

    /// <summary>The checked records in view: with all checked, the view itself (filters included); null, said in the
    /// status bar, when none is checked.</summary>
    async Task<AiScope?> Scope()
    {
        bool whole;
        lock (_flip) whole = _all && _flip.Count == 0;
        if (whole)
            return new AiScope($"the records of {Artifact.Name}" + (Describe() is { } f ? $" ({f})" : ""),
                [new AiSelection(Artifact, null, Filter, [.. _columnFilters])], []);
        var ids = await CheckedIds();
        if (ids.Count == 0) { _main.Status = "Nothing checked: check the records first"; return null; }
        return new AiScope($"{ids.Count:N0} checked records of {Artifact.Name}", [new AiSelection(Artifact, ids)], []);
    }

    /// <summary>The filters in force, as written in exported reports.</summary>
    string? Describe()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Filter)) parts.Add($"\"{Filter.Trim()}\"");
        var cols = Columns;
        parts.AddRange(_columnFilters.Select(f => $"{cols.FirstOrDefault(c => c.Sql == f.Sql)?.Display ?? f.Sql} {f.Op} {f.Value}{(f.Value2 is null ? "" : "–" + f.Value2)}".TrimEnd()));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    [RelayCommand]
    async Task ExportCsv()
    {
        var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json", FileName = HtmlReportExporter.SafeName(Artifact.Name) + ".csv" };
        if (dlg.ShowDialog() != true) return;
        await Export(dlg.FileName, (sec) => WriteData(dlg.FileName, sec));
    }

    [RelayCommand]
    async Task ExportHtml()
    {
        var dlg = new SaveFileDialog { Filter = "HTML report (*.html)|*.html", FileName = HtmlReportExporter.SafeName(Artifact.Name) + ".html" };
        if (dlg.ShowDialog() != true) return;
        await Export(dlg.FileName, (sec) => HtmlReportExporter.Write(dlg.FileName, _s, [sec], Artifact.Name));
    }

    static void WriteData(string path, ExportSection sec)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) JsonExporter.Write(path, [sec]);
        else CsvExporter.Write(path, sec);
    }

    async Task Export(string path, Action<ExportSection> write)
    {
        var (filter, columnFilters, described) = (Filter, _columnFilters.ToList(), Describe());
        await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            var cols = ArtifactQuery.ExistingColumns(con, Artifact);
            write(new ExportSection(Artifact.Name, cols, ArtifactQuery.All(con, Artifact, filter, columnFilters: columnFilters, skip: _s.SearchSkips(Artifact)),
                _s.Tags.ForTable(Artifact.TableName), Artifact, described));
        });
        _main.Status = "Exported " + path;
        ExportLog.Add($"{Artifact.Name} ({Path.GetExtension(path).TrimStart('.').ToUpperInvariant()})", path, described ?? "every record in view", _s);
    }

    /// <summary>Exports the given records as an HTML report or CSV (a chat report for conversations).</summary>
    public async Task ExportRows(IReadOnlyList<long> ids, string selection)
    {
        if (HasConversation)
        {
            var chats = await Task.Run(() =>
            {
                using var con = _s.OpenDb();
                return ConversationBuilder.Build(con, _s, Artifact, ids.Count, ids).Conversations;
            });
            if (await ConversationViewModel.ExportChats(_s, Artifact, chats, selection) is { } status) _main.Status = status;
            return;
        }
        var dlg = new SaveFileDialog { Filter = "HTML report (*.html)|*.html|CSV (*.csv)|*.csv|JSON (*.json)|*.json", FileName = HtmlReportExporter.SafeName(Artifact.Name) + " - selection" };
        if (dlg.ShowDialog() != true) return;
        await Task.Run(() =>
        {
            using var con = _s.OpenDb();
            var sec = new ExportSection(Artifact.Name, ArtifactQuery.ExistingColumns(con, Artifact),
                ArtifactQuery.All(con, Artifact, rowIds: ids), _s.Tags.ForTable(Artifact.TableName), Artifact, selection);
            if (!Path.GetExtension(dlg.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase)) WriteData(dlg.FileName, sec);
            else HtmlReportExporter.Write(dlg.FileName, _s, [sec], Artifact.Name);
        });
        _main.Status = $"Exported {ids.Count} records to {dlg.FileName}";
        ExportLog.Add($"{Artifact.Name} ({Path.GetExtension(dlg.FileName).TrimStart('.').ToUpperInvariant()})", dlg.FileName, selection, _s);
    }

    public void Dispose()
    {
        _version++;
        _cursor?.Dispose(); // now, so closing the case can delete a zip case's temp folder
        _cursor = null;
    }
}
