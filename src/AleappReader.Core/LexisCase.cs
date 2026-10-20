using System.Text.Json;
using System.Text.Json.Serialization;

namespace AleappReader.Core;

/// <summary>One device of an investigation case: its report (folder, manifest or portable case zip), how it is called
/// in the case and the colour it is drawn in.</summary>
public sealed record LexisDevice(string Path, string Label, string Color);

/// <summary>
/// An investigation case (.lexiscase): a name, a proceeding number and the reports of several devices opened together.
/// Report paths are kept relative to the .lexiscase file when they sit below its folder, so the case moves with them.
/// </summary>
public sealed record LexisCase(string Name, string Number, IReadOnlyList<LexisDevice> Devices)
{
    public const string Extension = ".lexiscase";
    public static readonly string[] Palette = ["#1F77B4", "#D62728", "#2CA02C", "#9467BD", "#FF7F0E", "#17BECF", "#8C564B", "#E377C2"];
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    sealed record Dto(int FormatVersion, string Name, string Number, List<LexisDevice> Devices)
    {
        [JsonPropertyName("lexis")] public string Kind { get; init; } = "investigation case";
    }

    /// <summary>Reads a .lexiscase, with every device path made absolute.</summary>
    public static LexisCase Load(string file)
    {
        var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Empty investigation case file.");
        if (dto.Kind != "investigation case" || dto.FormatVersion != 1) throw new InvalidDataException("Not a LEXIS investigation case (format 1).");
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!;
        var devices = (dto.Devices ?? []).Select(d => d with { Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, d.Path)) }).ToList();
        if (devices.Select(d => d.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Count)
            throw new InvalidDataException("Two devices of the investigation case have the same label.");
        return new LexisCase(dto.Name ?? "", dto.Number ?? "", devices);
    }

    public void Save(string file)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!;
        string Rel(string p)
        {
            var r = System.IO.Path.GetRelativePath(dir, p);
            return r.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(r) ? System.IO.Path.GetFullPath(p) : r;
        }
        File.WriteAllText(file, JsonSerializer.Serialize(new Dto(1, Name, Number, Devices.Select(d => d with { Path = Rel(d.Path) }).ToList()), Json));
    }
}
