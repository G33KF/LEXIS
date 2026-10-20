__artifacts_v2__ = {
    "wechat_messages": {
        "name": "WeChat - Messages",
        "description": "Rows from the message table of the decrypted EnMicroMsg.db, each a "
                       "message with its conversation, direction, time and content",
        "author": "@AlexisBrignoni, Claude",
        "creation_date": "2026-08-30",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "com.tencent.mm stores its messages in EnMicroMsg.db, a SQLCipher database. The "
                 "key is the first seven characters of the lowercase MD5 of the device IMEI joined "
                 "to the account uin; no reference for that scheme is cited here, and the derived "
                 "key is confirmed only by the database opening with it. The account directory name "
                 "under MicroMsg is the MD5 of the literal mm joined to the account uin, so each "
                 "database is keyed with the uin that reproduces its directory name, chosen from "
                 "every integer stored under a name containing uin in the shared preferences of "
                 "the same app data directory (auth_info_key_prefs.xml holds the signed-in one); a "
                 "database whose uin is in none of them is logged and not read. The IMEI is "
                 "taken from the application's WLOGIN_DEVICE_INFO.xml, as stored and, where it is "
                 "stored as hex of printable text, as that text; the literal "
                 "1234567890ABCDEF is also tried; on the corpus below the database opened with the "
                 "literal rather than the stored IMEI, and why the application uses that literal "
                 "is not sourced here. The database is opened with the SQLCipher parameter set "
                 "this application uses, page size 1024, 4000 KDF iterations and HMAC off, and a "
                 "database that does not open with any of these keys is logged in the run log and no rows "
                 "are reported for it. No rows are reported either when the sqlcipher3 package is "
                 "not installed, so an empty result is not evidence the store held no messages. "
                 "createTime is Unix milliseconds. Is Send is the isSend column, reported as "
                 "stored; the Direction column shows Outgoing where isSend is 1 and Incoming for "
                 "every other stored value, including an empty one; no source for the meaning of "
                 "isSend is cited here, so read Is Send for the value as stored. Talker is the "
                 "talker column, reported as stored. A received row in a conversation whose talker "
                 "ends in @chatroom usually begins with an account id, a colon and a newline (voice, "
                 "video and sticker rows may not); that id is reported as Sender ID and "
                 "removed before Message is read, while Content (as stored) keeps it. For any "
                 "other received row Sender ID is the talker, and for a sent row it is empty. "
                 "Conversation Name and Sender are the contact remark, else the nickname, that "
                 "rcontact holds for the talker and the sender, falling back to the id. Message "
                 "Type (as stored) is the type column and is reported "
                 "as stored, no authoritative source for its full code list having been located. "
                 "The attachment lookup treats type 3 as an image row, type 34 as a voice row and "
                 "type 47 as a sticker row; no source for those three values is cited here. On the "
                 "corpus below type 1 was text and the other values carried XML documents. Message "
                 "is the readable form of the content column: for a plain text row it is that text "
                 "unchanged, and for a row holding a document it is the document's own title and "
                 "des elements, with Link taken from its url element. Those are the document's own "
                 "element names, read from it rather than inferred. Content Element names the "
                 "first element inside the root, so a document carrying no title still says what "
                 "kind it is. Content (as stored) keeps the raw value in every case, so nothing "
                 "the summary omits is lost. A row whose Content Element is img holds an image "
                 "reference and no text, so Message is empty on it by design rather than through a "
                 "failure to parse. A document that does not parse is logged and leaves Message, "
                 "Content Element and Link empty, with the stored column as the only record for "
                 "the row. Media, Attachment File, Attachment Format and Attachment Size are "
                 "resolved from names the database itself recorded for the row, never by matching "
                 "a file on size or time. An image row points at its thumbnail through the imgPath "
                 "column, a voice row is joined to voiceinfo on the message id to get its file "
                 "name, and a sticker row names its file directly; each name is then looked for "
                 "only inside the com.tencent.mm folder the row's database sits in, so a file of "
                 "the same name under another Android user's copy of the app is not picked up. Two "
                 "WeChat accounts inside one com.tencent.mm folder share the lookup, and the first "
                 "file of a given name is used. Media is checked in only where the resolved "
                 "thumbnail is a real image by its leading bytes. On the corpus below image rows "
                 "resolved to a JPEG thumbnail, which is rendered. Attachment Format is read from "
                 "the file's leading bytes, and the values seen were the reason the other files "
                 "are not rendered: the full size images are a wxgf container, the voice files "
                 "carry a SILK header despite their amr extension, and the sticker files matched "
                 "no known signature. Those three are reported by name, format and size so an "
                 "examiner knows the file exists and where, without this artifact claiming to have "
                 "decoded it. Image Path (as stored) keeps the raw imgPath token where the row has "
                 "one. Account ID and Account Name are the values of userinfo ids 2 and 4 in the "
                 "database the row was read from, so rows of two accounts stay apart; those ids are "
                 "read as the signed-in account's WeChat id and display name (see "
                 "WeChat - Account for the id mapping and its limits).",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml',
                  '*/com.tencent.mm/MicroMsg/*/image2/*',
                  '*/com.tencent.mm/MicroMsg/*/voice2/*',
                  '*/com.tencent.mm/MicroMsg/*/emoji/*'),
        "output_types": "standard",
        "artifact_icon": "message-circle",
        "data_views": {
            "conversation": {
                "conversationDiscriminatorColumn": "Talker",
                "textColumn": "Message",
                "directionColumn": "Direction",
                "directionSentValue": "Outgoing",
                "timeColumn": "Message Time",
                "senderColumn": "Sender",
                "conversationLabelColumn": "Conversation Name",
                "sentMessageStaticLabel": "Local User",
                "mediaColumn": "Media",
            }
        },
        "sample_data": {
            "kevin_pocox7_a15": "Android 15 | com.tencent.mm | 602 rows",
        },
    },
    "wechat_contacts": {
        "name": "WeChat - Contacts",
        "description": "Rows from the rcontact table of the decrypted EnMicroMsg.db, each a "
                       "contact record the database holds, with the WeChat id, nickname and any "
                       "remark",
        "author": "@AlexisBrignoni, Claude",
        "creation_date": "2026-08-30",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "rcontact holds the contact records the account keeps; on the corpus below "
                 "these included service and official accounts alongside personal contacts. Type "
                 "(as stored) and Verify Flag (as stored) are the columns that separate those, "
                 "and both are reported as stored so the distinction is preserved rather than "
                 "asserted: no authoritative source for their full code lists was located, and "
                 "on the corpus below most rows carried a type value that the service and "
                 "official accounts share. WeChat ID is the username column, Alias the alias "
                 "column, Nickname the nickname column and Remark the conRemark column, each "
                 "reported as stored. Whether a populated conRemark reflects any deliberate "
                 "interaction with the contact is not established here. createTime is Unix "
                 "milliseconds where present. Profile Picture is the file "
                 "avatar/<aa>/<bb>/user_hd_<md5>.png, else user_<md5>.png, beside the account "
                 "database, where <md5> is the MD5 of the username and <aa>, <bb> its first two "
                 "pairs of hex digits; it is reported only when its leading bytes are a JPEG, "
                 "PNG or GIF signature, since the name does not establish the format; a file can "
                 "hold JPEG bytes despite the .png name. The file is the copy the app cached, and when "
                 "it was cached is not established.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/MicroMsg/*/avatar/*/*/user_*.png',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "users",
        "sample_data": {
            "kevin_pocox7_a15": "Android 15 | com.tencent.mm | 32 rows",
        },
    },
    "wechat_conversations": {
        "name": "WeChat - Conversations",
        "description": "Rows from the rconversation table of the decrypted EnMicroMsg.db, each a "
                       "conversation the account holds, with its message count and the time and "
                       "digest of its last message",
        "author": "@AlexisBrignoni, Claude",
        "creation_date": "2026-08-30",
        "last_update_date": "2026-08-30",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "rconversation is one row per conversation. Username is the username column, "
                 "reported as stored. Message Count is the msgCount column and Unread Count the "
                 "unReadCount column, both as stored. Last Message Time is conversationTime as "
                 "Unix milliseconds. Digest is the digest column and Digest User the digestUser "
                 "column, both reported as stored; what the application writes into them is not "
                 "sourced here. Status (as stored) is the status column.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "message-square",
        "sample_data": {
            "kevin_pocox7_a15": "Android 15 | com.tencent.mm | 7 rows",
        },
    },
    "wechat_account": {
        "name": "WeChat - Account",
        "description": "The signed-in account, read from the userinfo table of the decrypted "
                       "EnMicroMsg.db and the account's own preference files",
        "author": "@AlexisBrignoni, Claude",
        "creation_date": "2026-08-30",
        "last_update_date": "2026-08-30",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "userinfo is a key and value table holding the signed-in account's own record. "
                 "The rows are reported with the numeric id the table uses and the value as "
                 "stored, except that a stored 0 or empty value is shown blank: on the corpus "
                 "below id 2 held the account's WeChat id, id 4 the display name and id 6 the "
                 "bound telephone number, but the id to field mapping is the application's own and "
                 "is not expanded here beyond reporting the id, since no authoritative source for "
                 "the full list was located. The account uin, the one that reproduces the database "
                 "directory name, "
                 "is reported alongside so the record can be tied to the database directory it "
                 "came from.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "user",
        "sample_data": {
            "kevin_pocox7_a15": "Android 15 | com.tencent.mm | 97 rows",
        },
    },
    "wechat_calls": {
        "name": "WeChat - Calls",
        "description": "Rows of the decrypted EnMicroMsg.db message table whose content is a "
                       "voipmsg document, each a call record shown in a conversation",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "Selected by message type 50, the type voipmsg documents were found to "
                 "carry; a row of that type holding a short plain string instead of a document is "
                 "reported with Call Summary taken from that string and the "
                 "document fields empty. Call Summary is the msg element of VoIPBubbleMsg as "
                 "stored, the text the application shows in the chat. Duration (as stored), Room "
                 "Type (as stored) and Missed Call (as stored) are the duration, room_type and "
                 "missed_call elements; no source for their units or codes is cited here, and "
                 "missed_call can be absent, so its absence is not "
                 "evidence a call was answered. Direction follows isSend as in WeChat - Messages. "
                 "Database decryption is described under WeChat - Messages. Validated on a private Android extraction.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "phone",
    },
    "wechat_payments": {
        "name": "WeChat - Transfers and Red Envelopes",
        "description": "Rows of the decrypted EnMicroMsg.db message table whose content carries "
                       "a wcpayinfo element, with the amount text, memo and parties the document "
                       "names",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "Selected by message types 419430449 and 436207665, the two types found to carry a "
                 "wcpayinfo element. Every value is read from the document's "
                 "own element names: Title and Description are title and des, Amount (as stored) "
                 "is feedesc, Memo is pay_memo, Payer and Receiver are payer_username and "
                 "receiver_username, Pay Subtype (as stored) is paysubtype and Transfer ID is "
                 "transferid; for type 436207665 rows Sender Title and Receiver Title are "
                 "sendertitle and receivertitle. A transfer can appear as two rows "
                 "with the same Transfer ID and different Pay Subtype; "
                 "what each subtype stands for is not sourced here. Record Status (as stored) and "
                 "Record Receive Time come from the RemittanceRecord row whose transferId equals "
                 "the Transfer ID. receiveTime can be 0 and is then left blank. WalletLuckyMoney "
                 "is left out: its receiveAmount was 0 on every row examined. Group messages carry the "
                 "member who sent them as described under WeChat - Messages. Validated on a private Android extraction.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "dollar-sign",
    },
    "wechat_locations": {
        "name": "WeChat - Shared Locations",
        "description": "Rows of the decrypted EnMicroMsg.db message table whose content is a "
                       "location document, with its coordinates, label and place name",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "Selected by message type 48. Latitude is the x attribute and Longitude the y "
                 "attribute of the location element. That order is data-proven rather than "
                 "sourced: x never exceeded 90 in magnitude on the data examined, and with x read "
                 "as latitude the rows fell close to WeChat Moments post locations, against none "
                 "the other way round. A coordinate is where the "
                 "shared pin was placed, which need not be where the device was. Label and Place "
                 "Name are the label and poiname attributes as stored. Validated on a private Android extraction.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "all",
        "artifact_icon": "map-pin",
    },
    "wechat_moments": {
        "name": "WeChat - Moments",
        "description": "Rows from the SnsInfo table of SnsMicroMsg.db, each a Moments post held "
                       "on the device, with its author, time, text and any location",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "SnsMicroMsg.db is plain SQLite. A row's content column is a protobuf message "
                 "decoded here without a schema, so its fields are named only where the data "
                 "examined supports it. Post Time and Author ID are the createTime (Unix seconds) and "
                 "userName columns; field 4 of the message equalled createTime on every row examined "
                 "and field 2 equalled userName on nearly every row. Text is field 5, decoded "
                 "as UTF-8. Latitude and Longitude are fields 2 and 1 of the field 6 message, "
                 "both 32-bit floats; the order is data-proven, field 1 exceeding 90 in magnitude "
                 "on most posts carrying coordinates. Location Text joins the text "
                 "subfields 3, 4 and 5 of that message in field order; which of them is a city, "
                 "a place name or an address is not asserted. Media Count is the number of "
                 "entries in field 5 of the field 8 message. Post Type (as stored) and Source "
                 "Type (as stored) are the type and sourceType columns. Author Name is resolved "
                 "from the decrypted EnMicroMsg.db contact table where it opens. The rows are what "
                 "the application cached, posts by the account and by its contacts alike, not a "
                 "complete history. Likes and comments are in WeChat - Moments Likes and "
                 "Comments. Not covered: the cached post images under the sns folder, which were "
                 "not in the extraction this was built on.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/SnsMicroMsg.db*',
                  '*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "all",
        "artifact_icon": "image",
    },
    "wechat_moments_reactions": {
        "name": "WeChat - Moments Likes and Comments",
        "description": "Entries of the two lists in the attrBuf column of the SnsInfo table of "
                       "SnsMicroMsg.db, one row per like or comment held for a cached Moments post",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "attrBuf is a protobuf message decoded without a schema; field names below rest on "
                 "a private Android extraction. Its fields 1, "
                 "2 and 4 equalled the row's snsId, userName and createTime on every row examined. "
                 "Fields 9 and 12 are lists of entries of one shape. Kind is Like for field 9 and "
                 "Comment for field 12: field 9 entries carry no text and type 1, field 12 entries "
                 "carry text on nearly all of them and type 2 (Entry Type, as stored, is that "
                 "field 4). Entries the "
                 "decoder could not read with this layout are logged, not reported. Rows from "
                 "several accounts' databases are listed together. Time is entry field 6 read as Unix seconds: on every entry it was at "
                 "or after the post's createTime and before the extraction. Actor ID is entry "
                 "field 1; Actor Name is the contact name from the decrypted EnMicroMsg.db where "
                 "it opens, else entry field 2 as stored. Text is entry field 5. Reply To ID is "
                 "entry field 9, present on some comments, each equal to another commenter or the "
                 "author of the same post. The lists hold what the application cached with the "
                 "post, not every like or comment ever made; the SnsComment table is not read.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/SnsMicroMsg.db*',
                  '*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "message-circle",
    },
    "wechat_group_members": {
        "name": "WeChat - Group Members",
        "description": "Members of each group in the chatroom table of the decrypted EnMicroMsg.db, "
                       "one row per member id in the memberlist column",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-15",
        "requirements": "sqlcipher3",
        "category": "WeChat",
        "notes": "Rows come from the semicolon-separated memberlist column; Group Owner marks the "
                 "member equal to the roomowner column. Group Name and Member Name are the "
                 "remark, else nickname, of the matching rcontact row, blank when the database "
                 "holds no contact record for the id. The roomdata column is a protobuf message "
                 "decoded without a schema: field 1 is a list of entries whose field 1 is a member "
                 "id. On the data examined every memberlist "
                 "id had one roomdata entry, memberCount equalled the list length in every "
                 "group, and the owner was in the list. Group Nickname is entry field 2 "
                 "as stored (text, present on some entries). Field 4 Account ID is entry field 4 as "
                 "stored: mostly an account id, often a member of the same group "
                 "or its owner; it is commonly read as the member who invited, which is not "
                 "established here. The list is the group membership the database held at "
                 "extraction, not a history of who joined or left. Validated on a private Android extraction.",
        "paths": ('*/com.tencent.mm/MicroMsg/*/EnMicroMsg.db*',
                  '*/com.tencent.mm/shared_prefs/*.xml'),
        "output_types": "standard",
        "artifact_icon": "users",
    },
}

