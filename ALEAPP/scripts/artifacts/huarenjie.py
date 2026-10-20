__artifacts_v2__ = {
    "huarenjie_account": {
        "name": "Huarenjie - Account",
        "description": "Signed-in account rows kept by the Huarenjie (华人街) app in foa.db",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Huarenjie",
        "notes": "One row per UserCurrEntity and OaUsercurr row of foa.db. UID, Phone and "
                 "Nickname are the uid, phone and nick columns as stored; Data is the data "
                 "column, a JSON text, kept whole. OaUsercurr also has a pwd column, which is "
                 "not reported. HttpResp holds cached server responses keyed by URL and "
                 "sp_db the app's settings; neither is reported.",
        "paths": ('*/com.huarenjie.gj/databases/foa.db*',),
        "output_types": "standard",
        "artifact_icon": "user",
    },
}

from scripts.ilapfuncs import artifact_processor, get_sqlite_db_records


@artifact_processor
def huarenjie_account(context):
    data_list, sources = [], []
    for file_found in context.get_files_found():
        file_found = str(file_found)
        if not file_found.endswith('foa.db'):
            continue
        sources.append(context.get_relative_path(file_found))
        for table in ('UserCurrEntity', 'OaUsercurr'):
            nick = 'nick' if table == 'UserCurrEntity' else "''"
            for row in get_sqlite_db_records(file_found, f'SELECT uid, phone, {nick}, data FROM {table}'):
                data_list.append((row[0], row[1] or '', row[2] or '', table, row[3] or ''))
    data_headers = ('UID', 'Phone', 'Nickname', 'Table', 'Data')
    return data_headers, data_list, '\n'.join(sources)
