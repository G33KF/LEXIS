__artifacts_v2__ = {
    "airtagAlerts": {
        "name": "Android Airtag Alerts",
        "description": "Parses unknown-tracker (AirTag) alerts (creation and update timestamps, MAC address, device type and alert status) from the Google Play services personalsafety database.",
        "author": "@AlexisBrignoni, @AlexisBrignoni, Codex",
        "creation_date": "2023-08-18",
        "last_update_date": "2025-03-16",
        "requirements": "none",
        "category": "Airtag Detection",
        "notes": "",
        "paths": '*/com.google.android.gms/databases/personalsafety_db*',
        "output_types": "standard",
        "artifact_icon": "bell-ringing",
        "sample_data": {
            "anne_a15": "Android 15 | com.google.android.gms | 0 rows",
            "hc_pixel8pro_a16": "Android 16 | com.google.android.gms vc 253830035 | 0 rows",
            "kevin_pocox7_a15": "Android 15 | com.google.android.gms | 4 rows",
            "pixel7a_a14": "Android 14 | com.google.android.gms vc 242632038 | 2 rows",
            "samsunga53_a14": "Android 14 | com.google.android.gms | 0 rows",
            "samsungs20_a13": "Android 13 | com.google.android.gms | 0 rows",
            "sharon_a14": "Android 14 | com.google.android.gms vc 242835039 | 0 rows",
            "userb2_a13": "Android 13 | com.google.android.gms | 0 rows",
        }
    },
    "airtagScans": {
        "name": "Android Airtag Scans",
        "description": "Parses unknown-tracker (AirTag) scan records (timestamps, MAC address, state, and three values decoded without a schema from the bleScan and locationScan blobs) from the Google Play services personalsafety database.",
        "author": "@AlexisBrignoni, @AlexisBrignoni, Codex",
        "creation_date": "2023-08-18",
        "last_update_date": "2026-10-04",
        "requirements": "none",
        "category": "Airtag Detection",
        "notes": "Possible RSSI is field 2 of the bleScan blob. Possible Latitude and Possible "
                 "Longitude are fields 4 and 5 of the locationScan blob divided by 10,000,000. "
                 "Both blobs are decoded without a schema, and the meaning of those fields and "
                 "the scale are not established by a published schema. On the 86 rows of "
                 "kevin_pocox7_a15, pixel7a_a14 and sharon_a14, field 2 was a whole number from "
                 "-98 to -27, and fields 4 and 5 divided by 10,000,000 fell within -90 to 90 and "
                 "-180 to 180 on every row. The values were not compared with another location "
                 "record of those devices, so no map output is produced from them. Record Source "
                 "says where a row was read. Live table rows come from the Scan table as the "
                 "database presents it. WAL frame rows are Scan records carved from the leaf "
                 "pages of every frame in personalsafety_db-wal, including frames the database "
                 "no longer presents, and are reported only when no live row has the same MAC "
                 "address, timestamps and state; carved records sharing those four values are "
                 "reported once, with the first frame. A carved record is accepted only when it decodes to "
                 "the table's six columns with a text MAC address, two integers, a text state and two "
                 "blobs or nulls; payloads that spill onto overflow pages are not reassembled. A carved row "
                 "records that the database once held it, not when it was removed.",
        "paths": '*/com.google.android.gms/databases/personalsafety_db*',
        "output_types": "standard",
        "artifact_icon": "radar",
        "sample_data": {
            "anne_a15": "Android 15 | com.google.android.gms | 0 rows",
            "hc_pixel8pro_a16": "Android 16 | com.google.android.gms vc 253830035 | 0 rows",
            "kevin_pocox7_a15": "Android 15 | com.google.android.gms | 43 rows",
            "pixel7a_a14": "Android 14 | com.google.android.gms vc 242632038 | 39 rows",
            "samsunga53_a14": "Android 14 | com.google.android.gms | 0 rows",
            "samsungs20_a13": "Android 13 | com.google.android.gms | 0 rows",
            "sharon_a14": "Android 14 | com.google.android.gms vc 242835039 | 4 rows",
            "userb2_a13": "Android 13 | com.google.android.gms | 0 rows",
        }
    },
    "airtagLastScan": {
        "name": "Android Airtag Last Scan",
        "description": "Reports field 1 of the personalsafety_info protobuf file, read as Unix milliseconds.",
        "author": "@AlexisBrignoni, @AlexisBrignoni, Codex",
        "creation_date": "2023-08-18",
        "last_update_date": "2026-10-04",
        "requirements": "none",
        "category": "Airtag Detection",
        "notes": "The file is decoded without a schema. That field 1 is the time of the last scan "
                 "is not established by a published schema. On kevin_pocox7_a15, pixel7a_a14 and "
                 "sharon_a14, field 1 read as Unix milliseconds was 200 seconds after, 23 seconds "
                 "before and 72 seconds before the newest creationTimestampMillis in the Scan "
                 "table of personalsafety_db, so it did not equal the time of the newest stored "
                 "scan record on any of the three.",
        "paths": '*/files/personalsafety/shared/personalsafety_info.pb',
        "output_types": "standard",
        "artifact_icon": "clock-search",
        "sample_data": {
            "anne_a15": "Android 15 | com.google.android.gms | 1 row",
            "hc_pixel8pro_a16": "Android 16 | com.google.android.gms vc 253830035 | 1 row",
            "kevin_pocox7_a15": "Android 15 | com.google.android.gms | 1 row",
            "pixel7a_a14": "Android 14 | com.google.android.gms vc 242632038 | 1 row",
            "sharon_a14": "Android 14 | com.google.android.gms vc 242835039 | 1 row",
            "userb2_a13": "Android 13 | com.google.android.gms | 1 row",
        }
    },
    "airtagPassiveScan": {
        "name": "Android Airtag Passive Scan",
        "description": "Reports field 1 of the personalsafety_optin protobuf file as stored.",
        "author": "@AlexisBrignoni, @AlexisBrignoni, Codex",
        "creation_date": "2023-08-18",
        "last_update_date": "2026-10-04",
        "requirements": "none",
        "category": "Airtag Detection",
        "notes": "The file is decoded without a schema, and what each value of field 1 means is not "
                 "established by a published schema, so the number is reported as stored with no "
                 "label. The file was present on one of the 20 registered Android zip extractions "
                 "(russell_a14), where it held field 1 only; the state of the unknown tracker "
                 "alerts setting on that device is not recorded with the image.",
        "paths": '*/files/personalsafety/shared/personalsafety_optin.pb',
        "output_types": "standard",
        "artifact_icon": "radar-2",
        "sample_data": {
            "russell_a14": "Android 14 | com.google.android.gms | 1 row",
        }
    }
}


