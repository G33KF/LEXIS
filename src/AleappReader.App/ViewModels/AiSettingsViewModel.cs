using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AleappReader.Core;

namespace AleappReader.App.ViewModels;

public partial class AiProviderRow : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WhereText), nameof(IsLocal))] private string name = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WhereText), nameof(IsLocal))] private AiKind kind = AiKind.OpenAi;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WhereText), nameof(IsLocal))] private string address = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(KeyText))] private string? protectedKey;
    /// <summary>A key typed in this session, saved encrypted; null leaves the stored one.</summary>
    public string? NewKey { get; set; }
    public ObservableCollection<AiModel> Models { get; } = [];

    AiProvider Provider => new(Name, Kind, Address);
    public string WhereText => Provider.WhereText;
    public bool IsLocal => Provider.IsLocal;
    public bool IsRemote => Provider.Where == "remote";
    public string KeyText => string.IsNullOrEmpty(ProtectedKey) ? "no key" : "key stored, encrypted";

    public AiProviderSetting ToSetting()
    {
        var s = new AiProviderSetting { Name = Name.Trim(), Kind = Kind, Address = Address.Trim(), ProtectedKey = ProtectedKey };
        if (NewKey is not null) s.SetKey(NewKey.Trim());
        return s;
    }

    [RelayCommand]
    void ForgetKey() { ProtectedKey = null; NewKey = null; }

    /// <summary>A detected model chosen for a job: 0 text, 1 vision, 2 transcription.</summary>
    public event Action<AiProviderRow, AiModel, int>? Use;
    [RelayCommand] void UseForText(AiModel m) => Use?.Invoke(this, m, 0);
    [RelayCommand] void UseForVision(AiModel m) => Use?.Invoke(this, m, 1);
    [RelayCommand] void UseForTranscription(AiModel m) => Use?.Invoke(this, m, 2);

    /// <summary>Old and new name, so the roles that use this provider follow a rename.</summary>
    public event Action<string, string>? Renamed;
    partial void OnNameChanged(string? oldValue, string newValue) => Renamed?.Invoke(oldValue ?? "", newValue);

    /// <summary>The address was changed (committed when the box loses focus): its models are listed again.</summary>
    public event Action<AiProviderRow>? AddressChanged;
    partial void OnAddressChanged(string value) => AddressChanged?.Invoke(this);
}

public sealed record AiKindChoice(AiKind Kind, string Label);

/// <summary>A job (text, vision, transcription) and the provider and model that does it.</summary>
public partial class AiRoleRow(string title, ObservableCollection<AiProviderRow> providers) : ObservableObject
{
    public string Title { get; } = title;
    public bool IsTranscription { get; init; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Models))] private string provider = "";
    [ObservableProperty] private string model = "";
    [ObservableProperty] private string status = "";
    public IEnumerable<AiModel> Models => providers.FirstOrDefault(p => p.Name == Provider)?.Models ?? [];
    public void RefreshModels() => OnPropertyChanged(nameof(Models));
    public AiRole ToRole() => new() { Provider = Provider, Model = Model.Trim() };
}

public partial class AiThemeRow : ObservableObject
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string indicators = "";
}

/// <summary>AI settings: on or off, the model servers (keys kept encrypted), the model for each job, the Claude CLI,
/// and the prompts of the thematic review.</summary>
public partial class AiSettingsViewModel : ObservableObject
{
    public const string OllamaName = "Ollama", LmStudioName = "LM Studio";
    public AiKindChoice[] Kinds { get; } = [new(AiKind.OpenAi, "OpenAI-compatible"), new(AiKind.Ollama, "Ollama API")];
    /// <summary>Every server: the two local engines and the other endpoints.</summary>
    public ObservableCollection<AiProviderRow> Providers { get; } = [];
    /// <summary>The local engines to choose from: Ollama and LM Studio, each keeping its own address.</summary>
    public ObservableCollection<AiProviderRow> Engines { get; } = [];
    public ObservableCollection<AiProviderRow> Endpoints { get; } = [];
    public ObservableCollection<AiRoleRow> Roles { get; } = [];
    public ObservableCollection<AiThemeRow> Themes { get; } = [];
    /// <summary>The servers a job can use: the local engine chosen and the other endpoints.</summary>
    public IEnumerable<string> ProviderNames => Endpoints.Select(p => p.Name).Prepend(Engine?.Name ?? "")
        .Append(ClaudeAllowed ? AiSettings.ClaudeName : "").Append(WhisperPath.Trim() != "" ? AiSettings.WhisperName : "").Where(n => n != "");

