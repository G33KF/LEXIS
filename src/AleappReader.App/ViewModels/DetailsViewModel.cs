using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

/// <summary>One name/value line of the details pane; a Header line starts a section and has no value. An AI line
/// carries its provenance (<paramref name="Ai"/>: model, provider, when) and is shown apart, in italics.</summary>
public sealed record Field(string Name, string Value, bool Header = false, string? Ai = null)
{
    /// <summary>Tooltip: an AI line's provenance, or for a bare number what it reads as under each timestamp epoch.</summary>
    public string? Timestamps => Header ? null : Ai is not null ? Ai + "\n" + AiViewModel.Caveat : ValueFormatter.TimestampCandidates(Value);
    public bool IsAi => Ai is not null;
    /// <summary>A date written as text, read in the display zone (see <see cref="ValueFormatter.DateReading"/>).</summary>
    public string? Reading => Header || IsAi ? null : ValueFormatter.DateReading(Value);
    /// <summary>The value as stored, when it can hold more than its text shows (a BLOB).</summary>
    public object? Raw { get; init; }
    /// <summary>JSON, XML, plist, HTML or BLOB when the value holds structured data that opens as a tree.</summary>
    public string? Kind => Header || IsAi ? null : StructuredValue.Kind(Raw ?? Value);
    /// <summary>The structured value decoded as a tree, or a line saying it does not decode; read only when shown.</summary>
    public IReadOnlyList<object> Decoded => Kind is { } k && StructuredValue.Parse(Raw ?? Value, k) is { } tree ? [tree]
        : ["It does not decode as " + Kind + " (damaged or cut short): the value stays as stored."];
}

/// <summary>One message a file was sent or received in, as the "Sent / received history" lists it.</summary>
public sealed record MediaHistoryEntry(MediaUse Use, string When, bool Copy)
{
    public string What => $"{(Use.Direction == "" ? "In a message" : Use.Direction)} · {Use.Artifact.Name}{(Copy ? " · same content, other file" : "")}";
    public string Who => (Use.Sent == true ? $"to {Use.Chat}" : Use.Sender != "" && Use.Sender != Use.Chat ? $"from {Use.Sender} · chat {Use.Chat}"
        : Use.Sent == false ? $"from {Use.Chat}" : $"chat {Use.Chat}") + (Use.Group ? " (group)" : "");
}

