using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>
/// Sets up whisper.cpp from its public sources, never bundled: the Windows builds of ggml-org/whisper.cpp's GitHub releases
/// (each from the newest release that has it: the binaries are on build tags such as b5454, not on the version tags) and
/// the ggml models of huggingface.co/ggerganov/whisper.cpp. A file is used only once its size and SHA-256 match what its
/// source publishes; install.log in the folder records what was installed, from where, with its SHA-256.
/// </summary>
public static class WhisperSetup
{
    public const string Releases = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=30";
    public const string ModelTree = "https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main";
    const string ModelFile = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    const string BuildDownloads = "https://github.com/ggml-org/whisper.cpp/releases/download/";
    public const string LogName = "install.log";
    /// <summary>Written by <see cref="Unpack"/> inside the bin folder it installs: the one bin it may replace.</summary>
    public const string Marker = ".lexis-whisper";
    public static readonly string DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "whisper");

    public sealed record Download(string Name, long Size, string Sha256, string Url);
    public sealed record Build(string Variant, string Tag, Download File);

    static readonly (string Variant, Regex Name)[] Variants =
    [
        ("CPU", new(@"^whisper-bin-x64\.zip$")),
        ("CPU with OpenBLAS", new(@"^whisper-blas-bin-x64\.zip$")),
        ("NVIDIA CUDA 12", new(@"^whisper-(bin-win-cuda-12[\d.]*-x64|cublas-12[\d.]*-bin-x64)\.zip$")),
        ("NVIDIA CUDA 11", new(@"^whisper-(bin-win-cuda-11[\d.]*-x64|cublas-11[\d.]*-bin-x64)\.zip$")),
    ];

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "LEXIS" } } };

    /// <summary>Whether an NVIDIA driver is installed (its CUDA library is in System32).</summary>
    public static bool HasNvidia => File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    /// <summary>Per variant, its zip from the newest published release that has it with a SHA-256.</summary>
    public static List<Build> ParseBuilds(string json)
    {
        var releases = (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonNode>()
            .Where(r => (bool?)r["draft"] != true).OrderByDescending(r => (string?)r["published_at"], StringComparer.Ordinal).ToList();
        var builds = new List<Build>();
        foreach (var (variant, name) in Variants)
            foreach (var r in releases)
                if ((r["assets"] as JsonArray ?? []).FirstOrDefault(a => name.IsMatch((string?)a?["name"] ?? "")) is { } a
                    && (string?)a["digest"] is { } d && Regex.IsMatch(d, "^sha256:[0-9a-fA-F]{64}$") && (string?)a["browser_download_url"] is { } url
                    && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsoluteUri.StartsWith(BuildDownloads, StringComparison.Ordinal)) // only the project's own release files, over HTTPS, after ".." is resolved
                {
                    builds.Add(new(variant, (string?)r["tag_name"] ?? "", new((string)a["name"]!, (long?)a["size"] ?? 0, d[7..].ToLowerInvariant(), url)));
                    break;
                }
        return builds;
    }

    /// <summary>The ggml-*.bin models of the repository's top folder, with their size and SHA-256 (their LFS id).</summary>
    public static List<Download> ParseModels(string json) =>
        (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonNode>()
            .Select(f => (Name: (string?)f["path"] ?? "", Size: (long?)f["size"] ?? 0, Sha: (string?)f["lfs"]?["oid"] ?? ""))
            .Where(f => Regex.IsMatch(f.Name, @"^ggml-[\w.-]+\.bin$") && f.Size > 0 && Regex.IsMatch(f.Sha, "^[0-9a-f]{64}$"))
            .Select(f => new Download(f.Name, f.Size, f.Sha, ModelFile + f.Name)).ToList();

    public static async Task<List<Build>> Builds(CancellationToken ct) => ParseBuilds(await List(Releases, ct));
    public static async Task<List<Download>> Models(CancellationToken ct) => ParseModels(await List(ModelTree, ct));

    static async Task<string> List(string url, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(30));
        return await Http.GetStringAsync(url, t.Token);
    }

    /// <summary>Downloads <paramref name="d"/> into <paramref name="folder"/> and returns its path once its size and SHA-256
    /// match the published ones. Until then it is a .part file, deleted when they do not.</summary>
    public static async Task<string> Fetch(Download d, string folder, IProgress<long>? progress, CancellationToken ct, HttpClient? http = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, d.Name);
        var part = path + ".part";
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            using (var resp = await (http ?? Http).GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buf = new byte[1 << 16];
                for (int n; (n = await src.ReadAsync(buf, ct)) > 0;)
                {
                    sha.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (got > d.Size) throw new AiException($"{d.Name}: more than the {d.Size:N0} bytes published; stopped.");
                    progress?.Report(got);
                }
            }
            if (got != d.Size) throw new AiException($"{d.Name}: {got:N0} bytes received, {d.Size:N0} published.");
            var hex = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (hex != d.Sha256) throw new AiException($"{d.Name}: its SHA-256 {hex} is not the published {d.Sha256}.");
            File.Move(part, path, true);
            return path;
        }
        catch { File.Delete(part); throw; }
    }

    /// <summary>Unpacks a build's zip into <paramref name="folder"/>\bin, replacing an earlier one, and returns its
    /// whisper-cli.exe. The folder holding it becomes bin, so the models folder beside bin is kept across updates.
    /// A bin LEXIS did not install (no <see cref="Marker"/> inside it) is never replaced: the folder is refused.</summary>
    public static string Unpack(string zip, string folder)
    {
        var bin = Path.Combine(folder, "bin");
        if (Directory.Exists(bin) && !File.Exists(Path.Combine(bin, Marker)))
            throw new AiException($"{folder} already has a bin folder that LEXIS did not install; choose an empty folder for whisper.cpp.");
        var fresh = Path.Combine(folder, $"bin.{Guid.NewGuid():N}.new"); // its own, never a folder already there
        try
        {
            ZipFile.ExtractToDirectory(zip, fresh); // refuses entries that would land outside it
            var exe = Directory.EnumerateFiles(fresh, "whisper-cli.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new AiException($"{Path.GetFileName(zip)} has no whisper-cli.exe.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, Marker), $"installed by LEXIS from {Path.GetFileName(zip)}\n");
            if (Directory.Exists(bin)) Directory.Delete(bin, true);
            Directory.Move(Path.GetDirectoryName(exe)!, bin);
            return Path.Combine(bin, "whisper-cli.exe");
        }
        finally { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); }
    }

    /// <summary>Where models are downloaded for <paramref name="exe"/>: beside its bin folder when it is in one (as
    /// <see cref="Unpack"/> puts it), else in a models folder beside it. Both are among <see cref="WhisperCpp.ModelFolders"/>.</summary>
    public static string ModelFolder(string exe)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        return Path.GetFileName(dir).Equals("bin", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Path.GetDirectoryName(dir)!, "models") : Path.Combine(dir, "models");
    }

    /// <summary>Runs whisper-cli: with a model, on one second of silence (the program starts, its libraries load, the model
    /// is read); without, its help.</summary>
    public static async Task Check(string exe, string? model, CancellationToken ct)
    {
        if (model is null) await WhisperCpp.Exec(exe, ["-h"], Path.GetDirectoryName(exe)!, "whisper-cli", WhisperCpp.FfmpegLimit, ct);
        else await WhisperCpp.Transcribe(exe, model, "silence.wav", [.. SilkAudio.WavHeader(32000, 16000), .. new byte[32000]], ct);
    }

    /// <summary>Adds a line to <paramref name="folder"/>\install.log: when, the file, its size and SHA-256, its source, where it went.</summary>
    public static void Log(string folder, Download d, string path) =>
        File.AppendAllText(Path.Combine(folder, LogName), $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\t{d.Name}\t{d.Size}\tsha256 {d.Sha256}\t{d.Url}\t{path}\n");
}
