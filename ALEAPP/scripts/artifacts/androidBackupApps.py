__artifacts_v2__ = {
    "androidBackupApps": {
        "name": "Android Backup - Apps",
        "description": "Apps in an Android backup (adb backup), from the manifest the backup stores for each app: "
                       "package, version code, platform SDK, installer, whether the APK is included, and signatures.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Android Backup",
        "notes": "Read from apps/<package>/_manifest in a backup opened with input type ab. The lines are those "
                 "AppMetadataBackupWriter.getManifestBytes writes, in order: manifest version, package name, "
                 "version code, Build.VERSION.SDK_INT of the device that made the backup, installer package name "
                 "(empty when none), 1 or 0 for an included APK, the number of signatures, then each signature as "
                 "Signature.toCharsString (Reference: AOSP platform_frameworks_base at commit "
                 "1cdfff555f4a21f71ccc978290e2e212e2f8b168, services/backup/java/com/android/server/backup/fullbackup/"
                 "AppMetadataBackupWriter.java). Signature SHA-256 is the SHA-256 of the bytes of each listed "
                 "signature (that string decoded from hex). A manifest of another shape is shown with its lines raw. "
                 "An app listed here was included in the backup; an app that opts out of backup is not listed, so "
                 "the list is not the device's installed apps. Source-verified only: tested on synthetic backups "
                 "built to that source, no backup from a device was run here.",
        "paths": ('*/apps/*/_manifest',),
        "output_types": "standard",
        "artifact_icon": "package",
    },
}

import hashlib

from scripts.ilapfuncs import artifact_processor, logfunc


def _manifest_row(lines):
    if len(lines) < 7 or not lines[2].lstrip('-').isdigit() or not lines[6].isdigit():
        return None
    count = int(lines[6])
    sigs = lines[7:7 + count]
    try:
        digests = [hashlib.sha256(bytes.fromhex(s)).hexdigest() for s in sigs]
    except ValueError:
        digests = sigs
    return (lines[1], lines[2], lines[3], lines[4], 'Yes' if lines[5] == '1' else 'No', count, '\n'.join(digests))


@artifact_processor
def androidBackupApps(context):
    data_list, sources = [], []
    for f in map(str, context.get_files_found()):
        try:
            with open(f, 'rb') as fp:
                lines = fp.read(1 << 20).decode('utf-8', 'replace').split('\n')
        except OSError as ex:
            logfunc(f'Android Backup - Apps: could not read {f}: {ex}')
            continue
        sources.append(f)
        row = _manifest_row([line.rstrip('\r') for line in lines])
        if row is None:
            logfunc(f'Android Backup - Apps: {f} is not a backup manifest of the known shape')
            row = ('', '', '', '', '', '', ' | '.join(lines[:12]))
        data_list.append(row + (context.get_relative_path(f),))
    data_headers = ('Package', 'Version Code', 'Platform SDK', 'Installer', 'APK Included', 'Signatures',
                    'Signature SHA-256', 'Source File')
    return data_headers, sorted(data_list, key=lambda r: r[0]),'\n'.join(sources)
