using System.Text.Json;

namespace AleappReader.Core;

/// <summary>One column of an artifact table: SQL name, display header, LAVA type (datetime, date, media, ... or null).</summary>
public sealed record ColumnInfo(string Sql, string Display, string? Type)
{
    public bool IsDateTime => Type == "datetime";
    public bool IsMedia => Type == "media";
}

/// <summary>The conversation view an artifact declares in data_views. Column values are SQL names.</summary>
public sealed record ConversationView(
    string? Discriminator, string? LabelColumn, string? Text, string? Direction, string? SentValue,
    string? Time, string? Sender, string? SentStaticLabel, string? Media, IReadOnlyList<string> Extra)
{
    /// <summary>The words a direction column says "sent" with, when the view is read from the columns' names.</summary>
    public const string InferredSentValues = "Outgoing|Sent|Out|From Me|Yes|True|1";

    /// <summary>Whether a direction value says the message was sent: the declared value, or one of the inferred ones.</summary>
    public bool IsSent(string value) => SentValue is { } s && s.Split('|').Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A conversation view read from the column names of a table of messages that declares none (Telegram, Signal…):
    /// a message text, a date, and the chat each message belongs to; the direction, sender and media when named so.
    /// Null when any of the three is missing.
    /// </summary>
    public static ConversationView? Infer(IReadOnlyList<ColumnInfo> columns)
    {
        string? Find(string pattern) => columns.FirstOrDefault(c => System.Text.RegularExpressions.Regex.IsMatch(c.Display.Trim(), pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase))?.Sql;
        var text = Find(@"^(message|message text|message body|text|body|content|message content)$");
        var time = columns.FirstOrDefault(c => c.IsDateTime)?.Sql;
        var chat = Find(@"^(chat|conversation|thread|dialog|room|group) ?id$") ?? Find(@"^(chat|conversation|thread|dialog|room)( name| title)?$");
        if (text is null || time is null || chat is null) return null;
        return new ConversationView(chat, Find(@"^(chat|conversation|thread|dialog|room|group)( name| title)?$") ?? chat, text,
            Find(@"^(direction|from me|is from me|is outgoing|outgoing|sent)$"), InferredSentValues, time,
            Find(@"^(sender|sender name|from|author)$"), null, columns.FirstOrDefault(c => c.IsMedia)?.Sql, []);
    }
}

