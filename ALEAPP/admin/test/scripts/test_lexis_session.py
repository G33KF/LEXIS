"""LEXIS_Session reads Session Android messages (sms and mms tables) once secrets.json gives the key,
trying the SQLCipher settings of each database era and keeping the one whose HMAC verifies."""
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.lexis_artifacts import LEXIS_Session as lexis  # pylint: disable=wrong-import-position

ME_IN, ME_OUT = 0xA00014, 0xA00017      # received / sent as stored (SECURE | PUSH | base type)
ALICE, BOB = '05' + 'a' * 64, '05' + 'b' * 64
GROUP = '__textsecure_group__!' + '00' * 16
COMMUNITY = '__loki_public_chat_group__!' + 'ab' * 8
SCHEMA = '''
CREATE TABLE thread (_id INTEGER PRIMARY KEY, recipient_ids TEXT);
CREATE TABLE sms (_id INTEGER PRIMARY KEY, thread_id INTEGER, address TEXT, date INTEGER, date_sent INTEGER,
    read INTEGER, type INTEGER, body TEXT, expires_in INTEGER);
CREATE TABLE mms (_id INTEGER PRIMARY KEY, thread_id INTEGER, date INTEGER, date_received INTEGER, msg_box INTEGER,
    read INTEGER, body TEXT, address TEXT, expires_in INTEGER, quote_id INTEGER, quote_author TEXT, quote_body TEXT);
CREATE TABLE part (_id INTEGER PRIMARY KEY, mid INTEGER, ct TEXT);
CREATE TABLE groups (_id INTEGER PRIMARY KEY, group_id TEXT, title TEXT);
CREATE TABLE loki_public_chat_database (thread_id INTEGER PRIMARY KEY, public_chat TEXT);
'''
OLD_NAMES = 'CREATE TABLE session_contact_database (session_id TEXT PRIMARY KEY, name TEXT, nickname TEXT);'
NEW_NAMES = 'CREATE TABLE recipient_settings (address TEXT PRIMARY KEY, name TEXT);'


def build(folder, name='session.db', old=False):
    path = pathlib.Path(folder) / 'data/data/network.loki.messenger/databases' / name
    path.parent.mkdir(parents=True)
    db = sqlite3.connect(path)
    db.executescript(SCHEMA + (OLD_NAMES if old else NEW_NAMES))
    if old:
        db.execute('INSERT INTO session_contact_database VALUES (?, ?, ?)', (ALICE, 'Alice', 'Ali'))
    else:
        db.execute('INSERT INTO recipient_settings VALUES (?, ?)', (ALICE, 'Alice'))
    db.executemany('INSERT INTO thread VALUES (?, ?)', [(1, ALICE), (2, GROUP), (3, COMMUNITY)])
    db.execute('INSERT INTO groups VALUES (1, ?, ?)', (GROUP, 'Family'))
    db.execute('INSERT INTO loki_public_chat_database VALUES (3, ?)',
               ('{"room": "lobby", "server": "https://example.org", "displayName": "Lobby"}',))
    db.executemany('INSERT INTO sms VALUES (?, ?, ?, ?, ?, 1, ?, ?, ?)', [
        (1, 1, ALICE, 2000, 1000, ME_IN, 'hi', 0),
        (2, 1, ALICE, 3000, 3000, ME_OUT, 'hello back', 60000),
        (3, 1, ALICE, 4000, 4000, 0xA00000 | 19, None, 0),           # deleted incoming
        (4, 2, GROUP, 5000, 5000, 0x80000 | 23, None, 0),            # group update, sent
        (5, 3, BOB, 6000, 6000, ME_IN, 'community post', 0)])
    db.execute('INSERT INTO mms VALUES (1, 2, 7000, 7500, ?, 1, ?, ?, 0, 1000, ?, ?)',
               (ME_IN, 'photo', ALICE, ALICE, 'hi'))
    db.executemany('INSERT INTO part VALUES (?, 1, ?)', [(1, 'image/jpeg'), (2, 'image/jpeg')])
    db.executescript('UPDATE sms SET date = date + 1700000000000, date_sent = date_sent + 1700000000000; '
                     'UPDATE mms SET date = date + 1700000000000, date_received = date_received + 1700000000000;')
    db.commit()
    db.close()
    return path


class Context:
    def __init__(self, root, files):
        self.root, self.files = root, [str(f) for f in files]

    def get_files_found(self):
        return self.files

    def get_relative_path(self, path):
        return str(pathlib.Path(path).relative_to(self.root)).replace('\\', '/')


