using System.Globalization;

namespace AleappReader.Core;

/// <summary>Turns stored cell values into display text. Datetime columns hold UTC epoch seconds.</summary>
public static partial class ValueFormatter
{
    /// <summary>Display zone for datetime columns. UTC unless the examiner picks another.</summary>
    public static TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public static string Format(object? value, ColumnInfo column)
    {
        if (value is null) return "";
        if (column.IsDateTime && TryEpoch(value, out var dto)) return FormatTime(dto);
        return value switch
        {
            byte[] b => $"<{b.Length} bytes>",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    /// <summary>The value as read on screen: <see cref="Format"/>, with percent-encoded text (search terms and URLs, e.g.
    /// "%E4%B8%96%E7%95%8C" for 世界) decoded. Exports and the details' "as stored" line keep the stored form.</summary>
    public static string Display(object? value, ColumnInfo column) => value is string s && !column.IsDateTime ? Readable(WithoutIcons(s)) : Format(value, column);

    /// <summary>The text without the icons a LEAPP adds for its HTML report ("Outgoing &lt;i data-feather="phone-outgoing"…&gt;&lt;/i&gt;"
    /// reads "Outgoing"): the word beside the icon already says what it means.</summary>
    public static string WithoutIcons(string s) => s.Contains("data-feather", StringComparison.Ordinal) ? FeatherIcon().Replace(s, "").Trim() : s;

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*<i\s+data-feather=""[^""]*""[^>]*>\s*</i>")]
    private static partial System.Text.RegularExpressions.Regex FeatherIcon();

    /// <summary>Percent-encoded UTF-8 decoded (twice at most, for double-encoded URLs); "+" reads as a space when the
    /// text has encoded parts and no space of its own, as in a URL query. Unchanged unless the result is valid UTF-8.</summary>
    public static string Readable(string s)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            if (s.Length > 200_000 || !HasPercent(s)) return s;
            var bytes = new List<byte>(s.Length);
            var plusIsSpace = !s.Contains(' ');
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                {
                    bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else if (s[i] == '+' && plusIsSpace) bytes.Add((byte)' ');
                else
                {
                    var n = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
                    bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(s.Substring(i, n)));
                    i += n - 1;
                }
            }
            string decoded;
            try { decoded = Strict.GetString([.. bytes]); }
            catch (System.Text.DecoderFallbackException) { return s; }
            if (decoded == s || decoded.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) return s;
            s = decoded;
        }
        return s;
    }

    static readonly System.Text.UTF8Encoding Strict = new(false, true);

    static bool HasPercent(string s)
    {
        for (int i = s.IndexOf('%'); i >= 0 && i + 2 < s.Length; i = s.IndexOf('%', i + 1))
            if (Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) return true;
        return false;
    }

    /// <summary>The start of a calendar day in the display time zone, as Unix seconds.</summary>
    public static long DayStart(DateTime day) => new DateTimeOffset(day.Date, Zone.GetUtcOffset(day.Date)).ToUnixTimeSeconds();

    /// <summary>A period of calendar days in the display zone, both included, as Unix seconds (null: open).</summary>
    public static (long? From, long? To) Days(DateTime? from, DateTime? to) =>
        (from is { } f ? DayStart(f) : null, to is { } t ? DayStart(t.AddDays(1)) - 1 : null);

    /// <summary>A wall-clock time in the display zone as Unix seconds. A time the clock skips when summer time starts reads
    /// as the same time an hour on; a time it shows twice when summer time ends reads as its first instant for a lower
    /// bound and its second for an upper one, so a period never loses that hour.</summary>
    public static long ToUnixSeconds(DateTime local, bool upper = false)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = Zone.IsAmbiguousTime(local)
            ? (upper ? Zone.GetAmbiguousTimeOffsets(local).Min() : Zone.GetAmbiguousTimeOffsets(local).Max())
            : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeSeconds();
    }

    public static string FormatTime(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone);
        var off = local.Offset;
        var sign = off < TimeSpan.Zero ? "-" : "+";
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
               (off == TimeSpan.Zero ? " UTC" : $" (UTC{sign}{off:hh\\:mm})");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?(?<zone>\s*(Z|UTC|GMT|[+-]\d{2}:?\d{2}))?$")]
    private static partial System.Text.RegularExpressions.Regex IsoLike();

    /// <summary>A date written as text (ISO 8601 or "yyyy-MM-dd HH:mm[:ss]"), or a plist date, read in the display zone:
    /// "in the display zone: …" when the text states its zone (Z, UTC, GMT, +hh:mm), "no time zone in the text; read as
    /// UTC: …" when it does not, since the zone it was written in is then not known. Null for anything else, and while
    /// the display zone is UTC (nothing to convert to).</summary>
    public static string? DateReading(object? value)
    {
        if (Zone == TimeZoneInfo.Utc) return null;
        if (value is DateTime { Kind: DateTimeKind.Utc } d) return "in the display zone: " + FormatTime(new DateTimeOffset(d));
        if (value is not string s || s.Length > 40 || IsoLike().Match(s.Trim()) is not { Success: true } m) return null;
        var t = s.Trim();
        var zoned = m.Groups["zone"].Success;
        if (zoned) t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*(UTC|GMT)$", "Z");
        if (!DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, zoned ? DateTimeStyles.None : DateTimeStyles.AssumeUniversal, out var dto)) return null;
        return (zoned ? "in the display zone: " : "no time zone in the text; read as UTC: ") + FormatTime(dto);
    }

    static readonly (string Name, DateTime Epoch, double TicksPerUnit)[] Epochs =
    [
        ("Unix seconds", DateTime.UnixEpoch, TimeSpan.TicksPerSecond),
        ("Unix milliseconds", DateTime.UnixEpoch, TimeSpan.TicksPerMillisecond),
        ("Unix microseconds", DateTime.UnixEpoch, 10),
        ("Unix nanoseconds", DateTime.UnixEpoch, 0.01),
        ("Apple Cocoa seconds (2001)", new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.TicksPerSecond),
        ("Apple Cocoa nanoseconds (2001)", new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0.01),
        ("HFS+ seconds (1904)", new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.TicksPerSecond),
        ("WebKit/Chrome microseconds (1601)", new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), 10),
        ("Windows FILETIME (1601, 100 ns)", new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1),
        (".NET ticks (0001, 100 ns)", DateTime.MinValue, 1),
        ("GPS seconds (1980, no leap seconds)", new DateTime(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc), TimeSpan.TicksPerSecond),
    ];

    /// <summary>
    /// A number read under every common timestamp epoch, keeping the readings that land between 2005 and 2040; null when
    /// none does. The floor is the smartphone era: below it any small number would qualify as a Cocoa time in 2001.
    /// The examiner picks the one the source implies: this lists candidates, it does not decide.
    /// </summary>
    public static string? TimestampCandidates(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n == 0) return null;
        var lines = new List<string>();
        foreach (var (name, epoch, ticksPerUnit) in Epochs)
        {
            var ticks = epoch.Ticks + n * ticksPerUnit;
            if (ticks < new DateTime(2005, 1, 1).Ticks || ticks > new DateTime(2040, 1, 1).Ticks) continue;
            lines.Add($"{name}: {FormatTime(new DateTimeOffset((long)ticks, TimeSpan.Zero))}");
        }
        return lines.Count == 0 ? null : "As a timestamp, this number reads as:\n" + string.Join("\n", lines);
    }

    public static bool TryEpoch(object? value, out DateTimeOffset result)
    {
        result = default;
        long secs;
        switch (value)
        {
            case long l: secs = l; break;
            case double d when double.IsFinite(d): secs = (long)d; break;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p): secs = p; break;
            default: return false;
        }
        if (secs is < -62135596800 or > 253402300799) return false;
        result = DateTimeOffset.FromUnixTimeSeconds(secs);
        return true;
    }
}