import hashlib
import os
import re
import sqlite3
import struct
import xml.etree.ElementTree as ET

from google.protobuf.message import DecodeError

from scripts import blackboxprotobuf

from scripts.artifacts.storagePathViews import canonical_path, unique_files
from scripts.ilapfuncs import (
    artifact_processor,
    check_in_media,
    convert_unix_ts_to_utc,
    get_sqlite_db_path,
    logfunc,
    open_sqlite_db_readonly,
)

try:
    from sqlcipher3 import dbapi2 as sqlcipher
    _SQLCIPHER_AVAILABLE = True
except ImportError as _sqlcipher_import_error:
    # Guarded so a missing sqlcipher3 install disables only these artifacts rather
    # than aborting the import of every ALEAPP artifact, the pattern threema.py uses.
    _SQLCIPHER_AVAILABLE = False
    logfunc(f'WeChat: sqlcipher3 not available, encrypted-database artifacts '
            f'disabled: {_sqlcipher_import_error}')

SIDECARS = ('-wal', '-shm', '-journal')

# The parameter set this application's EnMicroMsg.db uses.
_CIPHER_PRAGMAS = (
    'PRAGMA cipher_page_size = 1024;',
    'PRAGMA kdf_iter = 4000;',
    'PRAGMA cipher_use_hmac = OFF;',
    'PRAGMA cipher_hmac_algorithm = HMAC_SHA1;',
    'PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_SHA1;',
)

