"""LEXIS: Session Android messages. Session (network.loki.messenger) is a Signal fork whose message
database is SQLCipher-encrypted with a key held in the Android Keystore; the key comes from the
extraction's secrets.json, in the same form as Signal's, through a private copy of signalAndroid.py
pointed at the Session package. Without that entry nothing is decrypted."""
import hashlib
import importlib.util
import json
import os
import pathlib
import sqlite3
import tempfile

from scripts.ilapfuncs import artifact_processor, convert_unix_ts_to_utc, logfunc
from scripts.sqlcipher_decrypt import decrypt_sqlcipher_db


def _load(name):
    spec = importlib.util.spec_from_file_location(
        f'lexis_session_{name}_base', pathlib.Path(__file__).resolve().parents[1] / 'artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ses = _load('signalAndroid')
ses.SIGNAL_PACKAGE = PACKAGE = 'network.loki.messenger'

# SQLCipher settings per database era, from Session-Android (format only): session.db (1.29.0 to
# current) kdf_iter 1; signal_v4.db (1.18.5, 1.20.0) kdf_iter 256000; both SQLCipher 4 defaults
# (SHA-512); signal.db cipher_compatibility 3 (SHA-1). The file's own era is tried first; a setting is
# used only when its HMAC verifies.
_SETTINGS = {'session.db': ('sha512', 1), 'signal_v4.db': ('sha512', 256000), 'signal.db': ('sha1', 1)}
_cache = {}

_OUTGOING = {2, 21, 22, 23, 24, 25, 26, 28, 29, 30, 31}
_INCOMING = {1, 3, 5, 19, 20}
_DELETED = {19, 28}
_CALLS = {1: 'Incoming call', 2: 'Outgoing call', 3: 'Missed call', 5: 'Missed call'}
_FLAGS = ((0x80000, 'Group update'), (0x20000, 'Left group'), (0x40000, 'Disappearing messages timer changed'),
          (0x4000, 'Community invitation'), (0x2000, 'Screenshot taken'), (0x1000, 'Media saved'),
          (0x400000, 'Session reset'))


def _decrypted(context, path):
    """The decrypted copy of a Session database, or None when no key in secrets.json authenticates it."""
    if path in _cache:
        return _cache[path]
    _cache[path] = None
    keys = [pair[0] for pair in ses._signal_secrets(context) if pair[0]]  # pylint: disable=protected-access
    if not keys:
        logfunc('LEXIS Session: secrets.json has no "sqlite db key" entry for network.loki.messenger; '
                'the messages stay encrypted')
        return None
    first = _SETTINGS.get(os.path.basename(path))
    order = [first] + [s for s in _SETTINGS.values() if s != first] if first else list(_SETTINGS.values())
    digest = hashlib.sha1(path.encode('utf-8', 'replace')).hexdigest()[:12]
    output = os.path.join(tempfile.gettempdir(), 'aleapp_session', f'session_{digest}.db')
    os.makedirs(os.path.dirname(output), exist_ok=True)
    for key in keys:
        for hmac_algorithm, iterations in order:
            try:
                pages, verified = decrypt_sqlcipher_db(path, key, output, page_size=4096, kdf_iterations=iterations,
                                                       hmac_algorithm=hmac_algorithm, kdf_algorithm=hmac_algorithm)
            except Exception:  # pylint: disable=broad-exception-caught
                continue
            if pages and verified:
                if verified != pages:
                    logfunc(f'LEXIS Session: {pages - verified} of {pages} pages failed HMAC verification')
                _cache[path] = output
                return output
    logfunc(f'LEXIS Session: no key in secrets.json authenticated {os.path.basename(path)}')
    return None


def _rows(db, table, sql):
    try:
        return db.execute(sql).fetchall() if table in _tables(db) else []
    except sqlite3.Error as ex:
        logfunc(f'LEXIS Session: {table} not read: {ex}')
        return []


def _tables(db):
    return {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}


def _names(db):
    """{address or thread id: display name}: contacts, groups and communities, where the database has them."""
    names = {}
    for address, name in _rows(db, 'loki_user_display_name_database',
                               'SELECT hex_encoded_public_key, display_name FROM loki_user_display_name_database'):
        names[address] = name
    for address, name in _rows(db, 'recipient_settings', 'SELECT address, name FROM recipient_settings'):
        names[address] = name or names.get(address)
    for address, name, nickname in _rows(db, 'session_contact_database',
                                         'SELECT session_id, name, nickname FROM session_contact_database'):
        names[address] = nickname or name or names.get(address)
    for group, title in _rows(db, 'groups', 'SELECT group_id, title FROM groups'):
        names[group] = title
    for thread, chat in _rows(db, 'loki_public_chat_database', 'SELECT thread_id, public_chat FROM loki_public_chat_database'):
        try:
            names[('thread', thread)] = json.loads(chat).get('displayName')
        except (TypeError, ValueError, AttributeError):
            pass
    return {k: v for k, v in names.items() if v}


def _kind(stored):
    """(Direction, Deleted, Event) from the stored type: base type in the low 5 bits, flags above."""
    stored = stored or 0
    base = stored & 0x1F
    direction = 'Outgoing' if base in _OUTGOING else 'Incoming' if base in _INCOMING else 'Unknown'
    events = [_CALLS[base]] if base in _CALLS else []
    events += [label for bit, label in _FLAGS if stored & bit]
    return direction, 'Yes' if base in _DELETED else '', ', '.join(events)


def _messages(db, relative):
    threads = dict(_rows(db, 'thread', 'SELECT _id, recipient_ids FROM thread'))
    names = _names(db)
    parts = dict(_rows(db, 'part', 'SELECT mid, COUNT(*) FROM part GROUP BY mid'))
    queries = {
        'sms': ('date_sent', 'date', 'type', 'NULL', 'NULL'),
        'mms': ('date', 'date_received', 'msg_box', 'quote_author', 'quote_body'),
    }
    out = []
    for table, (sent, received, kind, quote_author, quote_body) in queries.items():
        columns = ses._table_columns(db, table) if table in _tables(db) else set()  # pylint: disable=protected-access
        if not columns:
            continue
        pick = ses._select_list(columns, 't', ['_id', 'thread_id', 'address', sent, received, kind, 'body',  # pylint: disable=protected-access
                                                'expires_in'])
        quote = ses._select_list(columns, 't', [c for c in (quote_author, quote_body) if c != 'NULL']) or 'NULL, NULL'  # pylint: disable=protected-access
        for row_id, thread, address, date_sent, date_received, stored, body, expires, q_author, q_body in _rows(
                db, table, f'SELECT {pick}, {quote} FROM {table} t ORDER BY 4'):
            direction, deleted, event = _kind(stored)
            chat_address = threads.get(thread) or address
            chat = names.get(('thread', thread)) or names.get(chat_address) or chat_address or ''
            sender = 'Local user' if direction == 'Outgoing' else names.get(address) or address or ''
            quoted = f'{names.get(q_author) or q_author}: {q_body}' if q_body else ''
            out.append((convert_unix_ts_to_utc(date_sent), convert_unix_ts_to_utc(date_received), chat, sender,
                        '' if direction == 'Outgoing' else address or '', direction, body or '', deleted, event,
                        parts.get(row_id, 0) if table == 'mms' else 0, quoted,
                        ses._expires_in_seconds(expires), thread, row_id, table, relative))  # pylint: disable=protected-access
    return out


@artifact_processor
def get_sessionMessages(context):
    data_list, sources, seen = [], [], set()
    for file_found in sorted(context.get_files_found()):
        name = os.path.basename(file_found)
        container, _ = ses._container(file_found)  # pylint: disable=protected-access
        if name not in _SETTINGS or not container or (container, name) in seen:
            continue
        seen.add((container, name))
        decrypted = _decrypted(context, file_found)
        if not decrypted:
            continue
        db = sqlite3.connect(f'file:{decrypted}?mode=ro', uri=True)
        try:
            data_list += _messages(db, context.get_relative_path(file_found))
        finally:
            db.close()
        sources.append(file_found)
    return ((('Date Sent', 'datetime'), ('Date Received', 'datetime'), 'Chat', 'Sender', 'Sender ID', 'Direction',
             'Message', 'Deleted', 'Event', 'Attachments', 'Quote', 'Expires In (s)', 'Thread ID', 'Message ID',
             'Table', 'Source File'), data_list, '\n'.join(sources))


__artifacts_v2__ = {'get_sessionMessages': {
    'name': 'Session - Messages',
    'description': 'Session messages from the sms and mms tables, decrypted with the key from secrets.json',
    'author': '@gk-forensics', 'creation_date': '2026-10-22', 'last_update_date': '2026-10-22',
    'requirements': 'none', 'category': 'Session Messenger',
    'notes': 'Requires an extra/Secrets/secrets.json entry for network.loki.messenger holding "sqlite db key", '
             'the form Signal - Messages reads; without it the database stays encrypted and nothing is reported. '
             'Format only, from Session-Android (dev a8011acb, tags 1.18.5, 1.20.0, 1.29.0): messages are in sms '
             'and mms of databases/session.db (signal_v4.db and signal.db in older versions; a migrated device may '
             'keep the old file, reported separately in Source File). The SQLCipher settings of each era are '
             'tried and used only when the HMAC verifies. Direction, Deleted (base types 19 and 28) and Event come '
             'from the stored type: base type in the low five bits, flags above. sms date_sent and mms date are the '
             'sent time, sms date and mms date_received the received time, all in milliseconds. Chat names come '
             'from session_contact_database (nickname first), recipient_settings, groups and the community '
             'displayName, where present; names kept only in libsession config blobs are not read, so Chat and '
             'Sender then show the Session ID or group address. Verified on synthetic data only.',
    'paths': tuple(f'*/{PACKAGE}/databases/{name}*' for name in _SETTINGS),
    'output_types': 'standard', 'artifact_icon': 'message-circle'}}
