using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;
using Microsoft.Win32;

namespace AleappReader.App.ViewModels;

public enum NodeKind { Summary, Timeline, Map, Gallery, Documents, Tagged, Search, Indicators, Files, Group, Category, Artifact, Chats, ChatApp, ChatAccount,
                       Device, CoPresence, CommonContacts, Statistics, Case }

/// <summary>One report open in the reader: the only one, or a device of an investigation case, with the name and colour
/// the case gives it and the path it was opened from.</summary>
public sealed partial class Device(CaseSession session, string label, string color, string path = "") : ObservableObject
{
    public CaseSession Session { get; } = session;
    [ObservableProperty] private string label = label;
    [ObservableProperty] private string color = color;
    public string Path { get; } = path;
    public bool IsIos => Session.Manifest.IsIos;
    public string Platform => IsIos ? "iOS" : "Android";
}

/// <summary>An investigation case open in the reader: its file, name and number, and its devices in the examiner's order.
/// The default case, NO CASE, has no file: it holds the reports opened on their own and is never saved.</summary>
public sealed class OpenCase(string file, string name, string number)
{
    public const string NoCase = "NO CASE";
    public string File { get; } = file;
    public bool IsDefault => File == "";
    public string Name { get; } = name;
    public string Number { get; } = number;
    public List<Device> Devices { get; } = [];
    public string Title => string.Join(" ", new[] { Name, Number }.Where(x => x != "")) is { Length: > 0 } t ? t : System.IO.Path.GetFileNameWithoutExtension(File);
    /// <summary>The case's search node, its joint timeline node and its last search (for the search box and its counts).</summary>
    public NodeViewModel? SearchNode { get; set; }
    public NodeViewModel? JointTimeline { get; set; }
    public SearchViewModel? LastSearch { get; set; }

    public void Save()
    {
        if (!IsDefault) new LexisCase(Name, Number, [.. Devices.Select(d => new LexisDevice(d.Path, d.Label, d.Color))]).Save(File);
    }
}

public partial class NodeViewModel : ObservableObject
{
    public required string Title { get; init; }
    public required NodeKind Kind { get; init; }
    public string Glyph { get; init; } = "";
    public ArtifactInfo? Artifact { get; init; }
    /// <summary>The device the node belongs to; null for the views across every device of an investigation case.</summary>
    public CaseSession? Session { get; set; }
    /// <summary>The device's colour on a device node.</summary>
    public string? Color { get; init; }
    /// <summary>The device a device node stands for (its name, platform and colour).</summary>
    public Device? Device { get; init; }
    /// <summary>The investigation case of a case node and of the views across its devices.</summary>
    public OpenCase? Case { get; init; }
    /// <summary>The local account a ChatAccount node lists the chats of.</summary>
    public string? Account { get; init; }
    /// <summary>The column filter an Artifact node opens its table with (a browser of the Chromium artifacts).</summary>
    public ColumnFilter? Filter { get; init; }
    /// <summary>The view of a node whose results take a run to make (indicators, co-presence, common contacts, files), kept
    /// so that going to another node and back finds them again.</summary>
    public object? Kept { get; set; }
    public ObservableCollection<NodeViewModel> Children { get; } = [];
    [ObservableProperty] private string countText = "";
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isSelected;
    public bool IsEmpty => Kind == NodeKind.Artifact && Artifact!.RecordCount == 0;

    /// <summary>The artifacts under the node, at any depth.</summary>
    public IEnumerable<ArtifactInfo> Artifacts() => Children.SelectMany(c => c.Artifact is { } a ? [a] : c.Artifacts());
}

/// <summary>A category of the tree opened: its artifacts, each with its app and records, most records first; one opens
/// its table. User Accounts &amp; Details shows the device's account cards above them.</summary>
public partial class CategoryViewModel : ObservableObject
{
    readonly MainViewModel _main;

    public CategoryViewModel(MainViewModel main, NodeViewModel node, CaseSession s)
    {
        _main = main;
        Title = node.Title;
        Artifacts = [.. node.Artifacts().Distinct().OrderByDescending(a => a.RecordCount)];
        if (Title == MacroCategories.Accounts.Name)
            _ = AccountCardView.LoadAsync(s).ContinueWith(t => AccountCards = t.IsCompletedSuccessfully ? t.Result : [], TaskScheduler.FromCurrentSynchronizationContext());
    }

    public string Title { get; }
    public IReadOnlyList<ArtifactInfo> Artifacts { get; }
    public string Info => $"{Artifacts.Count:N0} artifact{(Artifacts.Count == 1 ? "" : "s")} · {Artifacts.Sum(a => a.RecordCount):N0} records";
    /// <summary>The account cards, for User Accounts &amp; Details; null for another category or while they are read.</summary>
    [ObservableProperty] private IReadOnlyList<AccountCardView>? accountCards;
    [ObservableProperty] private AccountCardView? selectedAccount;

    partial void OnSelectedAccountChanged(AccountCardView? value) => value?.Show(_main.Details);

    [RelayCommand]
    void Open(ArtifactInfo? a) { if (a is not null) _main.NavigateTo(a); }
}

/// <summary>A tab of the main area: one per opened tree node, chat or map; <see cref="Key"/> finds it again.</summary>
public partial class TabViewModel : ObservableObject
{
    public required string Key { get; init; }
    /// <summary>The navigation tab this one is (the single one or a group's): the next view of it takes its place.</summary>
    public string? Slot { get; init; }
    public required string Header { get; init; }
    public string Glyph { get; init; } = "";
    public bool CanClose { get; init; } = true;
    public NodeViewModel? Node { get; set; }
    /// <summary>The device the tab shows; null for a view across devices.</summary>
    public CaseSession? Session { get; init; }
    /// <summary>In an investigation case, the device the tab shows: its dot, platform and name mark the tab and the view.</summary>
    public Device? Device { get; init; }
    /// <summary>The investigation case of a view across its devices.</summary>
    public OpenCase? Case { get; init; }
    [ObservableProperty] private object? content;

    // a table view holds an open cursor on the case database
    partial void OnContentChanged(object? oldValue, object? newValue) => (oldValue as IDisposable)?.Dispose();
}

