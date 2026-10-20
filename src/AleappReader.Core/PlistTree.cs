using System.Globalization;
using System.Text;
using System.Xml;

namespace AleappReader.Core;

/// <summary>One node of a decoded property list: its key, type, value as text, and key path from the root.</summary>
public sealed class PlistNode(string key, string type, string value, string path)
{
    public string Key { get; } = key;
    public string Type { get; } = type;
    public string Value { get; } = value;
    public string Path { get; } = path;
    public List<PlistNode> Children { get; } = [];
    /// <summary>The raw value (number, bytes, date...) for the inspector; null for containers.</summary>
    public object? Raw { get; init; }
    /// <summary>A leaf that is a date (a plist date, or text in ISO form) read in the display zone; null otherwise.</summary>
    public string? Reading => Children.Count == 0 ? ValueFormatter.DateReading(Raw is DateTime ? Raw : Value) : null;
}

/// <summary>
/// A property list as a tree, whatever its form: XML or bplist00; an NSKeyedArchiver archive resolved into the objects it
/// encodes (dictionaries, arrays, strings, data, dates, with each object's class); a data value that is itself a plist
/// decoded in place; a data value that parses whole as protobuf wire format decoded blind (field numbers only: the
/// schema is not known). Dates are Mac Absolute time (seconds since 2001-01-01 UTC) as the format stores them. The file
/// is untrusted: nesting and work are capped, the XML is read without DTDs or external entities.
/// </summary>
public static class PlistTree
{
    const int MaxDepth = 64;

    public static bool IsPlist(ReadOnlySpan<byte> b) => b.StartsWith("bplist00"u8) || XmlPlist(b);

    static bool XmlPlist(ReadOnlySpan<byte> b)
    {
        var head = Encoding.UTF8.GetString(b[..Math.Min(b.Length, 512)]);
        return head.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('<') && head.Contains("<plist", StringComparison.Ordinal);
    }

    /// <summary>The plist as a tree under a "root" node; null when it is neither form or is damaged.</summary>
    public static PlistNode? Read(byte[] b)
    {
        var v = b.AsSpan().StartsWith("bplist00"u8) ? Bplist.Parse(b) : XmlPlist(b) ? Xml(b) : null;
        if (v is null) return null;
        var work = 0;
        if (Unarchive(v) is { } archived) return Node("root", archived.Value, "", 0, ref work, $"NSKeyedArchiver ({archived.Count:N0} objects)");
        return Node("root", v, "", 0, ref work);
    }

    /// <summary>A BLOB decoded: as a plist, else as blind protobuf; null when it is neither.</summary>
    public static PlistNode? Blob(byte[] b)
    {
        if (Read(b) is { } p) return p;
        var work = 0;
        return Node("root", b, "", 0, ref work) is { Children.Count: > 0 } n ? n : null;
    }

    /// <summary>The tree as indented text: key = value, with each node's type.</summary>
    public static string ToText(PlistNode root)
    {
        var sb = new StringBuilder();
        void W(PlistNode n, int d)
        {
            sb.Append(' ', d * 2).Append(n.Key).Append(" (").Append(n.Type).Append(')');
            if (n.Value != "") sb.Append(" = ").Append(n.Value);
            sb.Append('\n');
            foreach (var c in n.Children) W(c, d + 1);
        }
        W(root, 0);
        return sb.ToString();
    }

