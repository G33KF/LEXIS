"""The SIM card of each Android SMS and MMS (LEXIS helper, no artifacts).

The telephony provider records with every message of mmssms.db (tables sms, spam_sms, pdu) the
subscription it went through, sub_id, which is the _id of a row of the siminfo table of
telephony.db: that row gives the card's number, ICCID and name. The SIM columns are filled only
from that link: a message whose sub_id is missing, -1 (none recorded) or ambiguous (two databases
disagree) leaves them blank, and a sub_id siminfo does not describe is named "Subscription <n>".
"""
import os
import sqlite3

from scripts.ilapfuncs import open_sqlite_db_readonly

TELEPHONY = '*/user_de/*/com.android.providers.telephony/databases/telephony.db*'
HEADERS = ('SIM', 'SIM Name', 'SIM ICCID', 'SIM Sub ID')
NOTE = (' LEXIS: SIM, SIM Name and SIM ICCID are the siminfo row of telephony.db whose _id is the message\'s '
        'sub_id (SIM is its number, else its ICCID); SIM Sub ID is sub_id as stored. Blank when sub_id is missing, '
        '-1 or ambiguous, and on records recovered by carving.')

# per artifact: a row -> (the tables its message can come from, its message id, its thread id)
ROWS = {
    'get_sms_mms': lambda r: (('sms', 'spam_sms'), r[5], r[6]),
    'get_sms_mms_mms': lambda r: (('pdu',), r[6], r[7]),
    'get_sms_mms_combined': lambda r: ((r[16],), r[7], r[8]),
}


def _read(path, sql):
    db = open_sqlite_db_readonly(path)
    if db is None:
        return []
    db.row_factory = sqlite3.Row
    try:
        return db.execute(sql).fetchall()
    except sqlite3.Error:
        return []
    finally:
        db.close()


def _subscriptions(files):
    """{sub_id: {(SIM, name, ICCID)}} from every telephony.db, and the files that gave any."""
    subs, used = {}, []
    for f in files:
        if os.path.basename(f) != 'telephony.db':
            continue
        for r in _read(f, 'SELECT * FROM siminfo'):
            def get(name, row=r):
                return str(row[name]).strip() if name in row.keys() and row[name] is not None else ''
            number, iccid = get('number'), get('icc_id')
            subs.setdefault(r['_id'], set()).add((number or iccid, get('display_name') or get('carrier_name'), iccid))
            if f not in used:
                used.append(f)
    return subs, used


def _message_subs(files):
    """{(table, message id, thread id): {sub_id}} from every mmssms.db."""
    found = {}
    for f in files:
        if not f.endswith('mmssms.db') or '/mirror/' in f.replace('\\', '/'):
            continue
        for table in ('sms', 'spam_sms', 'pdu'):
            if 'sub_id' not in {c['name'] for c in _read(f, f'PRAGMA table_info({table})')}:
                continue
            for r in _read(f, f'SELECT _id, thread_id, sub_id FROM {table}'):
                found.setdefault((table, str(r['_id']), str(r['thread_id'])), set()).add(r['sub_id'])
    return found


def with_sim(key, files, headers, rows):
    """The headers and rows of an SMS/MMS artifact with the SIM columns, and the telephony.db files read."""
    files = [str(f) for f in files]
    subs, used = _subscriptions(files)
    found = _message_subs(files)
    out = []
    for row in rows:
        tables, msg, thread = ROWS[key](row)
        ids = set().union(*(found.get((t, str(msg), str(thread)), set()) for t in tables))
        sub = next(iter(ids)) if len(ids) == 1 else None
        if not isinstance(sub, int) or sub < 0:
            out.append(tuple(row) + ('',) * len(HEADERS))
            continue
        known = subs.get(sub, set())
        sim, name, iccid = next(iter(known)) if len(known) == 1 else ('', '', '')
        out.append(tuple(row) + (sim or f'Subscription {sub}', name, iccid, sub))
    return tuple(headers) + HEADERS, out, used