/// <summary>Shell: owns the open case, the navigation tree, the current view and the details pane.</summary>
public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<NodeViewModel> Nodes { get; } = [];
    public DetailsViewModel Details { get; }
    public IReadOnlyList<TimeZoneInfo> Zones { get; } = TimeZoneInfo.GetSystemTimeZones();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCase), nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(PortableCaseCommand), nameof(SearchCommand), nameof(CloseCaseCommand), nameof(AnalyzeWithAiCommand))]
    private CaseSession? session;
    [ObservableProperty] private TabViewModel? selectedTab;
    public ObservableCollection<TabViewModel> Tabs { get; } = [];
    [ObservableProperty] private NodeViewModel? selectedNode;
    [ObservableProperty] private string status = "Open an ALEAPP report folder, a _lava_data.lava file or a portable case.";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool busy;
    /// <summary>Set while an altered portable case is open; shown above everything until the case is closed.</summary>
    [ObservableProperty] private string? packageWarning;
    [ObservableProperty] private TimeZoneInfo zone = ValueFormatter.Zone;

    /// <summary>The reports open, the devices of the cases open. <see cref="Session"/> is the one of the tab in front.</summary>
    public List<Device> Devices { get; } = [];
    /// <summary>The cases open: NO CASE first, then the investigation cases in the order they were opened; none when
    /// nothing is open.</summary>
    public List<OpenCase> Cases { get; } = [];
    OpenCase NoCase()
    {
        if (Cases.FirstOrDefault(c => c.IsDefault) is { } n) return n;
        Cases.Insert(0, n = new OpenCase("", ViewModels.OpenCase.NoCase, ""));
        return n;
    }
    static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    /// <summary>The tree starts from the cases: each case, the views across its devices, then each device.</summary>
    public bool CaseMode => Cases.Count > 0;
    readonly Dictionary<ArtifactInfo, CaseSession> _owner = new(ReferenceEqualityComparer.Instance);

    /// <summary>The device a record of this artifact is on (artifacts are told apart by instance, not by table name,
    /// since every device has its own get_chat).</summary>
    public CaseSession? SessionOf(ArtifactInfo a) => _owner.GetValueOrDefault(a) ?? Session;
    public Device? DeviceOf(CaseSession? s) => Devices.FirstOrDefault(d => d.Session == s);
    public OpenCase? CaseOf(CaseSession? s) => Cases.FirstOrDefault(c => c.Devices.Any(d => d.Session == s));
    /// <summary>A device's key in the tab keys: the same while it is open, whatever its place or case.</summary>
    static int Dev(CaseSession? s) => s is null ? -1 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s);
    static int Id(OpenCase c) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);

    public bool HasCase => CaseMode;
    public string WindowTitle => !CaseMode ? "LEXIS — LEAPP Reader"
        : $"LEXIS — {string.Join(" · ", Cases.Where(c => !c.IsDefault || c.Devices.Count > 0).Select(c => c.Title))} ({Devices.Count} device{(Devices.Count == 1 ? "" : "s")})";

    /// <summary>After the cases open changed: the window title and what can be done with nothing or something open.</summary>
    void CasesChanged()
    {
        OnPropertyChanged(nameof(HasCase));
        OnPropertyChanged(nameof(WindowTitle));
        foreach (var c in new IRelayCommand[] { PortableCaseCommand, SearchCommand, CloseCaseCommand, AnalyzeWithAiCommand }) c.NotifyCanExecuteChanged();
    }
    public IEnumerable<string> RecentCases => Settings.Current.RecentCases;

    public MainViewModel()
    {
        Details = new DetailsViewModel(this);
        UpdateMcp();
    }

    McpHttpServer? _mcpHttp;
    volatile McpServer? _mcp;
    /// <summary>What the MCP server is doing, for the AI settings.</summary>
    public string McpState { get; private set; } = "";

    // the server follows the device in front; each device's tool answers go to its own AI base
    partial void OnSessionChanged(CaseSession? value) =>
        _mcp = value is null ? null : new McpServer(value, AiStore.For(value), McpServer.ExternalRun + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

    /// <summary>Starts or stops the MCP server inside LEXIS as the AI settings say.</summary>
    public void UpdateMcp()
    {
        var ai = Settings.Current.Ai;
        if (ai.McpAllowed && ai.McpToken == "") { ai.McpToken = McpHttpServer.NewToken(); Settings.Current.Save(ai: true); }
        if (_mcpHttp is not null && (!ai.McpAllowed || _mcpHttp.Url != ai.McpUrl)) { _mcpHttp.Dispose(); _mcpHttp = null; }
        if (ai.McpAllowed && _mcpHttp is null)
            try { _mcpHttp = new McpHttpServer(() => _mcp, ai.McpPort, ai.McpToken); }
            catch (System.Net.HttpListenerException e) { McpState = $"The MCP server cannot start on port {ai.McpPort}: {e.Message}"; return; }
        McpState = _mcpHttp is null ? "Off." : "On, at " + _mcpHttp.Url + ", serving the case open in LEXIS (the device in front).";
    }

    /// <summary>Brings the tab with this key forward, opening it first if needed; <paramref name="replace"/> swaps its view.</summary>
    void Show(string key, string header, string glyph, Func<object> make, NodeViewModel? node = null, bool replace = false, CaseSession? session = null,
              OpenCase? @case = null)
    {
        var tab = Tabs.FirstOrDefault(t => t.Key == key);
        session ??= node?.Session;
        if (tab is null)
        {
            var slot = SlotOf(node);
            tab = new TabViewModel { Key = key, Slot = slot, Header = header, Glyph = glyph, Node = node, Session = session,
                                     Device = CaseMode ? DeviceOf(session) : null, Case = @case ?? node?.Case ?? CaseOf(session), Content = make() };
            // the tab of the single navigation tab or of the node's group takes the new view in its place
            if (slot is not null && Tabs.FirstOrDefault(t => t.Slot == slot) is { } held)
            {
                Tabs[Tabs.IndexOf(held)] = tab;
                held.Content = null;
            }
            else Tabs.Add(tab);
        }
        else
        {
            if (replace) tab.Content = make();
            if (node is not null) tab.Node = node; // the same artifact under its category and under its app
        }
        SelectedTab = tab;
    }

    /// <summary>The tab a view goes into as the settings say: the single navigation tab, or the tab of the node's group (its
    /// first ancestor, or itself, that is not a case, a device or a fold such as Analyzed Data); null for a tab of its own.</summary>
    string? SlotOf(NodeViewModel? node)
    {
        if (Settings.Current.SingleNavigationTab) return "single";
        if (!Settings.Current.NavigationGroupTabs || node is null) return null; // ponytail: a chat or a map opened from a view gets its own tab
        var path = Walk(Nodes).FirstOrDefault(x => x.Node == node).Path ?? [];
        // a case's or a device's own view (its summary) belongs to no group: a tab of its own
        return path.Append(node).FirstOrDefault(n => n.Kind is not (NodeKind.Case or NodeKind.Device or NodeKind.Group)) is { } g ? "group|" + KeyOf(g) : null;
    }

    const string BrowserColumn = "Browser Name";
    static string KeyOf(NodeViewModel n) => $"{Dev(n.Session)}|{n.Kind}|{n.Artifact?.TableName}|{n.Account}|{n.Filter?.Op}{n.Filter?.Value}" +
        (n.Session is null && n.Case is { } c ? "|case" + Id(c) : "") + // the views across the devices of one case
        (n.Kind == NodeKind.Category ? "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(n) : ""); // categories may share a name

    /// <summary>Keeps the tree on the node of the front tab, so clicking a node whose tab was closed opens it again.</summary>
    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        if (SelectedNode is { } cur && cur != value?.Node) cur.IsSelected = false;
        if (value?.Node is { } n) n.IsSelected = true;
        if (value?.Session is { } s && s != Session) Session = s; // the current device follows the tab in front
        Details.Clear(); // the details belong to the tab left behind
    }

    public void OpenChat(ChatViewModel chat) =>
        Show($"chat|{Dev(SessionOf(chat.Artifact))}|{chat.Artifact.TableName}|{chat.Chat.Account}|{chat.Chat.Key}", chat.Chat.Label, "", () => chat, session: SessionOf(chat.Artifact));

    [RelayCommand]
    void CloseTab(TabViewModel? tab)
    {
        if (tab is null || !tab.CanClose) return;
        var i = Tabs.IndexOf(tab);
        if (SelectedTab == tab) SelectedTab = Tabs.Count > 1 ? Tabs[i > 0 ? i - 1 : 1] : null;
        Tabs.Remove(tab);
        tab.Content = null;
    }

    /// <summary>Closes every closable tab but <paramref name="keep"/> (null: all of them); the fixed tabs stay.</summary>
    [RelayCommand]
    void CloseOtherTabs(TabViewModel? keep)
    {
        foreach (var t in Tabs.Where(t => t.CanClose && t != keep).ToList())
        {
            Tabs.Remove(t);
            t.Content = null;
        }
        SelectedTab = keep ?? Tabs.FirstOrDefault();
    }

    partial void OnZoneChanged(TimeZoneInfo value)
    {
        ValueFormatter.Zone = value;
        Settings.Current.TimeZoneId = value.Id;
        Settings.Current.Save();
        RefreshViews();
    }

    partial void OnSelectedNodeChanged(NodeViewModel? value)
    {
        if (value is null || Session is null && value.Kind != NodeKind.Case) return; // a case may have no device
        // a node without a device is a view across every device of its investigation case
        var all = value.Session is null && value.Case is not null;
        var devices = value.Case?.Devices ?? Devices;
        var s = (value.Session ?? Session)!; // null only on a case node, which does not use it
        Func<object>? make = value.Kind switch
        {
            NodeKind.Case => () => new CaseOverviewViewModel(this, value.Case!),
            NodeKind.Summary or NodeKind.Device => () => new SummaryViewModel(this, s),
            NodeKind.Statistics => () => new StatisticsViewModel(this, s),
            NodeKind.Timeline => () => new TimelineViewModel(this, all ? devices : [DeviceOf(s)!]),
            NodeKind.Map => () => all ? new MapViewModel(this, devices) : new MapViewModel(this, s),
            NodeKind.CoPresence => () => value.Kept ??= new CoPresenceViewModel(this, devices),
            NodeKind.CommonContacts => () => value.Kept ??= new CommonContactsViewModel(this, devices),
            NodeKind.Gallery => () => new GalleryViewModel(this, s),
            NodeKind.Tagged => () => new TaggedViewModel(this, s),
            NodeKind.Search => () => all ? value.Case?.LastSearch ?? new SearchViewModel(this, devices, "")
                                         : value.Kept ??= new SearchViewModel(this, [DeviceOf(s)!], ""), // this device only
            NodeKind.Indicators => () => value.Kept ??= new IndicatorsViewModel(this, s),
            NodeKind.Files => () => value.Kept ??= new FilesViewModel(this, s),
            NodeKind.Documents => () => value.Kept ??= new FilesViewModel(this, s, RawFiles.Documents, n => value.CountText = n.ToString("N0")),
            NodeKind.Artifact => () => new ArtifactViewModel(this, s, value.Artifact!, columnFilter: value.Filter),
            NodeKind.ChatApp => () => new ConversationViewModel(this, s, value.Artifact!),
            NodeKind.ChatAccount => () => new ConversationViewModel(this, s, value.Artifact!, value.Account, value.Title),
            NodeKind.Category => () => new CategoryViewModel(this, value, s),
            _ => null, // groups only fold the tree
        };
        if (make is not null) Show(KeyOf(value), value.Title, value.Glyph, make, value);
    }

    [RelayCommand]
    void OpenCase()
    {
        var dlg = new OpenFolderDialog { Title = "Select an ALEAPP report folder" };
        if (dlg.ShowDialog() == true) OpenCase(dlg.FolderName);
    }

    [RelayCommand]
    void OpenCaseFile()
    {
        var dlg = new OpenFileDialog { Filter = "ALEAPP case or investigation case (*.lava;*.json;*.zip;*.lexiscase)|*.lava;*_lava_data.json;*.zip;*.lexiscase|All files|*.*" };
        if (dlg.ShowDialog() == true) OpenCase(dlg.FileName);
    }

    [RelayCommand]
    void OpenRecent(string path) => OpenCase(path);

    /// <summary>Opens a report into NO CASE, or an investigation case (.lexiscase) beside the cases open; either open
    /// already is selected.</summary>
    public async void OpenCase(string path) => await OpenCaseAsync(path); // a failure not handled there reaches the crash handler

    public async Task OpenCaseAsync(string path)
    {
        var isCase = path.EndsWith(LexisCase.Extension, StringComparison.OrdinalIgnoreCase);
        if (isCase && Cases.FirstOrDefault(c => !c.IsDefault && SamePath(c.File, path)) is { } open)
        {
            SelectedNode = Nodes.FirstOrDefault(n => n.Case == open);
            return;
        }
        if (!isCase && Devices.FirstOrDefault(d => d.Path != "" && SamePath(d.Path, path)) is { } there)
        {
            SelectDevice(there);
            return;
        }
        Busy = true;
        Status = "Opening " + path + " …";
        var opened = new List<Device>();
        try
        {
            var inv = isCase ? LexisCase.Load(path) : null;
            foreach (var d in inv?.Devices ?? [new LexisDevice(System.IO.Path.GetFullPath(path), "", LexisCase.Palette[0])])
            {
                Status = $"Opening {(d.Label != "" ? d.Label + ": " : "")}{d.Path} …";
                CaseSession s;
                try { s = await Task.Run(() => CaseSession.Open(d.Path)); }
                catch (Exception e) when (inv is not null && e is IOException or UnauthorizedAccessException or InvalidDataException
                                              or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
                {
                    throw new IOException($"Device \"{d.Label}\" ({d.Path}): {e.Message}", e);
                }
                opened.Add(new Device(s, d.Label, d.Color, d.Path));
                if (s.Package is { Intact: false } check &&
                    MessageBox.Show((d.Label != "" ? $"Device \"{d.Label}\".\n\n" : "") + ExaminerKey.Describe(check) +
                                    "\n\nOpen it anyway? It stays marked as altered while it is open.", "Portable case altered",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    Status = "Not opened: the portable case is altered.";
                    return;
                }
            }
            var noCase = NoCase(); // always there while anything is open, so devices can be moved out of a case
            OpenCase target;
            if (inv is null)
            {
                target = noCase;
                var d = opened[0];
                d.Label = FreeLabel(target.Devices.Select(x => x.Label), LabelOf(path));
                d.Color = LexisCase.Palette.FirstOrDefault(p => target.Devices.All(x => x.Color != p)) ?? d.Color;
            }
            else
            {
                Cases.Add(target = new OpenCase(System.IO.Path.GetFullPath(path), inv.Name, inv.Number));
                // a report open on its own that the case lists is the case's now
                foreach (var d in noCase.Devices.Where(d => opened.Any(o => SamePath(o.Path, d.Path))).ToList())
                {
                    noCase.Devices.Remove(d);
                    Drop(d);
                }
            }
            target.Devices.AddRange(opened);
            Devices.AddRange(opened);
            foreach (var d in opened) foreach (var a in d.Session.Manifest.Artifacts) _owner[a] = d.Session;
            var first = opened.FirstOrDefault();
            opened.Clear(); // the case owns them now
            Session = first?.Session ?? Devices.FirstOrDefault()?.Session; // a case may have no device yet
            UpdatePackageWarning();
            Settings.Current.AddRecent(path);
            OnPropertyChanged(nameof(RecentCases));
            RebuildTree();
            if (inv is null) SelectDevice(first!); // a report opens on its overview, a case on its own
            else SelectedNode = Nodes.First(n => n.Case == target);
            var arts = (inv is null ? [first!] : target.Devices).SelectMany(d => d.Session.Manifest.Artifacts).ToList();
            Status = (inv is not null ? $"{target.Title}: {target.Devices.Count} device{(target.Devices.Count == 1 ? "" : "s")} · " : "") +
                     $"{arts.Count} artifacts in {arts.Select(a => a.Category).Distinct().Count()} categories · {arts.Sum(a => a.RecordCount):N0} records";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
                                      or Microsoft.Data.Sqlite.SqliteException)
        {
            Status = "Could not open the case.";
            MessageBox.Show(e.Message, "Open case", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            foreach (var d in opened) d.Session.Dispose(); // not opened after all
            Busy = false;
        }
    }

    /// <summary>Edits an investigation case (by default the one of the device in front), or with NO CASE makes a new case of
    /// its devices (which then leave NO CASE). The case is opened again as saved.</summary>
    [RelayCommand]
    public void InvestigationCase(OpenCase? c = null)
    {
        c ??= SelectedNode?.Case ?? CaseOf(Session);
        var devices = c?.Devices.Select(d => new LexisDevice(d.Path, d.Label, d.Color)).ToList() ?? [];
        var vm = c is { IsDefault: false } ? new InvestigationCaseViewModel(new LexisCase(c.Name, c.Number, devices), c.File)
            : new InvestigationCaseViewModel(new LexisCase("", "", devices), null);
        new Views.InvestigationCaseWindow { DataContext = vm, Owner = Application.Current.MainWindow }.ShowDialog();
        if (vm.Saved is not { } file) return;
        if (c is { IsDefault: false }) CloseOneCase(c);
        OpenCase(file);
    }

    /// <summary>A new investigation case beside the ones open.</summary>
    [RelayCommand]
    void NewInvestigationCase()
    {
        var vm = new InvestigationCaseViewModel(new LexisCase("", "", []), null);
        new Views.InvestigationCaseWindow { DataContext = vm, Owner = Application.Current.MainWindow }.ShowDialog();
        if (vm.Saved is { } file) OpenCase(file);
    }

    /// <summary>Closes one investigation case, its devices and their tabs; the other cases stay open. NO CASE closes its
    /// devices and stays.</summary>
    public void CloseOneCase(OpenCase c)
    {
        CloseTabsOf(c);
        foreach (var d in c.Devices) Drop(d);
        c.Devices.Clear();
        if (!c.IsDefault) Cases.Remove(c);
        AfterDevicesChanged();
    }

    /// <summary>Closes the tabs of a case: its devices' and its views across them.</summary>
    void CloseTabsOf(OpenCase c, bool jointOnly = false)
    {
        foreach (var t in Tabs.Where(t => t.Session is null ? t.Case == c : !jointOnly && c.Devices.Any(d => d.Session == t.Session)).ToList())
        {
            if (SelectedTab == t) SelectedTab = null;
            Tabs.Remove(t);
            t.Content = null;
        }
    }

    /// <summary>A device leaves the reader: its tabs closed, its report closed.</summary>
    void Drop(Device d)
    {
        foreach (var t in Tabs.Where(t => t.Session == d.Session).ToList())
        {
            if (SelectedTab == t) SelectedTab = null;
            Tabs.Remove(t);
            t.Content = null;
        }
        foreach (var a in d.Session.Manifest.Artifacts) _owner.Remove(a);
        Devices.Remove(d);
        d.Session.Dispose();
    }

    void AfterDevicesChanged()
    {
        if (Cases.All(c => c.IsDefault && c.Devices.Count == 0)) { CloseCase(); return; } // nothing left but an empty NO CASE
        if (!Devices.Any(d => d.Session == Session)) Session = Devices.FirstOrDefault()?.Session;
        SelectedTab ??= Tabs.FirstOrDefault();
        Details.Clear();
        UpdatePackageWarning();
        OnPropertyChanged(nameof(WindowTitle));
        RebuildTree();
    }

    void UpdatePackageWarning()
    {
        var altered = Devices.Where(d => d.Session.Package is { Intact: false }).ToList();
        PackageWarning = altered.Count == 0 ? null
            : $"ALTERED PORTABLE CASE{(CaseMode ? " (" + string.Join(", ", altered.Select(d => d.Label)) + ")" : "")}: " +
              $"{altered.Sum(d => d.Session.Package!.Problems.Count)} problem(s) against SHA256SUMS.txt or its signature. Its content is not what was packaged: see the device's overview.";
    }

    /// <summary>A name for a device not taken by another device of the case.</summary>
    static string FreeLabel(IEnumerable<string> taken, string label)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(label)) return label;
        for (var i = 2; ; i++) if (!used.Contains($"{label} ({i})")) return $"{label} ({i})";
    }

    /// <summary>A report's name in NO CASE: its folder (the one holding _lava_data.lava for that file), or the file's name.</summary>
    static string LabelOf(string path)
    {
        path = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(path);
        return Directory.Exists(path) ? name
            : name.StartsWith("_lava_data", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? name
            : System.IO.Path.GetFileNameWithoutExtension(path);
    }

    static bool TrySave(params OpenCase[] cases)
    {
        try { foreach (var c in cases) c.Save(); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(e.Message, "Save investigation case", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Selects a device's node, which opens its overview.</summary>
    public void SelectDevice(Device d)
    {
        var hit = Walk(Nodes).FirstOrDefault(x => x.Node.Device == d);
        foreach (var p in hit.Path ?? []) p.IsExpanded = true;
        if (hit.Node is { } n) SelectedNode = n;
    }

    /// <summary>Renames a device within its case, saved in the case file.</summary>
    public void RenameDevice(Device d)
    {
        if (CaseOf(d.Session) is not { } c) return;
        var name = Views.InputWindow.Ask("Rename device", "Name of the device in this case:", d.Label)?.Trim();
        if (string.IsNullOrEmpty(name) || name == d.Label) return;
        if (c.Devices.Any(x => x != d && string.Equals(x.Label, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show($"Another device of this case is already called \"{name}\".", "Rename device", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var old = d.Label;
        d.Label = name;
        if (!TrySave(c)) { d.Label = old; return; }
        RebuildTree();
    }

    /// <summary>Removes a device from its case, saved in the case file, and closes it; the report itself is not touched.
    /// From NO CASE it only closes.</summary>
    public void RemoveDevice(Device d)
    {
        if (CaseOf(d.Session) is not { } c) return;
        if (!c.IsDefault && MessageBox.Show($"Remove \"{d.Label}\" from {c.Title}?\n\nThe case file is saved without it; the report itself is not touched.",
                "Remove device", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var i = c.Devices.IndexOf(d);
        c.Devices.Remove(d);
        if (!TrySave(c)) { c.Devices.Insert(i, d); return; }
        CloseTabsOf(c, jointOnly: true);
        Drop(d);
        AfterDevicesChanged();
        Status = $"\"{d.Label}\" removed from {c.Title}.";
    }

    /// <summary>Puts a device before <paramref name="before"/> in its case (null: last), saved in the case file.</summary>
    public void MoveDevice(Device d, Device? before)
    {
        if (CaseOf(d.Session) is not { } c || before == d || before is not null && !c.Devices.Contains(before)) return;
        var order = c.Devices.ToList();
        c.Devices.Remove(d);
        c.Devices.Insert(before is null ? c.Devices.Count : c.Devices.IndexOf(before), d);
        if (!TrySave(c)) { c.Devices.Clear(); c.Devices.AddRange(order); return; }
        RebuildTree();
    }

    /// <summary>Moves a device to another open case: both case files are saved, the views across the devices of both close.</summary>
    public void MoveDeviceToCase(Device d, OpenCase target)
    {
        if (CaseOf(d.Session) is not { } from || from == target) return;
        var (label, color) = (d.Label, d.Color);
        d.Label = FreeLabel(target.Devices.Select(x => x.Label), d.Label);
        if (target.Devices.Any(x => x.Color == d.Color))
            d.Color = LexisCase.Palette.FirstOrDefault(p => target.Devices.All(x => x.Color != p)) ?? d.Color;
        from.Devices.Remove(d);
        target.Devices.Add(d);
        if (!TrySave(target, from))
        {
            target.Devices.Remove(d);
            from.Devices.Add(d);
            (d.Label, d.Color) = (label, color);
            TrySave(target, from); // as they were
            return;
        }
        CloseTabsOf(from, jointOnly: true);
        CloseTabsOf(target, jointOnly: true);
        RebuildTree();
        Status = $"\"{d.Label}\" moved from {from.Title} to {target.Title}.";
    }

    /// <summary>Moves a device to a case file that is not open: added to it and saved, then that case is opened.</summary>
    public void MoveDeviceToCaseFile(Device d)
    {
        if (CaseOf(d.Session) is not { } from) return;
        var dlg = new OpenFileDialog { Title = "Investigation case to move the device to", Filter = "Investigation case (*.lexiscase)|*.lexiscase" };
        if (dlg.ShowDialog() != true) return;
        if (Cases.FirstOrDefault(c => !c.IsDefault && SamePath(c.File, dlg.FileName)) is { } open)
        {
            MoveDeviceToCase(d, open);
            return;
        }
        try
        {
            var to = LexisCase.Load(dlg.FileName);
            if (to.Devices.Any(x => SamePath(x.Path, d.Path)))
            {
                MessageBox.Show("That case already has this report.", "Move device", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var color = to.Devices.Any(x => x.Color == d.Color) ? LexisCase.Palette.FirstOrDefault(p => to.Devices.All(x => x.Color != p)) ?? d.Color : d.Color;
            new LexisCase(to.Name, to.Number, [.. to.Devices, new LexisDevice(d.Path, FreeLabel(to.Devices.Select(x => x.Label), d.Label), color)]).Save(dlg.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(e.Message, "Move device", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        from.Devices.Remove(d);
        if (!TrySave(from)) { from.Devices.Add(d); return; }
        CloseTabsOf(from, jointOnly: true);
        Drop(d);
        AfterDevicesChanged();
        OpenCase(dlg.FileName);
    }

    /// <summary>AI settings: model servers, the model for each job, the Claude CLI and the review themes.</summary>
    [RelayCommand]
    void OpenAiSettings()
    {
        new Views.AiSettingsWindow { DataContext = new AiSettingsViewModel(Settings.Current.Ai, this), Owner = Application.Current.MainWindow }.ShowDialog();
        AiState.Changed();
        UpdateMcp();
    }

    /// <summary>The AI window over a scope of one device, left open beside the case so cited records can be opened.
    /// With AI off it offers the settings first.</summary>
    public AiViewModel? OpenAi(CaseSession s, AiScope scope)
    {
        if (!AiOn()) return null;
        var vm = new AiViewModel(this, s, scope);
        new Views.AiWindow { DataContext = vm, Owner = Application.Current.MainWindow }.Show();
        return vm;
    }

    /// <summary>After an AI job: the open tables and chats show the new translations and file notes, the details theirs.</summary>
    public void RefreshAiViews()
    {
        foreach (var t in Tabs)
            _ = (t.Content switch { ArtifactViewModel a => a.Translation, ChatViewModel c => c.Translation, _ => null })?.LoadAsync();
        Details.RefreshAi();
    }

    bool AiOn()
    {
        if (Settings.Current.Ai.Enabled) return true;
        if (MessageBox.Show("AI is off. Open the AI settings to choose a model server and turn it on?", "AI",
                            MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return false;
        OpenAiSettings();
        return Settings.Current.Ai.Enabled;
    }

    /// <summary>Translates the records of a scope without the AI window: progress in the status bar, the translation
    /// kept in the case's AI base like any other, then shown beside the original in <paramref name="shown"/>.</summary>
    public async Task TranslateQuietly(CaseSession s, AiScope scope, TranslationView? shown)
    {
        if (!AiOn()) return;
        var vm = new AiViewModel(this, s, scope);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AiViewModel.Status) && vm.Status != "") Status = "AI translation · " + vm.Status; };
        Status = $"AI translation · {scope.Text}…";
        await vm.TranslateCommand.ExecuteAsync(null);
        if (shown is null) return;
        if (shown.Mode == TranslationView.Original) shown.Mode = TranslationView.Both; // loads them
        else await shown.LoadAsync();
    }

    /// <summary>AI over the current device as a whole: every message of its chats and every media file.</summary>
    [RelayCommand(CanExecute = nameof(HasCase))]
    async Task AnalyzeWithAi()
    {
        if (Session is not { } s) return;
        var media = await Task.Run(() => { using var con = s.OpenDb(); return new MediaResolver(s).All(con); });
        var chats = s.Manifest.Artifacts.Where(a => a.Conversation is not null).Select(a => new AiSelection(a)).ToList();
        var dev = CaseMode && DeviceOf(s) is { } d ? d.Label + ": " : "";
        OpenAi(s, new AiScope($"{dev}every message of {chats.Count} chat artifacts and {media.Count:N0} media files", chats, media));
    }

    /// <summary>Checks a portable case zip, read-only, without opening it.</summary>
    [RelayCommand]
    async Task VerifyPackage()
    {
        var dlg = new OpenFileDialog { Title = "Portable case to verify", Filter = "Portable case (*.zip)|*.zip" };
        if (dlg.ShowDialog() != true) return;
        Busy = true;
        Status = "Verifying " + dlg.FileName + " …";
        try
        {
            var check = await Task.Run(() => Core.PortableCase.Check(dlg.FileName));
            Status = check is null ? "Not a portable case (no SHA256SUMS.txt)." : check.Intact ? "Portable case intact." : "Portable case ALTERED.";
            MessageBox.Show(check is null ? "This zip has no SHA256SUMS.txt: it is not a portable case." : ExaminerKey.Describe(check), "Verify package",
                MessageBoxButton.OK, check is { Intact: true } ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Status = "Could not read the zip.";
            MessageBox.Show(e.Message, "Verify package", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    void CloseCase()
    {
        SelectedTab = null;
        PackageWarning = null;
        foreach (var t in Tabs) t.Content = null;
        Tabs.Clear();
        Nodes.Clear();
        Details.Clear();
        foreach (var d in Devices) d.Session.Dispose();
        Devices.Clear();
        Cases.Clear();
        _owner.Clear();
        Session = null;
        CasesChanged();
        DetailsViewModel.DeleteSourceCopies();
    }

    /// <summary>Rebuilds the case tree after a view setting changed (e.g. grouping by application).</summary>
    public void RebuildTree()
    {
        CasesChanged();
        if (!CaseMode) return;
        // the branches open stay open (devices are told by their report, not their name or place)
        var open = Walk(Nodes).ToDictionary(x => PlaceOf(x.Node, x.Path), x => x.Node.IsExpanded);
        BuildTree();
        var nodes = new Dictionary<string, NodeViewModel>();
        foreach (var (n, path) in Walk(Nodes))
        {
            if (open.TryGetValue(PlaceOf(n, path), out var e)) n.IsExpanded = e;
            nodes.TryAdd(KeyOf(n), n);
        }
        // open tabs follow their node into the new tree, so the tree still shows which tab is in front
        foreach (var t in Tabs) if (t.Node is { } old) t.Node = nodes.GetValueOrDefault(KeyOf(old));
        OnSelectedTabChanged(SelectedTab);
        Details.RefreshTagChoices();
    }

    /// <summary>Where a node is in the tree, the same across rebuilds: its device's report, its case and the titles above it.</summary>
    static string PlaceOf(NodeViewModel n, NodeViewModel[] path) =>
        $"{Dev(n.Session)}|{(n.Case is { } c ? Id(c) : 0)}|" + string.Join("/", path.Where(p => p.Kind is not (NodeKind.Device or NodeKind.Case))
            .Select(p => p.Title).Append(n.Kind is NodeKind.Device or NodeKind.Case ? n.Kind.ToString() : n.Title));

    int _tree; // bumped on each rebuild: counts still loading for an older tree are dropped
    readonly Dictionary<CaseSession, long> _timelineTotals = []; // per device, summed into its case's joint timeline

    /// <summary>Each case (NO CASE first), holding the views across its devices (search, joint timeline and map, co-presence,
    /// common contacts), then one node per device with its tree.</summary>
    void BuildTree()
    {
        _tree++;
        Nodes.Clear();
        _timelineTotals.Clear();
        foreach (var c in Cases)
        {
            var node = new NodeViewModel { Title = c.Title, Kind = NodeKind.Case, Case = c, Glyph = "", IsExpanded = true,
                                           CountText = $"{c.Devices.Count} device{(c.Devices.Count == 1 ? "" : "s")}" };
            Nodes.Add(node);
            if (c.Devices.Count == 0) continue; // nothing to analyze yet
            var shared = new NodeViewModel { Title = "Shared analysis", Kind = NodeKind.Group, Case = c, Glyph = "", IsExpanded = true };
            shared.Children.Add(c.SearchNode = new NodeViewModel { Title = "Search (all devices)", Kind = NodeKind.Search, Case = c, Glyph = "",
                                                                   CountText = c.SearchNode?.CountText ?? "" });
            shared.Children.Add(c.JointTimeline = new NodeViewModel { Title = "Joint Timeline", Kind = NodeKind.Timeline, Case = c, CountText = "…", Glyph = "" });
            shared.Children.Add(new NodeViewModel { Title = "Joint Map", Kind = NodeKind.Map, Case = c, Glyph = "" });
            if (c.Devices.Count > 1)
            {
                shared.Children.Add(new NodeViewModel { Title = "Co-presence", Kind = NodeKind.CoPresence, Case = c, Glyph = "" });
                shared.Children.Add(new NodeViewModel { Title = "Common Contacts", Kind = NodeKind.CommonContacts, Case = c, Glyph = "" });
            }
            node.Children.Add(shared);
            foreach (var d in c.Devices)
            {
                var dn = new NodeViewModel { Title = d.Label, Kind = NodeKind.Device, Device = d, Case = c, Glyph = "", Color = d.Color,
                                             IsExpanded = d == c.Devices[0], CountText = d.Session.Manifest.Artifacts.Sum(a => a.RecordCount).ToString("N0") };
                BuildDevice(d.Session, dn.Children); // the device node itself opens its overview
                dn.Session = d.Session;
                node.Children.Add(dn);
            }
        }
        RefreshTagCount(); // the device nodes are in the tree only now
    }

    void BuildDevice(CaseSession s, ObservableCollection<NodeViewModel> into)
    {
        var m = s.Manifest;
        if (s.Manifest.Artifacts.Any(a => a.Columns.Any(c => c.IsDateTime))) into.Add(new NodeViewModel { Title = "Timeline", Kind = NodeKind.Timeline, Glyph = "" });
        into.Add(new NodeViewModel { Title = "Statistics", Kind = NodeKind.Statistics, Glyph = "" });
        var geo = GeoExtractor.GeoArtifacts(m).Count();
        if (geo > 0) into.Add(new NodeViewModel { Title = "Map", Kind = NodeKind.Map, Glyph = "", CountText = $"{geo} sources" });
        into.Add(new NodeViewModel { Title = "Media Gallery", Kind = NodeKind.Gallery, Glyph = "" });
        into.Add(new NodeViewModel { Title = "Documents", Kind = NodeKind.Documents, Glyph = "" });
        var tagged = new NodeViewModel { Title = "Tagged Items", Kind = NodeKind.Tagged, Glyph = "" };
        into.Add(tagged);
        into.Add(new NodeViewModel { Title = "Search", Kind = NodeKind.Search, Glyph = "" });
        into.Add(new NodeViewModel { Title = "Indicators", Kind = NodeKind.Indicators, Glyph = "" });
        into.Add(new NodeViewModel { Title = "Extraction Files", Kind = NodeKind.Files, Glyph = "" });

        // Chats: app › local account › chat list. Counts and accounts are filled in by UpdateCountsAsync.
        var chatArtifacts = m.Artifacts.Where(a => a.Conversation is not null && (a.RecordCount > 0 || !m.Artifacts.Any(x => x.RecordCount > 0)))
            .OrderBy(a => a.Category, StringComparer.OrdinalIgnoreCase).ToList();
        if (chatArtifacts.Count > 0)
        {
            var chats = new NodeViewModel { Title = "Chats", Kind = NodeKind.Chats, Glyph = "", IsExpanded = true };
            foreach (var a in chatArtifacts)
                chats.Children.Add(new NodeViewModel { Title = chatArtifacts.Count(x => x.Category == a.Category) > 1 ? a.Name : a.Category,
                                                       Kind = NodeKind.ChatApp, Artifact = a, Glyph = "", CountText = "…" });
            into.Add(chats);
        }

        // Single-artifact apps are flattened
        // and artifacts without records go to one collapsed group at the end, so the tree stays short.
        var analyzed = new NodeViewModel { Title = "Analyzed Data", Kind = NodeKind.Group, Glyph = "", IsExpanded = true,
                                           CountText = m.Artifacts.Sum(a => a.RecordCount).ToString("N0") };
        var order = MacroCategories.All.ToList();
        // A manifest without record_count reads as all zero: then nothing is filed under "No data".
        var counted = m.Artifacts.Any(a => a.RecordCount > 0);
        var withData = m.Artifacts.Where(a => !counted || a.RecordCount > 0).ToList();
        // Optionally each app's data in one place (Data by application › WeChat), beside the macro categories, which
        // stay complete: Messages keeps WhatsApp next to SMS.
        var apps = Settings.Current.GroupByApp ? MacroCategories.Applications(withData) : [];
        if (apps.Count > 0)
        {
            var appsNode = new NodeViewModel { Title = "Data by application", Kind = NodeKind.Category, Glyph = "\uE71D",
                                               CountText = apps.Sum(x => x.Artifacts.Sum(a => a.RecordCount)).ToString("N0") };
            foreach (var (name, _, arts) in apps)
            {
                if (arts.Count == 1) { appsNode.Children.Add(ArtifactNode(arts[0], true)); continue; }
                var several = arts.Select(a => a.Category).Distinct().Count() > 1;
                var appNode = new NodeViewModel { Title = name, Kind = NodeKind.Category, Glyph = "\uE8B7",
                                                  CountText = arts.Sum(a => a.RecordCount).ToString("N0") };
                foreach (var a in arts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) appNode.Children.Add(ArtifactNode(a, several));
                appsNode.Children.Add(appNode);
            }
            analyzed.Children.Add(appsNode);
        }
        // Tree: group › data type (when the group has several) › app › artifact.
        foreach (var macro in withData.GroupBy(a => MacroCategories.Classify(a)).GroupBy(g => g.Key.Macro)
                     .OrderBy(g => order.IndexOf(g.Key)))
        {
            var node = new NodeViewModel { Title = macro.Key.Name, Kind = NodeKind.Category, Glyph = macro.Key.Glyph,
                                           CountText = macro.Sum(t => t.Sum(a => a.RecordCount)).ToString("N0") };
            foreach (var type in macro.OrderBy(t => t.Key.Type, StringComparer.OrdinalIgnoreCase))
            {
                var parent = node;
                if (macro.Count() > 1)
                {
                    parent = new NodeViewModel { Title = type.Key.Type, Kind = NodeKind.Category, Glyph = macro.Key.Glyph,
                                                 CountText = type.Sum(a => a.RecordCount).ToString("N0") };
                    node.Children.Add(parent);
                }
                foreach (var app in type.GroupBy(a => a.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (app.Count() == 1) { parent.Children.Add(ArtifactNode(app.First(), true)); continue; }
                    var appNode = new NodeViewModel { Title = app.Key, Kind = NodeKind.Category, Glyph = "",
                                                      CountText = app.Sum(a => a.RecordCount).ToString("N0") };
                    foreach (var a in app.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)) appNode.Children.Add(ArtifactNode(a, false));
                    parent.Children.Add(appNode);
                }
            }
            analyzed.Children.Add(node);
        }
        var empty = m.Artifacts.Where(a => counted && a.RecordCount == 0).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (empty.Count > 0)
        {
            var node = new NodeViewModel { Title = "No data", Kind = NodeKind.Category, Glyph = "", CountText = $"{empty.Count} artifacts" };
            foreach (var a in empty) node.Children.Add(ArtifactNode(a, true));
            analyzed.Children.Add(node);
        }
        into.Add(analyzed);
        foreach (var (n, _) in Walk(into)) n.Session = s;
        RefreshTagCount();
        _ = UpdateCountsAsync(s, [.. into]);
    }

    static NodeViewModel ArtifactNode(ArtifactInfo a, bool withApp)
    {
        // ALEAPP names usually start with the app ("WeChat - Messages"); prefix the app only when they do not.
        var title = withApp && !a.Name.StartsWith(a.Category, StringComparison.OrdinalIgnoreCase) ? $"{a.Category} › {a.Name}" : a.Name;
        return new NodeViewModel { Title = title, Kind = NodeKind.Artifact, Artifact = a,
            Glyph = a.Conversation is not null ? "" : "", CountText = a.RecordCount.ToString("N0") };
    }

    /// <summary>Every node below these, depth first, with the chain of ancestors that leads to it.</summary>
    static IEnumerable<(NodeViewModel Node, NodeViewModel[] Path)> Walk(IEnumerable<NodeViewModel> nodes, NodeViewModel[]? path = null)
    {
        path ??= [];
        foreach (var n in nodes)
        {
            yield return (n, path);
            foreach (var x in Walk(n.Children, [.. path, n])) yield return x;
        }
    }

    async Task UpdateCountsAsync(CaseSession s, IReadOnlyList<NodeViewModel> roots)
    {
        var tree = _tree;
        var chatNodes = Walk(roots).Select(x => x.Node).Where(n => n.Kind == NodeKind.ChatApp).ToList();
        var accounts = await Task.Run(() =>
        {
            using var con = s.OpenDb();
            return chatNodes.Select(n => ConversationBuilder.Accounts(con, n.Artifact!)).ToList();
        });
        if (tree != _tree) return;
        foreach (var (n, acc) in chatNodes.Zip(accounts))
        {
            n.CountText = $"{acc.Sum(x => x.Chats):N0} chats · {acc.Sum(x => x.Messages):N0}";
            if (ConversationBuilder.AccountOf(n.Artifact!) is null) continue; // the artifact records no account: no level
            foreach (var x in acc)
                n.Children.Add(new NodeViewModel { Session = s, Kind = NodeKind.ChatAccount, Artifact = n.Artifact, Account = x.Account, Glyph = "",
                    Title = ConversationBuilder.AccountTitle(x.Account, x.Name), CountText = $"{x.Chats:N0} chats · {x.Messages:N0}" });
        }
        var (timeline, media) = await Task.Run(() =>
        {
            long t = Timeline.Query(s, 0, 1).Total;
            using var con = s.OpenDb();
            return (t, new MediaResolver(s).All(con, int.MaxValue).Count);
        });
        if (tree != _tree) return;
        foreach (var n in roots)
        {
            if (n.Kind == NodeKind.Timeline) n.CountText = timeline.ToString("N0");
            if (n.Kind == NodeKind.Gallery) n.CountText = media.ToString("N0");
        }
        _timelineTotals[s] = timeline;
        if (CaseOf(s) is { JointTimeline: { } joint } c && c.Devices.All(d => _timelineTotals.ContainsKey(d.Session)))
            joint.CountText = c.Devices.Sum(d => _timelineTotals[d.Session]).ToString("N0");
        // Chromium artifacts hold every Chromium browser of the device: one child per browser, opening the table filtered.
        var browserNodes = Walk(roots).Select(x => x.Node).Where(n => n.Kind == NodeKind.Artifact && n.Filter is null
            && n.Artifact!.Column(BrowserColumn) is not null).ToList();
        Dictionary<ArtifactInfo, IReadOnlyList<(string Value, long Count)>> browsers;
        try
        {
            browsers = await Task.Run(() =>
            {
                using var con = s.OpenDb();
                return browserNodes.Select(n => n.Artifact!).Distinct()
                    .ToDictionary(a => a, a => ArtifactQuery.ValueCounts(con, a, BrowserColumn));
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return; } // the tree stays without browser children
        if (tree != _tree) return;
        foreach (var n in browserNodes)
        {
            if (browsers[n.Artifact!] is not { Count: > 1 } values) continue; // one browser: the table itself is its list
            var col = n.Artifact!.Column(BrowserColumn)!.Sql;
            foreach (var (value, count) in values)
                n.Children.Add(new NodeViewModel { Session = s, Kind = NodeKind.Artifact, Artifact = n.Artifact, Glyph = "",
                    Title = value == "" ? "(no browser recorded)" : MacroCategories.BrowserLabel(value), CountText = count.ToString("N0"),
                    Filter = value == "" ? new ColumnFilter(col, "empty") : new ColumnFilter(col, "equals", value) });
        }
    }

    /// <summary>The Tagged Items node of each device shows that device's tags.</summary>
    public void RefreshTagCount()
    {
        foreach (var (n, _) in Walk(Nodes))
            if (n.Kind == NodeKind.Tagged && n.Session is { } s) n.CountText = s.Tags.All().Count.ToString("N0");
    }

    /// <summary>Tags (or with null untags) records of any artifacts, keeping their notes; each record is tagged on the
    /// device its artifact belongs to.</summary>
    /// <summary>Tags the records; with <paramref name="toggle"/> (menus, shortcuts) records that all hold that tag lose it.
    /// Returns false when it removed the tag.</summary>
    public bool SetTag(IEnumerable<(ArtifactInfo Artifact, long RowId)> records, string? tag, bool toggle = false)
    {
        var groups = records.GroupBy(r => r.Artifact, ReferenceEqualityComparer.Instance)
            .Select(g => ((ArtifactInfo)g.Key!, g.Select(r => r.RowId).Distinct().ToList())).ToList();
        if (toggle && tag is not null && groups.All(x => SessionOf(x.Item1) is { } s &&
                s.Tags.ForTable(x.Item1.TableName) is var cur && x.Item2.All(id => cur.GetValueOrDefault(id)?.Tag == tag)))
            tag = null;
        foreach (var (a, ids) in groups)
        {
            if (SessionOf(a) is not { } s) continue;
            s.Tags.SetMany(a.TableName, ids, tag);
            OnTagChanged(a, ids, tag);
        }
        return tag is not null;
    }

    /// <summary>Called after any tag change so every view showing that row stays in step.</summary>
    public void OnTagChanged(ArtifactInfo artifact, IReadOnlyCollection<long> rowIds, string? tag)
    {
        var s = SessionOf(artifact);
        RefreshTagCount();
        Details.SyncTag(s, artifact.TableName, rowIds, tag);
        foreach (var t in Tabs)
        {
            if (t.Content is ArtifactViewModel av && av.Artifact.TableName == artifact.TableName && SessionOf(av.Artifact) == s) av.UpdateTag(rowIds, tag);
            if (t.Content is ChatViewModel cv && cv.Artifact.TableName == artifact.TableName && SessionOf(cv.Artifact) == s) cv.UpdateTag(rowIds, tag);
            if (t.Content is TaggedViewModel tv) tv.Refresh();
        }
    }

    public void NavigateTo(ArtifactInfo artifact, string? filter = null, long? selectRowId = null)
    {
        if (SessionOf(artifact) is not { } s) return;
        var hit = Walk(Nodes).FirstOrDefault(x => x.Node.Artifact == artifact && x.Node.Kind == NodeKind.Artifact && x.Node.Filter is null);
        foreach (var parent in hit.Path ?? []) parent.IsExpanded = true;
        Show(hit.Node is { } n ? KeyOf(n) : $"{Dev(s)}|{NodeKind.Artifact}|{artifact.TableName}||", hit.Node?.Title ?? artifact.Name,
             hit.Node?.Glyph ?? "", () => new ArtifactViewModel(this, s, artifact, filter, selectRowId), hit.Node, replace: true, session: s);
    }

    /// <summary>An artifact's table with a column filter set (e.g. one album of the gallery).</summary>
    public void OpenFiltered(ArtifactInfo artifact, ColumnFilter filter, string title)
    {
        if (SessionOf(artifact) is not { } s) return;
        Show($"{Dev(s)}|filtered|{artifact.TableName}|{filter.Sql}|{filter.Value}", title, "", () => new ArtifactViewModel(this, s, artifact, columnFilter: filter), session: s);
    }

    /// <summary>Opens the Extraction Files view on a recorded source file.</summary>
    public void ShowInFiles(string recorded, CaseSession? session = null)
    {
        if ((session ?? Session) is not { } s) return;
        var node = Walk(Nodes).Select(x => x.Node).FirstOrDefault(n => n.Kind == NodeKind.Files && n.Session == s);
        Show(node is { } n ? KeyOf(n) : $"files|{Dev(s)}", node?.Title ?? "Extraction Files", node?.Glyph ?? "", () => new FilesViewModel(this, s), node, session: s);
        if (SelectedTab?.Content is FilesViewModel f) f.Reveal(recorded);
    }

    /// <summary>The artifact's positions on the map; with <paramref name="focus"/> centred and zoomed on that one.</summary>
    public void NavigateToMap(ArtifactInfo artifact, GeoPoint? focus = null)
    {
        if (SessionOf(artifact) is not { } s) return;
        Show($"map|{Dev(s)}|{artifact.TableName}", "Map · " + artifact.Name, "", () => new MapViewModel(this, s, artifact, focus), replace: true, session: s);
    }

    /// <summary>Opens the Statistics view of a device.</summary>
    public void ShowStatistics(CaseSession s)
    {
        var node = Walk(Nodes).Select(x => x.Node).FirstOrDefault(n => n.Kind == NodeKind.Statistics && (n.Session ?? Session) == s);
        Show(node is { } n ? KeyOf(n) : $"stats|{Dev(s)}", "Statistics", node?.Glyph ?? "", () => new StatisticsViewModel(this, s), node, session: s);
    }

    /// <summary>The timeline of a device on one day (in the display time zone).</summary>
    public void ShowTimelineDay(CaseSession s, DateOnly day)
    {
        var d = day.ToDateTime(TimeOnly.MinValue);
        Show($"timeline-day|{Dev(s)}", "Timeline · " + day.ToString("yyyy-MM-dd"), "", () => new TimelineViewModel(this, [DeviceOf(s)!]) { From = d, To = d },
             replace: true, session: s);
    }

    /// <summary>A zone of interest on the map of its devices (the joint map for several), centred and zoomed on it.</summary>
    public void ShowZoneOnMap(IReadOnlyList<Device> devices, GeoPoint focus)
    {
        var s = devices[0].Session;
        var c = CaseOf(s);
        Show(devices.Count > 1 ? $"zonemap|case{(c is null ? 0 : Id(c))}" : $"zonemap|{Dev(s)}", "Map · Zone", "", () => devices.Count > 1 ? new MapViewModel(this, devices, focus) : new MapViewModel(this, s, null, focus),
             replace: true, session: devices.Count > 1 ? null : s, @case: c);
    }

    /// <summary>The co-presences found on a map of their own (one per case), opened on <paramref name="focus"/>.</summary>
    public void ShowCoPresenceMap(IReadOnlyList<Device> devices, IReadOnlyList<CoPresenceGroup> groups, double radius, GeoPoint? focus)
    {
        var c = CaseOf(devices[0].Session);
        Show($"copresmap|case{(c is null ? 0 : Id(c))}", "Map · Co-Presence", "", () => new MapViewModel(this, devices, groups, radius, focus),
             replace: true, @case: c);
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    void Search()
    {
        // a search of one device in front searches that device again
        if (!string.IsNullOrWhiteSpace(SearchText)) SearchFor(SearchText, (SelectedTab?.Content as SearchViewModel)?.Devices);
    }

    /// <summary>Runs a search in a tab of its own (the same query again brings its tab forward and runs it anew).</summary>
    public void SearchFor(string query, IReadOnlyList<Device>? devices = null)
    {
        if (Session is null) return;
        query = query.Trim();
        SearchText = query;
        // across the devices of the case of the device in front, or the one device of a device's Search
        if (CaseOf(Session) is not { } c) return;
        var one = devices is { Count: 1 } && c.Devices.Count > 1 ? devices[0] : null;
        // the categories of the node selected in the tree (a category or a group of them), else all
        var categories = SelectedNode is { Kind: NodeKind.Category or NodeKind.Group } n && n.Artifacts().Any()
            ? n.Artifacts().Select(a => a.Category).ToHashSet() : null;
        var search = new SearchViewModel(this, one is null ? c.Devices : [one], query, categories);
        var node = one is null ? c.SearchNode : SearchNodeOf(one.Session);
        if (one is null) c.LastSearch = search;
        else if (node is not null) node.Kept = search;
        if (node is not null) node.CountText = "…";
        var text = query.Length > 30 ? query[..30] + "…" : query;
        Show($"search|{Id(c)}|{(one is null ? "" : Dev(one.Session))}|{query}", "Search · " + (one is null ? text : $"{one.Label} · {text}"),
             node?.Glyph ?? "", () => search, replace: true, @case: c);
    }

    NodeViewModel? SearchNodeOf(CaseSession s) => Walk(Nodes).Select(x => x.Node).FirstOrDefault(n => n.Kind == NodeKind.Search && n.Session == s);

    /// <summary>Reloads every open view (after a change in what searches look at).</summary>
    public void RefreshViews()
    {
        foreach (var t in Tabs) if (t.Content is IRefreshable r) r.Refresh();
    }

    public void SetSearchCount(SearchViewModel search, int artifacts, long hits)
    {
        if ((Cases.FirstOrDefault(c => c.LastSearch == search)?.SearchNode ??
             Walk(Nodes).Select(x => x.Node).FirstOrDefault(n => n.Kind == NodeKind.Search && n.Kept == search)) is { } node)
            node.CountText = $"{hits:N0}";
        Status = $"Search: {hits:N0} hits in {artifacts} artifacts";
    }

    /// <summary>Runs ALEAPP or iLEAPP and opens the report in the case chosen there: the case in front by default, an
    /// open case, a new one (made with the case number typed for the run), or NO CASE.</summary>
    [RelayCommand]
    async Task NewExtraction()
    {
        var choices = new List<CaseChoice> { new(ViewModels.OpenCase.NoCase + " (the report on its own)", null) };
        choices.AddRange(Cases.Where(c => !c.IsDefault).Select(c => new CaseChoice(c.Title, c)));
        choices.Add(new("New case…", null, IsNew: true));
        var front = SelectedNode?.Case ?? CaseOf(Session);
        var vm = new ExtractionViewModel { CaseChoices = choices, TargetCase = choices.FirstOrDefault(c => c.Case is not null && c.Case == front) ?? choices[0] };
        var win = new Views.ExtractionWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        win.ShowDialog();
        if (vm.ResultManifest is not { } report) return;
        await OpenCaseAsync(report);
        if (Devices.FirstOrDefault(d => d.Path != "" && SamePath(d.Path, report)) is not { } device) return;
        if (vm.TargetCase is { IsNew: true })
        {
            var cvm = new InvestigationCaseViewModel(new LexisCase("", vm.CaseNumber.Trim(), [new LexisDevice(device.Path, device.Label, device.Color)]), null);
            new Views.InvestigationCaseWindow { DataContext = cvm, Owner = Application.Current.MainWindow }.ShowDialog();
            if (cvm.Saved is { } file) await OpenCaseAsync(file); // the case takes the report out of NO CASE
        }
        else if (vm.TargetCase?.Case is { } target && Cases.Contains(target)) MoveDeviceToCase(device, target);
    }

    [RelayCommand(CanExecute = nameof(HasCase))]
    void PortableCase()
    {
        // The device selected in the tree (its node or anything under it); from a joint view, the one in front.
        if ((SelectedNode?.Session ?? Session) is not { } s) return;
        if (CaseMode) Status = $"Portable case of the selected device: {DeviceOf(s)?.Label}";
        var vm = new PortableCaseViewModel(s);
        new Views.PortableCaseWindow { DataContext = vm, Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    static void ShowExports() => ExportLogWindow.ShowLog();

    [RelayCommand]
    void About() => AboutWindow.Show("About LEXIS",
        "LEXIS — LEAPP Reader\n\nA reader for the reports of ALEAPP (Android) and iLEAPP (iOS), which it also carries " +
        "and runs to analyze new extractions.\n" +
        "Evidence databases are opened read-only. Tags and notes are stored in _reader_tags.db beside the case.\n\n" +
        "Built on the LEAPP project by Alexis Brignoni and its contributors (MIT license):\n" +
        "ALEAPP: https://github.com/abrignoni/ALEAPP\niLEAPP: https://github.com/abrignoni/iLEAPP\n\n" +
        "Projects studied for further features (ideas reimplemented; code is ported only where its license allows, with attribution):\n" +
        "MVT, Mobile Verification Toolkit (STIX2 indicator format and matching rules; MVT License 1.1): https://github.com/mvt-project/mvt\n" +
        "Indicator feeds: https://github.com/mvt-project/mvt-indicators\n" +
        "FFS-Explorer: https://github.com/KlasScripts/FFS-Explorer\n" +
        "SignalToALEAPP (MIT): https://github.com/AndroidIosForensik/SignalToALEAPP\n" +
        "ThreemaToALEAPP (MIT): https://github.com/AndroidIosForensik/ThreemaToALEAPP\n" +
        "peach-forensics (Apache-2.0): https://github.com/kalink0/peach-forensics\n" +
        "android-crypto-wallet-forensics: https://github.com/GabrielNeves24/android-crypto-wallet-forensics\n" +
        "verichron-epoch: https://github.com/vermiliontides/verichron-epoch\n\n" +
        "Data and formats: BIP-39 English wordlist (embedded for seed-phrase detection): https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt\n" +
        "Android backup (.ab) format, from AOSP platform_frameworks_base (Apache-2.0): https://android.googlesource.com/platform/frameworks/base\n\n" +
        new System.IO.StreamReader(typeof(MainViewModel).Assembly.GetManifestResourceStream("NOTICE")!).ReadToEnd());
}

public interface IRefreshable { void Refresh(); }
