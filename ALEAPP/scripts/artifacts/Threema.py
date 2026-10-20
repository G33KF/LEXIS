__artifacts_v2__ = {
    "threema_account": {
        "name": "Threema - Account",
        "description": "The signed-in Threema account's own Threema ID, display "
                       "nickname, and any mobile number linked to it, read from the "
                       "app's main preferences file.",
        "author": "@Gear-I & Claude, @AlexisBrignoni, Codex",
        "creation_date": "2026-08-17",
        "last_update_date": "2026-08-17",
        "requirements": "none",
        "category": "Threema",
        "notes": "The account is reported by its Threema ID as stored in the preferences file. "
                 "'Linked Mobile Number' is reported when the preferences file carries one and "
                 "is blank otherwise; absence is not evidence that no number was ever linked.",
        "paths": ('*/ch.threema.app/shared_prefs/ch.threema.app_preferences.xml',),
        "output_types": ["standard"],
        "artifact_icon": "user",
        "sample_data": {
            "pixel7a_a14": "Android 14 | ch.threema.app | 1 row",
        },
    },
    "threema_contacts": {
        "name": "Threema - Contacts",
        "description": "Contacts recorded in Threema's local, SQLCipher-encrypted database, with each one's stored verification level and the date the contact record was created locally.",
        "author": "@Gear-I & Claude, @AlexisBrignoni, Codex",
        "creation_date": "2026-08-17",
        "last_update_date": "2026-10-04",
        "requirements": "sqlcipher3", 
        "category": "Threema",
        "notes": "Threema encrypts its main database (threema4.db) at rest with "
                 "SQLCipher; this module decrypts it in memory before reading it, "
                 "using the version 1 key.dat format in Threema's published source (a fixed "
                 "32-byte XOR mask followed by a SHA-1 checksum verification). Reference: Threema "
                 "for Android, 'Version1MasterKeyFileManager.kt', "
                 "https://github.com/threema-ch/threema-android/blob/"
                 "d44a2b53b2ea2c0bf1065bdaa96987570e5b4e6d/app/src/main/java/ch/threema/"
                 "localcrypto/Version1MasterKeyFileManager.kt#L18-L43; "
                 "the format is also described at github.com/wilzbach/threema-decrypt. On "
                 "pixel7a_a14 the checksum embedded in key.dat matched the derived key, and the "
                 "decrypted database opened and returned the contact and message rows recorded in "
                 "sample_data. This covers only a version 1 key.dat with no passphrase protection. "
                 "The client's current source also defines version 2 master key files, "
                 "master_key.dat and enc_master_key_v2.dat, which this module does not read; an "
                 "install that has no key.dat produces no rows, and its threema4.db is listed "
                 "as a source file and left undecrypted. "
                 "Reference: Threema for Android, 'MasterKeyFileProvider.kt', "
                 "https://github.com/threema-ch/threema-android/blob/"
                 "d44a2b53b2ea2c0bf1065bdaa96987570e5b4e6d/app/src/main/java/ch/threema/"
                 "localcrypto/MasterKeyFileProvider.kt#L8-L20. "
                 "The no-key.dat case was exercised on a copy of the pixel7a_a14 files with "
                 "key.dat removed, not on an install that carries a version 2 file. "
                 "A passphrase-protected key.dat is also left undecrypted (key.dat's own "
                 "first byte, read directly rather than assumed, records this; on a "
                 "device where it indicates passphrase protection this module leaves "
                 "the database undecrypted rather than guess at a passphrase-derived "
                 "key). The database is opened strictly read-only, and its "
                 "write-ahead log (threema4.db-wal) is opened alongside it so that "
                 "any transaction committed to the log but not yet checkpointed into "
                 "the main file is still included - on the device this was validated "
                 "against, the main file alone was several weeks stale and missing "
                 "real, later content that only existed in the log. 'Verification "
                 "Level' maps the stored verificationLevel value as 0=Unverified, "
                 "1=Server-verified, 2=Fully verified; the names are those of the client's "
                 "VerificationLevel list (Reference: Threema for Android, "
                 "'VerificationLevel.java', "
                 "https://github.com/threema-ch/threema-android/blob/"
                 "d44a2b53b2ea2c0bf1065bdaa96987570e5b4e6d/domain/src/main/java/ch/threema/domain/"
                 "models/VerificationLevel.java#L10-L12). "
                 "How a contact reached a level is not stored in this column and is not "
                 "reported. On the device this was validated against, the "
                 "one contact recorded at level 2 corresponds exactly to a 'via QR "
                 "scan' verification event documented for that same contact. "
                 "'Group-Only / Removed' reflects Threema's own AcquaintanceLevel "
                 "field on the contact record; the column shows Yes when the stored "
                 "acquaintanceLevel is set and not 0. Threema's Android source defines an "
                 "acquaintance level named GROUP_OR_DELETED as covering three cases: a contact who "
                 "is in a group with the "
                 "user, was not explicitly added and has no 1:1 conversation; a contact who was in "
                 "a group with the user before, where either of them has since been removed from "
                 "all common groups; and "
                 "a contact the user explicitly removed (Reference: Threema for Android, "
                 "'Contact.kt (AcquaintanceLevel)', "
                 "https://github.com/threema-ch/threema-android/blob/"
                 "d44a2b53b2ea2c0bf1065bdaa96987570e5b4e6d/domain/src/main/java/ch/threema/domain/"
                 "models/Contact.kt#L213-L231) "
                 "- the three cases cannot be told apart from this "
                 "field alone, so they are reported together rather than guessing "
                 "which applies. 'Date Created' is the stored dateCreated value, read as Unix "
                 "milliseconds; the client's source describes that column as the date when "
                 "the contact was created locally (Reference: Threema for Android, "
                 "'ContactModel.java', "
                 "https://github.com/threema-ch/threema-android/blob/"
                 "d44a2b53b2ea2c0bf1065bdaa96987570e5b4e6d/app/src/main/java/ch/threema/storage/"
                 "models/ContactModel.java#L57).",
        "paths": (
            '*/ch.threema.app/files/key.dat',
            '*/ch.threema.app/databases/threema4.db*',
        ),
        "output_types": ["standard"],
        "artifact_icon": "address-book",
        "sample_data": {
            "pixel7a_a14": "Android 14 | ch.threema.app | 3 rows",
        },
    },
    "threema_messages": {
        "name": "Threema - Messages",
        "description": "One-to-one messages recorded in Threema's local, SQLCipher-encrypted database: text, file (type 8), static location, and call events, with direction, delivery/read status, and any quote-reply relationship between messages.",
        "author": "@Gear-I & Claude, @AlexisBrignoni, Codex",
        "creation_date": "2026-08-17",
        "last_update_date": "2026-10-06",
        "requirements": "sqlcipher3",
        "category": "Threema",
        "notes": "Decrypted the same way as Threema - Contacts, including reading "
                 "the database's write-ahead log alongside the main file; see that "
                 "artifact's notes for the key.dat/SQLCipher method and its "
                 "passphrase-protected limitation. On the device this was validated "
                 "against, a pair of static-location messages exchanged in one "
                 "conversation existed only in the write-ahead log and would have "
                 "been missed entirely by reading the main database file alone. "
                 "Only the four message-type combinations confirmed on this device "
                 "are decoded: plain text; a file message, type 8 (FILE) in the client's "
                 "MessageType list (threema-android MessageType.kt at commit d44a2b53, "
                 "https://github.com/threema-ch/threema-android/blob/"
                 "d44a2b53b2ea2c0bf1065bdaa96987570e5b4e6d/app/src/main/java/ch/threema/storage/"
                 "models/MessageType.kt#L23-L28), "
                 "reported as File; read the MIME Type "
                 "column to see what kind of file a row holds (filename, MIME type and file "
                 "size are read from the message's own structured content, matched "
                 "by value rather than by trusting a fixed field position, since "
                 "that content is a positional JSON array with no field names of "
                 "its own); a static location (latitude, longitude, accuracy in "
                 "metres and a resolved address, read by fixed field position, "
                 "since this content's structure - unlike the file case - showed "
                 "the same fixed [latitude, longitude, accuracy, address] shape on "
                 "every location message observed); and a call event (status/call "
                 "ID/duration are read by name, since that part of the structure is "
                 "a JSON object). Every call on this device carried the same "
                 "numeric call-type marker regardless of whether it was really an "
                 "audio or a video call, so this module does not report an "
                 "audio/video distinction rather than guess at one; duration and "
                 "direction are still reported. 'Quoted "
                 "Message' is resolved via the replying message's own "
                 "quotedMessageId against the original message's apiMessageId (the message's "
                 "protocol id, which is distinct from the database's own row "
                 "id), not by row order. Group messages, distribution lists and "
                 "polls are out of scope for this release: every corresponding "
                 "table was empty on the device this was validated against, so "
                 "their column mapping could not be confirmed against real "
                 "content. A message can be missing from this table with a gap in "
                 "the numbering between its neighbours; this module does not assert deletion from "
                 "a "
                 "gap alone since a gap has other possible causes (e.g. a message "
                 "never finishing sync before extraction), and the database's own "
                 "deletedAtUtc/editedAtUtc columns, which would confirm a deletion "
                 "or edit directly, were unset on every row on this device.",
        "paths": (
            '*/ch.threema.app/files/key.dat',
            '*/ch.threema.app/databases/threema4.db*',
        ),
        "output_types": ["standard", "timeline"],
        "artifact_icon": "message-circle",
        "sample_data": {
            "pixel7a_a14": "Android 14 | ch.threema.app | 33 rows",
        },
    },
    "threema_groups": {
        "name": "Threema - Groups and Distribution Lists",
        "description": "Groups and distribution lists recorded in Threema's local, SQLCipher-encrypted database, with their members.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "sqlcipher3",
        "category": "Threema",
        "notes": "Decrypted as Threema - Contacts. Groups come from m_group with members from group_member; distribution "
                 "lists from distribution_list with members from distribution_list_member. Members are shown by contact "
                 "name where the contacts table has one, else by Threema ID. 'User State' is m_group.userState as stored, "
                 "not decoded. A database without one of these tables yields no rows for it and logs the absence. "
                 "Verified against a synthetic database built to the table layout used by ThreemaToALEAPP (MIT, AndroidIosForensik); not yet run on a device image.",
        "paths": (
            '*/ch.threema.app/files/key.dat',
            '*/ch.threema.app/databases/threema4.db*',
        ),
        "output_types": ["standard"],
        "artifact_icon": "users",
    },
    "threema_group_messages": {
        "name": "Threema - Group Messages",
        "description": "Messages in Threema groups and distribution lists, from the local, SQLCipher-encrypted database.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "sqlcipher3",
        "category": "Threema",
        "notes": "Decrypted as Threema - Contacts. Rows come from m_group_message and distribution_list_message; message "
                 "types are decoded as in Threema - Messages (text, type 8 file, location, call), any other type is shown "
                 "with its number and raw body. Created, Delivered, Read At, Edited and Deleted are the stored *AtUtc "
                 "values read as Unix milliseconds. Sender is 'Local User' for outgoing rows. Verified against a synthetic database built to the table layout used by ThreemaToALEAPP (MIT, AndroidIosForensik); not yet run on a device image.",
        "paths": (
            '*/ch.threema.app/files/key.dat',
            '*/ch.threema.app/databases/threema4.db*',
        ),
        "output_types": ["standard", "timeline"],
        "artifact_icon": "message-circle",
    },
    "threema_reactions": {
        "name": "Threema - Reactions",
        "description": "Emoji reactions to Threema messages, one-to-one and in groups, with who reacted and when.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "sqlcipher3",
        "category": "Threema",
        "notes": "Decrypted as Threema - Contacts. Threema for Android keeps reactions in contact_reactions (keyed to the "
                 "1:1 message table) and group_reactions (keyed to m_group_message) (Reference: threema-android, "
                 "'ContactEmojiReactionModelFactory.kt' and 'GroupEmojiReactionModelFactory.kt', "
                 "https://github.com/threema-ch/threema-android/tree/main/app/src/main/java/ch/threema/storage/factories). "
                 "Those files do not spell out the column names, so message id, sender identity, emoji and reaction time "
                 "are resolved from each database's own schema; the table names contact_emoji_reaction and "
                 "group_emoji_reaction used by ThreemaToALEAPP are read as well. Reacted At is read as Unix milliseconds. "
                 "Emoji reactions exist from Threema 5.8.1 for Android; an older database has no such table and yields "
                 "no rows. Verified against a synthetic database; not yet run on a device image.",
        "paths": (
            '*/ch.threema.app/files/key.dat',
            '*/ch.threema.app/databases/threema4.db*',
        ),
        "output_types": ["standard", "timeline"],
        "artifact_icon": "smile",
    },
}

