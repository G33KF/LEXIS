"""LEXIS: "recover deleted messages" apps. They keep a copy of every messaging notification, so a
message deleted in WhatsApp or elsewhere can survive in their storage. Five open-source formats are
read exactly; in the other curated apps a generic detector reads the tables shaped like captured
notifications and marks their rows as heuristic. Apps outside the list are never read."""
import json
import pathlib
import re
import xml.etree.ElementTree as ET

from scripts.ilapfuncs import artifact_processor, convert_unix_ts_to_utc, logfunc, open_sqlite_db_readonly

# Play Store ids found by a search on 2026-10-22, unverified against an install; schemas unknown.
_CLOSED = (
    'com.drilens.wamr', 'color.dev.com.whatsremoved', 'color.dev.com.magenta', 'com.tenqube.notisave',
    'com.ikvaesolutions.notificationhistorylog', 'recovermessages.restoredeleteddata.recoverdata.notifyme',
    'recover.deleted.messages.business.messagerecovery', 'com.appsborn.whatsdelete.recover.deleted.messages.rdm',
    'com.xsdev.recover.deleted.messages', 'com.svprdga.notificationhistory', 'hexcoders.notisave',
    'com.noti.log.notifications.save', 'com.tactics.rdm', 'com.sg.wamrdeleted.unseen',
    'wamr.whatsrecovered.whatsdeleted.whatsremoved.unseen.recovery.recover.deleted.message',
    'com.app.batterysaver', 'com.notistar.app.notistar', 'com.notigist', 'com.notify.messagevault')
_OPEN = ('com.tiriig.whatsdeleted', 'bot.box.whatsdelete', 'com.test.recovermessages',
         'com.gmail.anubhavdas54.whatsdeleted', 'com.wassaver.app')
PACKAGES = _CLOSED + _OPEN
_LOCAL = ' (device local time)'

# Generic detector: column names, lower case, in order of preference.
_TEXT = ('message', 'msg', 'text', 'body', 'content', 'last_message', 'big_text', 'notification_text', 'ticker')
_TITLE = ('title', 'user', 'username', 'sender', 'name', 'conversation', 'contact', 'author', 'chat')
_PACKAGE = ('app', 'package', 'packagename', 'package_name', 'pkg', 'app_package')
_TIME = ('timestamp', 'time', 'datetime', 'date', 'when', 'posttime', 'post_time', 'whole_time', 'created_at')
_DELETED = ('isdeleted', 'is_deleted', 'deleted')


def _times(value):
    """(Timestamp (UTC), Time As Stored): an epoch in seconds or milliseconds becomes UTC; anything else
    is kept as stored, said to be the device's local time."""
    if isinstance(value, str) and value.strip().isdigit():
        value = int(value)
    if isinstance(value, (int, float)) and not isinstance(value, bool) and value > 1e9:
        return convert_unix_ts_to_utc(value), ''
    return None, f'{value}{_LOCAL}' if value not in (None, '') else ''


def _split_sender(text):
    sender, sep, rest = (text or '').partition(': ')
    return (sender, rest) if sep else ('', text)


def _deleted(value):
    return 'Yes' if value not in (None, '', 0, '0', False, 'false') else ''


def _jimale(db):
    for user, message, when, app, deleted, group in db.execute(
            'SELECT user, message, dateTime, app, isDeleted, isGroup FROM Chat ORDER BY dateTime'):
        sender, message = _split_sender(message) if group else (user, message)
        yield (*_times(when), app or '', user, sender, message, _deleted(deleted))


def _thebotbox(db):
    for name, last, when, chats in db.execute('SELECT name, last_message, time, allChats FROM chatRooms'):
        try:
            entries = json.loads(chats or '[]')
        except ValueError:
            entries = [{'message': last, 'time': when}]
        for entry in entries:
            yield (*_times(entry.get('time')), 'com.whatsapp', name, name, entry.get('message'),
                   _deleted(entry.get('isDeleted')))


def _smkhurramkhan(db):
    for package, user, msg, whole_time in db.execute(
            'SELECT package, username, msg, whole_time FROM messeges ORDER BY _id'):
        yield (None, f'{whole_time}{_LOCAL}' if whole_time else '', package or '', user, user, msg, '')


_EXACT_DB = {  # (package, table): (reader, label)
    ('com.tiriig.whatsdeleted', 'Chat'): (_jimale, 'WhatsDeleted (jimale) Chat table'),
    ('bot.box.whatsdelete', 'chatRooms'): (_thebotbox, 'WhatsDelete (TheBotBox) chatRooms.allChats'),
    ('com.test.recovermessages', 'messeges'): (_smkhurramkhan, 'Recover Deleted Messages (smkhurramkhan) messeges'),
}


def _text_log(path, source_app):
    """files/msgLog.txt: 'date | sender: message' per line; a line without ' | ' continues the message."""
    rows = []
    for line in pathlib.Path(path).read_text(encoding='utf-8', errors='replace').splitlines():
        date, sep, rest = line.partition(' | ')
        if sep:
            sender, message = _split_sender(rest)
            rows.append([None, f'{date}{_LOCAL}', source_app, sender, sender, message, ''])
        elif rows:
            rows[-1][5] += '\n' + line
    return [tuple(r) for r in rows]


