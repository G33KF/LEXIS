__artifacts_v2__ = {
    "weChatMessages": {
        "name": "WeChat - Messages",
        "description": "Chat messages from the WeChat (Weixin) iOS client, with the body as stored",
        "author": "@AlexisBrignoni, Codex",
        "creation_date": "2026-09-22",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "WeChat",
        "notes": "On the two devices tested, messages were held in tables named Chat_ followed by "
                 "32 hex characters, spread across MM.sqlite and message_N.sqlite in the "
                 "account's DB folder. Every such table in those databases is read. On the two "
                 "devices tested the message body was in the clear, so the Message column carries "
                 "the text as stored; a row whose type is not text carries the client's own "
                 "payload for that type instead, also as stored. Rows whose WCDB_CT_Message column "
                 "is 2 hold the body as a zstd frame: a frame made without a dictionary is "
                 "decompressed, and a frame made with one is reported as a placeholder naming the "
                 "dictionary id, because the dictionary is not stored in the database or the app "
                 "container. Dictionaries 3 and 5 have been seen on rows of many types (text, "
                 "images, voice, links, locations and transfers among them). For such a row, "
                 "when the client's search index (fts/fts_message.db) holds a document for the "
                 "same contact and MesLocalID, Message carries the indexed text and Message Source "
                 "is 'search index'; otherwise Message Source is 'message table'. The indexed text "
                 "is what the client stored for search, not the decompressed body; for types other "
                 "than 1 what it holds was not established. A dictionary frame with no index "
                 "document stays undecoded, so locations and transfers sent in chats may be "
                 "missing here. CreateTime is the stored column "
                 "read as Unix seconds in UTC; whether it marks sending, receipt or storage is "
                 "not established. Direction is the Des column and is reported as stored, because "
                 "no source for the mapping of its two values was established from the closed "
                 "source client; it held 0 and 1 on both devices. Type is the client's message "
                 "type integer, reported as stored: the text messages held type 1, and the other "
                 "values are reported as stored, without a mapping. Chat Partner is the Friend "
                 "table userName whose MD5 equals the 32 characters in the table name, and Chat "
                 "Partner Name is that row's nickname where the contact store holds one. A "
                 "message table with no matching Friend row is still reported, with the id and "
                 "name blank; on one tested device this left 21 of 131 rows without a resolved "
                 "name. The cells are also blank when no contact database was found for the "
                 "account folder. Field mapping was done against two private samples; no sample "
                 "data is recorded for them. The account id is the MD5 folder under Documents, "
                 "and every account folder in the extraction is read. Original contribution: @AlexisBrignoni, Claude.",
        "paths": ('*/Documents/*/DB/message_*.sqlite*',
                  '*/Documents/*/DB/MM.sqlite*',
                  '*/Documents/*/DB/WCDB_Contact.sqlite*',
                  '*/Documents/*/fts/fts_message.db*'),
        "output_types": "standard",
        "artifact_icon": "message-circle"
    },
    "weChatRecoveredMessages": {
        "name": "WeChat - Recovered Messages",
        "description": "WeChat (Weixin) iOS messages no longer returned by the message tables, read from older page versions, free space and the client's search index",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "WeChat",
        "notes": "Nothing is written to the databases. Three places are read. (1) Every older "
                 "version of the leaf pages of the Chat_ tables in MM.sqlite and message_N.sqlite: "
                 "the main file's copy of a page the write-ahead log has replaced, and every frame "
                 "of the log, including frames from before the last checkpoint. A cell whose "
                 "MesLocalID is no longer in its table is 'row no longer in table'; a cell whose "
                 "MesLocalID is still there with a different Message is 'earlier version of a live "
                 "row'. Versions that differ from the live row only in other columns (Status, "
                 "MesSvrID) are not reported. (2) The free space of those pages (the unallocated "
                 "gap and the freeblock chain), where a record is recognised by the table's column "
                 "count and declared types; one whose row id was overwritten is 'deleted cell, row "
                 "id lost'. Cells that parse but hold no CreateTime, Type or Message are dropped. "
                 "(3) fts/fts_message.db, the client's message search index: a document whose "
                 "contact and MesLocalID match no row of any Chat_ table is 'search index entry, row "
                 "no longer in table', with the indexed text, its CreateTime and the contact; the "
                 "index holds no direction, type or server id, so those cells are blank. Recovered "
                 "From names the page version or the index. A recovered body that is a zstd frame "
                 "needing a dictionary stays a placeholder, as in WeChat - Messages. Validated on a "
                 "private iOS extraction. A recovered row records that the client once stored the "
                 "record; it does not establish when or why it was removed, or that the account "
                 "read it. Direction, Type, Chat Partner and Chat Partner Name are read as in "
                 "WeChat - Messages; Local ID and Server ID are MesLocalID and MesSvrID as stored.",
        "paths": ('*/Documents/*/DB/message_*.sqlite*',
                  '*/Documents/*/DB/MM.sqlite*',
                  '*/Documents/*/DB/WCDB_Contact.sqlite*',
                  '*/Documents/*/fts/fts_message.db*'),
        "output_types": "standard",
        "artifact_icon": "message-circle"
    },
    "weChatContacts": {
        "name": "WeChat - Contacts",
        "description": "Contacts recorded by the WeChat (Weixin) iOS client",
        "author": "@AlexisBrignoni, Claude",
        "creation_date": "2026-09-22",
        "last_update_date": "2026-09-22",
        "requirements": "none",
        "category": "WeChat",
        "notes": "Read from the Friend table of WCDB_Contact.sqlite. A row is a contact record "
                 "the client holds. On the two devices tested the table also held WeChat's own "
                 "official accounts (WeChat-Team, File Transfer, New Friends and the like), so a "
                 "row is not by itself evidence that the account communicated with that contact. "
                 "WeChat ID is the userName column. Nickname is read from the first protobuf "
                 "field of the dbContactRemark blob, which held the contact's display name on "
                 "every row checked against a known official account (the WeChat-Team, File "
                 "Transfer and New Friends accounts each resolved to their published name). The "
                 "other fields of that blob are not decoded here, because no source for their "
                 "meaning in the closed source client was established. Type is reported as "
                 "stored. Field mapping was done against two private samples; no sample data is "
                 "recorded for them.",
        "paths": ('*/Documents/*/DB/WCDB_Contact.sqlite*',),
        "output_types": "standard",
        "artifact_icon": "users"
    },
    "weChatMoments": {
        "name": "WeChat - Moments",
        "description": "Moments (timeline) posts cached by the WeChat (Weixin) iOS client",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "WeChat",
        "notes": "Read from wc/wc005_008.db in the account folder: the Buffer column of "
                 "MyWC_Timeline01, of the per-contact MyWC01_ tables, of WCTogether_ tables and of "
                 "timeline_micro_merchant_feed01. Every Buffer is a protobuf with no published "
                 "schema; the fields were mapped against one private iOS sample and no sample data "
                 "is recorded. A post cached in more than one table is reported once, keyed by its "
                 "id. Author is field 5, which equalled the row's FromUser column on every row of "
                 "that sample; Author Name is field 6. Created is "
                 "field 7 read as Unix seconds in UTC; it was the only field in the Unix range on "
                 "every row, and whether it marks posting or caching is not established. Text is "
                 "field 23. Content Type is field 5 of the content object, reported as stored "
                 "(rows with a video attachment held 15, rows with photos 1). Title, Description "
                 "and Link are fields 1, 2 and 3 of the content object. Media URLs are the first "
                 "URL of each attachment in the content object's attachment list; the files are "
                 "the client's remote URLs and no cached copy is linked. Longitude and Latitude "
                 "are the two doubles at fields 39.2 and 39.3; the assignment of each to latitude "
                 "or longitude rests on their ranges "
                 "in that sample, not on a published source. Place holds the strings of field 19 "
                 "as stored. A cached post is not by itself evidence that the account viewed it. "
                 "Likes and comments were not present in this store on the sample: "
                 "MyWC_Message01 and MyWC_Stranger_Message were empty, so none are reported.",
        "paths": ('*/Documents/*/wc/wc*.db*',),
        "output_types": "all",
        "artifact_icon": "image"
    },
}

