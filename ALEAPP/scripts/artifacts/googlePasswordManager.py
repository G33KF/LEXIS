__artifacts_v2__ = {
    "google_password_manager": {
        "name": "Google Password Manager - Saved Logins",
        "description": "Saved login entries indexed in Google Play services' chromesync.data_store, "
                       "with the site or app, the sign-on realm and the user name",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Accounts",
        "notes": "One row per password_index row: Account, Origin, Signon Realm and Username are "
                 "the account, idx_origin, idx_signon_realm and idx_username columns as stored. An "
                 "origin beginning android:// names an Android app rather than a web site. The "
                 "saved password is not reported: the entry's sync_entities value is a protobuf "
                 "blob that is not decoded here, so whether it holds the password, and in what "
                 "form, is not established. In Sync Entities says whether a sync_entities row "
                 "with the same account and id exists, and Deleted Locally (as stored) is that "
                 "row's is_deleted_locally column. Account is the column as stored "
                 "and its form is not interpreted. Whether an entry was "
                 "saved on this device or synced from another signed in to the same account is "
                 "not established. affiliation_data and "
                 "facet_group_data, which map sites to apps, and password_local_chrome_data are "
                 "not reported.",
        "paths": ('*/com.google.android.gms/databases/chromesync.data_store*',),
        "output_types": "standard",
        "artifact_icon": "key",
    },
}

import os

from scripts.ilapfuncs import artifact_processor, logfunc, open_sqlite_db_readonly


@artifact_processor
def google_password_manager(context):
    data_list, source_paths = [], []
    for file_found in context.get_files_found():
        file_found = str(file_found)
        if os.path.basename(file_found) != 'chromesync.data_store':
            continue
        db = open_sqlite_db_readonly(file_found)
        if db is None:
            continue
        try:
            rows = db.execute('''
                SELECT p.account, p.idx_origin, p.idx_signon_realm, p.idx_username, p.id,
                       s.id IS NOT NULL, s.is_deleted_locally
                FROM password_index p
                LEFT JOIN sync_entities s ON s.account = p.account AND s.id = p.id
                ORDER BY p.idx_origin
            ''').fetchall()
        except Exception as error:  # pylint: disable=broad-except
            logfunc(f'Google Password Manager: could not read {file_found}: {error}')
            rows = []
        finally:
            db.close()
        source_paths.append(context.get_relative_path(file_found))
        for account, origin, realm, username, entry_id, in_sync, deleted in rows:
            data_list.append((
                origin or '',
                username or '',
                realm or '',
                account or '',
                'Yes' if in_sync else 'No',
                deleted if deleted is not None else '',
                entry_id or '',
            ))
    data_headers = (
        'Origin',
        'Username',
        'Signon Realm',
        'Account',
        'In Sync Entities',
        'Deleted Locally (as stored)',
        'Entry ID',
    )
    return data_headers, data_list, '\n'.join(source_paths)
