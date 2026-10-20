__artifacts_v2__ = {
    "recoveredSms": {
        "name": "SMS Messages - Recovered Records",
        "description": "Records of the SMS table recovered from older page versions and free space of mmssms.db and "
                       "its write-ahead log, which a query of the live table no longer returns.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "SMS & MMS",
        "notes": "Reads the sms table of the telephony provider's mmssms.db. Date and Date Sent are read as Unix "
                 "milliseconds, Type and Read as stored. Column names are those of the database's own schema, so a "
                 "vendor database without one of them leaves it blank. "
                 "Records are recovered by scripts/sqlite_carver.py, the reader iLEAPP uses for WeChat: older versions of "
                 "the table's leaf pages (the main file's copy of a page the write-ahead log replaced, and every log "
                 "frame, before and after the last checkpoint) and the free space of every version of those pages. A "
                 "record equal to a live row, or to another recovered record, is not reported. A record from free space "
                 "usually lost its rowid, because SQLite overwrites a freed cell's first four bytes; Row ID is then blank. "
                 "Recovered From says which of the two places a row came from. An older page version holds a row as it "
                 "was before an edit or a deletion, so a row here is not by itself evidence that it was deleted. "
                 "Truncated means the record's overflow pages were not in the current image and only its start is "
                 "shown. Tested on synthetic databases built with the carver's own test cases and on synthetic tables; "
                 "no device image was run here.",
        "paths": ('*/com.android.providers.telephony/databases/mmssms.db*',),
        "output_types": "standard",
        "artifact_icon": "message-square",
    },
    "recoveredCalls": {
        "name": "Call Logs - Recovered Records",
        "description": "Records of the calls table recovered from older page versions and free space of the call log "
                       "database and its write-ahead log, which a query of the live table no longer returns.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Call Logs",
        "notes": "Reads the calls table of calllog.db or calls.db, the files the Call logs artifact reads. Date is read "
                 "as Unix milliseconds, Duration as seconds, Type as stored. The Samsung logs.db logs table is not "
                 "read. "
                 "Records are recovered by scripts/sqlite_carver.py, the reader iLEAPP uses for WeChat: older versions of "
                 "the table's leaf pages (the main file's copy of a page the write-ahead log replaced, and every log "
                 "frame, before and after the last checkpoint) and the free space of every version of those pages. A "
                 "record equal to a live row, or to another recovered record, is not reported. A record from free space "
                 "usually lost its rowid, because SQLite overwrites a freed cell's first four bytes; Row ID is then blank. "
                 "Recovered From says which of the two places a row came from. An older page version holds a row as it "
                 "was before an edit or a deletion, so a row here is not by itself evidence that it was deleted. "
                 "Truncated means the record's overflow pages were not in the current image and only its start is "
                 "shown. Tested on synthetic databases built with the carver's own test cases and on synthetic tables; "
                 "no device image was run here.",
        "paths": ('*/com.android.providers.contacts/databases/calllog.db*',
                  '*/com.samsung.android.providers.contacts/databases/calllog.db*',
                  '*/com.android.providers.contacts/databases/calls.db*'),
        "output_types": "standard",
        "artifact_icon": "phone",
    },
}

import sqlite3
import struct

from scripts.ilapfuncs import artifact_processor, convert_unix_ts_to_utc, logfunc
from scripts.sqlite_carver import carve


def _recovered(context, names, table, columns):
    """Rows (values of `columns`, then the carver's provenance) for `table` in each main database file."""
    rows, sources = [], []
    for f in map(str, context.get_files_found()):
        if not f.endswith(names):
            continue
        try:
            records = carve(f, lambda n: n == table)
        except (sqlite3.Error, OSError, IndexError, struct.error) as e:
            logfunc(f"Recovered records: could not read {f}: {e}")
            continue
        sources.append(f)
        source = context.get_relative_path(f)
        for r in records:
            v = r['values']
            rows.append([v.get(c) for c in columns] + [r['kind'], r['origin'], r['rowid'] if r['rowid'] is not None else '',
                                                       'Yes' if r['truncated'] else '', r['page'], r['offset'], source])
    return rows, sources


_PROVENANCE = ('Recovered From', 'Origin', 'Row ID', 'Truncated', 'Page', 'Offset', 'Source File')


def _ms(value):
    return convert_unix_ts_to_utc(value) if isinstance(value, int) and value > 0 else ''


@artifact_processor
def recoveredSms(context):
    rows, sources = _recovered(context, ('mmssms.db',), 'sms',
                               ('date', 'date_sent', 'address', 'type', 'body', 'read', 'thread_id'))
    data_list = [(_ms(r[0]), _ms(r[1]), *r[2:]) for r in rows]
    data_headers = (('Date', 'datetime'), ('Date Sent', 'datetime'), 'Address', 'Type', 'Body', 'Read', 'Thread ID',
                    *_PROVENANCE)
    return data_headers, data_list, '\n'.join(sources)


@artifact_processor
def recoveredCalls(context):
    rows, sources = _recovered(context, ('calllog.db', 'calls.db'), 'calls',
                               ('date', 'number', 'name', 'type', 'duration'))
    data_list = [(_ms(r[0]), *r[1:]) for r in rows]
    data_headers = (('Date', 'datetime'), 'Number', 'Name', 'Type', 'Duration', *_PROVENANCE)
    return data_headers, data_list, '\n'.join(sources)
