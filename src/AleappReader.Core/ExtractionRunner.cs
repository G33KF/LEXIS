using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>Where a LEAPP lives (Tool "aleapp" for Android, "ileapp" for iOS): a source checkout run by Python, or a
/// built exe. The two share their command line, plugin loader and LAVA output.</summary>
public sealed record AleappInstall(string? PythonExe, string? AleappDir, string? AleappExe, string Tool = "aleapp")
{
    public string Name => Tool == "ileapp" ? "iLEAPP" : "ALEAPP";
    public bool IsSource => !string.IsNullOrEmpty(PythonExe) && !string.IsNullOrEmpty(AleappDir) &&
                            File.Exists(Path.Combine(AleappDir, Tool + ".py"));
    public bool IsValid => IsSource || (!string.IsNullOrEmpty(AleappExe) && File.Exists(AleappExe));

    /// <summary>Finds a checkout beside the app or its parents, preferring its own .venv.</summary>
    public static AleappInstall Detect(string startDir, string tool = "aleapp")
    {
        for (var d = new DirectoryInfo(startDir); d is not null; d = d.Parent)
        {
            var dir = Path.Combine(d.FullName, tool.ToUpperInvariant());
            if (!File.Exists(Path.Combine(dir, tool + ".py"))) continue;
            var venv = Path.Combine(dir, ".venv", "Scripts", "python.exe");
            return new AleappInstall(File.Exists(venv) ? venv : "python", dir, null, tool);
        }
        return new AleappInstall(null, null, null, tool);
    }
}

public sealed record PluginInfo(string Name, string Category, string Artifact, string Module);

/// <summary>Where a LEAPP run stands, read off the lines ALEAPP and iLEAPP print around each artifact:
/// "[n/N] name [module] artifact started", "No file found", "name [module] artifact completed in"/"failed after".</summary>
public sealed partial class RunProgress
{
    public int Done { get; private set; }
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public int NoFiles { get; private set; }
    /// <summary>The artifact running now, "name [module]"; empty before the first and after the last.</summary>
    public string Current { get; private set; } = "";
    public string Phase => Total == 0 ? "Reading the input" : Done < Total ? "Parsing artifacts" : "Writing the report";

    [GeneratedRegex(@"^\[(\d+)/(\d+)\] (.+ \[[^\]]+\]) artifact started")] private static partial Regex Started();
    [GeneratedRegex(@" \[[^\]]+\] artifact (completed in|failed after) ")] private static partial Regex Ended();
    [GeneratedRegex(@"^\[(\d+)/(\d+)\] ")] private static partial Regex Counter(); // a start line worded otherwise still sets the total

    /// <summary>A copy for the UI thread, which reads it while the next lines arrive.</summary>
    public RunProgress Snapshot() => (RunProgress)MemberwiseClone();

    /// <summary>Takes one log line; true when the state changed.</summary>
    public bool Feed(string line)
    {
        if (Started().Match(line) is { Success: true } s)
        {
            Total = int.Parse(s.Groups[2].Value);
            Current = s.Groups[3].Value;
            return true;
        }
        if (Total == 0 && Counter().Match(line) is { Success: true } c) { Total = int.Parse(c.Groups[2].Value); return true; }
        if (line == "No file found") { NoFiles++; return true; }
        if (Ended().Match(line) is not { Success: true } e) return false;
        Done++;
        if (e.Groups[1].Value == "failed after") Failed++;
        if (Done >= Total) Current = "";
        return true;
    }
}

public sealed record ExtractionOptions(string InputType, string InputPath, string OutputDir,
    IReadOnlyCollection<string>? Plugins, IReadOnlyDictionary<string, string>? CaseData, string? ImagePassword, string? Keychain = null);

/// <summary>Runs ALEAPP or iLEAPP as a child process. Arguments go through ArgumentList: no shell, no quoting bugs.</summary>
public static partial class ExtractionRunner
{
    public static string[] InputTypes(string tool) => tool == "ileapp" ? ["fs", "tar", "zip", "gz", "itunes", "raw"] : ["fs", "tar", "zip", "gz", "raw", "ab"];

