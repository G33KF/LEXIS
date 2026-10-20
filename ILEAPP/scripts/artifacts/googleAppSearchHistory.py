__artifacts_v2__ = {
    "googleAppSearches": {
        "name": "Google App - Search History",
        "description": "Searches recorded by the Google app for iOS in its local search history",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Google App",
        "notes": "Read from the search_history table of Library/GoogleMobile/searchhistory.db. "
                 "Created and Last Accessed are the created and accessed columns read as Unix "
                 "seconds in UTC; Hit Count, Type and Account are the hitcount, type and user "
                 "columns as stored. Type values seen are com.google.mobile.history and "
                 "com.google.mobile.historynavsuggest, the latter on rows that also hold a "
                 "Suggested URL; the suggestion blob is not decoded. A row records that the app "
                 "stored the query; whether it was typed or picked from a suggestion is not "
                 "established. Validated on a private iOS extraction.",
        "paths": ('*/GoogleMobile/searchhistory.db*',),
        "output_types": "standard",
        "artifact_icon": "search"
    },
    "googleAppBrowseHistory": {
        "name": "Google App - Web History",
        "description": "Web pages recorded by the Google app for iOS in its local browse history",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Google App",
        "notes": "Read from the browse_history_v2 table of Library/GoogleMobile/searchhistory.db. "
                 "URL is rebuilt from the scheme, host, port, path and query columns. Created and "
                 "Last Accessed are the created and accessed columns read as Unix seconds in UTC; "
                 "Hit Count and Account are the hitcount and user columns as stored. Validated on "
                 "a private iOS extraction.",
        "paths": ('*/GoogleMobile/searchhistory.db*',),
        "output_types": "standard",
        "artifact_icon": "globe"
    },
}

from scripts.ilapfuncs import artifact_processor, get_sqlite_db_records, convert_unix_ts_to_utc


def _db(context):
    return next((str(f) for f in context.get_files_found() if str(f).endswith('searchhistory.db')), '')


def _ts(value):
    return convert_unix_ts_to_utc(value) if value else ''


@artifact_processor
def googleAppSearches(context):
    data_headers = (('Created', 'datetime'), ('Last Accessed', 'datetime'), 'Query', 'Hit Count',
                    'Type (as stored)', 'Suggested URL', 'Account')
    db = _db(context)
    rows = get_sqlite_db_records(db, '''
        SELECT created, accessed, query, hitcount, type, suggestnav_url, user
        FROM search_history ORDER BY created''') if db else []
    data_list = [(_ts(r[0]), _ts(r[1]), r[2], r[3], r[4], r[5] or '', r[6]) for r in rows]
    return data_headers, data_list, db


@artifact_processor
def googleAppBrowseHistory(context):
    data_headers = (('Created', 'datetime'), ('Last Accessed', 'datetime'), 'URL', 'Title', 'Hit Count',
                    'Account')
    db = _db(context)
    rows = get_sqlite_db_records(db, '''
        SELECT created, accessed, scheme, host, port, path, query, title, hitcount, user
        FROM browse_history_v2 ORDER BY created''') if db else []
    data_list = []
    for created, accessed, scheme, host, port, path, query, title, hits, user in rows:
        url = f"{scheme}://{host}{f':{port}' if port and port > 0 else ''}{path or ''}{f'?{query}' if query else ''}"
        data_list.append((_ts(created), _ts(accessed), url, title or '', hits, user))
    return data_headers, data_list, db
