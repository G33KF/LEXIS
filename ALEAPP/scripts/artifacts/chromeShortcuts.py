__artifacts_v2__ = {
    "get_chromeShortcuts": {
        "name": "Chrome Omnibox Shortcuts",
        "description": "Rows of the omni_box_shortcuts table in the Shortcuts database of Chromium based browsers",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Chromium",
        "notes": "One row per omni_box_shortcuts row, columns as stored: text, fill_into_edit, url, "
                 "contents, description, number_of_hits, and last_access_time converted from "
                 "Chromium time (microseconds since 1601-01-01 UTC). The columns are reported "
                 "under their stored names; what typing or selection produced a row is not "
                 "established here. Validated on a private Android 15 extraction.",
        "paths": ('*/app_chrome/Default/Shortcuts*', '*/app_sbrowser/Default/Shortcuts*',
                  '*/app_opera/Shortcuts*', '*/app_webview/Default/Shortcuts*'),
        "output_types": "standard",
        "artifact_icon": "search",
    }
}

import os
import sqlite3

from scripts.ilapfuncs import logfunc, open_sqlite_db_readonly, artifact_processor
from scripts.artifacts.chrome import get_browser_name, _webkit_to_utc
from scripts.artifacts.storagePathViews import unique_files


@artifact_processor
def get_chromeShortcuts(context):
    data_list = []
    sources = []
    for file_found in map(str, unique_files(context)):
        if os.path.basename(file_found) != 'Shortcuts' or ('.magisk' in file_found and 'mirror' in file_found):
            continue
        db = open_sqlite_db_readonly(file_found)
        if db is None:
            continue
        try:
            rows = db.execute('''
                select last_access_time, text, fill_into_edit, url, contents, description, number_of_hits
                from omni_box_shortcuts order by last_access_time''').fetchall()
        except sqlite3.Error as ex:
            logfunc(f'Unable to read {file_found}: {ex}')
            continue
        finally:
            db.close()
        browser = 'Browser' if 'app_sbrowser' in file_found else get_browser_name(file_found)
        sources.append(file_found)
        data_list.extend((_webkit_to_utc(r[0]),) + tuple(r[1:]) + (browser, file_found) for r in rows)

    data_headers = (('Last Access Time', 'datetime'), 'Text', 'Fill Into Edit', 'URL', 'Contents',
                    'Description', 'Number of Hits', 'Browser Name', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
