using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public partial class PortableItem(ArtifactInfo a, Action changed) : ObservableObject
{
    public ArtifactInfo Artifact { get; } = a;
    public string Label => $"{Artifact.Name} ({Artifact.RecordCount:N0})";
    [ObservableProperty] private bool isChecked = true;
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>A macro category in the selection: checked, unchecked or partly (null).</summary>
public partial class PortableGroup : ObservableObject
{
    public string Name { get; }
    public IReadOnlyList<PortableItem> Items { get; }

    public PortableGroup(string name, IEnumerable<ArtifactInfo> artifacts, Action changed)
    {
        Name = name;
        Items = artifacts.Select(a => new PortableItem(a, () => { OnPropertyChanged(nameof(IsChecked)); changed(); })).ToList();
    }

    public bool? IsChecked
    {
        get => Items.All(i => i.IsChecked) ? true : Items.Any(i => i.IsChecked) ? null : false;
        set { foreach (var i in Items) i.IsChecked = value == true; }
    }
}

/// <summary>Builds a portable case: a zip with the chosen content and SHA256SUMS.txt, and the reader beside it.</summary>
public partial class PortableCaseViewModel : ObservableObject
{
    readonly CaseSession _s;
    CancellationTokenSource? _cts;

    public int TaggedCount { get; }
    public IReadOnlyList<PortableGroup> Groups { get; }
    [ObservableProperty] private bool taggedOnly;
    [ObservableProperty] private bool includeMedia = true;
    [ObservableProperty] private bool includeReader = true;
    [ObservableProperty] private bool sign = true;
    /// <summary>The fingerprint the recipient compares, by another channel, with the one the reader shows them.</summary>
    public string SignText { get; } = ExaminerKey.Fingerprint() is { } f
        ? $"Your examiner key's fingerprint: {f}. Give it to the recipient by another channel (mail, phone, the report)."
        : "Your examiner key is created on the first signed case, in your Windows key store: it never leaves this account.";
    [ObservableProperty] private bool includeFileSystem;
    [ObservableProperty] private bool allRead;
    /// <summary>Files tagged in Extraction Files or the gallery: carried with the case so the recipient sees them in its file system.</summary>
    public int TaggedFileCount { get; }
    public bool HasTaggedFiles => TaggedFileCount > 0;
    [ObservableProperty] private bool includeTaggedFiles;
    [ObservableProperty] private string fileSystemText = "";
    int _estimate;
    [ObservableProperty] private string zipPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        $"PortableCase_{DateTime.Now:yyyyMMdd_HHmm}", $"PortableCase_{DateTime.Now:yyyyMMdd_HHmm}.zip");
    [ObservableProperty] private string log = "";
    [ObservableProperty] private string selectionText = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(CancelCommand))] private bool running;

    public PortableCaseViewModel(CaseSession s)
    {
        _s = s;
        TaggedCount = s.Tags.All().Count(t => !t.TableName.StartsWith(TagStore.FilePrefix, StringComparison.Ordinal));
        TaggedFileCount = s.Tags.FileTags().Count;
        includeTaggedFiles = TaggedFileCount > 0;
        var counted = s.Manifest.Artifacts.Any(a => a.RecordCount > 0);
        var order = MacroCategories.All.ToList();
        Groups = s.Manifest.Artifacts.Where(a => !counted || a.RecordCount > 0).GroupBy(MacroCategories.Of).OrderBy(g => order.IndexOf(g.Key))
            .Select(g => new PortableGroup(g.Key.Name, g.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase), UpdateSelection)).ToList();
        UpdateSelection();
    }

    IEnumerable<PortableItem> AllItems => Groups.SelectMany(g => g.Items);

    void UpdateSelection()
    {
        var on = AllItems.Where(i => i.IsChecked).ToList();
        SelectionText = $"{on.Count:N0} of {AllItems.Count():N0} artifacts · {on.Sum(i => i.Artifact.RecordCount):N0} records";
        Estimate();
    }

    partial void OnIncludeFileSystemChanged(bool value) => Estimate();
    partial void OnAllReadChanged(bool value) => Estimate();
    partial void OnIncludeTaggedFilesChanged(bool value) => Estimate();

    PortableSelection Selection()
    {
        var chosen = AllItems.Where(i => i.IsChecked).Select(i => i.Artifact.TableName).ToHashSet();
        return new PortableSelection(chosen.Count == AllItems.Count() ? null : chosen, TaggedOnly, IncludeMedia,
            !IncludeFileSystem ? FileSystemScope.None : AllRead ? FileSystemScope.AllRead : FileSystemScope.Sources, IncludeTaggedFiles && TaggedFileCount > 0);
    }

    /// <summary>What "Include file system" adds, counted off the UI thread; a later change drops an older count.</summary>
    async void Estimate()
    {
        var n = ++_estimate;
        if (!IncludeFileSystem && !(IncludeTaggedFiles && TaggedFileCount > 0)) { FileSystemText = ""; return; }
        FileSystemText = "Counting the files…";
        var sel = Selection();
        string text;
        try
        {
            var fs = await Task.Run(() => PortableCase.FileSystemFiles(_s, sel));
            int x = fs.Count(f => f.FromExtraction), missing = fs.Count(f => f.Size < 0);
            text = $"{fs.Count:N0} files · {Size(fs.Where(f => f.Size > 0).Sum(f => f.Size))} · {x:N0} from the extraction, " +
                   $"{fs.Count - x - missing:N0} from ALEAPP's copies" + (missing > 0 ? $", {missing:N0} missing" : "") +
                   (_s.Extraction is null ? " · extraction not found: locate it in Extraction Files to read the originals" : "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            text = "Cannot count the files: " + e.Message;
        }
        if (n == _estimate) FileSystemText = text;
    }

    [RelayCommand]
    void SelectAll(string? on)
    {
        foreach (var g in Groups) g.IsChecked = on != "none";
    }

    [RelayCommand]
    void Browse()
    {
        var d = new SaveFileDialog { Filter = "Zip (*.zip)|*.zip", FileName = Path.GetFileName(ZipPath) };
        if (d.ShowDialog() == true) ZipPath = d.FileName;
    }

    bool NotRunning() => !Running;

    /// <summary>The reader to ship: this exe when it is a single-file build, else the build folder.</summary>
    static string Reader => File.Exists(Path.Combine(AppContext.BaseDirectory, "AleappReader.dll")) ? AppContext.BaseDirectory : Environment.ProcessPath!;

    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task Build()
    {
        if (!AllItems.Any(i => i.IsChecked)) { Log += "Select at least one artifact.\n"; return; }
        if (TaggedOnly && TaggedCount == 0) { Log += "No tagged items: tag records first or include all records.\n"; return; }
        var sel = Selection();
        Running = true;
        _cts = new CancellationTokenSource();
        var progress = new Progress<string>(l => Log += l + "\n");
        var lastPct = -1;
        var bytes = new Progress<(long Done, long Total)>(b =>
        {
            Progress = 100.0 * b.Done / Math.Max(1, b.Total);
            if ((int)Progress == lastPct) return; // one label update per percent, not per megabyte
            lastPct = (int)Progress;
            StatusText = $"Writing {Size(b.Done)} of {Size(b.Total)} ({lastPct}%)";
        });
        var (zip, reader, sign) = (ZipPath, IncludeReader ? Reader : null, Sign);
        Progress = 0;
        StatusText = "Preparing the content…";
        try
        {
            string? fingerprint = null;
            var sha = await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zip))!);
                using var key = sign ? ExaminerKey.Open(create: true) : null;
                fingerprint = key is null ? null : PortableCase.Fingerprint(key);
                PortableCase.Build(_s, zip, sel, reader, "AleappReader.exe", progress, _cts.Token, bytes, key);
                ((IProgress<string>)progress).Report("Computing the SHA-256 of the case…");
                using var f = File.OpenRead(zip);
                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var buf = new byte[1 << 20];
                for (int n; (n = f.Read(buf)) > 0;) { _cts.Token.ThrowIfCancellationRequested(); hash.AppendData(buf, 0, n); }
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            });
            StatusText = $"Completed: {zip} · {Size(new FileInfo(zip).Length)} · SHA-256 {sha}" +
                         (fingerprint is null ? " · not signed" : $" · signed, key fingerprint {fingerprint}");
            Log += StatusText + "\n";
            ExportLog.Add("Portable case" + (fingerprint is null ? "" : " (signed)"), zip, $"SHA-256 {sha}", _s);
        }
        catch (OperationCanceledException)
        {
            StatusText = File.Exists(zip) ? $"Cancelled after the case was written: {zip} is complete; the reader beside it or the SHA-256 may be missing."
                                          : "Cancelled: no portable case was written.";
            Log += "Cancelled.\n";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
                                      or Microsoft.Data.Sqlite.SqliteException or System.Security.Cryptography.CryptographicException)
        {
            StatusText = "Failed: " + e.Message;
            Log += "Failed: " + e.Message + "\n";
        }
        finally { Running = false; }
    }

    [RelayCommand(CanExecute = nameof(Running))]
    void Cancel() => _cts?.Cancel();

    static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : $"{b / (double)(1 << 20):0.0} MB";
}