# Substituted for the device IMEI by releases that can no longer read it.
_FALLBACK_IMEI = '1234567890ABCDEF'


_logged = set()


def _log_once(message):
    """Every WeChat artifact opens the same databases, so a finding is logged once a run."""
    if message not in _logged:
        _logged.add(message)
        logfunc(message)


def _prefs(path):
    """[(name, text)] for the entries of a preference file, one per item of a string set."""
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        _log_once(f'WeChat: could not parse {path}: {error}')
        return []
    entries = []
    for entry in root:
        name = entry.get('name') or ''
        if entry.tag == 'set':
            entries.extend((name, item.text or '') for item in entry)
        else:
            entries.append((name, entry.text if entry.tag == 'string' else entry.get('value', '')))
    return entries


def _uin_candidates(pref_files):
    """Every integer stored under a name containing uin in the app's preferences.

    The account a database belongs to is decided by its directory, the MD5 of mm joined to
    the uin, so a candidate is only used where it reproduces that name exactly; collecting
    widely costs nothing and lets the accounts other than the current one be opened too.
    """
    candidates = []
    for path in pref_files:
        for name, value in _prefs(path):
            value = (value or '').strip()
            if 'uin' in name.lower() and re.fullmatch(r'-?\d{1,20}', value) and value not in candidates:
                candidates.append(value)
    return candidates


