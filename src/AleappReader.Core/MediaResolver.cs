using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <param name="Created">The file's creation time on the device as LAVA recorded it (Unix seconds), null when unknown.</param>
/// <param name="Modified">The file's last modification time on the device (Unix seconds), null when unknown.</param>
public sealed record MediaRef(string RefId, string Name, string? MimeType, string? SourcePath,
    string? ExtractionPath, string? FullPath, string Module, string Artifact, long? Created = null, long? Modified = null)
{
    /// <summary>Set for a portable case, whose media stay in the zip.</summary>
    public DeferredFiles? Deferred { get; init; }
    public bool Exists => FullPath is not null && (File.Exists(FullPath) || Deferred?.Has(FullPath) == true);
    bool OnDisk => File.Exists(FullPath);

    /// <summary>The file's bytes, read-only: from disk, else from the portable case's zip (checked).</summary>
    public Stream OpenRead() => OnDisk ? new FileStream(FullPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        : Deferred?.Open(FullPath!) ?? throw new FileNotFoundException("Not in the case.", Name);

    public byte[] ReadAllBytes()
    {
        using var st = OpenRead();
        using var ms = new MemoryStream();
        st.CopyTo(ms);
        return ms.ToArray();
    }

    public long Size => OnDisk ? new FileInfo(FullPath!).Length : Deferred?.Size(FullPath!) ?? 0;

    /// <summary>A file on disk for a player or another program: a portable case's is written to its temp folder once.</summary>
    public string LocalFile() => OnDisk ? FullPath! : Deferred?.Materialize(FullPath!) ?? throw new FileNotFoundException("Not in the case.", Name);

    public bool IsImage => Kind == "image";
    public string Kind
    {
        get
        {
            var t = MimeType ?? "";
            if (t.StartsWith("image/")) return "image";
            if (t.StartsWith("video/")) return "video";
            if (t.StartsWith("audio/")) return "audio";
            return Path.GetExtension(FullPath ?? Name).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" or ".tif" or ".tiff" => "image",
                ".mp4" or ".3gp" or ".mov" or ".mkv" or ".webm" or ".avi" => "video",
                ".mp3" or ".m4a" or ".aac" or ".amr" or ".ogg" or ".opus" or ".wav" => "audio",
                _ => "file",
            };
        }
    }
}

/// <summary>Resolves media cells (a media ref id, or a JSON list of them) through _lava_media_info.</summary>
public sealed class MediaResolver(CaseSession session)
{
    public static IReadOnlyList<string> RefIds(object? cell)
    {
        if (cell is not string s || string.IsNullOrWhiteSpace(s)) return [];
        s = s.Trim();
        if (s.StartsWith('['))
            try { return JsonSerializer.Deserialize<List<JsonElement>>(s)!.Select(e => e.ToString()).Where(x => x != "").ToList(); }
            catch (JsonException) { }
        return [s];
    }

    public IReadOnlyList<MediaRef> Resolve(SqliteConnection con, object? cell) =>
        RefIds(cell).Select(id => Get(con, id)).OfType<MediaRef>().ToList();

    public MediaRef? Get(SqliteConnection con, string refId)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT * FROM _lava_media_info WHERE media_ref_id = @id";
        cmd.Parameters.AddWithValue("@id", refId);
        try
        {
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
        catch (SqliteException) { return null; } // case without media tables
    }

    /// <summary>All media in the case, for the gallery.</summary>
    public IReadOnlyList<MediaRef> All(SqliteConnection con, int limit = 5000)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT * FROM _lava_media_info ORDER BY artifact_name, name LIMIT @l";
        cmd.Parameters.AddWithValue("@l", limit);
        var list = new List<MediaRef>();
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Read(r));
        }
        catch (SqliteException) { }
        return list;
    }

    MediaRef Read(SqliteDataReader r)
    {
        string? S(string col) { var i = Ordinal(r, col); return i < 0 || r.IsDBNull(i) ? null : r.GetValue(i).ToString(); }
        var extraction = S("extraction_path");
        return new MediaRef(S("media_ref_id") ?? "", S("name") ?? Path.GetFileName(extraction ?? ""), S("type"),
            S("source_path"), extraction, session.ResolveInCase(extraction), S("module_name") ?? "", S("artifact_name") ?? "",
            Epoch(S("created_at")), Epoch(S("updated_at"))) { Deferred = session.Deferred };
    }

    /// <summary>Unix seconds from a stored number (milliseconds are scaled down); 0, negative or text: unknown.</summary>
    static long? Epoch(string? v) =>
        double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            ? (long)(d > 1e11 ? d / 1000 : d) : null;

    static int Ordinal(SqliteDataReader r, string col)
    {
        for (int i = 0; i < r.FieldCount; i++) if (r.GetName(i) == col) return i;
        return -1;
    }
}
