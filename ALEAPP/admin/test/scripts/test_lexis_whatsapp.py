"""LEXIS_WhatsApp decrypts local WhatsApp backups, runs every WhatsApp artifact once per app data
directory (WhatsApp and WhatsApp Business) and adds the backup messages the active database lost."""
import hashlib
import pathlib
import sqlite3
import sys
import tempfile
import unittest
import zlib
from unittest.mock import patch

from Crypto.Cipher import AES

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
CREATE TABLE audit (n INTEGER);
CREATE TRIGGER message_bd BEFORE DELETE ON message BEGIN SELECT RAISE(ABORT, 'no deletes'); END;
'''
PEER = '111@s.whatsapp.net'
# text -> (key_id, from_me, timestamp ms)
MESSAGES = {'A': ('A' * 20, 0, 1_000_000), 'B': ('B' * 20, 1, 2_000_000), 'C': ('C' * 20, 0, 3_000_000),
            'D': ('D' * 20, 1, 4_000_000), 'E': ('E' * 20, 0, 5_000_000), 'F': ('F' * 20, 0, 6_000_000),
            'S1': ('', 0, 7_000_000), 'S2': ('', 0, 8_000_000), 'G': ('G' * 20, 0, 9_000_000)}


def msgstore(path, texts):
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    connection.executescript(SCHEMA)
    connection.execute('INSERT INTO jid VALUES (1, ?)', (PEER,))
    connection.execute('INSERT INTO chat VALUES (1, 1, NULL)')
    for n, text in enumerate(texts, 1):
        key_id, from_me, timestamp = MESSAGES[text]
        connection.execute('INSERT INTO message VALUES (?, 1, ?, ?, ?, 0, 0, ?, NULL)',
                           (n, from_me, key_id, timestamp, text))
    connection.commit()
    connection.close()
    return path.read_bytes()


def gcm(key, iv, plain):
    cipher = AES.new(key, AES.MODE_GCM, nonce=iv)
    body, tag = cipher.encrypt_and_digest(zlib.compress(plain))
    return body, tag


def crypt12(key, db):
    iv = b'\x12' * 16
    body, tag = gcm(key, iv, db)
    return b'\x00\x01\x01' + b'S' * 32 + b'G' * 16 + iv + body + tag + b'1234'


def field(number, value):
    return bytes([number << 3 | 2, len(value)]) + value


def crypt14(key, db):
    iv = b'\x14' * 16
    proto = field(2, b'\x08\x01' + field(3, b'S' * 32) + field(4, b'G' * 16) + field(6, iv))
    body, tag = gcm(key, iv, db)
    return bytes([len(proto), 1]) + proto + body + tag + b'M' * 16


def crypt15(root, db):
    iv = b'\x15' * 16
    proto = field(3, field(1, iv))
    body, tag = gcm(lexis._hkdf(root), iv, db)  # pylint: disable=protected-access
    return bytes([len(proto)]) + proto + body + tag + b'M' * 16


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
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


class TestCrypto(unittest.TestCase):
    def test_hkdf_is_rfc5869(self):
        # RFC 5869 test case 3 (zero-length salt is a zero salt; info empty) reworked for our info
        prk = bytes.fromhex('19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04')
        ikm = bytes.fromhex('0b' * 22)
        import hmac  # pylint: disable=import-outside-toplevel
        self.assertEqual(hmac.new(b'\x00' * 32, ikm, hashlib.sha256).digest(), prk)
        self.assertEqual(lexis._hkdf(ikm, b'')[:16].hex(), '8da4e775a563c18f715f802a063c5a31')  # pylint: disable=protected-access

    def test_candidates_skip_salts(self):
        key = b'k' * 32
        data = crypt14(key, b'SQLite format 3\x00' + b'\x00' * 100)
        candidates = lexis._candidates(data[:1024])  # pylint: disable=protected-access
        self.assertIn((b'\x14' * 16, 2 + data[0]), candidates)


class TestBackups(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = self.root = pathlib.Path(self.tmp.name)
        app = root / 'data/data/com.whatsapp'
        biz = root / 'data/data/com.whatsapp.w4b'
        sd = root / 'storage/emulated/0/Android/media'
        key, root_key, biz_key = b'K' * 32, b'R' * 32, b'B' * 32
        files = [
            write(app / 'files/key', b'\xac\xed' + b'x' * 124 + key),
            write(app / 'files/encrypted_backup.key', b'\xac\xed\x00\x05ur' + b'x' * 20 + root_key),
            write(biz / 'files/key', b'\xac\xed' + b'y' * 124 + biz_key),
            write(app / 'shared_prefs/com.whatsapp_preferences_light.xml',
                  b'<map><string name="cc">39</string><string name="ph">3330000000</string></map>'),
        ]
        msgstore(app / 'databases/msgstore.db', ['A', 'B', 'S1'])
        msgstore(biz / 'databases/msgstore.db', ['E'])
        files += [app / 'databases/msgstore.db', biz / 'databases/msgstore.db']
        newer = msgstore(root / 'tmp/newer.db', ['A', 'B', 'C', 'S1', 'S2'])
        older = msgstore(root / 'tmp/older.db', ['A', 'C', 'D'])
        bizdb = msgstore(root / 'tmp/biz.db', ['E', 'F'])
        dbs = sd / 'com.whatsapp/WhatsApp/Databases'
        files += [write(dbs / 'msgstore.db.crypt15', crypt15(root_key, newer)),
                  write(dbs / 'msgstore-2026-10-01.1.db.crypt14', crypt14(key, older)),
                  write(dbs / 'msgstore-2026-09-01.1.db.crypt14', crypt14(b'X' * 32, older)),
                  write(sd / 'com.whatsapp.w4b/WhatsApp Business/Databases/msgstore.db.crypt12', crypt12(biz_key, bizdb))]
        # the same backup under a second storage view is read once
        files.append(write(root / 'sdcard/Android/media/com.whatsapp/WhatsApp/Databases/msgstore.db.crypt15',
                           crypt15(root_key, newer)))
        self.context = Context(root, files)
        lexis._PREPARED.clear()  # pylint: disable=protected-access

    def tearDown(self):
        self.tmp.cleanup()

    def run_artifact(self, name):
        with patch.object(lexis, 'logfunc'), patch.object(lexis.wa, 'logfunc'):
            return getattr(lexis, name).__wrapped__(self.context)

    def test_inventory(self):
        headers, rows, source = self.run_artifact('get_whatsapp_backups')
        table = {r[1].rsplit('/', 1)[-1]: dict(zip([h if isinstance(h, str) else h[0] for h in headers], r))
                 for r in rows}
        self.assertEqual(len(rows), 4, rows)
        newer = table['msgstore.db.crypt15']
        self.assertEqual((newer['Decrypted'], newer['GCM Tag Verified'], newer['Messages'],
                          newer['Messages Not In Active Database'], newer['Merged Into WhatsApp Messages']),
                         ('Yes', 'Yes', 5, 2, 2))
        self.assertTrue(newer['Key File'].endswith('encrypted_backup.key'))
        self.assertEqual(newer['Account ID'], '+393330000000')
        older = table['msgstore-2026-10-01.1.db.crypt14']
        self.assertEqual((older['Decrypted'], older['GCM Tag Verified'], older['Messages Not In Active Database'],
                          older['Merged Into WhatsApp Messages']), ('Yes', 'Yes', 2, 1))
        self.assertEqual(table['msgstore-2026-09-01.1.db.crypt14']['Decrypted'], 'No')
        self.assertIn('64-digit', table['msgstore-2026-09-01.1.db.crypt14']['Note'])
        biz = table['msgstore.db.crypt12']
        self.assertEqual((biz['App'], biz['Decrypted'], biz['Merged Into WhatsApp Messages']),
                         ('WhatsApp Business', 'Yes', 1))
        copy = self.root / 'report' / newer['Decrypted Copy']
        self.assertTrue(copy.read_bytes().startswith(b'SQLite format 3\x00'))
        self.assertNotIn(str(self.root / 'report'), source)

    def test_messages_merged_once_and_marked(self):
        headers, rows, source = self.run_artifact('get_whatsapp_one_to_one_messages')
        self.assertEqual(headers[-3:], ('Account ID', 'App', 'Recovered'))
        message = headers.index('Message')
        got = sorted((r[message], r[-2], r[-1].split('backup ')[-1]) for r in rows)
        self.assertEqual(got, [('A', 'WhatsApp', ''), ('B', 'WhatsApp', ''),
                               ('C', 'WhatsApp', 'msgstore.db.crypt15'),
                               ('D', 'WhatsApp', 'msgstore-2026-10-01.1.db.crypt14'),
                               ('E', 'WhatsApp Business', ''),
                               ('F', 'WhatsApp Business', 'msgstore.db.crypt12'),
                               ('S1', 'WhatsApp', ''),
                               ('S2', 'WhatsApp', 'msgstore.db.crypt15')])
        self.assertIn('msgstore.db.crypt15', source)

    def test_deleted_messages_carved_once(self):
        db = sqlite3.connect(self.root / 'data/data/com.whatsapp/databases/msgstore.db')
        db.execute('PRAGMA secure_delete = 0')
        db.execute('DROP TRIGGER message_bd')
        for n, text in ((10, 'C'), (11, 'G')):
            key_id, from_me, timestamp = MESSAGES[text]
            db.execute('INSERT INTO message VALUES (?, 1, ?, ?, ?, 0, 0, ?, NULL)', (n, from_me, key_id, timestamp, text))
        db.commit()
        db.execute('DELETE FROM message WHERE _id IN (10, 11)')
        db.commit()
        db.close()
        with patch.object(lexis.carving, 'logfunc'):
            headers, rows, _ = self.run_artifact('get_whatsapp_one_to_one_messages')
        message = headers.index('Message')
        texts = [r[message] for r in rows]
        self.assertEqual(texts.count('C'), 1)                           # a backup already gave it
        carved = [r for r in rows if r[message] == 'G']
        self.assertEqual(len(carved), 1)
        self.assertIn('carved from msgstore.db', carved[0][-1])

    def test_backup_messages(self):
        headers, rows, _ = self.run_artifact('get_whatsapp_backup_one_to_one_messages')
        message = headers.index('Message')
        got = sorted((r[-2].split(': ')[-1], r[message], r[-1]) for r in rows)
        self.assertEqual(got, [('msgstore-2026-10-01.1.db.crypt14', 'A', 'Yes'),
                               ('msgstore-2026-10-01.1.db.crypt14', 'C', 'No'),
                               ('msgstore-2026-10-01.1.db.crypt14', 'D', 'No'),
                               ('msgstore.db.crypt12', 'E', 'Yes'), ('msgstore.db.crypt12', 'F', 'No'),
                               ('msgstore.db.crypt15', 'A', 'Yes'), ('msgstore.db.crypt15', 'B', 'Yes'),
                               ('msgstore.db.crypt15', 'C', 'No'), ('msgstore.db.crypt15', 'S1', 'Yes'),
                               ('msgstore.db.crypt15', 'S2', 'No')])

    def test_other_artifacts_per_app(self):
        headers, rows, _ = self.run_artifact('get_whatsapp_call_logs')
        self.assertEqual(headers[-2:], ('Account ID', 'App'))
        self.assertEqual(rows, [])


class TestArtifacts(unittest.TestCase):
    def test_every_standard_key_replaced(self):
        keys = (set(lexis.wa.__artifacts_v2__) | set(lexis.wax.__artifacts_v2__)) - {'get_whatsapp_user_profile'}
        self.assertTrue(keys <= set(lexis.__artifacts_v2__))
        for key in keys:
            self.assertTrue(hasattr(getattr(lexis, key), '__wrapped__'), key)
            self.assertEqual(getattr(lexis, key).__name__, key)
        self.assertTrue(any('com.whatsapp.w4b' in p for p in lexis.__artifacts_v2__['get_whatsapp_contacts']['paths']))

    def test_loader(self):
        from scripts import plugin_loader  # pylint: disable=import-outside-toplevel
        with patch('builtins.print'):
            loader = plugin_loader.PluginLoader([plugin_loader.PLUGINPATH])
        specs = {spec.name: spec for spec in loader.plugins}
        for key in lexis.__artifacts_v2__:
            self.assertEqual(specs[key].module_name, 'LEXIS_WhatsApp', key)
        self.assertEqual(specs['get_whatsapp_user_profile'].module_name, 'WhatsApp')


if __name__ == '__main__':
    unittest.main()
