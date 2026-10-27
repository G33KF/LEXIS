"""LEXIS replacement for the WeChat artifacts of weChat.py: same keys, names and columns,
every account database opened. weChat.py finds an account's uin only in the shared
preferences, which hold the signed-in account; this module also reads it from the cdninfo
caches in each account's own MicroMsg directory."""
import copy
import hashlib
import importlib.util
import os
import pathlib
import struct

from scripts.ilapfuncs import artifact_processor

_BASE_PATH = pathlib.Path(__file__).resolve().parents[1] / 'artifacts' / 'weChat.py'
_spec = importlib.util.spec_from_file_location('lexis_wechat_base', _BASE_PATH)
# A private copy, so replacing its _connections below leaves scripts.artifacts.weChat alone.
base = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(base)

_CDNINFO = ('cdninfo_new.cache', 'cdninfo_mobile_net_new.cache')
_CDNINFO_MAX = 256 << 10
_CFG = ('CompatibleInfo.cfg', 'systemInfo.cfg')
_CFG_MAX = 1 << 20


def _account_dir(uin):
    """The MicroMsg directory name of an account: md5 of mm joined to the uin."""
    return hashlib.md5(f'mm{uin}'.encode()).hexdigest()


def _cdninfo_uin(paths, directory):
    """The uin, signed or unsigned, of the 4-byte big-endian integer in the cdninfo caches
    that reproduces the account directory name, or None."""
    for path in paths:
        try:
            with open(path, 'rb') as handle:
                data = handle.read(_CDNINFO_MAX)
        except OSError as error:
            base._log_once(f'WeChat: could not read {path}: {error}')  # pylint: disable=protected-access
            continue
        tried = set()
        for offset in range(len(data) - 3):
            (signed,) = struct.unpack_from('>i', data, offset)
            if signed in tried:
                continue
            tried.add(signed)
            for uin in (str(signed), str(signed & 0xFFFFFFFF)):
                if _account_dir(uin) == directory:
                    return uin
    return None


def _luhn(digits):
    """True for a 15-digit string whose Luhn check digit is right, as an IMEI's is."""
    if len(digits) != 15 or not digits.isascii() or not digits.isdigit():
        return False
    return sum(int(d) if i % 2 == 0 else sum(divmod(int(d) * 2, 10)) for i, d in enumerate(digits)) % 10 == 0


def _cfg_strings(path):
    """The Java-serialized strings (TC_STRING: 0x74, 2-byte length, text) of a WeChat .cfg file."""
    try:
        with open(path, 'rb') as handle:
            data = handle.read(_CFG_MAX)
    except OSError as error:
        base._log_once(f'WeChat: could not read {path}: {error}')  # pylint: disable=protected-access
        return []
    found = []
    for offset in range(len(data) - 3):
        if data[offset] != 0x74:
            continue
        size = int.from_bytes(data[offset + 1:offset + 3], 'big')
        text = data[offset + 3:offset + 3 + size]
        if 0 < size <= 64 and len(text) == size and all(32 < b < 127 for b in text):
            found.append(text.decode('ascii'))
    return found


def _imei_fallbacks(prefs, cfgs):
    """IMEI candidates beyond weChat.py's, from the same app data directory: the strings of
    CompatibleInfo.cfg and systemInfo.cfg, where WeChat keeps the IMEI it read, the preference
    values named for an IMEI or a device id, and any Luhn-valid 15-digit preference value."""
    found = [s for f in cfgs for s in _cfg_strings(f)]
    for f in prefs:
        for name, value in base._prefs(f):  # pylint: disable=protected-access
            value = (value or '').strip()
            lowered = name.lower()
            if value and ('imei' in lowered or 'deviceid' in lowered.replace('_', '') or _luhn(value)):
                found.append(value)
    return found


def _connections(context):
    """Yield (connection, uin, db path) for each account database opened.

    The uin is taken from the shared preferences as in weChat.py and, where none of them
    reproduces the account directory name, from the cdninfo caches of that directory.
    """
    files_found = [str(f) for f in base.unique_files(context)]
    if not base._SQLCIPHER_AVAILABLE:  # pylint: disable=protected-access
        return
    for db_path in files_found:
        if os.path.basename(db_path) != 'EnMicroMsg.db':
            continue
        home = base._home(context, db_path)  # pylint: disable=protected-access
        directory = os.path.basename(os.path.dirname(db_path))
        prefs = [f for f in files_found if base._home(context, f) == home  # pylint: disable=protected-access
                 and os.path.basename(os.path.dirname(f)) == 'shared_prefs' and f.endswith('.xml')]
        caches = [f for f in files_found if base._home(context, f) == home  # pylint: disable=protected-access
                  and os.path.basename(os.path.dirname(f)) == directory
                  and os.path.basename(f) in _CDNINFO]
        uin = next((u for u in base._uin_candidates(prefs)  # pylint: disable=protected-access
                    if _account_dir(u) == directory), None) or _cdninfo_uin(caches, directory)
        if uin is None:
            base._log_once(f'WeChat: {db_path} belongs to an account whose uin is in neither the '  # pylint: disable=protected-access
                           f'preference files nor its cdninfo caches, so its key cannot be '
                           f'derived; skipping')
            continue
        imei = next((v for f in prefs if os.path.basename(f) == 'WLOGIN_DEVICE_INFO.xml'
                     for n, v in base._prefs(f) if n == 'imei'), '')  # pylint: disable=protected-access
        cfgs = [f for f in files_found if base._home(context, f) == home  # pylint: disable=protected-access
                and os.path.basename(f) in _CFG]
        # weChat.py's candidates first, then the others, each also hex decoded; every key is
        # derived and tried, none guessed. ponytail: one SQLCipher open per candidate, a few ms each.
        imeis = base._imei_candidates(imei)  # pylint: disable=protected-access
        for value in _imei_fallbacks(prefs, cfgs):
            imeis += [c for c in base._imei_candidates(value) if c not in imeis]  # pylint: disable=protected-access
        connection = base._open_database(db_path, uin, imeis)  # pylint: disable=protected-access
        if connection is not None:
            yield connection, uin, db_path


