using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

public sealed record RecordRow(long RowId, object?[] Values);

/// <summary>Rows plus the columns their values line up with.</summary>
public sealed record PageResult(long Total, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<RecordRow> Rows);

public sealed record PageRequest(int Offset = 0, int Limit = 500, string? Filter = null,
    string? SortColumn = null, bool Descending = false, IReadOnlyCollection<long>? RowIds = null,
    IReadOnlyList<ColumnFilter>? ColumnFilters = null, IReadOnlyCollection<string>? SkipColumns = null);

/// <summary>A condition on one column. <see cref="Op"/>: contains, notcontains, equals, empty, notempty, or between
/// (numbers, or epoch seconds for datetime columns; either bound may be null).</summary>
public sealed record ColumnFilter(string Sql, string Op, string? Value = null, string? Value2 = null)
{
    /// <summary>The same condition on a value already in memory (a list the app built itself, e.g. the chats).
    /// A datetime value is the formatted "yyyy-MM-dd HH:mm:ss" text in the case time zone.</summary>
    public bool Matches(string value, bool dateTime)
    {
        switch (Op)
        {
            case "contains": return value.Contains(Value ?? "", StringComparison.OrdinalIgnoreCase);
            case "notcontains": return !value.Contains(Value ?? "", StringComparison.OrdinalIgnoreCase);
            case "equals": return value == (Value ?? "");
            case "empty": return value == "";
            case "notempty": return value != "";
            case "between":
                if (value == "") return false;
                if (dateTime)
                {
                    static string At(string epoch) => ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(long.Parse(epoch, CultureInfo.InvariantCulture)))[..19];
                    var t = value.Length >= 19 ? value[..19] : value;
                    return (Value is not { Length: > 0 } lo || string.CompareOrdinal(t, At(lo)) >= 0) &&
                           (Value2 is not { Length: > 0 } hi || string.CompareOrdinal(t, At(hi)) <= 0);
                }
                if (!double.TryParse(value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
                return (Value is not { Length: > 0 } a || d >= double.Parse(a, CultureInfo.InvariantCulture)) &&
                       (Value2 is not { Length: > 0 } b || d <= double.Parse(b, CultureInfo.InvariantCulture));
            default: return true;
        }
    }
}

/// <summary>Dynamic, paged queries over any artifact table. Identifiers are quoted, values bound.</summary>
public static class ArtifactQuery
{
    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Manifest columns that really exist in the table, in manifest order.</summary>
    public static IReadOnlyList<ColumnInfo> ExistingColumns(SqliteConnection con, ArtifactInfo a)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({Q(a.TableName)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) present.Add(r.GetString(1));
        }
        return a.Columns.Where(c => present.Contains(c.Sql)).ToList();
    }

    public static PageResult Query(SqliteConnection con, ArtifactInfo a, PageRequest req)
    {
        var cols = ExistingColumns(con, a);
        if (cols.Count == 0) return new PageResult(0, cols, []);

        using var cmd = con.CreateCommand();
        var whereSql = Where(cmd, cols, req);

        cmd.CommandText = $"SELECT COUNT(*) FROM {Q(a.TableName)}{whereSql}";
        var total = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        cmd.CommandText = $"SELECT rowid, {Select(cols)} FROM {Q(a.TableName)}" +
                          $"{whereSql} ORDER BY {Order(cols, req)} LIMIT @lim OFFSET @off";
        cmd.Parameters.AddWithValue("@lim", req.Limit);
        cmd.Parameters.AddWithValue("@off", req.Offset);
        return new PageResult(total, cols, Read(cmd, cols.Count));
    }

