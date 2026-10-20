__artifacts_v2__ = {
    "persistentPropertiesAll": {
        "name": "Persistent Properties",
        "description": "Name and value pairs stored in the protobuf form of persistent_properties",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Device Info",
        "notes": "Reads the file as the AOSP PersistentProperties protobuf (system/core/init/"
                 "persistent_properties.proto: repeated record, name = field 1, value = field 2). "
                 "A file that does not decode that way, such as the older text form, is left to "
                 "Persistent Properties - Matched Text Observations. Values are reported as "
                 "stored; their meaning is the property owner's and is not interpreted here. "
                 "Validated on a private Android 15 extraction.",
        "paths": ('*/property/persistent_properties',),
        "output_types": ["html", "tsv", "lava"],
        "artifact_icon": "settings",
    },
    "persistentBootReasonHistory": {
        "name": "Persistent Properties - Boot Reason History",
        "description": "Lines of the persist.sys.boot.reason.history property in the protobuf form of persistent_properties",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-08",
        "last_update_date": "2026-10-08",
        "requirements": "none",
        "category": "Wipe & Setup",
        "notes": "One row per line of the persist.sys.boot.reason.history value. The text before "
                 "the last comma is the stored reason; the last comma field is read as Unix "
                 "seconds when it is an integer and left blank "
                 "otherwise, with the raw text kept. The lines are reported as stored; what each "
                 "reason string means is not interpreted here. Validated on a private Android 15 extraction.",
        "paths": ('*/property/persistent_properties',),
        "output_types": "standard",
        "artifact_icon": "power",
    },
}

import struct
from datetime import datetime, timezone

from google.protobuf.message import DecodeError

from scripts.ilapfuncs import artifact_processor, decode_protobuf, logfunc

# AOSP PersistentProperties: repeated PersistentPropertyRecord {string name = 1; string value = 2}
# Fixed typedef so a name or value is never guessed as a nested message.
_TYPEDEF = {'1': {'type': 'message', 'seen_repeated': True,
                  'message_typedef': {'1': {'type': 'bytes'}, '2': {'type': 'bytes'}}}}


def _records(context):
    """(path, [(name, value)]) for each persistent_properties file that decodes as the protobuf form."""
    for path in map(str, context.get_files_found()):
        if not path.endswith('persistent_properties'):
            continue
        try:
            with open(path, 'rb') as f:
                message, _ = decode_protobuf(f.read(), _TYPEDEF)
            records = message.get('1', [])
            records = records if isinstance(records, list) else [records]
            if not records or any(not isinstance(r, dict) for r in records):
                raise ValueError('no name/value records')
            pairs = [(r['1'].decode('utf-8', 'replace'), r.get('2', b'').decode('utf-8', 'replace'))
                     for r in records if '1' in r]
        except (OSError, ValueError, TypeError, KeyError, AttributeError, IndexError, struct.error,
                DecodeError) as ex:
            logfunc(f'{path} is not the protobuf form of persistent_properties ({type(ex).__name__})')
            continue
        yield path, pairs


@artifact_processor
def persistentPropertiesAll(context):
    data_list, sources = [], []
    for path, pairs in _records(context):
        sources.append(path)
        data_list.extend((name, value, path) for name, value in pairs)
    return ('Property', 'Value', 'Source File'), data_list, '\n'.join(sources)


@artifact_processor
def persistentBootReasonHistory(context):
    data_list, sources = [], []
    for path, pairs in _records(context):
        for name, value in pairs:
            if name != 'persist.sys.boot.reason.history':
                continue
            sources.append(path)
            for line in filter(None, (l.strip() for l in value.splitlines())):
                reason, comma, raw = line.rpartition(',')
                try:
                    when = datetime.fromtimestamp(int(raw), timezone.utc) if comma else ''
                except (ValueError, OverflowError, OSError):
                    when = ''
                data_list.append((when, reason if comma else line, raw if comma else '', path))
    return (('Timestamp', 'datetime'), 'Reason (as stored)', 'Raw Timestamp Text', 'Source File'), \
        data_list, '\n'.join(sources)
