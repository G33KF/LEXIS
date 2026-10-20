using System.Buffers.Binary;
using System.Text;

namespace AleappReader.Core;

/// <summary>Binary property list (bplist00) as indented text, for the file browser, or as values. The file is untrusted:
/// every offset is bounds-checked and nesting is capped, so a crafted file gives an error line (or null) instead of a hang
/// or a crash.</summary>
public static class Bplist
{
    static readonly DateTime Cocoa = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A UID reference of an NSKeyedArchiver plist: an index into its $objects.</summary>
    public readonly record struct Uid(long Value);

    public static string ToText(byte[] b)
    {
        if (Open(b) is not var (r, top)) return b.Length < 40 || !b.AsSpan(0, 8).SequenceEqual("bplist00"u8) ? "Not a bplist00 file." : "Damaged trailer.";
        var sb = new StringBuilder();
        r.Write(sb, top, 0);
        return sb.ToString();
    }

    /// <summary>The plist as values: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, byte[], string, long, double,
    /// bool, DateTime (UTC), Uid; null when it is not a bplist00 or is damaged anywhere.</summary>
    public static object? Parse(byte[] b)
    {
        try { return Open(b) is var (r, top) ? r.Value(top, 0) : null; }
        catch (InvalidDataException) { return null; }
    }

    static (Reader, long)? Open(byte[] b)
    {
        if (b.Length < 40 || !b.AsSpan(0, 8).SequenceEqual("bplist00"u8)) return null;
        var t = b.AsSpan(b.Length - 32);
        int offSize = t[6], refSize = t[7];
        var count = BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
        var top = BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
        var table = BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
        if (offSize is < 1 or > 8 || refSize is < 1 or > 8 || count > (ulong)b.Length || table + count * (ulong)offSize > (ulong)b.Length - 32)
            return null;
        return (new Reader(b, offSize, refSize, (long)count, (long)table), (long)top);
    }

    sealed class Reader(byte[] b, int offSize, int refSize, long count, long table)
    {
        int _visits; // a shared object referenced many times fans out exponentially: cap the work, not just the depth

        static ulong Uint(ReadOnlySpan<byte> s) { ulong v = 0; foreach (var x in s) v = v << 8 | x; return v; }

        long Offset(long obj) => obj >= 0 && obj < count ? (long)Uint(b.AsSpan((int)(table + obj * offSize), offSize)) : -1;

        /// <summary>The object's marker, its count or size, and where its content starts; null when out of bounds.</summary>
        (int Marker, int Low, long P)? Head(long obj)
        {
            var at = Offset(obj);
            if (at < 8 || at >= b.Length - 32) return null;
            int marker = b[at], low = marker & 0xF;
            long p = at + 1;
            if (marker >> 4 is 0x4 or 0x5 or 0x6 or 0xA or 0xD && low == 0xF) // the length follows as an int object
            {
                if (p >= b.Length || b[p] >> 4 != 1 || p + 1 + (1 << (b[p] & 0xF)) > b.Length) return null;
                var n = 1 << (b[p] & 0xF);
                low = (int)Math.Min(int.MaxValue, Uint(b.AsSpan((int)p + 1, n)));
                p += 1 + n;
            }
            return (marker, low, p);
        }

        bool Fits(long p, long bytes) => bytes >= 0 && p + bytes <= b.Length - 32;
        long Ref(long at) => (long)Uint(b.AsSpan((int)at, refSize));

