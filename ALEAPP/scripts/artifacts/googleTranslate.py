__artifacts_v2__ = {
    "google_translate_history": {
        "name": "Google Translate - History",
        "description": "Translation history entries the Google Translate app keeps in its "
                       "portable_geller database, with the text, its translation and both "
                       "language codes",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Google Translate",
        "notes": "portable_geller_<account>.db holds rows keyed by data_type in "
                 "geller_key_table, each pointing at a protobuf blob in geller_data_table. Only "
                 "the TRANSLATE_HISTORY_ENTRIES rows are reported; the GELLER_CONFIG and "
                 "PRIVACY_SETTINGS rows held settings and are left out. The blob is decoded "
                 "without a schema and its fields are named from the data examined: in field 133.1, "
                 "fields 4 and 5 held language codes, and field 2 is read as the text in the "
                 "field 4 language and field 3 as the text in the field 5 language because the "
                 "script of each text matched the language code read with it. Which of the two was the "
                 "source text is not established. Entry Timestamp is the key row's "
                 "timestamp_micro, microseconds since the Unix epoch; whether it marks when the "
                 "translation was made or when the entry synced is not established. Account is "
                 "taken from the database file name. Delete Status (as stored) is the "
                 "delete_status column; its values are not interpreted. Whether the "
                 "database also holds entries synced from other devices signed in to the same "
                 "account is not established. The historydb_sql and favoritedb_sql databases in "
                 "the same folder are not read. Validated on a private Android extraction.",
        "paths": ('*/com.google.android.apps.translate/databases/portable_geller_*.db*',),
        "output_types": "standard",
        "artifact_icon": "globe",
    },
}

import os

from scripts import blackboxprotobuf
from scripts.ilapfuncs import (
    artifact_processor,
    convert_unix_ts_to_utc,
    logfunc,
    open_sqlite_db_readonly,
)

# Forces the text fields to bytes, so a short text is never guessed to be a nested message.
_ENTRY_TYPEDEF = {'133': {'type': 'message', 'message_typedef': {
    '1': {'type': 'message', 'message_typedef': {
        '2': {'type': 'bytes'}, '3': {'type': 'bytes'},
        '4': {'type': 'bytes'}, '5': {'type': 'bytes'}}}}}}


def _text(entry, field):
    value = entry.get(field)
    return value.decode('utf-8', 'replace') if isinstance(value, bytes) else ''


@artifact_processor
def google_translate_history(context):
    data_list, source_paths = [], []
    for file_found in context.get_files_found():
        file_found = str(file_found)
        name = os.path.basename(file_found)
        if not (name.startswith('portable_geller_') and name.endswith('.db')):
            continue
        db = open_sqlite_db_readonly(file_found)
        if db is None:
            continue
        try:
            rows = db.execute('''
                SELECT k.timestamp_micro, k.delete_status, k.key, d.data
                FROM geller_key_table k JOIN geller_data_table d ON d._id = k.data_id
                WHERE k.data_type = 'TRANSLATE_HISTORY_ENTRIES'
                ORDER BY k.timestamp_micro
            ''').fetchall()
        except Exception as error:  # pylint: disable=broad-except
            logfunc(f'Google Translate: could not read {file_found}: {error}')
            rows = []
        finally:
            db.close()
        source_paths.append(context.get_relative_path(file_found))
        account = name[len('portable_geller_'):-len('.db')]
        for timestamp, delete_status, key, data in rows:
            try:
                message, _ = blackboxprotobuf.decode_message(data, _ENTRY_TYPEDEF)
                entry = message.get('133', {}).get('1', {})
            except Exception as error:  # pylint: disable=broad-except
                logfunc(f'Google Translate: an entry did not decode: {error}')
                entry = {}
            if not isinstance(entry, dict):
                entry = {}
            data_list.append((
                convert_unix_ts_to_utc(timestamp / 1_000_000) if isinstance(timestamp, int) and timestamp else '',
                _text(entry, '4'),
                _text(entry, '2'),
                _text(entry, '5'),
                _text(entry, '3'),
                account,
                delete_status if delete_status is not None else '',
                key or '',
            ))
    data_headers = (
        ('Entry Timestamp', 'datetime'),
        'Language A',
        'Text A',
        'Language B',
        'Text B',
        'Account',
        'Delete Status (as stored)',
        'Entry Key',
    )
    return data_headers, data_list, '\n'.join(source_paths)
