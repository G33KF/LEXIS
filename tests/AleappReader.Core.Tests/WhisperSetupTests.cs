using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace AleappReader.Core.Tests;

public class WhisperSetupTests
{
    static string Sha(char c) => new(c, 64);

    [Fact]
    public void Each_build_comes_from_the_newest_release_that_has_it_with_a_sha256()
    {
        var json = $$"""
        [
          {"tag_name":"v1.9.5","published_at":"2026-05-02T00:00:00Z","assets":[]},
          {"tag_name":"b9999","draft":true,"published_at":"2026-06-01T00:00:00Z","assets":[{"name":"whisper-bin-x64.zip","size":1,"digest":"sha256:{{Sha('9')}}","browser_download_url":"https://x/draft"}]},
          {"tag_name":"b5454","prerelease":true,"published_at":"2026-05-01T00:00:00Z","assets":[
            {"name":"whisper-bin-x64.zip","size":10,"digest":"sha256:{{Sha('A')}}","browser_download_url":"https://x/cpu"},
            {"name":"whisper-blas-bin-x64.zip","size":20,"browser_download_url":"https://x/blas-no-digest"},
            {"name":"whisper-bin-win-cuda-12.4.0-x64.zip","size":30,"digest":"sha256:{{Sha('c')}}","browser_download_url":"https://x/cuda12"},
            {"name":"whisper-bin-ubuntu-x64.tar.gz","size":40,"digest":"sha256:{{Sha('d')}}","browser_download_url":"https://x/linux"}]},
          {"tag_name":"b5130","published_at":"2026-04-01T00:00:00Z","assets":[
            {"name":"whisper-bin-x64.zip","size":11,"digest":"sha256:{{Sha('e')}}","browser_download_url":"https://x/old-cpu"},
            {"name":"whisper-blas-bin-x64.zip","size":21,"digest":"sha256:{{Sha('f')}}","browser_download_url":"https://x/blas"},
            {"name":"whisper-cublas-11.8.0-bin-x64.zip","size":31,"digest":"sha256:{{Sha('1')}}","browser_download_url":"https://x/cuda11"}]}
        ]
        """;
        var b = WhisperSetup.ParseBuilds(json).ToDictionary(x => x.Variant);
        Assert.Equal(["CPU", "CPU with OpenBLAS", "NVIDIA CUDA 12", "NVIDIA CUDA 11"], b.Keys);
        Assert.Equal(("b5454", "https://x/cpu", Sha('a')), (b["CPU"].Tag, b["CPU"].File.Url, b["CPU"].File.Sha256));
        Assert.Equal(("b5130", "https://x/blas"), (b["CPU with OpenBLAS"].Tag, b["CPU with OpenBLAS"].File.Url));
        Assert.Equal("whisper-bin-win-cuda-12.4.0-x64.zip", b["NVIDIA CUDA 12"].File.Name);
        Assert.Equal("whisper-cublas-11.8.0-bin-x64.zip", b["NVIDIA CUDA 11"].File.Name);
        Assert.Empty(WhisperSetup.ParseBuilds("[]"));
    }

    [Fact]
    public void Models_are_the_top_level_ggml_files_with_their_lfs_sha256()
    {
        var json = $$$"""
        [
          {"type":"file","path":".gitattributes","size":1477},
          {"type":"file","path":"ggml-tiny.bin","size":77691713,"lfs":{"oid":"{{{Sha('b')}}}","size":77691713}},
          {"type":"file","path":"ggml-large-v3-turbo-q5_0.bin","size":574041195,"lfs":{"oid":"{{{Sha('3')}}}"}},
          {"type":"file","path":"ggml-no-lfs.bin","size":5},
          {"type":"file","path":"sub/ggml-x.bin","size":5,"lfs":{"oid":"{{{Sha('4')}}}"}},
          {"type":"file","path":"ggml-tiny-encoder.mlmodelc.zip","size":5,"lfs":{"oid":"{{{Sha('5')}}}"}}
        ]
        """;
        var m = WhisperSetup.ParseModels(json);
        Assert.Equal(["ggml-tiny.bin", "ggml-large-v3-turbo-q5_0.bin"], m.Select(x => x.Name));
        Assert.Equal(new WhisperSetup.Download("ggml-tiny.bin", 77691713, Sha('b'), "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin"), m[0]);
    }

