"""EncryptedSharedPreferences decrypted with a keystore export, built the way Tink and
AndroidX write them: AES-SIV names bound to the preferences name, AES-GCM values bound to
the encrypted name, each keyset stored in the file encrypted and exported decrypted."""
import base64
import json
import os
import pathlib
import struct
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch
import xml.etree.ElementTree as ET

from Crypto.Cipher import AES

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts import android_keystore as keystore  # pylint: disable=wrong-import-position
from scripts.artifacts import encryptedSharedPreferences as artifact  # pylint: disable=wrong-import-position


def field(number, value):
    def varint(n):
        out = b''
        while True:
            out += bytes([(n & 0x7F) | (0x80 if n > 0x7F else 0)])
            n >>= 7
            if not n:
                return out
    if isinstance(value, int):
        return varint(number << 3) + varint(value)
    return varint(number << 3 | 2) + varint(len(value)) + value


def keyset(type_name, key_field, key_id, key):
    key_value = field(1, 0) + field(key_field, key)
    key_data = (field(1, f'type.googleapis.com/google.crypto.tink.{type_name}'.encode())
                + field(2, key_value) + field(3, 1))
    return field(1, key_id) + field(2, field(1, key_data) + field(2, 1) + field(3, key_id) + field(4, 1))


def prefix(key_id):
    return b'\x01' + key_id.to_bytes(4, 'big')


class Store:
    """One EncryptedSharedPreferences file and the keystore entries that unlock it."""

    def __init__(self, seed):
        self.siv_key, self.gcm_key = bytes([seed]) * 64, bytes([seed + 1]) * 32
        self.siv_id, self.gcm_id = 1000 + seed, 2000 + seed
        self.siv_keyset = keyset('AesSivKey', 2, self.siv_id, self.siv_key)
        self.gcm_keyset = keyset('AesGcmKey', 3, self.gcm_id, self.gcm_key)
        # What the Keystore key made of each keyset; only its presence inside the file matters
        self.wrapped = (bytes([seed + 2]) * 157, bytes([seed + 3]) * 124)

    def name(self, text, file_stem):
        cipher = AES.new(self.siv_key, AES.MODE_SIV)
        cipher.update(file_stem.encode())
        ciphertext, tag = cipher.encrypt_and_digest(text.encode())
        return base64.b64encode(prefix(self.siv_id) + tag + ciphertext).decode()

    def value(self, plaintext, encrypted_name):
        nonce = os.urandom(12)
        cipher = AES.new(self.gcm_key, AES.MODE_GCM, nonce=nonce)
        cipher.update(encrypted_name.encode())
        ciphertext, tag = cipher.encrypt_and_digest(plaintext)
        return base64.b64encode(prefix(self.gcm_id) + nonce + ciphertext + tag).decode()

    def write(self, path, values):
        path.parent.mkdir(parents=True, exist_ok=True)
        root = ET.Element('map')
        ET.SubElement(root, 'string', name=keystore.KEY_KEYSET).text = \
            field(2, self.wrapped[0]).hex()
        ET.SubElement(root, 'string', name=keystore.VALUE_KEYSET).text = \
            field(2, self.wrapped[1]).hex()
        for text, plaintext in values:
            name = self.name(text, path.stem)
            ET.SubElement(root, 'string', name=name).text = self.value(plaintext, name)
        ET.ElementTree(root).write(path, encoding='utf-8')

    def entries(self):
        return [{'result_type': 'EncryptedSharedPreference Data Blob',
                 'key_name': '_androidx_security_master_key_', 'description': description,
                 'encrypted_data': wrapped.hex(), 'decrypted_data': plain.hex()}
                for description, wrapped, plain in (('key_keyset : x', self.wrapped[0], self.siv_keyset),
                                                     ('value_keyset : x', self.wrapped[1], self.gcm_keyset))]


def string(text):
    data = text.encode()
    return struct.pack('>ii', 0, len(data)) + data


VALUES = [
    ('token', string('abc è')),
    ('tags', struct.pack('>i', 1) + b''.join(struct.pack('>i', len(t)) + t for t in (b'a', b'bc'))),
    ('count', struct.pack('>ii', 2, -7)),
    ('stamp', struct.pack('>iq', 3, 1700000000000)),
    ('ratio', struct.pack('>if', 4, 0.5)),
    ('enabled', struct.pack('>i', 5) + b'\x01'),
    ('cleared', string('__NULL__')),
]


class Context:
    def __init__(self, root, files):
        self.root, self.files = root, [str(f) for f in files]

    def get_files_found(self):
        return self.files

    def get_relative_path(self, path):
        return str(pathlib.Path(path).relative_to(self.root)).replace('\\', '/')


