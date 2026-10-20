using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

/// <summary>
/// Indicators matched against every text cell of the case: STIX2 indicators of compromise (e.g. the MVT feeds), or the
/// built-in cryptocurrency address finder.
/// </summary>
public partial class IndicatorsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly CaseSession _s;
    CancellationTokenSource? _cts;
    string? _tag = "IOC match"; // null: each hit is tagged with its rule's tag

    public ObservableCollection<IocHit> Hits { get; } = [];
    [ObservableProperty] private IocHit? selected;
    [ObservableProperty] private string info = "Load STIX2 indicator files (.stix2), such as the feeds listed by the MVT project, or hash sets (MD5, SHA-1, SHA-256), " +
                                               "or use a built-in list, or tag records by your own keyword and /regex/ rules. Every text cell of the case is searched, and the extraction's file list when it is reachable. " +
                                               "Nothing is downloaded.";
    [ObservableProperty] private double progress;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand), nameof(LoadCommand), nameof(FindCryptoCommand), nameof(FindRootCommand), nameof(CheckSettingsCommand), nameof(TagByRulesCommand), nameof(TagAllCommand))]
    private bool running;

    public IndicatorsViewModel(MainViewModel main, CaseSession s)
    {
        _main = main;
        _s = s;
    }

    bool Idle => !Running;
    bool CanTag => !Running && Hits.Count > 0;

    [RelayCommand(CanExecute = nameof(Idle))]
    async Task Load()
    {
        var dlg = new OpenFileDialog { Title = "Indicator files or hash sets", Multiselect = true,
                                       Filter = "Indicators and hash sets|*.stix2;*.json;*.txt;*.csv;*.md5;*.sha1;*.sha256;*.hash|" +
                                                "STIX2 indicators (*.stix2;*.json)|*.stix2;*.json|All files (read as hash sets)|*.*" };
        if (dlg.ShowDialog() != true) return;
        await Run("IOC match", (p, ct) =>
        {
            var set = IndicatorSet.Load(dlg.FileNames);
            return (WithFiles(set, p, ct), $"{set.Count:N0} indicators from {set.Files.Count} file(s)");
        }, "A match says a cell or file holds an indicator value, not that the device was compromised. " +
           "Hash sets are matched against hash cells and against the extraction's media files, hashed on the fly.");
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    Task FindRoot() => Run("Root/jailbreak trace", (p, ct) => (WithFiles(RootTraces.Set(Platform), p, ct), $"{RootName} traces (built-in list)"),
        "A trace is not proof the device is rooted or jailbroken now: a package name can come from a web page, a su binary can outlive its root.");

    [RelayCommand(CanExecute = nameof(Idle))]
    Task CheckSettings() => Run("Security setting", (p, ct) => (SecurityPosture.Check(_s), $"{PlatformName} security settings"),
        Platform == true
            ? "The iOS version with the age of its major release, and passcode changes logged by lockdownd; age is counted to the report run. " +
              "iLEAPP does not report whether a passcode is set, nor configuration profiles."
            : "Settings that weaken the device's defences, and the security patch level; age is counted to the report run.");

    /// <summary>iOS (true), Android (false) or not known (null), from the LEAPP that made the report.</summary>
    bool? Platform => _s.Manifest.IsIos ? true : _s.Manifest.IsAndroid ? false : null;
    public string PlatformName => Platform switch { true => "iOS", false => "Android", null => "Device" };
    string RootName => Platform switch { true => "Jailbreak", false => "Root", null => "Root and jailbreak" };
    public string RootButton => $"Find {RootName.ToLowerInvariant()} traces";
    public string SettingsButton => $"Check {PlatformName} security settings";

    /// <summary>The set matched against every text cell, then against the extraction's file list when it is reachable.</summary>
    IReadOnlyList<IocHit> WithFiles(IndicatorSet set, IProgress<(int, int)> p, CancellationToken ct)
    {
        var hits = set.Scan(_s, p, ct);
        return _s.Extraction is { } x ? [.. hits, .. set.ScanFiles(x.AllPaths(), ct), .. set.ScanMediaHashes(x, p, ct)] : hits;
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    Task FindCrypto() => Run("Crypto address", (p, ct) =>
        ([.. IndicatorSet.Scan(_s, (Func<string, string, IEnumerable<Indicator>>)CryptoAddresses.Match, p, ct), .. WithFiles(CryptoAddresses.WalletApps(), p, ct)],
         "Cryptocurrency addresses, seed phrases and wallet apps"),
        "Addresses are reported only when their checksum holds and they stand on their own, not inside a token or a value named as a secret; " +
        "an Ethereum address written in one case has no checksum, nor has a Solana address, reported only where a word names it. " +
        "A seed phrase of fewer than 24 words must be the whole value or be named (seed, mnemonic, recovery…). A wallet app found is installed or was, not used.");

    [RelayCommand(CanExecute = nameof(Idle))]
    async Task TagByRules()
    {
        var dlg = new OpenFileDialog { Title = "Tagging rules (one per line: Tag: keyword or Tag: /regex/)", Filter = "Rules (*.txt)|*.txt|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        await Run(null, (p, ct) =>
        {
            var rules = TagRules.Load(dlg.FileName);
            return (IndicatorSet.Scan(_s, TagRules.Matcher(rules), p, ct), $"{rules.Count} rules");
        }, "Tag all matches gives each record the tag of its rule; a record matched by several rules keeps the last.");
    }

    async Task Run(string? tag, Func<IProgress<(int, int)>, CancellationToken, (IReadOnlyList<IocHit> Hits, string What)> work, string caveat)
    {
        Running = true;
        Hits.Clear();
        _tag = tag;
        _cts = new CancellationTokenSource();
        var prog = new Progress<(int Done, int Total)>(p => Progress = 100.0 * p.Done / Math.Max(1, p.Total));
        try
        {
            var (hits, what) = await Task.Run(() => work(prog, _cts.Token));
            foreach (var h in hits) Hits.Add(h);
            Info = $"{what}: {hits.Count:N0} matches ({hits.Select(h => h.Indicator.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} distinct values) in " +
                   $"{hits.Select(h => (h.Artifact, h.RowId)).Distinct().Count():N0} records of {hits.Select(h => h.Artifact).Distinct().Count()} artifacts. {caveat}";
        }
        catch (OperationCanceledException) { Info = "Scan cancelled."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Info = "Could not read the indicators.";
            MessageBox.Show(e.Message, "Indicators", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Running = false; TagAllCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(Running))]
    void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanTag))]
    void TagAll()
    {
        foreach (var g in Hits.Where(h => h.Artifact is not null).GroupBy(h => _tag ?? h.Indicator.Collection))
            _main.SetTag(g.Select(h => (h.Artifact!, h.RowId)), g.Key);
    }

    partial void OnSelectedChanged(IocHit? value)
    {
        if (value?.Artifact is { } a) _main.Details.ShowRecord(_s, a, value.RowId);
        else if (value is not null) _main.Details.ShowFields(value.Where, value.Indicator.Collection,
            [new("Path", value.Cell), new("Indicator", value.Indicator.Value), new("Indicator type", value.Indicator.Type)]);
    }

    [RelayCommand]
    void GoTo()
    {
        if (Selected is { Artifact: { } a } h) _main.NavigateTo(a, null, h.RowId);
    }
}
