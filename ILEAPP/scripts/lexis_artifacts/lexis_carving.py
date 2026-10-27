"""Deleted records of an artifact, read with the artifact's own query (LEXIS helper, no artifacts).

scripts/sqlite_carver.py recovers the records of every table of a database that a query no
longer returns: older versions of pages kept by the main file and the write-ahead log, and the
free space of pages. Here a recovered record counts as deleted only when the active database
holds no row with its row id and none with the same values in a UNIQUE index (or in the same
index of an `others` database, e.g. a decrypted backup); an older version of a live row is an
edit, not a deletion, so it is left out. Ambiguity resolves towards "present".

The surviving records of a table are written into a private copy of the database in place of
that table's rows, and the standard function runs unchanged on the copy, so each app's own
rules read them (joins with the live tables included). Only the tables the function reads are
tried. A table counts only when its records add rows: when they also change rows the function
already returns (a lookup table), it is skipped. Every row is reported once, never one equal to
a live row. The evidence is never written: copies live in a temporary folder removed at exit.

Reliability, per record, from how the carver found it:
  high   - an older version of a page, with its row id;
  medium - the free space of a page, with its row id;
  low    - free space, row id lost, or a record cut short by a missing overflow page.
"""
import atexit
import collections
import functools
import hashlib
import os
import shutil
import sqlite3
import tempfile
from pathlib import Path
from urllib.parse import unquote

from scripts.ilapfuncs import logfunc
from scripts.sqlite_carver import carve

TIERS = {'high': 'high, an older version of a page with its row id',
         'medium': 'medium, free space of a page with its row id',
         'low': 'low, free space with the row id lost or the record truncated'}
_RANK = {'high': 0, 'medium': 1, 'low': 2}
_SIDECARS = ('-wal', '-shm', '-journal')
_CARVED = {}
_TEMP = []


def _temp():
    if not _TEMP:
        _TEMP.append(tempfile.mkdtemp(prefix='lexis_carving_'))
        atexit.register(shutil.rmtree, _TEMP[0], True)
    return _TEMP[0]


def _q(name):
    return '"' + str(name).replace('"', '""') + '"'


def _is_sqlite(path):
    try:
        with open(path, 'rb') as handle:
            return handle.read(16) == b'SQLite format 3\x00'
    except OSError:
        return False


def _connect(path):
    """Read-only with the write-ahead log applied; ignoring the log when it cannot be read."""
    uri = Path(path).resolve().as_uri()
    for mode in ('mode=ro', 'immutable=1'):
        try:
            conn = sqlite3.connect(f'{uri}?{mode}', uri=True)
            conn.execute('SELECT count(*) FROM sqlite_master').fetchone()
            return conn
        except sqlite3.Error:
            continue
    return None


def tier(record):
    if record['rowid'] is None or record['truncated']:
        return 'low'
    return 'high' if record['kind'] == 'older page version' else 'medium'


def _keys(conn, table):
    """Row ids and the value tuples of every UNIQUE index of a table."""
    try:
        rowids = {r[0] for r in conn.execute(f'SELECT rowid FROM {_q(table)}')}
    except sqlite3.Error:
        rowids = set()
    uniques = []
    try:
        for index in conn.execute(f'PRAGMA index_list({_q(table)})').fetchall():
            if not index[2]:
                continue
            cols = [c[2] for c in conn.execute(f'PRAGMA index_info({_q(index[1])})')]
            if cols and None not in cols:
                values = {tuple(r) for r in conn.execute(f'SELECT {", ".join(map(_q, cols))} FROM {_q(table)}')}
                uniques.append((tuple(cols), values))
    except sqlite3.Error:
        pass
    return rowids, uniques


def _unique_values(record, cols):
    values = tuple(record['values'].get(c) for c in cols)
    return None if None in values else values


