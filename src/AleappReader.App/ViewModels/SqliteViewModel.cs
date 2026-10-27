using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

/// <summary>A grid row of the SQLite reader: display text, raw values for the cell inspector, the carved record if any.</summary>
public sealed record SqlRow(string[] Cells, object?[] Raw, CarvedRecord? Carved = null);

/// <summary>
/// A SQLite file of the evidence, read from a temporary copy: tables, views and indexes with row counts and schema, a
/// table paged with the artifact filter syntax and sort, the read with or without the write-ahead log and what the log
/// changes, typed SELECT/WITH/PRAGMA, the records carved from free space, older page versions and the journal, a cell
/// decoded (timestamps, hex, plist or blind protobuf, image), CSV of what is shown.
/// </summary>
public sealed partial class SqliteViewModel : ObservableObject, IDisposable
{
    const int PageSize = 500;
    readonly SqliteFile _f;
    ArtifactInfo? _table;
    int _offset;
    long _total;
    string? _sort;
    bool _desc;
    CarveResult? _carved;

    public string Title => _f.Name;
    public string Origin { get; }
    public string FileInfo => $"Read from a temporary copy: the evidence file is not opened. Write-ahead log: {(_f.HasWal ? "present" : "none")}. " +
                              $"Rollback journal: {(_f.Journal is null ? "none" : "present, read only by the carver (never rolled back)")}.";
    public bool HasWal => _f.HasWal;