import hashlib
import json
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

from scripts.ilapfuncs import artifact_processor, get_sqlite_db_path, logfunc
from scripts.artifacts.storagePathViews import unique_files

try:
    from sqlcipher3 import dbapi2 as sqlcipher
    _SQLCIPHER_AVAILABLE = True
except ImportError as _sqlcipher_import_error:
    # Without this guard a missing sqlcipher3 install would raise at
    # module-import time and abort loading of ALL ALEAPP artifacts, not just
    # these two - see browserArtifactsViaMisterSkinnylegs.py for the same
    # pattern already established in this project.
    _SQLCIPHER_AVAILABLE = False
    logfunc(f"Threema: sqlcipher3 not available, encrypted-database "
            f"artifacts disabled: {_sqlcipher_import_error}")

_INVALID_XML_CHARS = re.compile(r'[\x00-\x08\x0b\x0c\x0e-\x1f]')
# Some binary values (e.g. a Threema Safe backup hash) get written out as
# per-byte numeric character references, including control-code bytes that
# are syntactically well-formed XML entities but not valid XML characters
# (only tab/LF/CR are valid below 0x20) - ElementTree rejects the whole file
# for these the same as it would a raw control byte, so they need the same
# stripping, just matched in their &#N; / &#xN; encoded form instead.
_INVALID_XML_NUMERIC_REF = re.compile(r'&#x?[0-9a-fA-F]+;')


