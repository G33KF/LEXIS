using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AleappReader.Core;

/// <summary>The MCP server inside the LEXIS that is open: Streamable HTTP (JSON answers, no event stream) on the loopback
/// only, at a path holding a secret token, serving whatever case <c>current</c> gives at each request (none: the tools
/// say to open one). LM Studio and other URL clients connect to it; stdio-only clients go through <see cref="Bridge"/>.</summary>
public sealed class McpHttpServer : IDisposable
{
    readonly HttpListener _l = new();
    readonly Func<McpServer?> _current;
    readonly string _path;
    public string Url { get; }

    /// <exception cref="HttpListenerException">The port is taken.</exception>
    public McpHttpServer(Func<McpServer?> current, int port, string token)
    {
        _current = current;
        _path = "/mcp/" + token;
        Url = Address(port, token);
        // HttpListener answers only requests whose Host is one of these: a page that rebinds its own name to 127.0.0.1 is refused
        _l.Prefixes.Add($"http://127.0.0.1:{port}/");
        _l.Prefixes.Add($"http://localhost:{port}/");
        _l.Start();
        _ = Loop();
    }

    public static string Address(int port, string token) => $"http://127.0.0.1:{port}/mcp/{token}";

    /// <summary>A new secret for the server's path: whoever does not have it gets 404.</summary>
    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    async Task Loop()
    {
        // ponytail: one request at a time, MCP clients wait for each answer anyway
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            try { await Task.Run(() => Serve(c)); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { } // the client went away
        }
    }

    void Serve(HttpListenerContext c)
    {
        var (req, res) = (c.Request, c.Response);
        using (res)
        {
            if (req.Url?.AbsolutePath.TrimEnd('/') != _path) { res.StatusCode = 404; return; }
            // a browser page posting here carries its own origin
            if (req.Headers["Origin"] is { Length: > 0 } o && !(Uri.TryCreate(o, UriKind.Absolute, out var u) && u.IsLoopback)) { res.StatusCode = 403; return; }
            if (req.HttpMethod != "POST") { res.StatusCode = 405; res.AddHeader("Allow", "POST"); return; }
            string body;
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            var reply = _current() is { } mcp ? mcp.Respond(body) : McpServer.RespondIdle(body);
            if (reply is null) { res.StatusCode = 202; return; }
            var bytes = Encoding.UTF8.GetBytes(reply);
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes);
        }
    }

    public void Dispose() => _l.Close();

    /// <summary>For a client that only starts servers on stdio (Claude Desktop): each line read is posted to the LEXIS
    /// that is open, its answer written back. LEXIS closed or its server off: an error answer saying so.</summary>
    public static async Task Bridge(TextReader input, TextWriter output, string url, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        while (await input.ReadLineAsync(ct) is { } line)
        {
            if (line.Trim() == "") continue;
            string? reply;
            try
            {
                using var res = await http.PostAsync(url, new StringContent(line, Encoding.UTF8, "application/json"), ct);
                reply = res.StatusCode == HttpStatusCode.Accepted ? null
                    : res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync(ct)
                    : Failed(line, $"LEXIS answered {(int)res.StatusCode}: its MCP address changed. Copy the configuration again from LEXIS › AI settings.");
            }
            catch (HttpRequestException)
            {
                reply = Failed(line, "LEXIS is not open, or its MCP server is off: open LEXIS, turn the server on in AI settings › MCP, and ask again.");
            }
            if (reply is { Length: > 0 }) { await output.WriteLineAsync(reply.ReplaceLineEndings("")); await output.FlushAsync(ct); }
        }
    }

    // a notification gets no answer, even an error
    static string? Failed(string line, string message)
    {
        JsonNode? id = null;
        try { id = (JsonNode.Parse(line) as JsonObject)?["id"]; } catch (JsonException) { }
        return id is null ? null : McpServer.ErrorReply(id, -32000, message);
    }
}

/// <summary>The MCP clients whose configuration file LEXIS can add itself to, as the "lexis" server.</summary>
public static class McpClients
{
    public const string Name = "lexis";

    public static string LmStudio => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "mcp.json");

    /// <summary>Claude Desktop's file; the Store version keeps it in its package folder, where it reads it from.</summary>
    public static string ClaudeDesktop
    {
        get
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            var store = Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages, "Claude_*").Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude")).FirstOrDefault(Directory.Exists)
                : null;
            return Path.Combine(store ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude"), "claude_desktop_config.json");
        }
    }

    /// <summary>Whether the client is installed: its configuration folder exists.</summary>
    public static bool Installed(string file) => Directory.Exists(Path.GetDirectoryName(file));

    /// <summary>The "lexis" entry the file holds now, if any.</summary>
    public static JsonNode? Entry(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?["mcpServers"]?[Name] : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Writes the "lexis" entry in the file's mcpServers, leaving every other setting as it is; the file as it
    /// was before LEXIS first added itself is kept beside it (".lexis-backup"). Returns the backup's path, or null when there was no file.</summary>
    /// <exception cref="JsonException">The file is not JSON: left untouched.</exception>
    public static string? Add(string file, JsonObject entry)
    {
        var old = File.Exists(file) ? File.ReadAllText(file) : null;
        var root = old is null || old.Trim() == "" ? new JsonObject() : JsonNode.Parse(old) as JsonObject ?? throw new JsonException("Not a JSON object: " + file);
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = [];
        servers[Name] = entry;
        // the first one kept: adding again never overwrites the file as it was before LEXIS
        var backup = old is null ? null : file + ".lexis-backup";
        if (backup is not null && !File.Exists(backup)) File.WriteAllText(backup, old);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return backup;
    }

    /// <summary>An entry for a client that connects to a URL (LM Studio).</summary>
    public static JsonObject UrlEntry(string url) => new() { ["url"] = url };

    /// <summary>An entry for a client that starts the server itself on stdio (Claude Desktop): LEXIS as the bridge.</summary>
    public static JsonObject BridgeEntry(string exe) => new() { ["command"] = exe, ["args"] = new JsonArray("--mcp-bridge") };
}
