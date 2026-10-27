"""LEXIS: the SMS artifacts without a conversation view get one, so their messages read as chats
too. Same keys, names, columns and rows as the standard artifacts; only data_views is added:
SMS Backup and Restore Messages (one chat per number) and SMS Messages - Recovered Records (one
chat per thread; Type 2 is a sent message in the telephony provider's sms table)."""
import copy
import importlib.util
import pathlib

from scripts.ilapfuncs import artifact_processor

_HERE = pathlib.Path(__file__).resolve().parent

_VIEWS = {
    ('smsBackupRestore', 'smsbackuprestore_messages'): {
        "conversationDiscriminatorColumn": "Number",
        "textColumn": "Message",
        "directionColumn": "Direction",
        "directionSentValue": "Sent",
        "timeColumn": "Timestamp",
        "senderColumn": "Number",
        "sentMessageStaticLabel": "Local User",
    },
    ('recoveredSmsCalls', 'recoveredSms'): {
        "conversationDiscriminatorColumn": "Thread ID",
        "textColumn": "Body",
        "directionColumn": "Type",
        "directionSentValue": "2",
        "timeColumn": "Date",
        "senderColumn": "Address",
        "sentMessageStaticLabel": "Local User",
    },
}


def _load(name):
    spec = importlib.util.spec_from_file_location(f'lexis_smsviews_{name}', _HERE.parent / 'artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _replacement(func):
    def run(context):
        return func.__wrapped__(context)
    run.__name__ = run.__qualname__ = func.__name__   # the name decides the artifact and its LAVA table
    return artifact_processor(run)


__artifacts_v2__ = {}
for (_module_name, _key), _view in _VIEWS.items():
    try:
        _module = _load(_module_name)
    except Exception:  # pylint: disable=broad-exception-caught
        continue  # a module whose own imports fail is left to the standard loader
    _func = getattr(_module, _key, None)
    if _key not in getattr(_module, '__artifacts_v2__', {}) or not hasattr(_func, '__wrapped__'):
        continue
    _info = copy.deepcopy(_module.__artifacts_v2__[_key])
    _info['data_views'] = {'conversation': _view}
    _info['notes'] = (_info.get('notes') or '') + ' LEXIS: a conversation view is added; rows and columns are unchanged.'
    if '@gk-forensics' not in _info.get('author', ''):
        _info['author'] = f"{_info.get('author', '')}, @gk-forensics"
    _info['last_update_date'] = '2026-10-22'
    __artifacts_v2__[_key] = _info
    globals()[_key] = _replacement(_func)