    /// <summary>whisper.cpp on this machine, for transcription: listed with the models beside it, never stored among the servers.</summary>
    readonly AiProviderRow _whisper = new() { Name = AiSettings.WhisperName, Kind = AiKind.WhisperCpp };
    public AiProviderRow WhisperRow => _whisper;
    [ObservableProperty] private string whisperPath = "";

    partial void OnWhisperPathChanged(string value)
    {
        _whisper.Address = value.Trim();
        if (value.Trim() != "") _ = Detect(_whisper);
        OnPropertyChanged(nameof(ProviderNames));
    }

    [RelayCommand]
    void BrowseWhisper()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = "whisper.cpp program", Filter = "whisper-cli.exe|whisper-cli.exe;main.exe|Programs (*.exe)|*.exe" };
        if (d.ShowDialog() != true) return;
        WhisperPath = d.FileName;
        if (Roles[2].Provider is "" or OllamaName or LmStudioName) (Roles[2].Provider, Roles[2].Model) = (AiSettings.WhisperName, "");
    }

    [RelayCommand]
    void OpenWhisperDownloads(string? which) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(which == "models" ? WhisperCpp.ModelDownloads : WhisperCpp.Downloads) { UseShellExecute = true });

    /// <summary>The Claude CLI as a server the jobs can use while it is allowed: listed with its models, never stored
    /// among the servers (its address is the executable).</summary>
    readonly AiProviderRow _claude = new() { Name = AiSettings.ClaudeName, Kind = AiKind.ClaudeCli };
    public AiProviderRow ClaudeRow => _claude;

    partial void OnClaudeAllowedChanged(bool value)
    {
        if (value) _ = Detect(_claude);
        else foreach (var r in Roles.Where(r => r.Provider == AiSettings.ClaudeName)) (r.Provider, r.Model) = (Engine?.Name ?? "", "");
        OnPropertyChanged(nameof(ProviderNames));
    }

    partial void OnClaudePathChanged(string value) => _claude.Address = value.Trim();
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private AiProviderRow? engine;
    [ObservableProperty] private AiProviderRow? selectedProvider;

    // the jobs on the engine left move to the one chosen; its models are listed at once
    partial void OnEngineChanged(AiProviderRow? oldValue, AiProviderRow? newValue)
    {
        if (newValue is null) return;
        foreach (var r in Roles.Where(r => r.Provider == oldValue?.Name || r.Provider == "")) (r.Provider, r.Model) = (newValue.Name, "");
        OnPropertyChanged(nameof(ProviderNames));
        _ = Detect(newValue);
    }
    [ObservableProperty] private AiThemeRow? selectedTheme;
    [ObservableProperty] private string claudePath;
    [ObservableProperty] private bool claudeAllowed;
    [ObservableProperty] private bool explainWebLookup;
    [ObservableProperty] private string webSearchUrl = WebLookup.DefaultUrl;
    [ObservableProperty] private bool mcpAllowed;
    [ObservableProperty] private string claudeStatus = "";
    [ObservableProperty] private string translateTo;
    [ObservableProperty] private string answerLanguage;
    [ObservableProperty] private string visionCategories;
    [ObservableProperty] private int maxRecordsPerRequest;
    [ObservableProperty] private string message = "";
    public event Action? Done;

    readonly MainViewModel? _main;
    [ObservableProperty] private string mcpState = "";
    public string McpUrl => Settings.Current.Ai.McpUrl;
    /// <summary>What a client that only starts servers itself (on stdio) runs: LEXIS as a bridge to the one open.</summary>
    public string McpBridge => $"\"{Environment.ProcessPath}\" --mcp-bridge";
    public bool HasLmStudio => McpClients.Installed(McpClients.LmStudio);
    public bool HasClaudeDesktop => McpClients.Installed(McpClients.ClaudeDesktop);

    // the switch acts at once, like the server it starts: no Save needed for another program to connect
    partial void OnMcpAllowedChanged(bool value)
    {
        Settings.Current.Ai.McpAllowed = value;
        Settings.Current.Save(ai: true);
        _main?.UpdateMcp();
        McpState = _main?.McpState ?? "";
        OnPropertyChanged(nameof(McpUrl));
    }

    [RelayCommand] void AddToLmStudio() => AddTo("LM Studio", McpClients.LmStudio, McpClients.UrlEntry(McpUrl),
        "In LM Studio the lexis tools appear under the chat's integrations (restart LM Studio if they do not).");

    [RelayCommand] void AddToClaudeDesktop() => AddTo("Claude Desktop", McpClients.ClaudeDesktop, McpClients.BridgeEntry(Environment.ProcessPath ?? "LEXIS.exe"),
        "Restart Claude Desktop to load it. Data from the case goes to Anthropic's servers when Claude Desktop reads it.");

    void AddTo(string client, string file, System.Text.Json.Nodes.JsonObject entry, string next)
    {
        if (!McpAllowed) McpAllowed = true;
        if (MessageBox.Show($"Add LEXIS as the \"lexis\" MCP server in {client}'s configuration?\n\n{file}\n\nOther settings in it stay as they are; " +
                            "the file as it was is kept beside it (.lexis-backup).", "MCP", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var backup = McpClients.Add(file, entry);
            Message = $"Added to {client}{(backup is null ? "" : " (backup: " + backup + ")")}. {next}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Message = $"{client}'s configuration was not changed: {e.Message}";
        }
    }

    [RelayCommand]
    void CopyMcpConfig()
    {
        if (!McpAllowed) McpAllowed = true;
        Clipboard.SetText(McpServer.Config(Environment.ProcessPath ?? "LEXIS.exe", ["--mcp-bridge"], true));
        Message = "mcp.json copied, for a client that starts the server itself: it connects to this LEXIS. A client that takes a URL uses the address above.";
    }

    public AiSettingsViewModel(AiSettings s, MainViewModel? main = null)
    {
        _main = main;
        mcpState = main?.McpState ?? "";
        enabled = s.Enabled;
        foreach (var p in s.Providers) Add(new AiProviderRow { Name = p.Name, Kind = p.Kind, Address = p.Address, ProtectedKey = p.ProtectedKey });
        if (Engines.All(e => e.Name != OllamaName)) Add(new AiProviderRow { Name = OllamaName, Kind = AiKind.Ollama, Address = "http://localhost:11434" });
        if (Engines.All(e => e.Name != LmStudioName)) Add(new AiProviderRow { Name = LmStudioName, Kind = AiKind.OpenAi, Address = "http://localhost:1234/v1" });
        engine = Engines.FirstOrDefault(e => e.Name == s.LocalEngine) ?? Engines.FirstOrDefault(e => e.Name == s.Text.Provider) ?? Engines[0];
        Roles.Add(new AiRoleRow("Text (summary, questions, translation, thematic review)", Providers) { Provider = s.Text.Provider, Model = s.Text.Model });
        Roles.Add(new AiRoleRow("Vision (image classification)", Providers) { Provider = s.Vision.Provider, Model = s.Vision.Model });
        Roles.Add(new AiRoleRow("Transcription (audio)", Providers) { Provider = s.Transcription.Provider, Model = s.Transcription.Model, IsTranscription = true });
        foreach (var t in s.Themes) Themes.Add(new AiThemeRow { Name = t.Name, Indicators = t.Indicators });
        (claudePath, claudeAllowed, mcpAllowed, translateTo, answerLanguage, visionCategories, maxRecordsPerRequest) =
            (s.ClaudePath, s.ClaudeAllowed, s.McpAllowed, s.TranslateTo, s.AnswerLanguage, s.VisionCategories, s.MaxRecordsPerRequest);
        (explainWebLookup, webSearchUrl) = (s.ExplainWebLookup, s.WebSearchUrl);
        _claude.Address = claudePath;
        Providers.Add(_claude); // for the roles' model lists only: Engines and Endpoints do not hold it, Save leaves it out
        if (claudeAllowed) _ = Detect(_claude);
        Providers.Add(_whisper); // like the Claude CLI: for the transcription models, not saved as a server
        WhisperPath = s.WhisperPath;
        SelectedTheme = Themes.FirstOrDefault();
    }

    [RelayCommand]
    void AddProvider()
    {
        var row = new AiProviderRow { Name = $"Endpoint {Endpoints.Count + 1}", Kind = AiKind.OpenAi, Address = "https://" };
        Add(row);
        SelectedProvider = row;
        OnPropertyChanged(nameof(ProviderNames));
    }

    void Add(AiProviderRow row)
    {
        Providers.Add(row);
        (row.Name is OllamaName or LmStudioName ? Engines : Endpoints).Add(row);
        row.Renamed += (old, now) =>
        {
            foreach (var r in Roles.Where(r => r.Provider == old)) r.Provider = now;
            OnPropertyChanged(nameof(ProviderNames));
        };
        row.Use += (p, m, job) => (Roles[job].Provider, Roles[job].Model, Roles[job].Status) = (p.Name, m.Id, "");
        row.AddressChanged += p => _ = Detect(p);
    }

    /// <summary>Lists the models of the local engine and of the endpoints on this machine or the network when the window
    /// opens; a remote one when its address is set or on Connect (nothing of a case is sent either way).</summary>
    public async Task DetectLocal()
    {
        foreach (var p in Endpoints.Where(p => !p.IsRemote).Prepend(Engine).OfType<AiProviderRow>().ToList())
            await Detect(p);
    }

    [RelayCommand]
    void RemoveProvider(AiProviderRow? row)
    {
        if ((row ?? SelectedProvider) is not { } p || !Endpoints.Contains(p)) return;
        Providers.Remove(p);
        Endpoints.Remove(p);
        foreach (var r in Roles.Where(r => r.Provider == p.Name)) (r.Provider, r.Model) = ("", "");
        OnPropertyChanged(nameof(ProviderNames));
    }

    /// <summary>Connects and lists the server's models: the test that the address (and key) work.</summary>
    [RelayCommand]
    async Task Detect(AiProviderRow? row)
    {
        if (row is null) return;
        row.Status = "Connecting…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var models = await row.ToSetting().Client().Models(cts.Token);
            row.Models.Clear();
            foreach (var m in models) row.Models.Add(m);
            row.Status = models.Count == 0 ? "Connected, no models" : $"Connected · {models.Count} models" +
                (row.Kind == AiKind.Ollama ? $", {models.Count(m => m.Vision == true)} with vision" : row.Kind == AiKind.WhisperCpp ? " for transcription" : " (vision not reported by this server)");
            foreach (var r in Roles) r.RefreshModels();
            OnPropertyChanged(nameof(ProviderNames));
        }
        catch (Exception e) when (e is AiException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            row.Status = "Failed: " + (e is OperationCanceledException ? "no answer in 30 s" : e.Message);
        }
    }

    [RelayCommand]
    void CheckClaude() =>
        ClaudeStatus = ClaudeCli.Resolve(ClaudePath.Trim()) is { } found ? "Found: " + found : "Not found. Give the full path of claude.exe or claude.cmd.";

    [RelayCommand]
    void AddTheme()
    {
        var t = new AiThemeRow { Name = "New theme", Indicators = "- " };
        Themes.Add(t);
        SelectedTheme = t;
    }

    [RelayCommand]
    void RemoveTheme()
    {
        if (SelectedTheme is { } t) Themes.Remove(t);
        SelectedTheme = Themes.FirstOrDefault();
    }

    [RelayCommand]
    void Save()
    {
        var servers = Providers.Where(p => p != _claude && p != _whisper).ToList();
        var names = servers.Select(p => p.Name.Trim()).ToList();
        if (names.Any(n => n == "")) { Message = "Every provider needs a name."; return; }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count || names.Contains(AiSettings.ClaudeName, StringComparer.OrdinalIgnoreCase) ||
            names.Contains(AiSettings.WhisperName, StringComparer.OrdinalIgnoreCase))
        { Message = "Two servers have the same name (Ollama and LM Studio are the local engines, Claude CLI is the CLI)."; return; }
        if (servers.FirstOrDefault(p => !Uri.TryCreate(p.Address.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) is { } bad)
        { Message = $"{bad.Name}: the address must start with http:// or https://"; return; }
        if (!WebSearchUrl.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !WebSearchUrl.Contains("{q}"))
        { Message = "Web search address: https://, with {q} where the search words go."; return; }
        var ai = Settings.Current.Ai;
        ai.Enabled = Enabled;
        ai.LocalEngine = Engine?.Name ?? "";
        ai.Providers = [.. servers.Select(p => p.ToSetting())];
        (ai.Text, ai.Vision, ai.Transcription) = (Roles[0].ToRole(), Roles[1].ToRole(), Roles[2].ToRole());
        ai.ClaudePath = ClaudePath.Trim() is { Length: > 0 } c ? c : "claude";
        ai.ClaudeAllowed = ClaudeAllowed;
        ai.WhisperPath = WhisperPath.Trim();
        (ai.ExplainWebLookup, ai.WebSearchUrl) = (ExplainWebLookup, WebSearchUrl.Trim());
        ai.McpAllowed = McpAllowed;
        ai.TranslateTo = TranslateTo.Trim() is { Length: > 0 } l ? l : "English";
        ai.AnswerLanguage = AnswerLanguage.Trim() is { Length: > 0 } al ? al : "English";
        ai.MaxRecordsPerRequest = Math.Clamp(MaxRecordsPerRequest, 1, 1000);
        ai.VisionCategories = VisionCategories.Trim();
        ai.Themes = [.. Themes.Where(t => t.Name.Trim() != "").Select(t => new AiTheme { Name = t.Name.Trim(), Indicators = t.Indicators.Trim() })];
        Settings.Current.Save(ai: true);
        Done?.Invoke();
    }
}
