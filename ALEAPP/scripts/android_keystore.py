"""Keys from an examiner-supplied Android keystore export.

Android apps keep their secrets wrapped by keys held in the Keystore, which a file system
extraction does not carry in usable form. Extraction tools that can reach the Keystore
export what they unwrapped as a separate file; the examiner supplies it with --keystore
(or the field in the GUI) and artifacts read it through here.

The layout read is a JSON object, alone or as the single member of a zip, as
acquisition tools write it: one key per package, each holding a list of entries with result_type,
key_name, description, and the encrypted_data and decrypted_data of the blob as hex.

What artifacts use today is the AndroidX EncryptedSharedPreferences layout. Such a
preferences file stores two Tink keysets, each encrypted by a Keystore key, and every
entry name and value encrypted with them: names by the AES-SIV keyset with the
preferences name as associated data, values by the AES-GCM keyset with the encrypted name
as associated data, and each value prefixed by a type code. Both modes are authenticated,
so a wrong key or a wrong guess at the associated data fails verification rather than
returning bytes.

The layout follows the AndroidX source (security-crypto, EncryptedSharedPreferences.java)
and Tink's wire format; it is implemented on PyCryptodome, already an ALEAPP requirement.
"""
import base64
import json
import os
import struct
import zipfile
import xml.etree.ElementTree as ET

from Crypto.Cipher import AES

from scripts.ilapfuncs import logfunc

KEY_KEYSET = '__androidx_security_crypto_encrypted_prefs_key_keyset__'
VALUE_KEYSET = '__androidx_security_crypto_encrypted_prefs_value_keyset__'
NULL_VALUE = '__NULL__'

# EncryptedSharedPreferences.EncryptedType
_TYPES = {0: 'String', 1: 'String set', 2: 'Int', 3: 'Long', 4: 'Float', 5: 'Boolean'}
# Tink OutputPrefixType: TINK prefixes 0x01 and the key id, LEGACY and CRUNCHY 0x00 and it
_PREFIX = {1: b'\x01', 2: b'\x00', 4: b'\x00'}

_path = None
_entries = None


def set_path(path):
    """Records the keystore export the examiner supplied, or None."""
    global _path, _entries  # pylint: disable=global-statement
    _path, _entries = path, None


def get_path():
    """The examiner-supplied keystore path, or None when none was given."""
    return _path


def entries():
    """The export's entries as dicts with package, result_type, key_name, description,
    encrypted and decrypted (bytes). Empty when none was supplied or it cannot be read."""
    global _entries  # pylint: disable=global-statement
    if _entries is None:
        _entries = _load(_path) if _path else []
    return _entries


def _load(path):
    try:
        if zipfile.is_zipfile(path):
            with zipfile.ZipFile(path) as archive:
                documents = [archive.read(name) for name in archive.namelist()
                             if not name.endswith('/')]
        else:
            with open(path, 'rb') as handle:
                documents = [handle.read()]
    except OSError as ex:
        logfunc(f'Keystore: could not read {path}: {ex}')
        return []
    found = []
    for document in documents:
        try:
            parsed = json.loads(document.decode('utf-8-sig'))
        except (UnicodeDecodeError, ValueError):
            continue
        if not isinstance(parsed, dict):
            continue
        for package, items in parsed.items():
            for item in items if isinstance(items, list) else []:
                if not isinstance(item, dict):
                    continue
                try:
                    encrypted = bytes.fromhex(item.get('encrypted_data') or '')
                    decrypted = bytes.fromhex(item.get('decrypted_data') or '')
                except ValueError:
                    encrypted = decrypted = b''
                found.append({'package': package,
                              'result_type': str(item.get('result_type') or ''),
                              'key_name': str(item.get('key_name') or ''),
                              'description': str(item.get('description') or ''),
                              'encrypted': encrypted, 'decrypted': decrypted})
    if not found:
        logfunc(f'Keystore: {path} holds no entries in a supported layout')
    return found


def report_supplied_keystore():
    """Logs what the supplied keystore holds, once, at the start of the run."""
    if not _path:
        return
    found = entries()
    if found:
        usable = sum(1 for entry in found if entry['decrypted'])
        packages = len({entry['package'] for entry in found})
        logfunc(f'Keystore: {len(found)} entries for {packages} packages read from {_path}, '
                f'{usable} with a decrypted key')


# Tink keysets ---------------------------------------------------------------------------

def _varint(buf, i):
    value = shift = 0
    while True:
        byte = buf[i]
        i += 1
        value |= (byte & 0x7F) << shift
        if byte < 0x80:
            return value, i
        shift += 7


def _fields(buf):
    """(number, value) for each field of a protobuf message; value is int or bytes."""
    i = 0
    while i < len(buf):
        tag, i = _varint(buf, i)
        wire = tag & 7
        if wire == 0:
            value, i = _varint(buf, i)
        elif wire == 2:
            length, i = _varint(buf, i)
            value, i = buf[i:i + length], i + length
        elif wire == 5:
            value, i = buf[i:i + 4], i + 4
        elif wire == 1:
            value, i = buf[i:i + 8], i + 8
        else:
            raise ValueError(f'unsupported wire type {wire}')
        yield tag >> 3, value


