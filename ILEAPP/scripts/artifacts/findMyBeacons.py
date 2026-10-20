__artifacts_v2__ = {
    "findMyOwnedBeacons": {
        "name": "Find My - Owned Beacons",
        "description": "Beacon records (AirTag and other Find My accessories) stored by searchpartyd in "
                       "OwnedBeacons, decrypted with the BeaconStore key from a keychain",
        "author": "@gk-forensics",
        "creation_date": "2026-10-12",
        "last_update_date": "2026-10-12",
        "requirements": "pycryptodome",
        "category": "Find My",
        "notes": "Each file in com.apple.icloud.searchpartyd/OwnedBeacons holds a plist array of nonce, "
                 "tag and ciphertext, decrypted with AES-GCM using the keychain item whose service or "
                 "label is BeaconStore; the plaintext is a plist. The key is taken from the keychain "
                 "the Keychain artifacts read or, failing that, from the keychain supplied with "
                 "--keychain. Without a keychain no row is produced, which does not mean no beacon "
                 "was paired. Identifier, Model, Pairing Date, Product ID, Vendor ID, System Version "
                 "and Stable Identifier are read from keys of those names when present; Keys in "
                 "Record lists every top-level key so a field this artifact does not name is still "
                 "visible, and key material (private keys, shared secrets) is never printed. Name and "
                 "Emoji come from the BeaconNamingRecord whose associatedBeacon equals the "
                 "Identifier. The record layout follows public research and a round-trip test; it "
                 "has not been checked against an extraction holding OwnedBeacons records. "
                 "fmipcore Devices.data and ItemGroups.data are not read, for want of a sample.",
        "paths": ('*/com.apple.icloud.searchpartyd/OwnedBeacons/*',
                  '*/com.apple.icloud.searchpartyd/BeaconNamingRecord/*',
                  '*/keychain-backup.plist',
                  '*/extra/KeychainDump/backup_keychain_v2.plist',
                  '*/Keychains/keychain-2.db*'),
        "output_types": "standard",
        "artifact_icon": "tag"
    },
    "findMyBeaconNames": {
        "name": "Find My - Beacon Names",
        "description": "Names and emoji given to Find My beacons, stored by searchpartyd in "
                       "BeaconNamingRecord, decrypted with the BeaconStore key from a keychain",
        "author": "@gk-forensics",
        "creation_date": "2026-10-12",
        "last_update_date": "2026-10-12",
        "requirements": "pycryptodome",
        "category": "Find My",
        "notes": "Each file under com.apple.icloud.searchpartyd/BeaconNamingRecord holds a plist "
                 "array of nonce, tag and ciphertext, decrypted as for Find My - Owned Beacons. Name, "
                 "Emoji, Role ID, Associated Beacon and Identifier are read from keys of those names "
                 "when present; Role ID is reported as stored. Without a keychain no row is produced. "
                 "The record layout follows public research and a round-trip test; it has not been "
                 "checked against an extraction holding BeaconNamingRecord files.",
        "paths": ('*/com.apple.icloud.searchpartyd/BeaconNamingRecord/*',
                  '*/keychain-backup.plist',
                  '*/extra/KeychainDump/backup_keychain_v2.plist',
                  '*/Keychains/keychain-2.db*'),
        "output_types": "standard",
        "artifact_icon": "tag"
    },
}

import os
import plistlib
from datetime import datetime, timezone

from Crypto.Cipher import AES

from scripts.artifacts.keychain import parse_keychain
from scripts.ilapfuncs import artifact_processor, logfunc
from scripts.ios_keychain import active_keychain_path, load_keychain_entries

KEY_NAME = 'BeaconStore'


def _is_beacon_store(entry):
    for field in ('svce', 'labl'):
        value = entry.get(field)
        if isinstance(value, bytes):
            value = value.decode('utf-8', 'ignore').rstrip('\x00')
        if value == KEY_NAME:
            return True
    return False


