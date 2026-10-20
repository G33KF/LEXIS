using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>A record a query no longer returns, read from the database's bytes: Origin names the copy it came from (main
/// file, WAL frame n, rollback journal record n), Page and Offset where in it. RowId is null when it did not survive
/// (freeing a cell usually overwrites it).</summary>
public sealed record CarvedRecord(string Table, long? RowId, IReadOnlyList<string> Columns, IReadOnlyList<object?> Values,
    string Origin, int Page, int Offset, string Kind, bool Truncated)
{
    /// <summary>How sure the record is what the table held: a cell of an older page version or of the journal is SQLite's
    /// own record of that table; a cell of a page no table owns and free space with its rowid are recognised by a schema;
    /// free space without a rowid by the schema alone; a truncated one misses its overflow.</summary>
    public string Confidence => Truncated ? "low (truncated)"
        : Kind is SqliteCarver.Older or SqliteCarver.Journal ? "high"
        : Kind == SqliteCarver.Unowned || RowId is not null ? "medium"
        : "low";
}

public sealed record CarveResult(IReadOnlyList<CarvedRecord> Records, IReadOnlyList<string> Notes);

/// <summary>
/// Recovers records of a SQLite database's tables that a query no longer returns. A C# port of the LEAPPs'
/// scripts/sqlite_carver.py (MIT, ours), which reads, without opening the database for writing:
/// every older version of a table leaf page (the main file's copy of a page the WAL has since replaced, and every WAL
/// frame, before the last checkpoint or after the last commit), and the free space of every version (the gap between
/// the cell pointers and the cell content, and the freeblock chain), recognising records by the table's schema.
/// Beyond the Python: the rollback journal's pre-transaction pages, cells of table leaf pages no table owns any more
/// (freelist), and notes on what makes "nothing recovered" no proof of absence (auto_vacuum, zeroed free space).
/// A record equal to a live row or to another recovered one is reported once at most, live copies not at all.
/// </summary>
public static class SqliteCarver
{
    public const string Older = "older page version", Free = "free space", Journal = "rollback journal", Unowned = "page of no table (freelist)";

    static readonly int[] Sizes = [0, 1, 2, 3, 4, 6, 8, 8, 0, 0];
    static readonly Dictionary<int, long> IntSerial = new() { [1] = 1, [2] = 2, [3] = 3, [4] = 4, [6] = 5, [8] = 6 };
    static readonly UTF8Encoding Strict = new(false, true), Lenient = new(false, false);

    enum Aff { Alias, Int, Num, Text, Any }

    sealed record Table(string Name, string[] Names, Aff[] Affinities, int? Alias, int Root);

