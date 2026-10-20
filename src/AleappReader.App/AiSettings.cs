using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AleappReader.Core;

namespace AleappReader.App;

public sealed class AiProviderSetting
{
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<AiKind>))] public AiKind Kind { get; set; } = AiKind.OpenAi;
    public string Address { get; set; } = "";
    /// <summary>The API key encrypted with DPAPI for this Windows user (base64), never the key itself.</summary>
    public string? ProtectedKey { get; set; }

    [JsonIgnore] public AiProvider Provider => new(Name, Kind, Address);
    [JsonIgnore] public bool HasKey => !string.IsNullOrEmpty(ProtectedKey);

    public string? Key()
    {
        if (!HasKey) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey!), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; } // another user's or machine's settings
    }

    public void SetKey(string? key) =>
        ProtectedKey = string.IsNullOrWhiteSpace(key) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));

    static readonly byte[] Entropy = "LEXIS AI endpoint key"u8.ToArray();

    public AiClient Client() => new(Provider, Key());
}

/// <summary>Whether AI is on, for the AI buttons and menus to bind to
/// ({Binding Enabled, Source={x:Static local:AiState.Instance}}): off, they are disabled. An instance, not a static
/// property: WPF's binding to a static property threw "Value cannot be null (key)" when the main window opened.</summary>
public sealed class AiState : System.ComponentModel.INotifyPropertyChanged
{
    public static AiState Instance { get; } = new();
    public bool Enabled => Settings.Current.Ai.Enabled;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public static void Changed() => Instance.PropertyChanged?.Invoke(Instance, new(nameof(Enabled)));
}

/// <summary>Which provider and model does a job (text, vision, transcription).</summary>
public sealed class AiRole
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AiTheme
{
    public string Name { get; set; } = "";
    public string Indicators { get; set; } = "";
}

/// <summary>AI preferences: off until the examiner turns it on. Ollama and LM Studio are listed by default.</summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public List<AiProviderSetting> Providers { get; set; } =
    [
        new() { Name = "Ollama", Kind = AiKind.Ollama, Address = "http://localhost:11434" },
        new() { Name = "LM Studio", Kind = AiKind.OpenAi, Address = "http://localhost:1234/v1" },
    ];
    /// <summary>The local engine in use: "Ollama" or "LM Studio" (both kept in <see cref="Providers"/> with their addresses).</summary>
    public string LocalEngine { get; set; } = "";
    public AiRole Text { get; set; } = new();
    public AiRole Vision { get; set; } = new();
    public AiRole Transcription { get; set; } = new();
    /// <summary>The Claude CLI executable; it runs only when allowed here and confirmed at each run.</summary>
    public string ClaudePath { get; set; } = "claude";
    public bool ClaudeAllowed { get; set; }
    /// <summary>whisper.cpp's whisper-cli.exe: transcription on this machine. Empty until the examiner gives it.</summary>
    public string WhisperPath { get; set; } = "";
    /// <summary>The MCP server inside LEXIS is on: other programs (LM Studio, Claude Desktop…) read the case open in
    /// LEXIS through it, read-only, on the loopback only.</summary>
    public bool McpAllowed { get; set; }
    public int McpPort { get; set; } = 47811;
    /// <summary>The secret part of the server's address, made once: the configurations given to other programs keep working.</summary>
    public string McpToken { get; set; } = "";
    [JsonIgnore] public string McpUrl => McpHttpServer.Address(McpPort, McpToken);
    /// <summary>"Explain" may look up on the web what the app's data format means: only the app, artifact, database file
    /// and column names leave the machine, shown and confirmed at every run. Off unless turned on.</summary>
    public bool ExplainWebLookup { get; set; }
    public string WebSearchUrl { get; set; } = WebLookup.DefaultUrl;
    public string TranslateTo { get; set; } = "English";
    /// <summary>Language of the AI's answers (summaries, answers, reasons); the records' own words are quoted as they are.</summary>
    public string AnswerLanguage { get; set; } = "English";
    /// <summary>Most records (messages) sent in one request; a summary over more goes in steps carrying the summary so far.</summary>
    public int MaxRecordsPerRequest { get; set; } = 20;
    public string VisionCategories { get; set; } =
        "weapons, drugs, identity documents, cash or payment cards, screenshots, vehicles, people, documents, other";
    public List<AiTheme> Themes { get; set; } =
    [
        new()
        {
            Name = "Drug dealing",
            Indicators = """
                - prices together with quantities or weights
                - names or street names of substances
                - arranging a handover: place, time, "the usual place", "outside"
                - payment for goods: cash, transfers, vouchers, crypto
                - care to avoid detection: deleting messages, moving to another app, code words
                """,
        },
    ];

    public const string ClaudeName = "Claude CLI", WhisperName = "whisper.cpp";

    /// <summary>The provider of a job; the Claude CLI only while it is allowed (it is not one of the stored servers).</summary>
    public AiProviderSetting? ProviderOf(AiRole r) => r.Provider switch
    {
        ClaudeName => ClaudeAllowed ? new AiProviderSetting { Name = ClaudeName, Kind = AiKind.ClaudeCli, Address = ClaudePath } : null,
        WhisperName => WhisperPath != "" ? new AiProviderSetting { Name = WhisperName, Kind = AiKind.WhisperCpp, Address = WhisperPath } : null,
        _ => Providers.FirstOrDefault(p => p.Name == r.Provider),
    };
}
