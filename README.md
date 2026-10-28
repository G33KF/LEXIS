# LEXIS — LEAPP Reader

LEXIS is a Windows graphical analysis environment for **ALEAPP** (Android) and **iLEAPP** (iOS).
LEAPP parses the extraction and reports each artifact on its own; LEXIS brings those results
together, so you can search, correlate and tag across artifacts and across devices. Both tools
are bundled in a single portable exe: open an existing LEAPP report, or load a new extraction
and LEXIS runs the right tool for you. No Python, nothing to install.

On top of the LEAPP reports it adds:
- **LEAPP artifacts, organized**: every artifact from the LAVA output, sorted into categories
  and data types (Messages › SMS, Calls › Call Log, ...), or grouped by application. Tables
  load as you scroll, with column filters, `/regex/` search, inline media and export. Cells
  holding JSON, XML, plists or protobuf (decoded by recognized schema) open as a tree
- **Multi-device cases**: joint timeline and map, co-presence, common contacts, search across devices
- **Chats, timeline, map, media gallery and documents** in one window, with one search syntax
- **Deleted messages recovered**: the messaging apps' databases carved (WAL and free pages) and
  WhatsApp backups decrypted with the keys found in the extraction; recovered rows are marked
- **The extraction file system**: file browser, SQLite reader with WAL and free-page carving, plist reader,
  offline OCR, search inside the files' contents
- **Indicators**: matching of STIX2 indicators and hash sets, root/jailbreak traces, crypto addresses and seed phrases
- **Verifiable exports**: tags and notes, SHA-256 manifests, signed portable cases
- **AI analysis**: off by default. Plug in Ollama, LM Studio or any OpenAI-compatible
  model to analyze, explain, summarize and translate, read images (OCR), transcribe audio and
  analyze videos (frames and audio track on one timeline), with every answer citing the records it used. A built-in MCP server lets any MCP client
  query the open case

**Supported inputs**
- Existing LEAPP report folders
- File system extractions: unpacked folder, zip, tar, tar.gz
- Extractions from major third-party forensic tools (UFD, GK)
- iTunes/Finder backups, including encrypted backups from iOS 10.2 and later
- Android adb backups (`.ab`), plain or encrypted

Evidence is never modified: databases are opened from temporary copies, so the original
files keep their hashes. Tags and notes are saved in `_reader_tags.db` beside the case,
AI results in `_reader_ai.db`.

> [!NOTE]
> *This is a first release and may still need some work, but I'm happy to share it.*
>
> *For details about the logic and architecture, see https://g33kf.github.io/LEXIS/*


## Download

Get `LEXIS-<version>-win-x64.exe` from the [Releases](../../releases) page. The `.sha256` file
beside it is the checksum to verify the download against:

```powershell
Get-FileHash .\LEXIS-1.2.5-win-x64.exe -Algorithm SHA256
```

Windows 10/11 x64 is required. Nothing is installed: the exe runs from any folder. The WebView2
runtime, which Windows 11 already has, is needed only for the HTML previews and the map.

## What the reader adds on top of LEAPP

### Cases and devices
- **Investigation case** (`.lexiscase`): several devices opened together, with a name, a proceeding
  number, and a label and colour per device. Devices can be renamed, reordered, moved between
  cases or removed. Reports opened on their own join a default case.
- **Case Summary** per device, in tabs (Overview, Device, SIM cards, Owner & accounts, Users,
  Report): the device, every SIM card recorded with its first and last date, wipes and setups,
  Android users and profiles, the extraction, the user contents by kind, and whether LEXIS or
  ALEAPP/iLEAPP made the report. The **Owner** tab lists each name, number, address and account
  that identifies the user.
- **Accounts Overview**: one row per account of each app or service, merged from the artifacts
  that record it; every value names the artifact and column it came from.
- **Case tree** with artifacts grouped by category, or by application in a parallel view. Tables
  load while you scroll and have column menus, `/regex/` filters, and CSV/JSON export.

