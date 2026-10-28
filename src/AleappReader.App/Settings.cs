using System.IO;
using System.Text.Json;

namespace AleappReader.App;

/// <summary>Per-user preferences in %APPDATA%\AleappReader\settings.json.</summary>
public sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AleappReader", "settings.json");

    public string? LastOutputDir { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public bool OnlineMapTiles { get; set; } = true;
    public List<string> RecentCases { get; set; } = [];
    /// <summary>Shows each app's artifacts together under Applications instead of in the macro categories.</summary>
    public bool GroupByApp { get; set; }
    /// <summary>Every view opened from the tree in one tab, its view swapped for the next.</summary>
    public bool SingleNavigationTab { get; set; } = true;
    /// <summary>One tab for each group of the tree (Analyzed Data › Calls), swapping its view for the next of that group.</summary>
    public bool NavigationGroupTabs { get; set; }
    /// <summary>Tags offered in menus, in order, and their keyboard shortcuts ("Ctrl+1").</summary>
    public List<string> Tags { get; set; } = [.. Core.TagStore.DefaultTags];
    public Dictionary<string, string> TagShortcuts { get; set; } =
        Core.TagStore.DefaultTags.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => $"Ctrl+{x.i + 1}");

    /// <summary>The main window as the examiner left it: null until then (it opens maximized).</summary>
    public WindowLayout? Window { get; set; }

    /// <summary>AI providers, roles and prompts; AI stays off until enabled.</summary>
    public AiSettings Ai { get; set; } = new();
    /// <summary>OCR, run on request: the Tesseract languages, the Windows OCR language and the limits of a file read.</summary>
    public OcrSettings Ocr { get; set; } = new();

    public static Settings Current { get; } = Load();

    static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new Settings(); }
    }

    /// <summary>Writes the settings. The AI part is written only by the AI settings (<paramref name="ai"/>): any other save
    /// keeps the AI part on disk, so a second LEXIS window, opened while AI was on, cannot turn it back on by saving
    /// its recent cases or layout after AI was turned off.</summary>
    public void Save(bool ai = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var node = JsonSerializer.SerializeToNode(this)!;
            if (!ai && File.Exists(FilePath) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))?["Ai"] is { } onDisk)
                node["Ai"] = onDisk.DeepClone();
            File.WriteAllText(FilePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public sealed record WindowLayout(double Left, double Top, double Width, double Height, bool Maximized, double TreeWidth, double DetailsWidth);

    public void AddRecent(string path)
    {
        RecentCases.Remove(path);
        RecentCases.Insert(0, path);
        if (RecentCases.Count > 10) RecentCases.RemoveRange(10, RecentCases.Count - 10);
        Save();
    }
}

public sealed class OcrSettings
{
    public List<string> Languages { get; set; } = ["eng", "ita"];
    /// <summary>A language Windows can read (e.g. it-IT); empty: the first of the user's languages it can.</summary>
    public string WindowsLanguage { get; set; } = "";
    public int MaxMegabytes { get; set; } = 20;
    public int MaxPages { get; set; } = 200;
}