    /// <summary>Each distinct value of a column (empty for NULL) with its row count, largest first; none when the
    /// table lacks the column.</summary>
    public static IReadOnlyList<(string Value, long Count)> ValueCounts(SqliteConnection con, ArtifactInfo a, string nameOrDisplay)
    {
        if (a.Column(nameOrDisplay) is not { } c || !ExistingColumns(con, a).Contains(c)) return [];
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(CAST({Q(c.Sql)} AS TEXT), ''), COUNT(*) FROM {Q(a.TableName)} GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT 100"; // a tree, not a column profile
        using var r = cmd.ExecuteReader();
        var list = new List<(string, long)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    internal static string Select(IReadOnlyList<ColumnInfo> cols, string table = "") =>
        string.Join(", ", cols.Select(c => table + Q(c.Sql)));

    internal static string Order(IReadOnlyList<ColumnInfo> cols, PageRequest req) =>
        cols.FirstOrDefault(c => c.Sql == req.SortColumn) is { } sort ? $"{Q(sort.Sql)} {(req.Descending ? "DESC" : "ASC")}, rowid" : "rowid";

    /// <summary>" WHERE ..." for the request's text filter, column filters and row ids ("" when none); values are bound.</summary>
    internal static string Where(SqliteCommand cmd, IReadOnlyList<ColumnInfo> cols, PageRequest req)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(req.Filter)) where.Add(FilterSql(cmd, cols, req.Filter, req.SkipColumns));
        if (req.ColumnFilters is { Count: > 0 } cf) where.AddRange(ColumnFiltersSql(cmd, cols, cf));
        if (req.RowIds is not null)
            where.Add(req.RowIds.Count == 0 ? "0" : $"rowid IN ({string.Join(',', req.RowIds)})"); // longs, not user text
        return where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
    }

    /// <summary>Rows of a "SELECT rowid, columns..." command.</summary>
    internal static List<RecordRow> Read(SqliteCommand cmd, int columns)
    {
        var rows = new List<RecordRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var values = new object?[columns];
            for (int i = 0; i < columns; i++) values[i] = r.IsDBNull(i + 1) ? null : r.GetValue(i + 1);
            rows.Add(new RecordRow(r.GetInt64(0), values));
        }
        return rows;
    }

    /// <summary>One condition per filter, on columns of the table only: the UI never supplies SQL text.</summary>
    static IEnumerable<string> ColumnFiltersSql(SqliteCommand cmd, IReadOnlyList<ColumnInfo> cols, IReadOnlyList<ColumnFilter> filters)
    {
        int n = 0;
        string P(object v) { var name = $"@c{n++}"; cmd.Parameters.AddWithValue(name, v); return name; }
        static object Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
        foreach (var f in filters)
        {
            if (cols.FirstOrDefault(c => c.Sql == f.Sql) is not { } c) continue;
            var col = Q(c.Sql);
            var text = $"CAST({col} AS TEXT)";
            yield return f.Op switch
            {
                "contains" => $"{text} LIKE {P("%" + EscapeLike(f.Value ?? "") + "%")} ESCAPE '\\'",
                "notcontains" => $"({col} IS NULL OR {text} NOT LIKE {P("%" + EscapeLike(f.Value ?? "") + "%")} ESCAPE '\\')",
                "equals" => $"{text} = {P(f.Value ?? "")}",
                "empty" => $"({col} IS NULL OR {text} = '')",
                "notempty" => $"({col} IS NOT NULL AND {text} <> '')",
                // every non-datetime LAVA column is TEXT: compare as numbers, not as text
                // numbers only: CAST('2024-05-01' AS REAL) would be 2024
                "between" => $"({col} IS NOT NULL AND {text} <> '' AND NOT (ltrim({text}, '-') GLOB '*[^0-9.]*')" +
                             (f.Value is { Length: > 0 } lo ? $" AND CAST({col} AS REAL) >= {P(Num(lo))}" : "") +
                             (f.Value2 is { Length: > 0 } hi ? $" AND CAST({col} AS REAL) <= {P(Num(hi))}" : "") + ")",
                _ => "1",
            };
        }
    }

    /// <summary>What a filter (<see cref="SearchQuery"/> syntax) means for one text outside SQL.</summary>
    public static bool TextMatches(string filter, string value) => SearchQuery.Parse(filter).Matches([value]);

    /// <summary>The REGEXP function every connection gets (<see cref="CaseSession.OpenReadOnly"/>). A pattern that runs
    /// past the timeout counts as no match rather than freezing a search.</summary>
    internal static bool RegexMatch(string pattern, string? input)
    {
        if (input is null) return false;
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>The text filter (<see cref="SearchQuery"/> syntax) as a WHERE condition over the columns not skipped.</summary>
    static string FilterSql(SqliteCommand cmd, IReadOnlyList<ColumnInfo> cols, string filter, IReadOnlyCollection<string>? skip) =>
        SearchQuery.Parse(filter).Sql(cmd, skip is { Count: > 0 } ? cols.Where(c => !skip.Contains(c.Sql)).ToList() : cols);

    /// <summary>For each term of the filter (<see cref="SearchQuery.Terms"/>), how many of the matching rows hold it.</summary>
    public static long[] TermCounts(SqliteConnection con, ArtifactInfo a, string filter, IReadOnlyCollection<string>? skip = null)
    {
        var terms = SearchQuery.Parse(filter).Terms;
        var cols = ExistingColumns(con, a);
        if (cols.Count == 0 || terms.Count == 0) return new long[terms.Count];
        var searched = skip is { Count: > 0 } ? cols.Where(c => !skip.Contains(c.Sql)).ToList() : cols;
        using var cmd = con.CreateCommand();
        var sums = string.Join(", ", terms.Select(t => $"IFNULL(SUM({SearchQuery.AtomSql(cmd, searched, t)}), 0)"));
        cmd.CommandText = $"SELECT {sums} FROM {Q(a.TableName)} WHERE {FilterSql(cmd, cols, filter, skip)}";
        using var r = cmd.ExecuteReader();
        r.Read();
        return terms.Select((_, i) => r.GetInt64(i)).ToArray();
    }

    /// <summary>Row ids of the rows the filter matches (skipping those columns in the text search).</summary>
    public static HashSet<long> MatchingRowIds(SqliteConnection con, ArtifactInfo a, string filter, IReadOnlyCollection<string>? skip = null)
    {
        var ids = new HashSet<long>();
        var cols = ExistingColumns(con, a);
        if (cols.Count == 0) return ids;
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT rowid FROM {Q(a.TableName)} WHERE {FilterSql(cmd, cols, filter, skip)}";
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>Streams every row (optionally filtered) in rowid order, for export and search. Pages continue after
    /// the last rowid seen, so a 20M-row table is read once instead of re-counted and re-skipped per page.</summary>
    public static IEnumerable<RecordRow> All(SqliteConnection con, ArtifactInfo a, string? filter = null,
        IReadOnlyCollection<long>? rowIds = null, IReadOnlyList<ColumnFilter>? columnFilters = null, IReadOnlyCollection<string>? skip = null)
    {
        const int page = 2000;
        var cols = ExistingColumns(con, a);
        if (cols.Count == 0) yield break;
        long last = long.MinValue;
        while (true)
        {
            using var cmd = con.CreateCommand();
            var where = Where(cmd, cols, new PageRequest(Filter: filter, RowIds: rowIds, ColumnFilters: columnFilters, SkipColumns: skip));
            cmd.CommandText = $"SELECT rowid, {Select(cols)} FROM {Q(a.TableName)}" +
                              (where == "" ? " WHERE" : where + " AND") + " rowid > @last ORDER BY rowid LIMIT @lim";
            cmd.Parameters.AddWithValue("@last", last);
            cmd.Parameters.AddWithValue("@lim", page);
            var rows = Read(cmd, cols.Count);
            foreach (var row in rows) yield return row;
            if (rows.Count < page) yield break;
            last = rows[^1].RowId;
        }
    }

    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

/// <summary>
/// One artifact, filtered and ordered once, read by position: the grid asks for any window of rows as it scrolls.
/// Positions sit in a TEMP table on the cursor's own connection (the evidence database stays read-only); an unfiltered,
/// unsorted table whose rowids have no gaps needs none.
/// </summary>
public sealed class RecordCursor : IDisposable
{
    readonly SqliteConnection _con;
    readonly string _table;
    readonly string? _view;
    readonly long _first;
    readonly Lock _lock = new();
    bool _ownsConnection;

    public IReadOnlyList<ColumnInfo> Columns { get; }

    /// <summary>A cursor on its own read-only connection, closed with it: for a grid that reads while it scrolls.</summary>
    public static RecordCursor Open(CaseSession s, ArtifactInfo a, PageRequest req)
    {
        var con = s.OpenDb();
        try { return new RecordCursor(con, a, req) { _ownsConnection = true }; }
        catch { con.Dispose(); throw; }
    }
    public long Count { get; }

    public RecordCursor(SqliteConnection con, ArtifactInfo a, PageRequest req)
    {
        _con = con;
        _table = ArtifactQuery.Q(a.TableName);
        Columns = ArtifactQuery.ExistingColumns(con, a);
        if (Columns.Count == 0) return;
        using var cmd = con.CreateCommand();
        var where = ArtifactQuery.Where(cmd, Columns, req);
        if (where == "" && req.SortColumn is null)
        {
            cmd.CommandText = $"SELECT min(rowid), max(rowid), count(*) FROM {_table}";
            using var r = cmd.ExecuteReader();
            r.Read();
            Count = r.GetInt64(2);
            if (Count == 0) return;
            _first = r.GetInt64(0);
            if (r.GetInt64(1) - _first + 1 == Count) return; // position = rowid - first
        }
        _view = "temp.v" + Guid.NewGuid().ToString("N");
        cmd.CommandText = $"CREATE TABLE {_view} AS SELECT rowid AS rid FROM {_table}{where} ORDER BY {ArtifactQuery.Order(Columns, req)}";
        cmd.ExecuteNonQuery();
        cmd.Parameters.Clear();
        cmd.CommandText = $"SELECT count(*) FROM {_view}";
        Count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Rows at positions [start, start + count).</summary>
    public IReadOnlyList<RecordRow> Fetch(long start, int count)
    {
        if (Columns.Count == 0 || count <= 0) return [];
        lock (_lock)
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = _view is null
                ? $"SELECT rowid, {ArtifactQuery.Select(Columns)} FROM {_table} WHERE rowid BETWEEN @a AND @b ORDER BY rowid"
                : $"SELECT t.rowid, {ArtifactQuery.Select(Columns, "t.")} FROM {_view} v JOIN {_table} t ON t.rowid = v.rid " +
                  "WHERE v.rowid BETWEEN @a AND @b ORDER BY v.rowid";
            var a = _view is null ? _first + start : start + 1;
            cmd.Parameters.AddWithValue("@a", a);
            cmd.Parameters.AddWithValue("@b", a + count - 1);
            return ArtifactQuery.Read(cmd, Columns.Count);
        }
    }

    /// <summary>Position of a record in this view, or -1.</summary>
    public long PositionOf(long rowId)
    {
        lock (_lock)
        {
            using var cmd = _con.CreateCommand();
            if (_view is null) return rowId >= _first && rowId < _first + Count ? rowId - _first : -1;
            cmd.CommandText = $"SELECT rowid FROM {_view} WHERE rid = @id LIMIT 1";
            cmd.Parameters.AddWithValue("@id", rowId);
            return cmd.ExecuteScalar() is long p ? p - 1 : -1;
        }
    }

    /// <summary>Every rowid in the view, in order (for "check all").</summary>
    public List<long> RowIds()
    {
        lock (_lock)
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = _view is null ? $"SELECT rowid FROM {_table} ORDER BY rowid" : $"SELECT rid FROM {_view} ORDER BY rowid";
            var ids = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            // closing its own connection drops the temp table anyway
            if (_view is not null && !_ownsConnection && _con.State == System.Data.ConnectionState.Open)
            {
                using var cmd = _con.CreateCommand();
                cmd.CommandText = $"DROP TABLE IF EXISTS {_view}";
                cmd.ExecuteNonQuery();
            }
            if (_ownsConnection) _con.Dispose();
        }
    }
}
