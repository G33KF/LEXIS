"""LEXIS: siminfo also gives each card's MCC and MNC as the telephony provider stores them (mcc_string and
mnc_string, else the older integer mcc and mnc, which drop an MNC's leading zero). Same key, name and rows as
the standard artifact; two columns are added at the end, blank where the database has neither."""
import copy
import importlib.util
import pathlib
import sqlite3

from scripts.ilapfuncs import artifact_processor, open_sqlite_db_readonly

_HERE = pathlib.Path(__file__).resolve().parent
_KEY = 'get_siminfo'


def _load():
    spec = importlib.util.spec_from_file_location('lexis_siminfo_base', _HERE.parent / 'artifacts' / 'siminfo.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _codes(path):
    """(icc_id, number) -> [(mcc, mnc), ...] in the table's order; empty when the database has no such columns."""
    db = open_sqlite_db_readonly(path)
    if db is None:
        return {}
    codes = {}
    try:
        cols = {r[1].lower() for r in db.execute('PRAGMA table_info(siminfo)')}
        mcc = 'mcc_string' if 'mcc_string' in cols else 'mcc' if 'mcc' in cols else None
        mnc = 'mnc_string' if 'mnc_string' in cols else 'mnc' if 'mnc' in cols else None
        if mcc or mnc:
            for icc, number, a, b in db.execute(
                    f"SELECT icc_id, number, {mcc or 'NULL'}, {mnc or 'NULL'} FROM siminfo"):
                codes.setdefault((icc, number), []).append(('' if a is None else str(a), '' if b is None else str(b)))
    except sqlite3.Error:
        return {}
    finally:
        db.close()
    return codes


def _replacement(func):
    def get_siminfo(context):
        headers, rows, source = func.__wrapped__(context)
        # the rows of each User ID, in order, come from that user's telephony.db
        codes = {}
        for path in (source or '').split(', '):
            parts = path.replace('\\', '/').split('/')
            if len(parts) > 4 and path:
                codes[parts[-4]] = _codes(path)
        out = []
        for row in rows:
            found = codes.get(row[0], {}).get((row[7], row[1]))
            out.append(tuple(row) + (found.pop(0) if found else ('', '')))
        return tuple(headers) + ('MCC', 'MNC'), out, source
    return artifact_processor(get_siminfo)


__artifacts_v2__ = {}
try:
    _base = _load()
except Exception:  # pylint: disable=broad-exception-caught
    _base = None  # a module whose own imports fail is left to the standard loader
if _base is not None and _KEY in getattr(_base, '__artifacts_v2__', {}) and hasattr(getattr(_base, _KEY, None), '__wrapped__'):
    _info = copy.deepcopy(_base.__artifacts_v2__[_KEY])
    _info['notes'] = (_info.get('notes') or '') + (
        ' LEXIS: MCC and MNC are added as siminfo stores them (mcc_string/mnc_string, else mcc/mnc); '
        'blank where the database has neither.')
    if '@gk-forensics' not in _info.get('author', ''):
        _info['author'] = f"{_info.get('author', '')}, @gk-forensics"
    _info['last_update_date'] = '2026-10-22'
    __artifacts_v2__[_KEY] = _info
    globals()[_KEY] = _replacement(getattr(_base, _KEY))
