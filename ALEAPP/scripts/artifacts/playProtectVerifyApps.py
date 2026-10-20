__artifacts_v2__ = {
    "playProtectApks": {
        "name": "Play Protect - APK Records",
        "description": "APK records kept by the Play Store app verifier in verify_apps.db",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Installed Apps",
        "notes": "One row per apk_info record, joined on the APK SHA-256 to package_installation_state "
                 "and package_verdict_cache. Every value is a protobuf field read from the data blob; "
                 "field positions are not documented and were assigned on a private Android 15 "
                 "extraction: apk_info 1 = SHA-256 (32 bytes; equal to the SHA-256 of the "
                 "installed base.apk on the APKs checked), 2 = "
                 "package, 3 = version code "
                 "(equal to the versionCode in Graphics Stats on the packages checked), "
                 "4 = app label; package_installation_state 1 = package, 2 = Unix ms, 3 = SHA-256; "
                 "package_verdict_cache 1 = SHA-256, 2 = Unix ms, 3, 8 and 18 = integers shown as "
                 "stored, without a verdict meaning. What the two times mark is not established, so "
                 "they are named after their table.",
        "paths": ('*/com.android.vending/databases/verify_apps.db*',),
        "output_types": "standard",
        "artifact_icon": "shield",
    },
    "playProtectInstallAttempts": {
        "name": "Play Protect - Installation Attempts",
        "description": "Rows of the installation_attempts table of the Play Store app verifier in verify_apps.db",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Installed Apps",
        "notes": "One row per installation_attempts record. Time is protobuf field 2 read as Unix ms; "
                 "fields 3, 4, 7 and 8 are integers shown as stored, without an outcome meaning. "
                 "Package and App Label come from the apk_info record with the same SHA-256, when "
                 "there is one. A row records that the verifier logged an attempt for that APK; it "
                 "does not by itself show that the app was installed or kept. Validated on a private Android 15 extraction.",
        "paths": ('*/com.android.vending/databases/verify_apps.db*',),
        "output_types": "standard",
        "artifact_icon": "download",
    },
}

import os
import sqlite3
import struct
from datetime import datetime, timezone

from google.protobuf.message import DecodeError

from scripts.ilapfuncs import logfunc, open_sqlite_db_readonly, artifact_processor, decode_protobuf
from scripts.artifacts.storagePathViews import unique_files


def _text(value):
    return value.decode('utf-8', 'replace') if isinstance(value, bytes) else ('' if value is None else str(value))


def _hex(value):
    return value.hex() if isinstance(value, bytes) else _text(value)


def _ms(value):
    try:
        return datetime.fromtimestamp(value / 1000, timezone.utc) if isinstance(value, int) and value > 0 else ''
    except (ValueError, OverflowError, OSError):
        return ''


# Hashes, packages and labels forced to bytes, so none is ever guessed to be a nested message.
_B = {'type': 'bytes'}
_TYPEDEFS = {
    'apk_info': {'1': _B, '2': _B, '4': _B},
    'package_installation_state': {'1': _B, '3': _B},
    'package_verdict_cache': {'1': _B},
    'installation_attempts': {'1': _B},
}


def _tables(context):
    """(path, {table: [decoded protobuf dicts]}) per verify_apps.db; undecodable blobs are logged and skipped."""
    for path in map(str, unique_files(context)):
        if os.path.basename(path) != 'verify_apps.db':
            continue
        db = open_sqlite_db_readonly(path)
        if db is None:
            continue
        tables = {}
        try:
            for table, typedef in _TYPEDEFS.items():
                tables[table] = []
                for (blob,) in db.execute(f'select data from {table}'):
                    try:
                        tables[table].append(decode_protobuf(bytes(blob), typedef)[0])
                    except (ValueError, TypeError, KeyError, IndexError, struct.error, DecodeError) as ex:
                        logfunc(f'Undecodable {table} row in {path}: {ex}')
        except sqlite3.Error as ex:
            logfunc(f'Unable to read {path}: {ex}')
        finally:
            db.close()
        yield path, tables


@artifact_processor
def playProtectApks(context):
    data_list, sources = [], []
    for path, t in _tables(context):
        sources.append(path)
        state = {m.get('3'): m for m in t.get('package_installation_state', [])}
        verdict = {m.get('1'): m for m in t.get('package_verdict_cache', [])}
        for m in t.get('apk_info', []):
            sha = m.get('1')
            s, v = state.get(sha, {}), verdict.get(sha, {})
            label = m.get('4')
            data_list.append((_ms(s.get('2')), _text(m.get('2')), _text(label) if isinstance(label, bytes) else '',
                              _text(m.get('3')), _hex(sha), _ms(v.get('2')), _text(v.get('3')), _text(v.get('8')),
                              _text(v.get('18')), path))
    data_headers = (('package_installation_state Time', 'datetime'), 'Package', 'App Label', 'Version Code',
                    'APK SHA-256', ('package_verdict_cache Time', 'datetime'), 'Verdict Field 3 (as stored)',
                    'Verdict Field 8 (as stored)', 'Verdict Field 18 (as stored)', 'Source File')
    return data_headers, data_list, '\n'.join(sources)


@artifact_processor
def playProtectInstallAttempts(context):
    data_list, sources = [], []
    for path, t in _tables(context):
        sources.append(path)
        apks = {m.get('1'): m for m in t.get('apk_info', [])}
        for m in t.get('installation_attempts', []):
            apk = apks.get(m.get('1'), {})
            label = apk.get('4')
            data_list.append((_ms(m.get('2')), _text(apk.get('2')), _text(label) if isinstance(label, bytes) else '',
                              _hex(m.get('1')), _text(m.get('3')), _text(m.get('4')), _text(m.get('7')),
                              _text(m.get('8')), path))
    data_headers = (('Time', 'datetime'), 'Package', 'App Label', 'APK SHA-256', 'Field 3 (as stored)',
                    'Field 4 (as stored)', 'Field 7 (as stored)', 'Field 8 (as stored)', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