def _strip_invalid_numeric_refs(text):
    def _replace(match):
        ref = match.group(0)
        try:
            if ref[2:3] in ('x', 'X'):
                code_point = int(ref[3:-1], 16)
            else:
                code_point = int(ref[2:-1])
        except ValueError:
            return ref
        is_valid = (code_point in (0x09, 0x0A, 0x0D)
                    or 0x20 <= code_point <= 0xD7FF
                    or 0xE000 <= code_point <= 0xFFFD
                    or 0x10000 <= code_point <= 0x10FFFF)
        return ref if is_valid else ''
    return _INVALID_XML_NUMERIC_REF.sub(_replace, text)

# Fixed 32-byte XOR mask applied to a version-1 key.dat's stored key bytes,
# reverse engineered from github.com/wilzbach/threema-decrypt's cmg_clean.java
# and verified against this device's real key.dat (the file's own trailing
# SHA-1 checksum matched the derived key exactly).
_KEY_DAT_XOR_MASK = bytes([
    149, 13, 38, 122, 136, 234, 119, 16, 156, 80, 231, 63, 71, 224, 105, 114,
    218, 196, 57, 124, 153, 234, 126, 103, 175, 253, 221, 50, 218, 53, 247, 12,
])


def _parse_xml(file_found):
    try:
        return ET.parse(file_found).getroot()
    except ET.ParseError:
        try:
            with open(file_found, encoding='utf-8', errors='replace') as f:
                cleaned = _strip_invalid_numeric_refs(
                    _INVALID_XML_CHARS.sub('', f.read()))
            return ET.fromstring(cleaned)
        except ET.ParseError as ex:
            logfunc(f'Threema: could not parse {file_found}: {ex}')
            return ET.Element('empty')