def _imei_candidates(imei):
    """The stored IMEI, its text when it is stored hex encoded, and the fallback literal."""
    candidates = [imei]
    if imei and re.fullmatch(r'(?:[0-9a-fA-F]{2})+', imei):
        decoded = bytes.fromhex(imei)
        if all(32 < b < 127 for b in decoded):
            candidates.append(decoded.decode('ascii'))
    candidates.append(_FALLBACK_IMEI)
    return [c for i, c in enumerate(candidates) if c and c not in candidates[:i]]


def _db_key(imei, uin):
    """The EnMicroMsg.db key: first 7 chars of lowercase md5(imei + uin)."""
    return hashlib.md5(f'{imei}{uin}'.encode()).hexdigest()[:7]


def _open_database(db_path, uin, imeis):
    """Open EnMicroMsg.db with the key derived for this account, or None."""
    failures = []
    for imei in imeis:
        connection = None
        try:
            connection = sqlcipher.connect(  # pylint: disable=no-member
                f'file:{get_sqlite_db_path(db_path)}?mode=ro', uri=True)
            cursor = connection.cursor()
            cursor.execute(f"PRAGMA key = '{_db_key(imei, uin)}';")
            for pragma in _CIPHER_PRAGMAS:
                cursor.execute(pragma)
            cursor.execute('SELECT count(*) FROM sqlite_master;').fetchone()
            return connection
        except Exception as error:  # pylint: disable=broad-exception-caught
            failures.append(f'{imei!r}: {error}')
            if connection is not None:
                try:
                    connection.close()
                except Exception:  # pylint: disable=broad-exception-caught
                    pass
    _log_once(f'WeChat: {db_path} (uin {uin}) did not open with the key from any IMEI tried '
              f'({"; ".join(failures)})')
    return None


def _home(context, path):
    """The app data directory a file belongs to, the same key for every storage view of it."""
    return canonical_path(context.get_relative_path(_container_of(path)))[0]


def _connections(context):
    """Yield (connection, uin, db path) for each account database opened.

    The uin and IMEI are read from the same app data directory as the database, so a
    second Android user or a cloned app is keyed with its own preferences.
    """
    files_found = [str(f) for f in unique_files(context)]
    if not _SQLCIPHER_AVAILABLE:
        return
    for db_path in files_found:
        if os.path.basename(db_path) != 'EnMicroMsg.db':
            continue
        home = _home(context, db_path)
        prefs = [f for f in files_found if _home(context, f) == home
                 and os.path.basename(os.path.dirname(f)) == 'shared_prefs' and f.endswith('.xml')]
        directory = os.path.basename(os.path.dirname(db_path))
        uin = next((u for u in _uin_candidates(prefs)
                    if hashlib.md5(f'mm{u}'.encode()).hexdigest() == directory), None)
        if uin is None:
            _log_once(f'WeChat: {db_path} belongs to an account whose uin is in none of the '
                      f'preference files, so its key cannot be derived; skipping')
            continue
        imei = next((v for f in prefs if os.path.basename(f) == 'WLOGIN_DEVICE_INFO.xml'
                     for n, v in _prefs(f) if n == 'imei'), '')
        connection = _open_database(db_path, uin, _imei_candidates(imei))
        if connection is not None:
            yield connection, uin, db_path


def _read_head(path, length=16):
    try:
        with open(path, 'rb') as handle:
            return handle.read(length)
    except OSError as error:
        logfunc(f'WeChat: could not read {path}: {error}')
        return b''


