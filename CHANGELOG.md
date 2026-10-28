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
- **Video analysis with the AI.** Right-click a video (Gallery, artifact tiles, Extraction Files) or the AI window:
  frames taken by LEXIS's ffmpeg every N % of the duration (default 10 %) or every N seconds, at most 30 by default,
  each described by the video model (the vision model unless one is set), and the audio track transcribed; the result
  is a timeline of what is seen and said, with the SHA-256 of each frame sent. Settings in AI settings › Video. LEXIS's
  ffmpeg now also decodes H.264, HEVC, MPEG-4, H.263, VP8, VP9 and MJPEG (6 MB).
- **Documents node** beside Media Gallery: the extraction's PDF, Office, OpenDocument, RTF and iWork files, typed from
  their first bytes when it opens (so one saved without an extension is found too), with the artifacts that read them.
- **Details in tabs.** *Details*, then *Text Extraction* (text read from the file, part by part, with its method and
  any AI translation under it, marked out of date when the text changes) and *AI Analysis* (an AI summary of all the
  item's analyses, written after each one or on *Update*, then every analysis by kind); the two show only when they
  have content. An analysis can be corrected or excluded by the examiner: the model's text is kept, nothing is deleted.
- **Explain on a record explains that record**: what it is, then each value it holds and what it means; the result
  shows in the record's *AI Analysis* tab.
- **The AI offers what fits the artifact**: Translate only for text people wrote (messages, emails, notes, calendar,
  posts, searches), Summarize for those, calls, locations, web history or several artifacts together; Explain always.
- **whisper.cpp setup.** AI settings › whisper.cpp › *Set up* installs whisper.cpp (CPU, OpenBLAS or, with an NVIDIA
  card, CUDA) from its GitHub releases and the models chosen from Hugging Face, after a confirmation; each file is kept
  only if its SHA-256 is the published one, and `install.log` records what came from where. It also adds and deletes
  models. Not bundled with LEXIS. When a step fails it says which and why, and how to install by hand.
- **Opus, AMR, M4A and MP3 audio transcribed without installing ffmpeg.** LEXIS carries its own ffmpeg (2 MB,
  decoders only, LGPL, built from FFmpeg 9.0.2 by `tools/ffmpeg/build.sh`) to convert them for whisper.cpp.
- **Parallel requests.** AI settings › *Parallel requests* (default 1) sends that many requests at once for images,
  audio, the thematic review, the complete analysis's reading, Explain and questions. Summaries and translations stay
  in order, each step carrying the one before.
- **AI on right-click wherever data is shown.** Any field of the details: *Explain with the AI*, the other fields going
  with it as context. Extraction Files: describe and OCR or classify an image, transcribe audio, or explain any file
  from its content or hex. Chat list: the AI on the whole chat, read in full, with its media and its data.
- **AI window by kind of work.** *The data shown* (explain it), *Read the records* (summarise, explain, translate),
  *Search the records* (question, thematic review), images, audio; the results can be shown one kind at a time.
- **OCR, offline, with the engine chosen each time.** Right-click a picture or a PDF (Extraction Files, Gallery,
  Search results) › *Read the text (OCR)*: Tesseract, carried by LEXIS (12 languages, chosen in Settings), or the OCR
  built into Windows; the AI's *Describe and read the text* stays in the AI menu. A scanned PDF's pages are read from
  their images, within the size and page limits set in Settings. The text is kept in the case's text base: Search
  finds it and the file's details show it. Tesseract needs no Visual C++ install: its runtime comes with LEXIS.

### Changed
- **Accounts in short, cleaner.** The device's own users (owner, Secure Folder) are no longer listed as accounts, nor
  the accounts an app keeps for itself under its own name or label (Meet, WhatsApp, a bank's name); Chrome's saved
  logins go with the other sites; the Samsung account and the phone's mail app are named as such; long cells wrap.
- **Chats start collapsed.** The *Chats* node lists the apps; an app opens on its accounts only when clicked.
- **An account no record identifies is "Unidentified Account"** in the chat tree (was "(no account recorded)"); an
  account known only by its name or only by its id is shown by that alone.
- **Engines for each job.** AI settings list Ollama, LM Studio, the Claude CLI and whisper.cpp as engines; each job
  offers every engine that can do it, with where it runs. Choosing the Claude CLI for a job is the permission (the
  separate checkbox is gone); *Ask Claude* is available when it does the text job. Transcription defaults to whisper.cpp,
  and can be any other engine or endpoint; with whisper.cpp chosen, *Set up / models…* sits beside the job.
- **Audio that cannot be transcribed is said, not skipped silently.** When whisper.cpp is not set up or the model chosen
  for audio runs chat only, the analysis says so and asks whether to continue without the audio files; the result
  records them as not transcribed and why.
- **AI functions follow the jobs that have a model.** Text functions need a text model, image ones a vision model,
  transcription an audio engine; without one their buttons and menu items are off. AI on with no job set is not saved.
- **Summaries are summaries, and cover the media.** Overview, subjects, media and people, each point citing all its
  records; long chats are folded step by step instead of retold. Pictures and audio not read yet are offered to read
  first; what the AI read in a message's files goes with that message. A chat's summary opens with the chat's own
  data (name, type and ID, app, device account, participants, the messages summarised), from the case, not the AI.

### Fixed
- **Gallery: the AI read only the first of several files found in the extraction only**, and cited them all alike;
  each now has its own id, from its path.
- **SILK voice messages are transcribed from their decoded WAV** (the one kept in `_reader_audio`), not their raw
  bytes; the log records the SHA-256 of both.
- **Gallery › Whole extraction: Sent and Received found nothing**; files now carry the messages that name them, iOS
  backups included (their domains read as the paths iLEAPP records).
- **AI corrections and exclusions** now apply in the grid's translations and the chats' media notes too, not only in
  the details' *AI Analysis* tab; a record no longer shows an attachment's summary as its own.
- **Video analysis**: a video with no readable duration gets one frame and its audio; the audio of videos over an hour
  is not sent; the count says how many were analyzed; its temporary frames are deleted with retries.
- **Documents** is one list of all the documents, with a Name column; the Files toolbar is in labelled rows.
- **Security review fixes.** whisper.cpp setup accepts only build files under the project's own GitHub release URL,
  stops a download that grows past its published size, and refuses a folder whose `bin` it did not install (instead of
  replacing it). ffmpeg is LEXIS's own, the one beside whisper-cli or an `ffmpeg.exe` on PATH, never a script; the
  file extension passed on is checked. ffmpeg, whisper-cli and the Claude CLI stop after a time limit (10 minutes
  for ffmpeg, 3 hours for whisper-cli, 2 hours for one Claude CLI request); a video that stalls ffmpeg stops at the
  first frame. Temporary frames, audio and prompts are deleted when a job ends; what cannot be deleted then is said,
  and removed at the next start when no other LEXIS is running. OCR skips pictures declaring more than 80 million
  pixels (GIFs included) and reads a scanned PDF one page at a time, saying how many images it skipped.
  Translate and Write summary in the details refuse to run when the AI is off.

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
