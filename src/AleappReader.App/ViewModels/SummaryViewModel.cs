using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

public sealed record CategorySummary(string Name, int Artifacts, long Records);
/// <summary>A category of the report in the overview: its records, its artifacts and the biggest of them.</summary>
public sealed record ContentTile(string Name, long Records, int Artifacts, IReadOnlyList<ArtifactInfo> Top)
{
    public string Counts => $"{Records:N0} records · {Artifacts:N0} artifact{(Artifacts == 1 ? "" : "s")}";
}
/// <summary>A kind of user content (Messaging, Social Network, Gallery…) with the categories of the report that hold it.</summary>
public sealed record ContentKind(string Name, IReadOnlyList<ContentTile> Tiles)
{
    public string Counts => $"{Tiles.Sum(t => t.Records):N0} records · {Tiles.Count:N0} source{(Tiles.Count == 1 ? "" : "s")}";
    /// <summary>The first sources by name, for the folded header.</summary>
    public string Apps => string.Join(", ", Tiles.Take(4).Select(t => t.Name)) + (Tiles.Count > 4 ? ", …" : "");
}
/// <summary>A category of the LEAPP's device information, with its facts.</summary>
public sealed record FactGroup(string Title, IReadOnlyList<DeviceFact> Facts);
/// <summary>A fixed group of the device profile (Common info, Device, Settings) with its entries.</summary>
public sealed record ProfileGroup(string Title, IReadOnlyList<ProfileEntry> Entries);
/// <summary>One SIM card's card: its title, the identifiers it has, its sources on one line.</summary>
public sealed record SimCardView(string Title, IReadOnlyList<Field> Fields, string Sources);

