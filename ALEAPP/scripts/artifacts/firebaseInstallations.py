__artifacts_v2__ = {
    "firebaseInstallations": {
        "name": "Firebase Installation Tokens",
        "description": "Firebase installation records kept by apps in PersistedInstallation JSON files",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Installed Apps",
        "notes": "One row per PersistedInstallation.<key>.json file and app folder; the data/data and "
                 "data/user/0 views of one file are read once. Package is the app folder holding the "
                 "file. Firebase App Name and Firebase App ID are the two base64 parts of the file "
                 "name decoded. Installation ID, Status, Auth Token, Refresh Token and Expires In are "
                 "the Fid, Status, AuthToken, RefreshToken and ExpiresInSecs keys as stored; Token "
                 "Created is TokenCreationEpochInSecs read as Unix seconds in UTC; a value of 0 is "
                 "shown blank. A row records that the app's Firebase SDK stored an installation, "
                 "not that the user opened the app at Token Created. Validated on a private Android 15 extraction.",
        "paths": ('*/PersistedInstallation.*.json',),
        "output_types": "standard",
        "artifact_icon": "key"
    },
}

import base64
import json
import re

from scripts.ilapfuncs import artifact_processor, convert_unix_ts_to_utc, logfunc


def _b64(part):
    try:
        return base64.urlsafe_b64decode(part + '=' * (-len(part) % 4)).decode('utf-8')
    except (ValueError, UnicodeDecodeError):
        return part


@artifact_processor
def firebaseInstallations(context):
    data_headers = (('Token Created', 'datetime'), 'Package', 'Firebase App Name', 'Firebase App ID',
                    'Installation ID', 'Status (as stored)', 'Expires In (seconds)', 'Auth Token',
                    'Refresh Token', 'Source File')
    data_list, sources, seen = [], [], set()
    for file_found in map(str, context.get_files_found()):
        parts = re.split(r'[\\/]', file_found)
        package, name = parts[-3], parts[-1]
        if (package, name) in seen:
            continue
        seen.add((package, name))
        try:
            with open(file_found, encoding='utf-8') as f:
                d = json.load(f)
        except (OSError, ValueError) as ex:
            logfunc(f'Firebase Installation Tokens: {context.get_relative_path(file_found)} not read: {ex}')
            continue
        app_name, _, app_id = name[len('PersistedInstallation.'):-len('.json')].partition('+')
        created = d.get('TokenCreationEpochInSecs')
        data_list.append((convert_unix_ts_to_utc(created) if created else '', package, _b64(app_name),
                          _b64(app_id), d.get('Fid', ''), d.get('Status'), d.get('ExpiresInSecs'),
                          d.get('AuthToken', ''), d.get('RefreshToken', ''), context.get_relative_path(file_found)))
        sources.append(file_found)
    return data_headers, data_list, '\n'.join(sources)
