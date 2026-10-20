using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AleappReader.Core;

/// <summary>A cell value that holds structured data (JSON, XML, a plist, HTML, a BLOB that may be a plist or protobuf):
/// named by a cheap look at its first characters, decoded into a tree only when asked. The value is untrusted: XML is
/// read without DTDs, nesting and size are capped as in <see cref="PlistTree"/>.</summary>
public static partial class StructuredValue
{
    public const string Json = "JSON", Xml = "XML", Plist = "plist", Html = "HTML", Blob = "BLOB";
    const int MaxNodes = 100_000, MaxDepth = 64;

    [GeneratedRegex(@"<(html|body|div|p|br|table|span|a|img|font|head)[\s/>]", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/li|/h\d)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>|<[^>]+>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTagOrBlock();

    /// <summary>What the value looks like it holds, or null for plain text and numbers.</summary>
    public static string? Kind(object? raw)
    {
        if (raw is byte[] b) return b.Length == 0 ? null : PlistTree.IsPlist(b) ? Plist : Blob;
        if (raw is not string s) return null;
        var t = s.AsSpan().Trim();
        if (t.Length < 2) return null;
        if (t[0] is '{' or '[' && t[^1] is '}' or ']') return Json;
        if (t.StartsWith("YnBsaXN0MDA")) return Plist; // base64 of "bplist00"
        if (t[0] == '<' && t[^1] == '>')
            return t.Contains("<plist", StringComparison.Ordinal) ? Plist : HtmlTag().IsMatch(s) ? Html : Xml;
        return null;
    }

    /// <summary>The value decoded as a tree under a "root" node; null when it does not parse as its kind.</summary>
    public static PlistNode? Parse(object? raw, string kind)
    {
        try
        {
            return kind switch
            {
                Plist when raw is byte[] b => PlistTree.Read(b),
                Plist when raw is string s && s.TrimStart().StartsWith("YnBsaXN0MDA") => PlistTree.Read(Convert.FromBase64String(s.Trim())),
                Plist when raw is string s => PlistTree.Read(Encoding.UTF8.GetBytes(s)),
                Blob when raw is byte[] b => PlistTree.Blob(b),
                Json when raw is string s => FromJson(s),
                Xml when raw is string s => FromXml(s),
                Html when raw is string s => FromHtml(s),
                _ => null,
            };
        }
        catch (Exception e) when (e is JsonException or XmlException or FormatException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>The value laid out for reading: JSON and XML indented, a plist or BLOB as its tree, HTML as its text.</summary>
    public static string? Pretty(object? raw, string kind)
    {
        try
        {
            switch (kind)
            {
                case Json when raw is string s:
                    using (var doc = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = MaxDepth, AllowTrailingCommas = true }))
                        return JsonSerializer.Serialize(doc.RootElement, Indented);
                case Xml when raw is string s:
                    return Load(s).ToString();
                case Html when raw is string s:
                    return HtmlText(s);
                default:
                    return Parse(raw, kind) is { } n ? PlistTree.ToText(n) : null;
            }
        }
        catch (Exception e) when (e is JsonException or XmlException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>A structured value on one line for a list row: its filled-in leaves as "key: value; …" (HTML as its text),
    /// cut at <paramref name="max"/> characters; null when the value is not structured or does not decode.</summary>
    public static string? Brief(object? raw, int max = 160)
    {
        if (Kind(raw) is not { } kind || kind == Blob) return null;
        if (raw is string { Length: > 256 * 1024 }) return null; // ponytail: big values stay raw in a row; the details decode them
        if (kind == Html) return Cut(string.Join(" · ", HtmlText((string)raw!).Split('\n')), max);
        if (Parse(raw, kind) is not { } root) return null;
        var sb = new StringBuilder();
        void Walk(PlistNode n)
        {
            foreach (var c in n.Children)
            {
                if (sb.Length > max) return;
                // a branch's Value is a count ("3 keys"); only XML elements carry text beside their attributes
                if ((c.Children.Count == 0 || c.Type == "element") && c.Value is { Length: > 0 } v)
                    sb.Append(sb.Length == 0 ? "" : "; ").Append(c.Key.TrimStart('@')).Append(": ").Append(v);
                Walk(c);
            }
        }
        if (root.Children.Count == 0) sb.Append(root.Value);
        Walk(root);
        return Cut(sb.ToString(), max);
    }

    static string Cut(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static PlistNode FromJson(string s)
    {
        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = MaxDepth, AllowTrailingCommas = true });
        var work = 0;
        return Json1("root", doc.RootElement, "", ref work);
    }

    static PlistNode Json1(string key, JsonElement e, string path, ref int work)
    {
        if (++work > MaxNodes) return new PlistNode(key, "truncated", "size limit reached", path);
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var n = new PlistNode(key, "object", $"{e.EnumerateObject().Count()} keys", path);
                foreach (var p in e.EnumerateObject()) n.Children.Add(Json1(p.Name, p.Value, path == "" ? p.Name : $"{path}.{p.Name}", ref work));
                return n;
            }
            case JsonValueKind.Array:
            {
                var n = new PlistNode(key, "array", $"{e.GetArrayLength()} items", path);
                var i = 0;
                foreach (var x in e.EnumerateArray()) { n.Children.Add(Json1($"[{i}]", x, $"{path}[{i}]", ref work)); i++; }
                return n;
            }
            case JsonValueKind.String:
                var v = e.GetString() ?? "";
                // a string that is itself JSON (common in app stores) is opened in place
                if (Kind(v) == Json && FromJsonOrNull(v) is { } inner) { var w = new PlistNode(key, "string (JSON)", "", path); w.Children.AddRange(inner.Children); return w; }
                return new PlistNode(key, "string", v, path);
            default:
                return new PlistNode(key, e.ValueKind.ToString().ToLowerInvariant(), e.GetRawText(), path);
        }
    }