### Reading the data
- **Chats** with message bubbles, replies quoted and linked, reactions, inline media, shared
  positions, participants matched to their contact record, and search inside a chat. Private and
  group chats are told apart, and chats are split by the user's account, SIM card or backup.
  Recovered (deleted) messages are marked, and WeChat voice messages (SILK) play. Each chat
  exports to a paged HTML report in a WhatsApp or Telegram look.
- **Timeline** built from every datetime column, filtered by period and by artifact.
- **Map** (Leaflet) of every row that has coordinates, with the route, pictures taken at a place,
  and zones of interest with visits and first and last time.
- **Media gallery** of the extraction's media, with every chat message each file was sent or
  received in (copies with the same bytes included), EXIF data, filters by app, direction and
  date, and colour tags. A **media viewer** zooms pictures, plays video and audio, and shows PDFs
  in a sandboxed viewer.
- **Documents**: every PDF, Office, OpenDocument, RTF and iWork file of the extraction in one
  list, typed from its first bytes, with the artifacts that read it.
- **Statistics**: user activity by weekday and hour, busiest days, top parties, contacts, apps and places.
- **Details** pane, in tabs: a resizable preview, every embedded metadata of a media file, the
  record's source on the device, and any number decoded as a date under 11 common timestamp
  epochs; then the text read from the file and the AI analyses of the item.
- **Search**: one syntax for search, filters, timeline and chats (`a b`, `a | b`, `-a`, an exact
  phrase, `/regex/`), per device or across devices, with a removable history of searches.
  Results come as a table, filterable by any column. Search also reads the text inside the
  extraction's files (PDF, Office, SQLite, plists, text) and the AI results; keyword lists can
  be loaded from a file, in any language.

### Across devices
- **Joint timeline** and **joint map** of the chosen devices.
- **Co-presence**: places and times where two or more devices were together.
- **Common contacts**: numbers and accounts shared by the devices, from contacts, calls and
  messages, matched with or without the country code.

### The extraction itself
- **Extraction Files**: a file browser by device path. File types are identified by their magic
  bytes. Archives can be browsed inside, nested ones too. JSON, XML and HTML are shown formatted,
  and the view has hex, text and thumbnails. You can search file contents and see which files
  the artifacts read. Files can be **tagged**.
- **SQLite reader**: any SQLite file opens from a temporary copy with its `-wal`, `-shm` and
  `-journal` files. It shows tables, views, indexes and schema, plus records carved from the
  WAL and from free pages.
- **Plist reader**: XML and binary plists as a tree, and NSKeyedArchiver archives resolved into
  their objects.
- **OCR, offline**: pictures and scanned PDFs read with Tesseract (carried by LEXIS) or the OCR
  built into Windows. The text goes to the search.
- **Content manifest**: a SHA-256 of every file and one root hash over the whole extraction.
  The root hash is the same for a folder and for its zip.
- **Export of a data source**: verified copies of an artifact's source files, with
  `SHA256SUMS.txt` and `PROVENANCE.txt`.
- The acquisition file beside an extraction (`.ufd`) is read for its device, extraction and
  hash details.

### Indicators (IOC)
- STIX2 indicator files and MD5/SHA-1/SHA-256 hash sets, matched against every text cell and
  against the extraction's file list.
- Root and jailbreak traces. Weakened Android security settings and the age of the security patch.
- Cryptocurrency addresses and BIP-39 seed phrases, reported only when their checksum holds.
  Address types: Bitcoin, Litecoin, Dogecoin, Tron, Ethereum (EIP-55) and Monero.
- Tagging by keyword or regex rules.

### Tags, notes and exports
- **Tags and notes** on records, chats, media and extraction files, with colours and keyboard
  shortcuts. **Tagged Items** lists them all.
- **Exports log**: every export of the session, with its time, its source device and where it
  went. The log can be saved as CSV.
- **Portable case**: a zip with the case and the reader beside it. It can carry:
  - the source files of the included artifacts, or every file the parsing read;
  - the tagged extraction files.
  Every file is checked by SHA-256 against the copies LEAPP made. `SHA256SUMS.txt` is signed
  with the examiner's ECDSA P-256 key, which is kept in the Windows key store. **Verify
  package** checks a received portable case.

