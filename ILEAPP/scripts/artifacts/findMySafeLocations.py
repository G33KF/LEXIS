__artifacts_v2__ = {
    "findMySafeLocations": {
        "name": "Find My - Safe Locations",
        "description": "Safe locations stored by searchpartyd, decrypted with the BeaconStore key "
                       "from an exported keychain",
        "author": "@AlexisBrignoni, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-12",
        "requirements": "pycryptodome",
        "category": "Find My",
        "notes": "Each file in com.apple.icloud.searchpartyd/SafeLocations holds a plist array of "
                 "nonce, tag and ciphertext; the ciphertext is decrypted with AES-GCM using the "
                 "keychain item with service BeaconStore, and the plaintext is a plist with name, "
                 "latitude, longitude, radius, type, referenceFrame, approvalState and "
                 "associatedBeacons. Records are read only when the keychain was exported (see the "
                 "Keychain artifacts) or supplied with --keychain; without it no row is produced, which does not mean no safe "
                 "location exists. Radius, Type, Reference Frame and Approval State are reported "
                 "as stored, units and meanings not established. Validated on a private iOS "
                 "extraction. The cloudKitMetadata blob is not decoded. A safe location records a place configured in Find My, not a visit to it.",
        "paths": ('*/com.apple.icloud.searchpartyd/SafeLocations/*',
                  '*/keychain-backup.plist',
                  '*/extra/KeychainDump/backup_keychain_v2.plist',
                  '*/Keychains/keychain-2.db*'),
        "output_types": "all",
        "artifact_icon": "map-pin"
    },
}

from scripts.artifacts.findMyBeacons import decrypted_records
from scripts.ilapfuncs import artifact_processor


@artifact_processor
def findMySafeLocations(context):
    data_headers = ('Name', 'Latitude', 'Longitude', 'Radius (as stored)', 'Type (as stored)',
                    'Reference Frame (as stored)', 'Approval State (as stored)', 'Associated Beacons',
                    'Identifier', 'Source File')
    records, decrypted = decrypted_records(context, 'SafeLocations', 'safe locations')
    data_list = []
    for record, p in decrypted:
        data_list.append((p.get('name', ''), p.get('latitude'), p.get('longitude'), p.get('radius'),
                          p.get('type'), p.get('referenceFrame'), p.get('approvalState'),
                          ', '.join(map(str, p.get('associatedBeacons') or [])), p.get('identifier', ''),
                          context.get_relative_path(record)))
    return data_headers, data_list, '\n'.join(records)