    /// <summary>Carves <paramref name="db"/> (a copy: never the evidence), with <c>-wal</c> and <c>-journal</c> beside
    /// it when they exist. <paramref name="wanted"/> limits the tables read; <paramref name="journalPath"/> is the
    /// rollback journal when it is kept away from the database (beside it, SQLite would roll it back on opening).</summary>
    public static CarveResult Carve(string db, Func<string, bool>? wanted = null, string? journalPath = null)
    {
        // Bytes first, before SQLite opens anything.
        var main = File.ReadAllBytes(db);
        var wal = File.Exists(db + "-wal") ? File.ReadAllBytes(db + "-wal") : null;
        journalPath ??= db + "-journal";
        var journal = File.Exists(journalPath) ? File.ReadAllBytes(journalPath) : null;
        var notes = new List<string>();
        if (main.Length < 100 || !main.AsSpan().StartsWith("SQLite format 3\0"u8)) return new([], ["Not a SQLite database"]);

        var tables = Schema(db).Where(t => wanted is null || wanted(t.Name)).ToDictionary(t => t.Name);
        if (tables.Count == 0) return new([], notes);
        var live = Live(db, tables.Values);
        var size = BinaryPrimitives.ReadUInt16BigEndian(main.AsSpan(16)) is var ps && ps == 1 ? 65536 : (int)ps;
        var (image, versions) = Versions(main, wal, journal, size, notes);
        var owners = Owners(image, tables.Values, size);

        if (BinaryPrimitives.ReadUInt32BigEndian(main.AsSpan(52)) != 0)
            notes.Add("auto_vacuum is on: freed pages are moved away and the file truncated, so fewer deleted records survive.");
        var freeBytes = 0L;
        var nonZero = 0L;

        var found = new List<CarvedRecord>();
        var seen = new HashSet<string>();
        string Loose(Table t, IReadOnlyList<object?> v) => string.Join("\u001f", v.Where((_, k) => k != t.Alias).Select(Key));

        void Keep(Table t, long? rowid, List<object?> values, string origin, int number, int offset, string kind, bool truncated)
        {
            if (t.Alias is { } a && rowid is not null) values[a] = rowid;
            var key = Loose(t, values);
            if (live[t.Name].Contains(key) || !seen.Add(t.Name + "\u001e" + key)) return;
            found.Add(new CarvedRecord(t.Name, rowid, t.Names, values, origin, number, offset, kind, truncated));
        }

        foreach (var (origin, number, page, fromJournal) in versions)
        {
            var hdr = number == 1 ? 100 : 0;
            if (page.Length <= hdr + 8 || page[hdr] != 0x0D) continue;
            var cellCount = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 3));
            var current = image.TryGetValue(number, out var now) && now.AsSpan().SequenceEqual(page);
            if (!owners.TryGetValue(number, out var name))
            {
                // A leaf no table owns now: its cells are tried against every table's schema.
                foreach (var (rowid, payload, truncated, ptr) in Cells(page, hdr, cellCount, size, image))
                    foreach (var t in tables.Values)
                        if (Record(payload, 0, payload.Length, t, false) is { } p)
                        {
                            Keep(t, rowid, p.Values, origin, number, ptr, fromJournal ? Journal : Unowned, truncated);
                            break;
                        }
                continue;
            }
            var table = tables[name];
            if (!current)
                foreach (var (rowid, payload, truncated, ptr) in Cells(page, hdr, cellCount, size, image))
                    if (Record(payload, 0, payload.Length, table, false) is { } p)
                        Keep(table, rowid, p.Values, origin, number, ptr, fromJournal ? Journal : Older, truncated);
            var ranges = FreeRanges(page, hdr, size);
            foreach (var (lo, hi) in ranges)
            {
                // Freeblocks only: the gap keeps stale cell pointers even under secure_delete.
                if (current && origin == "main file" && lo != ranges[0].Lo)
                {
                    freeBytes += Math.Max(0, hi - lo);
                    for (var k = lo; k < hi; k++) if (page[k] != 0) nonZero++;
                }
                var offset = lo;
                while (offset < hi - 1)
                {
                    var p = Record(page, offset, hi, table, true);
                    if (p is null && offset == lo && lo != hdr + 8 + 2 * cellCount)
                        // A freed cell: its first four bytes now hold the freeblock link and size.
                        p = Record(page, offset, hi, table, true, headless: true) ?? Record(page, offset, hi, table, true, headless: true, lost: 1);
                    if (p is null) { offset++; continue; }
                    var rowid = RowidBefore(page, offset, p.End - offset, lo - 4);
                    Keep(table, rowid, p.Values, origin, number, offset, Free, false);
                    offset = p.End;
                }
            }
        }
        if (freeBytes > 0 && nonZero == 0)
            notes.Add("The free space of every table page is zeros (secure_delete or VACUUM): no record recovered from it is not proof there was none.");
        return new(found, notes);
    }

    // ---- reading the database the way SQLite stores it ----

    static (long Value, int Next) Varint(byte[] b, int i)
    {
        long v = 0;
        for (var k = 0; k < 8; k++)
        {
            var x = b[i + k];
            v = (v << 7) | (long)(x & 0x7F);
            if (x < 0x80) return (v, i + k + 1);
        }
        return ((v << 8) | b[i + 8], i + 9);
    }

    static int? Size(long serial) => serial < 0 ? null : serial < 10 ? Sizes[serial] : serial < 12 ? null
        : (int)(serial % 2 == 0 ? (serial - 12) / 2 : (serial - 13) / 2);

    static object? Value(long serial, ReadOnlySpan<byte> raw, bool strict) => serial switch
    {
        0 => null,
        7 => BinaryPrimitives.ReadDoubleBigEndian(raw),
        8 or 9 => serial - 8,
        < 7 => Int(raw),
        _ when serial % 2 == 0 => raw.ToArray(),
        _ => (strict ? Strict : Lenient).GetString(raw),
    };

    static long Int(ReadOnlySpan<byte> raw)
    {
        long v = (sbyte)raw[0];
        for (var k = 1; k < raw.Length; k++) v = (v << 8) | raw[k];
        return v;
    }

    static Aff Affinity(string? declared)
    {
        var d = (declared ?? "").ToUpperInvariant();
        if (d.Contains("INT")) return Aff.Int;
        if (d.Contains("CHAR") || d.Contains("CLOB") || d.Contains("TEXT")) return Aff.Text;
        if (d == "" || d.Contains("BLOB")) return Aff.Any;
        return Aff.Num;
    }

    static bool Admits(Aff a, long serial) => a switch
    {
        Aff.Alias => serial == 0,
        Aff.Int or Aff.Num => serial < 10,
        Aff.Text => serial == 0 || serial >= 12,
        _ => true,
    };

    static long? LostSerial(Aff a, int size) => a switch
    {
        Aff.Alias => size == 0 ? 0 : null,
        Aff.Int or Aff.Num => IntSerial.TryGetValue(size, out var s) ? s : null,
        Aff.Text => 2L * size + 13,
        _ => null,
    };

    sealed record Parsed(List<object?> Values, int End);

    /// <summary>A record whose header starts at buf[start], ending at or before end. headless: the header-length varint was
    /// overwritten; lost: so were that many leading serial types (the one lost column's size is what is left before end).</summary>
    static Parsed? Record(byte[] buf, int start, int end, Table t, bool strict, bool headless = false, int lost = 0)
    {
        try
        {
            var count = t.Names.Length - lost;
            int i, limit;
            if (headless) (i, limit) = (start, end);
            else
            {
                (var length, i) = Varint(buf, start);
                if (length < 2 || start + length > end) return null;
                limit = start + (int)length;
            }
            var serials = new List<long>();
            while (i < limit && serials.Count < count)
            {
                (var s, i) = Varint(buf, i);
                serials.Add(s);
            }
            if (serials.Count != count || (!headless && i != limit)) return null;
            var sizes = serials.Select(Size).ToList();
            if (sizes.Any(s => s is null)) return null;
            if (lost > 0)
            {
                if (LostSerial(t.Affinities[0], end - i - sizes.Sum(s => s!.Value)) is not { } s || Size(s) is not { } n) return null;
                serials.Insert(0, s);
                sizes.Insert(0, n);
            }
            for (var k = 0; k < serials.Count; k++) if (!Admits(t.Affinities[k], serials[k])) return null;
            var total = sizes.Sum(s => s!.Value);
            if (total == 0 || i + total > end) return null; // zeroed free space parses as a record of NULLs
            var values = new List<object?>(serials.Count);
            var j = i;
            for (var k = 0; k < serials.Count; k++)
            {
                var n = sizes[k]!.Value;
                values.Add(Value(serials[k], buf.AsSpan(j, n), strict));
                j += n;
            }
            return new(values, j);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or DecoderFallbackException) { return null; }
    }

    /// <summary>Every cell of a table leaf page: rowid, the whole payload (overflow followed in the current image), and
    /// whether the payload is cut short.</summary>
    static IEnumerable<(long RowId, byte[] Payload, bool Truncated, int Ptr)> Cells(byte[] page, int hdr, int cellCount, int size, Dictionary<int, byte[]> image)
    {
        for (var k = 0; k < cellCount; k++)
        {
            (long, byte[], bool, int)? cell = null;
            try
            {
                var ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 8 + 2 * k));
                var (rowid, payload, truncated) = Payload(page, ptr, size, image);
                cell = (rowid, payload, truncated, ptr);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            if (cell is { } c) yield return c;
        }
    }

    static (long RowId, byte[] Data, bool Truncated) Payload(byte[] page, int ptr, int usable, Dictionary<int, byte[]> image)
    {
        var (total, i) = Varint(page, ptr);
        (var rowid, i) = Varint(page, i);
        var most = usable - 35;
        if (total <= most) return (rowid, Slice(page, i, (int)total), false);
        var least = (usable - 12) * 32 / 255 - 23;
        var local = (int)(least + (total - least) % (usable - 4));
        if (local > most) local = least;
        var data = new List<byte>(Slice(page, i, local));
        var next = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(i + local));
        var seen = new HashSet<long>();
        while (next != 0 && data.Count < total && seen.Add(next) && image.TryGetValue((int)next, out var over))
        {
            data.AddRange(Slice(over, 4, (int)Math.Min(usable - 4, total - data.Count)));
            next = BinaryPrimitives.ReadUInt32BigEndian(over);
        }
        return (rowid, data.ToArray(), data.Count < total);
    }

    /// <summary>buf[start:start+n] the way Python slices: cut at the end of the buffer.</summary>
    static byte[] Slice(byte[] buf, int start, int n) => start >= buf.Length || n <= 0 ? [] : buf.AsSpan(start, Math.Min(n, buf.Length - start)).ToArray();

    static List<(int Lo, int Hi)> FreeRanges(byte[] page, int hdr, int size)
    {
        var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 3));
        var content = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 5)) is var c && c == 0 ? 65536 : c;
        var ranges = new List<(int, int)> { (hdr + 8 + 2 * cells, Math.Min(content, size)) };
        var block = (int)BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 1));
        var seen = new HashSet<int>();
        while (block != 0 && seen.Add(block) && block + 4 <= size)
        {
            var next = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(block));
            var length = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(block + 2));
            ranges.Add((block + 4, Math.Min(block + length, size)));
            block = next;
        }
        return ranges;
    }

    /// <summary>The rowid of a carved record when its payload-length and rowid varints survive just before it.</summary>
    static long? RowidBefore(byte[] page, int start, int total, int lo)
    {
        for (var q = Math.Max(lo, start - 18); q < start - 1; q++)
        {
            try
            {
                var (length, i) = Varint(page, q);
                (var rowid, i) = Varint(page, i);
                if (i == start && length == total) return rowid;
            }
            catch (IndexOutOfRangeException) { }
        }
        return null;
    }

    /// <summary>The current image (main file, then the WAL's committed frames of the current salt) and every page
    /// version: the main file's pages, every WAL frame, and the rollback journal's pre-transaction pages.</summary>
    static (Dictionary<int, byte[]> Image, List<(string Origin, int Page, byte[] Bytes, bool Journal)>) Versions(byte[] main, byte[]? wal, byte[]? journal,
        int size, List<string> notes)
    {
        var image = new Dictionary<int, byte[]>();
        for (var n = 0; n < main.Length / size; n++) image[n + 1] = main.AsSpan(n * size, size).ToArray();
        var versions = image.Select(kv => ("main file", kv.Key, kv.Value, false)).ToList();
        if (wal is { Length: >= 32 } && BinaryPrimitives.ReadUInt32BigEndian(wal) is 0x377F0682 or 0x377F0683
            && BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8)) == size)
        {
            var salts = wal.AsSpan(16, 8).ToArray();
            var pending = new Dictionary<int, byte[]>();
            var index = 0;
            for (var off = 32; off <= wal.Length - 24 - size; off += 24 + size)
            {
                index++;
                var number = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(off));
                var commit = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(off + 4));
                var page = wal.AsSpan(off + 24, size).ToArray();
                var current = wal.AsSpan(off + 8, 8).SequenceEqual(salts);
                versions.Add(($"WAL frame {index}" + (current ? "" : " (before last checkpoint)"), number, page, false));
                if (!current) continue;
                pending[number] = page;
                if (commit != 0)
                {
                    foreach (var (k, v) in pending) image[k] = v;
                    pending.Clear();
                }
            }
        }
        else if (wal is { Length: > 0 }) notes.Add("The -wal file is not a write-ahead log for this page size: not read.");
        if (journal is not null)
        {
            var read = JournalPages(journal, size).ToList();
            versions.AddRange(read.Select(r => ($"rollback journal record {r.Index}", r.Page, r.Bytes, true)));
            notes.Add(read.Count > 0 ? $"A rollback journal holds {read.Count:N0} pre-transaction pages; the live view shows the database without rolling it back."
                                     : "The -journal file has no readable page records (zeroed header or empty).");
        }
        return (image, versions);
    }

    /// <summary>Rollback journal page records: a header sector (8-byte magic, or zeros before the journal was synced or
    /// after a PERSIST commit), then page number + page + checksum. Pages still hold what the transaction changed.</summary>
    static IEnumerable<(int Index, int Page, byte[] Bytes)> JournalPages(byte[] j, int size)
    {
        if (j.Length < 28) yield break;
        var magic = j.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7 });
        if (!magic && j.AsSpan(0, 8).IndexOfAnyExcept((byte)0) >= 0) yield break;
        var sector = (int)BinaryPrimitives.ReadUInt32BigEndian(j.AsSpan(20));
        var pageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(j.AsSpan(24));
        if (pageSize != 0 && pageSize != size) yield break;
        if (sector is < 512 or > 65536 || (sector & (sector - 1)) != 0)
        {
            // Header zeroed: the first record starts at the first sector boundary holding a page number.
            sector = 512;
            while (sector < 65536 && sector + 4 <= j.Length && BinaryPrimitives.ReadUInt32BigEndian(j.AsSpan(sector)) == 0) sector *= 2;
        }
        var index = 0;
        for (var off = sector; off + 4 + size + 4 <= j.Length; off += 4 + size + 4)
        {
            var number = (int)BinaryPrimitives.ReadUInt32BigEndian(j.AsSpan(off));
            if (number == 0) yield break;
            yield return (++index, number, j.AsSpan(off + 4, size).ToArray());
        }
    }

    /// <summary>Page number → table name, from the b-trees of the current image.</summary>
    static Dictionary<int, string> Owners(Dictionary<int, byte[]> image, IEnumerable<Table> tables, int size)
    {
        var owners = new Dictionary<int, string>();
        foreach (var t in tables)
        {
            var stack = new Stack<int>([t.Root]);
            while (stack.Count > 0)
            {
                var number = stack.Pop();
                if (!image.TryGetValue(number, out var page) || owners.ContainsKey(number)) continue;
                owners[number] = t.Name;
                var hdr = number == 1 ? 100 : 0;
                if (page[hdr] != 0x05) continue;
                var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 3));
                stack.Push((int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(hdr + 8)));
                for (var k = 0; k < cells && hdr + 14 + 2 * k <= size; k++)
                {
                    var ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(hdr + 12 + 2 * k));
                    if (ptr + 4 <= size) stack.Push((int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(ptr)));
                }
            }
        }
        return owners;
    }

    // ---- what SQLite itself returns, read-only ----

    static SqliteConnection OpenRo(string db)
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        con.Open();
        return con;
    }

    static List<Table> Schema(string db)
    {
        using var con = OpenRo(db);
        var list = new List<(string Name, int Root, string Sql)>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name, rootpage, sql FROM sqlite_master WHERE type = 'table'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(1) && r.GetInt64(1) != 0 && !(r.IsDBNull(2) ? "" : r.GetString(2)).Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
                    list.Add((r.GetString(0), (int)r.GetInt64(1), r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        var tables = new List<Table>();
        foreach (var (name, root, _) in list)
        {
            var cols = new List<(string Name, string Type, bool Pk)>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({ArtifactQuery.Q(name)})";
                using var r = cmd.ExecuteReader();
                while (r.Read()) cols.Add((r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt64(5) != 0));
            }
            var pk = cols.Where(c => c.Pk).ToList();
            var alias = pk.Count == 1 && pk[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) ? pk[0].Name : null;
            var ai = alias is null ? (int?)null : cols.FindIndex(c => c.Name == alias);
            tables.Add(new Table(name, cols.Select(c => c.Name).ToArray(),
                cols.Select((c, k) => k == ai ? Aff.Alias : Affinity(c.Type)).ToArray(), ai, root));
        }
        return tables;
    }

    /// <summary>Table → the keys of its live rows, without the rowid alias.</summary>
    static Dictionary<string, HashSet<string>> Live(string db, IEnumerable<Table> tables)
    {
        using var con = OpenRo(db);
        var live = new Dictionary<string, HashSet<string>>();
        foreach (var t in tables)
        {
            var set = live[t.Name] = [];
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", t.Names.Select(ArtifactQuery.Q))} FROM {ArtifactQuery.Q(t.Name)}";
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    set.Add(string.Join("\u001f", Enumerable.Range(0, t.Names.Length).Where(k => k != t.Alias).Select(k => Key(r.IsDBNull(k) ? null : r.GetValue(k)))));
            }
            catch (SqliteException) { } // a virtual or damaged table: nothing to compare against
        }
        return live;
    }

    /// <summary>A value as a comparable key, equal where Python's tuple comparison is: 1 and 1.0 are the same.</summary>
    static string Key(object? v) => v switch
    {
        null => "n",
        long l => "i" + l,
        double d when d == Math.Floor(d) && Math.Abs(d) < 9.2e18 => "i" + (long)d,
        double d => "f" + d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        byte[] b => "b" + Convert.ToHexString(b),
        string s => "s" + s,
        _ => "o" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };
}
