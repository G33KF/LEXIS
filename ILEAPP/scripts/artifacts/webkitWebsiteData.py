__artifacts_v2__ = {
    "webkitObservedDomains": {
        "name": "WebKit - Observed Domains",
        "description": "Web domains WebKit's resource load statistics recorded per app container: when each was last seen, "
                       "whether and when it had a user interaction, and the redirects recorded between domains.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Safari Browser",
        "notes": "Read from observations.db under an app's Library/WebKit/WebsiteData (Safari, apps embedding a web view, "
                 "and SafariViewService). Columns follow the ObservedDomains, TopFrameUniqueRedirectsTo and "
                 "TopFrameUniqueRedirectsFrom tables as WebKit creates them (Reference: WebKit, "
                 "Source/WebKit/NetworkProcess/Classifier/ResourceLoadStatisticsStore.cpp at "
                 "https://github.com/WebKit/WebKit/blob/3ff034369d0db2dd47eb57dc932ab45547684f70/Source/WebKit/"
                 "NetworkProcess/Classifier/ResourceLoadStatisticsStore.cpp). Last Seen and Last User Interaction are "
                 "the lastSeen and mostRecentUserInteractionTime REAL columns, which that file writes as Unix seconds; "
                 "on navigation lastSeen is rounded down to 5 seconds (ResourceLoadStatistics::reduceTimeResolution, "
                 "timestampResolution 5 s, Source/WebCore/loader/ResourceLoadStatistics.cpp). A value of zero or below "
                 "is left blank: lastSeen can be 0, and domains without hadUserInteraction have been seen holding -1 "
                 "as the interaction time. Prevalent and Very Prevalent are WebKit's own tracker classification, reported as "
                 "stored. App is the container's MCMMetadataIdentifier from its metadata plist; it is blank when the "
                 "database is outside an app container or the plist was not extracted. A domain row records that "
                 "WebKit observed a load from it, not that the user opened it. Validated on a private iOS extraction "
                 "and on a synthetic database built with WebKit's CREATE TABLE "
                 "statements; the four public images list 6 to 46 such databases each but were not available here. "
                 "Older WebKit kept the same statistics in full_browsing_session_resourceLog.plist, read here too: "
                 "browsingStatistics entries with PrevalentResourceOrigin, lastSeen and mostRecentUserInteraction "
                 "(Unix seconds), hadUserInteraction, isPrevalentResource, isVeryPrevalentResource, dataRecordsRemoved "
                 "and topFrameUniqueRedirectsTo/From as origin lists (Reference: WebKit tag Safari-606.1.36, "
                 "Source/WebCore/loader/ResourceLoadStatistics.cpp, ResourceLoadStatistics::encode, and "
                 "Source/WebKit/UIProcess/ResourceLoadStatisticsPersistentStorage.cpp for the file name). The plist "
                 "is source-verified only: it is absent from all four public path lists and was tested on a synthetic "
                 "file. Idea from MVT's webkit_session_resource_log module; no MVT code is used.",
        "paths": ('*/WebKit/WebsiteData/*observations.db*',
                  '*/full_browsing_session_resourceLog.plist',
                  '*/Containers/Data/Application/*/.com.apple.mobile_container_manager.metadata.plist'),
        "output_types": "standard",
        "artifact_icon": "globe",
    },
    "webkitStorageOrigins": {
        "name": "WebKit - Website Storage Origins",
        "description": "Web origins for which an app's WebKit view kept LocalStorage or IndexedDB data, per app container.",
        "author": "@Gear-I, @gk-forensics",
        "creation_date": "2026-10-09",
        "last_update_date": "2026-10-09",
        "requirements": "none",
        "category": "Safari Browser",
        "notes": "Two layouts are read. The older one names each origin in the file or folder name under "
                 "WebsiteData/LocalStorage (<scheme>_<host>_<port>.localstorage) and WebsiteData/IndexedDB/v0 or v1 "
                 "(<scheme>_<host>_<port>), where port 0 is the scheme's default. The newer one keeps each origin under "
                 "WebsiteData/Default/<salted hash>/<salted hash>/ with a file named origin holding the top-level and "
                 "frame origins, which WebKit writes with WTF::Persistence::Encoder (Reference: WebKit, "
                 "Source/WebCore/storage/StorageUtilities.cpp, writeOriginToFile); strings there are a 32-bit length, an "
                 "8-bit flag and the characters (Source/WTF/wtf/persistence/PersistentCoders.cpp). Each origin is "
                 "taken as its scheme and host strings, top-level first, and a file holding fewer than two such pairs "
                 "is shown raw in hex; ports in that layout are not decoded. One row per app, layout and origin, "
                 "however many files sit beneath it. No timestamp is reported. Presence of an origin records that "
                 "the web view stored data for it, not that the user visited it. Source-verified only: tested "
                 "against synthetic files. The patterns match 14 to 53 origin files on three of the four public images "
                 "whose path lists ship in admin/data/filepath-lists, and 45 older-layout entries on the fourth.",
        "paths": ('*/WebKit/WebsiteData/LocalStorage/*.localstorage',
                  '*/WebKit/WebsiteData/IndexedDB/v[01]/*',
                  '*/WebKit/WebsiteData/Default/*/origin',
                  '*/Containers/Data/Application/*/.com.apple.mobile_container_manager.metadata.plist'),
        "output_types": "standard",
        "artifact_icon": "database",
    },
}