import os
import struct

from scripts.ilapfuncs import decode_protobuf
from scripts.ilapfuncs import artifact_processor, \
    get_file_path, get_sqlite_db_records, get_binary_file_content, \
    convert_unix_ts_to_utc, does_column_exist_in_db, logfunc


def _varint(data, offset):
    """A SQLite varint at offset: (value, next offset)."""
    value = 0
    for index in range(9):
        byte = data[offset + index]
        if index == 8:
            return (value << 8) | byte, offset + 9
        value = (value << 7) | (byte & 0x7F)
        if byte < 0x80:
            return value, offset + index + 1
    raise ValueError('unterminated varint')


def _record_values(payload):
    """The column values of a SQLite record payload, in table column order."""
    header_length, offset = _varint(payload, 0)
    serial_types = []
    while offset < header_length:
        serial_type, offset = _varint(payload, offset)
        serial_types.append(serial_type)
    values, position = [], header_length
    for serial_type in serial_types:
        if serial_type == 0:
            values.append(None)
        elif 1 <= serial_type <= 6:
            size = (1, 2, 3, 4, 6, 8)[serial_type - 1]
            values.append(int.from_bytes(payload[position:position + size], 'big', signed=True))
            position += size
        elif serial_type == 7:
            values.append(struct.unpack('>d', payload[position:position + 8])[0])
            position += 8
        elif serial_type in (8, 9):
            values.append(serial_type - 8)
        elif serial_type >= 12:
            size = (serial_type - 12) // 2
            raw = payload[position:position + size]
            position += size
            values.append(raw if serial_type % 2 == 0 else raw.decode('utf-8', 'replace'))
        else:
            raise ValueError('reserved serial type')
    if position > len(payload):
        raise ValueError('record runs past its cell')
    return values