class TestSession(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.root = pathlib.Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def table(self, name='session.db', old=False):
        root = pathlib.Path(tempfile.mkdtemp(dir=self.root))
        path = build(root, name, old)
        files = [path, path.with_name(name + '-wal')]
        with patch.object(lexis, '_decrypted', side_effect=lambda _, p: p if p == str(path) else None), \
                patch.object(lexis, 'logfunc'):
            headers, rows, source = lexis.get_sessionMessages.__wrapped__(Context(root, files))
        names = [h if isinstance(h, str) else h[0] for h in headers]
        return {(r[names.index('Table')], r[names.index('Message ID')]): dict(zip(names, r)) for r in rows}, source

    def test_messages(self):
        rows, source = self.table()
        self.assertEqual(len(rows), 6)
        hi, back = rows[('sms', 1)], rows[('sms', 2)]
        self.assertEqual((hi['Chat'], hi['Sender'], hi['Sender ID'], hi['Direction'], hi['Message']),
                         ('Alice', 'Alice', ALICE, 'Incoming', 'hi'))
        self.assertEqual((str(hi['Date Sent']), str(hi['Date Received'])),
                         ('2023-11-14 22:13:21+00:00', '2023-11-14 22:13:22+00:00'))
        self.assertEqual((back['Direction'], back['Sender'], back['Expires In (s)']), ('Outgoing', 'Local user', 60))
        self.assertEqual(rows[('sms', 3)]['Deleted'], 'Yes')
        self.assertEqual((rows[('sms', 4)]['Chat'], rows[('sms', 4)]['Event']), ('Family', 'Group update'))
        self.assertEqual((rows[('sms', 5)]['Chat'], rows[('sms', 5)]['Sender']), ('Lobby', BOB))
        photo = rows[('mms', 1)]
        self.assertEqual((photo['Chat'], photo['Attachments'], photo['Quote']), ('Family', 2, 'Alice: hi'))
        self.assertEqual(str(photo['Date Sent']), '2023-11-14 22:13:27+00:00')
        self.assertEqual(photo['Source File'], 'data/data/network.loki.messenger/databases/session.db')
        self.assertNotIn('-wal', source)

    def test_old_era_names(self):
        rows, _ = self.table('signal_v4.db', old=True)
        self.assertEqual(rows[('sms', 1)]['Chat'], 'Ali')      # nickname first

    def test_no_key_no_rows(self):
        root = pathlib.Path(tempfile.mkdtemp(dir=self.root))
        path = build(root)
        with patch.object(lexis, '_decrypted', return_value=None), patch.object(lexis, 'logfunc'):
            _, rows, source = lexis.get_sessionMessages.__wrapped__(Context(root, [path]))
        self.assertEqual((rows, source), ([], ''))

    def test_settings_tried_per_era(self):
        tried = []

        def fake(_path, _key, _out, **settings):
            tried.append((settings['hmac_algorithm'], settings['kdf_iterations']))
            return (4, 4) if settings['kdf_iterations'] == 256000 else (4, 0)
        lexis._cache.clear()  # pylint: disable=protected-access
        with patch.object(lexis.ses, '_signal_secrets', return_value=[('ab' * 32, None)]), \
                patch.object(lexis, 'decrypt_sqlcipher_db', side_effect=fake), patch.object(lexis, 'logfunc'):
            out = lexis._decrypted(None, str(self.root / 'x/network.loki.messenger/databases/signal_v4.db'))  # pylint: disable=protected-access
        self.assertTrue(out)
        self.assertEqual(tried[0], ('sha512', 256000))          # the era's own settings first
        lexis._cache.clear()  # pylint: disable=protected-access
        with patch.object(lexis.ses, '_signal_secrets', return_value=[('ab' * 32, None)]), \
                patch.object(lexis, 'decrypt_sqlcipher_db', return_value=(4, 0)), patch.object(lexis, 'logfunc'):
            self.assertIsNone(lexis._decrypted(None, str(self.root / 'y/network.loki.messenger/databases/session.db')))  # pylint: disable=protected-access

    def test_registered(self):
        self.assertIn('get_sessionMessages', lexis.__artifacts_v2__)
        self.assertEqual(lexis.get_sessionMessages.__name__, 'get_sessionMessages')


if __name__ == '__main__':
    unittest.main()