def _query(context, sql, keep_path=False):
    """(rows, source paths) from every account database, closing each after.

    With keep_path the database each row came from is carried alongside it, so a
    caller resolving media can look only inside that row's own account container.
    """
    data_rows, source_paths = [], []
    for connection, uin, db_path in _connections(context):
        try:
            rows = connection.cursor().execute(sql).fetchall()
        except Exception as error:  # pylint: disable=broad-exception-caught
            logfunc(f'WeChat: query failed on {db_path}: {error}')
            rows = []
        finally:
            connection.close()
        source_paths.append(context.get_relative_path(db_path))
        for row in rows:
            data_rows.append((row, uin, db_path) if keep_path else (row, uin))
    return data_rows, source_paths



# Leading-byte signatures, checked because the name a message points to does not
# establish the format of the bytes behind it.
_SIGNATURES = (
    (b'\xff\xd8\xff', 'JPEG'),
    (b'\x89PNG\r\n\x1a\n', 'PNG'),
    (b'GIF87a', 'GIF'),
    (b'GIF89a', 'GIF'),
    (b'RIFF', 'RIFF container'),
    (b'wxgf', 'wxgf (WeChat image container)'),
    (b'\x02#!SILK_V3', 'SILK audio'),
    (b'#!SILK_V3', 'SILK audio'),
    (b'#!AMR', 'AMR audio'),
)


def _sniff(head):
    """A format label read from a file's leading bytes, or '' if unrecognised."""
    for magic, label in _SIGNATURES:
        if head.startswith(magic):
            return label
    return ''


def _media_index(files_found):
    """{(container, basename): path} for the media the seeker returned.

    Keyed on the account container as well as the name, so two accounts holding a
    file of the same name are never confused for one another.
    """
    index = {}
    for file_found in files_found:
        if os.path.isdir(file_found):
            continue
        parts = file_found.replace('\\', '/').split('/')
        container = ''
        for position in range(len(parts) - 1, -1, -1):
            if parts[position] == 'com.tencent.mm':
                container = '/'.join(parts[:position + 1])
                break
        index.setdefault((container, os.path.basename(file_found)), file_found)
    return index


def _container_of(path):
    parts = str(path).replace('\\', '/').split('/')
    for position in range(len(parts) - 1, -1, -1):
        if parts[position] == 'com.tencent.mm':
            return '/'.join(parts[:position + 1])
    return ''

def _readable_content(content):
    """(readable text, content element, link) for a message's content column.

    A plain text message is returned unchanged. Where the column holds a document,
    the values are read from that document's own element names rather than being
    inferred: title and des for the readable text, url for the link, and the name
    of the first element inside the root for the content element, so a document
    with no title still says what kind of document it is. A document that does not
    parse yields empty values and is logged, leaving the stored column as the only
    record for that row.
    """
    if not content:
        return '', '', ''
    stripped = content.lstrip()
    if not stripped.startswith('<'):
        return content, '', ''
    try:
        root = ET.fromstring(stripped)
    except ET.ParseError as error:
        logfunc(f'WeChat: message content did not parse as XML: {error}')
        return '', '', ''
    first = next(iter(root), None)
    element = first.tag if first is not None else root.tag

    def value(tag):
        node = root.find(f'./{element}/{tag}')
        if node is None:
            node = root.find(f'.//{tag}')
        return (node.text or '').strip() if node is not None and node.text else ''

    title, description, link = value('title'), value('des'), value('url')
    if title and description:
        readable = f'{title} - {description}'
    else:
        readable = title or description
    return readable, element, link


# A group conversation's received rows begin with the sending member's id, a colon
# and a newline, ahead of the body.
_GROUP_SENDER = re.compile(r'([^\s:<]{1,64}):\n')


def _split_sender(talker, is_send, content):
    """(sending member id, body); the id is '' unless a received group row carries one."""
    content = content or ''
    if is_send != 1 and (talker or '').endswith('@chatroom'):
        match = _GROUP_SENDER.match(content)
        if match:
            return match.group(1), content[match.end():]
    return '', content


def _contact_names(context):
    """{username: remark, else nickname} from every account database."""
    rows, _ = _query(context, 'SELECT username, conRemark, nickname FROM rcontact')
    return {username: remark or nickname or ''
            for (username, remark, nickname), _ in rows if username}


def _xml_root(body):
    stripped = (body or '').strip()
    if not stripped.startswith('<'):
        return None
    try:
        return ET.fromstring(stripped)
    except ET.ParseError as error:
        logfunc(f'WeChat: message content did not parse as XML: {error}')
        return None


def _ms_time(value):
    return convert_unix_ts_to_utc(value / 1000) if value else ''


_HEAD_HEADERS = ('Direction', 'Sender', 'Conversation Name', 'Talker')


def _message_rows(context, types):
    """(head, body, msg id, type) per message of the given types, oldest first.

    head is (time, direction, sender, conversation name, talker): the sender is the
    group member a received group row names, else the talker for a received row,
    else empty for a sent one.
    """
    names = _contact_names(context)
    rows, source_paths = _query(context, f'''
        SELECT createTime, isSend, talker, content, msgId, type FROM message
        WHERE type IN ({','.join(str(t) for t in types)}) ORDER BY createTime
    ''')
    result = []
    for (created, is_send, talker, content, msg_id, kind), _ in rows:
        member, body = _split_sender(talker, is_send, content)
        sender = member or ('' if is_send == 1 else talker or '')
        head = (_ms_time(created), 'Outgoing' if is_send == 1 else 'Incoming',
                names.get(sender) or sender, names.get(talker) or talker or '', talker or '')
        result.append((head, body, msg_id if msg_id is not None else '', kind))
    return result, source_paths

