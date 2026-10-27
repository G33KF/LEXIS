"""LEXIS replacement for the message artifacts of the messaging modules below: same keys, names,
columns and conversation views, plus a Recovered column. The deleted records of the SQLite
databases each artifact reads are carved (scripts/sqlite_carver.py) and read with the
artifact's own query (lexis_carving.py), so they appear in the same table and chat view, marked
and never twice. WhatsApp is handled by LEXIS_WhatsApp, which also merges its backups. The SMS and
MMS artifacts also get the SIM card of each message (lexis_sms_sim.py)."""
import copy
import importlib.util
import inspect
import os
import pathlib

from scripts.ilapfuncs import artifact_processor, logfunc


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_HERE = pathlib.Path(__file__).resolve().parent
carving = _load(_HERE / 'lexis_carving.py', 'lexis_carving_helper_carved')
sms_sim = _load(_HERE / 'lexis_sms_sim.py', 'lexis_sms_sim_helper_carved')

# Modules whose artifacts declare a conversation view, read from SQLite. Left out: WhatsApp
# (LEXIS_WhatsApp) and weChat (an encrypted database, decrypted into a copy the run discards).
_MODULES = ('FacebookMessenger', 'Viber', 'bumble', 'burner', 'burnerMessages', 'discordChats', 'gettr',
            'googleChat', 'googleMessages', 'googleVoice', 'houseparty', 'imo', 'instagram', 'justalk',
            'kikMessenger', 'kleinanzeigen.de', 'knuddels', 'line', 'mewe', 'reddit', 'simplexChat', 'skype',
            'slack', 'smsmms', 'snapchat', 'teleguard', 'tikTok', 'tinder', 'LinkedIn', 'Life360')

_NOTE = (' LEXIS: deleted records of the SQLite databases this artifact reads are carved (older page versions '
         'in the main file and the write-ahead log, free space of pages) and read with the same query. A '
         'record counts as deleted only when the active database holds no row with its row id and none with '
         'the same values in a UNIQUE index; such rows have Recovered set, with the reliability: high (older '
         'page version, row id kept), medium (free space, row id kept), low (row id lost or record '
         'truncated). Recovered is empty on live rows.')


class _Files:
    """The run's context with files_found replaced."""

    def __init__(self, context, files):
        self._context, self._files = context, files

    def get_files_found(self):
        return self._files

    def __getattr__(self, name):
        return getattr(self._context, name)


def _with_recovered(func, context):
    # telephony.db is only the SIM lookup of the SMS artifacts: never carved
    files = [str(f) for f in context.get_files_found() if os.path.basename(str(f)) != 'telephony.db']
    headers, rows, source = func.__wrapped__(context)
    if not isinstance(rows, list):
        return headers, rows, source  # streamed or split HTML rows: left as the standard artifact returns them
    rows = [tuple(r) + ('',) for r in rows]
    try:
        found, sources = carving.recovered(lambda fs: func.__wrapped__(_Files(context, fs)), files,
                                           [r[:-1] for r in rows], context)
    except Exception as ex:  # pylint: disable=broad-exception-caught
        logfunc(f'LEXIS carving: {func.__name__}: {ex}')
        found, sources = [], []
    rows += [tuple(row) + (carving.note(level, path),) for row, level, path in found]
    headers = tuple(headers) + ('Recovered',)
    if func.__name__ in sms_sim.ROWS:
        try:
            headers, rows, used = sms_sim.with_sim(func.__name__, context.get_files_found(), headers, rows)
            sources += used
        except Exception as ex:  # pylint: disable=broad-exception-caught
            logfunc(f'LEXIS SIM: {func.__name__}: {ex}')
            headers, rows = headers + sms_sim.HEADERS, [tuple(r) + ('',) * len(sms_sim.HEADERS) for r in rows]
    source = '\n'.join(dict.fromkeys(s for s in str(source or '').split('\n') + sources if s))
    return headers, rows, source


def _replacement(func):
    def run(context):
        return _with_recovered(func, context)
    run.__name__ = run.__qualname__ = func.__name__   # the name decides the artifact and its LAVA table
    return artifact_processor(run)


__artifacts_v2__ = {}
for _name in _MODULES:
    try:
        _module = _load(_HERE.parent / 'artifacts' / f'{_name}.py', f'lexis_carved_{_name.replace(".", "_")}')
    except Exception:  # pylint: disable=broad-exception-caught
        continue  # a module whose own imports fail is left to the standard loader
    for _key, _info in getattr(_module, '__artifacts_v2__', {}).items():
        _func = getattr(_module, _key, None)
        if ('conversation' not in (_info.get('data_views') or {}) or not hasattr(_func, '__wrapped__')
                or len(inspect.signature(_func.__wrapped__).parameters) != 1):
            continue
        _info = copy.deepcopy(_info)
        _info['notes'] = (_info.get('notes') or '') + _NOTE
        if _key in sms_sim.ROWS:
            _info['notes'] += sms_sim.NOTE
            _info['paths'] = tuple(_info['paths']) + (sms_sim.TELEPHONY,)
        _info['author'] = f"{_info.get('author', '')}, @gk-forensics"
        _info['last_update_date'] = '2026-10-22'
        __artifacts_v2__[_key] = _info
        globals()[_key] = _replacement(_func)