    [ObservableProperty] private IReadOnlyList<SqliteObject> objects = [];
    [ObservableProperty] private SqliteObject? selected;
    [ObservableProperty] private bool applyWal = true;
    [ObservableProperty] private string walText = "";
    [ObservableProperty] private string schema = "";
    [ObservableProperty] private IReadOnlyList<string> columns = [];
    [ObservableProperty] private IReadOnlyList<SqlRow> rows = [];
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string sql = "";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string cell = "";
    [ObservableProperty] private ImageSource? cellImage;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand))] private bool paged;
    [ObservableProperty] private bool busy;

    public SqliteViewModel(SqliteFile f, string origin)
    {
        _f = f;
        Origin = origin;
        Load();
    }

    void Load()
    {
        var name = Selected?.Name;
        try
        {
            Objects = _f.Objects(ApplyWal);
            WalText = !_f.HasWal ? "" : _f.WalDifference() is { Count: > 0 } d
                ? "The write-ahead log changes: " + string.Join(", ", d.Select(x => $"{x.Table} ({N(x.MainOnly)} rows in the main file alone, {N(x.WithWal)} with the log)"))
                : "The write-ahead log changes no table's row count (rows can still differ: carve to see older versions).";
            Selected = Objects.FirstOrDefault(o => o.Name == name) ?? Objects.FirstOrDefault(o => o.Type == "table");
        }
        catch (SqliteException e) { Status = "Cannot read the database: " + e.Message; }
    }

    static string N(long? n) => n is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "no table";

    partial void OnApplyWalChanged(bool value) => Load();

    partial void OnSelectedChanged(SqliteObject? value)
    {
        Schema = value?.Sql ?? "";
        (_offset, _sort, _desc, Filter) = (0, null, false, "");
        if (value is null) return;
        if (value.Type == "table" && value.HasRowId) ShowTable();
        else if (value.Type is "table" or "view")
        {
            Sql = $"SELECT * FROM {ArtifactQuery.Q(value.Name)}";
            RunSql();
        }
        else { (Columns, Rows, Paged) = ([], [], false); Status = $"{value.Type} on {value.Table}: see its schema"; }
    }

    [RelayCommand]
    void ShowTable()
    {
        if (Selected is not { Type: "table", HasRowId: true } t) return;
        try
        {
            using var con = _f.Connect(ApplyWal);
            _table = _f.AsArtifact(con, t.Name);
            var p = ArtifactQuery.Query(con, _table, new PageRequest(_offset, PageSize, Filter.Trim() is { Length: > 0 } q ? q : null, _sort, _desc));
            _total = p.Total;
            Columns = ["rowid", .. p.Columns.Select(c => c.Display)];
            Rows = p.Rows.Select(r => new SqlRow([r.RowId.ToString(CultureInfo.InvariantCulture), .. r.Values.Select(Text)], [r.RowId, .. r.Values])).ToList();
            Paged = true;
            Status = _total == 0 ? "No rows" : $"Rows {_offset + 1:N0}–{_offset + Rows.Count:N0} of {_total:N0}{(Filter.Trim() == "" ? "" : " matching the filter")}";
        }
        catch (SqliteException e) { Status = e.Message; }
    }

    bool CanNext => Paged && _offset + PageSize < _total;
    bool CanPrevious => Paged && _offset > 0;
    [RelayCommand(CanExecute = nameof(CanNext))] void Next() { _offset += PageSize; ShowTable(); PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanPrevious))] void Previous() { _offset = Math.Max(0, _offset - PageSize); ShowTable(); PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged(); }

    /// <summary>Sorts the shown table by a column (0 is the rowid); a second click on it reverses the order.</summary>
    public void SortBy(int column)
    {
        if (!Paged || _table is null) return;
        var sql = column == 0 ? null : _table.Columns[column - 1].Sql;
        _desc = sql == _sort && !_desc;
        (_sort, _offset) = (sql, 0);
        ShowTable();
    }

    [RelayCommand]
    void RunSql()
    {
        Paged = false;
        try
        {
            var r = _f.Run(Sql, ApplyWal);
            Columns = r.Columns;
            Rows = r.Rows.Select(v => new SqlRow(v.Select(Text).ToArray(), v)).ToList();
            Status = $"{Rows.Count:N0} rows{(r.Cut ? " (the first ones only)" : "")}";
        }
        catch (Exception e) when (e is SqliteException or InvalidOperationException) { (Columns, Rows, Status) = ([], [], e.Message); }
    }

    /// <summary>Records recovered by carving: of the selected table, or of every table.</summary>
    [RelayCommand]
    async Task Carve(string? scope)
    {
        Busy = true;
        Status = "Carving free space, older page versions and the journal…";
        try { _carved ??= await Task.Run(() => _f.Carve()); }
        catch (Exception e) when (e is IOException or SqliteException) { Status = "Carving failed: " + e.Message; return; }
        finally { Busy = false; }
        Paged = false;
        var one = scope != "all" && Selected is { Type: "table" } t ? t.Name : null;
        var recs = _carved.Records.Where(r => one is null || r.Table == one).ToList();
        string[] head = ["Kind", "Confidence", "Origin", "Page", "Offset", "rowid"];
        if (one is not null)
        {
            Columns = [.. head, .. recs.FirstOrDefault()?.Columns ?? []];
            Rows = recs.Select(r => new SqlRow([.. Meta(r), .. r.Values.Select(Text)], [r.Kind, r.Confidence, r.Origin, r.Page, r.Offset, r.RowId, .. r.Values], r)).ToList();
        }
        else
        {
            Columns = ["Table", .. head, "Values"];
            Rows = recs.Select(r => (r, values: string.Join("; ", r.Columns.Zip(r.Values, (c, v) => $"{c}={Text(v)}"))))
                .Select(x => new SqlRow([x.r.Table, .. Meta(x.r), x.values], [x.r.Table, x.r.Kind, x.r.Confidence, x.r.Origin, x.r.Page, x.r.Offset, x.r.RowId, x.values], x.r)).ToList();
        }
        var kinds = string.Join(", ", recs.GroupBy(r => r.Kind).Select(g => $"{g.Count():N0} {g.Key}"));
        Status = $"{recs.Count:N0} records recovered{(one is null ? " from every table" : " of " + one)}{(kinds == "" ? "" : ": " + kinds)}. Live rows are never repeated.";
        Notes = string.Join("\n", _carved.Notes);
    }

    static string[] Meta(CarvedRecord r) =>
        [r.Kind, r.Confidence, r.Origin, r.Page.ToString(CultureInfo.InvariantCulture), r.Offset.ToString(CultureInfo.InvariantCulture), r.RowId?.ToString(CultureInfo.InvariantCulture) ?? ""];

    static string Text(object? v) => v switch
    {
        null => "",
        byte[] b => $"<{b.Length} bytes>",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>The selected cell decoded: text, the epochs a number reads as, a BLOB as binary plist, image and hex.</summary>
    public void Inspect(SqlRow? row, int column)
    {
        CellImage = null;
        if (row is null || column < 0 || column >= row.Raw.Length) { Cell = ""; return; }
        var v = row.Raw[column];
        var name = column < Columns.Count ? Columns[column] : "";
        switch (v)
        {
            case null: Cell = $"{name}: NULL"; break;
            case byte[] b:
                var type = RawFiles.Sniff(b);
                var parts = new List<string> { $"{name}: {b.Length:N0} bytes{(type == "" ? "" : ", " + type)}" };
                if (PlistTree.Blob(b) is { } tree) parts.Add(PlistTree.ToText(tree));
                if (type.EndsWith("image", StringComparison.Ordinal)) CellImage = Image(b);
                parts.Add(RawFiles.HexDump(b.AsSpan(0, Math.Min(b.Length, 64 << 10)), 0));
                Cell = string.Join("\n\n", parts);
                break;
            default:
                var s = Text(v);
                Cell = $"{name}: {s}" + (ValueFormatter.TimestampCandidates(s) is { } t ? "\n\n" + t : "");
                break;
        }
    }

    internal static ImageSource? Image(byte[] b)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(b);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException) { return null; }
    }

    /// <summary>CSV of what the grid shows: a table's every row matching the filter, a query's or carve's rows.</summary>
    [RelayCommand]
    void ExportCsv()
    {
        var dlg = new SaveFileDialog { Title = "Export CSV", Filter = "CSV (*.csv)|*.csv", FileName = $"{Path.GetFileNameWithoutExtension(_f.Name)}-{Selected?.Name ?? "query"}.csv" };
        if (dlg.ShowDialog() != true) return;
        using var w = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(true));
        w.WriteLine($"# {Origin} · {(ApplyWal ? "write-ahead log applied" : "main file alone")} · exported {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC by LEXIS");
        w.WriteLine(string.Join(",", Columns.Select(CsvExporter.Cell)));
        if (Paged && _table is not null)
        {
            using var con = _f.Connect(ApplyWal);
            foreach (var r in ArtifactQuery.All(con, _table, Filter.Trim() is { Length: > 0 } q ? q : null))
                w.WriteLine(string.Join(",", new[] { r.RowId.ToString(CultureInfo.InvariantCulture) }.Concat(r.Values.Select(Text)).Select(CsvExporter.Cell)));
        }
        else foreach (var r in Rows) w.WriteLine(string.Join(",", r.Cells.Select(CsvExporter.Cell)));
        Status = $"Exported to {dlg.FileName}";
        ExportLog.Add($"SQLite {_f.Name}: {Selected?.Name ?? "query"} (CSV)", dlg.FileName);
    }

    public void Dispose() => _f.Dispose();
}
