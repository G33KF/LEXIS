using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AleappReader.Core;

/// <summary>
/// SILK v3 audio, the codec of WeChat and QQ voice messages (stored under names such as msg_….amr): recognised by its
/// content ("#!SILK_V3", in WeChat after a 0x02 byte), not by its name. It is encoded, not encrypted. For listening it is
/// decoded with the SILK SDK (silk.dll, built from source by tools/silk/build.cmd) into a 16-bit mono WAV at 24 kHz, kept in
/// the case's audio cache under the SHA-256 of the original. The original is only read.
/// </summary>
public static class SilkAudio
{
    public const int Rate = 24000;
    public const string Decoder = "SILK SDK (kn007/silk-v3-decoder 507be6b)";
    static ReadOnlySpan<byte> Magic => "#!SILK_V3"u8;

    /// <summary>Whether a file's first bytes are SILK v3's header, at its start or after WeChat's 0x02.</summary>
    public static bool IsSilk(ReadOnlySpan<byte> head) => head.StartsWith(Magic) || head.Length > 0 && head[0] == 2 && head[1..].StartsWith(Magic);

    [StructLayout(LayoutKind.Sequential)]
    struct DecControl { public int ApiSampleRate, FrameSize, FramesPerPacket, MoreInternalDecoderFrames, InBandFecOffset; }

    [DllImport("silk")] static extern int SKP_Silk_SDK_Get_Decoder_Size(out int bytes);
    [DllImport("silk")] static extern int SKP_Silk_SDK_InitDecoder(IntPtr state);
    [DllImport("silk")] static extern int SKP_Silk_SDK_Decode(IntPtr state, ref DecControl control, int lost, byte[] data, int bytes, short[] samples, ref short count);