        public object? Value(long obj, int depth)
        {
            if (++_visits > 200_000 || depth > 64 || Head(obj) is not var (marker, low, p)) throw new InvalidDataException("Damaged plist.");
            Exception Bad() => new InvalidDataException("Damaged plist.");
            switch (marker >> 4)
            {
                case 0x0: return marker switch { 0x08 => false, 0x09 => true, _ => null };
                case 0x1 when Fits(p, 1 << low): return (long)Uint(b.AsSpan((int)p, 1 << low)); // 8 bytes: signed
                case 0x2 when Fits(p, 1 << low): return low == 2 ? BinaryPrimitives.ReadSingleBigEndian(b.AsSpan((int)p)) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan((int)p));
                case 0x3 when Fits(p, 8) && BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan((int)p)) is var secs && double.IsFinite(secs):
                    return Cocoa.AddSeconds(Math.Clamp(secs, -6e10, 2.4e11));
                case 0x4 when Fits(p, low): return b.AsSpan((int)p, low).ToArray();
                case 0x5 when Fits(p, low): return Encoding.ASCII.GetString(b, (int)p, low);
                case 0x6 when Fits(p, 2L * low): return Encoding.BigEndianUnicode.GetString(b, (int)p, 2 * low);
                case 0x8 when Fits(p, low + 1): return new Uid((long)Uint(b.AsSpan((int)p, low + 1)));
                case 0xA when Fits(p, (long)low * refSize):
                    var list = new List<object?>(low);
                    for (int i = 0; i < low; i++) list.Add(Value(Ref(p + (long)i * refSize), depth + 1));
                    return list;
                case 0xD when Fits(p, 2L * low * refSize):
                    var dict = new Dictionary<string, object?>(low);
                    for (int i = 0; i < low; i++)
                        dict[Value(Ref(p + (long)i * refSize), depth + 1) as string ?? throw Bad()] = Value(Ref(p + (long)(low + i) * refSize), depth + 1);
                    return dict;
                default: throw Bad();
            }
        }

        public void Write(StringBuilder sb, long obj, int depth, string prefix = "")
        {
            var pad = new string(' ', depth * 2);
            if (++_visits > 200_000) { if (_visits == 200_001) sb.Append(pad).Append("… truncated\n"); return; }
            if (depth > 64 || Head(obj) is not var (marker, low, p)) { sb.Append(pad).Append(prefix).Append("<invalid reference>\n"); return; }
            string line;
            switch (marker >> 4)
            {
                case 0x0: line = marker switch { 0x08 => "false", 0x09 => "true", _ => "null" }; break;
                case 0x1 when Fits(p, 1 << low):
                    var iv = Uint(b.AsSpan((int)p, 1 << low));
                    line = low == 3 ? ((long)iv).ToString() : iv.ToString(); break;
                case 0x2 when Fits(p, 1 << low):
                    line = low == 2 ? BinaryPrimitives.ReadSingleBigEndian(b.AsSpan((int)p)).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan((int)p)).ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                case 0x3 when Fits(p, 8):
                    var secs = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan((int)p));
                    line = Math.Abs(secs) < 1e10 ? Cocoa.AddSeconds(secs).ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : secs.ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                case 0x4 when Fits(p, low):
                    line = $"<data {low:N0} bytes> " + Convert.ToHexStringLower(b, (int)p, Math.Min(low, 64)) + (low > 64 ? "…" : ""); break;
                case 0x5 when Fits(p, low): line = "\"" + Encoding.ASCII.GetString(b, (int)p, low) + "\""; break;
                case 0x6 when Fits(p, 2L * low): line = "\"" + Encoding.BigEndianUnicode.GetString(b, (int)p, 2 * low) + "\""; break;
                case 0x8 when Fits(p, low + 1): line = $"UID {Uint(b.AsSpan((int)p, low + 1))}"; break;
                case 0xA when Fits(p, (long)low * refSize):
                    sb.Append(pad).Append(prefix).Append($"[ {low} items\n");
                    for (int i = 0; i < low; i++) Write(sb, Ref(p + (long)i * refSize), depth + 1);
                    sb.Append(pad).Append("]\n");
                    return;
                case 0xD when Fits(p, 2L * low * refSize):
                    sb.Append(pad).Append(prefix).Append("{\n");
                    for (int i = 0; i < low; i++)
                    {
                        var key = new StringBuilder();
                        Write(key, Ref(p + (long)i * refSize), depth + 1);
                        Write(sb, Ref(p + (long)(low + i) * refSize), depth + 1, key.ToString().Trim().Trim('"') + " = ");
                    }
                    sb.Append(pad).Append("}\n");
                    return;
                default: line = $"<unknown or truncated object 0x{marker:X2}>"; break;
            }
            sb.Append(pad).Append(prefix).Append(line).Append('\n');
        }
    }
}
