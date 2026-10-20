__artifacts_v2__ = {
    "shortcutsLibrary": {
        "name": "Shortcuts - Library",
        "description": "Shortcuts kept in the Shortcuts app's library database, with their actions and any URLs in the "
                       "action parameters.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Shortcuts",
        "notes": "Rows come from ZSHORTCUT, with each shortcut's actions from ZSHORTCUTACTIONS.ZDATA, a property list "
                 "of WFWorkflowActionIdentifier / WFWorkflowActionParameters dictionaries, the layout MVT reads "
                 "(Reference: MVT, mvt/ios/modules/mixed/shortcuts.py, https://github.com/mvt-project/mvt). Created and "
                 "Modified are ZCREATIONDATE and ZMODIFICATIONDATE read as Cocoa (2001) seconds. URLs are every "
                 "http(s) URL found in the text of the parameters, so a URL may be a default value rather than one the "
                 "shortcut ever opened. Columns missing from a database are left blank and logged. "
                 "No populated row has been read from a device. The database exists in three of the four public images whose path lists ship in "
                 "admin/data/filepath-lists, but none was available to run here; tested on a synthetic database.",
        "paths": ('*/mobile/Library/Shortcuts/Shortcuts.sqlite*',),
        "output_types": "standard",
        "artifact_icon": "zap",
    }
}

import plistlib
import re

from scripts.ilapfuncs import artifact_processor, convert_cocoa_core_data_ts_to_utc, logfunc, open_sqlite_db_readonly

_URL = re.compile(r"https?://[^\s'\"<>,}\]]+")


def _actions(data):
    """(identifiers, urls) from a ZSHORTCUTACTIONS.ZDATA blob; ('', '') when it is not the expected plist."""
    try:
        items = plistlib.loads(data) if data else []
    except (plistlib.InvalidFileException, ValueError, TypeError):
        return '', ''
    if not isinstance(items, list):
        return '', ''
    ids, urls = [], []
    for item in items:
        if isinstance(item, dict):
            ids.append(str(item.get('WFWorkflowActionIdentifier', '')))
            urls.extend(_URL.findall(str(item.get('WFWorkflowActionParameters', ''))))
    return ', '.join(ids), ', '.join(dict.fromkeys(urls))


@artifact_processor
def shortcutsLibrary(context):
    data_list, sources = [], []
    for f in map(str, context.get_files_found()):
        if not f.endswith('Shortcuts.sqlite'):
            continue
        db = open_sqlite_db_readonly(f)
        if db is None:
            continue
        sources.append(f)
        try:
            cur = db.cursor()
            cols = {r[1] for r in cur.execute("PRAGMA table_info(ZSHORTCUT)")}
            if not cols:
                logfunc(f"Shortcuts - Library: no ZSHORTCUT table in {f}")
                continue
            has_actions = bool(list(cur.execute("PRAGMA table_info(ZSHORTCUTACTIONS)")))
            missing = [c for c in ('ZNAME', 'ZCREATIONDATE', 'ZMODIFICATIONDATE', 'ZACTIONSDESCRIPTION') if c not in cols]
            if missing:
                logfunc(f"Shortcuts - Library: {', '.join(missing)} not in ZSHORTCUT of {f}")

            col = {c: f"s.{c}" if c in cols else "NULL" for c in ('ZNAME', 'ZCREATIONDATE', 'ZMODIFICATIONDATE', 'ZACTIONSDESCRIPTION')}
            actions = "a.ZDATA" if has_actions else "NULL"
            join = "LEFT JOIN ZSHORTCUTACTIONS a ON a.ZSHORTCUT = s.Z_PK" if has_actions else ""
            source = context.get_relative_path(f)
            for pk, name, created, modified, desc, data in cur.execute(
                    f"SELECT s.Z_PK, {col['ZNAME']}, {col['ZCREATIONDATE']}, {col['ZMODIFICATIONDATE']}, "
                    f"{col['ZACTIONSDESCRIPTION']}, {actions} FROM ZSHORTCUT s {join} ORDER BY 3"):
                ids, urls = _actions(data)
                data_list.append((convert_cocoa_core_data_ts_to_utc(created), convert_cocoa_core_data_ts_to_utc(modified),
                                  name or '', len(ids.split(', ')) if ids else 0, ids, urls, desc or '', pk, source))
        finally:
            db.close()
    data_headers = (('Created', 'datetime'), ('Modified', 'datetime'), 'Name', 'Action Count', 'Actions', 'URLs',
                    'Description', 'Shortcut ID', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