def _xml_value(root, name):
    node = root.find(f".//*[@name='{name}']")
    if node is None:
        return None
    return node.get('value', node.text)


def _find_all(files_found, suffix):
    """Every file ending in suffix, sorted: an extraction can carry a container
    per Android user, and each user's Threema data is separate evidence. The
    exact-suffix test never matches a -wal/-shm/-journal sidecar."""
    return sorted(str(f) for f in files_found if str(f).endswith(suffix))


def _epoch_ms_to_utc(value):
    try:
        return datetime.fromtimestamp(int(value) / 1000, tz=timezone.utc)
    except (TypeError, ValueError, OverflowError, OSError):
        return None


def _derive_sqlcipher_key(key_dat_path):
    """Unwrap a version-1 Threema key.dat into the raw 32-byte SQLCipher key.

    Format (all fixed-size, 45 bytes total): 1-byte protection flag, 32-byte
    XOR-masked key, 8 reserved bytes, 4-byte SHA-1(key)[:4] checksum. Returns
    None (logging why) if the file is passphrase-protected, the wrong size,
    or its own checksum does not verify - never a guessed/partial key.
    """
    with open(key_dat_path, 'rb') as f:
        raw = f.read()

    if len(raw) != 45:
        logfunc(f"Threema: key.dat is {len(raw)} bytes, expected 45 for the "
                f"version-1 format; skipping.")
        return None

    protected = raw[0] != 0
    if protected:
        logfunc("Threema: key.dat is passphrase-protected; this module only "
                "supports the unprotected case, skipping.")
        return None

    masked_key = raw[1:33]
    checksum = raw[41:45]
    key = bytes(k ^ m for k, m in zip(masked_key, _KEY_DAT_XOR_MASK))

    if hashlib.sha1(key).digest()[:4] != checksum:
        logfunc("Threema: key.dat checksum did not verify; derived key "
                "rejected rather than used unverified.")
        return None

    return key