import hashlib
import os
import re
import struct

from scripts.ilapfuncs import (artifact_processor, get_sqlite_db_records, convert_unix_ts_to_utc)
from scripts import blackboxprotobuf
from scripts.sqlite_carver import carve

try:
    from compression import zstd  # Python 3.14+
except ImportError:
    zstd = None

CHAT_TABLE = re.compile(r'^Chat_[0-9a-f]{32}$')
# Chat_<md5>: the 32 hex after the underscore is MD5(userName).
CHAT_MD5 = re.compile(r'^Chat_([0-9a-f]{32})')


def _account_dir(path):
    """The .../Documents/<md5> account directory a WeChat store sits in."""
    found = re.match(r'(?P<root>.*/Documents/[0-9a-f]{32})/', path.replace('\\', '/'))
    if found:
        return found.group('root')
    return os.path.dirname(os.path.dirname(path.replace('\\', '/')))


ZSTD_MAGIC = bytes.fromhex('28b52ffd')


def _body(value):
    """A message body as text. WCDB stores some bodies as zstd frames (WCDB_CT_Message 2): frames
    made without a dictionary are decompressed; the others name the dictionary they need."""
    if not isinstance(value, (bytes, bytearray)):
        return value or ''
    value = bytes(value)
    if not value.startswith(ZSTD_MAGIC) or len(value) < 6:
        return value.decode('utf-8', 'replace')
    if zstd is not None:
        try:
            return zstd.decompress(value).decode('utf-8', 'replace')
        except zstd.ZstdError:
            pass
    descriptor = value[4]
    size = (0, 1, 2, 4)[descriptor & 3]
    start = 5 if descriptor & 0x20 else 6
    dict_id = int.from_bytes(value[start:start + size], 'little') if size else 0
    return f'[zstd-compressed body, dictionary {dict_id}: not decoded]'


