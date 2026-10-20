"""Recovers records of a SQLite table that a query no longer returns.

Two places are read, both without opening the database for writing:

* every older version of a table leaf page: the main file's copy of a page the write-ahead log
  has since replaced, and every frame in the log, including frames from before the last
  checkpoint (stale salts) and after the last commit. Their cells are read as SQLite stores
  them, rowid included.
* the free space of every version of a table leaf page: the gap between the cell pointer array
  and the cell content area, and the freeblock chain. A deleted cell stays there until it is
  overwritten; SQLite overwrites the first four bytes of a cell it frees (the freeblock link
  and size), which usually takes the payload length and the rowid with them, so a record
  carved from free space often has no rowid. Records are recognised by the table's own
  schema: the column count and, per column, the storage classes its declared type admits.

A record that equals a live row, or another recovered record, is reported once at most and
live copies not at all: B-tree balancing moves cells between pages and leaves copies of live
rows behind. Overflow pages of a recovered record are read from the current database image
when the record is complete there; otherwise only the part on the leaf page is returned and
the record is marked truncated.
"""

import os
import sqlite3
import struct

from scripts.ilapfuncs import get_sqlite_db_path

_SIZES = (0, 1, 2, 3, 4, 6, 8, 8, 0, 0)


def _varint(buf, i):
    value = 0
    for k in range(8):
        byte = buf[i + k]
        value = (value << 7) | (byte & 0x7F)
        if byte < 0x80:
            return value, i + k + 1
    return (value << 8) | buf[i + 8], i + 9


def _size(serial):
    if serial < 10:
        return _SIZES[serial]
    if serial < 12:
        return None
    return (serial - 12) // 2 if serial % 2 == 0 else (serial - 13) // 2


def _value(serial, raw, strict):
    if serial == 0:
        return None
    if serial == 7:
        return struct.unpack('>d', raw)[0]
    if serial in (8, 9):
        return serial - 8
    if serial < 7:
        return int.from_bytes(raw, 'big', signed=True)
    if serial % 2 == 0:
        return bytes(raw)
    return bytes(raw).decode('utf-8', 'strict' if strict else 'replace')


def _affinity(declared):
    declared = (declared or '').upper()
    if 'INT' in declared:
        return 'int'
    if any(word in declared for word in ('CHAR', 'CLOB', 'TEXT')):
        return 'text'
    if not declared or 'BLOB' in declared:
        return 'any'
    return 'num'


def _admits(affinity, serial):
    if affinity == 'alias':
        return serial == 0
    if affinity in ('int', 'num'):
        return serial < 10
    if affinity == 'text':
        return serial == 0 or serial >= 12
    return True


class _Table:
    def __init__(self, name, columns):
        self.name = name
        self.names = [c[0] for c in columns]
        self.affinities = ['alias' if c[2] else _affinity(c[1]) for c in columns]
        self.alias = next((i for i, c in enumerate(columns) if c[2]), None)


def _schema(db_path):
    """Table name -> (_Table, root page), read through SQLite in read-only mode."""
    con = sqlite3.connect(f'file:{get_sqlite_db_path(db_path)}?mode=ro', uri=True)
    try:
        tables = {}
        for name, root, sql in con.execute(
                "SELECT name, rootpage, sql FROM sqlite_master WHERE type = 'table'"):
            if not root or 'WITHOUT ROWID' in (sql or '').upper():
                continue
            info = con.execute(f'PRAGMA table_info("{name}")').fetchall()
            pk = [r for r in info if r[5]]
            alias = pk[0][1] if len(pk) == 1 and (pk[0][2] or '').upper() == 'INTEGER' else None
            tables[name] = (_Table(name, [(r[1], r[2], r[1] == alias) for r in info]), root)
        return tables
    finally:
        con.close()


def _live(db_path, tables):
    """Table name -> {rowid: values} as SQLite returns them now."""
    con = sqlite3.connect(f'file:{get_sqlite_db_path(db_path)}?mode=ro', uri=True)
    con.text_factory = lambda b: b.decode('utf-8', 'replace')
    try:
        live = {}
        for name, (table, _) in tables.items():
            cols = ', '.join(f'"{c}"' for c in table.names)
            live[name] = {r[0]: tuple(r[1:]) for r in con.execute(f'SELECT rowid, {cols} FROM "{name}"')}
        return live
    finally:
        con.close()