    const string ListScript = """
        import json, sys
        sys.path.insert(0, '.')
        from scripts.plugin_loader import PluginLoader
        out = [{"name": p.name, "category": p.category or "", "artifact": (p.artifact_info or {}).get("name") or p.name,
                "module": p.module_name} for p in PluginLoader().plugins]
        print("@@PLUGINS@@" + json.dumps(out))
        """;

    /// <summary>The file build.ps1 writes beside a bundled aleapp.exe: ListScript's output for that build.</summary>
    public const string PluginsFile = "plugins.json";

    /// <summary>Lists ALEAPP's plugins: from a source checkout, or from plugins.json beside a built aleapp.exe.</summary>
    public static async Task<IReadOnlyList<PluginInfo>> ListPluginsAsync(AleappInstall inst, CancellationToken ct = default)
    {
        if (!inst.IsSource)
        {
            var json = inst.AleappExe is null ? null : Path.Combine(Path.GetDirectoryName(inst.AleappExe)!, PluginsFile);
            return json is not null && File.Exists(json) ? ParsePlugins(await File.ReadAllTextAsync(json, ct)) : [];
        }
        var script = Path.Combine(Path.GetTempPath(), $"aleapp_list_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(script, ListScript, ct);
        try
        {
            var psi = Psi(inst.PythonExe!, inst.AleappDir!);
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var line = (await stdout).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("@@PLUGINS@@"));
            if (line is null) throw new InvalidOperationException(inst.Name + " did not list its plugins. Check the Python environment.");
            return ParsePlugins(line["@@PLUGINS@@".Length..]);
        }
        finally { File.Delete(script); }
    }