def beacon_store_key(context):
    """The BeaconStore key: from the keychain the Keychain artifacts read, else from --keychain."""
    _, genp, _, _ = parse_keychain(context)
    for entry in genp:
        if _is_beacon_store(entry) and isinstance(entry.get('v_Data'), bytes):
            return entry['v_Data']
    path = active_keychain_path()
    if path:
        for entry in load_keychain_entries(path):
            if _is_beacon_store(entry) and isinstance(entry.get('v_Data'), (bytes, bytearray)):
                return bytes(entry['v_Data'])
    return None


def decrypt_record(path, key, shown):
    """A searchpartyd record, a plist [nonce, tag, ciphertext], decrypted to its plist; None if it is not one."""
    try:
        with open(path, 'rb') as f:
            nonce, tag, ciphertext = plistlib.load(f)
        return plistlib.loads(AES.new(key, AES.MODE_GCM, nonce=nonce).decrypt_and_verify(ciphertext, tag))
    except (OSError, ValueError, TypeError, plistlib.InvalidFileException) as ex:
        logfunc(f'Find My: {shown} not decrypted: {ex}')
        return None


def _records(context, folder):
    return [str(f) for f in context.get_files_found()
            if f'/{folder}/' in str(f).replace('\\', '/') and os.path.isfile(f)]


def _plain(value):
    """A printable scalar; bytes and containers are not printed."""
    if isinstance(value, datetime):
        return value if value.tzinfo else value.replace(tzinfo=timezone.utc)
    if isinstance(value, (str, int, float)):
        return value
    if isinstance(value, list) and all(isinstance(v, (str, int)) for v in value):
        return ', '.join(map(str, value))
    return ''


def decrypted_records(context, folder, label):
    records = _records(context, folder)
    if not records:
        return records, []
    key = beacon_store_key(context)
    if key is None:
        logfunc(f'Find My {label}: no {KEY_NAME} key in a keychain, records not decrypted')
        return records, []
    out = []
    for record in records:
        p = decrypt_record(record, key, context.get_relative_path(record))
        if isinstance(p, dict):
            out.append((record, p))
    return records, out


@artifact_processor
def findMyOwnedBeacons(context):
    data_headers = (('Pairing Date', 'datetime'), 'Name', 'Emoji', 'Identifier', 'Model', 'Product ID',
                    'Vendor ID', 'System Version', 'Stable Identifier', 'Keys in Record', 'Source File')
    records, beacons = decrypted_records(context, 'OwnedBeacons', 'owned beacons')
    if not beacons:
        return data_headers, [], '\n'.join(records)
    _, names = decrypted_records(context, 'BeaconNamingRecord', 'beacon names')
    naming = {str(p.get('associatedBeacon')): p for _, p in names if p.get('associatedBeacon')}
    data_list = []
    for record, p in beacons:
        n = naming.get(str(p.get('identifier')), {})
        data_list.append((_plain(p.get('pairingDate')), _plain(n.get('name')), _plain(n.get('emoji')),
                          _plain(p.get('identifier')), _plain(p.get('model')), _plain(p.get('productId')),
                          _plain(p.get('vendorId')), _plain(p.get('systemVersion')),
                          _plain(p.get('stableIdentifier')), ', '.join(sorted(map(str, p))),
                          context.get_relative_path(record)))
    return data_headers, data_list, '\n'.join(records)


@artifact_processor
def findMyBeaconNames(context):
    data_headers = ('Name', 'Emoji', 'Role ID (as stored)', 'Associated Beacon', 'Identifier', 'Source File')
    records, names = decrypted_records(context, 'BeaconNamingRecord', 'beacon names')
    data_list = [(_plain(p.get('name')), _plain(p.get('emoji')), _plain(p.get('roleId')),
                  _plain(p.get('associatedBeacon')), _plain(p.get('identifier')),
                  context.get_relative_path(record)) for record, p in names]
    return data_headers, data_list, '\n'.join(records)