def _tables(db_path):
    return [row[0] for row in get_sqlite_db_records(
        db_path, "SELECT name FROM sqlite_master WHERE type='table'")]


def _nickname(blob):
    """The display name WeChat packs in the first field of dbContactRemark."""
    if not blob:
        return ''
    try:
        decoded, _ = blackboxprotobuf.decode_message(bytes(blob))
    except Exception:  # pylint: disable=broad-exception-caught
        return ''
    value = decoded.get('1')
    if isinstance(value, (bytes, bytearray)):
        try:
            return bytes(value).decode('utf-8')
        except UnicodeDecodeError:
            return ''
    return ''


def _friends_by_md5(contact_path):
    """MD5(userName) -> (userName, nickname), for linking a Chat table to a contact."""
    by_md5 = {}
    if not contact_path:
        return by_md5
    for row in get_sqlite_db_records(contact_path, 'SELECT userName, dbContactRemark FROM Friend'):
        user = row[0]
        if not user:
            continue
        by_md5[hashlib.md5(user.encode('utf-8')).hexdigest()] = (user, _nickname(row[1]))
    return by_md5


def _stores(context):
    """Group WeChat message databases and the contact store by account directory."""
    grouped = {}
    for file_found in context.get_files_found():
        file_found = str(file_found)
        name = os.path.basename(file_found.replace('\\', '/'))
        entry = grouped.setdefault(_account_dir(file_found), {})
        if name == 'WCDB_Contact.sqlite':
            entry['contact'] = file_found
        elif name == 'MM.sqlite' or re.match(r'message_\d+\.sqlite$', name):
            entry.setdefault('messages', []).append(file_found)
        elif name == 'fts_message.db':
            entry['fts'] = file_found
    return grouped


def _search_index(fts_path):
    """(MD5(userName), MesLocalID) -> (text, CreateTime, userName) from the client's message search index."""
    docs = {}
    if not fts_path:
        return docs
    tables = set(_tables(fts_path))
    if 'fts_username_id' not in tables:
        return docs
    users = dict(tuple(r) for r in get_sqlite_db_records(fts_path, 'SELECT usernameid, UsrName FROM fts_username_id'))
    for n in range(4):
        if f'fts5_message_table_{n}_content' not in tables:
            continue
        for text, created, local_id, user_id in get_sqlite_db_records(
                fts_path, f'SELECT c0, c1, c2, c5 FROM fts5_message_table_{n}_content'):
            user = users.get(user_id)
            if user and local_id is not None:
                docs[(hashlib.md5(user.encode('utf-8')).hexdigest(), local_id)] = (text or '', created, user)
    return docs


UNDECODED = '[zstd-compressed body'


