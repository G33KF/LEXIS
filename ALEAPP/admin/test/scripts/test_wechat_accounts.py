"""Every WeChat account database is keyed with the uin that reproduces its directory name,
taken from the preferences of the same app data directory, and the IMEI stored as hex is
tried as its text."""
import hashlib
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.artifacts import weChat  # pylint: disable=wrong-import-position


def account_dir(uin):
    return hashlib.md5(f'mm{uin}'.encode()).hexdigest()


def make_db(path, imei, uin):
    path.parent.mkdir(parents=True, exist_ok=True)
    connection = weChat.sqlcipher.connect(str(path))  # pylint: disable=no-member
    connection.execute(f"PRAGMA key = '{weChat._db_key(imei, uin)}';")  # pylint: disable=protected-access
    for pragma in weChat._CIPHER_PRAGMAS:  # pylint: disable=protected-access
        connection.execute(pragma)
    connection.execute('CREATE TABLE marker (uin TEXT)')
    connection.execute('INSERT INTO marker VALUES (?)', (uin,))
    connection.commit()
    connection.close()


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8')


class Context:
    def __init__(self, root, files):
        self.root, self.files = root, [str(f) for f in files]

    def get_files_found(self):
        return self.files

    def get_relative_path(self, path):
        return str(pathlib.Path(path).relative_to(self.root)).replace('\\', '/')


@unittest.skipUnless(weChat._SQLCIPHER_AVAILABLE, 'sqlcipher3 not installed')  # pylint: disable=protected-access
class TestWeChatAccounts(unittest.TestCase):
    def test_each_account_opens_with_its_own_uin_and_imei(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            home = root / 'data/data/com.tencent.mm'
            other = root / 'data/user/10/com.tencent.mm'
            files = [
                home / 'shared_prefs/auth_info_key_prefs.xml',
                home / 'shared_prefs/app_brand_global_sp.xml',
                home / 'shared_prefs/WLOGIN_DEVICE_INFO.xml',
                other / 'shared_prefs/auth_info_key_prefs.xml',
                home / f'MicroMsg/{account_dir(-111)}/EnMicroMsg.db',   # current account, hex IMEI
                home / f'MicroMsg/{account_dir(222)}/EnMicroMsg.db',    # earlier account, fallback
                home / f'MicroMsg/{account_dir(333)}/EnMicroMsg.db',    # uin stored nowhere
                other / f'MicroMsg/{account_dir(-111)}/EnMicroMsg.db',  # same uin, other user: not this one
                other / f'MicroMsg/{account_dir(444)}/EnMicroMsg.db',   # second Android user
            ]
            write(files[0], '<map><int name="_auth_uin" value="-111" /></map>')
            write(files[1], '<map><set name="uin_set"><string>222</string><string>-111</string></set>'
                            '<string name="last_count">333</string></map>')
            write(files[2], f'<map><string name="imei">{"A0123456789abcde".encode().hex()}</string></map>')
            write(files[3], '<map><string name="_auth_uin">444</string></map>')
            make_db(files[4], 'A0123456789abcde', '-111')
            make_db(files[5], weChat._FALLBACK_IMEI, '222')  # pylint: disable=protected-access
            make_db(files[6], weChat._FALLBACK_IMEI, '333')  # pylint: disable=protected-access
            make_db(files[7], weChat._FALLBACK_IMEI, '-111')  # pylint: disable=protected-access
            make_db(files[8], weChat._FALLBACK_IMEI, '444')  # pylint: disable=protected-access

            weChat._logged.clear()  # pylint: disable=protected-access
            with patch.object(weChat, 'logfunc') as log:
                opened = {}
                for _ in range(2):  # a second artifact logs nothing new
                    for connection, uin, db_path in weChat._connections(Context(root, files)):  # pylint: disable=protected-access
                        stored = connection.execute('SELECT uin FROM marker').fetchone()[0]
                        connection.close()
                        opened[db_path] = (uin, stored)
            self.assertEqual(opened, {str(files[4]): ('-111', '-111'), str(files[5]): ('222', '222'),
                                      str(files[8]): ('444', '444')})
            messages = [call.args[0] for call in log.call_args_list]
            self.assertEqual(len(messages), 2, messages)
            self.assertIn(str(files[6]), messages[0])  # 333 is stored, but not under a uin name
            self.assertIn(str(files[7]), messages[1])  # user 10 has no IMEI that opens a -111 database

    def test_imei_candidates(self):
        self.assertEqual(weChat._imei_candidates('41424344'),  # pylint: disable=protected-access
                         ['41424344', 'ABCD', weChat._FALLBACK_IMEI])  # pylint: disable=protected-access
        self.assertEqual(weChat._imei_candidates('358240051111110'),  # pylint: disable=protected-access
                         ['358240051111110', weChat._FALLBACK_IMEI])  # pylint: disable=protected-access
        self.assertEqual(weChat._imei_candidates(''), [weChat._FALLBACK_IMEI])  # pylint: disable=protected-access


if __name__ == '__main__':
    unittest.main()
