using System.IO;
using System.IO.Compression;

namespace AleappReader.App;

/// <summary>The ALEAPP and iLEAPP builds (PyInstaller one-folder, zipped by build.ps1) embedded in this exe: unpacked
/// once per build into %LOCALAPPDATA%\AleappReader\engine, so no LEAPP folder or Python is needed on the machine.
/// <c>tool</c> is "aleapp" or "ileapp".</summary>
static class BundledEngine
{
    static string Resource(string tool) => tool + "-engine.zip";

    static Stream? Open(string tool) => typeof(BundledEngine).Assembly.GetManifestResourceStream(Resource(tool));

    public static bool Available(string tool) => typeof(BundledEngine).Assembly.GetManifestResourceInfo(Resource(tool)) is not null;

    // ponytail: the zip's length names the build; a rebuild of identical size would reuse the old folder
    static string? Dir(string tool)
    {
        using var s = Open(tool);
        // ALEAPP keeps its pre-iLEAPP folder name, so an existing unpack is reused.
        return s is null ? null : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AleappReader", "engine", (tool == "aleapp" ? "" : tool + "-") + s.Length.ToString("x"));
    }

    /// <summary>The tool's exe if this build is already unpacked, else null.</summary>
    public static string? Ready(string tool) =>
        Dir(tool) is { } d && File.Exists(Path.Combine(d, tool + ".exe")) ? Path.Combine(d, tool + ".exe") : null;

    /// <summary>Unpacks the engine if needed (into a temp folder, then renamed, so a half-unpacked one is never used).</summary>
    public static string Ensure(string tool)
    {
        if (Ready(tool) is { } exe) return exe;
        var dir = Dir(tool) ?? throw new InvalidOperationException($"This build carries no {tool} engine.");
        var tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var s = Open(tool)!) ZipFile.ExtractToDirectory(s, tmp);
        try { Directory.Move(tmp, dir); }
        catch (IOException) when (Ready(tool) is not null) { Directory.Delete(tmp, true); } // another instance won the race
        return Ready(tool) ?? throw new FileNotFoundException($"The bundled engine has no {tool}.exe.", dir);
    }
}
