"""LEXIS_WhatsApp names the message types the standard artifacts show as numbers, describes in a
Content column what a row without text was (deleted, view once, poll, contact card, edited), and
lists contact cards and view-once media."""
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.lexis_artifacts import LEXIS_WhatsApp as lexis  # pylint: disable=wrong-import-position

SCHEMA = '''
CREATE TABLE jid (_id INTEGER PRIMARY KEY, raw_string TEXT);
CREATE TABLE chat (_id INTEGER PRIMARY KEY, jid_row_id INTEGER, subject TEXT);
CREATE TABLE message (_id INTEGER PRIMARY KEY, chat_row_id INTEGER, from_me INTEGER, key_id TEXT,
    timestamp INTEGER, received_timestamp INTEGER, message_type INTEGER, text_data TEXT,
    sender_jid_row_id INTEGER);
CREATE TABLE message_media (message_row_id INTEGER, file_path TEXT, file_size INTEGER);
CREATE TABLE message_location (message_row_id INTEGER, latitude REAL, longitude REAL,
    live_location_share_duration INTEGER, live_location_final_latitude REAL,
    live_location_final_longitude REAL, live_location_final_timestamp INTEGER);
CREATE TABLE message_revoked (message_row_id INTEGER, revoked_key_id TEXT, admin_jid_row_id INTEGER,
    revoke_timestamp INTEGER);
CREATE TABLE message_poll (message_row_id INTEGER, selectable_options_count INTEGER);
CREATE TABLE message_poll_option (_id INTEGER PRIMARY KEY, message_row_id INTEGER, option_name TEXT,
    vote_total INTEGER);
CREATE TABLE message_vcard (_id INTEGER PRIMARY KEY, message_row_id INTEGER, vcard TEXT);
CREATE TABLE message_edit_info (message_row_id INTEGER, original_key_id TEXT, edited_timestamp INTEGER,
    sender_timestamp INTEGER);
'''
VCARD = 'BEGIN:VCARD\nVERSION:3.0\nFN:Jane Roe\nTEL;type=CELL:+1 555 0100\nTEL:+1 555 0101\nEND:VCARD'
# _id: (message_type, text_data, from_me)
ROWS = {1: (15, None, 0), 2: (64, None, 0), 3: (42, None, 0), 4: (66, 'Lunch?', 1), 5: (4, None, 0),
        6: (0, 'fixed text', 1), 7: (15, None, 1), 8: (82, None, 0)}


def build(root, drop=()):
    path = root / 'data/data/com.whatsapp/databases/msgstore.db'
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path)
    db.executescript(SCHEMA)
    db.executemany('INSERT INTO jid VALUES (?, ?)', [(1, '111@s.whatsapp.net'), (2, '999@s.whatsapp.net')])
    db.execute('INSERT INTO chat VALUES (1, 1, NULL)')
    for n, (kind, text, from_me) in ROWS.items():
        db.execute('INSERT INTO message VALUES (?, 1, ?, ?, ?, 0, ?, ?, NULL)',
                   (n, from_me, f'K{n:019d}', n * 1_000_000, kind, text))
    db.execute('INSERT INTO message_revoked VALUES (1, ?, NULL, 1700000000000)', ('K' + '1' * 19,))
    db.execute('INSERT INTO message_revoked VALUES (2, ?, 2, 1700000001000)', ('K' + '2' * 19,))
    db.execute('INSERT INTO message_poll VALUES (4, 1)')
    db.executemany('INSERT INTO message_poll_option VALUES (?, 4, ?, ?)', [(1, 'Yes', 2), (2, 'No', 0)])
    db.execute('INSERT INTO message_vcard VALUES (1, 5, ?)', (VCARD,))
    db.execute('INSERT INTO message_edit_info VALUES (6, ?, 1700000002000, 0)', ('K' + '6' * 19,))
    for table in drop:
        db.execute(f'DROP TABLE {table}')
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

    def get_report_folder(self):
        return str(self.root / 'report' / '_HTML' / 'WhatsApp')


class TestTypes(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.root = pathlib.Path(self.tmp.name)
        lexis._PREPARED.clear()  # pylint: disable=protected-access

    def tearDown(self):
        self.tmp.cleanup()

    def run_artifact(self, name, drop=()):
        context = Context(self.root, [build(self.root, drop)])
        with patch.object(lexis, 'logfunc'), patch.object(lexis.wa, 'logfunc'), patch.object(lexis.carving, 'logfunc'):
            return getattr(lexis, name).__wrapped__(context)

    def table(self, name, drop=()):
        headers, rows, _ = self.run_artifact(name, drop)
        names = [h if isinstance(h, str) else h[0] for h in headers]
        return names, [dict(zip(names, r)) for r in rows]

    def test_labels_and_content(self):
        names, rows = self.table('get_whatsapp_one_to_one_messages')
        self.assertEqual(names[names.index('Message Type') + 1], 'Content')
        self.assertEqual(names[-3:], ['Account ID', 'App', 'Recovered'])
        by_ts = {r['Message Timestamp'].timestamp() // 1000: r for r in rows}
        got = {n: (by_ts[n]['Message Type'], by_ts[n]['Content'], by_ts[n]['Message']) for n in ROWS}
        self.assertEqual(got[1], ('Deleted For Everyone', 'Deleted for everyone at 2023-11-14 22:13:20 UTC', None))
        self.assertEqual(got[2], ('Deleted By Admin',
                                  'Deleted by admin 999@s.whatsapp.net at 2023-11-14 22:13:21 UTC', None))
        self.assertEqual(got[3], ('View Once Picture', 'View once picture', None))
        self.assertEqual(got[4], ('Poll', 'Poll: Yes (2), No (0)', 'Lunch?'))
        self.assertEqual(got[5], ('Contact Card', 'Contact card: Jane Roe +1 555 0100, +1 555 0101', None))
        self.assertEqual(got[6], ('Text', 'Edited at 2023-11-14 22:13:22 UTC', 'fixed text'))
        self.assertEqual(got[7], ('Deleted For Everyone', 'Deleted for everyone', None))   # no message_revoked row
        self.assertEqual(got[8], ('View Once Audio', 'View once audio', None))

    def test_missing_tables_leave_content_blank(self):
        _, rows = self.table('get_whatsapp_one_to_one_messages',
                             drop=('message_vcard', 'message_revoked', 'message_poll', 'message_poll_option',
                                   'message_edit_info'))
        by_ts = {r['Message Timestamp'].timestamp() // 1000: r for r in rows}
        self.assertEqual(by_ts[5]['Content'], '')
        self.assertEqual(by_ts[1]['Content'], 'Deleted for everyone')    # still known from the type
        self.assertEqual(len(rows), len(ROWS))

    def test_contact_cards(self):
        _, rows = self.table('get_whatsapp_contact_cards')
        self.assertEqual([(r['Name'], r['Phone Numbers'], r['Direction'], r['Chat']) for r in rows],
                         [('Jane Roe', '+1 555 0100, +1 555 0101', 'Incoming', '111@s.whatsapp.net')])
        self.assertEqual(rows[0]['App'], 'WhatsApp')

    def test_view_once(self):
        _, rows = self.table('get_whatsapp_view_once')
        self.assertEqual(sorted(r['Type'] for r in rows), ['View Once Audio', 'View Once Picture'])

    def test_new_artifacts_registered(self):
        for key in ('get_whatsapp_contact_cards', 'get_whatsapp_view_once'):
            self.assertIn(key, lexis.__artifacts_v2__)
            self.assertEqual(getattr(lexis, key).__name__, key)


if __name__ == '__main__':
    unittest.main()