/// <summary>Case overview: the device and extraction (from the .ufd file when one sits beside the extraction),
/// what was found, who produced the report, what ran and whether a portable case is intact.</summary>
public sealed partial class SummaryViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly UfdInfo? _ufd;
    readonly string _extraction;
    readonly ExtractionFiles? _files;
    public bool CanHashContent => _files is not null;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(HashContentCommand))] private string contentResult = "";
    /// <summary>The categories with the user's data by kind (Messaging, Social Network…), the biggest first in each.</summary>
    public IReadOnlyList<ContentKind> Contents { get; }
    /// <summary>The gallery's albums (Android media store buckets, iOS album titles) with their pictures and videos; one opens its rows.</summary>
    [ObservableProperty] private IReadOnlyList<Album> albums = [];
    public IReadOnlyList<Field> Device { get; }
    public IReadOnlyList<Field> Extraction { get; }
    public IReadOnlyList<Field> CaseInfo { get; }
    public bool HasUfd => _ufd is not null;
    public bool HasCaseInfo => CaseInfo.Count > 0;
    public bool CanVerify => _ufd is not null && _ufd.Sha256Of(Path.GetFileName(_extraction)) != "" && File.Exists(_extraction);
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(VerifyCommand))] private string verifyResult = "";
    [ObservableProperty] private bool? verified;
    public IReadOnlyList<Field> Info { get; }
    public IReadOnlyList<Field> Parameters { get; }
    public IReadOnlyList<ModuleStatus> Modules { get; }
    public IReadOnlyList<CategorySummary> Categories { get; }
    public string ModulesText { get; }
    public string? Integrity { get; }
    public bool IntegrityOk { get; }

    /// <summary>The device's main identifiers in one place, from the UFD and the LEAPP's device information: what it is,
    /// its software and its identifiers.</summary>
    [ObservableProperty] private IReadOnlyList<Field> glance = [];
    /// <summary>The device profile in its fixed groups (<see cref="DeviceOverview.Profile"/>), read after the view opens.</summary>
    [ObservableProperty] private IReadOnlyList<ProfileGroup> profile = [];
    /// <summary>The Android users and profiles as a table, for the Users tab.</summary>
    [ObservableProperty] private IReadOnlyList<AndroidUser> userTable = [];
    /// <summary>The owner and accounts in short: the first values of the account summary's first parts.</summary>
    [ObservableProperty] private IReadOnlyList<Field> ownerGlance = [];
    /// <summary>The tab shown: 0 Overview, 1 Device, 2 SIM cards, 3 Owner and accounts, 4 Users, 5 Report.</summary>
    [ObservableProperty] private int tab;
    public string SimCount => SimCards.Count == 0 ? "" : $"{SimCards.Count:N0} SIM card{(SimCards.Count == 1 ? "" : "s")} recorded";

    [RelayCommand]
    void ShowTab(string index) => Tab = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Each SIM card as its card shows it: the title, the identifiers it has, the sources.</summary>
    public IReadOnlyList<SimCardView> SimCardViews => SimCards.Select(c => new SimCardView(c.Title,
        new (string Name, string Value)[] { ("Number", c.Number), ("ICCID", c.Iccid), ("IMSI", c.Imsi), ("MCC", c.Mcc), ("MNC", c.Mnc), ("Carrier", c.Carrier),
            ("Country", c.Country), ("Slot", c.Slot), ("First seen", c.FirstSeen), ("Last seen", c.LastSeen) }
            .Where(f => f.Value != "").Select(f => new Field(f.Name, f.Value)).ToList(), c.Sources.Replace("\n", " · "))).ToList();

    partial void OnSimCardsChanged(IReadOnlyList<SimCard> value) { OnPropertyChanged(nameof(SimCount)); OnPropertyChanged(nameof(SimCardViews)); }

    partial void OnAccountsChanged(IReadOnlyList<AccountGroup>? value) => UpdateOwnerGlance();
    partial void OnAccountCardsChanged(IReadOnlyList<AccountCardView>? value) => UpdateOwnerGlance();

    // what names an account in the overview, in this order, one value of each
    static readonly string[] CoreValues = ["Full name", "Nickname", "Phone", "E-mail", "Username", "UID"];

    /// <summary>The owner and accounts in short: each account card with its name and main identifiers; until the cards are
    /// read, or when there are none, the first values of the account summary's first parts.</summary>
    void UpdateOwnerGlance()
    {
        const int Max = 6;
        if (AccountCards is { Count: > 0 } cards)
        {
            OwnerGlance = [.. cards.Take(Max).Select(c => new Field(c.Card.Title, string.Join(" · ", CoreValues
                    .Select(l => c.Card.Values.FirstOrDefault(v => v.Label == l)?.Value).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(3)))),
                .. cards.Count > Max ? [new Field("Other accounts", $"+{cards.Count - Max:N0}")] : Array.Empty<Field>()];
            return;
        }
        OwnerGlance = (Accounts ?? []).Where(g => g.Count > 0).Take(4)
            .Select(g => new Field(g.Title, string.Join("\n", g.Rows.Take(3).Select(r => r.Value)) + (g.Count > 3 ? $"\n+{g.Count - 3:N0} more" : ""))).ToList();
    }

    // the overview's identifiers the artifacts can add to: the label, and the label that makes it redundant
    static readonly (string Label, string? Unless)[] GlanceAdds = [("Manufacturer", null), ("Model", null), ("Android version", "Operating system"), ("Device name", null)];

    partial void OnProfileChanged(IReadOnlyList<ProfileGroup> value)
    {
        var have = Glance.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var add = GlanceAdds.Where(x => !have.Contains(x.Label) && (x.Unless is null || !have.Contains(x.Unless)))
            .SelectMany(x => value.SelectMany(g => g.Entries).Where(e => e.Label == x.Label)).Select(e => new Field(e.Label, e.Value)).ToList();
        if (add.Count > 0) Glance = [.. add, .. Glance];
    }
    public IReadOnlyList<FactGroup> DeviceFacts { get; }
    public IReadOnlyList<FactGroup> OwnerFacts { get; }
    public IReadOnlyList<FactGroup> SimFacts { get; }
    /// <summary>Artifacts with records about the device's starts and resets, its accounts, its SIM cards.</summary>
    public IReadOnlyList<ArtifactInfo> LifecycleArtifacts { get; }
    public IReadOnlyList<ArtifactInfo> OwnerArtifacts { get; }
    public IReadOnlyList<ArtifactInfo> SimArtifacts { get; }
    public string FactsSource { get; }
    /// <summary>Each account of the device once per service, with the names recorded with it (<see cref="AccountRoster"/>).</summary>
    [ObservableProperty] private IReadOnlyList<AccountGroup>? accounts;
    /// <summary>One row per account of each app or service (<see cref="AccountsOverview"/>), read after the view opens;
    /// the one selected shows in the details.</summary>
    [ObservableProperty] private IReadOnlyList<AccountCardView>? accountCards;
    [ObservableProperty] private AccountCardView? selectedAccount;

    partial void OnSelectedAccountChanged(AccountCardView? value) => value?.Show(_main.Details);
    /// <summary>The extraction as the overview shows it: the file, its type and the UFD's details, and the LEAPP that parsed it.</summary>
    public IReadOnlyList<Field> ExtractionOverview { get; }
    /// <summary>The last SIM card and the wipe, setup and restore dates (<see cref="DeviceOverview"/>), read after the view opens.</summary>
    [ObservableProperty] private IReadOnlyList<Field> lastSim = [];
    [ObservableProperty] private IReadOnlyList<Field> lifecycle = [];
    /// <summary>Every SIM card recorded, with its first and last dates (<see cref="SimHistory"/>).</summary>
    [ObservableProperty] private IReadOnlyList<SimCard> simCards = [];
    /// <summary>The Android users and profiles (owner, Secure Folder, work profile…), one per user id.</summary>
    [ObservableProperty] private IReadOnlyList<Field> users = [];
    /// <summary>The Statistics view in short: who the device exchanged with most and last, when its user was busiest, its positions.</summary>
    [ObservableProperty] private IReadOnlyList<Field> statistics = [];
    readonly CaseSession _s;

    [RelayCommand]
    void OpenStatistics() => _main.ShowStatistics(_s);

    /// <summary>The main identifiers of a device: each label's distinct values, the UFD's first.</summary>
    public static IReadOnlyList<Field> GlanceOf(IReadOnlyList<(string Group, string Name, string Value)> ufd, IReadOnlyList<DeviceFact> facts)
    {
        (string Label, string Pattern)[] keys =
        [
            ("Manufacturer", @"manufacturer|brand"), ("Model", @"^(model|model number|product name|product type|hardware model|vendor)$"),
            ("Codename", @"codename"), ("Device name", @"device name|host name|computer name"),
            ("Operating system", @"operating system|ios version|android version|^productversion$|product version"),
            ("Build", @"build"), ("Security patch", @"security patch"),
            ("Serial number", @"serial"), ("IMEI / MEID", @"\bimeis?\b|\bmeid\b"),
        ];
        var all = ufd.Where(x => x.Group == "Device").Select(x => (Label: x.Name, x.Value)).Concat(facts.SelectMany(f => f.Value.Contains(" | ")
            ? f.Value.Split(" | ").Select(p => p.Split(": ", 2)).Where(p => p.Length == 2).Select(p => (Label: p[0], Value: p[1]))
            : [(f.Label, f.Shown)])).ToList();
        return keys.Select(k => new Field(k.Label, string.Join("\n", all.Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Label, k.Pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(x => x.Value.Trim()).Where(v => v != "").Distinct(StringComparer.OrdinalIgnoreCase).Take(6))))
            .Where(f => f.Value != "").ToList();
    }

    public SummaryViewModel(MainViewModel main, CaseSession s)
    {
        _main = main;
        _s = s;
        var m = s.Manifest;
        _ = LoadStatisticsAsync();
        _extraction = EvidenceOrigin.InputPath(s);
        _ufd = UfdInfo.ForExtraction(_extraction);
        _files = s.Extraction;
        var ufd = _ufd?.Summary() ?? [];
        Field[] Group(string g) => ufd.Where(x => x.Group == g).Select(x => new Field(x.Name, x.Value)).ToArray();
        Device = Group("Device");
        Extraction = [new("Extraction file", _extraction), new("Extraction type (ALEAPP input)", m.Params.GetValueOrDefault("type") ?? ""), ..Group("Extraction")];
        var leappName = m.IsIos ? "iLEAPP" : "ALEAPP";
        Extraction = [.. Extraction, new(m.IsIos ? "Keychain" : "Keystore", ExtractionRunner.KeychainLog(s.RootDir) switch
            {
                null => $"Not recorded (the report keeps no {leappName} log)",
                [] => $"None recorded in the {leappName} log",
                var lines => string.Join("\n", lines),
            })];
        CaseInfo = Group("Case");
        var withData = m.Artifacts.Where(a => a.RecordCount > 0).ToList();

        var facts = LeappDeviceInfo.Read(s.RootDir);
        Glance = GlanceOf(ufd, facts);
        var known = Glance.Select(f => (f.Name, f.Value)).ToList();
        var leapp = m.ParserInfo.GetValueOrDefault("leapp_name") ?? "LEAPP";
        FactsSource = facts.Count == 0
            ? $"The report keeps no device information from {leapp} (_HTML/_Script_Logs/DeviceInfo.html is missing, as in a portable case). The artifacts below hold it."
            : $"As noted by {leapp} while parsing, each with the artifact it came from. Values as stored, a list or a SIM card one part per line.";
        IReadOnlyList<FactGroup> Groups(LeappDeviceInfo.Section sec) => facts.Where(f => LeappDeviceInfo.Of(f.Category, f.Label) == sec)
            .GroupBy(f => f.Category).Select(g => new FactGroup(g.Key, g.ToList())).ToList();
        (DeviceFacts, OwnerFacts, SimFacts) = (Groups(LeappDeviceInfo.Section.Device), Groups(LeappDeviceInfo.Section.Owner), Groups(LeappDeviceInfo.Section.Sim));
        List<ArtifactInfo> Arts(Func<string, bool> pick) => withData.Where(a => pick(a.Name)).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        (LifecycleArtifacts, OwnerArtifacts, SimArtifacts) = (Arts(LeappDeviceInfo.IsLifecycle), Arts(LeappDeviceInfo.IsOwner), Arts(LeappDeviceInfo.IsSim));
        _ = Task.Run(() => { using var con = s.OpenDb(); return AccountRoster.Summarize(AccountRoster.Collect(con, withData)); })
            .ContinueWith(t => Accounts = t.IsCompletedSuccessfully ? t.Result : [], TaskScheduler.FromCurrentSynchronizationContext());
        _ = AccountCardView.LoadAsync(s).ContinueWith(t => AccountCards = t.IsCompletedSuccessfully ? t.Result : [], TaskScheduler.FromCurrentSynchronizationContext());
        _ = Task.Run(() =>
            {
                using var con = s.OpenDb();
                static List<Field> F(IEnumerable<(string Label, string Value)> xs) => xs.Select(x => new Field(x.Label, x.Value)).ToList();
                var entries = DeviceOverview.Profile(con, withData, known);
                IReadOnlyList<ProfileGroup> profile = DeviceOverview.ProfileGroups.Select(g => new ProfileGroup(g, entries.Where(e => e.Group == g).ToList()))
                    .Where(g => g.Entries.Count > 0).ToList();
                return (F(DeviceOverview.LastSim(facts, con, withData)), F(DeviceOverview.Lifecycle(facts, con, withData)), F(DeviceOverview.Users(con, withData)),
                    SimHistory.Collect(facts, con, withData), profile, DeviceOverview.UserTable(con, withData));
            })
            .ContinueWith(t => { if (t.IsCompletedSuccessfully) (LastSim, Lifecycle, Users, SimCards, Profile, UserTable) = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
        Contents = withData.GroupBy(a => a.Category).Select(g => (Kind: MacroCategories.UserContentKind(g.Key, g), Tile: new ContentTile(g.Key, g.Sum(a => a.RecordCount), g.Count(),
                g.OrderByDescending(a => a.RecordCount).Take(4).ToList())))
            .Where(x => x.Kind is not null).GroupBy(x => x.Kind!)
            .OrderBy(g => MacroCategories.UserContentKinds.ToList().IndexOf(g.Key))
            .Select(g => new ContentKind(g.Key, [.. g.Select(x => x.Tile).OrderByDescending(t => t.Records)])).ToList();
        _ = Task.Run(() => { using var con = s.OpenDb(); return Core.Statistics.Albums(con, withData); })
            .ContinueWith(t => Albums = t.IsCompletedSuccessfully ? t.Result : [], TaskScheduler.FromCurrentSynchronizationContext());
        ExtractionOverview = [.. Extraction.Where(f => f.Value != ""), new("Parsed by", m.ParsedBy),
            new("Display time zone", ValueFormatter.Zone.DisplayName)];
        Info =
        [
            new("Case folder", s.RootDir),
            new("Processing status", m.ProcessingStatus),
            ..m.ParserInfo.Select(kv => new Field(kv.Key, kv.Value)),
            new("Artifacts", $"{m.Artifacts.Count:N0} ({m.Artifacts.Count(a => a.RecordCount > 0):N0} with data)"),
            new("Records", m.Artifacts.Sum(a => a.RecordCount).ToString("N0")),
            new("Display time zone", ValueFormatter.Zone.DisplayName),
        ];
        Parameters = m.Params.Select(kv => new Field(kv.Key, kv.Value)).ToList();
        Modules = m.Modules.OrderBy(x => x.Status == "success").ThenBy(x => x.Name).ToList();
        ModulesText = string.Join(" · ", m.Modules.GroupBy(x => x.Status).Select(g => $"{g.Key}: {g.Count()}"));
        Categories = m.ByCategory.Select(g => new CategorySummary(g.Key, g.Count(), g.Sum(a => a.RecordCount)))
            .OrderByDescending(c => c.Records).ToList();

        // checked against SHA256SUMS.txt and its signature when the zip was opened
        if (s.Package is { } package)
        {
            IntegrityOk = package.Intact;
            Integrity = ExaminerKey.Describe(package) + $"\nContent root (SHA-256 of SHA256SUMS.txt): {package.Root}";
        }
    }

    [RelayCommand]
    void OpenArtifact(ArtifactInfo? a) { if (a is not null) _main.NavigateTo(a); }

    [RelayCommand]
    void OpenAlbum(Album? a) { if (a is not null) _main.OpenFiltered(a.Artifact, a.Filter, $"{a.Artifact.Name} · {a.Name}"); }

    bool CanRunHashContent() => CanHashContent && !ContentResult.StartsWith("Hashing");

    /// <summary>Hashes every file of the extraction (read-only) into a content manifest whose own SHA-256 is the root,
    /// then offers to save the manifest.</summary>
    [RelayCommand(CanExecute = nameof(CanRunHashContent))]
    async Task HashContent()
    {
        ContentResult = "Hashing every file of the extraction…";
        var prog = new Progress<(int Done, int Total)>(p => ContentResult = $"Hashing every file of the extraction… {p.Done:N0} / {p.Total:N0}");
        try
        {
            var (manifest, root, unreadable) = await Task.Run(() => _files!.ContentManifest(prog));
            var files = manifest.Count(c => c == '\n');
            ContentResult = $"Content root (SHA-256 of the manifest of {files:N0} files): {root}" +
                            (unreadable.Count == 0 ? "" : $"\n{unreadable.Count:N0} file(s) could not be read and are not in the manifest: " + string.Join(", ", unreadable.Take(10)));
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the content manifest", Filter = "SHA-256 manifest (*.sha256)|*.sha256", FileName = root + ".sha256" };
            if (dlg.ShowDialog() == true)
            {
                await File.WriteAllTextAsync(dlg.FileName, manifest);
                ExportLog.Add("Content manifest (SHA-256)", dlg.FileName, $"{files:N0} files");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { ContentResult = "Could not read the extraction: " + e.Message; }
    }

    bool CanRunVerify() => CanVerify && VerifyResult != "Computing SHA-256 of the extraction…";

    /// <summary>Re-hashes the extraction (read-only) against the SHA-256 recorded in the .ufd file.</summary>
    [RelayCommand(CanExecute = nameof(CanRunVerify))]
    async Task Verify()
    {
        VerifyResult = "Computing SHA-256 of the extraction…";
        Verified = null;
        try
        {
            var (sha, ok) = await Task.Run(() => UfdInfo.VerifyAsync(_ufd!, _extraction));
            Verified = ok;
            VerifyResult = ok ? $"Matches the UFD: {sha}" : $"Does NOT match the UFD. Computed {sha}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { VerifyResult = "Could not read the extraction: " + e.Message; }
    }

    async Task LoadStatisticsAsync()
    {
        try
        {
            var st = await Core.Statistics.Of(_s);
            var events = await Core.Statistics.UserEvents(_s);
            var map = await Task.Run(() => Core.Statistics.Activity(events.Where(e => e.Type != "Cookies").SelectMany(e => e.Epochs)));
            var points = st.Positions.SelectMany(p => p.Points).ToList();
            var places = await Task.Run(() => Core.Statistics.Places(points));
            static string T(long e) => e == long.MinValue ? "" : ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(e));
            var parties = st.Interactions.GroupBy(i => i.Key)
                .Select(g => (Who: g.First().Display, Count: g.Sum(i => i.Count), Kinds: string.Join(", ", g.Select(i => i.Kind)), Last: g.MaxBy(i => i.Last)!)).ToList();
            var busiestHour = Enumerable.Range(0, map.WeekHours.Length).MaxBy(i => map.WeekHours[i]);
            string[] days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
            Statistics = new (string, string)[]
            {
                ("Parties exchanged with", parties.Count > 0 ? $"{parties.Count:N0} (one-to-one)" : ""),
                ("Most exchanged with", string.Join("\n", parties.OrderByDescending(p => p.Count).Take(3).Select(p => $"{p.Who}  ·  {p.Count:N0} ({p.Kinds})"))),
                ("Last exchange", parties.MaxBy(p => p.Last.Last) is { } l && l.Last.Last != long.MinValue ? $"{l.Who}  ·  {T(l.Last.Last)} ({l.Last.Kind}, {(l.Last.LastSent ? "out" : "in")})" : ""),
                ("Busiest day", map.Days.Count > 0 ? map.Days.MaxBy(d => d.Value) is var d ? $"{StatisticsViewModel.Day(d.Key)}  ·  {d.Value:N0} events of the user" : "" : ""),
                ("Busiest hour", map.Total > 0 ? $"{days[busiestHour / 24]} {busiestHour % 24:00}:00" : ""),
                ("Positions", points.Count > 0 ? $"{points.Count:N0}; most at {places.FirstOrDefault()?.Positions ?? 0:N0} in one place" : ""),
            }.Where(x => x.Item2 != "").Select(x => new Field(x.Item1, x.Item2)).ToList();
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { }
    }

}