def deleted(path, others=()):
    """{table: [record]} of the records carved from a database that it no longer holds."""
    if path not in _CARVED:
        try:
            _CARVED[path] = carve(path)
        except Exception as ex:  # pylint: disable=broad-exception-caught
            logfunc(f'LEXIS carving: {os.path.basename(path)} could not be carved ({ex})')
            _CARVED[path] = []
    records = [r for r in _CARVED[path] if r['table']]
    if not records:
        return {}
    conns = [c for c in [_connect(path)] + [_connect(o) for o in others] if c]
    if not conns:
        return {}
    keys, out = {}, collections.defaultdict(dict)
    for record in records:
        table = record['table']
        if table not in keys:
            live = _keys(conns[0], table)
            uniques = list(live[1])
            for other in conns[1:]:
                uniques += _keys(other, table)[1]
            keys[table] = (live[0], uniques)
        rowids, uniques = keys[table]
        if record['rowid'] is not None and record['rowid'] in rowids:
            continue  # an older version of a live row: an edit
        if any(_unique_values(record, cols) in values for cols, values in uniques):
            continue  # the same message is in the active database (or another one given)
        ident = record['rowid'] if record['rowid'] is not None else id(record)
        previous = out[table].get(ident)
        if previous is None or _RANK[tier(record)] <= _RANK[tier(previous)]:
            out[table][ident] = record  # the best found, the newest version among equals
    for conn in conns:
        conn.close()
    result = {}
    for table, found in out.items():
        # A record without its row id that shares a UNIQUE key with a better one is the same record.
        cols_list = [cols for cols, _ in keys[table][1]]
        seen, kept = set(), []
        for record in sorted(found.values(), key=lambda r: _RANK[tier(r)]):
            idents = {(cols, _unique_values(record, cols)) for cols in cols_list} - {(cols, None) for cols in cols_list}
            if idents & seen:
                continue
            seen |= idents
            kept.append(record)
        result[table] = kept
    return result


def _fingerprint(path):
    """Size and the hash of the first and last 64 KiB: the storage views of one file are read once."""
    try:
        size = os.path.getsize(path)
        with open(path, 'rb') as handle:
            head = handle.read(65536)
            handle.seek(max(0, size - 65536))
            return size, hashlib.sha1(head + handle.read()).hexdigest()
    except OSError:
        return path


def _cell(value):
    return value if isinstance(value, (str, int, float, bytes, type(None))) else repr(value)


def _row_key(row):
    return tuple(_cell(v) for v in row)


class _Reads:
    """Records the tables read through every SQLite connection opened on one file while active."""

    def __init__(self, path):
        self.path, self.tables, self._connect = os.path.normcase(os.path.abspath(path)), set(), sqlite3.connect

    def __enter__(self):
        def connect(database, *args, **kwargs):
            conn = self._connect(database, *args, **kwargs)
            target = str(database)
            if target.startswith('file:'):
                target = unquote(target[5:].split('?', 1)[0])  # ilapfuncs percent-encodes the whole path
            if os.path.normcase(os.path.basename(target.replace('\\', '/'))) == os.path.basename(self.path):
                conn.set_authorizer(self._authorize)
            return conn
        sqlite3.connect = connect
        return self

    def _authorize(self, action, arg1, *_):
        if action == sqlite3.SQLITE_READ and arg1:
            self.tables.add(arg1)
        return sqlite3.SQLITE_OK

    def __exit__(self, *_):
        sqlite3.connect = self._connect


def _call(run, swap, path, work, rel):
    """run on the file list with the working copy; its path in the rows put back to the database's."""
    try:
        _, rows, _ = run(swap)
    except Exception as ex:  # pylint: disable=broad-exception-caught
        logfunc(f'LEXIS carving: {os.path.basename(path)}: {ex}')
        return None
    forms = {work, work.replace('\\', '/')}
    mapped = []
    for row in rows:
        cells = []
        for cell in row:
            if isinstance(cell, str):
                for form in forms:
                    cell = cell.replace(form, rel)
            cells.append(cell)
        mapped.append(tuple(cells))
    return mapped


