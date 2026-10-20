using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public sealed record FileRow(string Path, string Device, long Size, string Type, int Hits, long FirstOffset, IReadOnlyList<ArtifactInfo> Artifacts, string Category,
                             string? Tag = null)
{
    public string UsedBy => string.Join(", ", Artifacts.Select(a => a.Name));
}

/// <summary>A file as a tile of the thumbnail view: its picture or video frame, else its kind.</summary>
public sealed partial class FileTile(FileRow row) : ObservableObject
{
    public FileRow Row => row;
    public string Name => System.IO.Path.GetFileName(row.Device);
    [ObservableProperty] private ImageSource? thumbnail;
}

/// <summary>An item of a file's right-click menu; "-" is a separator.</summary>
public sealed record FileAction(string Header, ICommand? Command = null, object? Parameter = null);

/// <summary>A folder of the device's file system, with how many files are under it.</summary>
public sealed partial class FolderNode(string name, string path, FolderNode? parent, Action<FolderNode> selected) : ObservableObject
{
    public string Name => name;
    public string Path => path;
    public FolderNode? Parent => parent;
    public List<FolderNode> Children { get; } = [];
    public int Files { get; set; }
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isExpanded;
    partial void OnIsSelectedChanged(bool value) { if (value) selected(this); }
}

/// <summary>The extraction's own files, read-only: folder tree by device path, the artifacts that read each file, path
/// and type filters, list or thumbnails, archives browsed in place, type by content, hex, text and tree viewer (JSON, XML,
/// HTML and plists laid out), keyword search through every file's bytes, a copy saved on request.</summary>
public partial class FilesViewModel : ObservableObject
{
    const int PreviewBytes = 64 << 10, PageSize = 300;
    public const string AllTypes = "All types";
    readonly MainViewModel _main;
    readonly CaseSession _s;
    IReadOnlyList<(string Path, string Device, long Size)> _all = [];
    IReadOnlyDictionary<string, IReadOnlyList<ArtifactInfo>> _used = new Dictionary<string, IReadOnlyList<ArtifactInfo>>();
    Dictionary<string, FolderNode> _dirs = [];
    string _kind = "";
    string? _folder;  // device path of the folder picked in the tree; null: all
    string? _reveal;  // a file to show once the list is read
    readonly Dictionary<string, string> _types = [];
    readonly Dictionary<string, string> _archiveOf = []; // a file found inside an archive → the archive's device path
    readonly HashSet<string> _opened = [];               // archives whose files are listed
    Dictionary<string, FileHit>? _hits; // null: no content search in force
    CancellationTokenSource? _cts;
    int _page, _shown;
    Views.FileViewerWindow? _window;

