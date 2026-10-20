__artifacts_v2__ = {
    "get_chromeFavicons": {
        "name": "Chrome Favicons",
        "description": "Page URLs mapped to cached icons in the Favicons database of Chromium based browsers",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Chromium",
        "notes": "One row per icon_mapping row: the page URL, the icon URL it maps to, and the "
                 "largest stored bitmap of that icon with its last_updated and last_requested "
                 "values converted from Chromium time (microseconds since 1601-01-01 UTC; 0 is "
                 "left blank). A mapping records that the browser cached an icon for the page "
                 "URL; it is not by itself a record of a visit, and its time is the icon's, not "
                 "the page's. Validated on a private Android 15 extraction.",
        "paths": ('*/app_chrome/Default/Favicons*', '*/app_sbrowser/Default/Favicons*',
                  '*/app_opera/Favicons*', '*/app_webview/Default/Favicons*'),
        "output_types": "standard",
        "artifact_icon": "globe",
    }
}

import os
import sqlite3
from urllib.parse import urlparse

from scripts.ilapfuncs import logfunc, open_sqlite_db_readonly, artifact_processor, check_in_embedded_media
from scripts.artifacts.chrome import get_browser_name, _webkit_to_utc
from scripts.artifacts.storagePathViews import unique_files


@artifact_processor
def get_chromeFavicons(context):
    data_list = []
    sources = []
    for file_found in map(str, unique_files(context)):
        if os.path.basename(file_found) != 'Favicons' or ('.magisk' in file_found and 'mirror' in file_found):
            continue
        db = open_sqlite_db_readonly(file_found)
        if db is None:
            continue
        try:
            # Largest bitmap per icon; an icon with no bitmap still keeps its mapping
            rows = db.execute('''
                select m.page_url, f.url, b.last_updated, b.last_requested, b.width, b.height, b.image_data
                from icon_mapping m
                left join favicons f on f.id = m.icon_id
                left join favicon_bitmaps b on b.id = (
                    select id from favicon_bitmaps where icon_id = m.icon_id order by width * height desc limit 1)
                order by b.last_updated''').fetchall()
        except sqlite3.Error as ex:
            logfunc(f'Unable to read {file_found}: {ex}')
            continue
        finally:
            db.close()
        browser = 'Browser' if 'app_sbrowser' in file_found else get_browser_name(file_found)
        sources.append(file_found)
        for page, icon_url, updated, requested, width, height, image in rows:
            name = os.path.basename(urlparse(icon_url or '').path) or 'favicon'
            icon = check_in_embedded_media(file_found, image, name) if image else ''
            size = f'{width}x{height}' if width else ''
            data_list.append((_webkit_to_utc(updated), page, icon_url, icon or '', size,
                              _webkit_to_utc(requested), browser, file_found))

    data_headers = (('Icon Last Updated', 'datetime'), 'Page URL', 'Icon URL', ('Icon', 'media'), 'Icon Size',
                    ('Icon Last Requested', 'datetime'), 'Browser Name', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
