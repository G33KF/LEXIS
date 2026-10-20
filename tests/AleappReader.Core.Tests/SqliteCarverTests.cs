using Microsoft.Data.Sqlite;

namespace AleappReader.Core.Tests;

/// <summary>The cases of scripts/sqlite_carver.py's self-test, plus what the port adds (journal, read-only SQL).</summary>
public class SqliteCarverTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("lexis-carver").FullName;
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_dir, true); }

    static SqliteConnection Db(string path)
    {
        var con = new SqliteConnection($"Data Source={path};Pooling=False");
        con.Open();
        return con;
    }

    static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Deleted_rows_are_recovered_from_free_space()
    {
        var path = Path.Combine(_dir, "t.db");
        using (var db = Db(path))
        {
            Exec(db, "PRAGMA secure_delete = 0");
            Exec(db, "CREATE TABLE m (id INTEGER PRIMARY KEY, t INTEGER, body TEXT)");
            for (var i = 1; i <= 40; i++) Exec(db, $"INSERT INTO m VALUES ({i}, {1700000000 + i}, '{string.Concat(Enumerable.Repeat($"message number {i} ", 3))}')");
            Exec(db, "DELETE FROM m WHERE id % 10 = 0");
        }
        using var f = SqliteFile.Open(path);
        var got = f.Carve().Records;
        Assert.Equal([10L, 20, 30, 40], got.Select(r => (long)r.Values[1]! - 1700000000).Order());
        Assert.All(got, r => { Assert.Equal(SqliteCarver.Free, r.Kind); Assert.Equal("m", r.Table); Assert.Equal("main file", r.Origin); });
    }

    [Fact]
    public void Older_versions_survive_in_the_write_ahead_log_and_the_main_file_alone_reads_without_it()
    {
        var path = Path.Combine(_dir, "w.db");
        var db = Db(path);
        Exec(db, "PRAGMA journal_mode = WAL");
        Exec(db, "PRAGMA wal_autocheckpoint = 0");
        Exec(db, "CREATE TABLE m (id INTEGER PRIMARY KEY, t INTEGER, body TEXT)");
        for (var i = 200; i < 210; i++) Exec(db, $"INSERT INTO m VALUES ({i}, {i}, 'first {i}')");
        Exec(db, "UPDATE m SET body = 'second' WHERE id = 201");
        Exec(db, "DELETE FROM m WHERE id = 205");
        using var f = SqliteFile.Open(path); // copied while the writer is open: the log is still on disk
        db.Dispose();

        var older = f.Carve().Records.Where(r => r.Kind == SqliteCarver.Older).Select(r => (r.RowId, (string)r.Values[2]!)).ToHashSet();
        Assert.Equal([(201L, "first 201"), (205L, "first 205")], older.OrderBy(o => o.RowId));
        Assert.True(f.HasWal);
        Assert.Equal(9, f.Objects(true).Single(o => o.Name == "m").Rows);
        Assert.DoesNotContain(f.Objects(false), o => o.Name == "m"); // the table was created after WAL mode: only in the log
        Assert.Contains(f.WalDifference(), d => d.Table == "m" && d.WithWal == 9 && d.MainOnly is null);
    }

    [Fact]
    public void A_persisted_rollback_journal_keeps_the_rows_a_secure_delete_zeroed()
    {
        var path = Path.Combine(_dir, "j.db");
        using (var db = Db(path))
        {
            Exec(db, "PRAGMA secure_delete = 1");
            Exec(db, "PRAGMA journal_mode = PERSIST");
            Exec(db, "CREATE TABLE m (id INTEGER PRIMARY KEY, body TEXT)");
            for (var i = 1; i <= 20; i++) Exec(db, $"INSERT INTO m VALUES ({i}, 'row {i}')");
            Exec(db, "DELETE FROM m WHERE id % 5 = 0");
        }
        Assert.True(File.Exists(path + "-journal"));
        using var f = SqliteFile.Open(path);
        Assert.NotNull(f.Journal);
        var result = f.Carve();
        Assert.Equal([5L, 10, 15, 20], result.Records.Select(r => r.RowId!.Value).Order());
        Assert.All(result.Records, r => { Assert.Equal(SqliteCarver.Journal, r.Kind); Assert.Equal("high", r.Confidence); });
        Assert.Contains(result.Notes, n => n.Contains("rollback journal"));
        Assert.Contains(result.Notes, n => n.Contains("secure_delete"));
    }

    [Fact]
    public void Typed_sql_runs_read_only_and_one_statement_at_a_time()
    {
        var path = Path.Combine(_dir, "q.db");
        using (var db = Db(path)) { Exec(db, "CREATE TABLE m (id INTEGER PRIMARY KEY, body TEXT)"); Exec(db, "INSERT INTO m VALUES (1, 'a'), (2, 'b')"); }
        var before = Sha(path);
        using var f = SqliteFile.Open(path);
        var rows = f.Run("SELECT body FROM m ORDER BY id;", true);
        Assert.Equal(["body"], rows.Columns);
        Assert.Equal(["a", "b"], rows.Rows.Select(r => (string)r[0]!));
        Assert.True(f.Run("SELECT id FROM m", true, limit: 1).Cut);
        Assert.Throws<InvalidOperationException>(() => f.Run("DELETE FROM m", true));
        Assert.Throws<InvalidOperationException>(() => f.Run("SELECT 1; DROP TABLE m", true));
        Assert.ThrowsAny<SqliteException>(() => f.Run("WITH x AS (SELECT 1) INSERT INTO m SELECT 3, 'c' FROM x", true));
        Assert.Equal(2, f.Objects(true).Single(o => o.Name == "m").Rows);
        Assert.Equal(before, Sha(path));
        Assert.False(File.Exists(path + "-wal") || File.Exists(path + "-shm")); // nothing created beside the original
    }

    static string Sha(string p) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)));
}