class TestAndroidKeystore(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()  # pylint: disable=consider-using-with
        self.root = pathlib.Path(self.directory.name)
        self.addCleanup(self.directory.cleanup)
        self.addCleanup(keystore.set_path, None)

    def export(self, document, zipped=True):
        path = self.root / ('keystore.zip' if zipped else 'keystore.json')
        if zipped:
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('android_keystore', json.dumps(document))
        else:
            path.write_text(json.dumps(document))
        keystore.set_path(str(path))
        return path

    def test_every_value_type_decrypts(self):
        store = Store(10)
        prefs = self.root / 'data/data/com.example/shared_prefs/secure.xml'
        store.write(prefs, VALUES)
        self.export({'com.example': store.entries()})
        status, rows = keystore.read_encrypted_prefs(str(prefs))
        self.assertEqual(status, 'decrypted')
        self.assertEqual(sorted(rows), sorted([
            ('token', 'String', 'abc è'), ('tags', 'String set', ['a', 'bc']),
            ('count', 'Int', -7), ('stamp', 'Long', 1700000000000), ('ratio', 'Float', 0.5),
            ('enabled', 'Boolean', True), ('cleared', 'String', None)]))

    def test_a_tampered_entry_or_a_foreign_key_does_not_verify(self):
        store, other = Store(10), Store(40)
        prefs = self.root / 'data/data/com.example/shared_prefs/secure.xml'
        store.write(prefs, VALUES[:2])
        text = prefs.read_text()
        tree = ET.fromstring(text)
        last = tree[-1]
        raw = bytearray(base64.b64decode(last.text))
        raw[-1] ^= 1
        last.text = base64.b64encode(bytes(raw)).decode()
        prefs.write_bytes(ET.tostring(tree))
        self.export({'com.example': store.entries()}, zipped=False)
        with patch.object(keystore, 'logfunc') as log:
            status, rows = keystore.read_encrypted_prefs(str(prefs))
        self.assertEqual((status, [r[0] for r in rows]), ('decrypted', ['token']))
        self.assertIn('1 entries', log.call_args[0][0])
        # The other store's keys are not linked to this file, so nothing is tried with them
        self.export({'com.example': other.entries()})
        self.assertEqual(keystore.read_encrypted_prefs(str(prefs)), ('no key in the keystore', []))

    def test_artifact_keeps_users_apart_and_lists_what_it_cannot_open(self):
        user0, user10, decoy = Store(10), Store(70), Store(130)
        first = self.root / 'data/data/com.example/shared_prefs/secure.xml'
        same = self.root / 'data/user/0/com.example/shared_prefs/secure.xml'
        second = self.root / 'data/user/10/com.example/shared_prefs/secure.xml'
        stranger = self.root / 'data/data/com.decoy/shared_prefs/secure.xml'
        plain = self.root / 'data/data/com.example/shared_prefs/plain.xml'
        user0.write(first, VALUES[:1])
        same.parent.mkdir(parents=True)
        same.write_bytes(first.read_bytes())
        user10.write(second, [('token', string('other user'))])
        decoy.write(stranger, VALUES[:1])
        plain.write_text('<map><string name="a">b</string></map>')
        files = [first, same, second, stranger, plain]

        keystore.set_path(None)
        _, rows, _ = artifact.encrypted_shared_preferences.__wrapped__(Context(self.root, files))
        self.assertEqual([r[5] for r in rows], ['Encrypted, no keystore supplied'] * 3)

        self.export({'com.example': user0.entries() + user10.entries()})
        headers, rows, sources = artifact.encrypted_shared_preferences.__wrapped__(
            Context(self.root, files))
        self.assertEqual(headers[-1], 'Source File')
        self.assertEqual([(r[0], r[2], r[4], r[5], r[6]) for r in rows], [
            ('com.example', 'token', 'abc è', 'Decrypted', 'data/data/com.example/shared_prefs/secure.xml'),
            ('com.example', 'token', 'other user', 'Decrypted', 'data/user/10/com.example/shared_prefs/secure.xml'),
            ('com.decoy', '', '', 'No key in the keystore', 'data/data/com.decoy/shared_prefs/secure.xml')])
        self.assertEqual(sources.splitlines(), [str(first), str(second), str(stranger)])

    def test_unreadable_export_is_empty_not_fatal(self):
        path = self.root / 'broken.zip'
        path.write_bytes(b'PK\x03\x04 not really')
        keystore.set_path(str(path))
        with patch.object(keystore, 'logfunc'):
            self.assertEqual(keystore.entries(), [])


if __name__ == '__main__':
    unittest.main()
