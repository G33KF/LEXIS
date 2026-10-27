"""LEXIS_Signal adds to Signal - Messages the edit chain, who deleted a message (deleted_by), mentions
and stories, and lists message edits and stories."""
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.lexis_artifacts import LEXIS_Signal as lexis  # pylint: disable=wrong-import-position

MODERN = '''
CREATE TABLE recipient (_id INTEGER PRIMARY KEY, e164 TEXT, profile_joined_name TEXT, system_joined_name TEXT,
    username TEXT);
CREATE TABLE message (_id INTEGER PRIMARY KEY, date_sent INTEGER, date_received INTEGER, thread_id INTEGER,
    type INTEGER, body TEXT, from_recipient_id INTEGER, to_recipient_id INTEGER, read INTEGER,
    remote_deleted INTEGER, view_once INTEGER, quote_id INTEGER, quote_author INTEGER, quote_body TEXT,
    quote_missing INTEGER, expires_in INTEGER, latest_revision_id INTEGER, original_message_id INTEGER,
    revision_number INTEGER, deleted_by INTEGER, mentions_self INTEGER, story_type INTEGER,
    parent_story_id INTEGER, story_archived INTEGER);
CREATE TABLE mention (_id INTEGER PRIMARY KEY, thread_id INTEGER, message_id INTEGER, recipient_id INTEGER,
    range_start INTEGER, range_length INTEGER);
'''
LEGACY = '''
CREATE TABLE recipient (_id INTEGER PRIMARY KEY, phone TEXT, profile_joined_name TEXT, system_display_name TEXT,
    username TEXT);
CREATE TABLE message (_id INTEGER PRIMARY KEY, date_sent INTEGER, date_received INTEGER, thread_id INTEGER,
    type INTEGER, body TEXT, from_recipient_id INTEGER, to_recipient_id INTEGER, read INTEGER,
    remote_deleted INTEGER, view_once INTEGER, quote_id INTEGER, quote_author INTEGER, quote_body TEXT,
    quote_missing INTEGER, expires_in INTEGER);
'''
IN, OUT = 20, 23   # MessageTypes base types: incoming, sent


def build(folder, legacy=False):
    path = pathlib.Path(folder) / 'data/data/org.thoughtcrime.securesms/databases/signal.db'
    path.parent.mkdir(parents=True)
    db = sqlite3.connect(path)
    db.executescript(LEGACY if legacy else MODERN)
    db.executemany('INSERT INTO recipient VALUES (?, ?, ?, ?, NULL)',
                   [(1, '+15550000001', 'Me', None), (2, '+15550000002', 'Alice', None), (3, '+15550000003', None, 'Bob')])
    rows = [  # _id, date_sent, type, body, from
        (1, 1000, IN, 'first draft', 2), (2, 2000, IN, 'second draft', 2), (3, 3000, IN, 'final text', 2),
        (4, 4000, IN, None, 2), (5, 5000, OUT, 'hi @Alice @Bob', 1), (6, 6000, IN, 'my story', 2),
        (7, 7000, OUT, 'nice story', 1), (8, 8000, OUT, 'love it', 1)]
    for _id, sent, kind, body, sender in rows:
        db.execute('INSERT INTO message (_id, date_sent, date_received, thread_id, type, body, from_recipient_id, '
                   'to_recipient_id, read, remote_deleted, view_once, quote_id, quote_missing, expires_in) '
                   'VALUES (?, ?, ?, 1, ?, ?, ?, 1, 1, 0, 0, 0, 0, 0)', (_id, sent, sent, kind, body, sender))
    if not legacy:
        db.executescript('''
            UPDATE message SET latest_revision_id = 3, original_message_id = 1, revision_number = 0 WHERE _id = 1;
            UPDATE message SET latest_revision_id = 3, original_message_id = 1, revision_number = 1 WHERE _id = 2;
            UPDATE message SET original_message_id = 1, revision_number = 2 WHERE _id = 3;
            UPDATE message SET deleted_by = 2 WHERE _id = 4;
            UPDATE message SET mentions_self = 0, story_type = 0, parent_story_id = 0;
            UPDATE message SET mentions_self = 1 WHERE _id = 5;
            UPDATE message SET story_type = 1, story_archived = 1 WHERE _id = 6;
            UPDATE message SET parent_story_id = 6 WHERE _id = 7;
            UPDATE message SET parent_story_id = -6 WHERE _id = 8;
            INSERT INTO mention VALUES (1, 1, 5, 2, 3, 1), (2, 1, 5, 3, 10, 1);''')
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


class TestSignal(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.root = pathlib.Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def table(self, name, legacy=False):
        root = pathlib.Path(tempfile.mkdtemp(dir=self.root))   # one extraction per call
        path = build(root, legacy)
        with patch.object(lexis.sig, '_decrypted_database', return_value=(str(path), None)), \
                patch.object(lexis.sig, 'logfunc'), patch.object(lexis, 'logfunc'):
            headers, rows, _ = getattr(lexis, name).__wrapped__(Context(root, [path]))
        names = [h if isinstance(h, str) else h[0] for h in headers]
        return names, {r[names.index('Message ID')]: dict(zip(names, r)) for r in rows}

    def test_message_columns(self):
        names, rows = self.table('get_signalMessages')
        self.assertEqual(names[-5:], list(lexis.EXTRA_HEADERS))
        self.assertEqual(rows[1]['Edit'], 'Replaced by edit (message 3)')
        self.assertEqual(rows[3]['Edit'], 'Edited (revision 2, original message 1)')
        self.assertEqual(rows[4]['Deleted By'], 'Alice')
        self.assertEqual((rows[5]['Mentions'], rows[5]['Mentions Me']), ('Alice, Bob', 'Yes'))
        self.assertEqual(rows[6]['Story'], 'Story')
        self.assertEqual(rows[7]['Story'], 'Reply to story (message 6)')
        self.assertEqual(rows[8]['Story'], 'Reply to story (message 6)')
        self.assertEqual(rows[7]['Message'], 'nice story')       # standard columns untouched

    def test_legacy_schema_leaves_columns_blank(self):
        _, rows = self.table('get_signalMessages', legacy=True)
        self.assertEqual(len(rows), 8)
        self.assertTrue(all(r[h] == '' for r in rows.values() for h in lexis.EXTRA_HEADERS))
        self.assertEqual(self.table('get_signalMessageEdits', legacy=True)[1], {})
        self.assertEqual(self.table('get_signalStories', legacy=True)[1], {})

    def test_edits(self):
        _, rows = self.table('get_signalMessageEdits')
        self.assertEqual(sorted((r['Message ID'], r['Revision'], r['Current'], r['Message']) for r in rows.values()),
                         [(1, 0, 'No', 'first draft'), (2, 1, 'No', 'second draft'), (3, 2, 'Yes', 'final text')])
        self.assertTrue(all(r['Original Message ID'] == 1 and r['Sender'] == 'Alice' for r in rows.values()))

    def test_stories(self):
        _, rows = self.table('get_signalStories')
        self.assertEqual(list(rows), [6])
        self.assertEqual((rows[6]['Sender'], rows[6]['Archived'], rows[6]['Replies'], rows[6]['Direction']),
                         ('Alice', 'Yes', 2, 'Incoming'))

    def test_registered(self):
        for key in ('get_signalMessages', 'get_signalMessageEdits', 'get_signalStories'):
            self.assertIn(key, lexis.__artifacts_v2__)
            self.assertEqual(getattr(lexis, key).__name__, key)


if __name__ == '__main__':
    unittest.main()
