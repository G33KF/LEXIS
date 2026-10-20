using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>One indicator of a STIX2 file: its type as the pattern names it (domain-name:value, process:name, app:id, ...).</summary>
public sealed record Indicator(string Type, string Value, string Collection, string File);

/// <summary>A match in an artifact cell, or (Artifact null) in the extraction's own file list.</summary>
public sealed record IocHit(ArtifactInfo? Artifact, long RowId, string Column, string Cell, Indicator Indicator)
{
    public string Where => Artifact?.Name ?? "Extraction file list";
}

/// <summary>
/// Indicators of compromise from STIX2 files (the format of the MVT indicator feeds), matched against every text cell of a
/// case. Clean-room: the pattern keys and matching rules follow MVT's published behaviour, no MVT code is used.
/// Domains (and IPv4) match any host-like token of a cell, or a parent domain of it; URLs match when their host does and
/// the cell contains the URL; every other type (process and file names, file paths, app ids, emails, hashes, properties,
/// profile ids) matches only a whole cell, its last path component, or for file paths a cell holding the path as whole segments.
/// </summary>
public sealed partial class IndicatorSet
{
    readonly Dictionary<string, List<Indicator>> _domains = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Indicator>> _urls = new(StringComparer.OrdinalIgnoreCase); // keyed by host
    readonly Dictionary<string, List<Indicator>> _exact = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<(Indicator, string[])>> _pathsByLeaf = new(StringComparer.OrdinalIgnoreCase);

    public int Count { get; private set; }
    public IReadOnlyList<string> Files { get; private set; } = [];

    [GeneratedRegex(@"([a-z0-9-]+:[a-z0-9_.'\- ]+?)\s*=\s*'((?:[^'\\]|\\.)*)'", RegexOptions.IgnoreCase)]
    private static partial Regex PatternTerm();

