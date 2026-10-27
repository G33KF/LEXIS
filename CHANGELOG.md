# Changelog

## 1.2.5

### Added
- **Search results as a table.** One row per record found: category, artifact, date, app, device, the column it was
  found in and its text. Narrow the results by text, by any column's ▾ (date between, contains…) and by the values
  on the right with their counts; double-click opens the record. The search covers the categories checked, by
  default the one selected in the tree.
- **Search in file contents and AI results.** *Search in* adds the text inside the extraction's files (PDF pages,
  Word, Excel, PowerPoint and OpenDocument files, SQLite rows, property lists, text, JSON and XML; databases without
  a name are recognised by their content) and the AI results (translations, analyses, descriptions). The first search
  reads the files and keeps their text in `_reader_text.db`, beside the tags: later searches and sessions do not read
  them again. A PDF whose pages are only images is listed as *Needs OCR*. Double-click opens a database in the SQLite
  reader, a picture, video or PDF in its viewer.
- **Media viewer.** Double-click a file in the Media Gallery (or a picture in a chat): a picture to zoom (wheel,
  buttons, double-click) and drag, a video or audio with its player, a PDF in the sandboxed viewer, beside its details.
  Any other file opens in its default program, as a read-only copy; programs, scripts and shortcuts are only shown in the folder.
- **Accounts Overview.** A table with one row per account of each app or service, merged from the artifacts that
  record it: name, username, phone, e-mail, UID, services. Select a row for every value in the details, each with the
  artifact and column it came from (installation source, date of birth, about…); a derived value (a number with its
  country code, a WhatsApp JID) says so. The picture comes from the extraction when it is there (WeChat avatars,
  WhatsApp `me.jpg`); a picture held online is shown as its address and never fetched. An account in another Android
  user's files says which user. The table opens with *User Accounts & Details* above its artifacts, and in the Case
  Summary's *Owner & accounts* tab. Android and iOS: on iOS the Apple IDs (with their services, and the DSID that
  bought or installed the apps), Telegram's own profile, and the account WhatsApp and Telegram chats belong to.
- **WhatsApp (iOS) own account.** *WhatsApp* artifacts now record the account's own JID and name (from WhatsApp's
  preferences) instead of the app container. Needs a new run of the report.
- **Sent / received history in the Gallery.** A file selected in the Gallery lists every chat message it was sent or
  received in: when, the app, the chat (group or private), who it went to or came from. A click opens the message in
  its table. Files holding the same bytes (a picture forwarded, saved or received again) lend each other their
  messages, marked "same content, other file". The *Sent* and *Received* filters now keep a file when any of its
  messages went that way, not only the earliest.
- **Telegram (iOS) chats by account.** *Telegram Messages* now records the account each message belongs to (its user
  id, as *Telegram Accounts* reports it) and names it by its username, else its phone number, else its name; LEXIS
  shows the account level under Telegram. Needs a new run of the report.

### Changed
- **Accounts in short, cleaner.** The device's own users (owner, Secure Folder) are no longer listed as accounts, nor
  the accounts an app keeps for itself under its own name or label (Meet, WhatsApp, a bank's name); Chrome's saved
  logins go with the other sites; the Samsung account and the phone's mail app are named as such; long cells wrap.
- **Chats start collapsed.** The *Chats* node lists the apps; an app opens on its accounts only when clicked.
- **An account no record identifies is "Unidentified Account"** in the chat tree (was "(no account recorded)"); an
  account known only by its name or only by its id is shown by that alone.

## 1.2.4

### Added
- **Case Summary says whether LEXIS or ALEAPP/iLEAPP made the report.** *Parsed by* reads "LEXIS (ALEAPP 3.4.0 with
  the LEXIS modules)" or "ALEAPP 3.4.0". LEXIS now writes `processed_with: LEXIS` in the report's parser info; an older
  report counts as LEXIS when it lists a `LEXIS_*` module.
- **Private and group chats told apart in the chat list.** A *Type* column (Private / Group), filterable like the
  others, and a picker *All chats / Private / Group* beside the account picker. A chat is a group when its ID is a
  group ID (`@g.us`, `@chatroom`) or more than one person other than the local account wrote in it; a group with
  another kind of ID where only one other person wrote shows as private. The case statistics use the same rule.
- **The user's account in every chat list.** The *Account* column now shows whenever the user's side is known: the
  local account, else the sender of the sent messages. Blank when neither is recorded.
- **WeChat voice messages playable (SILK audio).** WeChat stores voice messages as SILK v3
  audio under an `.amr` name; they are encoded, not encrypted. The player now recognises
  SILK by its content and decodes it with the SILK SDK (`silk.dll`, built from source by
  `tools/silk/build.cmd`) into a WAV, kept in `_reader_audio/<SHA-256 of the original>.wav`
  beside the case (else in `%LOCALAPPDATA%\AleappReader\audio`) with an `index.tsv`. The
  first play decodes, later plays reuse the WAV; the original is only read. A note under the
  player says the audio was converted, with the SHA-256 on hover. A portable case carries
  the decoded audio of the voice messages it contains.