@artifact_processor
def wechat_messages(context):
    data_list = []
    files_found = [str(f) for f in unique_files(context)]
    index = _media_index(files_found)
    voice_names = {}
    accounts = {}
    for connection, _, db_path in _connections(context):
        try:
            accounts[db_path] = dict(connection.cursor().execute(
                'SELECT id, value FROM userinfo WHERE id IN (2, 4)').fetchall())
            for local_id, file_name in connection.cursor().execute(
                    'SELECT MsgLocalId, FileName FROM voiceinfo'):
                voice_names[(_container_of(db_path), local_id)] = file_name
        except Exception as error:  # pylint: disable=broad-exception-caught
            logfunc(f'WeChat: could not read voiceinfo or userinfo from {db_path}: {error}')
        finally:
            connection.close()

    rows, source_paths = _query(context, '''
        SELECT createTime, isSend, talker, type, content, imgPath, status, msgId
        FROM message ORDER BY createTime
    ''', keep_path=True)
    names = _contact_names(context)
    for row, _, db_path in rows:
        created, is_send, talker, kind, content, img_path, status, msg_id = row
        member, body = _split_sender(talker, is_send, content)
        sender = member or ('' if is_send == 1 else talker or '')
        readable, element, link = _readable_content(body)
        container = _container_of(db_path)

        # Every lookup below uses a name the database itself recorded for the row,
        # never a match on size or time.
        thumb = full = attachment = ''
        token = (img_path or '').split('://')[-1]
        if kind == 3 and token:
            thumb = index.get((container, token), '')
            full = index.get((container, f"{token.replace('th_', '', 1)}.jpg"), '')
        elif kind == 47 and token:
            attachment = index.get((container, token), '')
        elif kind == 34:
            name = voice_names.get((container, msg_id))
            if name:
                attachment = index.get((container, f'msg_{name}.amr'), '')
        attachment = attachment or full

        media = ''
        if thumb:
            head = _read_head(thumb)
            if _sniff(head) in ('JPEG', 'PNG', 'GIF'):
                media = check_in_media(thumb, os.path.basename(thumb))

        if attachment:
            attach_name = os.path.basename(attachment)
            attach_format = _sniff(_read_head(attachment))
            try:
                attach_size = os.path.getsize(attachment)
            except OSError:
                attach_size = ''
        else:
            attach_name = attach_format = attach_size = ''
        account = accounts.get(db_path, {})

        data_list.append((
            convert_unix_ts_to_utc(created / 1000) if created else '',
            'Outgoing' if is_send == 1 else 'Incoming',
            names.get(sender) or sender,
            names.get(talker) or talker or '',
            readable,
            media,
            talker or '',
            sender,
            is_send if is_send is not None else '',
            kind if kind is not None else '',
            element,
            link,
            attach_name,
            attach_format,
            attach_size,
            img_path or '',
            status if status is not None else '',
            msg_id if msg_id is not None else '',
            content or '',
            account.get(2) or '',
            account.get(4) or '',
        ))
    data_headers = (
        ('Message Time', 'datetime'),
        'Direction',
        'Sender',
        'Conversation Name',
        'Message',
        ('Media', 'media'),
        'Talker',
        'Sender ID',
        'Is Send',
        'Message Type (as stored)',
        'Content Element',
        'Link',
        'Attachment File',
        'Attachment Format',
        'Attachment Size',
        'Image Path (as stored)',
        'Status (as stored)',
        'Message ID',
        'Content (as stored)',
        'Account ID',
        'Account Name',
    )
    return data_headers, data_list, '\n'.join(source_paths)


def _avatar(found, db_path, username):
    """Media ref of the cached profile picture for username, or ''."""
    digest = hashlib.md5(username.encode('utf-8'), usedforsecurity=False).hexdigest()
    folder = os.path.join(os.path.dirname(db_path), 'avatar', digest[:2], digest[2:4])
    for name in (f'user_hd_{digest}.png', f'user_{digest}.png'):
        path = os.path.normcase(os.path.join(folder, name))
        if path in found and _sniff(_read_head(found[path])) in ('JPEG', 'PNG', 'GIF'):
            return check_in_media(found[path], name)
    return ''


@artifact_processor
def wechat_contacts(context):
    data_list = []
    found = {os.path.normcase(str(f)): str(f) for f in unique_files(context)}
    rows, source_paths = _query(context, '''
        SELECT username, alias, nickname, conRemark, type, verifyFlag, createTime
        FROM rcontact ORDER BY nickname
    ''', keep_path=True)
    for row, _, db_path in rows:
        username, alias, nickname, remark, kind, verify, created = row
        data_list.append((
            username or '',
            alias or '',
            nickname or '',
            remark or '',
            _avatar(found, db_path, username) if isinstance(username, str) and username else '',
            kind if kind is not None else '',
            verify if verify is not None else '',
            convert_unix_ts_to_utc(created / 1000) if created else '',
        ))
    data_headers = (
        'WeChat ID',
        'Alias',
        'Nickname',
        'Remark',
        ('Profile Picture', 'media'),
        'Type (as stored)',
        'Verify Flag (as stored)',
        ('Created', 'datetime'),
    )
    return data_headers, data_list, '\n'.join(source_paths)


@artifact_processor
def wechat_conversations(context):
    data_list = []
    rows, source_paths = _query(context, '''
        SELECT conversationTime, username, msgCount, unReadCount, digest, digestUser, status
        FROM rconversation ORDER BY conversationTime DESC
    ''')
    for row, _ in rows:
        conv_time, username, count, unread, digest, digest_user, status = row
        data_list.append((
            convert_unix_ts_to_utc(conv_time / 1000) if conv_time else '',
            username or '',
            count if count is not None else '',
            unread if unread is not None else '',
            digest or '',
            digest_user or '',
            status if status is not None else '',
        ))
    data_headers = (
        ('Last Message Time', 'datetime'),
        'Username',
        'Message Count',
        'Unread Count',
        'Digest',
        'Digest User',
        'Status (as stored)',
    )
    return data_headers, data_list, '\n'.join(source_paths)


