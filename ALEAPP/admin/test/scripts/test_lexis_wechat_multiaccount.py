"""LEXIS_WeChatMultiAccount opens every WeChat account database, reading the uin also from
the cdninfo caches in the account's own directory, and replaces the weChat.py artifacts."""
import hashlib
import pathlib
import sqlite3
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.lexis_artifacts import LEXIS_WeChatMultiAccount as lexis  # pylint: disable=wrong-import-position

base = lexis.base


def account_dir(uin):
    return hashlib.md5(f'mm{uin}'.encode()).hexdigest()


def write_bytes(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def cdninfo(uin):
    """A cache with the uin as a big-endian int32 between unrelated bytes."""
    return b'\x00\x07header' + struct.pack('>i', uin) + b'\x01\x02trailer'


class TestCdninfoUin(unittest.TestCase):
    def test_uin_read_signed_and_unsigned(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            negative = root / 'a/cdninfo_new.cache'
            positive = root / 'b/cdninfo_new.cache'
            write_bytes(negative, cdninfo(-777))
            write_bytes(positive, cdninfo(555))
            self.assertEqual(lexis._cdninfo_uin([str(negative)], account_dir(-777)), '-777')  # pylint: disable=protected-access
            self.assertEqual(lexis._cdninfo_uin([str(positive)], account_dir(555)), '555')  # pylint: disable=protected-access
            self.assertEqual(lexis._cdninfo_uin([str(negative), str(positive)], account_dir(555)), '555')  # pylint: disable=protected-access
            self.assertIsNone(lexis._cdninfo_uin([str(positive)], account_dir(556)))  # pylint: disable=protected-access
            with patch.object(base, 'logfunc'):
                self.assertIsNone(lexis._cdninfo_uin([str(root / 'missing.cache')], account_dir(555)))  # pylint: disable=protected-access

    def test_unsigned_spelling(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / 'cdninfo_new.cache'
            write_bytes(path, cdninfo(-777))
            unsigned = str(-777 & 0xFFFFFFFF)
            self.assertEqual(lexis._cdninfo_uin([str(path)], account_dir(unsigned)), unsigned)  # pylint: disable=protected-access


IMEI = '490154203237518'  # the textbook Luhn-valid IMEI


def cfg(*strings):
    """A Java-serialized HashMap stand-in holding the strings as TC_STRING records."""
    body = b''.join(b'\x74' + len(s).to_bytes(2, 'big') + s.encode() for s in strings)
    return b'\xac\xed\x00\x05sr\x00\x11java.util.HashMap' + body + b'\x78'


class TestImeiFallbacks(unittest.TestCase):
    def test_luhn(self):
        self.assertTrue(lexis._luhn(IMEI))  # pylint: disable=protected-access
        for value in ('490154203237519', '49015420323751', 'abcdefghijklmno', '４９0154203237518'):
            self.assertFalse(lexis._luhn(value), value)  # pylint: disable=protected-access

    def test_cfg_strings_and_preferences(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            compatible = root / 'CompatibleInfo.cfg'
            write_bytes(compatible, cfg('x', IMEI))
            prefs = root / 'shared_prefs/DENGTA_META.xml'
            prefs.parent.mkdir(parents=True)
            prefs.write_text('<map><string name="IMEI_DENGTA">AAA</string><string name="other">'
                             f'{IMEI[:-1]}9</string><string name="any">{IMEI}</string></map>', encoding='utf-8')
            found = lexis._imei_fallbacks([str(prefs)], [str(compatible)])  # pylint: disable=protected-access
            self.assertEqual(found, ['x', IMEI, 'AAA', IMEI])


def make_db(path, imei, uin):
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = base.sqlcipher.connect(str(path))  # pylint: disable=no-member
    connection.execute(f"PRAGMA key = '{base._db_key(imei, uin)}';")  # pylint: disable=protected-access
    for pragma in base._CIPHER_PRAGMAS:  # pylint: disable=protected-access
        connection.execute(pragma)
    connection.execute('CREATE TABLE marker (uin TEXT)')
    connection.execute('INSERT INTO marker VALUES (?)', (uin,))
    connection.commit()
    connection.close()


class Context:
    def __init__(self, root, files):
        self.root, self.files = root, [str(f) for f in files]

    def get_files_found(self):
        return self.files

    def get_relative_path(self, path):
        return str(pathlib.Path(path).relative_to(self.root)).replace('\\', '/')


@unittest.skipUnless(base._SQLCIPHER_AVAILABLE, 'sqlcipher3 not installed')  # pylint: disable=protected-access
class TestConnections(unittest.TestCase):
    def test_every_account_with_a_database_opens_once(self):
        fallback = base._FALLBACK_IMEI  # pylint: disable=protected-access
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            home = root / 'data/data/com.tencent.mm'
            view = root / 'data/user/0/com.tencent.mm'      # same container, other spelling
            tenant = root / 'data/user/10/com.tencent.mm'   # second Android user
            prefs = home / 'shared_prefs/auth_info_key_prefs.xml'
            signed_in = home / f'MicroMsg/{account_dir(222)}/EnMicroMsg.db'
            other = home / f'MicroMsg/{account_dir(-777)}/EnMicroMsg.db'
            other_cache = home / f'MicroMsg/{account_dir(-777)}/cdninfo_new.cache'
            other_view = view / f'MicroMsg/{account_dir(-777)}/EnMicroMsg.db'
            other_view_cache = view / f'MicroMsg/{account_dir(-777)}/cdninfo_new.cache'
            wrong = home / f'MicroMsg/{account_dir(888)}/EnMicroMsg.db'
            wrong_cache = home / f'MicroMsg/{account_dir(888)}/cdninfo_mobile_net_new.cache'
            tenant_db = tenant / f'MicroMsg/{account_dir(999)}/EnMicroMsg.db'
            tenant_cache = tenant / f'MicroMsg/{account_dir(999)}/cdninfo_mobile_net_new.cache'
            other_imei = home / f'MicroMsg/{account_dir(-555)}/EnMicroMsg.db'   # keyed with a cfg IMEI
            other_imei_cache = home / f'MicroMsg/{account_dir(-555)}/cdninfo_new.cache'
            compatible = home / 'MicroMsg/CompatibleInfo.cfg'
            prefs.parent.mkdir(parents=True, exist_ok=True)
            prefs.write_text('<map><int name="_auth_uin" value="222" /></map>', encoding='utf-8')
            make_db(signed_in, fallback, '222')
            make_db(other, fallback, '-777')
            make_db(other_view, fallback, '-777')
            make_db(wrong, fallback, '888')
            make_db(tenant_db, fallback, '999')
            make_db(other_imei, IMEI, '-555')
            write_bytes(other_imei_cache, cdninfo(-555))
            write_bytes(compatible, cfg(IMEI))
            write_bytes(other_cache, cdninfo(-777))
            write_bytes(other_view_cache, cdninfo(-777))
            write_bytes(wrong_cache, cdninfo(887))       # does not reproduce the directory
            write_bytes(tenant_cache, cdninfo(999))
            files = [prefs, signed_in, other, other_cache, other_view, other_view_cache,
                     wrong, wrong_cache, tenant_db, tenant_cache, other_imei, other_imei_cache, compatible]

            base._logged.clear()  # pylint: disable=protected-access
            with patch.object(base, 'logfunc') as log:
                opened = {}
                for connection, uin, db_path in base._connections(Context(root, files)):  # pylint: disable=protected-access
                    opened[db_path] = (uin, connection.execute('SELECT uin FROM marker').fetchone()[0])
                    connection.close()
            self.assertEqual(opened, {str(signed_in): ('222', '222'), str(other): ('-777', '-777'),
                                      str(tenant_db): ('999', '999'), str(other_imei): ('-555', '-555')})
            messages = [call.args[0] for call in log.call_args_list]
            self.assertEqual(len(messages), 1, messages)
            self.assertIn(str(wrong), messages[0])
            self.assertIs(base._connections, lexis._connections)  # pylint: disable=protected-access


class TestArtifacts(unittest.TestCase):
    def test_same_keys_names_and_columns(self):
        self.assertEqual(set(lexis.__artifacts_v2__), set(base.__artifacts_v2__))
        for key, info in base.__artifacts_v2__.items():
            mine = lexis.__artifacts_v2__[key]
            for field in ('name', 'category', 'output_types', 'artifact_icon'):
                self.assertEqual(mine.get(field), info.get(field), (key, field))
            cdninfo_paths = tuple(f'*/com.tencent.mm/MicroMsg/*/{c}'
                                  for c in lexis._CDNINFO)  # pylint: disable=protected-access
            cfg_paths = tuple(f'*/com.tencent.mm/MicroMsg/{c}' for c in lexis._CFG)  # pylint: disable=protected-access
            self.assertEqual(mine['paths'], tuple(info['paths']) + cdninfo_paths + cfg_paths)
            self.assertIn('cdninfo', mine['notes'])
            self.assertTrue(hasattr(getattr(lexis, key), '__wrapped__'), key)

    def test_paths_ignore_a_decoy_package(self):
        import fnmatch  # pylint: disable=import-outside-toplevel
        decoy = f'data/data/com.tencent.mmx/MicroMsg/{account_dir(1)}/cdninfo_new.cache'
        real = f'data/data/com.tencent.mm/MicroMsg/{account_dir(1)}/cdninfo_new.cache'
        patterns = lexis.__artifacts_v2__['wechat_messages']['paths']
        self.assertFalse(any(fnmatch.fnmatch(decoy, p) for p in patterns))
        self.assertTrue(any(fnmatch.fnmatch(real, p) for p in patterns))


def lvbuffer(*strings):
    """The observed call-row lvbuffer: '{', a four-byte integer, length-prefixed strings, padding."""
    out = b'{' + bytes(4)
    for s in strings:
        data = s.encode('utf-8')
        out += struct.pack('>H', len(data)) + data
    return out + bytes(20)


def message_row(time, kind, text, msg_id):
    return (time, 'Incoming', 's', 'c', text, '', 'wxid_a', 'wxid_a', 0, kind,
            '', '', '', '', '', '', 6, msg_id, text, '', '')


class TestCallText(unittest.TestCase):
    def test_lvbuffer_shape(self):
        self.assertEqual(lexis._lvbuffer_text(lvbuffer('', '通话时长 01:36')), '通话时长 01:36')  # pylint: disable=protected-access
        self.assertEqual(lexis._lvbuffer_text(lvbuffer('', '对方已取消')), '对方已取消')  # pylint: disable=protected-access
        for bad in (None, b'', b'x' + bytes(10), b'{' + bytes(4) + b'\x00\x00\x00\xff' + b'ab',
                    b'{' + bytes(4) + b'\x00\x00\x00\x02\xff\xfe'):
            self.assertEqual(lexis._lvbuffer_text(bad), '', bad)  # pylint: disable=protected-access

    def test_call_rows_show_the_call_text(self):
        xml = '<voipmsg type="VoIPBubbleMsg"><VoIPBubbleMsg><msg><![CDATA[已拒绝]]></msg></VoIPBubbleMsg></voipmsg>'
        stored = [((1700000000000, 'wxid_a', 1, 'voip_content_voice', lvbuffer('', '对方已取消')), None),
                  ((1700000060000, 'wxid_a', 2, xml, lvbuffer('', '已拒绝')), None),
                  ((1700000120000, 'wxid_a', 3, 'voip_content_voice', b'junk'), None)]
        t = [base._ms_time(ms) for ms in (1700000000000, 1700000060000, 1700000120000)]  # pylint: disable=protected-access
        calls = [(t[0], 'Incoming', 's', 'c', 'wxid_a', 'voip_content_voice', '', '', '', 1),
                 (t[1], 'Incoming', 's', 'c', 'wxid_a', '已拒绝', '', '', '', 2),
                 (t[2], 'Incoming', 's', 'c', 'wxid_a', 'voip_content_voice', '', '', '', 3)]
        messages = [message_row(t[0], 50, 'voip_content_voice', 1), message_row(t[1], 50, '', 2),
                    message_row(t[2], 50, 'voip_content_voice', 3), message_row(t[0], 1, 'voip_content_voice', 1)]
        with patch.object(base, '_query', return_value=(stored, [])), \
                patch.object(base.wechat_calls, '__wrapped__', return_value=((), calls, '')), \
                patch.object(base.wechat_messages, '__wrapped__', return_value=((), messages, '')):
            _, got_calls, _ = lexis.wechat_calls.__wrapped__(None)
            _, got_messages, _ = lexis.wechat_messages.__wrapped__(None)
        self.assertEqual([r[5] for r in got_calls], ['对方已取消', '已拒绝', 'voip_content_voice'])
        self.assertEqual([r[4] for r in got_messages],
                         ['对方已取消', '已拒绝', 'voip_content_voice', 'voip_content_voice'])
        self.assertEqual(got_messages[0][18], 'voip_content_voice')   # Content (as stored) is kept


class TestVoiceMedia(unittest.TestCase):
    def test_a_voice_row_checks_in_its_own_file_as_media(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            db_a = root / 'user0/com.tencent.mm/MicroMsg/aaaa/EnMicroMsg.db'
            voice_a = root / 'user0/com.tencent.mm/MicroMsg/aaaa/voice2/ab/cd/msg_v1.amr'
            voice_b = root / 'user10/com.tencent.mm/MicroMsg/bbbb/voice2/ab/cd/msg_v2.amr'
            write_bytes(db_a, b'')
            write_bytes(voice_a, b'\x02#!SILK_V3' + bytes(8))
            write_bytes(voice_b, b'\x02#!SILK_V3' + bytes(8))

            def connections(_context):
                con = sqlite3.connect(':memory:')
                con.executescript('CREATE TABLE voiceinfo (MsgLocalId INTEGER, FileName TEXT);'
                                  "INSERT INTO voiceinfo VALUES (1, 'v1'), (2, 'v2');"
                                  'CREATE TABLE userinfo (id INTEGER, value TEXT);')
                yield con, '1', str(db_a)

            def row(kind, msg_id):
                return ((1700000000000, 0, 'wxid_b', kind, 'x', '', 3, msg_id), '1', str(db_a))
            # 1: its file is in the row's own container; 2: the name exists only under another
            # Android user's copy of the app; 3: no voiceinfo row; 4: not a voice row
            rows = [row(34, 1), row(34, 2), row(34, 3), row(1, 1)]
            with patch.object(base, '_connections', connections), \
                    patch.object(base, '_query', side_effect=lambda _c, _s, keep_path=False: (rows if keep_path else [], [])), \
                    patch.object(base, '_contact_names', return_value={}), \
                    patch.object(base, 'unique_files', return_value=[db_a, voice_a, voice_b]), \
                    patch.object(base, 'check_in_media', side_effect=lambda path, name: f'ref:{name}') as check_in:
                _, got, _ = lexis.wechat_messages.__wrapped__(None)
        self.assertEqual([r[5] for r in got], ['ref:msg_v1.amr', '', '', ''])   # Media
        self.assertEqual([r[12] for r in got], ['msg_v1.amr', '', '', ''])      # Attachment File
        self.assertEqual(got[0][13], 'SILK audio')
        check_in.assert_called_once_with(str(voice_a), 'msg_v1.amr')


class TestLoader(unittest.TestCase):
    def test_lexis_module_replaces_wechat_once(self):
        from scripts import plugin_loader  # pylint: disable=import-outside-toplevel
        with patch('builtins.print'):  # the loader prints one override line per key
            loader = plugin_loader.PluginLoader([plugin_loader.PLUGINPATH])
        specs = {spec.name: spec for spec in loader.plugins}
        for key, info in base.__artifacts_v2__.items():
            self.assertEqual(specs[key].module_name, 'LEXIS_WeChatMultiAccount', key)
            named = [s for s in loader.plugins if s.artifact_info.get('name') == info['name']]
            self.assertEqual(len(named), 1, info['name'])


if __name__ == '__main__':
    unittest.main()
