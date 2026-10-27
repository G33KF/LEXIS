using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

public partial class GalleryItem(MediaRef media, IReadOnlyList<MediaUse> uses, bool isChecked = false, Action<GalleryItem>? onCheck = null) : ObservableObject
{
    public MediaRef Media { get; } = media;
    /// <summary>Every chat message the file was sent or received in, the earliest first.</summary>
    public IReadOnlyList<MediaUse> Uses { get; } = uses;
    /// <summary>The messages of other files holding the same bytes (a copy forwarded, saved or received again).</summary>
    public IReadOnlyList<MediaUse> Copies { get; init; } = [];
    /// <summary>The earliest chat message the file came with, when a conversation artifact points to it.</summary>
    public MediaUse? Use => Uses.Count > 0 ? Uses[0] : null;
    /// <summary>The record the file belongs to, in an artifact's thumbnail view.</summary>
    public long RowId { get; init; } = -1;
    [ObservableProperty] private ImageSource? thumbnail;
    [ObservableProperty] private bool isChecked = isChecked;
    partial void OnIsCheckedChanged(bool value) => onCheck?.Invoke(this);
    /// <summary>The tag of the file (gallery) or of its record (an artifact's thumbnails), shown as a dot on the tile.</summary>
    [ObservableProperty] private string? tag;
    /// <summary>What a file tag is keyed by: its path in the device.</summary>
    public string TagKey => TagStore.FileKey(Media.SourcePath ?? Media.ExtractionPath ?? Media.Name);

    public string App => AppName ?? Use?.Artifact.Name ?? Media.Artifact;
    public string? AppName { get; init; }
    /// <summary>When the message was sent, else when the file was last changed or made.</summary>
    public long? Epoch => Use?.Epoch ?? Media.Modified ?? Media.Created;
    public string Caption => Use is { } u ? string.Join(" · ", new[] { u.Direction, u.Sender, Uses.Count > 1 ? $"{Uses.Count} messages" : "" }.Where(x => x != ""))
        : Media.Artifact;
    public string Tip => string.Join("\n", new[] { Media.SourcePath, Use is { } u ? $"{u.Artifact.Name} · {u.Chat}" : null,
        Epoch is { } e ? ValueFormatter.Format(e, new ColumnInfo("t", "t", "datetime")) : null }.Where(x => !string.IsNullOrEmpty(x)));
    public bool IsVideo => Media.Kind == "video";
    public string Glyph =>Media.Kind switch { "video" => "", "audio" => "", "image" => "", _ => "" };
}

/// <summary>All media referenced by the case, or every media file in the extraction, paged, with lazily decoded thumbnails.</summary>
public partial class GalleryViewModel : ObservableObject
{
    const int PageSize = 200;
    readonly MainViewModel _main;
    readonly CaseSession _s;
    List<(MediaRef M, IReadOnlyList<MediaUse> U, string Path, string App, bool Known, IReadOnlyList<MediaUse> C)> _all = [];

    public ObservableCollection<GalleryItem> Items { get; } = [];
    public string[] Kinds { get; } = ["all", "image", "video", "audio", "file"];
    public const string AllApps = "All apps";
    /// <summary>The apps the files belong to, those identified by name first; the rest under an "Others" group, folded.</summary>
    [ObservableProperty] private System.ComponentModel.ICollectionView apps = AppsView([]);
    [ObservableProperty] private string app = AllApps;
    public sealed record AppChoice(string Name, string Group);
    public const string AnyOrigin = "All origins", Gallery = "Device gallery", SentOrReceived = "Sent or received", Sent = "Sent",
        Received = "Received", Cache = "Cache", SystemFiles = "System files";
    public string[] Directions { get; } = [AnyOrigin, Gallery, SentOrReceived, Sent, Received, Cache, SystemFiles];
    [ObservableProperty] private string direction = AnyOrigin;