base._connections = _connections  # pylint: disable=protected-access


_NOTE = (' LEXIS: this module replaces the weChat.py artifacts with the same keys and names. '
         'Where no preference value reproduces an account directory name, the uin is read from '
         'the 4-byte big-endian integers in cdninfo_new.cache and cdninfo_mobile_net_new.cache '
         'of that directory, and used only if it reproduces the name. Where the key from the '
         'stored IMEI and the fallback literal does not open a database, the IMEI is also taken '
         'from the strings of MicroMsg/CompatibleInfo.cfg and systemInfo.cfg, the preference '
         'values named for an IMEI or a device id, and any Luhn-valid 15-digit preference value; '
         'each derives a key that is tried, none is guessed. Account directories without an '
         'EnMicroMsg.db are not read.')

__artifacts_v2__ = copy.deepcopy(base.__artifacts_v2__)
for _info in __artifacts_v2__.values():
    _info['paths'] = (tuple(_info['paths']) + tuple(f'*/com.tencent.mm/MicroMsg/*/{c}' for c in _CDNINFO)
                      + tuple(f'*/com.tencent.mm/MicroMsg/{c}' for c in _CFG))
    _info['notes'] = _info.get('notes', '') + _NOTE
    _info['author'] = f"{_info['author']}, @gk-forensics"
    _info['last_update_date'] = '2026-10-22'


_CALL_NOTE = (' On a type 50 row the call text the application shows (Message here, Call Summary in '
              'WeChat - Calls) is the msg element of VoIPBubbleMsg, else, where the content column holds '
              'only a short string such as voip_content_voice, the third field of the lvbuffer column: a { '
              'byte, a four-byte integer, then two strings each preceded by a two-byte big-endian length. '
              'That layout is read as observed, not from a published source; on every row examined that '
              'held both, the lvbuffer string equalled the msg element. An lvbuffer of any other shape '
              'leaves the stored value in place.')
for _key in ('wechat_messages', 'wechat_calls'):
    __artifacts_v2__[_key]['notes'] += _CALL_NOTE


def _lvbuffer_text(blob):
    """The third field of a call row's lvbuffer, or '' when it does not have the observed shape."""
    if not isinstance(blob, bytes) or blob[:1] != b'{':
        return ''
    fields, at = [], 5
    for _ in range(2):
        if at + 2 > len(blob):
            return ''
        (length,) = struct.unpack('>H', blob[at:at + 2])
        fields.append(blob[at + 2:at + 2 + length])
        at += 2 + length
    if at > len(blob):
        return ''
    try:
        return fields[1].decode('utf-8').strip()
    except UnicodeDecodeError:
        return ''


def _call_texts(context):
    """{(time, talker, msg id): call text} for the type 50 rows of every account database."""
    rows, _ = base._query(context, 'SELECT createTime, talker, msgId, content, lvbuffer FROM message WHERE type = 50')  # pylint: disable=protected-access
    texts = {}
    for (created, talker, msg_id, content, lvbuffer), _ in rows:
        root = base._xml_root(base._split_sender(talker, 0, content)[1])  # pylint: disable=protected-access
        text = base._find_text(root, './VoIPBubbleMsg/msg') or _lvbuffer_text(lvbuffer)  # pylint: disable=protected-access
        if text:
            texts[(base._ms_time(created), talker or '', msg_id if msg_id is not None else '')] = text  # pylint: disable=protected-access
    return texts


@artifact_processor
def wechat_messages(context):
    headers, rows, sources = base.wechat_messages.__wrapped__(context)
    texts = _call_texts(context)
    # columns: 0 time, 4 Message, 6 Talker, 9 type, 17 Message ID
    rows = [row[:4] + (texts.get((row[0], row[6], row[17]), row[4]),) + row[5:] if row[9] == 50 else row
            for row in rows]
    return headers, rows, sources


@artifact_processor
def wechat_contacts(context):
    return base.wechat_contacts.__wrapped__(context)


@artifact_processor
def wechat_conversations(context):
    return base.wechat_conversations.__wrapped__(context)


@artifact_processor
def wechat_account(context):
    return base.wechat_account.__wrapped__(context)


@artifact_processor
def wechat_calls(context):
    headers, rows, sources = base.wechat_calls.__wrapped__(context)
    texts = _call_texts(context)
    # columns: 0 time, 4 Talker, 5 Call Summary, 9 Message ID
    rows = [row[:5] + (texts.get((row[0], row[4], row[9]), row[5]),) + row[6:] for row in rows]
    return headers, rows, sources


@artifact_processor
def wechat_payments(context):
    return base.wechat_payments.__wrapped__(context)


@artifact_processor
def wechat_locations(context):
    return base.wechat_locations.__wrapped__(context)


@artifact_processor
def wechat_moments(context):
    return base.wechat_moments.__wrapped__(context)


@artifact_processor
def wechat_moments_reactions(context):
    return base.wechat_moments_reactions.__wrapped__(context)


@artifact_processor
def wechat_group_members(context):
    return base.wechat_group_members.__wrapped__(context)
