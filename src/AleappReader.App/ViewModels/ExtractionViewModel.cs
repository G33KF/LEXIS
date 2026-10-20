using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public partial class PluginItem(PluginInfo info) : ObservableObject
{
    public PluginInfo Info { get; } = info;
    [ObservableProperty] private bool isChecked = true;
}

/// <summary>Where the report goes when the run ends: an open investigation case, a new one, or NO CASE (Case null).</summary>
public sealed record CaseChoice(string Title, OpenCase? Case, bool IsNew = false);

/// <summary>Runs ALEAPP (Android) or iLEAPP (iOS) on an extraction and hands the produced manifest back to the reader.</summary>
public partial class ExtractionViewModel : ObservableObject
{
    public IReadOnlyList<CaseChoice> CaseChoices { get; init; } = [];
    [ObservableProperty] private CaseChoice? targetCase;
    CancellationTokenSource? _cts;

    public static string[] ToolChoices { get; } = ["ALEAPP — Android", "iLEAPP — iPhone / iPad"];
    [ObservableProperty] private string toolChoice = ToolChoices[0];
    public string Tool => ToolChoice == ToolChoices[1] ? "ileapp" : "aleapp";
    public string ToolName => Tool == "ileapp" ? "iLEAPP" : "ALEAPP";
    public string[] InputTypes => ExtractionRunner.InputTypes(Tool);
    public ObservableCollection<PluginItem> Plugins { get; } = [];
    public ICollectionView PluginsView { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NeedsPassword))] private string inputType = "zip";
    [ObservableProperty] private string inputPath = "";
    [ObservableProperty] private string outputDir = Settings.Current.LastOutputDir ?? "";
    [ObservableProperty] private string caseNumber = "";
    [ObservableProperty] private string agency = "";
    [ObservableProperty] private string examiner = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string keychain = "";
    public bool IsIos => Tool == "ileapp";
    [ObservableProperty] private string pluginFilter = "";
    [ObservableProperty] private string log = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string currentArtifact = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(LoadPluginsCommand))]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    private bool running;

    public bool NeedsPassword => InputType is "raw" or "itunes" or "ab";
    /// <summary>The engine runs from inside this program; null until it is ready.</summary>
    AleappInstall? _engine;
    [ObservableProperty] private string engineStatus = "";
    public string? ResultManifest { get; private set; }
    public event Action? RequestClose;

    public ExtractionViewModel()
    {
        InitEngine();
        PluginsView = CollectionViewSource.GetDefaultView(Plugins);
        PluginsView.Filter = o => PluginFilter == "" || o is PluginItem p &&
            $"{p.Info.Category} {p.Info.Artifact} {p.Info.Name}".Contains(PluginFilter, StringComparison.OrdinalIgnoreCase);
        PluginsView.GroupDescriptions.Add(new PropertyGroupDescription("Info.Category"));
    }

    /// <summary>The chosen tool's engine embedded in this exe; a development build without it uses the repo's checkout.</summary>
    void InitEngine()
    {
        Plugins.Clear();
        _engine = null;
        if (BundledEngine.Available(Tool))
        {
            _ = PrepareEngineAsync();
            return;
        }
        var dev = AleappInstall.Detect(AppContext.BaseDirectory, Tool);
        _engine = dev.IsValid ? dev : null;
        EngineStatus = _engine is null ? $"This build carries no {ToolName}: build the program with build.ps1." : $"{ToolName} from the source checkout (development build).";
        if (_engine is not null) _ = LoadPlugins();
    }

    partial void OnToolChoiceChanged(string value)
    {
        OnPropertyChanged(nameof(Tool)); OnPropertyChanged(nameof(ToolName)); OnPropertyChanged(nameof(InputTypes)); OnPropertyChanged(nameof(IsIos));
        if (!InputTypes.Contains(InputType)) InputType = "zip";
        InitEngine();
        OnPropertyChanged(nameof(KeyFileLabel));
        Keychain = ""; // a keychain is no keystore, and the other way round
        _ = FindKeychain(InputPath);
    }

    public string KeyFileLabel => IsIos
        ? "Keychain (optional): a keychain plist captured with the extraction (a *keychain*.plist file). Without one, iLEAPP uses a keychain the extraction carries."
        : "Keystore (optional): an Android keystore export captured with the extraction (a *keystore*.zip or .json file). ALEAPP decrypts the apps' encrypted preferences with it.";

    string? _proposedKeychain;

    /// <summary>Proposes the keys captured beside the extraction (an iOS keychain, an Android keystore export), or notes
    /// the keychain an iOS zip carries.</summary>
    async Task FindKeychain(string input)
    {
        if (Keychain != "" || input == "" || !(File.Exists(input) || Directory.Exists(input))) return;
        var ios = IsIos;
        var (beside, inside) = await Task.Run(() => (
            ios ? ExtractionRunner.KeychainBeside(input) : ExtractionRunner.KeychainBeside(input, ExtractionRunner.KeystorePatterns),
            ios && input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ExtractionRunner.KeychainInside(input) : null));
        if (input != InputPath || Keychain != "" || ios != IsIos) return; // changed meanwhile
        if (beside is not null) { Keychain = _proposedKeychain = beside; Append((ios ? "Keychain" : "Keystore") + " found beside the extraction: " + beside); }
        else if (inside is not null) Append($"The extraction carries a keychain ({inside}): iLEAPP reads it when no other is chosen.");
    }

    [RelayCommand]
    void BrowseKeychain()
    {
        var f = IsIos
            ? new OpenFileDialog { Title = "Keychain captured with the extraction", Filter = "Keychain plist|*.plist|All files|*.*" }
            : new OpenFileDialog { Title = "Android keystore captured with the extraction", Filter = "Keystore export|*.zip;*.json|All files|*.*" };
        if (f.ShowDialog() == true) Keychain = f.FileName;
    }

    /// <summary>Unpacks the embedded engine (first use of this version only), then lists its modules.</summary>
    async Task PrepareEngineAsync()
    {
        var tool = Tool;
        EngineStatus = BundledEngine.Ready(tool) is null ? $"Preparing the built-in {ToolName} (first use of this version only)…" : "";
        Running = true;
        try
        {
            var exe = await Task.Run(() => BundledEngine.Ensure(tool));
            if (tool != Tool) return; // switched device meanwhile
            _engine = new AleappInstall(null, null, exe, tool);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            EngineStatus = $"Could not prepare the built-in {ToolName}: " + e.Message;
            return;
        }
        finally { Running = false; }
        EngineStatus = $"Built in: {ToolName} runs inside this program, nothing to configure.";
        await LoadPlugins();
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    partial void OnPluginFilterChanged(string value) => PluginsView.Refresh();

    /// <summary>A .ufd file (picked, or beside the picked extraction) gives the extraction file and the case
    /// details the examiner already typed at acquisition; fields already filled in here are kept.</summary>
    partial void OnInputPathChanged(string value)
    {
        if (Keychain != "" && Keychain == _proposedKeychain) Keychain = ""; // proposed for the previous extraction
        _ = FindKeychain(value);
        UfdInfo? ufd;
        try
        {
            ufd = value.EndsWith(".ufd", StringComparison.OrdinalIgnoreCase) && File.Exists(value) ? UfdInfo.Parse(value) : UfdInfo.ForExtraction(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (ufd is null) return;
        if (value.EndsWith(".ufd", StringComparison.OrdinalIgnoreCase))
        {
            if (ufd.Extraction is not { } x) { Append("The UFD names no extraction file found beside it."); return; }
            if (x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) InputType = "zip";
            InputPath = x; // re-enters with the zip, which finds this UFD again
            return;
        }
        var device = ufd.Get("General", "Vendor") + " " + ufd.Get("General", "FullName");
        if (Tool == "aleapp" && new[] { "Apple", "iPhone", "iPad" }.Any(x => device.Contains(x, StringComparison.OrdinalIgnoreCase)))
        {
            ToolChoice = ToolChoices[1];
            Append("Apple device: switched to iLEAPP.");
        }
        if (CaseNumber == "") CaseNumber = ufd.Get("Crime Case", "Case Identifier");
        if (Agency == "") Agency = ufd.Get("Crime Case", "Department");
        if (Examiner == "") Examiner = ufd.Get("Crime Case", "Examiner Name");
        Append($".ufd file found: {ufd.Path} — device {ufd.Get("General", "FullName")}, {ufd.Get("General", "ExtractionType")} extraction.");
    }

    [RelayCommand]
    void BrowseInput()
    {
        if (InputType is "fs" or "itunes")
        {
            var d = new OpenFolderDialog { Title = InputType == "itunes" ? "iTunes backup folder" : "Extraction folder" };
            if (d.ShowDialog() == true) InputPath = d.FolderName;
            return;
        }
        var f = new OpenFileDialog { Filter = "Extractions or .ufd files|*.zip;*.tar;*.gz;*.tgz;*.bin;*.img;*.dd;*.raw;*.ufd;*.ab|All files|*.*" };
        if (f.ShowDialog() != true) return;
        if (Tool == "aleapp" && f.FileName.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)) InputType = "ab";
        InputPath = f.FileName;
    }

    [RelayCommand]
    void BrowseOutput()
    {
        var d = new OpenFolderDialog { Title = "Report output folder" };
        if (d.ShowDialog() == true) OutputDir = d.FolderName;
    }

    public bool NotRunning => !Running;

    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task LoadPlugins()
    {
        Running = true;
        try
        {
            Plugins.Clear();
            if (_engine is null) return;
            foreach (var p in await ExtractionRunner.ListPluginsAsync(_engine)) Plugins.Add(new PluginItem(p));
            Append(Plugins.Count > 0 ? $"{ToolName}: {Plugins.Count} modules available." : $"This {ToolName} cannot list its modules: all modules will run.");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            Append("Could not list modules: " + e.Message);
        }
        finally { Running = false; }
    }

    [RelayCommand]
    void CheckAll(string state)
    {
        foreach (var p in PluginsView.Cast<PluginItem>()) p.IsChecked = state == "true";
    }

    [RelayCommand(CanExecute = nameof(NotRunning))]
    async Task Run()
    {
        if (_engine is not { } engine) { Append(EngineStatus); return; }
        if (!File.Exists(InputPath) && !Directory.Exists(InputPath)) { Append("Input not found."); return; }
        if (string.IsNullOrWhiteSpace(OutputDir)) { Append("Choose an output folder."); return; }

        var s = Settings.Current;
        s.LastOutputDir = OutputDir;
        s.Save();

        var selected = Plugins.Count == 0 || Plugins.All(p => p.IsChecked) ? null : Plugins.Where(p => p.IsChecked).Select(p => p.Info.Name).ToList();
        if (selected is { Count: 0 }) { Append("No module selected."); return; }
        var caseData = new Dictionary<string, string> { ["Case Number"] = CaseNumber, ["Agency"] = Agency, ["Examiner"] = Examiner }
            .Where(kv => kv.Value != "").ToDictionary();
        var opts = new ExtractionOptions(InputType, InputPath, OutputDir, selected, caseData, NeedsPassword ? NullIfEmpty(Password) : null,
            NullIfEmpty(Keychain));

        Running = true;
        Progress = 0;
        CurrentArtifact = "";
        _cts = new CancellationTokenSource();
        var log = new Progress<string>(Append);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        RunProgress? last = null;
        TimeSpan? firstArtifact = null;
        void Show()
        {
            var el = clock.Elapsed;
            if (last is not { } p) { ProgressText = $"Reading the input · {el:hh\\:mm\\:ss}"; return; }
            firstArtifact ??= el;
            Progress = 100.0 * p.Done / Math.Max(1, p.Total);
            // ponytail: linear estimate over artifacts, which vary a lot in cost; shown only once a share has run
            var left = p.Done >= 20 && p.Done < p.Total ? (el - firstArtifact.Value) * ((double)(p.Total - p.Done) / p.Done) : (TimeSpan?)null;
            CurrentArtifact = p.Current == "" ? p.Phase : $"{p.Phase}: {p.Current}";
            ProgressText = $"{p.Done} / {p.Total} · no files {p.NoFiles} · failed {p.Failed} · {el:hh\\:mm\\:ss}" +
                           (left is { } l ? $" · ~{l:hh\\:mm\\:ss} left" : "");
        }
        var prog = new Progress<RunProgress>(p => { last = p; Show(); });
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Show();
        timer.Start();
        try
        {
            ResultManifest = await ExtractionRunner.RunAsync(engine, opts, log, prog, _cts.Token);
            Append("Done: " + ResultManifest);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) { Append("Cancelled."); }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Append("Failed: " + e.Message);
        }
        finally { timer.Stop(); Running = false; Password = ""; }
    }

    [RelayCommand(CanExecute = nameof(Running))]
    void Cancel() => _cts?.Cancel();

    void Append(string line)
    {
        // ponytail: whole-string log, capped; a line list control if logs ever need search
        var l = Log.Length > 200_000 ? Log[^150_000..] : Log;
        Log = l + line + Environment.NewLine;
    }
}
