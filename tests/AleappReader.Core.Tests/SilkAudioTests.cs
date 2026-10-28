using System.Buffers.Binary;

namespace AleappReader.Core.Tests;

public class SilkAudioTests
{
    // 1 s of a 440 Hz tone at 24 kHz, encoded by the SDK's own encoder (tools/silk/out/silk_encoder.exe -tencent), so it
    // starts with WeChat's 0x02.
    static readonly string Tone = Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone_440hz_1s.silk");

    static MediaRef Ref(string path) => new("1", Path.GetFileName(path), "audio/amr", path, "/data/voice2/" + Path.GetFileName(path), path, "m", "a");

    [Fact]
    public void Silk_is_recognised_by_its_header_at_the_start_or_after_wechats_byte()
    {
        Assert.True(SilkAudio.IsSilk("#!SILK_V3\x0c\0"u8));
        Assert.True(SilkAudio.IsSilk("\x02#!SILK_V3"u8));
        Assert.False(SilkAudio.IsSilk("#!AMR\n"u8));
        Assert.False(SilkAudio.IsSilk("\x02"u8));
        Assert.False(SilkAudio.IsSilk([]));
    }

    [Fact]
    public void The_wav_header_describes_16_bit_mono_pcm()
    {
        var h = SilkAudio.WavHeader(48000, 24000);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(h, 0, 4));
        Assert.Equal(36 + 48000, BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(h.AsSpan(22)));
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(h.AsSpan(34)));
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(40)));
    }

    [Fact]
    public void A_silk_file_decodes_to_its_length_of_audio()
    {
        using var input = File.OpenRead(Tone);
        using var wav = new MemoryStream();
        var (samples, rejected) = SilkAudio.Decode(input, wav);
        Assert.Equal(0, rejected);
        Assert.InRange(samples, 24000, 24000 + 480); // the encoder pads to whole 20 ms frames
        Assert.Equal(44 + samples * 2, wav.Length);
        var pcm = wav.ToArray().AsSpan(44 + 4800 * 2, 2400 * 2); // 0.1 s from 0.2 s in, past the codec's start-up
        var peak = 0;
        var crossings = 0;
        short last = 0;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var s = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            peak = Math.Max(peak, Math.Abs((int)s));
            if (i > 0 && (last < 0) != (s < 0)) crossings++;
            last = s;
        }
        Assert.InRange(peak, 4000, 12000); // encoded at 8000
        Assert.InRange(crossings, 80, 96); // 440 Hz: 88 sign changes in 0.1 s
    }

    [Fact]
    public void Something_that_is_not_silk_is_refused()
    {
        using var wav = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => SilkAudio.Decode(new MemoryStream("#!AMR\n\0\0\0\0\0"u8.ToArray()), wav));
        Assert.Throws<InvalidDataException>(() => SilkAudio.Decode(new MemoryStream("\x02#!SILK_V3\xff\xff"u8.ToArray()), wav)); // header, no audio
    }

    [Fact]
    public void Playable_decodes_once_into_the_cache_and_lists_it_and_leaves_other_files_alone()
    {
        var dir = Directory.CreateTempSubdirectory("silk").FullName;
        try
        {
            var original = Path.Combine(dir, "msg_x.amr");
            File.Copy(Tone, original);
            var before = File.ReadAllBytes(original);
            var cache = Path.Combine(dir, "_reader_audio");

            var (wav, sha) = SilkAudio.Playable(Ref(original), [cache]);
            Assert.Equal(cache, Path.GetDirectoryName(wav));
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(before)), sha);
            Assert.Equal(sha + ".wav", Path.GetFileName(wav));
            var written = File.GetLastWriteTimeUtc(wav);

            Assert.Equal((wav, sha), SilkAudio.Playable(Ref(original), [cache])); // a hit: not decoded again
            Assert.Equal(written, File.GetLastWriteTimeUtc(wav));
            Assert.Equal(before, File.ReadAllBytes(original));

            var index = File.ReadAllLines(Path.Combine(cache, SilkAudio.IndexName));
            Assert.Equal(2, index.Length);
            Assert.StartsWith(Path.GetFileNameWithoutExtension(wav) + "\t/data/voice2/msg_x.amr\t2981\t", index[1]);
            Assert.Empty(Directory.GetFiles(cache, "*.tmp"));

            var other = Path.Combine(dir, "real.amr");
            File.WriteAllBytes(other, "#!AMR\n\0\0"u8.ToArray());
            Assert.Equal((other, (string?)null), SilkAudio.Playable(Ref(other), [cache]));

            // speech to text gets the kept WAV for SILK, the file itself otherwise
            var (audio, name, silk) = SilkAudio.ForTranscription(Ref(original), [cache]);
            Assert.Equal(File.ReadAllBytes(wav), audio);
            Assert.Equal(("msg_x.wav", sha), (name, silk));
            Assert.Equal(written, File.GetLastWriteTimeUtc(wav));
            (audio, name, silk) = SilkAudio.ForTranscription(Ref(other), [cache]);
            Assert.Equal(File.ReadAllBytes(other), audio);
            Assert.Equal(("real.amr", (string?)null), (name, silk));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_case_keeps_its_decoded_audio_beside_it_and_a_portable_case_carries_only_the_chosen_ones()
    {
        using var c = new SyntheticCase();
        File.Copy(Tone, Path.Combine(c.Report, "media", "bbb.png"), true); // the chat's second attachment, SILK by content
        string sha;
        using (var s = CaseSession.Open(c.Report))
        {
            Assert.Equal([Path.Combine(s.RootDir, SilkAudio.FolderName)], SilkAudio.Folders(s));
            using var con = s.OpenDb();
            var m = new MediaResolver(s).Get(con, "ref2")!;
            Assert.Equal(SilkAudio.Folders(s), m.AudioFolders);
            (_, sha) = SilkAudio.Playable(m, m.AudioFolders!);
            Assert.True(File.Exists(Path.Combine(s.RootDir, SilkAudio.FolderName, sha + ".wav")));
            PortableCase.Build(s, Path.Combine(c.Root, "all.zip"), new PortableSelection(), null);
            PortableCase.Build(s, Path.Combine(c.Root, "nomedia.zip"), new PortableSelection(Media: false), null);
        }
        using (var z = System.IO.Compression.ZipFile.OpenRead(Path.Combine(c.Root, "nomedia.zip")))
            Assert.DoesNotContain(z.Entries, e => e.FullName.Contains(SilkAudio.FolderName));

        using var pc = CaseSession.Open(Path.Combine(c.Root, "all.zip"));
        Assert.True(pc.Package!.Intact);
        var carried = Path.Combine(pc.RootDir, SilkAudio.FolderName);
        Assert.Equal(carried, SilkAudio.Folders(pc)[1]);
        Assert.NotEqual(carried, SilkAudio.Folders(pc)[0]); // new decodes go outside the unpacked case
        Assert.Equal(2, File.ReadAllLines(Path.Combine(carried, SilkAudio.IndexName)).Length);
        using var pcon = pc.OpenDb();
        var pm = new MediaResolver(pc).Get(pcon, "ref2")!;
        Assert.Equal((Path.Combine(carried, sha + ".wav"), sha), SilkAudio.Playable(pm, pm.AudioFolders!)); // played without decoding again
    }
}