/// <summary>Right pane: every field of the selected record, its media and its tag/note.</summary>
public partial class DetailsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<Field> Fields { get; } = [];
    public ObservableCollection<MediaRef> Media { get; } = [];
    /// <summary>Every message the file shown was sent or received in, the earliest first (gallery).</summary>
    public ObservableCollection<MediaHistoryEntry> History { get; } = [];
    /// <summary>The first PDF among the media shown, previewed in the details (the main window shows it).</summary>
    public MediaRef? Pdf => Media.FirstOrDefault(m => IsPdf(m) && m.Exists);
    public static bool IsPdf(MediaRef m) => m.MimeType == "application/pdf"
        || Path.GetExtension(m.FullPath ?? m.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> TagChoices => Settings.Current.Tags;
    public void RefreshTagChoices() => OnPropertyChanged(nameof(TagChoices));
    static string DefaultTag => Settings.Current.Tags.FirstOrDefault() ?? TagStore.DefaultTags[0];

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string subtitle = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanTag))]
    [NotifyCanExecuteChangedFor(nameof(SaveTagCommand), nameof(RemoveTagCommand))]
    private (ArtifactInfo Artifact, long RowId)? tagKey;
    /// <summary>The device of the record or file shown.</summary>
    CaseSession? _s;
    [ObservableProperty] private string tag = "";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private bool isTagged;
    /// <summary>The record's original HTML (an email body), shown in a sandboxed browser: no scripts, no network.</summary>
    [ObservableProperty] private string? html;

    public bool CanTag => TagKey is not null;
    /// <summary>The record behind a timeline event, opened in its artifact table by "Open record".</summary>
    [ObservableProperty] private (ArtifactInfo Artifact, long RowId)? source;

    [RelayCommand]
    void OpenSource()
    {
        if (Source is { } x) main.NavigateTo(x.Artifact, null, x.RowId);
    }

    [RelayCommand]
    void OpenHistory(MediaHistoryEntry? e)
    {
        if (e is not null) main.NavigateTo(e.Use.Artifact, null, e.Use.RowId);
    }

    /// <summary>A timeline event: when and what happened first, then the record it comes from.</summary>
    public void ShowEvent(CaseSession s, TimelineEvent e)
    {
        ShowRecord(e.Session ?? s, e.Artifact, e.Columns, e.RowId, e.Values);
        Subtitle = $"{e.EventType} · {e.Timestamp}";
        Fields.Insert(0, new Field("Event", "", true));
        Fields.Insert(1, new Field("When", e.Timestamp));
        Fields.Insert(2, new Field("What", $"{e.EventType} of a \"{e.Artifact.Name}\" record"));
        Fields.Insert(3, new Field("Category", e.Artifact.Category));
        Fields.Insert(4, new Field("Record", "", true));
        Source = (e.Artifact, e.RowId);
    }

    /// <summary>What the AI said about the item shown: its keys ([table#row], [media#id] with the file's name) and the
    /// item shown when they were set, so a late answer never lands on another item.</summary>
    (string Key, string? Media)[] _aiKeys = [];
    int _shown;
    /// <summary>Changes whenever another item is shown: a late addition checks it before landing.</summary>
    public int Shown => _shown;

    /// <summary>An "AI" section with the latest note of each kind about the item and its media (translation,
    /// explanation, description, OCR and its translation, transcription…), before the record's own fields.</summary>
    async Task AddAiAsync()
    {
        if (_s is not { } s || _aiKeys.Length == 0) return;
        var (keys, shown) = (_aiKeys, _shown);
        var notes = await Task.Run(() =>
        {
            try
            {
                var store = AiStore.For(s);
                return keys.SelectMany(k => AiText.Notes(store.Citing(k.Key), k.Key)
                    .Select(n => new Field(k.Media is null ? n.Name : $"{n.Name} · {k.Media}", n.Text, Ai: n.Label))).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { return []; }
        });
        if (shown != _shown) return;
        foreach (var f in Fields.Where(f => f.IsAi || f is { Header: true, Name: "AI" }).ToList()) Fields.Remove(f);
        if (notes.Count == 0) return;
        var at = Fields.Select((f, i) => (f, i)).FirstOrDefault(x => x.f is { Header: true, Name: "All fields of the record" or "Source" or "File" }, (null!, Fields.Count)).Item2;
        foreach (var f in notes.Prepend(new Field("AI", "", true))) Fields.Insert(at++, f);
    }

    /// <summary>After an AI run: the notes of the item shown, as now saved.</summary>
    public void RefreshAi() => _ = AddAiAsync();

    public void Clear()
    {
        _shown++;
        _aiKeys = [];
        Fields.Clear();
        Media.Clear();
        History.Clear();
        Title = Subtitle = Tag = Note = "";
        TagKey = null;
        _s = null;
        Source = null;
        (_sources, HasSources) = ([], false);
        IsTagged = false;
        Html = null;
    }

    /// <summary>Shows one artifact row: every column, resolved media, source path and tag.</summary>
    public void ShowRecord(CaseSession s, ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, long rowId, object?[] values)
    {
        Clear();
        _s = s;
        Title = a.Name;
        Subtitle = $"{a.Category} · record #{rowId}";
        for (int i = 0; i < cols.Count; i++)
        {
            var (shown, stored) = (ValueFormatter.Display(values[i], cols[i]), ValueFormatter.Format(values[i], cols[i]));
            Fields.Add(new Field(cols[i].Display, shown) { Raw = values[i] });
            if (shown != stored) Fields.Add(new Field(cols[i].Display + " (as stored)", stored));
        }
        AddSource(s, EvidenceOrigin.RecordedSources(a), a.Module, a.Name);

        using (var con = s.OpenDb())
        {
            var resolver = new MediaResolver(s);
            for (int i = 0; i < cols.Count; i++)
                if (cols[i].IsMedia) foreach (var m in resolver.Resolve(con, values[i])) Media.Add(m);
        }
        TagKey = (a, rowId);
        _aiKeys = [(AiText.Key(a.TableName, rowId), null), .. Media.Select(m => (AiText.Key("media", m.RefId), (string?)m.Name))];
        _ = AddAiAsync();
        _ = AddMetadataAsync([.. Media]);
        _ = AddExtractionPhotosAsync(s, (a, rowId), cols, values);
        _ = ShowHtmlAsync(s, a, (a, rowId), cols, values);
        var t = s.Tags.ForTable(a.TableName).GetValueOrDefault(rowId);
        IsTagged = t is not null;
        Tag = t?.Tag ?? DefaultTag;
        Note = t?.Note ?? "";
    }

    /// <summary>Photos the record names by path or file name (Photos.sqlite, MediaStore, Google Photos…) that are not
    /// in the report: found in the extraction and shown from a read-only temp copy.</summary>
    async Task AddExtractionPhotosAsync(CaseSession s, (ArtifactInfo, long) key, IReadOnlyList<ColumnInfo> cols, object?[] values)
    {
        var texts = values.Select((v, i) => cols[i].IsMedia ? null : v as string).OfType<string>().ToList();
        if (!texts.Any(t => ExtractionFiles.MediaExtensions.Contains(Path.GetExtension(t.Trim())))) return;
        // pictures, videos and audio, so they can be seen and played here; ponytail: the first 4, each copied whole once
        // (a long video takes a moment on first selection); stream from the extraction if that bites
        var found = await Task.Run(() => s.Extraction is not { } x ? [] :
            texts.Select(t => x.Find(t, texts)).OfType<string>().Distinct()
                .Where(rel => x.ToMedia(rel).Kind is "image" or "video" or "audio").Take(4)
                .Select(rel => x.CopyOut(rel) is { } copy ? x.ToMedia(rel, copy) : null).OfType<MediaRef>().ToList());
        if (TagKey != key) return; // another record is shown by now
        var added = found.Where(m => !Media.Any(x => string.Equals(x.Name, m.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var m in added) Media.Add(m);
        await AddMetadataAsync(added);
    }

    /// <summary>The embedded metadata of the files shown (EXIF, GPS, XMP, QuickTime…), a group per file and tag group,
    /// read off the UI thread from the read-only file.</summary>
    async Task AddMetadataAsync(IReadOnlyList<MediaRef> media)
    {
        var shown = _shown;
        // ponytail: first 4 files of a record; open the file itself (gallery, double-click) for the others.
        var read = await Task.Run(() => media.Where(m => m.Exists).Take(4).Select(m => (m, Tags: Metadata(m))).Where(x => x.Tags.Count > 0).ToList());
        if (shown != _shown) return;
        foreach (var (m, tags) in read)
        {
            string? group = null;
            foreach (var t in tags)
            {
                if (t.Group != group) Fields.Add(new Field($"{(t.Group == "Summary" ? "Metadata" : t.Group)}{(read.Count > 1 || media.Count > 1 ? " · " + m.Name : "")}", "", true));
                group = t.Group;
                Fields.Add(new Field(t.Name, t.Value));
            }
        }
    }

    static IReadOnlyList<MediaMetadata.Tag> Metadata(MediaRef m)
    {
        try { using var f = m.OpenRead(); return MediaMetadata.Read(f); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    async Task ShowHtmlAsync(CaseSession s, ArtifactInfo a, (ArtifactInfo, long) key, IReadOnlyList<ColumnInfo> cols, object?[] values)
    {
        var html = await Task.Run(() => EmailBody.Html(s, a, cols, values));
        if (TagKey == key) Html = html;
    }

    /// <summary>A chat message: who, when, what and where it was read from, then
    /// every field of the record.</summary>
    public void ShowMessage(CaseSession s, ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, ConversationItem chat, MessageItem m, ContactMatch? sender = null)
    {
        ShowRecord(s, a, cols, m.RowId, m.Raw);
        Title = "Instant Message";
        Subtitle = $"{a.Category} · {chat.Label}";
        var owner = chat.Participants.FirstOrDefault(p => p.IsOwner);
        var from = m.IsSent ? (owner?.Display ?? m.Sender) + " (local account)"
                            : m.SenderId != "" && m.SenderId != m.Sender ? $"{m.Sender} ({m.SenderId})" : m.Sender;
        Field[] head =
        [
            new("Source app", a.Category),
            new("Timestamp", m.Time),
            new("Direction", (m.IsSent ? "Sent" : "Received") + (m.IsSystem ? " · system message" : "")),
            new("From", from),
            new("Sender contact", sender is null ? "" : Describe(sender)),
            new("Participants", string.Join("\n", chat.Participants.Select(p => p.Display + (p.IsOwner ? " (local account)" : "")))),
            new("Chat", $"{chat.Label} · ID {chat.Key}"),
            new("Body", m.Text),
            new("Attachments", string.Join("\n", m.Media.Select(x => $"{x.Name} — {EvidenceOrigin.DevicePath(x.SourcePath)}"))),
            new("In reply to", m.QuoteText + (m.ReplyTo is { } q ? $"\n({q.Time}, record {q.RowId})" : "")),
            new("Shared location", m.Location is { } p ? m.LocationText + (p.TableName != a.TableName ? $" — {p.Artifact}, record {p.RowId}" : "") : ""),
            new("Source record", m.SourceRecord),
            new("All fields of the record", "", true),
        ];
        int at = 0;
        foreach (var f in head.Where(f => f.Header || f.Value != "")) Fields.Insert(at++, f);
        if (sender?.Picture is { } pic) Media.Insert(0, pic);
    }

    static string Describe(ContactMatch c) =>
        string.Join("\n", c.Fields.Select(f => $"{f.Field}: {f.Value}").Append($"Matched: {c.MatchedOn}"));

    /// <summary>A chat participant: their activity in the chat, then the contact record their identifier matched.</summary>
    public void ShowPerson(ArtifactInfo a, ConversationItem chat, ChatParticipant p, ContactMatch? c)
    {
        Clear();
        Title = p.Name != "" ? p.Name : p.Id;
        Subtitle = $"{a.Category} · participant of {chat.Label}";
        Field[] fields =
        [
            new("Participant", "", true),
            new("Name", p.Name),
            new("Identifier", p.Id),
            new("Role", p.IsOwner ? "Device owner — local account (sender of the sent messages)" : "Other participant"),
            new("Messages", p.Messages.ToString("N0")),
            new("First message", p.First),
            new("Last message", p.Last),
            new("Chat", $"{chat.Label} · ID {chat.Key}"),
        ];
        foreach (var f in fields.Where(f => f.Header || f.Value != "")) Fields.Add(f);
        Fields.Add(new Field("Contact record", "", true));
        if (c is null)
        {
            Fields.Add(new Field("Match", p.LookupId is null ? "Not looked up: no identifier to match on" : "No single contact record holds this identifier"));
            return;
        }
        Fields.Add(new Field("Source", $"{c.Artifact.Category} · {c.Artifact.Name}"));
        foreach (var (k, v) in c.Fields) Fields.Add(new Field(k, v));
        Fields.Add(new Field("Matched", c.MatchedOn));
        if (c.Picture is { } pic) Media.Add(pic);
    }

    /// <summary>Loads a row by id (from the map or the tagged list) and shows it.</summary>
    public void ShowRecord(CaseSession s, ArtifactInfo a, long rowId)
    {
        using var con = s.OpenDb();
        var page = ArtifactQuery.Query(con, a, new PageRequest(RowIds: [rowId]));
        if (page.Rows.Count == 1) ShowRecord(s, a, page.Columns, rowId, page.Rows[0].Values);
    }

    public void ShowFields(string title, string subtitle, IEnumerable<KeyValuePair<string, string>> fields, IEnumerable<AiNote>? ai = null)
    {
        Clear();
        Title = title;
        Subtitle = subtitle;
        foreach (var kv in fields) Fields.Add(new Field(kv.Key, kv.Value));
        if (ai?.ToList() is not { Count: > 0 } notes) return;
        Fields.Add(new Field("AI", "", true));
        foreach (var n in notes) Fields.Add(new Field(n.Name, n.Text, Ai: n.Label));
    }

    /// <summary>A file of the extraction: where it is, its size, the artifacts that read it.</summary>
    public void ShowFile(CaseSession s, string rel, string device, long size, IReadOnlyList<ArtifactInfo> readBy)
    {
        Clear();
        _s = s;
        Title = Path.GetFileName(rel);
        Subtitle = $"File of the extraction {EvidenceOrigin.ExtractionName(s)}";
        Fields.Add(new Field("Path on the device", device));
        Fields.Add(new Field("Path in the extraction", rel));
        Fields.Add(new Field("Size", $"{size:N0} bytes"));
        Fields.Add(new Field("Read by", readBy.Count == 0 ? "No artifact" : string.Join("\n", readBy.Select(a => $"{a.Name} ({a.Category})"))));
        (_sources, _sourcesTitle, HasSources) = ([rel], Title, true);
        _ = AddFileMediaAsync(s, rel);
    }

    /// <summary>A picture, video, audio or PDF file of the extraction shown (and played) here, with its embedded metadata: from a
    /// read-only temp copy.</summary>
    async Task AddFileMediaAsync(CaseSession s, string rel)
    {
        if (s.Extraction is not { } x || x.ToMedia(rel).Kind is not ("image" or "video" or "audio")
            && !rel.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return; // a PDF is shown too
        var sources = _sources;
        var copy = await Task.Run(() => x.CopyOut(rel));
        if (copy is null || !ReferenceEquals(sources, _sources)) return; // another file is shown by now
        var m = x.ToMedia(rel, copy);
        Media.Add(m);
        await AddMetadataAsync([m]);
    }

    /// <summary>The first source file, selected in its folder of the Extraction Files view.</summary>
    [RelayCommand]
    void ShowInFiles()
    {
        if (_sources.Count > 0) main.ShowInFiles(_sources[0], _s);
    }

    /// <summary>A media file; with its <paramref name="history"/> (gallery) every message it went through is listed apart,
    /// then those of the files holding the same bytes (<paramref name="copies"/>); else the one message it came with heads the fields.</summary>
    public void ShowMedia(MediaRef m, MediaUse? use = null, IReadOnlyList<MediaUse>? history = null, IReadOnlyList<MediaUse>? copies = null)
    {
        Clear();
        Title = m.Name;
        Subtitle = m.Artifact;
        string Time(long e) => ValueFormatter.Format(e, new ColumnInfo("t", "t", "datetime"));
        foreach (var (h, copy) in (history ?? []).Select(h => (h, false)).Concat((copies ?? []).Select(h => (h, true))))
            History.Add(new(h, h.Epoch is { } e ? Time(e) : "No message time", copy));
        if (use is not null)
        {
            Source = (use.Artifact, use.RowId);
            if (History.Count == 0)
            {
                Fields.Add(new Field("Message", "", true));
                Fields.Add(new Field("App", $"{use.Artifact.Name} ({use.Artifact.Category})"));
                if (use.Chat != "") Fields.Add(new Field("Chat", use.Chat));
                if (use.Direction != "") Fields.Add(new Field("Direction", use.Direction));
                if (use.Sender != "") Fields.Add(new Field("Sender", use.Sender));
                if (use.Epoch is { } e) Fields.Add(new Field("Message time", Time(e)));
            }
            Fields.Add(new Field("File", "", true));
        }
        if (m.MimeType is not null) Fields.Add(new Field("Type", m.MimeType));
        if (m.Exists) Fields.Add(new Field("Size", $"{m.Size:N0} bytes"));
        if (m.Created is { } c) Fields.Add(new Field("Created (file system)", Time(c)));
        if (m.Modified is { } u) Fields.Add(new Field("Modified (file system)", Time(u)));
        _s = use is not null ? main.SessionOf(use.Artifact) : main.Session;
        if (_s is { } s) AddSource(s, m.SourcePath is { Length: > 0 } p ? [p] : [], m.Module, m.Name);
        if (use is not null && _s is not null) // a file a message or record names is tagged as that record
        {
            TagKey = (use.Artifact, use.RowId);
            var t = _s.Tags.ForTable(use.Artifact.TableName).GetValueOrDefault(use.RowId);
            IsTagged = t is not null;
            Tag = t?.Tag ?? DefaultTag;
            Note = t?.Note ?? "";
        }
        Media.Add(m);
        _ = AddMetadataAsync([m]);
        _aiKeys = [(AiText.Key("media", m.RefId), null)];
        _ = AddAiAsync();
    }

    /// <summary>One "Source" section: where the data sits on the phone, the extraction it was read from, the module.</summary>
    void AddSource(CaseSession s, IReadOnlyList<string> recorded, string? module, string title)
    {
        (_sources, _sourcesTitle) = (recorded, title);
        HasSources = recorded.Count > 0;
        var paths = recorded.Select(EvidenceOrigin.DevicePath).Distinct().ToList();
        Fields.Add(new Field("Source", "", true));
        if (paths.Count > 0 && paths[0] != "") Fields.Add(new Field(paths.Count == 1 ? "File on the device" : "Files on the device", EvidenceOrigin.Join(paths)));
        if (EvidenceOrigin.ExtractionName(s) is { Length: > 0 } x) Fields.Add(new Field("Extraction", x));
        if (!string.IsNullOrEmpty(module)) Fields.Add(new Field("LEAPP module", module));
    }

    [RelayCommand(CanExecute = nameof(CanTag))]
    void SaveTag()
    {
        if (TagKey is not { } k || _s is null) return;
        _s.Tags.Set(k.Artifact.TableName, k.RowId, string.IsNullOrWhiteSpace(Tag) ? DefaultTag : Tag.Trim(), Note.Trim());
        IsTagged = true;
        main.OnTagChanged(k.Artifact, [k.RowId], Tag);
    }

    /// <summary>Follows a tag set elsewhere (menu, shortcut) on the record shown here.</summary>
    public void SyncTag(CaseSession? s, string table, IReadOnlyCollection<long> rowIds, string? tag)
    {
        if (TagKey is not { } k || _s != s || k.Artifact.TableName != table || !rowIds.Contains(k.RowId)) return;
        IsTagged = tag is not null;
        if (tag is not null) Tag = tag;
        else Note = "";
    }

    [RelayCommand(CanExecute = nameof(CanTag))]
    void RemoveTag()
    {
        if (TagKey is not { } k || _s is null) return;
        _s.Tags.Remove(k.Artifact.TableName, k.RowId);
        IsTagged = false;
        Note = "";
        main.OnTagChanged(k.Artifact, [k.RowId], null);
    }

    IReadOnlyList<string> _sources = [];
    string _sourcesTitle = "";
    /// <summary>The shown record or file names source files in the extraction that can be exported.</summary>
    [ObservableProperty] private bool hasSources;

    [RelayCommand]
    Task ExportSources() => ExportSourceFiles(_s, _sourcesTitle, _sources);

    /// <summary>Saves verified copies of the source files (with SHA256SUMS.txt and PROVENANCE.txt) in a new subfolder.</summary>
    public async Task ExportSourceFiles(CaseSession? s, string title, IReadOnlyList<string> recorded)
    {
        if (s is null || recorded.Count == 0) return;
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the source files (a new subfolder is created)" };
        if (folder.ShowDialog() != true) return;
        if (Inside(folder.FolderName, EvidenceOrigin.InputPath(s)) || Inside(folder.FolderName, s.RootDir))
        {
            main.Status = "Choose a folder outside the extraction and the case: nothing is written beside the evidence";
            return;
        }
        var dir = Path.Combine(folder.FolderName, HtmlReportExporter.SafeName(title) + $" sources {DateTime.Now:yyyy-MM-dd HHmmss}");
        main.Status = "Exporting the source files…";
        try
        {
            var (files, missing) = await Task.Run(() => EvidenceOrigin.ExportSources(s, title, recorded, dir));
            ExportLog.Add($"{title}: source files", dir, $"{files.Count:N0} files" + (missing.Count > 0 ? $", {missing.Count} not reachable" : ""), s);
            main.Status = $"Exported {files.Count:N0} file(s), verified by SHA-256, to {dir}" +
                          (missing.Count > 0 ? $" — {missing.Count} not reachable (see PROVENANCE.txt)" : "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            main.Status = $"Export failed: {e.Message}";
        }
    }

    /// <summary>Whether <paramref name="dir"/> is <paramref name="root"/> or below it; false when root is unknown.</summary>
    static bool Inside(string dir, string root)
    {
        if (root == "") return false;
        static string Full(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Full(dir).StartsWith(Full(root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where "Open source copy" puts its copies; emptied when the case closes.</summary>
    public static string SourceCopies => Path.Combine(Path.GetTempPath(), "AleappReader", "sources");

    /// <summary>Types handed to their default app; anything else (databases, executables, scripts, shortcuts) is only
    /// shown in Explorer, so a file from the phone never runs on the examiner's machine.</summary>
    static readonly HashSet<string> ShellSafe = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".json", ".xml", ".plist", ".pdf", ".html", ".htm",
    };

    /// <summary>Opens a verified, read-only temp copy of the first source file: never the evidence itself.</summary>
    [RelayCommand]
    async Task OpenSourceCopy()
    {
        if (_s is not { } s || _sources.Count == 0) return;
        var dir = Path.Combine(SourceCopies, Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var (files, _) = await Task.Run(() => EvidenceOrigin.ExportSources(s, _sourcesTitle, _sources.Take(1), dir)); // with its -wal, -shm, -journal
            var source = EvidenceOrigin.DevicePath(_sources[0].Replace('\\', '/').TrimStart('/'));
            if (files.FirstOrDefault(f => f.DevicePath == source) is not { } copy)
            {
                main.Status = "The source file is not reachable: neither the extraction nor ALEAPP's copy";
                return;
            }
            foreach (var f in files) File.SetAttributes(f.File, FileAttributes.ReadOnly);
            if (SqliteFile.IsSqlite(copy.File))
            {
                string? Side(string x) => files.FirstOrDefault(f => f.DevicePath == source + x)?.File;
                Views.SqliteWindow.Show(copy.File, Path.GetFileName(source), $"{source} · SHA-256 {copy.Sha256}", Side("-wal"), Side("-shm"), Side("-journal"));
                main.Status = $"Opened in the SQLite reader: {source} (SHA-256 {copy.Sha256})";
                return;
            }
            var ext = Path.GetExtension(copy.File);
            var open = ShellSafe.Contains(ext) || ExtractionFiles.MediaExtensions.Contains(ext);
            try
            {
                if (open) Process.Start(new ProcessStartInfo(copy.File) { UseShellExecute = true });
                else Process.Start("explorer.exe", ["/select,", copy.File]);
            }
            catch (System.ComponentModel.Win32Exception) { open = false; Process.Start("explorer.exe", ["/select,", copy.File]); } // no app for the type
            main.Status = $"{(open ? "Opened" : "Copied, shown in Explorer:")} {copy.DevicePath} (SHA-256 {copy.Sha256})";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException)
        {
            main.Status = $"Cannot open the copy: {e.Message}";
        }
    }

    /// <summary>Deletes the copies "Open source copy" made; one still open in another app stays until next time.</summary>
    public static void DeleteSourceCopies()
    {
        if (!Directory.Exists(SourceCopies)) return;
        foreach (var f in Directory.EnumerateFiles(SourceCopies, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(SourceCopies, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Programs, scripts and shortcuts: a file from the phone of these types is never started, only shown in Explorer.</summary>
    static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".msi", ".msp", ".dll", ".sys", ".cpl", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".lnk", ".url", ".scf", ".reg", ".inf", ".chm", ".msc", ".jar", ".sh", ".py", ".pyw", ".gadget", ".xll",
        ".application", ".appref-ms", ".settingcontent-ms", ".library-ms",
    };

    [RelayCommand]
    internal static void OpenMedia(MediaRef? m)
    {
        if (m?.Exists != true) return;
        // a picture, a video or a plain document opens in its program; any other type opens as a read-only temp copy, since
        // its program may write beside it (lock files); a program, a script or a shortcut is only shown in Explorer
        var file = m.LocalFile();
        var ext = Path.GetExtension(file);
        try
        {
            if (Runnable.Contains(ext)) { Process.Start("explorer.exe", ["/select,", file]); return; }
            if (!ShellSafe.Contains(ext) && !ExtractionFiles.MediaExtensions.Contains(ext))
            {
                var dir = Directory.CreateDirectory(Path.Combine(SourceCopies, Guid.NewGuid().ToString("N")[..12])).FullName;
                var copy = Path.Combine(dir, Path.GetFileName(file));
                File.Copy(file, copy);
                File.SetAttributes(copy, FileAttributes.ReadOnly);
                file = copy;
            }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { Process.Start("explorer.exe", ["/select,", file]); } // no program for the type
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Process.Start("explorer.exe", ["/select,", m.LocalFile()]); }
    }

    [RelayCommand]
    internal static void ShowInFolder(MediaRef? m)
    {
        if (m?.Exists == true) Process.Start("explorer.exe", ["/select,", m.LocalFile()]);
    }
}