@artifact_processor
def weChatMessages(context):
    data_headers = (
        ('CreateTime', 'datetime'),
        'Direction (as stored)',
        'Type (as stored)',
        'Message',
        'Message Source',
        'Chat Partner',
        'Chat Partner Name',
        'Source File',
    )
    data_list = []
    source_paths = []

    for _, entry in sorted(_stores(context).items()):
        message_dbs = entry.get('messages') or []
        if not message_dbs:
            continue
        friends = _friends_by_md5(entry.get('contact'))
        index = _search_index(entry.get('fts'))
        from_index = 0
        for db_path in sorted(message_dbs):
            rows = 0
            for table in _tables(db_path):
                found = CHAT_MD5.match(table)
                if not found or not CHAT_TABLE.match(table):
                    continue
                user, nickname = friends.get(found.group(1), ('', ''))
                query = f'SELECT CreateTime, Des, Type, Message, MesLocalID FROM "{table}"'
                for row in get_sqlite_db_records(db_path, query):
                    message, source = _body(row[3]), 'message table'
                    doc = index.get((found.group(1), row[4]))
                    if message.startswith(UNDECODED) and doc and doc[0]:
                        message, source = doc[0], 'search index'
                        from_index += 1
                    data_list.append((
                        convert_unix_ts_to_utc(row[0]) if row[0] else '',
                        row[1] if row[1] is not None else '',
                        row[2] if row[2] is not None else '',
                        message,
                        source,
                        user,
                        nickname,
                        context.get_relative_path(db_path),
                    ))
                    rows += 1
            if rows:
                source_paths.append(db_path)
        if from_index:
            source_paths.append(entry['fts'])

    return data_headers, data_list, '\n'.join(source_paths)


@artifact_processor
def weChatRecoveredMessages(context):
    data_headers = (
        ('CreateTime', 'datetime'),
        'Recovered As',
        'Recovered From',
        'Direction (as stored)',
        'Type (as stored)',
        'Message',
        'Chat Partner',
        'Chat Partner Name',
        'Local ID',
        'Server ID',
        'Source File',
    )
    data_list = []
    source_paths = []

    for _, entry in sorted(_stores(context).items()):
        message_dbs = entry.get('messages') or []
        if not message_dbs:
            continue
        friends = _friends_by_md5(entry.get('contact'))
        index = _search_index(entry.get('fts'))
        live, recovered = set(), set()
        for db_path in sorted(message_dbs):
            current = {}
            for table in filter(CHAT_TABLE.match, _tables(db_path)):
                for local_id, message in get_sqlite_db_records(db_path, f'SELECT MesLocalID, Message FROM "{table}"'):
                    current[(table, local_id)] = message
                    live.add((table[5:], local_id))
            rows = 0
            for record in carve(db_path, CHAT_TABLE.match):
                v, table, local_id = record['values'], record['table'], record['rowid']
                # Cells that parse under the schema but carry no time, type or body are not messages.
                if None in (v['CreateTime'], v['Type'], v['Message']):
                    continue
                if (table, local_id) in current:
                    if current[(table, local_id)] == v['Message']:
                        continue  # an older version that differs only in status or server id
                    state = 'earlier version of a live row'
                else:
                    state = 'row no longer in table' if local_id is not None else 'deleted cell, row id lost'
                if (table, local_id, v['CreateTime'], v['Message']) in recovered:
                    continue
                recovered.add((table, local_id, v['CreateTime'], v['Message']))
                user, nickname = friends.get(table[5:], ('', ''))
                data_list.append((
                    convert_unix_ts_to_utc(v['CreateTime']),
                    state,
                    f"{record['origin']}, page {record['page']}" + (' (truncated)' if record['truncated'] else ''),
                    v['Des'] if v['Des'] is not None else '',
                    v['Type'],
                    _body(v['Message']),
                    user,
                    nickname,
                    local_id if local_id is not None else '',
                    v['MesSvrID'] if v['MesSvrID'] is not None else '',
                    context.get_relative_path(db_path),
                ))
                rows += 1
            if rows:
                source_paths.append(db_path)
        live |= {(table[5:], local_id) for table, local_id, _, _ in recovered}
        rows = 0
        for (md5, local_id), (text, created, user) in sorted(index.items(), key=lambda d: d[1][1] or 0):
            if (md5, local_id) in live:
                continue
            data_list.append((
                convert_unix_ts_to_utc(created) if created else '',
                'search index entry, row no longer in table',
                'fts_message.db',
                '',
                '',
                text,
                user,
                friends.get(md5, ('', ''))[1],
                local_id,
                '',
                context.get_relative_path(entry['fts']),
            ))
            rows += 1
        if rows:
            source_paths.append(entry['fts'])

    return data_headers, data_list, '\n'.join(source_paths)