    [GeneratedRegex(@"(?<![\w.@-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9][a-z0-9-]{0,62}(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex HostToken();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailToken();

    /// <summary>A set from indicators already in hand (the built-in lists).</summary>
    public static IndicatorSet From(IEnumerable<Indicator> indicators)
    {
        var set = new IndicatorSet();
        foreach (var i in indicators) set.Add(i);
        return set;
    }

    [GeneratedRegex(@"(?<![0-9a-f])(?:[0-9a-f]{64}|[0-9a-f]{40}|[0-9a-f]{32})(?![0-9a-f])", RegexOptions.IgnoreCase)]
    private static partial Regex HexHash();

    /// <summary>
    /// STIX2 bundles (.stix2, .json), or hash sets: any other file is read as text and every MD5, SHA-1 or SHA-256 in it
    /// becomes a hash indicator (one per line as md5sum writes them, or several per line as in NSRL-style CSV).
    /// </summary>
    public static IndicatorSet Load(IEnumerable<string> files)
    {
        var set = new IndicatorSet();
        var loaded = new List<string>();
        foreach (var file in files)
        {
            if (Path.GetExtension(file).ToLowerInvariant() is not (".json" or ".stix2"))
            {
                foreach (var line in File.ReadLines(file))
                    foreach (Match m in HexHash().Matches(line))
                        set.Add(new Indicator(m.Length switch { 32 => "file:hashes.md5", 40 => "file:hashes.sha1", _ => "file:hashes.sha256" },
                                              m.Value, Path.GetFileNameWithoutExtension(file), file));
                loaded.Add(file);
                continue;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{Path.GetFileName(file)} is not a STIX2 bundle (no objects).");
            var collection = objects.EnumerateArray()
                .Where(o => Str(o, "type") == "malware" && Str(o, "name") != "").Select(o => Str(o, "name")).FirstOrDefault()
                ?? Path.GetFileNameWithoutExtension(file);
            foreach (var o in objects.EnumerateArray().Where(o => Str(o, "type") == "indicator"))
                foreach (Match m in PatternTerm().Matches(Str(o, "pattern")))
                    set.Add(new Indicator(Key(m.Groups[1].Value), m.Groups[2].Value.Replace("\\'", "'").Trim(), collection, file));
            loaded.Add(file);
        }
        set.Files = loaded;
        return set;
    }

    static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>file:hashes.'SHA-256' and file:hashes.sha256 are one key.</summary>
    static string Key(string key)
    {
        key = key.Trim().ToLowerInvariant().Replace("'", "");
        var dot = key.IndexOf('.');
        return dot < 0 ? key : key[..dot] + key[dot..].Replace("-", "");
    }

    void Add(Indicator i)
    {
        if (i.Value == "") return;
        Count++;
        switch (i.Type)
        {
            case "domain-name:value" or "ipv4-addr:value":
                Bucket(_domains, i.Value.TrimEnd('.')).Add(i);
                break;
            case "url:value":
                if (Uri.TryCreate(i.Value.Contains("://") ? i.Value : "http://" + i.Value, UriKind.Absolute, out var u)) Bucket(_urls, u.Host).Add(i);
                else Bucket(_exact, i.Value).Add(i);
                break;
            case "file:path":
                var want = Segments(i.Value);
                if (want is ["private", "var", ..]) want = want[1..];
                if (want.Length > 0) (_pathsByLeaf.TryGetValue(want[^1], out var l) ? l : _pathsByLeaf[want[^1]] = []).Add((i, want));
                break;
            default:
                Bucket(_exact, i.Value).Add(i);
                break;
        }
    }

    static List<Indicator> Bucket(Dictionary<string, List<Indicator>> d, string key) =>
        d.TryGetValue(key, out var l) ? l : d[key] = [];

    /// <summary>The indicators one cell matches (each once).</summary>
    public IEnumerable<Indicator> Match(string cell)
    {
        var found = new HashSet<Indicator>();
        var text = cell.Trim();
        if (text == "") return found;
        if (_exact.TryGetValue(text, out var exact)) found.UnionWith(exact);
        var last = text[(text.LastIndexOfAny(['/', '\\']) + 1)..];
        if (last != text && _exact.TryGetValue(last, out var named)) found.UnionWith(named.Where(i => i.Type is "process:name" or "file:name"));
        if (_pathsByLeaf.Count > 0 && text.Contains('/')) found.UnionWith(MatchPath(Segments(text)).Select(m => m.I));
        if (_domains.Count + _urls.Count + _exact.Count == 0) return found;
        foreach (Match t in HostToken().Matches(text))
        {
            var host = t.Value.TrimEnd('.');
            if (_exact.TryGetValue(host, out var ids)) found.UnionWith(ids.Where(i => i.Type == "app:id"));
            if (_urls.TryGetValue(host, out var urls)) found.UnionWith(urls.Where(i => text.Contains(i.Value, StringComparison.OrdinalIgnoreCase)));
            for (var d = host; d.Contains('.'); d = d[(d.IndexOf('.') + 1)..])
                if (_domains.TryGetValue(d, out var doms)) found.UnionWith(doms);
        }
        if (text.Contains('@'))
            foreach (Match e in EmailToken().Matches(text))
                if (_exact.TryGetValue(e.Value, out var mails)) found.UnionWith(mails.Where(i => i.Type == "email-addr:value"));
        return found;
    }

    /// <summary>
    /// File-path indicators the path holds as whole trailing segments of some prefix: "/system/xbin/su" matches
    /// "Dump/system/xbin/su" and "Dump/system/xbin/su/x", never ".../xbin/sudo". A leading "/private" of an indicator is
    /// dropped, so "/private/var/db/x" also matches "var/db/x". Yields each indicator with the segment its match ends at.
    /// </summary>
    IEnumerable<(Indicator I, int End)> MatchPath(string[] segs)
    {
        for (int k = 0; k < segs.Length; k++)
            if (_pathsByLeaf.TryGetValue(segs[k], out var candidates))
                foreach (var (i, want) in candidates)
                    if (k + 1 >= want.Length && want.Select((w, j) => string.Equals(w, segs[k + 1 - want.Length + j], StringComparison.OrdinalIgnoreCase)).All(b => b))
                        yield return (i, k);
    }

    static string[] Segments(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Matches in the extraction's own file list, for what no artifact reports: file paths, app ids naming a folder, and
    /// file or process names equal to a file's name. MVT's file-listing check, over the extraction of the report. One hit
    /// per indicator and location: the matching folder is reported once, not once per file inside it.
    /// </summary>
    public IReadOnlyList<IocHit> ScanFiles(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var hits = new List<IocHit>();
        var seen = new HashSet<(Indicator, string)>();
        void Hit(Indicator i, string[] segs, int end)
        {
            var at = string.Join('/', segs[..(end + 1)]);
            if (seen.Add((i, at.ToLowerInvariant()))) hits.Add(new IocHit(null, 0, "File path", at, i));
        }
        foreach (var rel in paths)
        {
            ct.ThrowIfCancellationRequested();
            var segs = Segments(rel);
            foreach (var (i, end) in MatchPath(segs)) Hit(i, segs, end);
            for (int k = 0; k < segs.Length; k++)
                if (_exact.TryGetValue(segs[k], out var named))
                    foreach (var i in named.Where(i => i.Type == "app:id" || (k == segs.Length - 1 && i.Type is "process:name" or "file:name")))
                        Hit(i, segs, k);
        }
        return hits;
    }

    /// <summary>
    /// Media files of the extraction whose MD5, SHA-1 or SHA-256 is a hash indicator. Only the hashes the set holds are
    /// computed, in one read of each file. Media only (images, video, audio): what hash sets are made of.
    /// </summary>
    public IReadOnlyList<IocHit> ScanMediaHashes(ExtractionFiles x, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var algos = new[] { ("file:hashes.md5", HashAlgorithmName.MD5), ("file:hashes.sha1", HashAlgorithmName.SHA1), ("file:hashes.sha256", HashAlgorithmName.SHA256) }
            .Where(a => _exact.Values.Any(l => l.Any(i => i.Type == a.Item1))).ToList();
        var hits = new List<IocHit>();
        if (algos.Count == 0) return hits;
        var f = 0;
        x.Each(x.Media, (rel, stream) => // one pass over a tar.gz, not one per file
        {
            var hashers = algos.Select(a => IncrementalHash.CreateHash(a.Item2)).ToList();
            var buf = new byte[1 << 16];
            for (int n; (n = stream.Read(buf)) > 0;) { ct.ThrowIfCancellationRequested(); foreach (var h in hashers) h.AppendData(buf, 0, n); }
            for (int a = 0; a < algos.Count; a++)
                if (_exact.TryGetValue(Convert.ToHexStringLower(hashers[a].GetHashAndReset()), out var l))
                    foreach (var i in l.Where(i => i.Type == algos[a].Item1)) hits.Add(new IocHit(null, 0, "File hash", rel, i));
            if (++f % 50 == 0) progress?.Report((f, x.Media.Count));
        }, ct);
        return hits;
    }

    /// <summary>Every match in every text cell of the case.</summary>
    public IReadOnlyList<IocHit> Scan(CaseSession s, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default) =>
        Scan(s, Match, progress, ct);

    /// <summary>Every match of <paramref name="match"/> in every text cell of the case.</summary>
    public static IReadOnlyList<IocHit> Scan(CaseSession s, Func<string, IEnumerable<Indicator>> match,
                                             IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default) =>
        Scan(s, (cell, _) => match(cell), progress, ct);

    /// <summary>Every match of <paramref name="match"/>, told what each value is called (its column, its JSON path).</summary>
    public static IReadOnlyList<IocHit> Scan(CaseSession s, Func<string, string, IEnumerable<Indicator>> match,
                                             IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var hits = new List<IocHit>();
        using var con = s.OpenDb();
        var arts = s.Manifest.Artifacts;
        for (int a = 0; a < arts.Count; a++)
        {
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, arts[a]);
                foreach (var row in ArtifactQuery.All(con, arts[a]))
                {
                    ct.ThrowIfCancellationRequested();
                    for (int c = 0; c < cols.Count; c++)
                        if (!cols[c].IsDateTime && row.Values[c] is string cell)
                            foreach (var (path, text, i) in Matches(cell, cols[c].Display, match))
                                hits.Add(new IocHit(arts[a], row.RowId, path is null ? cols[c].Display : $"{cols[c].Display} › {path}", text, i));
                }
            }
            catch (SqliteException) { } // table missing from a partial case
            progress?.Report((a + 1, arts.Count));
        }
        return hits;
    }

    /// <summary>The matches in a cell. In a JSON cell, those in each of its values with the value's path, so an escaped
    /// character reads as written and the match is shown in the value that holds it; then those in each array of 12 or more
    /// values read joined by spaces (a seed phrase kept as a list of words).</summary>
    static IEnumerable<(string? Path, string Text, Indicator Hit)> Matches(string cell, string column, Func<string, string, IEnumerable<Indicator>> match)
    {
        if (StructuredValue.Kind(cell) != StructuredValue.Json || StructuredValue.Parse(cell, StructuredValue.Json) is not { } root)
        {
            foreach (var i in match(cell, column)) yield return (null, cell, i);
            yield break;
        }
        var nodes = new List<PlistNode>();
        for (var stack = new Stack<PlistNode>([root]); stack.Count > 0;)
        {
            var n = stack.Pop();
            nodes.Add(n);
            foreach (var k in n.Children) stack.Push(k);
        }
        var seen = new HashSet<Indicator>();
        foreach (var n in nodes.Where(n => n.Children.Count == 0))
            foreach (var i in match(n.Value, $"{column} {n.Path}"))
                if (seen.Add(i)) yield return (n.Path == "" ? "value" : n.Path, n.Value, i);
        foreach (var n in nodes.Where(n => n.Type == "array" && n.Children.Count(k => k.Children.Count == 0) >= 12))
        {
            var text = string.Join(' ', n.Children.Where(k => k.Children.Count == 0).Select(k => k.Value));
            foreach (var i in match(text, $"{column} {n.Path}"))
                if (seen.Add(i)) yield return (n.Path == "" ? "list" : n.Path, text, i);
        }
    }
}

/// <summary>Tagging rules from a text file, one per line: "Tag: keyword" or "Tag: /regex/" ('#' starts a comment).
/// A rule matches a cell the way the search box would (<see cref="ArtifactQuery.TextMatches"/>).</summary>
public static class TagRules
{
    public static IReadOnlyList<Indicator> Load(string path) =>
        File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#').Select(l =>
        {
            var i = l.IndexOf(':');
            if (i <= 0 || l[(i + 1)..].Trim().Length == 0) throw new InvalidDataException($"Not a rule (\"Tag: pattern\"): {l}");
            return new Indicator("rule", l[(i + 1)..].Trim(), l[..i].Trim(), Path.GetFileName(path));
        }).ToList();

    public static Func<string, IEnumerable<Indicator>> Matcher(IReadOnlyList<Indicator> rules) =>
        cell => rules.Where(r => ArtifactQuery.TextMatches(r.Value, cell));
}
