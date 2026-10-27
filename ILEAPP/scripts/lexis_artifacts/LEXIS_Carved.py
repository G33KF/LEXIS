"""LEXIS replacement for the message artifacts of the messaging modules below: same keys, names,
columns and conversation views, plus a Recovered column. The deleted records of the SQLite
databases each artifact reads are carved (scripts/sqlite_carver.py) and read with the
artifact's own query (lexis_carving.py), so they appear in the same table and chat view, marked
and never twice. WhatsApp is handled by LEXIS_WhatsApp."""
import copy
import importlib.util
import inspect
import pathlib

from scripts.ilapfuncs import artifact_processor, logfunc


def _load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_HERE = pathlib.Path(__file__).resolve().parent
carving = _load(_HERE / 'lexis_carving.py', 'lexis_carving_helper_carved_ios')

# Modules whose artifacts declare a conversation view (artifacts with a legacy signature or
# streamed rows are left as they are). Left out: whatsApp (LEXIS_WhatsApp).
_MODULES = ('Airbnb', 'LinkedIn', 'Oops', 'Threema', 'ZaloChats', 'ZangiChats', 'ZangiMessenger',
            'burner', 'burnerCache', 'discordChats', 'discord_a', 'dubox', 'facebookMessenger',
            'gettr', 'googleChat', 'googleVoice', 'grindr', 'groupMe', 'houseparty', 'imoHD_Chat',
            'instagramThreads', 'kakaoTalk', 'kikMessages', 'kleinanzeigen.de', 'life360', 'line',
            'mastodon', 'meWe', 'mega', 'odnoklassniki', 'pinterest', 'potatoChat', 'protonMailInbox',
            'reddit', 'sessionIOS', 'signalIOS', 'simplex', 'skout', 'slack', 'sms', 'snapchat',
            'teams', 'telegramMesssages', 'teleguard', 'textNow', 'tikTok', 'tinder', 'truthSocial',
            'twitterX', 'viber', 'vk')

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
    files = [str(f) for f in context.get_files_found()]
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
    source = '\n'.join(dict.fromkeys(s for s in str(source or '').split('\n') + sources if s))
    return tuple(headers) + ('Recovered',), rows, source


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
        _info['author'] = f"{_info.get('author', '')}, @gk-forensics"
        _info['last_update_date'] = '2026-10-22'
        __artifacts_v2__[_key] = _info
        globals()[_key] = _replacement(_func)
