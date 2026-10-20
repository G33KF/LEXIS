using System.Net;
using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>General documentation looked up on the web for "Explain": what an app's database and its columns mean.
/// Only words that describe the data's format leave the machine (the app, the artifact, the database file's name, the
/// columns' names), never a value of a record; a word that could identify someone (digits, @, a long token) is dropped.
/// What comes back is untrusted reference, never evidence and never instructions.</summary>
public static partial class WebLookup
{
    public const string DefaultUrl = "https://html.duckduckgo.com/html/?q={q}";

    [GeneratedRegex(@"\d{4,}|@|\+\d|^[0-9a-f]{12,}$|^[A-Za-z0-9+/_=-]{25,}$", RegexOptions.IgnoreCase)] private static partial Regex Identifying();
    [GeneratedRegex(@"[^\p{L}\p{N}._\- ]+")] private static partial Regex NotWord();

    /// <summary>The words of <paramref name="text"/> that describe a format, not a person: identifying tokens dropped.</summary>
    public static string Clean(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length <= 40 && !Identifying().IsMatch(w)) // judged as written: "+39", "a@b.c" go whole
        .Select(w => NotWord().Replace(w, "").Trim('.', '-', '_')).Where(w => w != ""));

    /// <summary>One query per artifact (at most <paramref name="max"/>): its app and name, its database file, its columns.</summary>
    public static List<string> Queries(IEnumerable<(ArtifactInfo Artifact, IEnumerable<string> Columns)> items, int max = 4) =>
        items.Select(x =>
        {
            var file = EvidenceOrigin.RecordedSources(x.Artifact).Select(p => Path.GetFileName(p.Replace('\\', '/'))).FirstOrDefault() ?? "";
            var name = x.Artifact.Name.StartsWith(x.Artifact.Category, StringComparison.OrdinalIgnoreCase) ? x.Artifact.Name : $"{x.Artifact.Category} {x.Artifact.Name}";
            return Clean($"{name} {file} {string.Join(" ", x.Columns.Take(6))} database columns meaning");
        }).Where(q => q != "").Distinct().Take(max).ToList();

    [GeneratedRegex("""class="result__a"[^>]*href="([^"]+)"[^>]*>(.*?)</a>.*?class="result__snippet"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex DuckResult();
    [GeneratedRegex("<[^>]+>")] private static partial Regex Tag();

    /// <summary>Title, text and address of the results of a DuckDuckGo HTML page.</summary>
    public static List<(string Title, string Text, string Url)> Parse(string html, int max = 3)
    {
        string Plain(string s) => WebUtility.HtmlDecode(Tag().Replace(s, "")).Trim();
        return DuckResult().Matches(html).Take(max).Select(m =>
        {
            var url = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (url.Contains("uddg=")) url = Uri.UnescapeDataString(url[(url.IndexOf("uddg=", StringComparison.Ordinal) + 5)..].Split('&')[0]);
            var text = Plain(m.Groups[3].Value);
            return (Plain(m.Groups[2].Value), text.Length > 400 ? text[..400] + "…" : text, url);
        }).ToList();
    }

    /// <summary>The queries' results as a reference block for the prompt; a query that fails is said, not hidden.</summary>
    public static async Task<string> Reference(HttpClient http, string urlTemplate, IReadOnlyList<string> queries, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var q in queries)
        {
            try
            {
                var html = await http.GetStringAsync(urlTemplate.Replace("{q}", Uri.EscapeDataString(q)), ct);
                var found = Parse(html);
                lines.Add($"Search: {q}");
                lines.AddRange(found.Count == 0 ? ["  (no result)"] : found.Select(r => $"  - {r.Title}: {r.Text} ({r.Url})"));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { lines.Add($"Search: {q}\n  (failed: {e.Message})"); }
        }
        return string.Join("\n", lines);
    }
}