    static System.ComponentModel.ICollectionView AppsView(IEnumerable<AppChoice> apps)
    {
        var v = System.Windows.Data.CollectionViewSource.GetDefaultView(new List<AppChoice>([new(AllApps, "Apps"), .. apps]));
        v.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(AppChoice.Group)));
        return v;
    }

    /// <summary>A file's path on the device, and its app: named when the package or artifact is a known app, else the
    /// package, the artifact or the folder as before.</summary>
    static (MediaRef, IReadOnlyList<MediaUse>, string, string, bool, IReadOnlyList<MediaUse>) Entry(MediaRef m, IReadOnlyList<MediaUse> u)
    {
        var path = EvidenceOrigin.DevicePath(m.SourcePath ?? m.ExtractionPath);
        var pkg = MediaOrigin.Package(path);
        var fallback = u.Count > 0 ? u[0].Artifact.Name : m.Artifact;
        var name = MediaOrigin.AppName(pkg) ?? MediaOrigin.AppName(fallback);
        return (m, u, path, name ?? pkg ?? fallback, name is not null, []);
    }

    /// <summary>Sent or Received keeps a file when any of its messages went that way: a picture received and forwarded is both.</summary>
    bool OfOrigin((MediaRef M, IReadOnlyList<MediaUse> U, string Path, string App, bool Known, IReadOnlyList<MediaUse> C) e) => Direction switch
    {
        Gallery => MediaOrigin.IsGallery(e.Path),
        SentOrReceived => e.U.Count > 0,
        Sent or Received => e.U.Any(u => u.Direction == Direction),
        Cache => MediaOrigin.IsCache(e.Path),
        SystemFiles => MediaOrigin.IsSystem(e.Path),
        _ => true,
    };
    [ObservableProperty] private DateTime? from;
    [ObservableProperty] private DateTime? to;
    public const string ReportSource = "Linked to records", ExtractionSource = "Whole extraction";
    public string[] SourceChoices { get; } = [ReportSource, ExtractionSource];

    [ObservableProperty] private string source = ReportSource;
    [ObservableProperty] private string status = "";
    /// <summary>What the gallery is showing, in one line.</summary>
    [ObservableProperty] private string about = "";

    [ObservableProperty] private string kind = "all";
    [ObservableProperty] private string filter = "";
    [ObservableProperty] private GalleryItem? selected;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PrevPageCommand))]
    private int page;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PrevPageCommand))]
    private int total;

    public string PageText => Total == 0 ? "No media" :
        $"{Page * PageSize + 1:N0}–{Math.Min((Page + 1) * PageSize, Total):N0} of {Total:N0}";

    public GalleryViewModel(MainViewModel main, CaseSession s)
    {
        _main = main;
        _s = s;
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var fromExtraction = Source == ExtractionSource;
        Status = fromExtraction ? "Reading the extraction…" : "";
        _all = await Task.Run(() =>
        {
            if (fromExtraction) return _s.Extraction is { } x ? x.Media.Select(m => Entry(x.ToMedia(m), [])).ToList() : [];
            using var con = _s.OpenDb();
            var uses = MediaUses.Build(con, _s.Manifest);
            // one tile per file: a picture used by two records is listed by the report twice; all its refs give its messages
            var byDate = Comparer<MediaUse>.Create(MediaUses.Earliest);
            var all = new MediaResolver(_s).All(con, int.MaxValue).GroupBy(m => m.ExtractionPath ?? m.RefId)
                .Select(g => Entry(g.First(), g.Select(m => m.RefId).Distinct().SelectMany(id => uses.GetValueOrDefault(id) ?? [])
                    .DistinctBy(u => (u.Artifact.TableName, u.RowId)).Order(byDate).ToList()))
                .ToList();
            // a copy of the same bytes (forwarded, saved, received again) lends its messages to each file's history
            foreach (var g in MediaUses.SameContent(Enumerable.Range(0, all.Count).Where(i => all[i].Item1.Exists), i => all[i].Item1.Size, i => all[i].Item1.OpenRead()))
                if (g.Any(i => all[i].Item2.Count > 0))
                    foreach (var i in g)
                        all[i] = all[i] with { Item6 = g.Where(j => j != i).SelectMany(j => all[j].Item2).Except(all[i].Item2).Distinct().Order(byDate).ToList() };
            return all;
        });
        Apps = AppsView(_all.Where(e => e.App != "").DistinctBy(e => e.App).OrderBy(e => !e.Known).ThenBy(e => e.App, StringComparer.OrdinalIgnoreCase)
            .Select(e => new AppChoice(e.App, e.Known ? "Apps" : "Others")));
        App = AllApps;
        About = fromExtraction
            ? $"{_all.Count:N0} picture, video and audio files found in the extraction (read-only), whether or not a record points to them."
            : $"{_all.Count:N0} files the {_s.Manifest.ParserInfo.GetValueOrDefault("leapp_name") ?? "LEAPP"} linked to records (attachments, photos, avatars…). " +
              "Choose \"Whole extraction\" for every media file in the phone image.";
        Status = fromExtraction && _s.Extraction is null ? $"Extraction not reachable: {EvidenceOrigin.InputPath(_s)}" : "";
        Page = 0;
        Show();
    }

    partial void OnSourceChanged(string value) => _ = LoadAsync();

    IEnumerable<GalleryItem> Filtered()
    {
        var query = SearchQuery.Parse(Filter);
        var (lo, hi) = ValueFormatter.Days(From, To); // the day after a change of summer time has its own offset
        return _all.Where(e => (App == AllApps || e.App == App) && OfOrigin(e))
            .Select(e => new GalleryItem(e.M, e.U, !_unchecked.Contains(e.M), OnCheck) { AppName = e.App, Tag = TagOf(e.M, e.U), Copies = e.C }).Where(i =>
            (Kind == "all" || i.Media.Kind == Kind) &&
            (lo is null && hi is null || i.Epoch is { } ep && ep >= (lo ?? long.MinValue) && ep <= (hi ?? long.MaxValue)) &&
            (query.IsEmpty || query.Matches([i.Media.Name, i.Media.Artifact, i.Media.SourcePath, .. i.Uses.SelectMany(u => new[] { u.Chat, u.Sender })])));
    }

    Dictionary<string, string> _fileTags = [];
    Dictionary<string, Dictionary<long, TagEntry>> _recordTags = [];

    string? TagOf(MediaRef m, IReadOnlyList<MediaUse> uses) => TagOf(m, uses.Count > 0 ? uses[0] : null);

    string? TagOf(MediaRef m, MediaUse? u) => _fileTags.GetValueOrDefault(TagStore.FileKey(m.SourcePath ?? m.ExtractionPath ?? m.Name)) ??
        (u is null ? null : (_recordTags.TryGetValue(u.Artifact.TableName, out var t) ? t : _recordTags[u.Artifact.TableName] = _s.Tags.ForTable(u.Artifact.TableName))
            .GetValueOrDefault(u.RowId)?.Tag);

    /// <summary>Tags the files themselves (by path) and the records that name them; files that all hold the tag lose it.
    /// Returns false when it removed the tag.</summary>
    public bool TagFiles(IReadOnlyCollection<GalleryItem> items, string? tag)
    {
        var keys = items.Select(i => i.TagKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var t = tag is not null && items.All(i => i.Tag == tag) ? null : tag;
        _s.Tags.SetFiles(keys, t);
        var uses = items.Where(i => i.Use is not null).Select(i => (i.Use!.Artifact, i.Use.RowId)).Distinct().ToList();
        if (uses.Count > 0) _main.SetTag(uses, t);
        _fileTags = _s.Tags.FileTags();
        _recordTags.Clear();
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items.Where(i => set.Contains(i.TagKey))) i.Tag = t;
        _main.RefreshTagCount();
        foreach (var tab in _main.Tabs) if (tab.Content is TaggedViewModel tv) tv.Refresh();
        return t is not null;
    }

    void Show()
    {
        _fileTags = _s.Tags.FileTags();
        _recordTags.Clear();
        var list = Filtered().ToList();
        Total = list.Count;
        CheckedCount = list.Count(i => i.IsChecked);
        Items.Clear();
        foreach (var i in list.Skip(Page * PageSize).Take(PageSize)) Items.Add(i);
        var shown = ++_shown;
        _ = LoadThumbnailsAsync(_s, Items.ToList(), () => shown != _shown);
    }

    int _shown; // a newer Show() stops the thumbnails of the page it replaced

    /// <summary>Decodes the tiles' pictures one by one off the UI thread, until <paramref name="stale"/> says the tiles were replaced.</summary>
    public static async Task LoadThumbnailsAsync(CaseSession s, List<GalleryItem> items, Func<bool> stale)
    {
        foreach (var it in items.Where(i => i.Media.Kind is "image" or "video"))
        {
            if (stale()) return;
            try { it.Thumbnail = await Task.Run(() => it.Media.IsImage ? Thumb(s, it.Media, 160) : VideoThumb(s, it.Media, 160)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException) { }
        }
    }

    /// <summary>A video's frame as Windows Explorer shows it (the installed codecs decide); a video only in the
    /// extraction is copied to a temporary file first and deleted after.</summary>
    public static ImageSource? VideoThumb(CaseSession s, MediaRef m, int size)
    {
        if (m.Exists) return ShellThumb(m.LocalFile(), size);
        if (m.SourcePath is not { } src || s.Extraction?.Read(src, 200 << 20) is not { } bytes) return null;
        var tmp = Path.Combine(Path.GetTempPath(), $"aleapp-{Guid.NewGuid():N}{Path.GetExtension(m.Name)}");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            return ShellThumb(tmp, size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        finally { try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    static ImageSource? ShellThumb(string path, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f);
            int hr;
            IntPtr h;
            try { hr = f.GetImage(new Size(size, size), 0x8 /* THUMBNAILONLY, scaled to fit */, out h); }
            finally { Marshal.ReleaseComObject(f); } // lets the temp copy of a video be deleted
            if (hr != 0) return null;
            try
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally { DeleteObject(h); }
        }
        catch (Exception e) when (e is COMException or ArgumentException or FileNotFoundException) { return null; } // no codec or thumbnail handler
    }

    [StructLayout(LayoutKind.Sequential)] record struct Size(int Width, int Height);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid iid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

    /// <summary>A picture's thumbnail from the case copy, else read from the extraction; null when neither has it.</summary>
    public static ImageSource? Thumb(CaseSession s, MediaRef m, int width) =>
        m.Exists ? Thumb(m, width)
        : m.SourcePath is { } src && s.Extraction?.Read(src, 40 << 20) is { } bytes ? Thumb(new MemoryStream(bytes), width) : null;

    /// <summary>Decoded off the UI thread, frozen, file closed: the evidence file is never locked; a portable case's
    /// picture is decoded straight from the zip.</summary>
    public static ImageSource? Thumb(MediaRef m, int width)
    {
        try { return Thumb(m.OpenRead(), width); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static ImageSource? Thumb(Stream stream, int width)
    {
        try
        {
            using var fs = stream;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = width;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException
                                      or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null; // undecodable (e.g. HEIC without codec): the glyph stays
        }
    }

    partial void OnKindChanged(string value) => ApplyFilter();
    partial void OnAppChanged(string value) => ApplyFilter();
    partial void OnDirectionChanged(string value) => ApplyFilter();
    partial void OnFromChanged(DateTime? value) => ApplyFilter();
    partial void OnToChanged(DateTime? value) => ApplyFilter();
    async partial void OnSelectedChanged(GalleryItem? value)
    {
        if (value is null) return;
        var m = await Local(value.Media);
        if (Selected == value) _main.Details.ShowMedia(m, value.Use, value.Uses, value.Copies);
    }

    /// <summary>An extraction file is copied out (read-only, temp folder) so the viewer and "Open" work on a copy.</summary>
    async Task<MediaRef> Local(MediaRef m) =>
        !m.Exists && m.RefId == "" && _s.Extraction is { } x && await Task.Run(() => x.CopyOut(m.SourcePath!)) is { } copy ? x.ToMedia(m.SourcePath!, copy) : m;

    /// <summary>Double-click: a picture, video, audio or PDF in its own window; any other file straight in its program.</summary>
    public async void Open(GalleryItem item)
    {
        var m = await Local(item.Media);
        if (m.Exists && m.Kind == "file" && !DetailsViewModel.IsPdf(m)) DetailsViewModel.OpenMedia(m);
        else Views.RecordWindow.ShowMedia(_main, m, item.Use, item.Uses, item.Copies);
    }

    [RelayCommand]
    void ApplyFilter() { Page = 0; Show(); }

    public CaseSession Session => _s;
    /// <summary>Every file is checked at first: what is kept is the files unchecked.</summary>
    readonly HashSet<MediaRef> _unchecked = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CheckedText), nameof(HasChecked))] private int checkedCount;
    public string CheckedText => _unchecked.Count == 0 ? "All checked" : $"{CheckedCount:N0} checked";
    public bool HasChecked => CheckedCount > 0;

    void Count() => CheckedCount = Filtered().Count(i => i.IsChecked);

    void OnCheck(GalleryItem i)
    {
        if (i.IsChecked) _unchecked.Remove(i.Media); else _unchecked.Add(i.Media);
        Count();
    }

    [RelayCommand]
    void ClearChecks()
    {
        foreach (var e in _all) _unchecked.Add(e.M);
        foreach (var i in Items) i.IsChecked = false;
        Count();
    }

    [RelayCommand]
    void CheckAll()
    {
        _unchecked.Clear();
        foreach (var i in Items) i.IsChecked = true;
        Count();
    }

    /// <summary>The checked files the gallery's filters keep (all pages), those only in the extraction copied out; null,
    /// said in the status bar, when none is.</summary>
    async Task<AiScope?> Scope()
    {
        var chosen = Filtered().Where(i => i.IsChecked).Select(i => i.Media).ToList();
        if (chosen.Count == 0) { _main.Status = "Nothing checked: check the files first"; return null; }
        var media = await Readable(chosen);
        return new AiScope($"{media.Count:N0} {(_unchecked.Count > 0 ? "checked " : "")}media files of the gallery ({Kind}, {App}, {Direction})", [], media);
    }

    /// <summary>Files chosen one by one, those only in the extraction copied out (read-only, temp folder) for the AI.</summary>
    public async Task<List<MediaRef>> Readable(IEnumerable<MediaRef> media)
    {
        var list = media.ToList();
        if (_s.Extraction is not { } x) return list;
        return await Task.Run(() => list.Select(m => !m.Exists && m.RefId == "" && m.SourcePath is { } p && x.CopyOut(p) is { } copy ? x.ToMedia(p, copy) : m).ToList());
    }

    [RelayCommand] async Task Ai() { if (await Scope() is { } sc) _main.OpenAi(_s, sc); }
    [RelayCommand] async Task Describe() { if (await Scope() is { } sc && _main.OpenAi(_s, sc) is { } vm) _ = vm.DescribeImagesCommand.ExecuteAsync(null); }
    [RelayCommand] async Task Transcribe() { if (await Scope() is { } sc && _main.OpenAi(_s, sc) is { } vm) _ = vm.TranscribeCommand.ExecuteAsync(null); }

    bool CanNext() => (Page + 1) * PageSize < Total;
    bool CanPrev() => Page > 0;

    [RelayCommand(CanExecute = nameof(CanNext))]
    void NextPage() { Page++; Show(); }

    [RelayCommand(CanExecute = nameof(CanPrev))]
    void PrevPage() { Page--; Show(); }
}