    sealed class Serve(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task A_download_is_kept_only_when_its_size_and_sha256_match()
    {
        var dir = Directory.CreateTempSubdirectory("whisper").FullName;
        try
        {
            var body = "model bytes"u8.ToArray();
            using var http = new HttpClient(new Serve(body));
            var good = new WhisperSetup.Download("ggml-x.bin", body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), "https://x/m");
            var path = await WhisperSetup.Fetch(good, dir, null, CancellationToken.None, http);
            Assert.Equal(body, File.ReadAllBytes(path));

            File.Delete(path);
            var e = await Assert.ThrowsAsync<AiException>(() => WhisperSetup.Fetch(good with { Sha256 = Sha('0') }, dir, null, CancellationToken.None, http));
            Assert.Contains("SHA-256", e.Message);
            await Assert.ThrowsAsync<AiException>(() => WhisperSetup.Fetch(good with { Size = 99 }, dir, null, CancellationToken.None, http));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    // 1 s of a 440 Hz tone, encoded by a full ffmpeg (MSYS2's: libopus, libopencore_amrnb, aac, libmp3lame)
    [Theory]
    [InlineData("opus")]
    [InlineData("amr")]
    [InlineData("m4a")]
    [InlineData("mp3")]
    public async Task The_bundled_ffmpeg_turns_phone_audio_into_the_wav_whisper_reads(string ext)
    {
        var ff = WhisperCpp.BundledFfmpeg;
        Assert.NotNull(ff);
        var dir = Directory.CreateTempSubdirectory("ffmpeg").FullName;
        try
        {
            var wav = Path.Combine(dir, "out.wav");
            await WhisperCpp.ToWav(ff, Path.Combine(AppContext.BaseDirectory, "Fixtures", "tone_440hz_1s." + ext), wav, CancellationToken.None);
            var h = File.ReadAllBytes(wav);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(h, 0, 4));
            Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(h.AsSpan(22)));     // mono
            Assert.Equal(16000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(24))); // 16 kHz
            Assert.InRange(h.Length, 30000, 36000); // about one second
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unpacking_puts_the_program_in_bin_and_models_go_beside_it()
    {
        var dir = Directory.CreateTempSubdirectory("whisper").FullName;
        try
        {
            var zip = Path.Combine(dir, "w.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var f in new[] { "Release/whisper-cli.exe", "Release/ggml.dll" })
                    using (var w = new StreamWriter(z.CreateEntry(f).Open())) w.Write(f);
            var folder = Path.Combine(dir, "whisper");
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            File.WriteAllText(Path.Combine(folder, "bin", "stale.dll"), "");

            var exe = WhisperSetup.Unpack(zip, folder);
            Assert.Equal(Path.Combine(folder, "bin", "whisper-cli.exe"), exe);
            Assert.Equal(["ggml.dll", "whisper-cli.exe"], Directory.GetFiles(Path.Combine(folder, "bin")).Select(Path.GetFileName).Order());
            Assert.False(Directory.Exists(Path.Combine(folder, "bin.new")));

            var models = WhisperSetup.ModelFolder(exe);
            Assert.Equal(Path.Combine(folder, "models"), models);
            Directory.CreateDirectory(models);
            Assert.Contains(WhisperCpp.ModelFolders(exe), f => Path.GetFullPath(f) == models);
            Assert.Equal(Path.Combine(dir, "models"), WhisperSetup.ModelFolder(Path.Combine(dir, "whisper-cli.exe")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
