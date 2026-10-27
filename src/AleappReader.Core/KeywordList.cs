using System.Globalization;
using System.Text;

namespace AleappReader.Core;

/// <summary>A keyword list from a text file: one keyword per line, each line also split on a separator (default ",").
/// Ends are trimmed, empty entries and repeats dropped; a line that is "#" or starts with "# " is a comment, so a
/// hashtag such as #tag stays a keyword.</summary>
public static class KeywordList
{
    public const string DefaultSeparator = ",";

    public static IReadOnlyList<string> Parse(string text, string? separator = DefaultSeparator)
    {
        var sep = separator == "\\t" ? "\t" : separator ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line == "#" || line.StartsWith("# ")) continue;
            foreach (var k in sep == "" ? [line] : line.Split(sep))
                if (k.Trim() is { Length: > 0 } w && seen.Add(w)) list.Add(w);
        }
        return list;
    }

    /// <summary>The file's text and the encoding read: a BOM (UTF-8, UTF-16), else UTF-8 when the bytes are valid
    /// UTF-8, else <paramref name="fallback"/> (default: the system's ANSI code page, GBK on a Chinese Windows).</summary>
    public static (string Text, string Encoding) Decode(byte[] bytes, Encoding? fallback = null)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        if (bytes is [0xFF, 0xFE, ..]) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        if (bytes is [0xFE, 0xFF, ..]) return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");
        try { return (new UTF8Encoding(false, true).GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException) { }
        if (fallback is null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            fallback = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        return (fallback.GetString(bytes), fallback.WebName);
    }
}
