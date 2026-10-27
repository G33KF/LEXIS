"""LEXIS replacement for the WhatsApp artifacts of whatsApp.py and whatsAppExtended.py: same keys,
names and columns, run once per app group container. The standard functions read the first
ChatStorage.sqlite matched, so WhatsApp Business, whose container holds the same files, was
read only when WhatsApp was absent. Account ID, Account Name and App columns are added."""
import copy
import importlib.util
import os
import pathlib
import re

from scripts.ilapfuncs import artifact_processor, get_plist_file_content, logfunc


def _load(name):
    """A private copy of a standard module, so nothing here changes scripts.artifacts.<name>."""
    spec = importlib.util.spec_from_file_location(
        f'lexis_{name}_base', pathlib.Path(__file__).resolve().parents[1] / 'artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


wa = _load('whatsApp')
wax = _load('whatsAppExtended')
_spec = importlib.util.spec_from_file_location('lexis_carving_helper_whatsapp_ios',
                                               pathlib.Path(__file__).resolve().parent / 'lexis_carving.py')
carving = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(carving)
_CARVED = ('whatsAppMessages',)

_GROUP = re.compile(r'(.*/Containers/Shared/AppGroup/[^/]+)/')
_METADATA = '.com.apple.mobile_container_manager.metadata.plist'
_APPS = {'group.net.whatsapp.WhatsApp.shared': 'WhatsApp',
         'group.net.whatsapp.WhatsAppSMB.shared': 'WhatsApp Business'}
_EXTRA_PATHS = (f'*/mobile/Containers/Shared/AppGroup/*/{_METADATA}',
                '*/mobile/Containers/Shared/AppGroup/*/Library/Preferences/group.net.whatsapp.*.plist')


def _plist(path):
    # the tolerant reader: WhatsApp's preferences can hold a date plistlib rejects, losing every key
    value = get_plist_file_content(path)
    return value if isinstance(value, dict) else {}


def _groups(context):
    """[(app, account, name, files)] per app group container holding WhatsApp data. The app is named
    from the container's MCMMetadataIdentifier; a container whose identifier names another app
    is left out, one with no metadata file is read as WhatsApp's."""
    groups = {}
    for file_found in context.get_files_found():
        file_found = str(file_found)
        match = _GROUP.match(file_found.replace('\\', '/'))
        if match:
            groups.setdefault(match.group(1), []).append(file_found)
    out = []
    for _, files in sorted(groups.items()):
        identifier = next((_plist(f).get('MCMMetadataIdentifier', '') for f in files
                           if os.path.basename(f) == _METADATA), '')
        if identifier and identifier not in _APPS:
            continue
        app = _APPS.get(identifier, 'WhatsApp')
        prefs = next((_plist(f) for f in files if os.path.basename(f) == f'{identifier}.plist'), {})
        account = str(prefs.get('OwnJabberID') or '') or identifier or app
        if any(os.path.basename(f) != _METADATA and not f.endswith('.plist') for f in files):
            out.append((app, account, str(prefs.get('FullUserName') or '').strip(), files))
    return out


class _Files:
    """The run's context with files_found narrowed to one container."""

    def __init__(self, context, files):
        self._context, self._files = context, files

    def get_files_found(self):
        return self._files

    def __getattr__(self, name):
        return getattr(self._context, name)


def _per_container(func, context, recovered=False):
    headers, rows, sources = None, [], []
    for app, account, name, files in _groups(context):
        headers, found, source = func.__wrapped__(_Files(context, files))
        rows += [tuple(row) + (account, name, app) + (('',) if recovered else ()) for row in found]
        sources += str(source or '').split('\n')
        if not recovered:
            continue
        # Deleted messages still in the pages of ChatStorage.sqlite, read with the same query.
        try:
            carved, carved_sources = carving.recovered(lambda fs: func.__wrapped__(_Files(context, fs)), files,
                                                       found, context)
        except Exception as ex:  # pylint: disable=broad-exception-caught
            logfunc(f'LEXIS carving: {func.__name__}: {ex}')
            carved, carved_sources = [], []
        rows += [tuple(row) + (account, name, app, carving.note(level, path)) for row, level, path in carved]
        sources += carved_sources
    if headers is None:
        headers = func.__wrapped__(_Files(context, []))[0]
    headers = tuple(headers) + ('Account ID', 'Account Name', 'App') + (('Recovered',) if recovered else ())
    return headers, rows, '\n'.join(dict.fromkeys(s for s in sources if s))


_NOTE = (' LEXIS: this module replaces the artifact with the same key and name and runs it once per app '
         'group container, WhatsApp (group.net.whatsapp.WhatsApp.shared) and WhatsApp Business '
         '(group.net.whatsapp.WhatsAppSMB.shared), named from the container metadata. Account ID is the '
         'OwnJabberID value of the container preferences when present, else the container identifier; '
         'Account Name is their FullUserName, the name the account shows to others, empty when absent.')
_CARVE_NOTE = (' Deleted messages still in the pages of ChatStorage.sqlite are carved (lexis_carving) and read '
               'with the same query when the active database holds neither their row id nor their UNIQUE key; '
               'Recovered then gives the reliability, and is empty on live rows.')

__artifacts_v2__ = {}
for _module in (wa, wax):
    for _key, _info in _module.__artifacts_v2__.items():
        _info = copy.deepcopy(_info)
        _info['paths'] = tuple(dict.fromkeys(tuple(_info['paths']) + _EXTRA_PATHS))
        _info['notes'] = _info.get('notes', '') + _NOTE + (_CARVE_NOTE if _key in _CARVED else '')
        _info['author'] = f"{_info['author']}, @gk-forensics"
        _info['last_update_date'] = '2026-10-26'
        __artifacts_v2__[_key] = _info


def _replacement(func):
    def run(context):
        return _per_container(func, context, func.__name__ in _CARVED)
    run.__name__ = run.__qualname__ = func.__name__   # the name decides the artifact and its LAVA table
    return artifact_processor(run)


for _module in (wa, wax):
    for _key in _module.__artifacts_v2__:
        globals()[_key] = _replacement(getattr(_module, _key))
