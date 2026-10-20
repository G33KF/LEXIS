using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>A table, view, index or trigger of a SQLite file: rows counted for tables and views.</summary>
public sealed record SqliteObject(string Type, string Name, string? Table, string? Sql, long? Rows, bool HasRowId);

/// <summary>Rows of a read-only query: column names and values, cut at the limit asked for.</summary>
public sealed record SqlRows(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Cut);

/// <summary>
/// A SQLite database from the evidence, read without ever opening the evidence file: the database, its -wal and -shm
/// are copied to a temporary folder and only the copy is opened, read-only and with query_only. Two reads of the copy:
/// with the write-ahead log applied (what the app saw last) or without it (immutable=1: the main file alone). A
/// rollback -journal is kept apart from the copy, where SQLite will not roll it back, and only the carver reads it.
/// </summary>
public sealed class SqliteFile : IDisposable
{
    public string Name { get; }
    public string Copy { get; }
    public bool HasWal => File.Exists(Copy + "-wal") && new FileInfo(Copy + "-wal").Length > 0;
    public string? Journal { get; }
    readonly string _dir;

    SqliteFile(string name, string dir, string copy, string? journal) => (Name, _dir, Copy, Journal) = (name, dir, copy, journal);

    public static bool IsSqlite(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            return f.ReadAtLeast(head, 16, false) == 16 && head.SequenceEqual("SQLite format 3\0"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Copies <paramref name="path"/> and its sidecars (read for sharing, never for writing) to a new
    /// temporary folder. The sidecars are taken from beside the file unless given.</summary>
    public static SqliteFile Open(string path, string? name = null, string? wal = null, string? shm = null, string? journal = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LEXIS-sqlite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "db");
        CopyIn(path, copy);
        if ((wal ?? path + "-wal") is var w && File.Exists(w)) CopyIn(w, copy + "-wal");
        if ((shm ?? path + "-shm") is var s && File.Exists(s)) CopyIn(s, copy + "-shm");
        string? j = null;
        if ((journal ?? path + "-journal") is var jr && File.Exists(jr)) CopyIn(jr, j = Path.Combine(dir, "rollback-journal"));
        return new SqliteFile(name ?? Path.GetFileName(path), dir, copy, j);
    }

    static void CopyIn(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = File.Create(to);
        src.CopyTo(dst);
    }

    /// <summary>A read-only connection to the copy: the write-ahead log applied, or the main file only.</summary>
    public SqliteConnection Connect(bool applyWal)
    {
        var source = applyWal ? Copy : new Uri(Copy).AbsoluteUri + "?immutable=1";
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA query_only = 1";
        cmd.ExecuteNonQuery();
        return con;
    }

    public IReadOnlyList<SqliteObject> Objects(bool applyWal)
    {
        using var con = Connect(applyWal);
        var list = new List<SqliteObject>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var sql = r.IsDBNull(3) ? null : r.GetString(3);
                var type = r.GetString(0);
                list.Add(new SqliteObject(type, r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), sql, null,
                    type == "table" && sql?.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) != true));
            }
        }
        return list.Select(o => o.Type is "table" or "view" ? o with { Rows = Count(con, o.Name) } : o).ToList();
    }

    static long? Count(SqliteConnection con, string name)
    {
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {ArtifactQuery.Q(name)}";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { return null; } // a virtual table whose module is missing, or a broken view
    }

    /// <summary>Table → (rows with the log applied, rows of the main file alone), for the tables the log changes.</summary>
    public IReadOnlyList<(string Table, long? WithWal, long? MainOnly)> WalDifference()
    {
        if (!HasWal) return [];
        var main = Objects(false).Where(o => o.Type == "table").ToDictionary(o => o.Name, o => o.Rows);
        return Objects(true).Where(o => o.Type == "table" && (!main.TryGetValue(o.Name, out var m) || m != o.Rows))
            .Select(o => (o.Name, o.Rows, main.GetValueOrDefault(o.Name))).ToList();
    }

    /// <summary>The table as an artifact, so the artifact grid's paging, filter and sort read it.</summary>
    public ArtifactInfo AsArtifact(SqliteConnection con, string table)
    {
        var cols = new List<ColumnInfo>();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({ArtifactQuery.Q(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(new ColumnInfo(r.GetString(1), r.GetString(1), null));
        return new ArtifactInfo { Category = Name, Name = table, TableName = table, Columns = cols };
    }

    /// <summary>A statement the examiner typed: one SELECT, WITH or PRAGMA, run on a read-only, query_only connection
    /// (so a PRAGMA that writes fails there too).</summary>
    public SqlRows Run(string sql, bool applyWal, int limit = 10_000)
    {
        var text = sql.Trim().TrimEnd(';').Trim();
        var first = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
        if (first is not ("SELECT" or "WITH" or "PRAGMA" or "EXPLAIN") || text.Contains(';'))
            throw new InvalidOperationException("Only one SELECT, WITH, PRAGMA or EXPLAIN statement is run here.");
        using var con = Connect(applyWal);
        using var cmd = con.CreateCommand();
        cmd.CommandText = text;
        using var r = cmd.ExecuteReader();
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
        var rows = new List<object?[]>();
        while (r.Read())
        {
            if (rows.Count == limit) return new(cols, rows, true);
            var v = new object?[r.FieldCount];
            for (var k = 0; k < v.Length; k++) v[k] = r.IsDBNull(k) ? null : r.GetValue(k);
            rows.Add(v);
        }
        return new(cols, rows, false);
    }

    public CarveResult Carve(Func<string, bool>? wanted = null) => SqliteCarver.Carve(Copy, wanted, Journal);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