def _wal_records(wal_path):
    """(frame number, values) for every table leaf cell in every frame of a WAL file.

    Every frame is read, not only the latest copy of each page, so records the
    database no longer presents are included.
    """
    try:
        with open(wal_path, 'rb') as handle:
            data = handle.read()
    except OSError as error:
        logfunc(f'Airtag: could not read {wal_path}: {error}')
        return
    if len(data) < 32 or struct.unpack('>I', data[:4])[0] not in (0x377F0682, 0x377F0683):
        return
    page_size = struct.unpack('>I', data[8:12])[0]
    page_size = 65536 if page_size == 1 else page_size
    for frame in range((len(data) - 32) // (page_size + 24)):
        page = data[32 + frame * (page_size + 24) + 24:32 + (frame + 1) * (page_size + 24)]
        if len(page) < 8 or page[0] != 0x0D:
            continue
        for cell in range(struct.unpack('>H', page[3:5])[0]):
            try:
                pointer = struct.unpack('>H', page[8 + 2 * cell:10 + 2 * cell])[0]
                size, offset = _varint(page, pointer)
                if size > page_size - 35:
                    continue  # payload spills to overflow pages, which are not followed
                _, offset = _varint(page, offset)
                yield frame, _record_values(page[offset:offset + size])
            except (IndexError, ValueError, struct.error):
                continue


def _is_scan_record(values):
    return (len(values) == 6 and isinstance(values[0], str)
            and all(isinstance(v, int) for v in values[1:3])
            and isinstance(values[3], str)
            and all(v is None or isinstance(v, bytes) for v in values[4:]))


@artifact_processor
def airtagAlerts(context):
    files_found = context.get_files_found()
    source_path = get_file_path(files_found, "personalsafety_db")
    data_list = []

    # older GmsCore personalsafety_db schemas do not carry these two columns
    device_type_col = 'deviceType' if does_column_exist_in_db(
        source_path, 'DeviceData', 'deviceType') else "NULL AS deviceType"
    optional_data_col = 'optionalDeviceData' if does_column_exist_in_db(
        source_path, 'DeviceData', 'optionalDeviceData') else "NULL AS optionalDeviceData"

    query = f'''
    SELECT
        creationTimestampMillis,
        lastUpdatedTimestampMillis,
        macAddress,
        {device_type_col},
        {optional_data_col},
        alertLifecycleId,
        alertStatus
    FROM DeviceData
    '''

    data_headers = (
        ('Creation Timestamp', 'datetime'), 
        ('Last Updated Timestamp', 'datetime'), 
        'MAC Address', 
        'Device Type', 
        'Optional Device Data', 
        'Alert Life Cycle ID', 
        'Alert Status')

    db_records = get_sqlite_db_records(source_path, query)

    for record in db_records:
        creation_timestamp = convert_unix_ts_to_utc(record[0])
        last_updated_timestamp = convert_unix_ts_to_utc(record[1])
        data_list.append((
            creation_timestamp, 
            last_updated_timestamp, 
            record[2], record[3], record[4], record[5], record[6]
        ))
    
    return data_headers, data_list, source_path

        
@artifact_processor
def airtagScans(context):
    files_found = context.get_files_found()
    source_path = get_file_path(files_found, "personalsafety_db")
    data_list = []
    
    query = '''
    SELECT 
        creationTimestampMillis,
        lastUpdatedTimestampMillis,
        macAddress,
        state,
        blescan,
        locationScan 
    FROM Scan
    '''

    data_headers = (
        ('Creation Timestamp', 'datetime'),
        ('Last Updated Timestamp', 'datetime'),
        'MAC Address',
        'State',
        'Possible RSSI',
        'Possible Latitude',
        'Possible Longitude',
        'Record Source')

    records = [(tuple(r), 'Live table') for r in get_sqlite_db_records(source_path, query)]
    seen = {r[:4] for r, _ in records}
    wal_path = f'{source_path}-wal'
    if source_path and os.path.exists(wal_path):
        for frame, values in _wal_records(wal_path):
            if not _is_scan_record(values):
                continue
            mac, created, updated, state, blescan, location_scan = values
            key = (created, updated, mac, state)
            if key in seen:
                continue
            seen.add(key)
            records.append(((created, updated, mac, state, blescan, location_scan),
                            f'WAL frame {frame}'))

    for record, record_source in records:
        _, _, mac_address, state, blescan, location_scan = record
        creation_timestamp = convert_unix_ts_to_utc(record[0])
        last_updated_timestamp = convert_unix_ts_to_utc(record[1])

        blescan_proto = {}
        if blescan:
            blescan_proto, _ = decode_protobuf(blescan)
            blescan_proto = blescan_proto or {}
        posrssi = blescan_proto.get('2', '')

        # a scan row without a location fix carries no lat/long fields
        location_scan_proto = {}
        if location_scan:
            location_scan_proto, _ = decode_protobuf(location_scan)
            location_scan_proto = location_scan_proto or {}
        lat_raw = location_scan_proto.get('4')
        lon_raw = location_scan_proto.get('5')
        latitude = lat_raw / 1e7 if isinstance(lat_raw, (int, float)) else ''
        longitude = lon_raw / 1e7 if isinstance(lon_raw, (int, float)) else ''

        data_list.append((
            creation_timestamp, last_updated_timestamp, mac_address,
            state, posrssi, latitude, longitude, record_source))

    return data_headers, data_list, source_path

        
@artifact_processor
def airtagLastScan(context):
    files_found = context.get_files_found()
    source_path = get_file_path(files_found, "personalsafety_info.pb")
    data_list = []
    
    proto_data = get_binary_file_content(source_path)

    lastscan, _ = decode_protobuf(proto_data)
    lastscan = (lastscan['1'])
    lastscan = convert_unix_ts_to_utc(lastscan)
    data_list.append((lastscan, ))

    data_headers = (('Field 1 Timestamp', 'datetime'),)

    return data_headers, data_list, source_path


@artifact_processor
def airtagPassiveScan(context):
    files_found = context.get_files_found()
    source_path = get_file_path(files_found, "personalsafety_optin.pb")
    data_list = []
    
    proto_data = get_binary_file_content(source_path)

    pass_scan, _ = decode_protobuf(proto_data)
    pass_scan = (pass_scan['1'])

    data_list.append((pass_scan, ))

    data_headers = ('Field 1 (as stored)', )

    return data_headers, data_list, source_path
