using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core;

/// <summary>A keyword found in an extraction file: how many times (UTF-8 and UTF-16LE together) and where first.</summary>
public sealed record FileHit(string Path, string Type, int Count, long FirstOffset);

/// <summary>Raw access to the extraction's files: type by content (magic bytes), hex dump, keyword search.</summary>
public static class RawFiles
{
    static readonly (byte[] Magic, int At, string Type)[] Magics =
    [
        (Encoding.ASCII.GetBytes("SQLite format 3\0"), 0, "SQLite database"),
        ([0x37, 0x7F, 0x06, 0x82], 0, "SQLite WAL"), ([0x37, 0x7F, 0x06, 0x83], 0, "SQLite WAL"),
        (Encoding.ASCII.GetBytes("bplist"), 0, "Binary plist"),
        (Encoding.ASCII.GetBytes("SEGB"), 0, "Apple SEGB (Biome)"), (Encoding.ASCII.GetBytes("SEGB"), 52, "Apple SEGB (Biome)"),
        (Encoding.ASCII.GetBytes("T-DB"), 16, "Realm database"),
        (Encoding.ASCII.GetBytes("ABX\0"), 0, "Android binary XML (ABX)"), ([0x03, 0x00, 0x08, 0x00], 0, "Android binary XML (AXML)"),
        (Encoding.ASCII.GetBytes("dex\n"), 0, "Android DEX"), ([0x7F, 0x45, 0x4C, 0x46], 0, "ELF executable"),
        ([0xCF, 0xFA, 0xED, 0xFE], 0, "Mach-O executable"), ([0xCA, 0xFE, 0xBA, 0xBE], 0, "Mach-O universal / Java class"),
        ([0xFF, 0xD8, 0xFF], 0, "JPEG image"), ([0x89, 0x50, 0x4E, 0x47], 0, "PNG image"), (Encoding.ASCII.GetBytes("GIF8"), 0, "GIF image"),
        (Encoding.ASCII.GetBytes("WEBP"), 8, "WebP image"), (Encoding.ASCII.GetBytes("WAVE"), 8, "WAV audio"),
        (Encoding.ASCII.GetBytes("OggS"), 0, "Ogg audio"), (Encoding.ASCII.GetBytes("ID3"), 0, "MP3 audio"), (Encoding.ASCII.GetBytes("#!AMR"), 0, "AMR audio"),
        (Encoding.ASCII.GetBytes("%PDF"), 0, "PDF document"), ([0x50, 0x4B, 0x03, 0x04], 0, "ZIP archive (also APK, JAR, Office)"),
        ([0x1F, 0x8B], 0, "gzip"), (Encoding.ASCII.GetBytes("BZh"), 0, "bzip2"), ([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], 0, "xz"),
        ([0x28, 0xB5, 0x2F, 0xFD], 0, "zstd"), ([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], 0, "7-Zip archive"),
    ];