def parse_keyset(data):
    """[(type_url, key_id, prefix, key bytes)] for the enabled keys of a plaintext Tink Keyset."""
    keys = []
    for number, key in _fields(data):
        if number != 2:
            continue
        fields = dict(_fields(key))
        if fields.get(2, 1) != 1:  # KeyStatusType ENABLED
            continue
        key_data = dict(_fields(fields.get(1, b'')))
        type_url = key_data.get(1, b'').decode('utf-8', 'replace')
        material = [value for _, value in _fields(key_data.get(2, b''))
                    if isinstance(value, bytes) and len(value) in (16, 32, 64)]
        if not material:
            continue
        key_id = fields.get(3, 0)
        prefix = _PREFIX.get(fields.get(4), b'')
        keys.append((type_url, key_id, prefix + key_id.to_bytes(4, 'big') if prefix else b'',
                     material[0]))
    return keys


def decrypt(keys, ciphertext, associated_data):
    """Plaintext of a Tink AES-SIV or AES-GCM ciphertext, or None when no key verifies."""
    for type_url, _, prefix, key in keys:
        if not ciphertext.startswith(prefix):
            continue
        body = ciphertext[len(prefix):]
        try:
            if type_url.endswith('AesSivKey'):
                cipher = AES.new(key, AES.MODE_SIV)
                cipher.update(associated_data)
                return cipher.decrypt_and_verify(body[16:], body[:16])
            if type_url.endswith('AesGcmKey') and len(body) >= 28:
                cipher = AES.new(key, AES.MODE_GCM, nonce=body[:12])
                cipher.update(associated_data)
                return cipher.decrypt_and_verify(body[12:-16], body[-16:])
        except ValueError:
            continue
    return None


# EncryptedSharedPreferences -------------------------------------------------------------

def _keyset_for(encrypted_keyset_hex):
    """The decrypted keyset whose Keystore ciphertext is inside the file's keyset entry.

    The export records the ciphertext it unwrapped, so the file is linked to its key by
    content rather than by name, and a second Android user's copy of the same app, or a
    second file of one app, gets its own key.
    """
    try:
        stored = bytes.fromhex(encrypted_keyset_hex)
    except ValueError:
        return None
    for entry in entries():
        if entry['decrypted'] and entry['encrypted'] and entry['encrypted'] in stored:
            return entry['decrypted']
    return None


def _value(plaintext):
    """(type, value) of a decrypted EncryptedSharedPreferences value."""
    kind = struct.unpack('>i', plaintext[:4])[0]
    body = plaintext[4:]
    if kind == 0:
        length = struct.unpack('>i', body[:4])[0]
        text = body[4:4 + length].decode('utf-8', 'replace')
        return _TYPES[0], None if text == NULL_VALUE else text
    if kind == 1:
        items, i = [], 0
        while i + 4 <= len(body):
            length = struct.unpack('>i', body[i:i + 4])[0]
            items.append(body[i + 4:i + 4 + length].decode('utf-8', 'replace'))
            i += 4 + length
        return _TYPES[1], items
    if kind == 2:
        return _TYPES[2], struct.unpack('>i', body[:4])[0]
    if kind == 3:
        return _TYPES[3], struct.unpack('>q', body[:8])[0]
    if kind == 4:
        return _TYPES[4], struct.unpack('>f', body[:4])[0]
    if kind == 5:
        return _TYPES[5], body[:1] == b'\x01'
    return f'Unknown ({kind})', body.hex()


def read_encrypted_prefs(path):
    """Decrypts an EncryptedSharedPreferences file with the supplied keystore.

    Returns (status, rows): rows are (name, type, value) for each entry that verified, and
    status says why none did when the file could not be read with the keystore. The file
    is parsed in memory and never written.
    """
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as ex:
        return f'not parsed: {ex}', []
    stored = {element.get('name'): element.text or '' for element in root
              if element.tag == 'string' and element.get('name') is not None}
    if KEY_KEYSET not in stored or VALUE_KEYSET not in stored:
        return 'not encrypted preferences', []
    name_keyset = _keyset_for(stored[KEY_KEYSET])
    value_keyset = _keyset_for(stored[VALUE_KEYSET])
    if not name_keyset or not value_keyset:
        return 'no key in the keystore', []
    name_keys, value_keys = parse_keyset(name_keyset), parse_keyset(value_keyset)

    # The names are bound to the preferences name the app passed in, which is the file
    # name without .xml; the full name is tried as well in case an app passed it whole
    file_name = os.path.basename(path)
    stems = [file_name[:-4], file_name] if file_name.endswith('.xml') else [file_name]
    rows, failures = [], 0
    for encrypted_name, encrypted_value in stored.items():
        if encrypted_name in (KEY_KEYSET, VALUE_KEYSET):
            continue
        try:
            name_bytes = base64.b64decode(encrypted_name)
            value_bytes = base64.b64decode(encrypted_value)
        except ValueError:
            failures += 1
            continue
        name = None
        for stem in stems:
            name = decrypt(name_keys, name_bytes, stem.encode('utf-8'))
            if name is not None:
                break
        value = decrypt(value_keys, value_bytes, encrypted_name.encode('utf-8'))
        if name is None or value is None or len(value) < 4:
            failures += 1
            continue
        text = name.decode('utf-8', 'replace')
        try:
            kind, decoded = _value(value)
        except (struct.error, KeyError):
            kind, decoded = 'Undecoded', value.hex()
        rows.append((None if text == NULL_VALUE else text, kind, decoded))
    if failures:
        logfunc(f'Keystore: {failures} entries of {file_name} did not verify with the key')
    return ('decrypted' if rows else 'no entry verified'), rows
