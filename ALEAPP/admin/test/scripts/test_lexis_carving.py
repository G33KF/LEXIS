"""lexis_carving: deleted records are read with the artifact's own query, edits and live copies are
left out, a lookup table is never read as messages, and LEXIS_Carved replaces the message
artifacts of the messaging modules with a Recovered column."""
import contextlib
import importlib.util
import os
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))


def _load(name):
    spec = importlib.util.spec_from_file_location(f'test_{name}', ROOT / 'scripts' / 'lexis_artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


carving = _load('lexis_carving')

QUERY = '''SELECT m.timestamp, c.subject, m.body, p.name, m.key_id FROM message m
           JOIN chat c ON c._id = m.chat_id LEFT JOIN person p ON p._id = m.person_id ORDER BY m.timestamp'''


def run(files):
    """A stand-in artifact: one row per message of the msgstore.db among the files."""
    rows = []
    for f in files:
        if os.path.basename(f) == 'msgstore.db':
            with contextlib.closing(sqlite3.connect(f'file:{f}?mode=ro', uri=True)) as db:
                rows += db.execute(QUERY).fetchall()
    return ('Time', 'Chat', 'Body', 'Person', 'Key'), rows, '\n'.join(files)


class Context:
    @staticmethod
    def get_relative_path(path):
        return path


def build(folder):
    path = os.path.join(folder, 'data', 'data', 'com.example', 'databases', 'msgstore.db')
    os.makedirs(os.path.dirname(path))
    db = sqlite3.connect(path)
    db.execute('PRAGMA secure_delete = 0')
    db.execute('CREATE TABLE chat (_id INTEGER PRIMARY KEY, subject TEXT)')
    db.execute('CREATE TABLE person (_id INTEGER PRIMARY KEY, name TEXT)')
    db.execute('CREATE TABLE message (_id INTEGER PRIMARY KEY, chat_id INTEGER, person_id INTEGER, timestamp INTEGER, '
               'body TEXT, key_id TEXT UNIQUE)')
    db.execute("INSERT INTO chat VALUES (1, 'family')")
    db.executemany('INSERT INTO person VALUES (?, ?)', [(i, f'person name number {i}') for i in range(1, 30)])
    db.executemany('INSERT INTO message VALUES (?, 1, ?, ?, ?, ?)',
                   [(i, i % 29 + 1, 1700000000 + i, f'message body number {i} ' * 3, f'KEY{i:020d}') for i in range(1, 41)])
    db.commit()
    db.execute("UPDATE message SET body = 'edited' WHERE _id = 5")
    db.commit()
    db.execute('DELETE FROM message WHERE _id IN (10, 20)')
    db.execute('DELETE FROM person WHERE _id IN (3, 4)')
    db.commit()
    db.close()
    return path


class TestCarving(unittest.TestCase):
    def test_deleted_messages_only_once_with_the_query_rules(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
            path = build(folder)
            live = run([path])[1]
            with patch.object(carving, 'logfunc'):
                found, sources = carving.recovered(run, [path], live, Context())
            self.assertEqual(sorted(r[0] - 1700000000 for r, _, _ in found), [10, 20])
            self.assertTrue(all(r[1] == 'family' for r, _, _ in found))      # joined with the live chat table
            self.assertNotIn('edited', [r[2] for r, _, _ in found])
            self.assertTrue(all(level in carving.TIERS for _, level, _ in found))
            self.assertEqual(sources, [path])
            self.assertIn('msgstore.db', carving.note(found[0][1], path))
            # The deleted persons (a LEFT JOIN lookup) change live rows: never read as messages.
            self.assertEqual(len(found), 2)

    def test_unique_key_in_another_database_counts_as_present(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
            path = build(folder)
            other = os.path.join(folder, 'backup.db')
            db = sqlite3.connect(other)
            db.execute('CREATE TABLE message (_id INTEGER PRIMARY KEY, key_id TEXT UNIQUE)')
            db.execute("INSERT INTO message VALUES (99, ?)", (f'KEY{10:020d}',))
            db.commit()
            db.close()
            carving._CARVED.clear()  # pylint: disable=protected-access
            with patch.object(carving, 'logfunc'):
                found, _ = carving.recovered(run, [path], run([path])[1], Context(), others=[other])
            self.assertEqual([r[0] - 1700000000 for r, _, _ in found], [20])

    def test_write_ahead_log_edit_left_out_deletion_high(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
            path = os.path.join(folder, 'msgstore.db')
            db = sqlite3.connect(path)
            db.execute('PRAGMA journal_mode = WAL')
            db.execute('PRAGMA wal_autocheckpoint = 0')
            db.execute('CREATE TABLE chat (_id INTEGER PRIMARY KEY, subject TEXT)')
            db.execute('CREATE TABLE person (_id INTEGER PRIMARY KEY, name TEXT)')
            db.execute('CREATE TABLE message (_id INTEGER PRIMARY KEY, chat_id INTEGER, person_id INTEGER, '
                       'timestamp INTEGER, body TEXT, key_id TEXT UNIQUE)')
            db.execute("INSERT INTO chat VALUES (1, 'work')")
            db.executemany('INSERT INTO message VALUES (?, 1, NULL, ?, ?, ?)',
                           [(i, i, f'first {i}', f'K{i:020d}') for i in range(1, 11)])
            db.commit()
            db.execute("UPDATE message SET body = 'second' WHERE _id = 7")
            db.execute('DELETE FROM message WHERE _id = 8')
            db.commit()
            reader = sqlite3.connect(f'file:{path}?mode=ro', uri=True)
            reader.execute('SELECT count(*) FROM message').fetchone()  # keeps the log on disk past close()
            db.close()
            carving._CARVED.clear()  # pylint: disable=protected-access
            with patch.object(carving, 'logfunc'):
                found, _ = carving.recovered(run, [path], run([path])[1], Context())
            reader.close()
            self.assertEqual([(r[0], r[2], level) for r, level, _ in found], [(8, 'first 8', 'high')])

    def test_no_sqlite_no_rows(self):
        with tempfile.TemporaryDirectory() as folder:
            text = os.path.join(folder, 'msgstore.db')
            pathlib.Path(text).write_text('not a database', encoding='utf-8')
            self.assertEqual(carving.recovered(run, [text], [], Context()), ([], []))


class TestLoader(unittest.TestCase):
    def test_message_artifacts_replaced_with_recovered(self):
        from scripts import plugin_loader  # pylint: disable=import-outside-toplevel
        with patch('builtins.print'):
            loader = plugin_loader.PluginLoader()
        specs = {spec.name: spec for spec in loader.plugins}
        carved = _load('LEXIS_Carved')
        self.assertIn('get_sms_mms', carved.__artifacts_v2__)
        self.assertNotIn('get_whatsapp_messages', carved.__artifacts_v2__)
        for key in carved.__artifacts_v2__:
            self.assertEqual(specs[key].module_name, 'LEXIS_Carved', key)
            self.assertIn('conversation', carved.__artifacts_v2__[key]['data_views'])


if __name__ == '__main__':
    unittest.main()