    static List<PluginInfo> ParsePlugins(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => new PluginInfo(
            e.GetProperty("name").GetString()!, e.GetProperty("category").GetString()!,
            e.GetProperty("artifact").GetString()!, e.GetProperty("module").GetString()!))
            .OrderBy(x => x.Category).ThenBy(x => x.Artifact).ToList();
    }

    /// <summary>Runs the extraction and returns the manifest it produced.</summary>
    public static async Task<string> RunAsync(AleappInstall inst, ExtractionOptions o, IProgress<string> log,
        IProgress<RunProgress>? progress = null, CancellationToken ct = default)
    {
        if (!inst.IsValid) throw new InvalidOperationException(inst.Name + " location is not configured.");
        if (!InputTypes(inst.Tool).Contains(o.InputType)) throw new ArgumentException("Unknown input type: " + o.InputType);
        Directory.CreateDirectory(o.OutputDir);
        var temps = new List<string>();
        try
        {
            var psi = inst.IsSource ? Psi(inst.PythonExe!, inst.AleappDir!) : Psi(inst.AleappExe!, Path.GetDirectoryName(inst.AleappExe)!);
            if (inst.IsSource) psi.ArgumentList.Add(Path.Combine(inst.AleappDir!, inst.Tool + ".py"));
            foreach (var a in new[] { "-t", o.InputType, "-i", o.InputPath, "-o", o.OutputDir }) psi.ArgumentList.Add(a);

            if (o.Plugins is { Count: > 0 })
            {
                var profile = Path.Combine(Path.GetTempPath(), $"reader_{Guid.NewGuid():N}.{(inst.Tool == "ileapp" ? "ilprofile" : "alprofile")}");
                await File.WriteAllTextAsync(profile, JsonSerializer.Serialize(new { leapp = inst.Tool, format_version = 1, plugins = o.Plugins }), ct);
                temps.Add(profile);
                psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(profile);
            }
            if (o.CaseData is { Count: > 0 })
            {
                var cd = Path.Combine(Path.GetTempPath(), $"reader_{Guid.NewGuid():N}.lcasedata");
                await File.WriteAllTextAsync(cd, JsonSerializer.Serialize(new { leapp = "case_data", case_data_values = o.CaseData }), ct);
                temps.Add(cd);
                psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(cd);
            }
            if (!string.IsNullOrEmpty(o.ImagePassword))
            {
                // Passed through the environment, never on the command line where other processes can read it.
                psi.Environment["ALEAPP_READER_IMAGE_PW"] = o.ImagePassword;
                psi.ArgumentList.Add("--image_password_env"); psi.ArgumentList.Add("ALEAPP_READER_IMAGE_PW");
            }
            if (!string.IsNullOrEmpty(o.Keychain))
            {
                // The keys captured beside the extraction: an iOS keychain, or an Android keystore export
                if (!File.Exists(o.Keychain)) throw new ArgumentException("Key file not found: " + o.Keychain);
                psi.ArgumentList.Add(inst.Tool == "ileapp" ? "--keychain" : "--keystore"); psi.ArgumentList.Add(o.Keychain);
            }

            var started = DateTime.UtcNow;
            log.Report("> " + Path.GetFileName(psi.FileName) + " " + string.Join(" ", psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var state = new RunProgress();
            void OnLine(string? line)
            {
                if (line is null) return;
                log.Report(line);
                lock (state) if (state.Feed(line)) progress?.Report(state.Snapshot()); // stdout and stderr arrive on two threads
            }
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.Start();
            p.StandardInput.Close(); // nothing may prompt: a missing password fails instead of hanging
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            if (p.ExitCode != 0) throw new InvalidOperationException($"{inst.Name} exited with code {p.ExitCode}.");

            var manifest = CaseSession.ManifestNames
                .SelectMany(n => Directory.EnumerateFiles(o.OutputDir, n, SearchOption.AllDirectories))
                .Where(f => File.GetLastWriteTimeUtc(f) >= started.AddMinutes(-1))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return manifest ?? throw new FileNotFoundException(inst.Name + " finished but no LAVA manifest was written.", o.OutputDir);
        }
        finally { foreach (var t in temps) File.Delete(t); }
    }

    [GeneratedRegex("[0-9A-Fa-f]{40}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{16}")] private static partial Regex Udid();

    /// <summary>A keychain plist captured beside the extraction, such as <c>UDID_keychain.plist</c> next to it
    /// or a folder or two up: the one naming the device's UDID found in the extraction's path, else the only one; null
    /// when there is none or several and none names the device. <paramref name="patterns"/> picks another kind of key file,
    /// such as <see cref="KeystorePatterns"/>.</summary>
    public static string? KeychainBeside(string input, params string[] patterns)
    {
        if (patterns.Length == 0) patterns = ["*keychain*.plist"];
        var found = new List<string>();
        var d = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(input));
        for (var i = 0; i < 3 && !string.IsNullOrEmpty(d); i++, d = Path.GetDirectoryName(d))
        {
            try { foreach (var p in patterns) found.AddRange(Directory.EnumerateFiles(d, p)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var udids = Udid().Matches(input).Select(m => m.Value).ToList();
        return found.FirstOrDefault(f => udids.Any(u => Path.GetFileName(f).Contains(u, StringComparison.OrdinalIgnoreCase)))
               ?? (found.Count == 1 ? found[0] : null);
    }

    /// <summary>An Android keystore export captured beside the extraction, such as <c>SERIAL_android_keystore.zip</c>.</summary>
    public static readonly string[] KeystorePatterns = ["*keystore*.zip", "*keystore*.json"];

    /// <summary>The keychain dump a zip extraction carries (a backup_keychain_v2.plist, an iTunes keychain-backup.plist),
    /// which iLEAPP reads when no other is given; null when it carries none.</summary>
    public static string? KeychainInside(string zip)
    {
        try
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(zip);
            return z.Entries.FirstOrDefault(e => e.Name is "backup_keychain_v2.plist" or "keychain-backup.plist")?.FullName;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>What the run said about the keys supplied, from its screen log in the report: iLEAPP's "Keychain:" and
    /// ALEAPP's "Keystore:" lines,
    /// or null when the report keeps no log (a portable case without _HTML).</summary>
    public static IReadOnlyList<string>? KeychainLog(string reportRoot)
    {
        var log = Path.Combine(reportRoot, "_HTML", "_Script_Logs", "Screen_Output.html");
        if (!File.Exists(log)) return null;
        return File.ReadLines(log)
            .SelectMany(l => l.Split("<br>", StringSplitOptions.TrimEntries))
            .Select(l => System.Net.WebUtility.HtmlDecode(l) ?? "")
            .Where(l => l.StartsWith("Keychain:", StringComparison.Ordinal) || l.StartsWith("Keystore:", StringComparison.Ordinal)
                        || l.Contains("BeaconStore", StringComparison.Ordinal))
            .Distinct().ToList();
    }

    static ProcessStartInfo Psi(string exe, string workDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return psi;
    }
}
