using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MetadataExtractor.Formats.Xmp;

namespace AleappReader.Core;

/// <summary>A media file's embedded metadata read with MetadataExtractor (EXIF, GPS, XMP, IPTC, maker notes, QuickTime/MP4,
/// RIFF, PNG, HEIF…): a summary first (date taken, camera, GPS, length), then every tag by group. Read from the bytes
/// only; the "File" group of the reader (the name and dates of the copy it was handed) is left out.</summary>
public static class MediaMetadata
{
    public sealed record Tag(string Group, string Name, string Value);

    const string Summary = "Summary";
    static readonly string[] Skipped = ["File", "Huffman", "ICC Profile"];

    public static IReadOnlyList<Tag> Read(Stream s)
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(s); }
        catch (Exception e) when (e is ImageProcessingException or IOException or ArgumentException or InvalidOperationException
                                      or IndexOutOfRangeException or NotSupportedException) { return []; }
        var list = new List<Tag>();
        void Sum(string name, string? v) { if (!string.IsNullOrWhiteSpace(v)) list.Add(new(Summary, name, v.Trim())); }

        var sub = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        Sum("Date taken (camera clock)", sub?.GetDescription(ExifDirectoryBase.TagDateTimeOriginal) ?? ifd0?.GetDescription(ExifDirectoryBase.TagDateTime));
        Sum("Camera", string.Join(" ", new[] { ifd0?.GetDescription(ExifDirectoryBase.TagMake), ifd0?.GetDescription(ExifDirectoryBase.TagModel) }
            .Where(x => !string.IsNullOrWhiteSpace(x))));
        Sum("Software", ifd0?.GetDescription(ExifDirectoryBase.TagSoftware));
        if (dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation() is { IsZero: false } g)
            Sum("GPS position", string.Create(CultureInfo.InvariantCulture, $"{g.Latitude:0.000000}, {g.Longitude:0.000000}"));
        if (dirs.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault() is { } mov)
        {
            Sum("Created (movie header, UTC)", mov.GetDescription(QuickTimeMovieHeaderDirectory.TagCreated));
            Sum("Duration", mov.GetDescription(QuickTimeMovieHeaderDirectory.TagDuration));
        }

        foreach (var d in dirs.Where(d => !Skipped.Contains(d.Name)))
        {
            foreach (var t in d.Tags)
                if (t.Description is { } v && v.Trim() != "") list.Add(new(d.Name, t.Name, Clip(v)));
            if (d is XmpDirectory x)
                foreach (var (k, v) in x.GetXmpProperties())
                    if (v.Trim() != "") list.Add(new(d.Name, k, Clip(v)));
            foreach (var e in d.Errors) list.Add(new(d.Name, "Reader note", e));
        }
        return list;
    }

    static string Clip(string v) => v.Length > 500 ? v[..500] + "…" : v.Trim();
}