def _open_decrypted_db(db_path, key):
    """Open threema4.db read-only through the derived key. Returns a live
    connection on success, or None (logging why) on failure - an unverified
    or wrong key fails here with a clear SQLCipher error rather than silently
    returning garbage, since SQLCipher HMAC-checks every page it decrypts.

    Opened via a read-only file: URI (the same helper and URI form used
    project-wide by scripts.ilapfuncs.open_sqlite_db_readonly) rather than a
    plain path, so evidence is never at risk of being modified, while still
    letting SQLite fold in any not-yet-checkpointed write-ahead log content
    sitting alongside the file - which on the device this was validated
    against held real content the main file alone did not.
    """
    try:
        uri = f"file:{get_sqlite_db_path(db_path)}?mode=ro"
        con = sqlcipher.connect(uri, uri=True)  # pylint: disable=no-member
        cur = con.cursor()
        cur.execute(f"PRAGMA key = 'x\"{key.hex()}\"';")
        # Threema's own DatabaseService overrides SQLCipher's default HMAC-key
        # KDF iteration count to 1; verified empirically against this
        # device's real database, since a wrong iteration count fails every
        # page's HMAC check rather than silently misreading.
        cur.execute("PRAGMA kdf_iter = 1;")
        cur.execute("SELECT name FROM sqlite_master WHERE type='table' LIMIT 1;")
        cur.fetchall()
        return con
    except Exception as ex:  # pylint: disable=broad-exception-caught
        logfunc(f"Threema: could not open threema4.db with the derived key: {ex}")
        return None


def _decrypted_connections(files_found):
    """[(connection or None, db path)] per app container, sorted by db path.

    An extraction can carry a container per Android user, each with its own
    key.dat and threema4.db. The pairing stays inside one container: a
    database only opens with the key file next to it, never another user's.
    A container whose key does not derive, or that has no key.dat at all,
    still reports its db path, so the report cites the store that could not
    be opened.
    """
    if not _SQLCIPHER_AVAILABLE:
        return []
    normalized = [str(f).replace('\\', '/') for f in files_found]
    keys_by_root = {f[:-len('files/key.dat')]: f for f in normalized
                    if f.endswith('files/key.dat')}
    pairs = []
    for db_path in sorted(f for f in normalized
                          if f.endswith('databases/threema4.db')):
        key_dat_path = keys_by_root.get(db_path[:-len('databases/threema4.db')])
        if not key_dat_path:
            logfunc("Threema: no key.dat beside threema4.db; version 2 master "
                    "key files are not read, so the database is cited and "
                    "left undecrypted.")
            pairs.append((None, db_path))
            continue
        key = _derive_sqlcipher_key(key_dat_path)
        con = _open_decrypted_db(db_path, key) if key is not None else None
        pairs.append((con, db_path))
    return pairs


_VERIFICATION_LEVELS = {
    0: "Unverified",
    1: "Server-verified",
    2: "Fully verified",
}


@artifact_processor
def threema_account(context):
    data_headers = ("Threema ID", "Nickname", "Linked Mobile Number")

    files_found = unique_files(context)
    prefs_paths = _find_all(files_found, 'ch.threema.app_preferences.xml')
    if not prefs_paths:
        return data_headers, [], ""

    data_list = []
    for prefs_path in prefs_paths:
        root = _parse_xml(prefs_path)
        identity = _xml_value(root, 'identity') or ''
        nickname = _xml_value(root, 'nickname') or ''
        linked_mobile = _xml_value(root, 'linked_mobile') or ''

        if not identity:
            continue

        logfunc(f"Threema Account: ID {identity} recovered.")
        data_list.append((identity, nickname, linked_mobile))

    return data_headers, data_list, '\n'.join(prefs_paths)


@artifact_processor
def threema_contacts(context):
    data_headers = (
        "Threema ID", "Display Name", "Verification Level",
        ("Date Created", "datetime"), "Group-Only / Removed", "Archived",
    )

    files_found = unique_files(context)
    pairs = _decrypted_connections(files_found)
    if not pairs:
        return data_headers, [], ""

    data_list = []
    for con, _db_path in pairs:
        if con is None:
            continue
        try:
            cur = con.cursor()
            cur.execute(
                "SELECT identity, firstName, lastName, publicNickName, "
                "verificationLevel, dateCreated, acquaintanceLevel, isArchived "
                "FROM contacts;")
            for (identity, first, last, nick, level, created,
                 acquaintance_level, archived) in cur.fetchall():
                display_name = " ".join(p for p in (first, last) if p) or nick or ''
                data_list.append((
                    identity,
                    display_name,
                    _VERIFICATION_LEVELS.get(level, str(level)),
                    _epoch_ms_to_utc(created),
                    "Yes" if acquaintance_level else "",
                    "Yes" if archived else "",
                ))
        finally:
            con.close()

    data_list.sort(key=lambda row: (row[3] is None, row[3]))
    logfunc(f"Threema Contacts: {len(data_list)} contact(s) recovered from "
            f"the decrypted database.")
    return data_headers, data_list, '\n'.join(db for _, db in pairs)


