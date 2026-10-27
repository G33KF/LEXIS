"""LEXIS_RecoveryApps reads the copies of messaging notifications that "recover deleted messages" apps
keep: five exact formats, a generic detector restricted to the curated packages, nothing elsewhere."""
import json
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from xml.sax.saxutils import escape

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.lexis_artifacts import LEXIS_RecoveryApps as lexis  # pylint: disable=wrong-import-position

MS = 1700000000000   # 2023-11-14 22:13:20 UTC


def sqlite(path, script, rows=()):
    path.parent.mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(path)
    db.executescript(script)
    for sql, values in rows:
        db.execute(sql, values)
    db.commit()
    db.close()
    return path


def build(root):
    data = root / 'data/data'
    files = [
        sqlite(data / 'com.tiriig.whatsdeleted/databases/database',
               'CREATE TABLE Chat (id TEXT PRIMARY KEY, user TEXT, message TEXT, dateTime INTEGER, app TEXT, '
               'isDeleted INTEGER, isGroup INTEGER); CREATE TABLE room_master_table (id INTEGER, identity_hash TEXT);',
               [('INSERT INTO Chat VALUES (?, ?, ?, ?, ?, ?, ?)', ('a', 'Alice', 'see you', MS, 'com.whatsapp', 1, 0)),
                ('INSERT INTO Chat VALUES (?, ?, ?, ?, ?, ?, ?)',
                 ('b', 'Team', 'Bob: on my way', MS + 1000, 'com.whatsapp', 0, 1))]),
        sqlite(data / 'bot.box.whatsdelete/databases/chatroom.db',
               'CREATE TABLE chatRooms (name TEXT PRIMARY KEY, last_message TEXT, time INTEGER, allChats TEXT, dp BLOB);',
               [('INSERT INTO chatRooms VALUES (?, ?, ?, ?, NULL)',
                 ('Carol', 'second', MS, json.dumps([{'message': 'first', 'time': MS, 'isDeleted': True},
                                                     {'message': 'second', 'time': MS + 5000, 'isDeleted': False}])))]),
        sqlite(data / 'com.test.recovermessages/databases/recover.db',
               'CREATE TABLE messeges (_id INTEGER PRIMARY KEY AUTOINCREMENT, package TEXT, username TEXT, msg TEXT, '
               'small_time TEXT, whole_time LONG);',
               [('INSERT INTO messeges (package, username, msg, small_time, whole_time) VALUES (?, ?, ?, ?, ?)',
                 ('org.telegram.messenger', 'Dave', 'ciao', 'March 05 14:07', '2024-03-05 14:07:00'))]),
        sqlite(data / 'com.tenqube.notisave/databases/noti.db',      # closed source: generic detector
               'CREATE TABLE android_metadata (locale TEXT); CREATE TABLE noti_list (_id INTEGER PRIMARY KEY, '
               'title TEXT, content TEXT, package_name TEXT, post_time INTEGER); '
               'CREATE TABLE settings (name TEXT, value TEXT);',
               [('INSERT INTO noti_list VALUES (1, ?, ?, ?, ?)', ('Erin', 'deleted later', 'com.whatsapp', MS))]),
        sqlite(data / 'com.example.notes/databases/database',       # decoy outside the list
               'CREATE TABLE Chat (id TEXT, user TEXT, message TEXT, dateTime INTEGER, app TEXT, isDeleted INTEGER, '
               'isGroup INTEGER);', [('INSERT INTO Chat VALUES (?, ?, ?, ?, ?, ?, ?)', ('x', 'X', 'no', MS, 'a', 0, 0))]),
    ]
    log = data / 'com.gmail.anubhavdas54.whatsdeleted/files/msgLog.txt'
    log.parent.mkdir(parents=True)
    log.write_text('11/14/23, 10:13 PM | Frank: line one\ncontinued\n11/14/23, 10:14 PM | Frank: again\n',
                   encoding='utf-8')
    signal_log = log.with_name('signalMsgLog.txt')
    signal_log.write_text('11/14/23, 10:15 PM | Gina: from signal\n', encoding='utf-8')
    prefs = data / 'com.wassaver.app/shared_prefs/wassaver_messages.xml'
    prefs.parent.mkdir(parents=True)
    captured = json.dumps([{'id': '1', 'sender': 'Hank', 'text': 'hello', 'timestamp': MS,
                            'packageName': 'com.whatsapp', 'isGroup': True, 'groupName': 'Family'}])
    prefs.write_text("<?xml version='1.0' encoding='utf-8' standalone='yes' ?>\n<map>\n"
                     f'    <string name="captured_messages">{escape(captured, {chr(34): "&quot;"})}</string>\n'
                     '    <boolean name="listener_enabled" value="true" />\n</map>\n', encoding='utf-8')
    files += [log, signal_log, prefs]
    # the same jimale database again under the other storage spelling: counted once
    twin = root / 'data/user/0/com.tiriig.whatsdeleted/databases/database'
    twin.parent.mkdir(parents=True)
    twin.write_bytes(files[0].read_bytes())
    files.append(twin)
    files.append(files[0].with_name('database-wal'))      # a sidecar: not a database on its own
    files[-1].write_bytes(b'')
    return files