### Fixed
- **Case Summary failed with "Sequence contains no matching element"** when *Use navigation group tabs* was on
  (and the single navigation tab off): a case's or a device's summary belongs to no group and now opens in a tab of
  its own.
- **WeChat chats: a voice message shows its file and a player (Android).** `wechat_messages`
  now also checks the voice file a voice row resolves to into *Media*, so the chat bubble
  shows the file name and the player under "Voice message". Applies to extractions processed
  again with this version.

## 1.2.3

### Added
- **Case Summary: overview and detail tabs.** The tabs are now Overview, Device, SIM cards,
  Owner & accounts, Users and Report. The Overview cards have *Details →* links to their
  tab, a new *Owner and accounts* card, and the SIM count. *Device* opens with fixed groups
  (Common info, Device, Settings) read from Build, settingsSecure, Settings Global and
  Persistent Properties, else from the device information; where a value was read from
  shows on hover, and a value that differs between Android users is given per user.
  *SIM cards* shows one card per SIM with MCC (from the IMSI), MNC and country where a
  source stores them. *Users* is a table of the Android users and profiles.
- **siminfo: MCC and MNC (Android).** Two columns added as telephony.db stores them
  (`mcc_string`/`mnc_string`, else `mcc`/`mnc`), blank where the database has neither.
- **Keyword lists from a file in the advanced search, in any language.** *Load list…* reads
  a text file with one keyword per line, each line also split on a separator (default
  `,`; `\t` for a tab, empty for lines only). A line starting with `# ` is a comment, so
  hashtags stay keywords. The encoding is detected (BOM, then UTF-8, then the system code
  page, e.g. GBK) and shown with the count. Keywords with spaces are searched as phrases.
  Whole words now work on Chinese, Japanese, Thai and similar scripts (no word boundary is
  forced next to them), and plain searches ignore case for Cyrillic, Greek and accented
  letters too, not only A–Z.
- **WhatsApp message types named, and what a row without text was (Android).** The message
  tables now name the types they showed as numbers: contact card, GIF, sticker, deleted for
  everyone, deleted by admin, view once, poll and album. Labels come from third-party
  catalogs, cited as such. A new *Content* column, right after *Message Type*, says what
  the row was:
  - deleted for everyone or by an admin (who, and when);
  - view once;
  - the poll's options and vote totals;
  - the name and numbers of a contact card;
  - edited (and when).

  The chat bubble of a message without text shows the same. This applies to active
  messages, backup messages and carved messages. *Message* still holds the stored text.
- **WhatsApp - Contact Cards** and **WhatsApp - View Once Media** (Android): one row per
  shared vCard (name, numbers, chat, sender, time) and per view-once picture, video or voice
  message.
- **Signal (Android): edits, deletions, mentions and stories.** *Signal - Messages* gets
  *Edit* (which revision a row is), *Deleted By* (read from `deleted_by`, which newer
  Signal versions use instead of `remote_deleted`), *Mentions*, *Mentions Me* and *Story*.
  New tables: *Signal - Message Edits* (every revision of an edited message, the current
  one marked) and *Signal - Stories* (with reply counts). Older Signal databases without
  these columns leave them blank.
- **Recovery Apps - Captured Messages (Android).** "Recover deleted messages" and
  notification log apps (WAMR, Notisave, WhatsRemoved+ and others) keep a copy of each
  messaging notification, so a message deleted in WhatsApp can survive there. Five
  open-source formats are read exactly. In the other listed apps a generic detector reads
  tables shaped like captured notifications; its rows say *Heuristic* and name the table
  and columns read. Times stored as local text are kept as stored. Apps outside the list
  are never read.
- **Session - Messages (Android).** Session messages (sms and mms), decrypted with the key
  from `secrets.json` when the extraction carries an entry for `network.loki.messenger`.
  The SQLCipher settings of each Session version are tried, and one is used only when its
  HMAC verifies. Columns cover direction, deleted messages, group and timer events,
  attachment count and quotes. Without the key nothing is decrypted and the log says why.

### Fixed
- **SQLite reader: the decoded cell panel stayed empty.** It now follows the selected cell
  as well as the current one, so a click or the keyboard both fill it.
- **SQLite reader: carved rows of all tables showed NULL in Values.** The decoded panel now
  gets the row's column=value pairs.
- **Chat date filter found nothing.** The From/To filter of a chat read the shown time back
  as text, which fails on the "(UTC+01:00)" suffix, so every message was dropped. It now
  compares the stored time, in the display time zone.
- **WeChat calls showed "voip_content_voice".** A call row whose content is only that string
  now shows the text the app shows ("对方已取消", "通话时长 01:36"), read from the row's
  lvbuffer, in WeChat - Messages and in the Call Summary of WeChat - Calls. Content (as
  stored) keeps the raw value.