    /// <summary>The file's type from its first bytes, whatever its name says.</summary>
    public static string Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return "Empty";
        foreach (var (magic, at, type) in Magics)
            if (head.Length >= at + magic.Length && head.Slice(at, magic.Length).SequenceEqual(magic)) return type;
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(head.Slice(8, 4));
            return brand is "heic" or "heix" or "mif1" or "msf1" ? "HEIF image" : brand == "qt  " ? "QuickTime video" : "MP4 / M4A";
        }
        var text = head[..Math.Min(head.Length, 4096)];
        if (text.Contains((byte)0)) return "Data";
        var t = Encoding.UTF8.GetString(text).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (t.StartsWith("<?xml") || t.StartsWith('<')) return t.Contains("<plist") ? "XML plist" : "XML / HTML";
        if (t.StartsWith('{') || t.StartsWith('[')) return "JSON (by its first character)";
        return "Text";
    }

    /// <summary>The kinds of file the file browser filters on.</summary>
    public const string Pictures = "Pictures", Videos = "Videos", Audio = "Audio", Documents = "Documents", Databases = "Databases",
        Plists = "Property lists", Archives = "Archives", Structured = "JSON, XML, HTML", PlainText = "Text", Executables = "Executables", Other = "Other";
    public static readonly IReadOnlyList<string> Categories = [Pictures, Videos, Audio, Documents, Databases, Plists, Archives, Structured, PlainText, Executables, Other];

    /// <summary>The kind of a file: from its content type (<see cref="Sniff"/>) when that is telling, else from its name.</summary>
    public static string Category(string path, string type)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var byName = ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".heif" or ".bmp" or ".tif" or ".tiff" or ".avif" => Pictures,
            ".mp4" or ".mov" or ".3gp" or ".mkv" or ".webm" or ".avi" => Videos,
            ".mp3" or ".m4a" or ".aac" or ".amr" or ".opus" or ".ogg" or ".wav" or ".caf" => Audio,
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".odt" or ".ods" or ".rtf" or ".pages" or ".numbers" or ".key" => Documents,
            ".db" or ".sqlite" or ".sqlite3" or ".sqlitedb" or ".storedata" or ".realm" or ".db-wal" or ".db-shm" or ".db-journal" => Databases,
            ".plist" => Plists,
            ".zip" or ".tar" or ".gz" or ".tgz" or ".7z" or ".rar" or ".bz2" or ".xz" or ".zst" or ".apk" or ".jar" or ".ipa" => Archives,
            ".json" or ".xml" or ".html" or ".htm" or ".xhtml" => Structured,
            ".txt" or ".log" or ".csv" or ".ini" or ".conf" or ".cfg" or ".md" or ".properties" or ".vcf" => PlainText,
            ".so" or ".dex" or ".odex" or ".oat" or ".vdex" or ".dylib" or ".exe" or ".dll" => Executables,
            _ => path.EndsWith("-wal", StringComparison.Ordinal) || path.EndsWith("-shm", StringComparison.Ordinal) || path.EndsWith("-journal", StringComparison.Ordinal) ? Databases : Other,
        };
        if (type.EndsWith(" image")) return Pictures;
        if (type is "QuickTime video" or "MP4 / M4A") return byName == Audio ? Audio : Videos;
        if (type.EndsWith(" audio")) return Audio;
        if (type == "PDF document") return Documents;
        if (type.StartsWith("ZIP archive")) return byName is Documents or Archives ? byName : Archives;
        if (type is "gzip" or "bzip2" or "xz" or "zstd" or "7-Zip archive") return Archives;
        if (type.StartsWith("SQLite") || type == "Realm database") return Databases;
        if (type is "Binary plist" or "XML plist") return Plists;
        if (type is "XML / HTML" || type.StartsWith("JSON") || type.StartsWith("Android binary XML")) return Structured;
        if (type is "Android DEX" or "ELF executable" or "Mach-O executable") return Executables;
        if (type == "Text") return byName is Structured or Other ? (byName == Other ? PlainText : Structured) : byName;
        return byName;
    }

    /// <summary>The bytes as text when they look like it: UTF-8, or UTF-16 with its byte order mark; null for binary data.</summary>
    public static string? AsText(ReadOnlySpan<byte> b)
    {
        if (b is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(b[2..]);
        if (b is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(b[2..]);
        if (b.IsEmpty || b[..Math.Min(b.Length, 8192)].Contains((byte)0)) return null;
        return Encoding.UTF8.GetString(b).TrimStart('﻿');
    }

    /// <summary>MD5, SHA-1 and SHA-256 of a stream, in one pass, as lower-case hex.</summary>
    public static (string Md5, string Sha1, string Sha256) Hashes(Stream s)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 16];
        for (int n; (n = s.Read(buf, 0, buf.Length)) > 0;)
        {
            md5.AppendData(buf, 0, n);
            sha1.AppendData(buf, 0, n);
            sha256.AppendData(buf, 0, n);
        }
        return (Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha1.GetHashAndReset()), Convert.ToHexStringLower(sha256.GetHashAndReset()));
    }

    /// <summary>Classic hex dump: offset, 16 bytes, their ASCII.</summary>
    public static string HexDump(ReadOnlySpan<byte> data, long offset)
    {
        var sb = new StringBuilder(data.Length * 5);
        for (int i = 0; i < data.Length; i += 16)
        {
            var line = data.Slice(i, Math.Min(16, data.Length - i));
            sb.Append((offset + i).ToString("X8")).Append("  ");
            for (int j = 0; j < 16; j++) sb.Append(j < line.Length ? line[j].ToString("X2") + " " : "   ").Append(j == 7 ? " " : "");
            sb.Append(' ');
            foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The bytes at <paramref name="offset"/> (at most <paramref name="max"/>) of an extraction file.</summary>
    public static byte[] ReadAt(ExtractionFiles x, string rel, long offset, int max) => x.Read(rel, s =>
    {
        if (s.CanSeek) s.Seek(offset, SeekOrigin.Begin);
        else { var skip = new byte[81920]; for (long left = offset; left > 0;) { var n = s.Read(skip, 0, (int)Math.Min(skip.Length, left)); if (n == 0) break; left -= n; } }
        var buf = new byte[max];
        int got = 0, r;
        while (got < max && (r = s.Read(buf, got, max - got)) > 0) got += r;
        return buf[..got];
    }) ?? [];

    static byte[] Fold(byte[] b) { for (int i = 0; i < b.Length; i++) if (b[i] is >= (byte)'A' and <= (byte)'Z') b[i] += 32; return b; }

    /// <summary>Every file of <paramref name="paths"/> holding <paramref name="keyword"/> as UTF-8 or UTF-16LE text,
    /// ASCII letters case-insensitive. Each file's type is sniffed on the way.</summary>
    public static IReadOnlyList<FileHit> Search(ExtractionFiles x, IReadOnlyList<string> paths, string keyword,
                                                IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var k = keyword.ToLowerInvariant();
        byte[][] needles = [Fold(Encoding.UTF8.GetBytes(k)), Fold(Encoding.Unicode.GetBytes(k))]; // folded as the data is
        var keep = needles.Max(n => n.Length) - 1;
        var hits = new List<FileHit>();
        var buf = new byte[1 << 20];
        var done = 0;
        void Next() => progress?.Report((++done, paths.Count));
        x.Each(paths, (rel, s) => // one pass over a tar.gz, not one per file
        {
            int count = 0, carry = 0, n;
            long first = -1, at = 0; // file offset of buf[0]
            string? type = null;
            while ((n = s.Read(buf, carry, buf.Length - carry)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var len = carry + n;
                type ??= Sniff(buf.AsSpan(0, Math.Min(len, 4096)));
                for (int i = carry; i < len; i++) if (buf[i] is >= (byte)'A' and <= (byte)'Z') buf[i] += 32;
                var span = buf.AsSpan(0, len);
                foreach (var needle in needles)
                    for (int i = span.IndexOf(needle); i >= 0; i = span[(i + 1)..].IndexOf(needle) is var j and >= 0 ? i + 1 + j : -1)
                        if (i + needle.Length > carry) // a match wholly inside the carried tail was counted last round
                        {
                            count++;
                            if (first < 0 || at + i < first) first = at + i;
                        }
                carry = Math.Min(keep, len);
                span[(len - carry)..].CopyTo(buf);
                at += len - carry;
            }
            if (count > 0) hits.Add(new FileHit(rel, type ?? "Empty", count, first));
            Next();
        }, ct, _ => Next());
        return hits;
    }
}
