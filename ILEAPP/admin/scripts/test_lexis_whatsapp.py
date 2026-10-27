"""LEXIS_WhatsApp (iLEAPP) runs every WhatsApp artifact once per app group container, so WhatsApp
Business is read beside WhatsApp, and leaves out a container of another app."""
import pathlib
import plistlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2]))
from scripts.lexis_artifacts import LEXIS_WhatsApp as lexis  # pylint: disable=wrong-import-position


def container(root, uuid, identifier, name, own=None):
    folder = root / 'private/var/mobile/Containers/Shared/AppGroup' / uuid
    folder.mkdir(parents=True)
    files = [folder / 'ContactsV2.sqlite', folder / lexis._METADATA]  # pylint: disable=protected-access
    connection = sqlite3.connect(files[0])
    connection.execute('''CREATE TABLE ZWAADDRESSBOOKCONTACT (ZFULLNAME TEXT, ZABOUTTEXT TEXT,
        ZABOUTTIMESTAMP REAL, ZPHONENUMBER TEXT, ZPHONENUMBERLABEL TEXT, ZWHATSAPPID TEXT, ZIDENTIFIER TEXT)''')
    connection.execute('INSERT INTO ZWAADDRESSBOOKCONTACT VALUES (?, NULL, NULL, NULL, NULL, NULL, NULL)', (name,))
    connection.commit()
    connection.close()
    files[1].write_bytes(plistlib.dumps({'MCMMetadataIdentifier': identifier}))
    if own:
        prefs = folder / f'Library/Preferences/{identifier}.plist'
        prefs.parent.mkdir(parents=True)
        prefs.write_bytes(plistlib.dumps({'OwnJabberID': own}))
        files.append(prefs)
    return files


class Context:
    def __init__(self, files):
        self.files = [str(f) for f in files]

    def get_files_found(self):
        return self.files


class TestContainers(unittest.TestCase):
    def test_both_apps_read_other_app_left_out(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as directory:  # the base keeps the db open
            root = pathlib.Path(directory)
            files = (container(root, 'A', 'group.net.whatsapp.WhatsApp.shared', 'alice', '111@s.whatsapp.net')
                     + container(root, 'B', 'group.net.whatsapp.WhatsAppSMB.shared', 'bob')
                     + container(root, 'C', 'group.com.example.other', 'decoy'))
            with patch.object(lexis.wa, 'logfunc', create=True):
                headers, rows, source = lexis.whatsAppContacts.__wrapped__(Context(files))
            self.assertEqual(headers[-2:], ('Account ID', 'App'))
            self.assertEqual(sorted((r[0], r[-2], r[-1]) for r in rows),
                             [('alice', '111@s.whatsapp.net', 'WhatsApp'),
                              ('bob', 'group.net.whatsapp.WhatsAppSMB.shared', 'WhatsApp Business')])
            self.assertEqual(len(source.split('\n')), 2)
            self.assertNotIn('/C/', source.replace('\\', '/'))

    def test_every_key_replaced_and_loaded(self):
        from scripts import plugin_loader  # pylint: disable=import-outside-toplevel
        with patch('builtins.print'):
            loader = plugin_loader.PluginLoader([plugin_loader.PLUGINPATH])
        specs = {spec.name: spec for spec in loader.plugins}
        keys = set(lexis.wa.__artifacts_v2__) | set(lexis.wax.__artifacts_v2__)
        self.assertEqual(keys, set(lexis.__artifacts_v2__))
        for key in keys:
            self.assertEqual(specs[key].module_name, 'LEXIS_WhatsApp', key)


if __name__ == '__main__':
    unittest.main()