def _extract_media_info(body_json):
    """Pull filename/mime-type/size out of an image message's structured
    content by matching value shape, not a fixed array position - Threema's
    media message body is a positional JSON array with no field names of its
    own, and this project does not trust an undocumented position blindly.
    """
    filename = mime_type = ''
    size = None
    try:
        parts = json.loads(body_json)
    except (ValueError, TypeError):
        return filename, mime_type, size
    if not isinstance(parts, list):
        return filename, mime_type, size
    for part in parts:
        if isinstance(part, str) and '/' in part and not mime_type:
            mime_type = part
        elif isinstance(part, str) and '.' in part and not filename:
            filename = part
        elif isinstance(part, int) and size is None:
            size = part
    return filename, mime_type, size


def _extract_location_info(body_json):
    """Pull latitude/longitude/accuracy/address out of a static location
    message's structured content. Unlike the image case above, every
    location message observed on the validated device showed the same
    fixed [latitude, longitude, accuracy, address, poi-or-null] shape, so
    this reads by position - but still type-checks each field rather than
    trusting the position blindly.
    """
    try:
        parts = json.loads(body_json)
    except (ValueError, TypeError):
        return None, None, None, ''
    if not isinstance(parts, list) or len(parts) < 4:
        return None, None, None, ''
    lat, lon, accuracy, address = parts[0], parts[1], parts[2], parts[3]
    if not isinstance(lat, (int, float)) or not isinstance(lon, (int, float)):
        return None, None, None, ''
    accuracy = accuracy if isinstance(accuracy, (int, float)) else None
    address = address if isinstance(address, str) else ''
    return lat, lon, accuracy, address


def _extract_call_info(body_json):
    """Pull status/callId/duration out of a call event's structured content
    by dictionary key - this part of the structure is a real JSON object
    with named fields, unlike the media message array above.
    """
    try:
        parts = json.loads(body_json)
    except (ValueError, TypeError):
        return None, None, None
    if not isinstance(parts, list):
        return None, None, None
    for part in parts:
        if isinstance(part, dict) and 'callId' in part:
            return part.get('status'), part.get('callId'), part.get('duration')
    return None, None, None


@artifact_processor
def threema_messages(context):
    data_headers = (
        ("Created", "datetime"), ("Delivered", "datetime"), ("Read At", "datetime"),
        "Contact", "Direction", "Content Type",
        "Text / Filename / Address", "MIME Type", "Size (bytes)",
        "Latitude", "Longitude", "Location Accuracy (m)",
        "Call Status", "Call ID", "Call Duration (s)", "State", "Read",
        "Quoted Message",
    )

    files_found = unique_files(context)
    pairs = _decrypted_connections(files_found)
    if not pairs:
        return data_headers, [], ""

    data_list = []
    for con, _db_path in pairs:
        if con is None:
            continue
        try:
            cur = con.cursor()
            contacts = {}
            for identity, first, last, nick in cur.execute(
                    "SELECT identity, firstName, lastName, publicNickName FROM contacts;"):
                contacts[identity] = " ".join(p for p in (first, last) if p) or nick or identity

            cur.execute(
                "SELECT identity, outbox, type, body, apiMessageId, quotedMessageId, "
                "state, isRead, createdAtUtc, deliveredAtUtc, readAtUtc "
                "FROM message ORDER BY createdAtUtc;")
            rows = cur.fetchall()

            api_id_to_text = {}
            for (identity, outbox, mtype, body, api_id, quoted_id, state,
                 is_read, created, delivered, read_at) in rows:
                if api_id and mtype == 0 and body:
                    api_id_to_text[api_id] = body

            for (identity, outbox, mtype, body, api_id, quoted_id, state,
                 is_read, created, delivered, read_at) in rows:
                contact_name = contacts.get(identity, identity)
                direction = "Sent" if outbox else "Received"
                text = filename = mime_type = ''
                size = call_status = call_id = duration = None
                lat = lon = accuracy = None
                content_type = "Text"

                if mtype == 0:
                    content_type = "Text"
                    text = body or ''
                elif mtype == 8:
                    content_type = "File"
                    filename, mime_type, size = _extract_media_info(body)
                elif mtype == 4:
                    content_type = "Location"
                    lat, lon, accuracy, text = _extract_location_info(body)
                elif mtype == 9:
                    content_type = "Call"
                    call_status, call_id, duration = _extract_call_info(body)
                else:
                    content_type = f"Unrecognized (type={mtype})"
                    text = body or ''

                quoted_text = api_id_to_text.get(quoted_id, '') if quoted_id else ''

                data_list.append((
                    _epoch_ms_to_utc(created),
                    _epoch_ms_to_utc(delivered),
                    _epoch_ms_to_utc(read_at),
                    contact_name,
                    direction,
                    content_type,
                    text or filename,
                    mime_type,
                    size,
                    lat,
                    lon,
                    accuracy,
                    call_status,
                    call_id,
                    duration,
                    state or '',
                    "Yes" if is_read else "",
                    quoted_text,
                ))
        finally:
            con.close()

    logfunc(f"Threema Messages: {len(data_list)} message(s) recovered from "
            f"the decrypted database.")
    return data_headers, data_list, '\n'.join(db for _, db in pairs)