@artifact_processor
def weChatContacts(context):
    data_headers = (
        'WeChat ID',
        'Nickname',
        'Type (as stored)',
        'Source File',
    )
    data_list = []
    source_paths = []

    for _, entry in sorted(_stores(context).items()):
        contact_path = entry.get('contact')
        if not contact_path:
            continue
        rows = 0
        for row in get_sqlite_db_records(contact_path,
                                         'SELECT userName, dbContactRemark, type FROM Friend'):
            if not row[0]:
                continue
            data_list.append((
                row[0],
                _nickname(row[1]),
                row[2] if row[2] is not None else '',
                context.get_relative_path(contact_path),
            ))
            rows += 1
        if rows:
            source_paths.append(contact_path)

    return data_headers, data_list, '\n'.join(source_paths)


FEED_TABLE = re.compile(r'^(MyWC_Timeline01|MyWC01_[0-9a-f]{32}|WCTogether_[0-9a-f]{32}|timeline_micro_merchant_feed01)$')


def _varint(buf, i):
    value = shift = 0
    while True:
        byte = buf[i]
        i += 1
        value |= (byte & 0x7f) << shift
        shift += 7
        if byte < 0x80:
            return value, i


def _fields(buf):
    """{field: [value, ...]} of a protobuf message, or {} when buf is not one."""
    out, i = {}, 0
    try:
        while i < len(buf):
            key, i = _varint(buf, i)
            field, wire = key >> 3, key & 7
            if wire == 0:
                value, i = _varint(buf, i)
            elif wire == 1:
                value = struct.unpack('<d', buf[i:i + 8])[0]
                i += 8
            elif wire == 5:
                value = buf[i:i + 4]
                i += 4
            elif wire == 2:
                size, i = _varint(buf, i)
                value = buf[i:i + size]
                if len(value) != size:
                    return {}
                i += size
            else:
                return {}
            out.setdefault(field, []).append(value)
    except (IndexError, struct.error):
        return {}
    return out if i == len(buf) else {}


def _first(fields, number, default=None):
    return fields.get(number, [default])[0]


def _text(value):
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode('utf-8', 'replace')
    return ''


def _attachments(buf):
    """The attachment list: varint-length-prefixed protobuf messages, one per attachment."""
    out, i = [], 0
    try:
        while i < len(buf):
            size, i = _varint(buf, i)
            out.append(_fields(buf[i:i + size]))
            i += size
    except IndexError:
        pass
    return out


@artifact_processor
def weChatMoments(context):
    data_headers = (
        ('Created', 'datetime'),
        'Author',
        'Author Name',
        'Text',
        'Content Type (as stored)',
        'Title',
        'Description',
        'Link',
        'Media Count',
        'Media URLs',
        'Latitude',
        'Longitude',
        'Place (as stored)',
        'Post ID',
        'Source File',
    )
    data_list = []
    source_paths = []
    seen = set()

    for file_found in sorted(map(str, context.get_files_found())):
        if not re.search(r'wc\d*_\d+\.db$', file_found):
            continue
        rows = 0
        for table in _tables(file_found):
            if not FEED_TABLE.match(table):
                continue
            for (buffer,) in get_sqlite_db_records(file_found, f'SELECT Buffer FROM "{table}"'):
                post = _fields(bytes(buffer or b''))
                post_id = _text(_first(post, 2))
                if not post or (file_found, post_id) in seen:
                    continue
                seen.add((file_found, post_id))
                content = _fields(_first(post, 18, b''))
                media = [m for raw in content.get(11, []) for m in _attachments(raw)]
                urls = [_text(_first(_fields(_first(m, 13, b'')), 1)) for m in media]
                where = _fields(_first(post, 39, b''))
                lon, lat = _first(where, 2, 0.0), _first(where, 3, 0.0)
                place = _fields(_first(post, 19, b''))
                created = _first(post, 7)
                data_list.append((
                    convert_unix_ts_to_utc(created) if created else '',
                    _text(_first(post, 5)),
                    _text(_first(post, 6)),
                    _text(_first(post, 23)),
                    _first(content, 5, ''),
                    _text(_first(content, 1)),
                    _text(_first(content, 2)),
                    _text(_first(content, 3)),
                    len(media),
                    '\n'.join(u for u in urls if u),
                    lat if lat or lon else '',
                    lon if lat or lon else '',
                    ', '.join(t for t in (_text(_first(place, n)) for n in range(1, 6)) if t),
                    post_id,
                    context.get_relative_path(file_found),
                ))
                rows += 1
        if rows:
            source_paths.append(file_found)

    return data_headers, data_list, '\n'.join(source_paths)
