__artifacts_v2__ = {
    "encrypted_shared_preferences": {
        "name": "Encrypted Shared Preferences",
        "description": "Lists the AndroidX EncryptedSharedPreferences files found for each app and, "
                       "when an Android keystore export is supplied, the entry names and "
                       "values decrypted with it.",
        "author": "@gk-forensics",
        "creation_date": "2026-10-15",
        "last_update_date": "2026-10-15",
        "requirements": "An Android keystore export (--keystore) to decrypt the entries",
        "category": "Android Keystore",
        "notes": "A file is taken for EncryptedSharedPreferences when it stores both AndroidX "
                 "keyset entries. Its two Tink keysets are encrypted by a Keystore key the "
                 "extraction does not carry in usable form; an extraction tool that reaches "
                 "the Keystore exports them decrypted, and the examiner supplies that export "
                 "with --keystore. Each keyset is matched to the file by the Keystore "
                 "ciphertext the export records, found inside the file's own keyset entry, "
                 "not by app or file name. Names are AES-SIV and values AES-GCM, both "
                 "authenticated, so a row is only written for an entry that verified; entries "
                 "that did not are counted in the log. Type is the type code the library "
                 "stores before each value. A file that could not be decrypted gets one row "
                 "with its Status and blank Name, Type and Value, so the encrypted stores on "
                 "the device stay visible without a keystore. The layout follows the AndroidX "
                 "security-crypto source and was checked by round-trip tests; the values mean "
                 "what each app writes into them, which this artifact does not interpret.",
        "paths": ('*/shared_prefs/*.xml',),
        "output_types": "standard",
        "artifact_icon": "lock"
    },
}

import os

from scripts import android_keystore
from scripts.artifacts.storagePathViews import unique_files
from scripts.ilapfuncs import artifact_processor


def _package(relative_path):
    '''The directory holding shared_prefs, which is the app's package on the data partition.'''
    parts = str(relative_path).replace('\\', '/').split('/')
    return parts[-3] if len(parts) >= 3 else ''


def _is_encrypted(path):
    try:
        with open(path, 'rb') as handle:
            return android_keystore.KEY_KEYSET.encode() in handle.read()
    except OSError:
        return False


def _shown(value):
    if isinstance(value, list):
        return '\n'.join(value)
    return '' if value is None else str(value)


@artifact_processor
def encrypted_shared_preferences(context):
    data_list = []
    source_files = []
    have_keystore = bool(android_keystore.entries())

    for path in unique_files(context):
        if not _is_encrypted(path):
            continue
        relative = context.get_relative_path(path)
        source_files.append(path)
        package, file_name = _package(relative), os.path.basename(path)
        if not have_keystore:
            status, rows = 'Encrypted, no keystore supplied', []
        else:
            status, rows = android_keystore.read_encrypted_prefs(path)
        if not rows:
            data_list.append((package, file_name, '', '', '', status.capitalize(), relative))
            continue
        for name, kind, value in rows:
            data_list.append((package, file_name, _shown(name), kind, _shown(value),
                              'Decrypted', relative))

    data_headers = ('Package', 'Preferences File', 'Name', 'Type', 'Value', 'Status',
                    'Source File')
    return data_headers, data_list, '\n'.join(source_files)
