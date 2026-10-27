"""LEXIS_SimInfo adds MCC and MNC to siminfo as the telephony provider stores them, blank without them."""
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[3]))
from scripts.lexis_artifacts import LEXIS_SimInfo as lexis  # pylint: disable=wrong-import-position

BASE = 'CREATE TABLE siminfo (_id INTEGER PRIMARY KEY, number TEXT, imsi TEXT, display_name TEXT, carrier_name TEXT, ' \
       'iso_country_code TEXT, carrier_id INTEGER, icc_id TEXT'


class Context:
    def __init__(self, files):
        self.files = [str(f) for f in files]

    def get_files_found(self):
        return self.files


class TestSimInfo(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        self.root = pathlib.Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def build(self, uid, extra, rows):
        path = self.root / f'data/user_de/{uid}/com.android.providers.telephony/databases/telephony.db'
        path.parent.mkdir(parents=True)
        db = sqlite3.connect(path)
        db.execute(BASE + extra + ')')
        db.executemany(f"INSERT INTO siminfo VALUES ({', '.join('?' * len(rows[0]))})", rows)
        db.commit()
        db.close()
        return path

    def run_artifact(self, files):
        with patch.object(lexis._base, 'logdevinfo'):  # pylint: disable=protected-access
            return lexis.get_siminfo.__wrapped__(Context(files))  # pylint: disable=no-member

    def test_codes_added(self):
        new = self.build(0, ', mcc_string TEXT, mnc_string TEXT', [
            (1, '+391110000001', '222010000000001', 'A', 'Carrier A', 'it', 1, '8939000000000000001', '222', '01'),
            (2, '', '222880000000002', 'B', 'Carrier B', 'it', 2, '8939000000000000002', None, None)])
        old = self.build(10, ', mcc INTEGER, mnc INTEGER', [
            (1, '+391110000003', '222100000000003', 'C', 'Carrier C', 'it', 3, '8939000000000000003', 222, 1)])
        headers, rows, _ = self.run_artifact([new, old])
        self.assertEqual(headers[-2:], ('MCC', 'MNC'))
        by_icc = {r[7]: r[-2:] for r in rows}
        self.assertEqual(by_icc['8939000000000000001'], ('222', '01'))
        self.assertEqual(by_icc['8939000000000000002'], ('', ''))
        self.assertEqual(by_icc['8939000000000000003'], ('222', '1'))   # the integer as stored, its leading zero gone
        self.assertTrue(all(len(r) == len(headers) for r in rows))

    def test_no_code_columns(self):
        path = self.build(0, '', [(1, '+391110000001', '222010000000001', 'A', 'Carrier A', 'it', 1, '8939000000000000001')])
        headers, rows, _ = self.run_artifact([path])
        self.assertEqual((len(rows), rows[0][-2:], len(rows[0]), len(headers)), (1, ('', ''), 10, 10))


if __name__ == '__main__':
    unittest.main()