### AI (optional, local first)
- Off until a model server is configured. **Ollama** and **LM Studio** are the defaults; any
  OpenAI-compatible endpoint can be added, with API keys protected by DPAPI. The **Claude CLI**
  can also act as the model server.
- On checked records, chats or media: summarize, translate, explain, describe images with OCR,
  and run a complete analysis of chosen data categories against an objective. Each answer cites
  the records it used.
- **Explain** on a record explains what it is and each value it holds.
- **Video analysis**: frames taken every N % of the duration or every N seconds, described by
  the vision model, and the audio track transcribed, on one timeline, with the SHA-256 of each
  frame sent.
- The examiner can correct or exclude any AI analysis; the model's text is kept, nothing is deleted.
- Audio transcription with **whisper.cpp** on this machine, set up from the AI settings (each
  file kept only if its SHA-256 is the published one). Opus, AMR, M4A and MP3 are converted by
  the ffmpeg carried by LEXIS.
- **MCP server** inside the open LEXIS: LM Studio, Claude Desktop or any MCP client can read
  the open case through it.

## Modules added to ALEAPP and iLEAPP

The two tools are vendored in `ALEAPP/` and `ILEAPP/`:

| Vendored copy | Upstream source | Base |
| --- | --- | --- |
| `ALEAPP/` | [abrignoni/ALEAPP](https://github.com/abrignoni/ALEAPP) | commit `4381852` |
| `ILEAPP/` | [abrignoni/iLEAPP](https://github.com/abrignoni/iLEAPP) | `2026.4.5-dev` |

On top of those bases, this repository adds the modules below. Each module's `notes` field
says what was validated on real data and what was checked only on synthetic files.
The `LEXIS_*` modules live in `scripts/lexis_artifacts/` and load after the tool's own
artifacts; one that declares a standard artifact replaces it, so nothing is reported twice and
the original modules stay untouched.

### ALEAPP

| Module | Artifacts |
| --- | --- |
| `android_backup.py`, `androidBackupApps.py` | `-t ab` input: adb backups, plain or AES-256 encrypted, read following the AOSP format · Android Backup - Apps |
| `sqlite_carver.py`, `recoveredSmsCalls.py` | SMS Messages and Call Logs - Recovered Records: older page versions in the WAL and free space |
| `weChat.py` (extended) | Calls · Transfers and Red Envelopes · Shared Locations · Moments · Moments Likes and Comments · Group Members · Account ID/Name per message |
| `signalAndroid.py` (extended) | Signal - Reactions |
| `Threema.py` (extended) | Groups and Distribution Lists · Group Messages · Reactions |
| `chromeFavicons.py`, `chromeShortcuts.py`, `chromePreferencesAccounts.py` | Chrome Favicons · Omnibox Shortcuts · Preferences - Accounts |
| `googlePasswordManager.py`, `googleTranslate.py` | Google Password Manager - Saved Logins · Google Translate - History |
| `firebaseInstallations.py` | Firebase Installation Tokens |
| `playProtectVerifyApps.py` | Play Protect - APK Records · Installation Attempts |
| `persistentPropProto.py` | Persistent Properties · Boot Reason History |
| `bunq.py`, `huarenjie.py` | bunq Account Holders and Monetary Accounts · Huarenjie Account |
| `wellbeing.py`, `accounts_ce.py` (extended) | Digital Wellbeing - App Settings · Account Extras |
| `calllog.py`, `airtagAndroid.py`, `smsmmsBackup.py` (fixes) | Honor call log layout · AirTag WAL carving · SMS/MMS backup paths |
| `LEXIS_WhatsApp.py` | WhatsApp and WhatsApp Business, per app and per Android user · WhatsApp Backups (`msgstore*.db.crypt12/14/15`, decrypted with the key files in the extraction) · Backups - One To One / Group Messages · deleted messages recovered from backups · message types named and a *Content* column · Contact Cards · View Once Media |
| `LEXIS_Carved.py`, `lexis_carving.py` | Deleted messages of the messaging apps recovered by carving (page versions, WAL, free space), in the same tables with a *Recovered* column |
| `LEXIS_WeChatMultiAccount.py` | WeChat: every account on the device, each database opened with the IMEIs WeChat keeps |
| `LEXIS_Signal.py` | Signal - Messages with edits, deletions, mentions and stories · Message Edits · Stories |
| `LEXIS_RecoveryApps.py` | Recovery Apps - Captured Messages (notification-log and "recover deleted messages" apps) |
| `LEXIS_Session.py` | Session - Messages, decrypted with the key from `secrets.json` |
| `LEXIS_SimInfo.py`, `lexis_sms_sim.py`, `LEXIS_SmsViews.py` | siminfo with MCC and MNC · SMS and MMS split per SIM card · chat view for SMS Backup and Restore and recovered SMS |

### iLEAPP

| Module | Artifacts |
| --- | --- |
| `sqlite_carver.py`, `weChat.py` (extended) | WeChat - Recovered Messages (WAL page versions, free space, search index) · WeChat - Moments · zstd message bodies |
| `webkitWebsiteData.py` | WebKit - Observed Domains (`observations.db` and the older `resourceLog.plist`) · Website Storage Origins |
| `globalPreferencesAddaily.py` | Global Preferences · OS Analytics - Network Usage Baseline |
| `shortcutsLibrary.py` | Shortcuts - Library |
| `findMySafeLocations.py` | Find My - Safe Locations, decrypted with the BeaconStore keychain key |
| `googleAppSearchHistory.py` | Google App - Search History · Web History |
| `keychain.py` (extended) | Keychain Passwords: all password items |
| `iosDiagnosticReports.py` (extended) | iOS Diagnostic Reports - OS Versions (`.ips` and Analytics-Journal metadata) |
| `lockdownEvents.py` (fix) | lockdownd upgrade detection from `perform_migration` |
| `LEXIS_WhatsApp.py` | WhatsApp and WhatsApp Business, each with the account's own JID and name |
| `LEXIS_Carved.py`, `lexis_carving.py` | Deleted messages of the messaging apps recovered by carving |
| `telegramMesssages.py`, `telegramAccounts.py` (extended) | Telegram Messages by account |
| `ileapp.py`, `search_files.py` (fixes) | encrypted iTunes backup password taken from an environment variable or a file and kept out of the report log; a run with no password and no terminal stops instead of waiting for input |

Not supported: encrypted legacy iTunes backups (`Manifest.mbdb`, iOS before 10.2).

## Build

You need the .NET 10 SDK and Python 3.10+. Each LEAPP needs its own virtual environment,
`python -m venv .venv`, created inside `ALEAPP\` and inside `ILEAPP\`.

```powershell
.\build.ps1              # PyInstaller builds of ALEAPP and iLEAPP, then the reader with both embedded
.\build.ps1 -SkipLeapp   # reuse the last LEAPP builds
dotnet test              # Core tests
```

The output is `release\LEXIS-<version>-win-x64.exe` plus its `.sha256`. The version comes
from `<Version>` in `src/AleappReader.App/AleappReader.App.csproj`. Release files are
published on GitHub Releases and are never committed.

## Layout

```
src/AleappReader.Core/        case reading, search, indicators, manifest (no UI)
src/AleappReader.App/         WPF app (MVVM)
tests/AleappReader.Core.Tests/
ALEAPP/  ILEAPP/              vendored LEAPP tools with the modules above
build.ps1                     builds the release
release/                      build output (ignored by git)
```

## Contact

- E-mail: [info@gk-forensics.com](mailto:info@gk-forensics.com)
- LinkedIn: [gkforensics](https://it.linkedin.com/in/gkforensics)

## Credits

LEXIS is built on the **LEAPP project by Alexis Brignoni and its contributors** (MIT):
- [ALEAPP](https://github.com/abrignoni/ALEAPP)
- [iLEAPP](https://github.com/abrignoni/iLEAPP)

Each vendored tool keeps its own licence and third-party components, such as
mister_skinnylegs in ALEAPP and the Unified Log parser in iLEAPP, in its folder.

These projects were studied for features. Code was ported only where the licence allows, with attribution. The other features were
reimplemented from the idea only.

| Project | Licence | What it contributed |
| --- | --- | --- |
| [MVT – Mobile Verification Toolkit](https://github.com/mvt-project/mvt) | MVT License 1.1 | Idea only: STIX2 indicator matching, root/jailbreak heuristics, security-settings checks, iOS stores (WebKit, addaily, Global Preferences, Shortcuts, Analytics-Journal) |
| [mvt-indicators](https://github.com/mvt-project/mvt-indicators) | — | Indicator feed format (files are loaded by the examiner, nothing is downloaded) |
| [FFS-Explorer](https://github.com/KlasScripts/FFS-Explorer) | GPL | Idea only: raw file browser, magic-byte typing, plist view, WAL/free-page SQLite recovery, timestamp decoding, hash sets, content search |
| [SignalToALEAPP](https://github.com/AndroidIosForensik/SignalToALEAPP) | MIT | Signal reactions |
| [ThreemaToALEAPP](https://github.com/AndroidIosForensik/ThreemaToALEAPP) | MIT | Threema groups, distribution lists and reactions |
| [peach-forensics](https://github.com/kalink0/peach-forensics) | Apache-2.0 | Regex search, daily-activity histogram, rule-based tagging, JSON export |
| [android-crypto-wallet-forensics](https://github.com/GabrielNeves24/android-crypto-wallet-forensics) | none stated | Idea only: wallet-address and seed-phrase scanning validated by checksum |
| [verichron-epoch](https://github.com/vermiliontides/verichron-epoch) | none stated | Idea only: evidence manifest with one root hash |

Data, formats and libraries:
- [BIP-39 English wordlist](https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt), embedded.
- Android backup format from [AOSP frameworks/base](https://android.googlesource.com/platform/frameworks/base) (Apache-2.0).
- WebKit `ResourceLoadStatistics` formats from [WebKit](https://github.com/WebKit/WebKit) sources.
- [Leaflet](https://leafletjs.com) (BSD-2), [Leaflet.markercluster](https://github.com/Leaflet/Leaflet.markercluster) (MIT) and [vis-network](https://github.com/visjs/vis-network) (Apache-2.0/MIT), loaded version-pinned from unpkg.
- Map tiles © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors. They can be switched off for offline use.
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT) and [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).

Carried inside the exe (the full notices are in [NOTICE](NOTICE)):
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) (MIT) with [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) (Apache-2.0) and [SQLite](https://sqlite.org/copyright.html) (public domain).
- [PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0): text of PDF pages.
- [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) (Apache-2.0) and [XmpCore](https://github.com/drewnoakes/xmp-core-dotnet) (BSD-3, from the Adobe XMP Toolkit): media metadata.
- [Tesseract OCR](https://github.com/tesseract-ocr/tesseract) (Apache-2.0), [Leptonica](https://github.com/DanBloomberg/leptonica) (BSD-2) and the [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) models (Apache-2.0), from the [Tesseract NuGet package](https://github.com/charlesw/tesseract).
- [FFmpeg](https://github.com/FFmpeg/FFmpeg) 9.0.2 (LGPL-2.1+, built without GPL or non-free parts by `tools/ffmpeg/build.sh`).
- The SILK decoder from [kn007/silk-v3-decoder](https://github.com/kn007/silk-v3-decoder) (MIT; SILK SDK © Skype Limited, BSD-style), for WeChat voice messages.
- ALEAPP and iLEAPP run on an embedded Python runtime ([PSF licence](https://docs.python.org/3/license.html)) packaged with [PyInstaller](https://github.com/pyinstaller/pyinstaller) (GPL-2.0 with the bootloader exception).

Downloaded only when the examiner asks for it:
- [whisper.cpp](https://github.com/ggml-org/whisper.cpp) (MIT) from its GitHub releases, and the OpenAI Whisper models (MIT) in ggml format from [ggerganov/whisper.cpp on Hugging Face](https://huggingface.co/ggerganov/whisper.cpp), each file kept only if its SHA-256 matches the published one.

LEXIS is an independent project. It is not affiliated with or endorsed by the LEAPP project.