@artifact_processor
def wechat_account(context):
    data_list = []
    rows, source_paths = _query(context, 'SELECT id, value FROM userinfo ORDER BY id')
    for row, uin in rows:
        row_id, value = row
        data_list.append((
            row_id if row_id is not None else '',
            value or '',
            uin,
        ))
    data_headers = ('Field ID (as stored)', 'Value', 'Account UIN')
    return data_headers, data_list, '\n'.join(source_paths)


def _find_text(root, path):
    return (root.findtext(path) or '').strip() if root is not None else ''


@artifact_processor
def wechat_calls(context):
    data_list = []
    rows, source_paths = _message_rows(context, (50,))
    for head, body, msg_id, _ in rows:
        root = _xml_root(body)
        summary = _find_text(root, './VoIPBubbleMsg/msg') if root is not None else body
        data_list.append(head + (
            summary,
            _find_text(root, './VoIPBubbleMsg/duration'),
            _find_text(root, './VoIPBubbleMsg/room_type'),
            _find_text(root, './VoIPBubbleMsg/missed_call'),
            msg_id,
        ))
    data_headers = (
        ('Call Time', 'datetime'),
        *_HEAD_HEADERS,
        'Call Summary',
        'Duration (as stored)',
        'Room Type (as stored)',
        'Missed Call (as stored)',
        'Message ID',
    )
    return data_headers, data_list, '\n'.join(source_paths)


@artifact_processor
def wechat_payments(context):
    data_list = []
    records = {}
    record_rows, _ = _query(context, 'SELECT transferId, receiveStatus, receiveTime FROM RemittanceRecord')
    for (transfer_id, status, receive_time), _ in record_rows:
        records[transfer_id] = (status if status is not None else '', _ms_time(receive_time))
    rows, source_paths = _message_rows(context, (419430449, 436207665))
    for head, body, msg_id, kind in rows:
        root = _xml_root(body)
        transfer_id = _find_text(root, './/wcpayinfo/transferid')
        status, receive_time = records.get(transfer_id, ('', '')) if transfer_id else ('', '')
        data_list.append(head + (
            _find_text(root, './appmsg/title'),
            _find_text(root, './appmsg/des'),
            _find_text(root, './/wcpayinfo/feedesc'),
            _find_text(root, './/wcpayinfo/pay_memo'),
            _find_text(root, './/wcpayinfo/payer_username'),
            _find_text(root, './/wcpayinfo/receiver_username'),
            _find_text(root, './/wcpayinfo/sendertitle'),
            _find_text(root, './/wcpayinfo/receivertitle'),
            _find_text(root, './/wcpayinfo/paysubtype'),
            transfer_id,
            status,
            receive_time,
            kind,
            msg_id,
        ))
    data_headers = (
        ('Message Time', 'datetime'),
        *_HEAD_HEADERS,
        'Title',
        'Description',
        'Amount (as stored)',
        'Memo',
        'Payer',
        'Receiver',
        'Sender Title',
        'Receiver Title',
        'Pay Subtype (as stored)',
        'Transfer ID',
        'Record Status (as stored)',
        ('Record Receive Time', 'datetime'),
        'Message Type (as stored)',
        'Message ID',
    )
    return data_headers, data_list, '\n'.join(source_paths)


@artifact_processor
def wechat_locations(context):
    data_list = []
    rows, source_paths = _message_rows(context, (48,))
    for head, body, msg_id, _ in rows:
        root = _xml_root(body)
        location = root.find('location') if root is not None else None
        attrs = location.attrib if location is not None else {}
        data_list.append(head + (
            attrs.get('x', ''),
            attrs.get('y', ''),
            attrs.get('label', ''),
            attrs.get('poiname', ''),
            msg_id,
        ))
    data_headers = (
        ('Message Time', 'datetime'),
        *_HEAD_HEADERS,
        'Latitude',
        'Longitude',
        'Label',
        'Place Name',
        'Message ID',
    )
    return data_headers, data_list, '\n'.join(source_paths)


def _float32(value):
    return struct.unpack('<f', struct.pack('<I', value & 0xffffffff))[0]


def _utf8(value):
    return value.decode('utf-8', 'replace') if isinstance(value, bytes) else ''


# Forces the text fields to bytes, so a short text is never guessed to be a nested message.
_MOMENT_TYPEDEF = {
    '5': {'type': 'bytes'},
    '6': {'type': 'message', 'message_typedef': {
        '1': {'type': 'fixed32'}, '2': {'type': 'fixed32'},
        '3': {'type': 'bytes'}, '4': {'type': 'bytes'}, '5': {'type': 'bytes'}}},
}


def _moment_fields(content):
    """(text, latitude, longitude, location text, media count) from a SnsInfo content blob."""
    try:
        message, _ = blackboxprotobuf.decode_message(content, _MOMENT_TYPEDEF)
    except Exception as error:  # pylint: disable=broad-except
        logfunc(f'WeChat: a Moments post did not decode: {error}')
        return '', '', '', '', ''
    latitude = longitude = place = ''
    location = message.get('6')
    if isinstance(location, dict):
        if location.get('1') or location.get('2'):
            longitude = round(_float32(location.get('1', 0)), 6)
            latitude = round(_float32(location.get('2', 0)), 6)
        place = ' | '.join(t for t in (_utf8(location.get(k)) for k in ('3', '4', '5')) if t)
    content_object = message.get('8')
    media = content_object.get('5') if isinstance(content_object, dict) else None
    count = len(media) if isinstance(media, list) else (1 if isinstance(media, dict) else 0)
    return _utf8(message.get('5')), latitude, longitude, place, count