    static string Child(string path, string key) =>
        key.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '-') && key != "" ? (path == "" ? key : path + "." + key) : $"{path}[\"{key.Replace("\"", "\\\"")}\"]";

    static PlistNode Node(string key, object? v, string path, int depth, ref int work, string? type = null)
    {
        if (++work > 200_000 || depth > MaxDepth) return new PlistNode(key, "truncated", "nesting or size limit reached", path);
        switch (v)
        {
            case Archived a:
            {
                var n = new PlistNode(key, type ?? a.Class, "", path);
                foreach (var (k, x) in a.Fields) n.Children.Add(Node(k, x, Child(path, k), depth + 1, ref work));
                return n;
            }
            case Dictionary<string, object?> d:
            {
                var n = new PlistNode(key, type ?? $"dict ({d.Count})", "", path);
                foreach (var (k, x) in d) n.Children.Add(Node(k, x, Child(path, k), depth + 1, ref work));
                return n;
            }
            case List<object?> l:
            {
                var n = new PlistNode(key, type ?? $"array ({l.Count})", "", path);
                for (var i = 0; i < l.Count; i++) n.Children.Add(Node(i.ToString(CultureInfo.InvariantCulture), l[i], $"{path}[{i}]", depth + 1, ref work));
                return n;
            }
            case byte[] b:
            {
                var preview = $"{b.Length:N0} bytes " + Convert.ToHexStringLower(b, 0, Math.Min(b.Length, 32)) + (b.Length > 32 ? "…" : "");
                if (IsPlist(b) && Read(b) is { } inner)
                {
                    var n = new PlistNode(key, "data: nested plist", preview, path) { Raw = b };
                    n.Children.AddRange(inner.Children.Select(c => Rebase(c, path, depth + 1)));
                    return n;
                }
                if (!PrintableUtf8(b) && Protobuf(b, 0) is { Count: > 0 } fields)
                {
                    var n = new PlistNode(key, "data: protobuf (blind)", preview, path) { Raw = b };
                    foreach (var f in fields) n.Children.Add(Field(f, path, depth + 1, ref work));
                    return n;
                }
                return new PlistNode(key, "data", PrintableUtf8(b) && b.Length > 0 ? $"\"{Encoding.UTF8.GetString(b)}\" ({b.Length:N0} bytes)" : preview, path) { Raw = b };
            }
            case DateTime t: return new PlistNode(key, "date", t.ToString("yyyy-MM-dd HH:mm:ss.FFF 'UTC'", CultureInfo.InvariantCulture), path) { Raw = t };
            case string s: return new PlistNode(key, "string", s, path) { Raw = s };
            case bool x: return new PlistNode(key, "bool", x ? "true" : "false", path) { Raw = x };
            case long x: return new PlistNode(key, "integer", x.ToString(CultureInfo.InvariantCulture), path) { Raw = x };
            case double x: return new PlistNode(key, "real", x.ToString("R", CultureInfo.InvariantCulture), path) { Raw = x };
            case float x: return new PlistNode(key, "real", x.ToString("R", CultureInfo.InvariantCulture), path) { Raw = (double)x };
            case Bplist.Uid u: return new PlistNode(key, "UID", u.Value.ToString(CultureInfo.InvariantCulture), path) { Raw = u.Value };
            case null: return new PlistNode(key, "null", "", path);
            default: return new PlistNode(key, v.GetType().Name, v.ToString() ?? "", path) { Raw = v };
        }
    }

    /// <summary>A nested plist's nodes with paths continuing the outer one.</summary>
    static PlistNode Rebase(PlistNode n, string prefix, int depth)
    {
        var path = prefix == "" || n.Path.StartsWith('[') ? prefix + n.Path : prefix + "." + n.Path;
        var r = new PlistNode(n.Key, n.Type, n.Value, depth > MaxDepth ? prefix : path) { Raw = n.Raw };
        r.Children.AddRange(n.Children.Select(c => Rebase(c, prefix, depth + 1)));
        return r;
    }

    static bool PrintableUtf8(byte[] b)
    {
        if (b.Length == 0) return true;
        try { return new UTF8Encoding(false, true).GetString(b).All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'); }
        catch (DecoderFallbackException) { return false; }
    }

    // ---- NSKeyedArchiver ----

    /// <summary>An archived object: its class name and its fields, resolved.</summary>
    sealed record Archived(string Class, List<(string Key, object? Value)> Fields);

    /// <summary>The root object of an NSKeyedArchiver archive, resolved; null when the plist is not one.</summary>
    static (object? Value, int Count)? Unarchive(object? v)
    {
        if (v is not Dictionary<string, object?> d || d.GetValueOrDefault("$objects") is not List<object?> objs || d.GetValueOrDefault("$top") is not Dictionary<string, object?> top
            || !(d.GetValueOrDefault("$archiver") as string ?? "").Contains("KeyedArchiver", StringComparison.Ordinal)) return null;
        var visiting = new HashSet<long>();
        var work = 0;
        object? Resolve(object? x, int depth)
        {
            if (++work > 200_000 || depth > MaxDepth) return "<nesting or size limit reached>";
            if (x is Bplist.Uid u)
            {
                if (u.Value < 0 || u.Value >= objs.Count) return $"<UID {u.Value} outside $objects>";
                if (!visiting.Add(u.Value)) return $"<reference to object {u.Value}, already open above>";
                try { return Object(objs[(int)u.Value], depth); }
                finally { visiting.Remove(u.Value); }
            }
            return x switch
            {
                List<object?> l => l.Select(i => Resolve(i, depth + 1)).ToList(),
                Dictionary<string, object?> o => Object(o, depth),
                _ => x,
            };
        }
        object? Object(object? o, int depth)
        {
            if (o is "$null") return null;
            if (o is not Dictionary<string, object?> dict) return o is List<object?> ? Resolve(o, depth + 1) : o;
            var cls = dict.GetValueOrDefault("$class") is Bplist.Uid cu && cu.Value >= 0 && cu.Value < objs.Count && objs[(int)cu.Value] is Dictionary<string, object?> cd
                ? cd.GetValueOrDefault("$classname") as string ?? "object" : null;
            if (cls is null) return new Archived("dict", dict.Select(kv => (kv.Key, Resolve(kv.Value, depth + 1))).ToList());
            if (dict.GetValueOrDefault("NS.keys") is List<object?> keys && dict.GetValueOrDefault("NS.objects") is List<object?> vals)
                return new Archived(cls, keys.Zip(vals, (k, x) => (Resolve(k, depth + 1)?.ToString() ?? "null", Resolve(x, depth + 1))).ToList());
            if (dict.GetValueOrDefault("NS.objects") is List<object?> items)
                return new Archived(cls, items.Select((x, i) => (i.ToString(CultureInfo.InvariantCulture), Resolve(x, depth + 1))).ToList());
            if (dict.GetValueOrDefault("NS.string") is { } s) return Resolve(s, depth + 1);
            if (dict.GetValueOrDefault("NS.bytes") is byte[] nb) return nb;
            if (dict.GetValueOrDefault("NS.data") is { } nd && Resolve(nd, depth + 1) is byte[] nbytes) return nbytes;
            if (dict.GetValueOrDefault("NS.time") is double t && double.IsFinite(t)) return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Math.Clamp(t, -6e10, 2.4e11));
            if (dict.GetValueOrDefault("NS.uuidbytes") is byte[] ub && ub.Length == 16) return new Guid(ub, bigEndian: true).ToString().ToUpperInvariant();
            if (dict.GetValueOrDefault("NS.relative") is { } rel) return Resolve(rel, depth + 1);
            return new Archived(cls, dict.Where(kv => kv.Key != "$class").Select(kv => (kv.Key, Resolve(kv.Value, depth + 1))).ToList());
        }
        var root = top.Count == 1 ? Resolve(top.Values.First(), 0) : new Archived("$top", top.Select(kv => (kv.Key, Resolve(kv.Value, 0))).ToList());
        return (root, objs.Count);
    }

    // ---- XML plist ----

    static object? Xml(byte[] b)
    {
        try
        {
            using var r = XmlReader.Create(new MemoryStream(b), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true });
            r.MoveToContent();
            if (r.Name != "plist") return null;
            r.Read();
            return r.NodeType == XmlNodeType.Element ? XmlValue(r, 0) : null;
        }
        catch (Exception e) when (e is XmlException or FormatException or InvalidDataException or OverflowException) { return null; }
    }

    /// <summary>The element under the reader, which is left past its end.</summary>
    static object? XmlValue(XmlReader r, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("Nesting too deep.");
        var name = r.Name;
        if (r.IsEmptyElement)
        {
            r.Read();
            return name switch { "true" => true, "false" => false, "dict" => new Dictionary<string, object?>(), "array" => new List<object?>(), "data" => Array.Empty<byte>(), "string" => "", _ => null };
        }
        switch (name)
        {
            case "dict":
            {
                var d = new Dictionary<string, object?>();
                r.Read();
                while (r.NodeType == XmlNodeType.Element)
                {
                    if (r.Name != "key") throw new InvalidDataException("A dict entry without its key.");
                    var key = r.ReadElementContentAsString();
                    d[key] = r.NodeType == XmlNodeType.Element ? XmlValue(r, depth + 1) : null;
                }
                r.Read();
                // an XML archive writes a UID as a dict with the one key CF$UID
                return d.Count == 1 && d.GetValueOrDefault("CF$UID") is long uid ? new Bplist.Uid(uid) : d;
            }
            case "array":
            {
                var l = new List<object?>();
                r.Read();
                while (r.NodeType == XmlNodeType.Element) l.Add(XmlValue(r, depth + 1));
                r.Read();
                return l;
            }
            default:
                var text = r.ReadElementContentAsString();
                return name switch
                {
                    "string" => text,
                    "integer" => long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : ulong.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? (long)u : throw new FormatException(),
                    "real" => double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                    "date" => DateTime.Parse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                    "data" => Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))),
                    _ => text,
                };
        }
    }

    // ---- blind protobuf ----

    public sealed record ProtoField(long Number, int Wire, object Value);

    /// <summary>The fields of a protobuf message read without its schema; null unless the bytes parse whole as wire format
    /// (varint, fixed64, length-delimited, fixed32). A length-delimited field is a nested message when it parses whole as
    /// one, else text when it is printable UTF-8, else bytes.</summary>
    public static List<ProtoField>? Protobuf(ReadOnlySpan<byte> b, int depth)
    {
        if (depth > 16 || b.Length == 0) return null;
        var fields = new List<ProtoField>();
        var i = 0;
        while (i < b.Length)
        {
            if (Varint(b, ref i) is not { } key) return null;
            var number = (long)(key >> 3);
            var wire = (int)(key & 7);
            if (number is < 1 or > 536_870_911) return null;
            switch (wire)
            {
                case 0: if (Varint(b, ref i) is not { } v) return null; fields.Add(new(number, 0, (long)v)); break;
                case 1: if (i + 8 > b.Length) return null; fields.Add(new(number, 1, BitConverter.ToInt64(b.Slice(i, 8)))); i += 8; break;
                case 5: if (i + 4 > b.Length) return null; fields.Add(new(number, 5, BitConverter.ToInt32(b.Slice(i, 4)))); i += 4; break;
                case 2:
                    if (Varint(b, ref i) is not { } len || len > (ulong)(b.Length - i)) return null;
                    var inner = b.Slice(i, (int)len).ToArray();
                    i += (int)len;
                    fields.Add(new(number, 2, PrintableUtf8(inner) ? Encoding.UTF8.GetString(inner) : Protobuf(inner, depth + 1) is { Count: > 0 } sub ? sub : inner));
                    break;
                default: return null; // groups (3, 4) are deprecated: a blob using them is more likely not protobuf
            }
        }
        return fields;
    }

    static ulong? Varint(ReadOnlySpan<byte> b, ref int i)
    {
        ulong v = 0;
        for (var shift = 0; shift < 64 && i < b.Length; shift += 7)
        {
            var x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if (x < 0x80) return v;
        }
        return null;
    }

    static PlistNode Field(ProtoField f, string path, int depth, ref int work)
    {
        var p = $"{path}.{f.Number}";
        var key = f.Number.ToString(CultureInfo.InvariantCulture);
        if (++work > 200_000 || depth > MaxDepth) return new PlistNode(key, "truncated", "nesting or size limit reached", p);
        switch (f.Value)
        {
            case List<ProtoField> sub:
                var n = new PlistNode(key, "message", "", p);
                foreach (var s in sub) n.Children.Add(Field(s, p, depth + 1, ref work));
                return n;
            case string s: return new PlistNode(key, "string", s, p) { Raw = s };
            case byte[] b: return new PlistNode(key, "bytes", $"{b.Length:N0} bytes " + Convert.ToHexStringLower(b, 0, Math.Min(b.Length, 32)) + (b.Length > 32 ? "…" : ""), p) { Raw = b };
            case long v when f.Wire == 1:
                return new PlistNode(key, "fixed64", $"{v} (double {BitConverter.Int64BitsToDouble(v).ToString("R", CultureInfo.InvariantCulture)}){AsTime(v)}", p) { Raw = BitConverter.Int64BitsToDouble(v) };
            case int v: return new PlistNode(key, "fixed32", $"{v} (float {BitConverter.Int32BitsToSingle(v).ToString("R", CultureInfo.InvariantCulture)}){AsTime(v)}", p) { Raw = (long)v };
            case long v: return new PlistNode(key, "varint", v.ToString(CultureInfo.InvariantCulture) + (v < 0 || v % 2 == 0 ? "" : $" (zigzag {-(v + 1) / 2})") + AsTime(v), p) { Raw = v };
            default: return new PlistNode(key, "?", f.Value.ToString() ?? "", p);
        }
    }

    /// <summary>A number read as a Unix time in seconds, milliseconds, microseconds or nanoseconds, when that falls in
    /// 2000–2040 (" · if a time: … UTC (Unix ms)"); empty otherwise. Without the schema it stays a reading, not a fact.</summary>
    public static string AsTime(long v)
    {
        const long From = 946_684_800, To = 2_208_988_800; // 2000-01-01, 2040-01-01 in seconds
        foreach (var (div, unit) in new[] { (1L, "s"), (1_000L, "ms"), (1_000_000L, "µs"), (1_000_000_000L, "ns") })
            if (v / div is >= From and < To) // the ranges of the units do not overlap: at most one reading
                return $" · if a time: {ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(v / div))} (Unix {unit})";
        return "";
    }
}