import plistlib
import re
import struct

from scripts.ilapfuncs import artifact_processor, convert_unix_ts_to_utc, logfunc, open_sqlite_db_readonly

_CONTAINER = re.compile(r"[/\\]Containers[/\\]Data[/\\]Application[/\\]([0-9A-Fa-f-]{36})[/\\]")
_METADATA = '.com.apple.mobile_container_manager.metadata.plist'


def _apps(files):
    """Container UUID -> bundle identifier, from the extracted container metadata plists."""
    apps = {}
    for f in files:
        if f.endswith(_METADATA) and (m := _CONTAINER.search(f)):
            try:
                with open(f, 'rb') as fp:
                    apps[m.group(1).upper()] = plistlib.load(fp).get('MCMMetadataIdentifier', '')
            except (plistlib.InvalidFileException, ValueError, OSError):
                pass
    return apps


def _app(path, apps):
    m = _CONTAINER.search(path)
    app = apps.get(m.group(1).upper(), '') if m else ''
    return app + (' (SafariViewService)' if 'com.apple.SafariViewService' in path else '')


@artifact_processor
def webkitObservedDomains(context):
    files = [str(f) for f in context.get_files_found()]
    apps = _apps(files)
    data_list, sources = [], []
    for f in files:
        if f.endswith('full_browsing_session_resourceLog.plist'):
            data_list.extend(_resource_log(f, _app(f, apps), context.get_relative_path(f)))
            sources.append(f)
            continue
        if not f.endswith('observations.db'):
            continue
        db = open_sqlite_db_readonly(f)
        if db is None:
            continue
        try:
            cur = db.cursor()
            tables = {r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            if 'ObservedDomains' not in tables:
                logfunc(f"WebKit - Observed Domains: no ObservedDomains table in {f}")
                sources.append(f)
                continue
            names = dict(cur.execute("SELECT domainID, registrableDomain FROM ObservedDomains"))
            redirects_to, redirects_from = {}, {}
            for table, out, a, b in (('TopFrameUniqueRedirectsTo', redirects_to, 'sourceDomainID', 'toDomainID'),
                                     ('TopFrameUniqueRedirectsFrom', redirects_from, 'targetDomainID', 'fromDomainID')):
                if table in tables:
                    for x, y in cur.execute(f"SELECT {a}, {b} FROM {table}"):
                        out.setdefault(x, []).append(names.get(y, str(y)))
            app, source = _app(f, apps), context.get_relative_path(f)
            for (did, domain, seen, interacted, interaction, prevalent, very, removed) in cur.execute(
                    "SELECT domainID, registrableDomain, lastSeen, hadUserInteraction, mostRecentUserInteractionTime, "
                    "isPrevalent, isVeryPrevalent, dataRecordsRemoved FROM ObservedDomains ORDER BY lastSeen"):
                data_list.append((_unix(seen), _unix(interaction),
                                  app, domain, 'Yes' if interacted else 'No', 'Yes' if prevalent else 'No',
                                  'Yes' if very else 'No', removed, ', '.join(sorted(redirects_to.get(did, []))),
                                  ', '.join(sorted(redirects_from.get(did, []))), source))
            sources.append(f)
        finally:
            db.close()
    data_headers = (('Last Seen', 'datetime'), ('Last User Interaction', 'datetime'), 'App', 'Domain',
                    'Had User Interaction', 'Prevalent', 'Very Prevalent', 'Data Records Removed',
                    'Redirected To', 'Redirected From', 'Source File')
    return data_headers, data_list, '\n'.join(sources)


def _resource_log(path, app, source):
    """Rows from an older WebKit's full_browsing_session_resourceLog.plist, in observations.db's columns."""
    try:
        with open(path, 'rb') as fp:
            stats = plistlib.load(fp).get('browsingStatistics', [])
    except (plistlib.InvalidFileException, ValueError, OSError, AttributeError) as ex:
        logfunc(f"WebKit - Observed Domains: could not read {path}: {ex}")
        return []

    def origins(entry, key):
        return ', '.join(sorted(str(o.get('origin', o.get('domain', ''))) for o in entry.get(key, []) if isinstance(o, dict)))

    rows = []
    for e in (e for e in stats if isinstance(e, dict)):
        rows.append((_unix(e.get('lastSeen')), _unix(e.get('mostRecentUserInteraction')), app,
                     e.get('PrevalentResourceOrigin', ''), 'Yes' if e.get('hadUserInteraction') else 'No',
                     'Yes' if e.get('isPrevalentResource') else 'No', 'Yes' if e.get('isVeryPrevalentResource') else 'No',
                     e.get('dataRecordsRemoved', ''), origins(e, 'topFrameUniqueRedirectsTo'),
                     origins(e, 'topFrameUniqueRedirectsFrom'), source))
    return sorted(rows, key=lambda r: str(r[0]))


def _unix(value):
    return convert_unix_ts_to_utc(value) if isinstance(value, (int, float)) and value > 0 else ''


def _legacy_origin(name):
    """'https_www.example.com_0' -> 'https://www.example.com', a non-default port kept."""
    m = re.fullmatch(r"([a-z][a-z0-9+.-]*)_(.*)_(\d+)", name)
    if not m:
        return name
    scheme, host, port = m.groups()
    return f"{scheme}://{host}" + (f":{port}" if port != '0' else '')


def _persistence_strings(data):
    """The length-prefixed strings a WTF::Persistence::Encoder wrote, in order, skipping anything else."""
    out, i = [], 0
    while i + 5 <= len(data):
        length, is8 = struct.unpack_from('<IB', data, i)
        size = length if is8 == 1 else length * 2
        if is8 in (0, 1) and 0 < length < 4096 and i + 5 + size <= len(data):
            raw = data[i + 5:i + 5 + size]
            text = raw.decode('latin-1') if is8 == 1 else raw.decode('utf-16-le', 'replace')
            if text.isprintable():
                out.append(text)
                i += 5 + size
                continue
        i += 1
    return out


def _modern_origins(data):
    s = _persistence_strings(data)
    pairs = [f"{s[k]}://{s[k + 1]}" for k in range(0, len(s) - 1, 2)]
    return (pairs[0], pairs[1]) if len(pairs) >= 2 else ('', 'raw: ' + data[:64].hex())


@artifact_processor
def webkitStorageOrigins(context):
    files = [str(f) for f in context.get_files_found()]
    apps = _apps(files)
    rows, sources = {}, []
    for f in files:
        p = f.replace('\\', '/')
        if (m := re.search(r"/WebsiteData/LocalStorage/([^/]+)\.localstorage$", p)):
            layout, top, frame = 'LocalStorage', '', _legacy_origin(m.group(1))
        elif (m := re.search(r"/WebsiteData/IndexedDB/v[01]/([^/]+)/", p)):
            layout, top, frame = 'IndexedDB', '', _legacy_origin(m.group(1))
        elif re.search(r"/WebsiteData/Default/[^/]+/[^/]+/origin$", p):
            try:
                with open(f, 'rb') as fp:
                    top, frame = _modern_origins(fp.read(65536))
            except OSError as e:
                logfunc(f"WebKit - Website Storage Origins: could not read {f}: {e}")
                continue
            layout = 'Default (origin file)'
        else:
            continue
        key = (_app(f, apps), layout, top, frame)
        if key not in rows:
            rows[key] = context.get_relative_path(f)
            sources.append(f)
    data_list = [(app, layout, top, frame, source) for (app, layout, top, frame), source in sorted(rows.items())]
    data_headers = ('App', 'Layout', 'Top-Level Origin', 'Origin', 'Source File')
    return data_headers, data_list, '\n'.join(sources)