def recovered(run, files, live_rows, context, others=()):
    """Rows the deleted records of the SQLite files among `files` add to the output of `run`.

    run: called with a file list, returns the standard function's (headers, rows, source).
    live_rows: the rows run(files) returned. others: databases whose UNIQUE keys also count as
    present (decrypted backups). Returns [(row, tier, database path)] and the source paths that gave rows.
    """
    files = [str(f) for f in files]
    seen = {_row_key(r) for r in live_rows}
    out, sources, read = [], [], set()
    for path in files:
        if path.endswith(_SIDECARS) or not _is_sqlite(path) or _fingerprint(path) in read:
            continue
        read.add(_fingerprint(path))
        carved = deleted(path, others)
        if not carved:
            continue
        rel = context.get_relative_path(path) if hasattr(context, 'get_relative_path') else path
        parts = [p for p in os.path.splitdrive(rel)[1].replace('\\', '/').split('/') if p]
        work = os.path.join(tempfile.mkdtemp(dir=_temp()), *parts)
        os.makedirs(os.path.dirname(work), exist_ok=True)
        source = _connect(path)
        if source is None:
            continue
        target = sqlite3.connect(work)
        source.backup(target)
        source.close()
        for (name,) in target.execute("SELECT name FROM sqlite_master WHERE type = 'trigger'").fetchall():
            target.execute(f'DROP TRIGGER {_q(name)}')
        target.commit()
        target.close()
        swap = [work if f == path else f for f in files if f not in (path + s for s in _SIDECARS)]
        call = functools.partial(_call, run, swap, path, work, rel)

        with _Reads(work) as reads:
            if call() is None:
                continue
        for table in sorted(set(carved) & reads.tables):
            conn = sqlite3.connect(work)
            conn.execute(f'DELETE FROM {_q(table)}')
            conn.commit()
            conn.close()
            empty = call()
            if empty is None:
                continue
            baseline = collections.Counter(_row_key(r) for r in empty)
            for level in TIERS:
                group = [r for r in carved[table] if tier(r) == level]
                if not group:
                    continue
                conn = sqlite3.connect(work)
                conn.execute('PRAGMA ignore_check_constraints = 1')
                conn.execute(f'DELETE FROM {_q(table)}')
                for record in group:
                    cols = list(record['values'])
                    cur = conn.execute(f'INSERT OR IGNORE INTO {_q(table)} ({", ".join(map(_q, cols))}) '
                                       f'VALUES ({", ".join("?" * len(cols))})', list(record['values'].values()))
                    if cur.rowcount == 1 and record['rowid'] is not None and cur.lastrowid != record['rowid']:
                        conn.execute(f'UPDATE OR IGNORE {_q(table)} SET rowid = ? WHERE rowid = ?', (record['rowid'], cur.lastrowid))
                conn.commit()
                conn.close()
                got = call()
                if got is None:
                    continue
                new = collections.Counter(_row_key(r) for r in got) - baseline
                if sum(new.values()) != len(got) - len(empty):
                    logfunc(f'LEXIS carving: {os.path.basename(path)}, table {table}: its records change rows '
                            'the artifact already returns, so it is not read as deleted messages')
                    break
                for row in got:
                    key = _row_key(row)
                    if new[key] > 0 and key not in seen:
                        new[key] -= 1
                        seen.add(key)
                        out.append((row, level, path))
                        if path not in sources:
                            sources.append(path)
            _restore(path, work, table)
    return out, sources


def _restore(path, work, table):
    """Put back a table's live rows in the working copy, so the next table is tried against them."""
    source = _connect(path)
    if source is None:
        return
    conn = sqlite3.connect(work)
    cols = [c[1] for c in conn.execute(f'PRAGMA table_info({_q(table)})')]
    names = ', '.join(map(_q, cols))
    conn.execute(f'DELETE FROM {_q(table)}')
    rows = source.execute(f'SELECT rowid, {names} FROM {_q(table)}')
    conn.executemany(f'INSERT OR IGNORE INTO {_q(table)} (rowid, {names}) VALUES ({", ".join("?" * (len(cols) + 1))})', rows)
    conn.commit()
    conn.close()
    source.close()


def note(level, path):
    return f'Deleted: carved from {os.path.basename(path)}; reliability {TIERS[level]}'