class Context:
    def __init__(self, root, files):
        self.root, self.files = root, [str(f) for f in files]

    def get_files_found(self):
        return self.files

    def get_relative_path(self, path):
        return str(pathlib.Path(path).relative_to(self.root)).replace('\\', '/')


class TestRecoveryApps(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.root = pathlib.Path(self.tmp.name)
        headers, rows, self.source = lexis.get_recoveryAppsMessages.__wrapped__(
            Context(self.root, build(self.root)))
        self.names = [h if isinstance(h, str) else h[0] for h in headers]
        self.rows = [dict(zip(self.names, r)) for r in rows]

    def tearDown(self):
        self.tmp.cleanup()

    def by(self, app):
        return [r for r in self.rows if r['Capturing App'] == app]

    def test_jimale(self):
        rows = sorted(self.by('com.tiriig.whatsdeleted'), key=lambda r: r['Message'])
        self.assertEqual(len(rows), 2)                     # the twin spelling adds nothing
        group, single = rows
        self.assertEqual((group['Chat'], group['Sender'], group['Message'], group['Marked Deleted']),
                         ('Team', 'Bob', 'on my way', ''))
        self.assertEqual((single['Chat'], single['Sender'], single['Marked Deleted'], single['Source App']),
                         ('Alice', 'Alice', 'Yes', 'com.whatsapp'))
        self.assertEqual(str(single['Timestamp (UTC)']), '2023-11-14 22:13:20+00:00')
        self.assertTrue(single['Method'].startswith('Exact'))
        self.assertEqual(single['Source File'], 'data/data/com.tiriig.whatsdeleted/databases/database')

    def test_thebotbox(self):
        rows = sorted(self.by('bot.box.whatsdelete'), key=lambda r: r['Message'])
        self.assertEqual([(r['Message'], r['Marked Deleted'], r['Chat']) for r in rows],
                         [('first', 'Yes', 'Carol'), ('second', '', 'Carol')])
        self.assertEqual(rows[0]['Source App'], 'com.whatsapp')

    def test_smkhurramkhan_keeps_local_time(self):
        row, = self.by('com.test.recovermessages')
        self.assertEqual((row['Timestamp (UTC)'], row['Time As Stored']),
                         (None, '2024-03-05 14:07:00 (device local time)'))
        self.assertEqual((row['Source App'], row['Sender'], row['Message']), ('org.telegram.messenger', 'Dave', 'ciao'))

    def test_text_log(self):
        rows = self.by('com.gmail.anubhavdas54.whatsdeleted')
        self.assertEqual([(r['Source App'], r['Sender'], r['Message'], r['Time As Stored']) for r in rows], [
            ('com.whatsapp', 'Frank', 'line one\ncontinued', '11/14/23, 10:13 PM (device local time)'),
            ('com.whatsapp', 'Frank', 'again', '11/14/23, 10:14 PM (device local time)'),
            ('org.thoughtcrime.securesms', 'Gina', 'from signal', '11/14/23, 10:15 PM (device local time)')])

    def test_wassaver(self):
        row, = self.by('com.wassaver.app')
        self.assertEqual((row['Chat'], row['Sender'], row['Message'], row['Source App']),
                         ('Family', 'Hank', 'hello', 'com.whatsapp'))

    def test_generic_detector(self):
        row, = self.by('com.tenqube.notisave')
        self.assertEqual((row['Chat'], row['Message'], row['Source App']), ('Erin', 'deleted later', 'com.whatsapp'))
        self.assertEqual(row['Method'], 'Heuristic: noti_list (text=content, title=title)')
        self.assertEqual(str(row['Timestamp (UTC)']), '2023-11-14 22:13:20+00:00')

    def test_scope(self):
        self.assertEqual(self.by('com.example.notes'), [])
        self.assertEqual(len(self.rows), 10)
        self.assertNotIn('com.example.notes', self.source)
        self.assertTrue(self.source)

    def test_registered(self):
        self.assertIn('get_recoveryAppsMessages', lexis.__artifacts_v2__)
        paths = lexis.__artifacts_v2__['get_recoveryAppsMessages']['paths']
        self.assertTrue(any('com.drilens.wamr' in p for p in paths))


if __name__ == '__main__':
    unittest.main()