## 1.2.2

### Added
- **WhatsApp backups (Android).** `LEXIS_WhatsApp` finds `msgstore*.db.crypt12/14/15` in the
  WhatsApp and WhatsApp Business `Databases` folders and decrypts them with the key files in
  the extraction (`files/key`, `files/encrypted_backup.key`). A backup is decrypted only when
  the result is an SQLite database; the GCM tag is checked and reported. New artifacts:
  *WhatsApp Backups* (one row per backup, with the reason when it could not be decrypted) and
  *WhatsApp Backups - One To One / Group Messages* (every backup message, split per backup).
  The evidence is never written: decrypted copies go to the report folder.
- **Deleted WhatsApp messages from backups.** A message that is in a backup but not in the
  active database, nor in a newer backup, is added once to the WhatsApp message tables with a
  `Recovered` column naming the backup. A message is never called deleted on a weak match.
- **WhatsApp Business beside WhatsApp, Android and iOS.** Every WhatsApp artifact now runs
  once per app (and per Android user), with `Account ID` and `App` columns. Before, Business
  was read only when WhatsApp was absent.
- **`lexis_artifacts` folder in iLEAPP**, loaded the same way as in ALEAPP.
- **Deleted messages recovered by carving, Android and iOS.** The SQLite databases of the
  messaging apps with a chat view (SMS, Messenger, Viber, Telegram, Signal iOS, WhatsApp and
  many more) are carved: older page versions in the database and its write-ahead log, and the
  free space of its pages. A record is added only when the active database no longer holds it
  (by row id and by every unique key; edits are not deletions), read with the app's own query,
  once, with `Recovered` giving its reliability (high, medium, low). WhatsApp messages a
  backup already gave are not repeated.
- **Recovered records are marked.** A row whose `Recovered` / `Recovered From` column is set,
  or whose `In Active Database` reads No, shows a red restore icon in the grid and in the chat
  table, a red border and a "Recovered (deleted)" line on its chat bubble, and a dashed
  outline with the reason in the HTML chat export. Chats split per backup as they do per
  account.
- **SIM history in the case overview.** The *SIM cards* tab lists every SIM card recorded,
  one row per ICCID (else per IMSI, else per number), with number, IMSI, carrier, slot, the
  first and last date a source records and every source. Merged from the device information,
  Android `siminfo`, Google Constellation and iOS subscriber info. `siminfo` now also shows
  among the SIM artifacts.
- **SMS split per SIM card.** Android SMS and MMS get `SIM`, `SIM Name`, `SIM ICCID` and
  `SIM Sub ID`: the `siminfo` row of `telephony.db` that the message's `sub_id` names. They
  stay blank when the link is missing or ambiguous. iOS Messages split by
  `Destination Caller ID`, the own number or address used. The chats split per SIM as they
  do per account.
- **Account in every chat list.** In *Analyzed Data* the chat list of an artifact with local
  accounts (accounts, SIMs, backups) has an *Account* column and an account picker. The open
  chat names its account.
- **Chat view for SMS Backup and Restore and recovered SMS records** (ALEAPP).

### Fixed
- **WeChat: accounts encrypted with another IMEI now open.** An account database is keyed
  with the IMEI WeChat read when the account was set up. When the stored IMEI and the
  fallback value do not open it, `LEXIS_WeChatMultiAccount` also tries the IMEIs WeChat keeps
  in `CompatibleInfo.cfg` and `systemInfo.cfg` and in its preferences. Every key is derived
  from data in the extraction and tried; none is guessed.

## 1.2.1

### Added
- **WeChat: every account on the device, not only the signed-in one.** ALEAPP used to open
  only the WeChat database of the account whose uin was in the app preferences, normally
  the one currently signed in. The other accounts' databases were skipped. The new module
  `LEXIS_WeChatMultiAccount` also reads each account's uin from the `cdninfo` caches in that
  account's own folder, and checks it against the folder name before using it. Messages,
  contacts, calls, payments, locations, Moments and group members of every account with a
  database now appear in the same WeChat tables, one table per artifact, told apart by the
  Account ID column. HTML, TSV, timeline and LAVA outputs are unchanged in structure.
- **`lexis_artifacts` folder in ALEAPP.** LEXIS improvements to ALEAPP live as
  `LEXIS_<Name>.py` modules in `scripts/lexis_artifacts/`. ALEAPP loads them after its own
  artifacts. A LEXIS module that declares a standard artifact replaces it, so no data is
  reported twice. The original modules are left untouched.

### Fixed
- **No false "still running" prompt when an extraction finishes.** The extraction window
  closed itself while the run was still flagged as in progress, so it asked whether to stop
  ALEAPP even though parsing had completed. It now closes straight to the report. The prompt,
  shown only if you close the window during a run, names the tool in use (ALEAPP or iLEAPP).