    [ObservableProperty] private IReadOnlyList<FileRow> rows = [];
    [ObservableProperty] private IReadOnlyList<FolderNode> folders = [];
    [ObservableProperty] private FileRow? selected;
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private bool usedOnly;
    /// <summary>Only the tagged files (tagged here or in the gallery).</summary>
    [ObservableProperty] private bool taggedOnly;
    Dictionary<string, string> _fileTags = [];
    [ObservableProperty] private string keyword = "";
    [ObservableProperty] private string info = "Reading the extraction's file list…";
    [ObservableProperty] private string hex = "";
    [ObservableProperty] private string text = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(IdentifyCommand), nameof(CancelCommand), nameof(LocateCommand), nameof(UnlockCommand))] private bool running;

    /// <summary>The kinds of file the list can be narrowed to (by content once typed, else by name).</summary>
    public IReadOnlyList<string> Kinds { get; } = [AllTypes, .. RawFiles.Categories];
    [ObservableProperty] private string typeFilter = AllTypes;

    /// <summary>Thumbnails instead of the list: a page of tiles, pictures and video frames decoded as they come.</summary>
    [ObservableProperty] private bool thumbnails;
    [ObservableProperty] private IReadOnlyList<FileTile> tiles = [];
    [ObservableProperty] private string pageInfo = "";

    /// <summary>The right-click menu of the selected file.</summary>
    [ObservableProperty] private IReadOnlyList<FileAction> fileActions = [];

    /// <summary>Where the hex / text / tree viewer is: right of the list, under the details pane, in a window of its own, or hidden.</summary>
    public IReadOnlyList<string> ViewerPlaces { get; } = [Right, UnderDetails, InWindow, Hidden];
    const string Right = "Right", UnderDetails = "Under the details", InWindow = "Window", Hidden = "Hidden";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ViewerRight), nameof(ViewerUnderDetails))] private string viewerAt = Right;
    public bool ViewerRight => ViewerAt == Right;
    public bool ViewerUnderDetails => ViewerAt == UnderDetails;

    /// <summary>What the selected file's content was recognised as, and how the viewer shows it.</summary>
    [ObservableProperty] private string detected = "";
    /// <summary>The viewer tab in front: 0 hex, 1 text, 2 tree.</summary>
    [ObservableProperty] private int viewerTab;

    public FilesViewModel(MainViewModel main, CaseSession s)
    {
        _main = main;
        _s = s;
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var x = await Task.Run(() => _s.Extraction);
        Unreachable = x is null;
        if (x is null)
        {
            Rows = [];
            Folders = [];
            var where = EvidenceOrigin.InputPath(_s) is { Length: > 0 } p ? p : EvidenceOrigin.ExtractionName(_s);
            if (_s.Manifest.FrozenProvenance is not null && !_s.Manifest.PortableFileSystem)
                Info = $"This portable case was made without the extraction's file system ({where}). Locate the extraction to browse it: it is checked against the copies in the case, then opened read-only.";
            else Info = $"The extraction this report was made from is not reachable from this machine ({where}). Locate it: it is checked against ALEAPP's copies, then opened read-only.";
            return;
        }
        _kind = x.Kind;
        _archiveOf.Clear();
        _opened.Clear();
        (_all, _used, _dirs) = await Task.Run(() =>
        {
            var all = x.Files().Select(f => (f.Path, EvidenceOrigin.DevicePath(f.Path), f.Size)).ToList();
            return ((IReadOnlyList<(string, string, long)>)all, EvidenceOrigin.UsedBy(_s), Tree(all.Select(f => f.Item2)));
        });
        var root = _dirs[""];
        root.IsExpanded = true;
        Folders = [root];
        ApplyFilter();
        Locked = x.Locked;
        if (Locked) Info = "Encrypted iTunes backup: its files are shown as stored, encrypted and named by hash. Enter the backup password to read them: it is used for this session only, never saved. " + Info;
        if (_reveal is { } r) Reveal(r);
    }

    /// <summary>An encrypted backup opened without its password.</summary>
    [ObservableProperty] private bool locked;

    /// <summary>Reads the encrypted backup with the password typed in the box, which is cleared at once.</summary>
    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task Unlock(System.Windows.Controls.PasswordBox? box)
    {
        var pw = box?.Password ?? "";
        box?.Clear();
        if (pw == "") return;
        Running = true;
        Info = "Unlocking the backup (the key derivation takes a few seconds)…";
        try
        {
            await Task.Run(() => _s.UnlockExtraction(pw));
            (_all, _folder) = ([], null);
            _types.Clear();
            _hits = null;
            Running = false;
            await LoadAsync();
        }
        catch (System.Security.Cryptography.CryptographicException) { Info = "Wrong backup password: nothing was decrypted."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            Info = "Cannot unlock the backup: " + e.Message;
        }
        finally { Running = false; }
    }

    /// <summary>The folders of the device paths, each with its file count, children sorted by name.</summary>
    Dictionary<string, FolderNode> Tree(IEnumerable<string> devicePaths)
    {
        void Pick(FolderNode n) { _folder = n.Path == "" ? null : n.Path; ApplyFilter(); }
        var root = new FolderNode("All files", "", null, Pick);
        var dirs = new Dictionary<string, FolderNode>(StringComparer.Ordinal) { [""] = root };
        foreach (var d in devicePaths)
        {
            root.Files++;
            var lead = d.StartsWith('/') ? "/" : "";
            var segs = d.TrimStart('/').Split('/');
            var (node, key) = (root, "");
            for (int i = 0; i < segs.Length - 1; i++)
            {
                key = i == 0 ? lead + segs[0] : key + "/" + segs[i];
                if (!dirs.TryGetValue(key, out var next)) { dirs[key] = next = new FolderNode(segs[i], key, node, Pick); node.Children.Add(next); }
                (node = next).Files++;
            }
        }
        foreach (var n in dirs.Values) n.Children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return dirs;
    }

    bool Idle => !Running && _all.Count > 0;

    partial void OnUsedOnlyChanged(bool value) => ApplyFilter();
    partial void OnTaggedOnlyChanged(bool value) => ApplyFilter();
    partial void OnTypeFilterChanged(string value) => ApplyFilter();
    partial void OnThumbnailsChanged(bool value) => ShowPage(_page);

    [RelayCommand]
    void ApplyFilter()
    {
        var f = Filter.Trim();
        var under = _folder is null ? null : _folder + "/";
        _fileTags = _s.Tags.FileTags();
        Rows = _all.Where(e => (under is null || e.Device.StartsWith(under, StringComparison.Ordinal))
                               && (_hits is null || _hits.ContainsKey(e.Path)) && (!UsedOnly || _used.ContainsKey(e.Path))
                               && (!TaggedOnly || _fileTags.ContainsKey(e.Path)))
            .Select(e => (e, type: _types.GetValueOrDefault(e.Path) ?? ""))
            .Select(t => (t.e, t.type, cat: RawFiles.Category(t.e.Device, t.type)))
            .Where(t => (TypeFilter == AllTypes || t.cat == TypeFilter)
                        && (f == "" || ArtifactQuery.TextMatches(f, t.e.Device) || ArtifactQuery.TextMatches(f, t.e.Path) || ArtifactQuery.TextMatches(f, t.type)))
            .Select(t => (t, h: _hits?.GetValueOrDefault(t.e.Path)))
            .Select(x => new FileRow(x.t.e.Path, x.t.e.Device, x.t.e.Size, x.t.type, x.h?.Count ?? 0, x.h?.FirstOffset ?? -1,
                                     _used.GetValueOrDefault(x.t.e.Path) ?? [], x.t.cat, _fileTags.GetValueOrDefault(x.t.e.Path)))
            .ToList();
        Info = (_hits is null ? "" : $"\"{Keyword}\" found in {_hits.Count:N0} files. ") +
               $"{Rows.Count:N0} of {_all.Count:N0} files shown{(_folder is null ? "" : " under " + _folder)}{(TypeFilter == AllTypes ? "" : $" ({TypeFilter})")}, " +
               $"{Rows.Sum(r => r.Size) / 1048576.0:N1} MB, {Rows.Count(r => r.Artifacts.Count > 0):N0} read by artifacts, {Rows.Count(r => r.Tag is not null):N0} tagged. " +
               $"Extraction ({_kind}) opened read-only: nothing is written to it." +
               (_kind == "tar.gz" ? " A tar.gz is read from its start for each file: previews are slow, searches read it once." : "") +
               (TypeFilter != AllTypes && _types.Count == 0 ? " Types come from file names until Identify types reads their first bytes." : "");
        ShowPage(0);
    }

    /// <summary>A page of tiles; the pictures and video frames are decoded one by one, until another page replaces it.</summary>
    async void ShowPage(int page)
    {
        var shown = ++_shown;
        if (!Thumbnails || _s.Extraction is not { } x) { Tiles = []; PageInfo = ""; return; }
        var pages = Math.Max(1, (Rows.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(page, 0, pages - 1);
        var tiles = Rows.Skip(_page * PageSize).Take(PageSize).Select(r => new FileTile(r)).ToList();
        Tiles = tiles;
        PageInfo = Rows.Count == 0 ? "" : $"{_page * PageSize + 1:N0}–{_page * PageSize + tiles.Count:N0} of {Rows.Count:N0}";
        foreach (var t in tiles.Where(t => t.Row.Category is RawFiles.Pictures or RawFiles.Videos))
        {
            if (shown != _shown) return;
            var m = x.ToMedia(t.Row.Path);
            try
            {
                t.Thumbnail = await Task.Run(() => t.Row.Category == RawFiles.Pictures
                    ? GalleryViewModel.Thumb(_s, m, 140) : GalleryViewModel.VideoThumb(_s, m, 140));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException) { }
        }
    }

    [RelayCommand] void PrevPage() => ShowPage(_page - 1);
    [RelayCommand] void NextPage() => ShowPage(_page + 1);

    /// <summary>Shows a recorded source file: its folder in the tree, the file selected. Waits for the list if needed.</summary>
    public void Reveal(string recorded)
    {
        var rel = recorded.Replace('\\', '/').TrimStart('/');
        if (_all.Count == 0) { _reveal = rel; return; }
        _reveal = null;
        var device = EvidenceOrigin.DevicePath(rel);
        SelectFolder(device);
        Selected = Rows.FirstOrDefault(r => r.Path == rel);
        if (Selected is null) Info = $"{device} is not in the extraction. " + Info;
    }

    /// <summary>The folder of a device path picked in the tree, its parents opened, every filter cleared.</summary>
    void SelectFolder(string device)
    {
        (Filter, UsedOnly, TaggedOnly, _hits) = ("", false, false, null);
        if (TypeFilter != AllTypes) TypeFilter = AllTypes;
        var cut = device.LastIndexOf('/');
        if (_dirs.TryGetValue(cut > 0 ? device[..cut] : "", out var dir))
        {
            for (var p = dir.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
            if (dir.IsSelected) ApplyFilter();
            else dir.IsSelected = true;
        }
        else { _folder = null; ApplyFilter(); }
    }

    /// <summary>The selected file in its folder, with every filter cleared.</summary>
    [RelayCommand]
    void ShowFolder()
    {
        if (Selected is not { } f) return;
        SelectFolder(f.Device);
        Selected = Rows.FirstOrDefault(r => r.Path == f.Path);
    }

    /// <summary>Opens an artifact that reads the selected file.</summary>
    [RelayCommand]
    void OpenArtifact(ArtifactInfo? a)
    {
        if (a is not null) _main.NavigateTo(a);
    }

    /// <summary>Double-click: an archive is explored, a database opens in the SQLite reader, anything else is in the viewer.</summary>
    [RelayCommand]
    async Task Open()
    {
        if (Selected is not { } f) return;
        if (f.Category == RawFiles.Archives || f.Type.StartsWith("ZIP") || f.Type is "gzip") await ExploreArchive();
        else if (f.Category == RawFiles.Databases) await OpenSqlite();
    }

    /// <summary>
    /// Lists the files inside the selected archive (zip, also APK/JAR/Office; tar; tar.gz; gz) as a folder of the same name
    /// beside it, opened in the tree: they read, search and open like any other file, and an archive inside is explored in
    /// turn. Nothing is unpacked to disk: each file is read from a read-only copy of the archive when it is needed.
    /// </summary>
    [RelayCommand]
    async Task ExploreArchive()
    {
        if (Selected is not { } f || _s.Extraction is not { } x) return;
        if (_opened.Contains(f.Path)) { SelectFolder(f.Device + "/"); return; }
        Info = $"Listing the files inside {f.Device}…";
        var files = await Task.Run(() => x.ArchiveFiles(f.Path));
        if (files.Count == 0)
        {
            Info = $"{f.Device} is not an archive that can be opened here (zip, APK/JAR/Office, tar, tar.gz, gz), or it is damaged.";
            return;
        }
        _opened.Add(f.Path);
        var cut = f.Path.Length + ExtractionFiles.Inside.Length;
        var inside = files.Select(e => (e.Path, Device: f.Device + "/" + e.Path[cut..], e.Size)).ToList();
        foreach (var e in inside) _archiveOf[e.Path] = f.Device;
        _all = [.. _all, .. inside];
        _dirs = Tree(_all.Select(a => a.Device));
        _dirs[""].IsExpanded = true;
        Folders = [_dirs[""]];
        SelectFolder(f.Device + "/");
        Info = $"{files.Count:N0} files inside {f.Device}, read from a read-only copy of it (nothing unpacked). " + Info;
    }

    async Task Run(Func<ExtractionFiles, IReadOnlyList<string>, IProgress<(int, int)>, CancellationToken, Task> work)
    {
        Running = true;
        _cts = new CancellationTokenSource();
        var paths = Rows.Select(r => r.Path).ToList();
        try { await work(_s.Extraction!, paths, new Progress<(int Done, int Total)>(p => Progress = 100.0 * p.Done / Math.Max(1, p.Total)), _cts.Token); }
        catch (OperationCanceledException) { }
        finally { Running = false; ApplyFilter(); }
    }

    /// <summary>Types every shown file from its first bytes, so a filter can find e.g. SQLite files without an extension.</summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    Task Identify() => Run(async (x, paths, p, ct) =>
    {
        var types = await Task.Run(() =>
        {
            var r = new List<(string, string)>();
            var done = 0;
            x.Each(paths, (rel, s) => // one pass over a tar.gz, not one per file
            {
                var head = new byte[4096];
                int got = 0, n;
                while (got < head.Length && (n = s.Read(head, got, head.Length - got)) > 0) got += n;
                r.Add((rel, RawFiles.Sniff(head.AsSpan(0, got))));
                p.Report((++done, paths.Count));
            }, ct, _ => p.Report((++done, paths.Count)));
            return r;
        });
        foreach (var (path, type) in types) _types[path] = type;
    });

    /// <summary>Searches the shown files' bytes for the keyword; an empty keyword clears the search.</summary>
    [RelayCommand(CanExecute = nameof(Idle))]
    async Task Search()
    {
        if (Keyword.Trim() == "") { _hits = null; ApplyFilter(); return; }
        _hits = null;
        ApplyFilter();
        var k = Keyword.Trim();
        await Run(async (x, paths, p, ct) =>
        {
            var hits = await Task.Run(() => RawFiles.Search(x, paths, k, p, ct));
            foreach (var h in hits) _types[h.Path] = h.Type;
            _hits = hits.ToDictionary(h => h.Path);
        });
    }


    /// <summary>The extraction is not where ALEAPP recorded it (or anywhere located): the view offers to locate it.</summary>
    [ObservableProperty] private bool unreachable;

    /// <summary>Where the extraction is now: a zip or tar picked as a file, or a folder (an iTunes backup too). It is
    /// checked against ALEAPP's working copies first, and used only when it matches or the examiner accepts the differences.</summary>
    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task Locate(string kind)
    {
        string path;
        if (kind == "folder")
        {
            var d = new OpenFolderDialog { Title = "Folder of the extraction (opened read-only)" };
            if (d.ShowDialog() != true) return;
            path = d.FolderName;
        }
        else
        {
            var d = new OpenFileDialog { Title = "Extraction archive (opened read-only)", Filter = "Extraction (*.zip;*.tar;*.tar.gz;*.tgz)|*.zip;*.tar;*.tar.gz;*.tgz|All files|*.*" };
            if (d.ShowDialog() != true) return;
            path = d.FileName;
        }
        Running = true;
        Info = "Checking the extraction against ALEAPP's working copies of the source files…";
        try
        {
            var check = await Task.Run(() => EvidenceOrigin.VerifyExtraction(_s, path));
            if (!check.Same && System.Windows.MessageBox.Show($"{check}\n\nUse {path} as this case's extraction anyway?",
                    "Locate extraction", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            {
                Info = check + " Not used.";
                return;
            }
            _s.LocateExtraction(path);
            (_all, _folder) = ([], null);
            _types.Clear();
            _hits = null;
            Running = false;
            await LoadAsync();
            Info = check + " " + Info;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Info = "Cannot use it: " + e.Message;
        }
        finally { Running = false; }
    }

    bool NotRunning => !Running;
    [RelayCommand(CanExecute = nameof(Running))]
    void Cancel() => _cts?.Cancel();

    async partial void OnSelectedChanged(FileRow? value)
    {
        if (value is null || _s.Extraction is not { } x) { Hex = Text = Detected = ""; Plist = []; FileActions = []; return; }
        _main.Details.ShowFile(_s, value.Path, value.Device, value.Size, value.Artifacts);
        if (_archiveOf.TryGetValue(value.Path, out var archive)) _main.Details.Fields.Insert(2, new Field("Inside the archive", archive));
        FileActions = Actions(value, false);
        var at = value.FirstOffset > 256 ? (value.FirstOffset - 256) & ~15L : 0;
        var bytes = await Task.Run(() => RawFiles.ReadAt(x, value.Path, at, PreviewBytes));
        if (Selected != value) return;
        var type = value.Type;
        if (type == "" && at == 0) _types[value.Path] = type = RawFiles.Sniff(bytes);
        var archiveKind = at == 0 ? ExtractionFiles.ArchiveKind(bytes, value.Path) : null;
        FileActions = Actions(value, archiveKind is not null);
        var more = value.Size > at + bytes.Length ? $"\n… first {bytes.Length / 1024} KB shown of {value.Size:N0} bytes" : "";
        Hex = (at > 0 ? $"From offset {at:X8} (first match at {value.FirstOffset:X8})\n" : "") + RawFiles.HexDump(bytes, at) + more;
        Plist = [];
        (Detected, ViewerTab) = (type, 0);
        if (at == 0 && PlistTree.IsPlist(bytes) && value.Size <= 16 << 20)
        {
            var (whole, tree) = await Task.Run(() => RawFiles.ReadAt(x, value.Path, 0, (int)value.Size) is var w ? (w, PlistTree.Read(w)) : default);
            if (Selected != value) return;
            if (tree is not null) { (Plist, Text, Detected, ViewerTab) = ([tree], PlistTree.ToText(tree), "Property list: shown as a tree and as text", 2); return; }
            if (whole.AsSpan().StartsWith("bplist"u8)) { (Text, Detected, ViewerTab) = (Bplist.ToText(whole), "Damaged binary plist: what is still readable", 1); return; }
        }
        if (archiveKind is not null)
            Detected = $"{type} — double-click, or right-click › Explore the archive, to browse the files inside";
        else if (at == 0 && RawFiles.AsText(bytes) is not null)
        {
            // text: the whole file (to 16 MB), laid out when it is JSON, XML or HTML
            var whole = value.Size > bytes.Length && value.Size <= 16 << 20 ? await Task.Run(() => RawFiles.ReadAt(x, value.Path, 0, (int)value.Size)) : bytes;
            if (Selected != value) return;
            var s = RawFiles.AsText(whole) ?? "";
            if (StructuredValue.Kind(s) is { } kind && kind is StructuredValue.Json or StructuredValue.Xml or StructuredValue.Html)
            {
                var (pretty, tree) = await Task.Run(() => (StructuredValue.Pretty(s, kind), kind == StructuredValue.Html ? null : StructuredValue.Parse(s, kind)));
                if (Selected != value) return;
                Plist = tree is null ? [] : [tree];
                Text = pretty ?? s;
                if (kind == StructuredValue.Html) _main.Details.Html = s; // rendered without scripts, nothing fetched
                (Detected, ViewerTab) = pretty is null
                    ? ($"Looks like {kind} but does not parse: shown as text", 1)
                    : kind == StructuredValue.Html ? ("HTML: rendered in the details (no scripts, nothing loaded from the network), its text here", 1)
                    : ($"{kind}: shown as a tree, and indented in Text", tree is null ? 1 : 2);
                return;
            }
            (Text, Detected, ViewerTab) = (s + (whole.Length < value.Size ? more : ""), "Text", 1);
            return;
        }
        Text = new string(System.Text.Encoding.UTF8.GetString(bytes).Select(c => char.IsControl(c) && c is not '\n' and not '\t' ? '·' : c).ToArray()) + more;
    }

    List<FileAction> Actions(FileRow f, bool archive)
    {
        List<FileAction> a = [];
        if (archive || f.Category == RawFiles.Archives) a.Add(new("Explore the archive", ExploreArchiveCommand));
        if (f.Category == RawFiles.Databases || f.Type.StartsWith("SQLite")) a.Add(new("Open in SQLite reader", OpenSqliteCommand));
        if (!_archiveOf.ContainsKey(f.Path)) a.Add(new("Open a verified copy", _main.Details.OpenSourceCopyCommand));
        a.Add(new("Save a copy…", SaveCopyCommand));
        a.Add(new("-"));
        foreach (var t in Settings.Current.Tags) a.Add(new($"Tag: {t}" + (f.Tag == t ? "  ✓" : ""), TagCommand, t));
        if (f.Tag is not null) a.Add(new("Remove tag", TagCommand, ""));
        a.Add(new("-"));
        a.Add(new("Copy path on the device", CopyCommand, "device"));
        a.Add(new("Copy path in the extraction", CopyCommand, "path"));
        a.Add(new("Copy file name", CopyCommand, "name"));
        a.Add(new("Compute hashes (MD5, SHA-1, SHA-256)", HashCommand));
        a.Add(new("Show in its folder", ShowFolderCommand));
        if (f.Artifacts.Count > 0) a.Add(new("-"));
        foreach (var art in f.Artifacts) a.Add(new($"Open artifact: {art.Name}", OpenArtifactCommand, art));
        return a;
    }

    /// <summary>Tags the selected file by its path in the extraction (the tag it already has, or "", removes it); the
    /// Tagged view lists it and a portable case can carry it.</summary>
    [RelayCommand]
    void Tag(string? tag)
    {
        if (Selected is not { } f) return;
        var t = string.IsNullOrEmpty(tag) || f.Tag == tag ? null : tag;
        _s.Tags.SetFiles([f.Path], t);
        _main.RefreshTagCount();
        foreach (var tab in _main.Tabs) if (tab.Content is TaggedViewModel tv) tv.Refresh();
        ApplyFilter();
        Selected = Rows.FirstOrDefault(r => r.Path == f.Path);
        _main.Status = t is null ? $"Tag removed from {f.Device}" : $"Tagged {f.Device} as \"{t}\"";
    }

    [RelayCommand]
    void Copy(string? what)
    {
        if (Selected is { } f) System.Windows.Clipboard.SetText(what switch { "device" => f.Device, "path" => f.Path, _ => System.IO.Path.GetFileName(f.Device) });
    }

    /// <summary>MD5, SHA-1 and SHA-256 of the selected file, read from the extraction: in the details and on the clipboard.</summary>
    [RelayCommand]
    async Task Hash()
    {
        if (Selected is not { } f || _s.Extraction is not { } x) return;
        Info = $"Hashing {f.Device}…";
        var h = await Task.Run(() => x.Read(f.Path, s => RawFiles.Hashes(s)));
        if (h.Md5 is null) { Info = $"Could not read {f.Device}"; return; }
        System.Windows.Clipboard.SetText($"{f.Device}\nMD5 {h.Md5}\nSHA-1 {h.Sha1}\nSHA-256 {h.Sha256}");
        if (Selected == f) foreach (var (n, v) in new[] { ("MD5", h.Md5), ("SHA-1", h.Sha1), ("SHA-256", h.Sha256) }) _main.Details.Fields.Add(new Field(n, v));
        Info = $"Hashes of {f.Device} in the details and copied to the clipboard.";
    }

    partial void OnViewerAtChanged(string value)
    {
        if (value != InWindow) { _window?.Close(); return; }
        if (_window is not null) return;
        _window = new Views.FileViewerWindow(this, System.Windows.Application.Current.MainWindow);
        _window.Closed += (_, _) => { _window = null; if (ViewerAt == InWindow) ViewerAt = Right; };
        _window.Show();
    }

    /// <summary>The selected file decoded as a tree: a property list (XML, binary, keyed archive, nested plists, blind
    /// protobuf), or JSON or XML.</summary>
    [ObservableProperty] private IReadOnlyList<PlistNode> plist = [];

    [RelayCommand]
    static void CopyPlistPath(PlistNode? n) { if (n is not null) System.Windows.Clipboard.SetText(n.Path == "" ? "root" : n.Path); }

    [RelayCommand]
    static void CopyPlistValue(PlistNode? n) { if (n is not null) System.Windows.Clipboard.SetText(n.Children.Count > 0 ? PlistTree.ToText(n) : n.Value); }

    /// <summary>The selected file in the SQLite reader, from read-only copies of it and of its -wal, -shm and -journal.</summary>
    [RelayCommand]
    async Task OpenSqlite()
    {
        if (Selected is not { } f || _s.Extraction is not { } x) return;
        string? Side(string suffix) => x.Contains(f.Path + suffix) ? x.CopyOut(f.Path + suffix) : null;
        var (db, wal, shm, journal) = await Task.Run(() => (x.CopyOut(f.Path), Side("-wal"), Side("-shm"), Side("-journal")));
        if (db is null || !SqliteFile.IsSqlite(db)) { Info = $"{f.Device} is not a SQLite database (by its first bytes)."; return; }
        Views.SqliteWindow.Show(db, System.IO.Path.GetFileName(f.Device), $"{f.Device} · in the extraction {EvidenceOrigin.ExtractionName(_s)}", wal, shm, journal);
    }

    [RelayCommand]
    void SaveCopy()
    {
        if (Selected is not { } f || _s.Extraction is not { } x) return;
        var dlg = new SaveFileDialog { Title = "Save a copy of the file", FileName = System.IO.Path.GetFileName(f.Device) };
        if (dlg.ShowDialog() != true) return;
        var ok = x.Read(f.Path, s => { using var o = File.Create(dlg.FileName); s.CopyTo(o); return true; });
        Info = ok ? $"Copied {f.Path} to {dlg.FileName}" : $"Could not read {f.Path}";
        if (ok) ExportLog.Add("Extraction file: " + f.Device, dlg.FileName, "1 file", _s);
    }
}