def _versions(db_path):
    """Page size, the current image {page: bytes}, and every page version as (origin, page, bytes)."""
    with open(db_path, 'rb') as f:
        main = f.read()
    size = struct.unpack('>H', main[16:18])[0]
    size = 65536 if size == 1 else size
    image = {n + 1: main[n * size:(n + 1) * size] for n in range(len(main) // size)}
    versions = [('main file', n, page) for n, page in image.items()]
    wal_path = db_path + '-wal'
    if os.path.exists(wal_path):
        with open(wal_path, 'rb') as f:
            wal = f.read()
        if len(wal) >= 32 and struct.unpack('>I', wal[:4])[0] in (0x377F0682, 0x377F0683) \
                and struct.unpack('>I', wal[8:12])[0] == size:
            salts = wal[16:24]
            pending = {}
            for index, off in enumerate(range(32, len(wal) - 24 - size + 1, 24 + size)):
                number, commit = struct.unpack('>II', wal[off:off + 8])
                page = wal[off + 24:off + 24 + size]
                current = wal[off + 8:off + 16] == salts
                versions.append((f'WAL frame {index + 1}' + ('' if current else ' (before last checkpoint)'),
                                 number, page))
                if current:
                    pending[number] = page
                    if commit:
                        image.update(pending)
                        pending = {}
    return size, image, versions


def _owners(image, tables, size):
    """Page number -> table name, from the b-trees of the current image."""
    owners = {}
    for name, (_, root) in tables.items():
        stack = [root]
        while stack:
            number = stack.pop()
            page = image.get(number)
            if page is None or number in owners:
                continue
            owners[number] = name
            hdr = 100 if number == 1 else 0
            if page[hdr] != 0x05:
                continue
            cells = struct.unpack('>H', page[hdr + 3:hdr + 5])[0]
            stack.append(struct.unpack('>I', page[hdr + 8:hdr + 12])[0])
            for k in range(cells):
                ptr = struct.unpack('>H', page[hdr + 12 + 2 * k:hdr + 14 + 2 * k])[0]
                if ptr + 4 <= size:
                    stack.append(struct.unpack('>I', page[ptr:ptr + 4])[0])
    return owners


_INT_SERIAL = {1: 1, 2: 2, 3: 3, 4: 4, 6: 5, 8: 6}


def _lost_serial(affinity, size):
    """The serial type of a column whose header byte was overwritten, from its size; None if unsure."""
    if affinity == 'alias':
        return 0 if size == 0 else None
    if affinity in ('int', 'num'):
        return _INT_SERIAL.get(size)
    if affinity == 'text':
        return 2 * size + 13
    return None


def _record(buf, start, end, table, strict, headless=False, lost=0):
    """Values of a record whose header starts at buf[start], ending at or before end, or None.

    headless: the header-length varint was overwritten and the serial types start at start.
    lost: the serial types of that many leading columns were overwritten too; the size of the
    one lost column is then what is left between the header and end, so end must be the end
    of the freed cell.
    """
    try:
        count = len(table.names) - lost
        if headless:
            i, limit = start, end
        else:
            length, i = _varint(buf, start)
            if length < 2 or start + length > end:
                return None
            limit = start + length
        serials = []
        while i < limit and len(serials) < count:
            serial, i = _varint(buf, i)
            serials.append(serial)
        if len(serials) != count or (not headless and i != limit):
            return None
        sizes = [_size(s) for s in serials]
        if None in sizes:
            return None
        if lost:
            serial = _lost_serial(table.affinities[0], end - i - sum(sizes))
            if serial is None:
                return None
            serials.insert(0, serial)
            sizes.insert(0, _size(serial))
        if not all(_admits(a, s) for a, s in zip(table.affinities, serials)):
            return None
        if not sum(sizes) or i + sum(sizes) > end:
            return None  # zeroed free space parses as a record of NULLs
        values, j = [], i
        for serial, n in zip(serials, sizes):
            values.append(_value(serial, buf[j:j + n], strict))
            j += n
        return values, j
    except (IndexError, UnicodeDecodeError, struct.error):
        return None


def _payload(page, ptr, size, image):
    """Rowid and the whole record payload of a table leaf cell, following overflow pages."""
    total, i = _varint(page, ptr)
    rowid, i = _varint(page, i)
    usable = size
    most = usable - 35
    if total <= most:
        return rowid, page[i:i + total], False
    least = (usable - 12) * 32 // 255 - 23
    local = least + (total - least) % (usable - 4)
    local = local if local <= most else least
    data = bytearray(page[i:i + local])
    following = struct.unpack('>I', page[i + local:i + local + 4])[0]
    seen = set()
    while following and len(data) < total and following not in seen and following in image:
        seen.add(following)
        over = image[following]
        data += over[4:4 + min(usable - 4, total - len(data))]
        following = struct.unpack('>I', over[:4])[0]
    return rowid, bytes(data), len(data) < total


def _free_ranges(page, hdr, size):
    cells = struct.unpack('>H', page[hdr + 3:hdr + 5])[0]
    content = struct.unpack('>H', page[hdr + 5:hdr + 7])[0] or 65536
    ranges = [(hdr + 8 + 2 * cells, min(content, size))]
    block, seen = struct.unpack('>H', page[hdr + 1:hdr + 3])[0], set()
    while block and block not in seen and block + 4 <= size:
        seen.add(block)
        following, length = struct.unpack('>HH', page[block:block + 4])
        ranges.append((block + 4, min(block + length, size)))
        block = following
    return ranges


def _rowid_before(page, start, total, lo):
    """The rowid of a carved record when its payload length and rowid varints survive before it."""
    for q in range(max(lo, start - 18), start - 1):
        try:
            length, i = _varint(page, q)
            rowid, i = _varint(page, i)
        except IndexError:
            continue
        if i == start and length == total:
            return rowid
    return None


def carve(db_path, wanted=None):
    """Recovered records of the tables in a SQLite database.

    wanted: an optional callable taking a table name, to limit the tables read.
    Returns dicts with table (None when the page now belongs to no table), rowid (None when it
    did not survive), values (a dict by column name, the rowid alias filled when known), origin,
    page, offset, kind ('older page version' or 'free space') and truncated.
    """
    tables = {n: t for n, t in _schema(db_path).items() if wanted is None or wanted(n)}
    if not tables:
        return []
    live = _live(db_path, tables)
    size, image, versions = _versions(db_path)
    owners = _owners(image, tables, size)
    loose = lambda table, values: tuple(v for k, v in enumerate(values) if k != table.alias)
    live_loose = {name: {loose(tables[name][0], row) for row in rows.values()} for name, rows in live.items()}
    found, seen = [], set()

    def keep(name, table, rowid, values, origin, number, offset, kind, truncated):
        if table.alias is not None and rowid is not None:
            values[table.alias] = rowid
        key = (name, loose(table, values))
        # A copy of a live row (moved by balancing, or an unchanged older version) is not reported.
        if key[1] in live_loose[name] or key in seen:
            return
        seen.add(key)
        found.append({'table': name, 'rowid': rowid, 'values': dict(zip(table.names, values)),
                      'origin': origin, 'page': number, 'offset': offset, 'kind': kind,
                      'truncated': truncated})

    for origin, number, page in versions:
        name = owners.get(number)
        if name is None:
            continue
        table = tables[name][0]
        hdr = 100 if number == 1 else 0
        if page[hdr] != 0x0D:
            continue
        cell_count = struct.unpack('>H', page[hdr + 3:hdr + 5])[0]
        if image.get(number) != page:
            for k in range(cell_count):
                ptr = struct.unpack('>H', page[hdr + 8 + 2 * k:hdr + 10 + 2 * k])[0]
                try:
                    rowid, payload, truncated = _payload(page, ptr, size, image)
                except (IndexError, struct.error):
                    continue
                parsed = _record(payload, 0, len(payload), table, False)
                if parsed:
                    keep(name, table, rowid, parsed[0], origin, number, ptr, 'older page version', truncated)
        for lo, hi in _free_ranges(page, hdr, size):
            offset = lo
            while offset < hi - 1:
                parsed = _record(page, offset, hi, table, True)
                if not parsed and offset == lo and lo != hdr + 8 + 2 * cell_count:
                    # A freed cell: its first four bytes now hold the freeblock link and size.
                    parsed = (_record(page, offset, hi, table, True, headless=True)
                              or _record(page, offset, hi, table, True, headless=True, lost=1))
                if not parsed:
                    offset += 1
                    continue
                values, end = parsed
                rowid = _rowid_before(page, offset, end - offset, lo - 4)
                keep(name, table, rowid, values, origin, number, offset, 'free space', False)
                offset = end
    return found


if __name__ == '__main__':
    import tempfile
    folder = tempfile.mkdtemp()
    path = os.path.join(folder, 't.db')
    db = sqlite3.connect(path)
    db.execute('PRAGMA secure_delete = 0')
    db.execute('CREATE TABLE m (id INTEGER PRIMARY KEY, t INTEGER, body TEXT)')
    db.executemany('INSERT INTO m VALUES (?, ?, ?)', [(i, 1700000000 + i, f'message number {i} ' * 3) for i in range(1, 41)])
    db.commit()
    db.execute('DELETE FROM m WHERE id % 10 = 0')
    db.commit()
    db.close()
    got = carve(path)
    bodies = sorted(r['values']['t'] - 1700000000 for r in got)
    assert bodies == [10, 20, 30, 40], bodies
    assert all(r['kind'] == 'free space' and r['table'] == 'm' for r in got)
    # Write-ahead log: an edited and a deleted row survive in the frames that preceded the change.
    path = os.path.join(folder, 'w.db')
    db = sqlite3.connect(path)
    db.execute('PRAGMA journal_mode = WAL')
    db.execute('PRAGMA wal_autocheckpoint = 0')
    db.execute('CREATE TABLE m (id INTEGER PRIMARY KEY, t INTEGER, body TEXT)')
    db.executemany('INSERT INTO m VALUES (?, ?, ?)', [(i, i, f'first {i}') for i in range(200, 210)])
    db.commit()
    db.execute("UPDATE m SET body = 'second' WHERE id = 201")
    db.execute('DELETE FROM m WHERE id = 205')
    db.commit()
    reader = sqlite3.connect(f'file:{path}?mode=ro', uri=True)
    reader.execute('SELECT count(*) FROM m').fetchone()  # an open reader keeps the log on disk past db.close()
    db.close()
    older = {(r['rowid'], r['values']['body']) for r in carve(path) if r['kind'] == 'older page version'}
    reader.close()
    assert older == {(201, 'first 201'), (205, 'first 205')}, older
    print('ok', len(got), 'from free space,', len(older), 'from the log')