    static PlistNode? FromJsonOrNull(string s)
    {
        try { return FromJson(s); }
        catch (JsonException) { return null; }
    }

    static XElement Load(string s)
    {
        using var r = XmlReader.Create(new StringReader(s.Trim()), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(r);
    }

    static PlistNode FromXml(string s)
    {
        var work = 0;
        return Element(Load(s), "", 0, ref work);
    }

    static PlistNode Element(XElement e, string path, int depth, ref int work)
    {
        var p = path == "" ? e.Name.LocalName : $"{path}/{e.Name.LocalName}";
        if (++work > MaxNodes || depth > MaxDepth) return new PlistNode(e.Name.LocalName, "truncated", "nesting or size limit reached", p);
        var leaf = !e.HasElements;
        var n = new PlistNode(e.Name.LocalName, "element", leaf ? e.Value.Trim() : "", p);
        foreach (var a in e.Attributes()) n.Children.Add(new PlistNode("@" + a.Name.LocalName, "attribute", a.Value, $"{p}/@{a.Name.LocalName}"));
        foreach (var c in e.Elements()) n.Children.Add(Element(c, p, depth + 1, ref work));
        return n;
    }

    static PlistNode FromHtml(string s)
    {
        var n = new PlistNode("root", "HTML text", "", "");
        var i = 0;
        foreach (var line in HtmlText(s).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            n.Children.Add(new PlistNode($"[{i}]", "text", line, $"[{i++}]"));
        return n;
    }

    /// <summary>HTML as its text: scripts and styles dropped, a line per paragraph, break, row or item, entities decoded.</summary>
    public static string HtmlText(string html)
    {
        var t = HtmlBreak().Replace(html, "\n");
        t = HtmlTagOrBlock().Replace(t, " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return string.Join("\n", t.Split('\n').Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim()).Where(l => l != ""));
    }
}