def _tables(cur):
    return {r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'")}


def _contact_names(cur):
    names = {}
    for identity, first, last, nick in cur.execute(
            "SELECT identity, firstName, lastName, publicNickName FROM contacts;"):
        names[identity] = " ".join(p for p in (first, last) if p) or nick or identity
    return names


def _decode_body(mtype, body):
    """(content type, text, mime, size, lat, lon) as Threema - Messages decodes them."""
    if mtype == 0:
        return "Text", body or '', '', None, None, None
    if mtype == 8:
        filename, mime_type, size = _extract_media_info(body)
        return "File", filename, mime_type, size, None, None
    if mtype == 4:
        lat, lon, _accuracy, text = _extract_location_info(body)
        return "Location", text, '', None, lat, lon
    if mtype == 9:
        status, _call_id, duration = _extract_call_info(body)
        return "Call", f"{status} {duration or ''}".strip(), '', None, None, None
    return f"Unrecognized (type={mtype})", body or '', '', None, None, None


@artifact_processor
def threema_groups(context):
    data_headers = (("Created", "datetime"), ("Last Update", "datetime"), "Kind", "Name", "Creator",
                    "Member Count", "Members", "Description", "User State", "Group ID")
    pairs = _decrypted_connections(unique_files(context))
    data_list = []
    for con, _db in pairs:
        if con is None:
            continue
        try:
            cur = con.cursor()
            names = _contact_names(cur)
            tables = _tables(cur)
            if {'m_group', 'group_member'} <= tables:
                members = {}
                for identity, gid in cur.execute("SELECT identity, groupId FROM group_member"):
                    members.setdefault(gid, []).append(names.get(identity, identity))
                for gid, api_id, name, creator, created, updated, desc, state in cur.execute(
                        "SELECT id, apiGroupId, name, creatorIdentity, createdAt, lastUpdateAt, groupDesc, userState "
                        "FROM m_group").fetchall():
                    m = members.get(gid, [])
                    data_list.append((_epoch_ms_to_utc(created), _epoch_ms_to_utc(updated), "Group", name or '',
                                      names.get(creator, creator or ''), len(m), ", ".join(m), desc or '',
                                      state if state is not None else '', api_id or ''))
            else:
                logfunc("Threema - Groups: no m_group/group_member table in this database")
            if {'distribution_list', 'distribution_list_member'} <= tables:
                members = {}
                for identity, lid in cur.execute("SELECT identity, distributionListId FROM distribution_list_member"):
                    members.setdefault(lid, []).append(names.get(identity, identity))
                for lid, name, created, updated in cur.execute(
                        "SELECT id, name, createdAt, lastUpdate FROM distribution_list").fetchall():
                    m = members.get(lid, [])
                    data_list.append((_epoch_ms_to_utc(created), _epoch_ms_to_utc(updated), "Distribution list",
                                      name or '', '', len(m), ", ".join(m), '', '', lid))
        finally:
            con.close()
    return data_headers, data_list, '\n'.join(db for _, db in pairs)


@artifact_processor
def threema_group_messages(context):
    data_headers = (("Created", "datetime"), ("Delivered", "datetime"), ("Read At", "datetime"), "Chat Type", "Chat",
                    "Sender", "Direction", "Content Type", "Text / Filename / Address", "MIME Type", "Size (bytes)",
                    "Latitude", "Longitude", "State", "Read", ("Edited", "datetime"), ("Deleted", "datetime"),
                    "Message ID")
    pairs = _decrypted_connections(unique_files(context))
    data_list = []
    for con, _db in pairs:
        if con is None:
            continue
        try:
            cur = con.cursor()
            names = _contact_names(cur)
            tables = _tables(cur)
            for kind, table, chat_col, chat_table in (
                    ("Group", "m_group_message", "groupId", "m_group"),
                    ("Distribution list", "distribution_list_message", "distributionListId", "distribution_list")):
                if table not in tables:
                    logfunc(f"Threema - Group Messages: no {table} table in this database")
                    continue
                cols = {r[1] for r in cur.execute(f"PRAGMA table_info({table})")}

                col = {c: f"m.{c}" if c in cols else "NULL" for c in (
                    "identity", "outbox", "type", "body", "state", "isRead",
                    "createdAtUtc", "deliveredAtUtc", "readAtUtc", "editedAtUtc", "deletedAtUtc")}
                has_chat = chat_table in tables
                chat_name = "c.name" if has_chat else "NULL"
                join = f"LEFT JOIN {chat_table} c ON c.id = m.{chat_col}" if has_chat else ""
                for (mid, chat, identity, outbox, mtype, body, state, is_read, created, delivered, read_at,
                     edited, deleted) in cur.execute(
                        f"SELECT m.id, COALESCE({chat_name}, m.{chat_col}), {col['identity']}, {col['outbox']}, "
                        f"{col['type']}, {col['body']}, {col['state']}, {col['isRead']}, {col['createdAtUtc']}, "
                        f"{col['deliveredAtUtc']}, {col['readAtUtc']}, {col['editedAtUtc']}, {col['deletedAtUtc']} "
                        f"FROM {table} m {join} ORDER BY {col['createdAtUtc']}").fetchall():
                    ctype, text, mime, size, lat, lon = _decode_body(mtype, body)
                    data_list.append((_epoch_ms_to_utc(created), _epoch_ms_to_utc(delivered),
                                      _epoch_ms_to_utc(read_at), kind, chat if chat is not None else '',
                                      "Local User" if outbox else names.get(identity, identity or ''),
                                      "Sent" if outbox else "Received", ctype, text, mime, size, lat, lon,
                                      state or '', "Yes" if is_read else "", _epoch_ms_to_utc(edited),
                                      _epoch_ms_to_utc(deleted), mid))
        finally:
            con.close()
    return data_headers, data_list, '\n'.join(db for _, db in pairs)


def _reaction_columns(cols):
    """(message id, sender, emoji, time) column names in this database, or None."""
    low = {c.lower(): c for c in cols}

    def pick(*needles):
        return next((orig for lc, orig in low.items() if all(n in lc for n in needles)), None)
    found = (pick('message', 'id'), pick('identity'), pick('emoji'), pick('reacted') or pick('at'))
    return found if all(found) else None


@artifact_processor
def threema_reactions(context):
    data_headers = (("Reacted At", "datetime"), "Emoji", "Reactor", "Chat Type", "Chat", "Message",
                    ("Message Created", "datetime"), "Message ID")
    pairs = _decrypted_connections(unique_files(context))
    data_list = []
    for con, _db in pairs:
        if con is None:
            continue
        try:
            cur = con.cursor()
            names = _contact_names(cur)
            tables = _tables(cur)
            found_any = False
            for kind, table, msg_table in (("1:1", "contact_reactions", "message"),
                                           ("1:1", "contact_emoji_reaction", "message"),
                                           ("Group", "group_reactions", "m_group_message"),
                                           ("Group", "group_emoji_reaction", "m_group_message")):
                if table not in tables:
                    continue
                found_any = True
                rc = _reaction_columns([r[1] for r in cur.execute(f"PRAGMA table_info({table})")])
                if rc is None:
                    logfunc(f"Threema - Reactions: unrecognised columns in {table}; skipped")
                    continue
                mid, sender, emoji, at = rc
                chat, body, created, join = "NULL", "NULL", "NULL", ""
                if msg_table in tables:
                    body, created, join = "m.body", "m.createdAtUtc", f"LEFT JOIN {msg_table} m ON m.id = r.{mid}"
                    if kind == "1:1":
                        chat = "m.identity"
                    elif "m_group" in tables:
                        chat, join = "g.name", join + " LEFT JOIN m_group g ON g.id = m.groupId"
                for reacted, emo, who, chat_value, text, made, message_id in cur.execute(
                        f"SELECT r.{at}, r.{emoji}, r.{sender}, {chat}, {body}, {created}, r.{mid} "
                        f"FROM {table} r {join} ORDER BY r.{at}").fetchall():
                    data_list.append((_epoch_ms_to_utc(reacted), emo or '', names.get(who, who or ''), kind,
                                      names.get(chat_value, chat_value or '') if kind == "1:1" else (chat_value or ''),
                                      text or '', _epoch_ms_to_utc(made), message_id))
            if not found_any:
                logfunc("Threema - Reactions: no reaction table in this database (Threema before 5.8.1)")
        finally:
            con.close()
    return data_headers, data_list, '\n'.join(db for _, db in pairs)