public sealed class ArtifactInfo
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string TableName { get; init; }
    public string Module { get; init; } = "";
    public string? Icon { get; init; }
    public string? SourcePath { get; init; }
    public long RecordCount { get; set; }
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public ConversationView? Conversation { get; init; }

    public ColumnInfo? Column(string? nameOrDisplay) => nameOrDisplay is null ? null :
        Columns.FirstOrDefault(c => c.Sql == nameOrDisplay) ??
        Columns.FirstOrDefault(c => string.Equals(c.Display, nameOrDisplay, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}

public sealed record ModuleStatus(string Name, string Status);

/// <summary>The _lava_data.lava manifest. Read generically: nothing here knows any artifact by name.</summary>
public sealed class LavaManifest
{
    public IReadOnlyDictionary<string, string> ParserInfo { get; private init; } = new Dictionary<string, string>();
    /// <summary>The report was made by iLEAPP (an iPhone or iPad), not ALEAPP (Android).</summary>
    public bool IsAndroid => ParserInfo.GetValueOrDefault("leapp_name")?.Contains("ALEAPP", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsIos => ParserInfo.GetValueOrDefault("leapp_name")?.Contains("iLEAPP", StringComparison.OrdinalIgnoreCase) == true;
    public IReadOnlyDictionary<string, string> Params { get; private init; } = new Dictionary<string, string>();
    public string DbName { get; private init; } = "_lava_artifacts.db";
    public string ProcessingStatus { get; private init; } = "";
    public IReadOnlyList<ArtifactInfo> Artifacts { get; private init; } = [];
    public IReadOnlyList<ModuleStatus> Modules { get; private init; } = [];
    /// <summary>LEXIS or ALEAPP/iLEAPP, with the version: LEXIS when the report says so (parser_info.processed_with,
    /// from 1.2.4) or lists a LEXIS module (LEXIS_*, which older LEXIS reports show when one of them found data).</summary>
    public string ParsedBy
    {
        get
        {
            var leapp = $"{ParserInfo.GetValueOrDefault("leapp_name") ?? "LEAPP"} {ParserInfo.GetValueOrDefault("leapp_version")}".Trim();
            var lexis = ParserInfo.GetValueOrDefault("processed_with") == "LEXIS" || Modules.Any(m => m.Name.StartsWith("LEXIS_", StringComparison.OrdinalIgnoreCase));
            return lexis ? $"LEXIS ({leapp} with the LEXIS modules)" : leapp;
        }
    }
    /// <summary>The data source as recorded when a portable case was made (portable_case.provenance), else null.</summary>
    public IReadOnlyList<(string Group, string Name, string Value)>? FrozenProvenance { get; private init; }
    /// <summary>A portable case made with the extraction's files under Case/extraction (portable_case.file_system).</summary>
    public bool PortableFileSystem { get; private init; }

    public IEnumerable<IGrouping<string, ArtifactInfo>> ByCategory =>
        Artifacts.GroupBy(a => a.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

    public static LavaManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return Parse(doc.RootElement);
    }

    public static LavaManifest Parse(JsonElement root)
    {
        var artifacts = new List<ArtifactInfo>();
        if (root.TryGetProperty("artifacts", out var cats) && cats.ValueKind == JsonValueKind.Object)
            foreach (var cat in cats.EnumerateObject())
                if (cat.Value.ValueKind == JsonValueKind.Array)
                    foreach (var a in cat.Value.EnumerateArray())
                        if (ParseArtifact(cat.Name, a) is { } art) artifacts.Add(art);

        var modules = new List<ModuleStatus>();
        if (root.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
            foreach (var m in mods.EnumerateArray())
                modules.Add(new ModuleStatus(Str(m, "module_name") ?? "", Str(m, "module_status") ?? ""));

        var prms = new Dictionary<string, string>();
        foreach (var p in root.EnumerateObject())
            if (p.Name.StartsWith("param_") && p.Value.ValueKind != JsonValueKind.Null)
                prms[p.Name["param_".Length..]] = p.Value.ToString();

        return new LavaManifest
        {
            ParserInfo = root.TryGetProperty("parser_info", out var pi) ? Flatten(pi) : new Dictionary<string, string>(),
            Params = prms,
            DbName = Str(root, "lava_db_name") ?? "_lava_artifacts.db",
            ProcessingStatus = Str(root, "processing_status") ?? "",
            Artifacts = artifacts,
            Modules = modules,
            FrozenProvenance = root.TryGetProperty("portable_case", out var pc) && pc.ValueKind == JsonValueKind.Object &&
                               pc.TryGetProperty("provenance", out var pv) && pv.ValueKind == JsonValueKind.Array
                ? pv.EnumerateArray().Select(x => (Str(x, "group") ?? "", Str(x, "name") ?? "", Str(x, "value") ?? "")).ToList()
                : null,
            PortableFileSystem = root.TryGetProperty("portable_case", out var pc2) && pc2.ValueKind == JsonValueKind.Object &&
                                 pc2.TryGetProperty("file_system", out var fs) && fs.ValueKind == JsonValueKind.Object,
        };
    }

    static ArtifactInfo? ParseArtifact(string category, JsonElement a)
    {
        var table = Str(a, "tablename");
        if (string.IsNullOrEmpty(table)) return null;

        var types = new Dictionary<string, string>();
        if (a.TryGetProperty("object_columns", out var oc) && oc.ValueKind == JsonValueKind.Array)
            foreach (var o in oc.EnumerateArray())
                if (Str(o, "name") is { } n && Str(o, "type") is { } t) types[n] = t;

        var columns = new List<ColumnInfo>();
        if (a.TryGetProperty("column_map", out var cm) && cm.ValueKind == JsonValueKind.Object)
            foreach (var c in cm.EnumerateObject())  // document order == header order
                columns.Add(new ColumnInfo(c.Name, c.Value.ToString(), types.GetValueOrDefault(c.Name)));

        ConversationView? conv = null;
        if (a.TryGetProperty("data_views", out var dv) && dv.ValueKind == JsonValueKind.Object &&
            (dv.TryGetProperty("conversation", out var cv) || dv.TryGetProperty("chat", out cv)) &&
            cv.ValueKind == JsonValueKind.Object)
        {
            var extra = cv.TryGetProperty("extraColumns", out var ex) && ex.ValueKind == JsonValueKind.Array
                ? ex.EnumerateArray().Select(e => e.ToString()).ToList() : [];
            conv = new ConversationView(
                Str(cv, "conversationDiscriminatorColumn") ?? Str(cv, "threadDiscriminatorColumn"),
                Str(cv, "conversationLabelColumn") ?? Str(cv, "threadLabelColumn"),
                Str(cv, "textColumn"), Str(cv, "directionColumn"), Str(cv, "directionSentValue"),
                Str(cv, "timeColumn"), Str(cv, "senderColumn"), Str(cv, "sentMessageStaticLabel"),
                Str(cv, "mediaColumn"), extra);
        }

        return new ArtifactInfo
        {
            Category = category,
            Name = Str(a, "name") ?? table,
            TableName = table,
            Module = Str(a, "module") ?? "",
            Icon = Str(a, "artifact_icon"),
            SourcePath = Str(a, "source_path"),
            RecordCount = a.TryGetProperty("record_count", out var rc) && rc.TryGetInt64(out var n2) ? n2 : 0,
            Columns = columns,
            Conversation = conv ?? ConversationView.Infer(columns),
        };
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ToString() : null;

    static Dictionary<string, string> Flatten(JsonElement e) =>
        e.ValueKind != JsonValueKind.Object ? [] :
        e.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .ToDictionary(p => p.Name, p => p.Value.ToString());
}