def _wassaver(path):
    node = ET.parse(path).getroot().find("string[@name='captured_messages']")
    for item in json.loads(node.text or '[]') if node is not None else []:
        chat = item.get('groupName') if item.get('isGroup') else item.get('sender')
        yield (*_times(item.get('timestamp')), item.get('packageName') or '', chat or '', item.get('sender'),
               item.get('text'), '')


def _pick(columns, names):
    return next((columns[n] for n in names if n in columns), None)


def _generic(db, skip):
    """Rows of every table shaped like a captured notification: a text column and a title column."""
    for (table,) in db.execute("SELECT name FROM sqlite_master WHERE type = 'table'").fetchall():
        if table in skip or table.startswith('sqlite_') or table in ('android_metadata', 'room_master_table'):
            continue
        columns = {row[1].lower(): row[1] for row in db.execute(f'PRAGMA table_info("{table}")')}
        text, title = _pick(columns, _TEXT), _pick(columns, _TITLE)
        if not text or not title or text == title:
            continue
        extra = [_pick(columns, names) for names in (_PACKAGE, _TIME, _DELETED)]
        select = ', '.join(f'"{c}"' if c else 'NULL' for c in [title, text] + extra)
        method = f'Heuristic: {table} (text={text}, title={title})'
        for chat, message, package, when, deleted in db.execute(f'SELECT {select} FROM "{table}"'):
            yield (*_times(when), package or '', chat, '', message, _deleted(deleted)), method


def _package(path):
    return next((part for part in pathlib.PurePath(path).parts if part in PACKAGES), None)


def _is_sqlite(path):
    try:
        with open(path, 'rb') as f:
            return f.read(16) == b'SQLite format 3\x00'
    except OSError:
        return False


@artifact_processor
def get_recoveryAppsMessages(context):
    data_list, sources, seen = [], [], set()
    for file_found in sorted(context.get_files_found()):
        package, relative = _package(file_found), context.get_relative_path(file_found)
        key = re.sub(r'(^|/)data/user/0/', r'\1data/data/', relative.replace('\\', '/'))
        if not package or key in seen or pathlib.Path(file_found).is_dir():
            continue
        seen.add(key)
        rows = []
        try:
            if package == 'com.gmail.anubhavdas54.whatsdeleted' and file_found.lower().endswith('msglog.txt'):
                app = 'org.thoughtcrime.securesms' if file_found.endswith('signalMsgLog.txt') else 'com.whatsapp'
                rows = [(r, 'Exact: WhatsDeleted (TheCosmoNomad) text log') for r in _text_log(file_found, app)]
            elif package == 'com.wassaver.app' and file_found.endswith('wassaver_messages.xml'):
                rows = [(r, 'Exact: WASSaver captured_messages') for r in _wassaver(file_found)]
            elif _is_sqlite(file_found):
                db = open_sqlite_db_readonly(file_found)
                if db is None:
                    continue
                tables = {r[0] for r in db.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
                done = set()
                for (owner, table), (reader, label) in _EXACT_DB.items():
                    if owner == package and table in tables:
                        rows += [(r, f'Exact: {label}') for r in reader(db)]
                        done.add(table)
                rows += list(_generic(db, done))
                db.close()
        except Exception as ex:  # pylint: disable=broad-exception-caught
            logfunc(f'LEXIS Recovery Apps: {relative} not read: {ex}')
            continue
        for row, method in rows:
            data_list.append(row[:2] + (package,) + row[2:] + (method, relative))
        if rows:
            sources.append(file_found)
    return ((('Timestamp (UTC)', 'datetime'), 'Time As Stored', 'Capturing App', 'Source App', 'Chat', 'Sender',
             'Message', 'Marked Deleted', 'Method', 'Source File'), data_list, '\n'.join(sources))


__artifacts_v2__ = {'get_recoveryAppsMessages': {
    'name': 'Recovery Apps - Captured Messages',
    'description': 'Messaging notifications kept by "recover deleted messages" and notification log apps',
    'author': '@gk-forensics', 'creation_date': '2026-10-22', 'last_update_date': '2026-10-22',
    'requirements': 'none', 'category': 'Deleted Message Recovery Apps',
    'notes': 'These apps copy each notification as it arrives, so a message later deleted in the source app '
             'can remain here. Exact formats (format only, no code taken) from the GitHub sources of '
             'jimale/WhatsDeleted, TheBotBox/WhatsDelete, smkhurramkhan/Recover-Deleted-Messages, '
             'TheCosmoNomad/WhatsDeleted and PBhadoo/WASSaver-Android. For the other listed apps (Play Store '
             'package ids, unverified against an install) no schema is public: Method "Heuristic" names the table '
             'and the columns read because they look like a captured notification (a text and a title column). '
             'Time As Stored keeps a time saved as text, in the device\'s local time and locale, without '
             'converting it. Sender is blank where the app stores only the notification title. Marked Deleted is '
             'set only where the app records a deleted flag. Verified on synthetic data only.',
    'paths': tuple(f'*/{p}/{d}/*' for p in PACKAGES for d in ('databases', 'files', 'shared_prefs')),
    'output_types': 'standard', 'artifact_icon': 'trash-2'}}