@artifact_processor
def wechat_moments(context):
    data_list, source_paths = [], []
    names = _contact_names(context)
    for db_path in (str(f) for f in unique_files(context)):
        if os.path.basename(db_path) != 'SnsMicroMsg.db':
            continue
        db = open_sqlite_db_readonly(db_path)
        if db is None:
            continue
        try:
            rows = db.execute('''
                SELECT createTime, userName, content, type, sourceType, snsId
                FROM SnsInfo ORDER BY createTime
            ''').fetchall()
        except Exception as error:  # pylint: disable=broad-except
            logfunc(f'WeChat: could not read SnsInfo from {db_path}: {error}')
            rows = []
        finally:
            db.close()
        source_paths.append(context.get_relative_path(db_path))
        for created, user, content, kind, source, sns_id in rows:
            text, latitude, longitude, place, media = _moment_fields(content)
            data_list.append((
                convert_unix_ts_to_utc(created) if created else '',
                user or '',
                names.get(user, ''),
                text,
                latitude,
                longitude,
                place,
                media,
                kind if kind is not None else '',
                source if source is not None else '',
                sns_id if sns_id is not None else '',
            ))
    data_headers = (
        ('Post Time', 'datetime'),
        'Author ID',
        'Author Name',
        'Text',
        'Latitude',
        'Longitude',
        'Location Text',
        'Media Count',
        'Post Type (as stored)',
        'Source Type (as stored)',
        'Post ID',
    )
    return data_headers, data_list, '\n'.join(source_paths)


# attrBuf: field 9 and field 12 are lists of the same record shape; ids and text forced to bytes.
_ATTR_ENTRY = {'type': 'message', 'seen_repeated': True, 'message_typedef': {
    '1': {'type': 'bytes'}, '2': {'type': 'bytes'}, '5': {'type': 'bytes'}, '9': {'type': 'bytes'}}}
_ATTR_TYPEDEF = {'2': {'type': 'bytes'}, '3': {'type': 'bytes'}, '9': _ATTR_ENTRY, '12': _ATTR_ENTRY}


@artifact_processor
def wechat_moments_reactions(context):
    data_list, source_paths = [], []
    names = _contact_names(context)
    for db_path in (str(f) for f in unique_files(context)):
        if os.path.basename(db_path) != 'SnsMicroMsg.db':
            continue
        db = open_sqlite_db_readonly(db_path)
        if db is None:
            continue
        try:
            rows = db.execute('SELECT snsId, userName, attrBuf FROM SnsInfo ORDER BY createTime').fetchall()
        except sqlite3.Error as error:
            logfunc(f'WeChat: could not read SnsInfo from {db_path}: {error}')
            rows = []
        finally:
            db.close()
        source_paths.append(context.get_relative_path(db_path))
        for sns_id, author, attr in rows:
            if not attr:
                continue
            try:
                message, _ = blackboxprotobuf.decode_message(bytes(attr), _ATTR_TYPEDEF)
            except (ValueError, TypeError, KeyError, IndexError, struct.error, DecodeError) as error:
                logfunc(f'WeChat: a Moments attrBuf did not decode: {error}')
                continue
            if any(k.startswith(('9-', '12-')) for k in message):
                logfunc(f'WeChat: Moments post {sns_id} has like/comment entries of another layout, not reported')
            for field, kind in (('9', 'Like'), ('12', 'Comment')):
                entries = message.get(field) or []
                for e in entries if isinstance(entries, list) else [entries]:
                    if not isinstance(e, dict):
                        continue
                    actor, reply_to = _utf8(e.get('1')), _utf8(e.get('9'))
                    data_list.append((
                        convert_unix_ts_to_utc(e['6']) if isinstance(e.get('6'), int) and e['6'] > 0 else '',
                        kind, actor, names.get(actor, '') or _utf8(e.get('2')), _utf8(e.get('5')),
                        reply_to, names.get(reply_to, ''), author or '', names.get(author, ''),
                        sns_id if sns_id is not None else '', e.get('4', '')))
    data_headers = (
        ('Time', 'datetime'), 'Kind', 'Actor ID', 'Actor Name', 'Text', 'Reply To ID', 'Reply To Name',
        'Post Author ID', 'Post Author Name', 'Post ID', 'Entry Type (as stored)')
    return data_headers, data_list, '\n'.join(source_paths)


# roomdata: field 1 is the member list; ids and names forced to bytes.
_ROOMDATA_TYPEDEF = {'1': {'type': 'message', 'seen_repeated': True, 'message_typedef': {
    '1': {'type': 'bytes'}, '2': {'type': 'bytes'}, '4': {'type': 'bytes'}}}}


@artifact_processor
def wechat_group_members(context):
    data_list = []
    names = _contact_names(context)
    rows, source_paths = _query(context, '''
        SELECT chatroomname, memberlist, roomowner, memberCount, roomdata FROM chatroom ORDER BY chatroomname
    ''')
    for (room, members, owner, count, roomdata), _ in rows:
        extra = {}
        if roomdata:
            try:
                decoded = blackboxprotobuf.decode_message(bytes(roomdata), _ROOMDATA_TYPEDEF)[0]
                if any(k.startswith('1-') for k in decoded):
                    logfunc(f'WeChat: roomdata of {room} has member entries of another layout, not read')
                entries = decoded.get('1') or []
            except (ValueError, TypeError, KeyError, IndexError, struct.error, DecodeError) as error:
                logfunc(f'WeChat: roomdata of a group did not decode: {error}')
                entries = []
            for e in entries if isinstance(entries, list) else [entries]:
                if isinstance(e, dict) and e.get('1'):
                    extra[_utf8(e['1'])] = (_utf8(e.get('2')), _utf8(e.get('4')))
        for member in filter(None, (members or '').split(';')):
            nick, field4 = extra.get(member, ('', ''))
            data_list.append((
                room or '', names.get(room, ''), member, names.get(member, ''), nick,
                'Yes' if member == owner else '', field4, names.get(field4, ''),
                count if count is not None else ''))
    data_headers = (
        'Group ID', 'Group Name', 'Member ID', 'Member Name', 'Group Nickname (as stored)', 'Group Owner',
        'Field 4 Account ID (as stored)', 'Field 4 Account Name', 'Member Count (as stored)')
    return data_headers, data_list, '\n'.join(source_paths)
