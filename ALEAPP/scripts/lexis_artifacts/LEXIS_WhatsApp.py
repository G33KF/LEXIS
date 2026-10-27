"""LEXIS replacement for the WhatsApp artifacts of WhatsApp.py and whatsAppExtended.py: same keys,
names and columns, run once per app data directory of WhatsApp and of WhatsApp Business, with
Account ID and App columns added. The message artifacts also report the messages of the local
backups (msgstore*.db.crypt12/14/15) that the active database no longer holds, marked in a
Recovered column. Three artifacts list the backups and every message they hold."""
import atexit
import copy
import hashlib
import hmac
import importlib.util
import os
import pathlib
import re
import shutil
import sqlite3
import tempfile
import xml.etree.ElementTree as ET
import zlib

from Crypto.Cipher import AES

from scripts.ilapfuncs import artifact_processor, logfunc, open_sqlite_db_readonly


def _load(name):
    """A private copy of a standard module, so nothing here changes scripts.artifacts.<name>."""
    spec = importlib.util.spec_from_file_location(
        f'lexis_{name}_base', pathlib.Path(__file__).resolve().parents[1] / 'artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


wa = _load('WhatsApp')
wax = _load('whatsAppExtended')
_spec = importlib.util.spec_from_file_location('lexis_carving_helper_whatsapp',
                                               pathlib.Path(__file__).resolve().parent / 'lexis_carving.py')
carving = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(carving)

_APPS = {'com.whatsapp': 'WhatsApp', 'com.whatsapp.w4b': 'WhatsApp Business'}
_PREFS = ('com.whatsapp_preferences_light.xml', 'com.whatsapp.w4b_preferences_light.xml', 'startup_prefs.xml')
_KEYS = ('key', 'encrypted_backup.key')
_BACKUP = re.compile(r'^msgstore.*\.db\.(crypt1[245])$')
_TENANT = re.compile(r'/(?:user(?:_de)?|emulated|media|null)/(\d+)/')
_SQLITE = b'SQLite format 3\x00'
_CHUNK = 1 << 20
_OUT = 'LEXIS_WhatsApp_Backups'
_BACKUP_PATHS = (tuple(f'*/{folder}/Databases/msgstore*.crypt1*' for folder in _APPS.values())
                 + tuple(f'*/{package}/files/{name}' for package in _APPS for name in _KEYS))
_PREFS_PATHS = tuple(f'*/{package}/shared_prefs/{name}' for package in _APPS for name in _PREFS)


# -- app data directories ------------------------------------------------------------------

def _container(relative):
    """(Android user, package) of a WhatsApp or WhatsApp Business file, or None."""
    path = '/' + str(relative).replace('\\', '/')
    parts = path.split('/')
    if 'com.whatsapp.w4b' in parts or 'WhatsApp Business' in parts:
        package = 'com.whatsapp.w4b'
    elif 'com.whatsapp' in parts or 'WhatsApp' in parts:
        package = 'com.whatsapp'
    else:
        return None
    tenant = _TENANT.search(path)
    return (tenant.group(1) if tenant else '0', package)


def _groups(context):
    """[((user, package), files)] of the run's files, sorted."""
    groups = {}
    for file_found in context.get_files_found():
        file_found = str(file_found)
        key = _container(context.get_relative_path(file_found))
        if key:
            groups.setdefault(key, []).append(file_found)
    return sorted(groups.items())


def _label(tenant, package):
    return _APPS[package] + ('' if tenant == '0' else f' (Android user {tenant})')


def _account(files, package):
    """'+<cc><ph>' from the app's preferences, else the package name."""
    values = {}
    for path in files:
        if os.path.basename(path) not in _PREFS:
            continue
        try:
            root = ET.parse(path).getroot()
        except (OSError, ET.ParseError):
            continue
        for entry in root.iter('string'):
            values.setdefault(entry.get('name'), (entry.text or '').strip())
    cc, ph = values.get('cc', ''), values.get('ph', '')
    return f'+{cc}{ph}' if cc and ph else ph or package


class _Files:
    """The run's context with files_found narrowed to the given files."""

    def __init__(self, context, files):
        self._context, self._files = context, files

    def get_files_found(self):
        return self._files

    def __getattr__(self, name):
        return getattr(self._context, name)


# -- decryption ----------------------------------------------------------------------------

def _hkdf(root, info=b'backup encryption'):
    """HKDF-SHA256 (RFC 5869), zero salt, one 32-byte block: the crypt15 key of a root key."""
    prk = hmac.new(b'\x00' * 32, root, hashlib.sha256).digest()
    return hmac.new(prk, info + b'\x01', hashlib.sha256).digest()


def _keys(paths):
    """(key file, key): the last 32 bytes of each file as stored (crypt12, crypt14) and through
    HKDF (crypt15)."""
    keys = []
    for path in paths:
        try:
            with open(path, 'rb') as handle:
                data = handle.read(4096)
        except OSError:
            continue
        if len(data) >= 32:
            keys += [(path, data[-32:]), (path, _hkdf(data[-32:]))]
    return keys


def _varint(data, pos):
    value = shift = 0
    while pos < len(data) and shift < 64:
        byte = data[pos]
        pos += 1
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value, pos
        shift += 7
    raise ValueError('bad varint')


def _fields16(data, depth=0):
    """The 16-byte length-delimited values of a protobuf message, nested ones included."""
    found, pos = [], 0
    try:
        while pos < len(data):
            tag, pos = _varint(data, pos)
            wire = tag & 7
            if wire == 0:
                _, pos = _varint(data, pos)
            elif wire in (1, 5):
                pos += 8 if wire == 1 else 4
            elif wire == 2:
                size, pos = _varint(data, pos)
                value = data[pos:pos + size]
                if size == 16 and len(value) == 16:
                    found.append(value)
                if depth < 3:
                    found += _fields16(value, depth + 1)
                pos += size
            else:
                break
    except ValueError:
        pass
    return found


def _candidates(head):
    """(IV, data offset) pairs a backup header can hold: crypt12's fixed layout, the 16-byte
    fields of the crypt14/15 protobuf (after a size byte and an optional feature byte), and
    early crypt14's fixed offsets."""
    found = [(head[51:67], 67)]
    for start in (1, 2):
        end = start + head[0]
        found += [(iv, end) for iv in _fields16(head[start:end])]
    found += [(head[67:83], 190), (head[67:83], 191)]
    return list(dict.fromkeys(c for c in found if len(c[0]) == 16))


def _opens(path, key, iv, offset):
    """True when the bytes at offset decrypt to a zlib stream that starts an SQLite database."""
    with open(path, 'rb') as handle:
        handle.seek(offset)
        chunk = handle.read(4096)
    try:
        plain = AES.new(key, AES.MODE_GCM, nonce=iv).decrypt(chunk)
        return zlib.decompressobj().decompress(plain, 16).startswith(_SQLITE)
    except (ValueError, zlib.error):
        return False


def _decrypt(path, key, iv, offset, target):
    """Write the database; return (stream complete, GCM tag after the stream verified)."""
    cipher = AES.new(key, AES.MODE_GCM, nonce=iv)
    check = AES.new(key, AES.MODE_GCM, nonce=iv)    # fed exactly the ciphertext, for the tag
    inflate = zlib.decompressobj()
    tag = b''
    with open(path, 'rb') as source, open(target, 'wb') as out:
        source.seek(offset)
        while not inflate.eof:
            chunk = source.read(_CHUNK)
            if not chunk:
                break
            out.write(inflate.decompress(cipher.decrypt(chunk)))
            used = len(chunk) - len(inflate.unused_data) if inflate.eof else len(chunk)
            check.decrypt(chunk[:used])
            if inflate.eof:
                tag = (chunk[used:] + source.read(16))[:16]
    try:
        check.verify(tag)
        verified = True
    except ValueError:
        verified = False
    return inflate.eof, verified


def _schema(connection):
    """('message', chat expression joins) for the modern schema, ('messages', ...) for the legacy
    one, else None."""
    tables = {r[0] for r in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if 'message' in tables and 'chat' in tables and 'jid' in tables:
        return 'message'
    if 'messages' in tables:
        return 'messages'
    return None


def _identities(db_path):
    """{message _id: identity}. The identity is (key_id, from_me) for a key_id of 16 or more
    characters, else (chat jid, key_id, from_me, timestamp); a LID chat jid is mapped through
    jid_map when present."""
    connection = open_sqlite_db_readonly(db_path)
    if not connection:
        return {}
    try:
        table = _schema(connection)
        if table == 'message':
            tables = {r[0] for r in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            mapped, joins = 'jid.raw_string', ''
            if 'jid_map' in tables:
                mapped = 'COALESCE(mapped.raw_string, jid.raw_string)'
                joins = ('LEFT JOIN jid_map ON jid_map.lid_row_id=jid._id '
                         'LEFT JOIN jid AS mapped ON mapped._id=jid_map.jid_row_id')
            rows = connection.execute(f'''SELECT message._id, {mapped}, message.key_id, message.from_me,
                message.timestamp FROM message LEFT JOIN chat ON chat._id=message.chat_row_id
                LEFT JOIN jid ON jid._id=chat.jid_row_id {joins}''').fetchall()
        elif table == 'messages':
            rows = connection.execute('''SELECT _id, key_remote_jid, key_id, key_from_me, timestamp
                FROM messages''').fetchall()
        else:
            rows = []
    except sqlite3.Error as error:
        logfunc(f'WhatsApp backups: messages of {db_path} not read: {error}')
        rows = []
    finally:
        connection.close()
    out = {}
    for row_id, chat, key_id, from_me, timestamp in rows:
        key_id = str(key_id or '')
        out[row_id] = (key_id, from_me) if len(key_id) >= 16 else (chat, key_id, from_me, timestamp)
    return out


def _last_message(db_path):
    connection = open_sqlite_db_readonly(db_path)
    try:
        table = _schema(connection)
        return connection.execute(f'SELECT MAX(timestamp) FROM {table}').fetchone()[0] if table else None
    except sqlite3.Error:
        return None
    finally:
        connection.close()


_TEMP = []


def _temp():
    if not _TEMP:
        _TEMP.append(tempfile.mkdtemp(prefix='lexis_whatsapp_'))
        atexit.register(shutil.rmtree, _TEMP[0], True)
    return _TEMP[0]


def _shadow(source, name, drop):
    """A temporary copy of a decrypted backup without the messages whose _id is in drop, named
    msgstore.db so the standard functions open it. Triggers are dropped first so deleting
    touches nothing else."""
    folder = os.path.join(_temp(), name)
    os.makedirs(folder, exist_ok=True)
    target = os.path.join(folder, 'msgstore.db')
    shutil.copyfile(source, target)
    connection = sqlite3.connect(target)
    try:
        table = _schema(connection)
        for (trigger,) in connection.execute("SELECT name FROM sqlite_master WHERE type='trigger'").fetchall():
            connection.execute('DROP TRIGGER "{}"'.format(trigger.replace('"', '""')))
        if table and drop:
            connection.execute('CREATE TEMP TABLE gone (id INTEGER PRIMARY KEY)')
            connection.executemany('INSERT INTO gone VALUES (?)', ((i,) for i in drop))
            connection.execute(f'DELETE FROM {table} WHERE _id IN (SELECT id FROM gone)')
        connection.commit()
    finally:
        connection.close()
    return target


# -- backups of one app data directory -----------------------------------------------------

_PREPARED = {}


def _out_base(context):
    return pathlib.Path(context.get_report_folder()).parents[1] / _OUT


def _prepared(context, tenant, package, files):
    """The backups of one app data directory, newest first, each a dict with the decryption
    outcome and, when decrypted, the shadow databases 'present', 'missing' (not in the active
    database) and 'new' (not in the active database nor in a newer backup)."""
    out_base = _out_base(context)
    backups = [f for f in files if _BACKUP.match(os.path.basename(f))]
    cache_key = (str(out_base), tenant, package, tuple(sorted(backups)))
    if cache_key in _PREPARED:
        return _PREPARED[cache_key]
    every_key_file = [str(f) for f in context.get_files_found() if os.path.basename(str(f)) in _KEYS
                      and _container(context.get_relative_path(str(f)))]
    own = [f for f in every_key_file if f in files]
    keys = _keys(own + [f for f in every_key_file if f not in own])
    active = wa._find([f for f in files if not _BACKUP.match(os.path.basename(f))], 'msgstore.db')  # pylint: disable=protected-access
    active_ids = set(_identities(active).values()) if active else set()

    records, seen_content = [], set()
    for path in backups:
        try:
            size = os.path.getsize(path)
            with open(path, 'rb') as handle:
                head = handle.read(1024)
        except OSError as error:
            logfunc(f'WhatsApp backups: could not read {path}: {error}')
            continue
        if (size, head) in seen_content:     # another storage view of the same backup
            continue
        seen_content.add((size, head))
        name = os.path.basename(path)
        record = {'path': path, 'name': name, 'format': _BACKUP.match(name).group(1), 'size': size,
                  'decrypted': 'No', 'key': '', 'tag': '', 'db': None, 'copy': '', 'note': '', 'last': None,
                  'messages': '', 'missing': '', 'merged': ''}
        records.append(record)
        if not keys:
            record['note'] = 'No WhatsApp key file (files/key, files/encrypted_backup.key) in the extraction'
            continue
        found = next(((k, c) for k in keys for c in _candidates(head) if _opens(path, k[1], *c)), None)
        if not found:
            record['note'] = ('No key in the extraction opens it; an end-to-end encrypted backup needs '
                              'its password or 64-digit key')
            continue
        (key_file, key), (iv, offset) = found
        digest = hashlib.sha1(path.encode('utf-8', 'surrogatepass')).hexdigest()[:8]
        folder = out_base / f'{package}_{tenant}'
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / f'{name}_{digest}.db'
        complete, verified = _decrypt(path, key, iv, offset, target)
        record.update(decrypted='Yes' if complete else 'Yes, truncated', key=key_file,
                      tag='Yes' if verified else 'No', db=str(target),
                      copy=f'{_OUT}/{folder.name}/{target.name}', last=_last_message(str(target)))
        if not complete:
            record['note'] = 'The compressed stream ends early: the backup file is incomplete'

    seen = set(active_ids)
    decrypted = sorted((r for r in records if r['db']), key=lambda r: r['last'] or 0, reverse=True)
    for index, record in enumerate(decrypted):
        identities = _identities(record['db'])
        missing = {i for i, k in identities.items() if k not in active_ids}
        new = {i for i in missing if identities[i] not in seen}
        seen |= set(identities.values())
        stem = f'{package}_{tenant}_{index}'
        record.update(messages=len(identities), missing=len(missing), merged=len(new), shadows={
            'present': _shadow(record['db'], stem + '_present', missing),
            'missing': _shadow(record['db'], stem + '_missing', set(identities) - missing),
            'new': _shadow(record['db'], stem + '_new', set(identities) - new)})
        if not active:
            record['note'] = 'No active msgstore.db in the extraction: every message counts as not in it'
    records.sort(key=lambda r: (r['db'] is None, -(r['last'] or 0), r['name']))
    _PREPARED[cache_key] = records
    return records


# -- the replaced artifacts ----------------------------------------------------------------

def _per_app(func, context, recovered):
    """Run a standard function once per app data directory; with recovered, add the messages
    of that directory's backups absent from its active database and from newer backups."""
    headers, rows, sources = None, [], []
    for (tenant, package), files in _groups(context):
        headers, found, source = func.__wrapped__(_Files(context, files))
        account, label = _account(files, package), _label(tenant, package)
        extra = (account, label) + (('',) if recovered else ())
        rows += [tuple(row) + extra for row in found]
        sources += str(source or '').split('\n')
        if not recovered:
            continue
        for record in _prepared(context, tenant, package, files):
            if not record.get('shadows'):
                continue
            _, found, _ = func.__wrapped__(_Files(context, [record['shadows']['new']] + files))
            note = f"Deleted: not in the active database; recovered from backup {record['name']}"
            rows += [tuple(row) + (account, label, note) for row in found]
            if found:
                sources.append(record['path'])
        # Deleted messages still in the active database's pages, unless a backup already gave them.
        backups = [r['db'] for r in _prepared(context, tenant, package, files) if r.get('db')]
        try:
            carved, carved_sources = carving.recovered(lambda fs: func.__wrapped__(_Files(context, fs)), files,
                                                       [r[:-3] for r in rows], context, others=backups)
        except Exception as ex:  # pylint: disable=broad-exception-caught
            logfunc(f'LEXIS carving: {func.__name__}: {ex}')
            carved, carved_sources = [], []
        rows += [tuple(row) + (account, label, carving.note(level, path)) for row, level, path in carved]
        sources += carved_sources
    if headers is None:
        headers = func.__wrapped__(_Files(context, []))[0]
    headers = tuple(headers) + ('Account ID', 'App') + (('Recovered',) if recovered else ())
    return _with_content(headers, rows) + ('\n'.join(dict.fromkeys(s for s in sources if s)),)


# -- message types and content -------------------------------------------------------------

# Labels for the message_type values the standard artifacts show as numbers, from third-party
# catalogs (whacodes.py of b16f00t/whapa, utility.py of KnugiHK/Whatsapp-Chat-Exporter); no
# WhatsApp source.
_TYPE_LABELS = {4: 'Contact Card', 13: 'GIF', 14: 'Contact Cards', 15: 'Deleted For Everyone', 20: 'Sticker',
                42: 'View Once Picture', 43: 'View Once Video', 64: 'Deleted By Admin', 66: 'Poll',
                82: 'View Once Audio', 99: 'Album'}
_VIEW_ONCE = {42: 'View once picture', 43: 'View once video', 82: 'View once audio'}
_SEP = '\x1f'
assert 'ELSE message.message_type' in wa._MESSAGE_TYPE_CASE  # pylint: disable=protected-access
# The label carries the row's content after a unit separator; _with_content splits them again.
wa._MESSAGE_TYPE_CASE = (  # pylint: disable=protected-access
    'COALESCE(CAST(' + wa._MESSAGE_TYPE_CASE.replace(  # pylint: disable=protected-access
        'ELSE message.message_type',
        ''.join(f'WHEN message.message_type={k} THEN "{v}" ' for k, v in _TYPE_LABELS.items())
        + 'ELSE message.message_type')
    + " AS TEXT), '') || char(31) || COALESCE(lexis_content(message._id), '')")


def _utc(ms):
    when = wa._ms_to_utc(ms)  # pylint: disable=protected-access
    return when.strftime('%Y-%m-%d %H:%M:%S UTC') if when else ''


def _vcard(text):
    """(name, phone numbers) of a vCard: its FN and TEL values, folded lines joined."""
    lines = re.sub(r'\r?\n[ \t]', '', str(text or '')).splitlines()
    name = next((l.split(':', 1)[1].strip() for l in lines if re.match(r'(?i)^(?:[\w-]+\.)?FN[;:]', l) and ':' in l), '')
    tels = [l.split(':', 1)[1].strip() for l in lines if re.match(r'(?i)^(?:[\w-]+\.)?TEL[;:]', l) and ':' in l]
    return name, [t for t in tels if t]


def _contents(connection):
    """{message _id: what the row was}: deleted, view once, poll options, contact cards, edited. A
    table or column the database lacks contributes nothing."""
    tables = {n for (n,) in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    out = {}

    def columns(table):
        return {c[1] for c in connection.execute(f'PRAGMA table_info("{table}")')} if table in tables else set()

    def add(row_id, text):
        out.setdefault(row_id, []).append(text)

    types = dict(connection.execute(f'SELECT _id, message_type FROM message WHERE message_type IN '
                                    f'(15, 64, {", ".join(map(str, _VIEW_ONCE))})')) if 'message' in tables else {}
    revoked = columns('message_revoked')
    if 'message_row_id' in revoked:
        admin, when = 'admin_jid_row_id' in revoked, 'revoke_timestamp' in revoked
        for row_id, admin_jid, at in connection.execute(
                f"SELECT r.message_row_id, {'j.raw_string' if admin else 'NULL'}, "
                f"{'r.revoke_timestamp' if when else 'NULL'} FROM message_revoked r"
                + (' LEFT JOIN jid j ON j._id = r.admin_jid_row_id' if admin else '')):
            by_admin = admin_jid or types.get(row_id) == 64
            text = f'Deleted by admin {admin_jid}'.strip() if by_admin else 'Deleted for everyone'
            add(row_id, text + (f' at {_utc(at)}' if _utc(at) else ''))
    for row_id, kind in types.items():
        if kind in _VIEW_ONCE:
            add(row_id, _VIEW_ONCE[kind])
        elif row_id not in out:
            add(row_id, 'Deleted by admin' if kind == 64 else 'Deleted for everyone')
    if {'message_row_id', 'option_name'} <= columns('message_poll_option'):
        polls = {}
        votes = 'vote_total' in columns('message_poll_option')
        for row_id, option, total in connection.execute(
                f"SELECT message_row_id, option_name, {'vote_total' if votes else 'NULL'} "
                'FROM message_poll_option ORDER BY message_row_id, rowid'):
            polls.setdefault(row_id, []).append(f'{option} ({total})' if total is not None else str(option))
        for row_id, options in polls.items():
            add(row_id, 'Poll: ' + ', '.join(options))
    if {'message_row_id', 'vcard'} <= columns('message_vcard'):
        for row_id, card in connection.execute('SELECT message_row_id, vcard FROM message_vcard ORDER BY rowid'):
            name, tels = _vcard(card)
            add(row_id, ' '.join(p for p in ('Contact card:', name, ', '.join(tels)) if p))
    if 'message_row_id' in columns('message_edit_info'):
        edited = 'edited_timestamp' in columns('message_edit_info')
        for row_id, at in connection.execute(
                f"SELECT message_row_id, {'edited_timestamp' if edited else 'NULL'} FROM message_edit_info"):
            add(row_id, f'Edited at {_utc(at)}' if _utc(at) else 'Edited')
    return {k: '; '.join(v) for k, v in out.items()}


_BASE_OPEN = wa._open_msgstore  # pylint: disable=protected-access


def _open_msgstore(files_found):
    """The standard opener, with lexis_content(_id) registered on the connection."""
    db, cursor, source, wa_db = _BASE_OPEN(files_found)
    if db:
        try:
            contents = _contents(db)
        except sqlite3.Error as ex:
            logfunc(f'LEXIS WhatsApp: message content not read: {ex}')
            contents = {}
        db.create_function('lexis_content', 1, lambda row_id: contents.get(row_id, ''), deterministic=True)
    return db, cursor, source, wa_db


wa._open_msgstore = _open_msgstore  # pylint: disable=protected-access


def _with_content(headers, rows):
    """Split the Message Type value into the label and a Content column right after it."""
    names = [h if isinstance(h, str) else h[0] for h in headers]
    if 'Message Type' not in names:
        return tuple(headers), rows
    i = names.index('Message Type')

    def split(row):
        label, _, content = str(row[i] if row[i] is not None else '').partition(_SEP)
        value = int(label) if label.lstrip('-').isdigit() else (label or None)
        return tuple(row[:i]) + (value, content) + tuple(row[i + 1:])
    return tuple(headers[:i + 1]) + ('Content',) + tuple(headers[i + 1:]), [split(r) for r in rows]


def _message_rows(where):
    """Timestamp, direction, chat, sender of the messages a join selects (aliases m, ch, cj, sj)."""
    return f'''SELECT m.timestamp, m.from_me, COALESCE(NULLIF(ch.subject, ''), cj.raw_string), sj.raw_string, {where}
        LEFT JOIN chat ch ON ch._id = m.chat_row_id
        LEFT JOIN jid cj ON cj._id = ch.jid_row_id
        LEFT JOIN jid sj ON sj._id = m.sender_jid_row_id'''


def _party(row):
    return (wa._ms_to_utc(row[0]), 'Outgoing' if row[1] == 1 else 'Incoming', row[2],  # pylint: disable=protected-access
            'Self' if row[1] == 1 else (row[3] or row[2]))


def get_whatsapp_contact_cards(context):
    db, cursor, source, _ = wa._open_msgstore(context.get_files_found())  # pylint: disable=protected-access
    rows = []
    if db:
        if {'message_row_id', 'vcard'} <= {c[1] for c in cursor.execute('PRAGMA table_info(message_vcard)')}:
            for row in cursor.execute(_message_rows('v.vcard FROM message_vcard v JOIN message m ON '
                                                    'm._id = v.message_row_id') + ' ORDER BY m.timestamp, v.rowid'):
                name, tels = _vcard(row[4])
                rows.append(_party(row) + (name, ', '.join(tels), row[4]))
        db.close()
    return ((('Message Timestamp', 'datetime'), 'Direction', 'Chat', 'Sender', 'Name', 'Phone Numbers', 'vCard'),
            rows, source)


def get_whatsapp_view_once(context):
    db, cursor, source, _ = wa._open_msgstore(context.get_files_found())  # pylint: disable=protected-access
    rows = []
    if db:
        media = {c[1] for c in cursor.execute('PRAGMA table_info(message_media)')} >= {'message_row_id', 'file_path'}
        sql = _message_rows(('mm.file_path' if media else 'NULL') + ', m.message_type FROM message m'
                            + (' LEFT JOIN message_media mm ON mm.message_row_id = m._id' if media else ''))
        for row in cursor.execute(sql + f' WHERE m.message_type IN ({", ".join(map(str, _VIEW_ONCE))}) '
                                        'ORDER BY m.timestamp'):
            rows.append(_party(row) + (_TYPE_LABELS[row[5]], wa._media(row[4]), row[4] or ''))  # pylint: disable=protected-access
        db.close()
    return ((('Message Timestamp', 'datetime'), 'Direction', 'Chat', 'Sender', 'Type', ('Media', 'media'),
             'Local Path To Media'), rows, source)


for _func in (get_whatsapp_contact_cards, get_whatsapp_view_once):
    _func.__wrapped__ = _func   # _per_app runs func.__wrapped__, as for the standard artifacts


def _paths(paths, backups):
    out = list(paths)
    for path in paths:
        business = path.replace('/com.whatsapp/', '/com.whatsapp.w4b/').replace('/WhatsApp/', '/WhatsApp Business/')
        if business != path:
            out.append(business)
    return tuple(dict.fromkeys(out + list(_PREFS_PATHS) + (list(_BACKUP_PATHS) if backups else [])))


_MERGED = ('get_whatsapp_messages', 'get_whatsapp_one_to_one_messages', 'get_whatsapp_group_messages')
_NOTE = (' LEXIS: this module replaces the artifact with the same key and name. It runs once per app '
         'data directory of WhatsApp (com.whatsapp) and WhatsApp Business (com.whatsapp.w4b), per Android '
         'user, and adds Account ID (+ the cc and ph values of the app preferences, else the package '
         'name) and App.')
_MERGE_NOTE = (' Messages of the local backups (msgstore*.db.crypt12/14/15) decrypted with a key file of '
               'the extraction are added when neither the active msgstore.db nor a newer backup holds them; '
               'Recovered then names the backup. A message is matched by key_id and from_me when key_id has '
               '16 or more characters, else by chat jid, key_id, from_me and timestamp, so a weak match counts '
               'as present and is never called deleted. Deleted messages still in the pages of the active '
               'msgstore.db are then carved (lexis_carving) and added when no backup holds them; Recovered gives '
               'the reliability.')
_TYPED = ('get_whatsapp_one_to_one_messages', 'get_whatsapp_group_messages', 'get_whatsapp_channel_messages')
_SOURCES_NOTE = ('The labels for message_type ' + ', '.join(map(str, _TYPE_LABELS)) + ' come from third-party '
                 'catalogs (whacodes.py of b16f00t/whapa, utility.py of KnugiHK/Whatsapp-Chat-Exporter), not from a '
                 'WhatsApp source, and were verified on synthetic databases only.')
_TYPES_NOTE = (' LEXIS also labels message_type ' + ', '.join(f'{k} ({v})' for k, v in _TYPE_LABELS.items())
               + '. ' + _SOURCES_NOTE + ' Content says what the row was, from the tables that record it: '
               'message_revoked (deleted for everyone, or by an admin, and when), types 15 and 64 without such a row, '
               'view-once types 42, 43 and 82, message_poll_option (options and vote totals), message_vcard (name '
               'and numbers of each contact card) and message_edit_info (edited, and when). A table or column the '
               'database lacks adds nothing. Message stays the stored text_data.')

__artifacts_v2__ = {}
for _module in (wa, wax):
    for _key, _info in _module.__artifacts_v2__.items():
        if _key == 'get_whatsapp_user_profile':
            continue
        _info = copy.deepcopy(_info)
        _info['paths'] = _paths(_info['paths'], _key in _MERGED)
        _info['notes'] = (_info.get('notes', '') + _NOTE + (_MERGE_NOTE if _key in _MERGED else '')
                          + (_TYPES_NOTE if _key in _TYPED else ''))
        _info['author'] = f"{_info['author']}, @gk-forensics"
        _info['last_update_date'] = '2026-10-22'
        __artifacts_v2__[_key] = _info


def _replacement(func, recovered):
    def run(context):
        return _per_app(func, context, recovered)
    run.__name__ = run.__qualname__ = func.__name__   # the name decides the artifact and its LAVA table
    return artifact_processor(run)


for _module in (wa, wax):
    for _key in _module.__artifacts_v2__:
        if _key in __artifacts_v2__:
            globals()[_key] = _replacement(getattr(_module, _key), _key in _MERGED)

_NEW_INFO = {'author': '@gk-forensics', 'creation_date': '2026-10-22', 'last_update_date': '2026-10-22',
             'requirements': 'none', 'category': 'WhatsApp', 'output_types': 'standard',
             'paths': _paths(('*/com.whatsapp/databases/msgstore.db*',), True)}
__artifacts_v2__['get_whatsapp_contact_cards'] = dict(
    _NEW_INFO, name='WhatsApp - Contact Cards', artifact_icon='user',
    description='Contact cards (vCards) shared in WhatsApp chats: name and phone numbers of each card, with the '
                'chat, the sender and the time of the message',
    notes='One row per message_vcard row, joined to its message. Name is the vCard FN value and Phone Numbers '
          'its TEL values; vCard is the stored text. Verified on synthetic databases only.' + _NOTE + _MERGE_NOTE)
__artifacts_v2__['get_whatsapp_view_once'] = dict(
    _NEW_INFO, name='WhatsApp - View Once Media', artifact_icon='eye',
    description='View-once pictures, videos and voice messages: chat, sender, time and the media file when the '
                'extraction still holds it',
    notes='Messages of message_type 42, 43 and 82. ' + _SOURCES_NOTE + _NOTE + _MERGE_NOTE)
for _func in (get_whatsapp_contact_cards, get_whatsapp_view_once):
    globals()[_func.__name__] = _replacement(_func, True)


# -- the backup artifacts ------------------------------------------------------------------

_BACKUP_INFO = {'author': '@gk-forensics', 'creation_date': '2026-10-22', 'last_update_date': '2026-10-22',
                'requirements': 'pycryptodome', 'category': 'WhatsApp', 'output_types': 'standard'}
_DECRYPT_NOTE = ('Backups are msgstore*.db.crypt12, .crypt14 and .crypt15 files in the WhatsApp and WhatsApp '
                 'Business Databases folders. Keys are the last 32 bytes of files/key (crypt12, crypt14) and of '
                 'files/encrypted_backup.key through HKDF-SHA256 (crypt15). Every key is tried with each IV the '
                 'backup header holds; a backup counts as decrypted only when the result inflates to an SQLite '
                 'database, and the AES-GCM tag after the compressed data is then checked (GCM Tag Verified). '
                 'A backup protected by a password or by a 64-digit key absent from the extraction is listed '
                 'as not decrypted. The decrypted copy is written to the report folder, never to the evidence.')

__artifacts_v2__['get_whatsapp_backups'] = dict(
    _BACKUP_INFO, name='WhatsApp Backups', artifact_icon='archive',
    description='Local WhatsApp and WhatsApp Business backups (msgstore*.db.crypt12/14/15), decrypted when a key '
                'file of the extraction opens them',
    notes=_DECRYPT_NOTE + ' Messages Not In Active Database counts the backup messages the active msgstore.db '
                          'does not hold; Merged counts those added to the WhatsApp message artifacts, i.e. '
                          'also absent from every newer backup.',
    paths=('*/com.whatsapp/databases/msgstore.db*', '*/com.whatsapp.w4b/databases/msgstore.db*')
    + _PREFS_PATHS + _BACKUP_PATHS)

for _key, _base, _name in (('get_whatsapp_backup_one_to_one_messages', 'get_whatsapp_one_to_one_messages',
                            'WhatsApp Backups - One To One Messages'),
                           ('get_whatsapp_backup_group_messages', 'get_whatsapp_group_messages',
                            'WhatsApp Backups - Group Messages')):
    __artifacts_v2__[_key] = dict(
        _BACKUP_INFO, name=_name, artifact_icon='message',
        description=f'Every message of each decrypted WhatsApp backup, as {wa.__artifacts_v2__[_base]["name"]} '
                    'reports them, with the backup and whether the active database still holds it',
        notes=_DECRYPT_NOTE + ' In Active Database is No when the active msgstore.db holds no message with the '
                              'same identity (key_id and from_me, or chat jid, key_id, from_me and timestamp for '
                              'a key_id shorter than 16 characters).',
        paths=__artifacts_v2__[_base]['paths'],
        data_views=copy.deepcopy(wa.__artifacts_v2__[_base].get('data_views', {})))


@artifact_processor
def get_whatsapp_backups(context):
    rows, sources = [], []
    for (tenant, package), files in _groups(context):
        account, label = _account(files, package), _label(tenant, package)
        for record in _prepared(context, tenant, package, files):
            rows.append((wa._ms_to_utc(record['last']), context.get_relative_path(record['path']),  # pylint: disable=protected-access
                         label, account, record['format'], record['size'], record['decrypted'],
                         context.get_relative_path(record['key']) if record['key'] else '', record['tag'],
                         record['messages'], record['missing'], record['merged'], record['copy'], record['note']))
            sources.append(record['path'])
    headers = (('Last Message In Backup', 'datetime'), 'Backup File', 'App', 'Account ID', 'Format',
               'Size (bytes)', 'Decrypted', 'Key File', 'GCM Tag Verified', 'Messages',
               'Messages Not In Active Database', 'Merged Into WhatsApp Messages', 'Decrypted Copy', 'Note')
    return headers, rows, '\n'.join(sources)


def _backup_messages(func, context):
    headers, rows, sources = None, [], []
    for (tenant, package), files in _groups(context):
        label = _label(tenant, package)
        for record in _prepared(context, tenant, package, files):
            if not record.get('shadows'):
                continue
            backup = f"{label}: {record['name']}"
            for kind, in_active in (('present', 'Yes'), ('missing', 'No')):
                headers, found, _ = func.__wrapped__(_Files(context, [record['shadows'][kind]] + files))
                rows += [tuple(row) + (backup, in_active) for row in found]
            sources.append(record['path'])
    if headers is None:
        headers = func.__wrapped__(_Files(context, []))[0]
    return _with_content(tuple(headers) + ('Backup', 'In Active Database'), rows) + ('\n'.join(sources),)


@artifact_processor
def get_whatsapp_backup_one_to_one_messages(context):
    return _backup_messages(wa.get_whatsapp_one_to_one_messages, context)


@artifact_processor
def get_whatsapp_backup_group_messages(context):
    return _backup_messages(wa.get_whatsapp_group_messages, context)