    /// <summary>Decodes a SILK v3 stream into a WAV. As the SDK's own decoder: packets are read until the stream ends or
    /// gives a negative length; a packet the SDK rejects is counted and skipped. Returns the samples written and the
    /// packets rejected.</summary>
    public static (long Samples, int Rejected) Decode(Stream silk, Stream wav)
    {
        var first = silk.ReadByte();
        var head = new byte[Magic.Length];
        head[0] = (byte)first;
        var skip = first == 2 ? 0 : 1; // WeChat's 0x02 is not part of the header
        if (first < 0 || silk.ReadAtLeast(head.AsSpan(skip), head.Length - skip, throwOnEndOfStream: false) < head.Length - skip || !head.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Not SILK v3 audio.");
        SKP_Silk_SDK_Get_Decoder_Size(out var size);
        var state = Marshal.AllocHGlobal(size);
        try
        {
            SKP_Silk_SDK_InitDecoder(state);
            var control = new DecControl { ApiSampleRate = Rate };
            wav.Write(new byte[44]); // the header, once the length is known
            var len = new byte[2];
            var payload = new byte[1024 * 5];
            var pcm = new short[Rate / 1000 * 20 * 5];
            long samples = 0;
            var rejected = 0;
            while (silk.ReadAtLeast(len, 2, throwOnEndOfStream: false) == 2 && BinaryPrimitives.ReadInt16LittleEndian(len) is var n and >= 0 and <= 1024 * 5
                   && silk.ReadAtLeast(payload.AsSpan(0, n), n, throwOnEndOfStream: false) == n)
            {
                var frames = 0;
                do
                {
                    var count = (short)pcm.Length;
                    if (SKP_Silk_SDK_Decode(state, ref control, 0, payload, n, pcm, ref count) != 0) { rejected++; break; }
                    var bytes = MemoryMarshal.AsBytes(pcm.AsSpan(0, count));
                    if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException();
                    wav.Write(bytes);
                    samples += count;
                } while (control.MoreInternalDecoderFrames != 0 && ++frames < 5);
            }
            if (samples == 0) throw new InvalidDataException(rejected > 0 ? $"The SILK decoder rejected all {rejected} packets." : "The SILK file holds no audio.");
            wav.Position = 0;
            wav.Write(WavHeader(samples * 2, Rate));
            wav.Position = wav.Length;
            return (samples, rejected);
        }
        finally { Marshal.FreeHGlobal(state); }
    }

    /// <summary>The 44-byte header of a 16-bit mono PCM WAV with <paramref name="dataBytes"/> of samples.</summary>
    public static byte[] WavHeader(long dataBytes, int rate)
    {
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), (int)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), 16);       // PCM format chunk
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1);        // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), 1);        // mono
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), rate * 2); // bytes per second
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), 2);        // bytes per sample
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(40), (int)dataBytes);
        return h;
    }

    public const string FolderName = "_reader_audio", IndexName = "index.tsv";
    const string IndexHeader = "sha256\tpath\tsize\tdecoded\tdecoder\trejected_packets";

    /// <summary>The folders of a case's decoded audio. The first, written: _reader_audio in the report folder when the
    /// tags are there, else under %LOCALAPPDATA%\AleappReader\audio keyed like them (read-only folder, portable case).
    /// The second, only read: the _reader_audio a portable case carries.</summary>
    public static string[] Folders(CaseSession s)
    {
        var own = Path.Combine(s.RootDir, FolderName);
        return string.Equals(Path.GetDirectoryName(s.Tags.Path), s.RootDir, StringComparison.OrdinalIgnoreCase) && s.PackagePath is null
            ? [own] : [Path.ChangeExtension(TagStore.LocalPath("audio", s.Tags.Path), null), own];
    }

    /// <summary>The SHA-256 of a file when it is SILK, else null.</summary>
    static string? SilkSha(Func<Stream> open)
    {
        using (var st = open())
        {
            Span<byte> head = stackalloc byte[10];
            if (!IsSilk(head[..st.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)])) return null;
        }
        using (var st = open()) return Convert.ToHexStringLower(SHA256.HashData(st));
    }

    /// <summary>
    /// A file to play for <paramref name="m"/>: the WAV decoded from it when it is SILK (with the original's SHA-256), else
    /// its own file. The WAV is
    /// <c>&lt;SHA-256 of the original&gt;.wav</c>, looked up in <paramref name="folders"/>, else decoded into the first
    /// and listed in its index.tsv (SHA-256, path in the extraction, size, when, decoder, packets rejected).
    /// </summary>
    public static (string File, string? Sha) Playable(MediaRef m, IReadOnlyList<string> folders)
    {
        if (SilkSha(m.OpenRead) is not { } sha) return (m.LocalFile(), null);
        var name = sha + ".wav";
        if (folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists) is { } found) return (found, sha);
        var folder = folders[0];
        var wav = Path.Combine(folder, name);
        Directory.CreateDirectory(folder);
        var temp = wav + $".{Environment.ProcessId}.tmp";
        (long Samples, int Rejected) r;
        try
        {
            using var input = m.OpenRead();
            using var output = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite);
            r = Decode(input, output);
        }
        catch { File.Delete(temp); throw; }
        try { File.Move(temp, wav); }
        catch (IOException) when (File.Exists(wav)) { File.Delete(temp); return (wav, sha); } // decoded meanwhile elsewhere
        var index = Path.Combine(folder, IndexName);
        var line = string.Join('\t', sha, m.ExtractionPath ?? m.SourcePath ?? m.Name, m.Size, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
            Decoder, r.Rejected) + "\n";
        File.AppendAllText(index, (File.Exists(index) ? "" : IndexHeader + "\n") + line);
        return (wav, sha);
    }

    /// <summary>What speech to text is given for <paramref name="m"/>: the WAV decoded from it when it is SILK (the one
    /// kept with the case, see <see cref="Playable"/>), named .wav, with the original's SHA-256; else its own bytes and name.</summary>
    public static (byte[] Audio, string Name, string? SilkSha) ForTranscription(MediaRef m, IReadOnlyList<string> folders)
    {
        if (SilkSha(m.OpenRead) is null) return (m.ReadAllBytes(), m.Name, null);
        var (wav, sha) = Playable(m, folders);
        return (File.ReadAllBytes(wav), Path.ChangeExtension(m.Name, ".wav"), sha);
    }

    /// <summary>The decoded audio a portable case takes: the WAVs of the case's folders for the originals in
    /// <paramref name="originals"/> (every one when null), and an index.tsv of only those, written to <paramref name="staging"/>.</summary>
    public static List<(string Src, string Rel)> ForExport(CaseSession s, string staging, IEnumerable<string>? originals)
    {
        var keep = originals?.Select(f => SilkSha(() => File.OpenRead(f))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string, string)>();
        var rows = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Folders(s).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var index = Path.Combine(dir, IndexName);
            var lines = File.Exists(index) ? File.ReadAllLines(index) : [];
            foreach (var wav in Directory.EnumerateFiles(dir, "*.wav"))
            {
                var sha = Path.GetFileNameWithoutExtension(wav);
                if (keep?.Contains(sha) == false || !seen.Add(sha)) continue;
                files.Add((wav, $"{FolderName}/{sha}.wav"));
                rows.AddRange(lines.Where(l => l.StartsWith(sha + "\t", StringComparison.OrdinalIgnoreCase)));
            }
        }
        if (files.Count == 0) return files;
        var staged = Path.Combine(staging, "audio_" + IndexName);
        File.WriteAllLines(staged, [IndexHeader, .. rows]);
        files.Add((staged, $"{FolderName}/{IndexName}"));
        return files;
    }
}
