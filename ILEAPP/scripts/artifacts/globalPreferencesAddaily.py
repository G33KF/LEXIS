__artifacts_v2__ = {
    "globalPreferences": {
        "name": "Global Preferences",
        "description": "Top-level keys and values of the mobile user's .GlobalPreferences.plist, as stored.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Device Information",
        "notes": "One row per top-level key; nested values are shown as their text form, data values as at most 64 "
                 "bytes of hex. LDMGlobalEnabled is the key MVT reads as the Lockdown Mode switch (Reference: MVT, "
                 "mvt/ios/modules/mixed/global_preferences.py, https://github.com/mvt-project/mvt); no Apple source "
                 "documents it, so it is reported as stored, and a missing key is not evidence that Lockdown Mode was "
                 "off. The Managed Preferences copy is not read. Validated on a private iOS extraction. The plist "
                 "exists in all four "
                 "public images whose path lists ship in admin/data/filepath-lists, but none was available to run here.",
        "paths": ('*/mobile/Library/Preferences/.GlobalPreferences.plist',),
        "output_types": "standard",
        "artifact_icon": "settings",
    },
    "osanalyticsAddaily": {
        "name": "OS Analytics - Network Usage Baseline",
        "description": "Per-process entries of netUsageBaseline in com.apple.osanalytics.addaily.plist, with the "
                       "timestamp and the four counters stored for each process.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Network Usage",
        "notes": "Each netUsageBaseline entry is a process name with a list; the first item is read as the timestamp "
                 "and the next four as Wi-Fi in, Wi-Fi out, cellular (WWAN) in and cellular out, the order MVT uses "
                 "(Reference: MVT, mvt/ios/modules/mixed/osanalytics_addaily.py, https://github.com/mvt-project/mvt). "
                 "No Apple source documents the list, so the counters are reported as stored with no unit, and an "
                 "entry of another shape is shown raw. Validated on a private iOS extraction. A process listed here is one the analytics baseline counted, "
                 "not one the user started. The plist exists in all four public images whose path lists ship in "
                 "admin/data/filepath-lists, which were not available to run here.",
        "paths": ('*/mobile/Library/Preferences/com.apple.osanalytics.addaily.plist',),
        "output_types": "standard",
        "artifact_icon": "activity",
    },
}

from datetime import datetime, timezone

from scripts.ilapfuncs import artifact_processor, get_plist_file_content, logfunc


def _text(value):
    if isinstance(value, bytes):
        return value[:64].hex() + ('…' if len(value) > 64 else '')
    return str(value)


@artifact_processor
def globalPreferences(context):
    data_list, sources = [], []
    for f in map(str, context.get_files_found()):
        pl = get_plist_file_content(f)
        if not isinstance(pl, dict):
            logfunc(f"Global Preferences: {f} is not a readable dictionary plist")
            continue
        source = context.get_relative_path(f)
        data_list.extend((key, _text(pl[key]), source) for key in sorted(pl))
        sources.append(f)
    return ('Key', 'Value', 'Source File'), data_list, '\n'.join(sources)


@artifact_processor
def osanalyticsAddaily(context):
    data_list, sources = [], []
    for f in map(str, context.get_files_found()):
        pl = get_plist_file_content(f)
        if not isinstance(pl, dict):
            logfunc(f"OS Analytics - Network Usage Baseline: {f} is not a readable dictionary plist")
            continue
        sources.append(f)
        source = context.get_relative_path(f)
        baseline = pl.get('netUsageBaseline')
        if not isinstance(baseline, dict):
            logfunc(f"OS Analytics - Network Usage Baseline: no netUsageBaseline dictionary in {f}")
            continue
        for process, values in sorted(baseline.items()):
            if isinstance(values, list) and len(values) >= 5 and isinstance(values[0], datetime):
                ts = values[0] if values[0].tzinfo else values[0].replace(tzinfo=timezone.utc)
                data_list.append((ts, process, *values[1:5], '', source))
            else:
                data_list.append(('', process, '', '', '', '', _text(values), source))
    data_headers = (('Timestamp', 'datetime'), 'Process', 'Wi-Fi In', 'Wi-Fi Out', 'WWAN In', 'WWAN Out',
                    'Raw Entry', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
