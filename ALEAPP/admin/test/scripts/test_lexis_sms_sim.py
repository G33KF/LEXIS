"""lexis_sms_sim: each SMS and MMS gets the SIM card its sub_id names in telephony.db, blank when the
link is missing or ambiguous; LEXIS_Carved runs it on the SMS artifacts and LEXIS_SmsViews gives
the SMS artifacts without one a conversation view."""
import importlib.util
import os
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))


def _load(name):
    spec = importlib.util.spec_from_file_location(f'test_{name}', ROOT / 'scripts' / 'lexis_artifacts' / f'{name}.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


sms_sim = _load('lexis_sms_sim')


class Context:
    def __init__(self, files):
        self.files = files

    def get_files_found(self):
        return self.files

    @staticmethod
    def get_relative_path(path):
        return path


def build(folder):
    base = os.path.join(folder, 'data', 'user_de', '0', 'com.android.providers.telephony', 'databases')
    os.makedirs(base)
    mmssms, telephony = os.path.join(base, 'mmssms.db'), os.path.join(base, 'telephony.db')
    db = sqlite3.connect(mmssms)
    db.execute('CREATE TABLE sms (_id INTEGER PRIMARY KEY, thread_id INTEGER, address TEXT, person INTEGER, date INTEGER, '
               'date_sent INTEGER, read INTEGER, type INTEGER, body TEXT, service_center TEXT, error_code INTEGER, sub_id INTEGER)')
    db.executemany('INSERT INTO sms VALUES (?, ?, ?, NULL, ?, 0, 1, ?, ?, NULL, 0, ?)', [
        (1, 1, '+15550000001', 1700000000000, 1, 'first sim', 1),
        (2, 1, '+15550000001', 1700000001000, 2, 'second sim', 2),
        (3, 2, '+15550000002', 1700000002000, 1, 'no sim recorded', -1),
        (4, 2, '+15550000002', 1700000003000, 1, 'unknown subscription', 7)])
    db.execute('CREATE TABLE pdu (_id INTEGER PRIMARY KEY, thread_id INTEGER, date INTEGER, date_sent INTEGER, read INTEGER, '
               'msg_box INTEGER, sub_id INTEGER)')
    db.execute('CREATE TABLE part (_id INTEGER PRIMARY KEY, mid INTEGER, seq INTEGER, ct TEXT, cl TEXT, _data TEXT, text TEXT)')
    db.execute('CREATE TABLE addr (_id INTEGER PRIMARY KEY, msg_id INTEGER, address TEXT, type INTEGER)')
    db.commit()
    db.close()
    db = sqlite3.connect(telephony)
    db.execute('CREATE TABLE siminfo (_id INTEGER PRIMARY KEY, icc_id TEXT, sim_id INTEGER, display_name TEXT, '
               'carrier_name TEXT, number TEXT)')
    db.executemany('INSERT INTO siminfo VALUES (?, ?, ?, ?, ?, ?)', [
        (1, '8939000000000000001', 0, 'Work', 'Carrier A', '+391110000001'),
        (2, '8939000000000000002', 1, '', 'Carrier B', '')])
    db.commit()
    db.close()
    return mmssms, telephony


class TestSmsSim(unittest.TestCase):
    def test_each_message_gets_the_sim_its_sub_id_names(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
            files = list(build(folder))
            carved = _load('LEXIS_Carved')
            with patch.object(carved.carving, 'logfunc'):
                headers, rows, _ = carved.get_sms_mms.__wrapped__(Context(files))
            self.assertEqual(tuple(headers[-5:]), ('Recovered',) + sms_sim.HEADERS)
            sims = {r[4]: r[-4:] for r in rows}
            self.assertEqual(sims['first sim'], ('+391110000001', 'Work', '8939000000000000001', 1))
            # no number: the ICCID names the card, the carrier its name
            self.assertEqual(sims['second sim'], ('8939000000000000002', 'Carrier B', '8939000000000000002', 2))
            self.assertEqual(sims['no sim recorded'], ('', '', '', ''))
            self.assertEqual(sims['unknown subscription'], ('Subscription 7', '', '', 7))
            self.assertIn(sms_sim.TELEPHONY, carved.__artifacts_v2__['get_sms_mms']['paths'])

    def test_ambiguous_sub_id_left_blank(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as folder:
            mmssms, telephony = build(folder)
            other = os.path.join(folder, 'other', 'mmssms.db')
            os.makedirs(os.path.dirname(other))
            db = sqlite3.connect(other)
            db.execute('CREATE TABLE sms (_id INTEGER PRIMARY KEY, thread_id INTEGER, sub_id INTEGER)')
            db.execute('INSERT INTO sms VALUES (1, 1, 2)')
            db.commit()
            db.close()
            row = (None, None, 'Received', '+15550000001', 'first sim', 1, 1, None, 1, None, 0)
            _, rows, used = sms_sim.with_sim('get_sms_mms', [mmssms, other, telephony], ('h',) * 11, [row])
            self.assertEqual(rows[0][-4:], ('', '', '', ''))
            self.assertEqual(used, [telephony])

    def test_sms_artifacts_without_a_chat_view_get_one(self):
        views = _load('LEXIS_SmsViews')
        self.assertEqual(views.__artifacts_v2__['smsbackuprestore_messages']['data_views']['conversation']
                         ['conversationDiscriminatorColumn'], 'Number')
        self.assertEqual(views.__artifacts_v2__['recoveredSms']['data_views']['conversation']['directionSentValue'], '2')
        self.assertEqual(views.recoveredSms.__name__, 'recoveredSms')


if __name__ == '__main__':
    unittest.main()
