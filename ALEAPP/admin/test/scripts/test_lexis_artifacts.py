"""Persistent properties protobuf, WhatsApp Business registration prefs and Chrome Preferences accounts."""
import datetime
import json
import pathlib
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.artifacts import WhatsApp as whatsApp, chromePreferencesAccounts, persistentPropProto


def context(root, paths):
    return SimpleNamespace(get_files_found=lambda: paths,
                           get_relative_path=lambda p: pathlib.Path(p).relative_to(root).as_posix())


def write(root, relative, content):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content if isinstance(content, bytes) else content.encode('utf-8'))
    return str(path)


def field(number, value):
    return bytes([number * 8 + 2, len(value)]) + value


def record(name, value):
    entry = field(1, name) + field(2, value)
    return field(1, entry)


class LexisArtifactsTest(unittest.TestCase):
    def test_persistent_properties_protobuf_and_boot_history(self):
        with tempfile.TemporaryDirectory() as folder, patch.object(persistentPropProto, 'logfunc') as logger:
            root = pathlib.Path(folder)
            proto = write(root, 'a/property/persistent_properties',
                          record(b'persist.sys.timezone', b'Europe/Rome')
                          + record(b'persist.sys.boot.reason.history', b'reboot,shell,1700000000\nreboot,bad\n'))
            text = write(root, 'b/property/persistent_properties', 'reboot,1700000000\n')
            ctx = context(root, [proto, text])
            _, rows, _ = persistentPropProto.persistentPropertiesAll.__wrapped__(ctx)
            self.assertEqual([r[:2] for r in rows][0], ('persist.sys.timezone', 'Europe/Rome'))
            self.assertEqual(len(rows), 2)
            _, rows, source = persistentPropProto.persistentBootReasonHistory.__wrapped__(ctx)
            self.assertEqual(rows[0][:3], (datetime.datetime.fromtimestamp(1700000000, datetime.timezone.utc),
                                           'reboot,shell', '1700000000'))
            self.assertEqual(rows[1][:3], ('', 'reboot', 'bad'))
            self.assertEqual(source, proto)
            self.assertEqual(logger.call_count, 2)  # the text file, once per artifact

    def test_whatsapp_business_registration_prefs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            prefs = 'data/data/com.whatsapp.w4b/shared_prefs/'
            reg = 'com.whatsapp.registration.RegisterPhone.'
            paths = [
                write(root, prefs + 'register_phone_prefs.xml',
                      f'<map><string name="{reg}country_code">39</string>'
                      f'<string name="{reg}phone_number">3330000000</string>'
                      f'<int name="{reg}verification_state" value="2" /></map>'),
                write(root, prefs + 'startup_prefs.xml', '<map><string name="version">2.25.1.1</string></map>'),
            ]
            headers, rows, _ = whatsApp.get_whatsapp_user_profile.__wrapped__(context(root, paths))
            self.assertEqual(len(rows), 1)
            row = dict(zip(headers, rows[0]))
            self.assertEqual(row['Package'], 'com.whatsapp.w4b')
            self.assertEqual(row['Version'], '2.25.1.1')
            self.assertEqual((row['Registration Screen Country Code'], row['Registration Screen Phone Number'],
                              row['Registration Screen Verification State (as stored)']), ('39', '3330000000', '2'))
            self.assertEqual(row['Mobile Number'], '')

    def test_chrome_preferences_accounts(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            prefs = {'account_info': [{'email': 'a@example.com', 'gaia': '1', 'full_name': 'A B'}, 'junk'],
                     'google': {'services': {'last_signed_in_username': 'a@example.com'}}}
            path = write(root, 'data/data/com.android.chrome/app_chrome/Default/Preferences', json.dumps(prefs))
            bad = write(root, 'data/data/x/app_webview/Default/Preferences', '{"no_accounts": 1}')
            headers, rows, source = chromePreferencesAccounts.get_chromePreferencesAccounts.__wrapped__(
                context(root, [path, bad]))
            self.assertEqual(len(rows), 1)
            row = dict(zip(headers, rows[0]))
            self.assertEqual((row['Email'], row['Gaia ID'], row['Last Signed-In Username (as stored)']),
                             ('a@example.com', '1', 'a@example.com'))
            self.assertEqual(source, path)


if __name__ == '__main__':
    unittest.main()
