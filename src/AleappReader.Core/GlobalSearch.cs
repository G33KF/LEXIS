using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>An artifact the search found: its matching records, a few of them, and per term of the query how many of
/// those records hold it.</summary>
public sealed record SearchHit(ArtifactInfo Artifact, long Total, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<RecordRow> Rows,
                               IReadOnlyList<long> TermCounts);

/// <summary>One result of a search, as a row of the results table: a record of the report, a part of a file (its
/// <see cref="Path"/> in the extraction, <see cref="Where"/> its part), a file still to OCR, or an AI result.</summary>
public sealed record SearchRow(string Origin, string Category, string Kind, long? Epoch, string App, string Device, string Where,
                               string Snippet, string Path, ArtifactInfo? Artifact = null, long RowId = 0)
{
    public const string ReportData = "Report data", FileContent = "File content", NeedsOcr = "Needs OCR", AiAnalysis = "AI analysis";
    public string Date => Epoch is { } e ? ValueFormatter.FormatTime(DateTimeOffset.FromUnixTimeSeconds(e)) : "";

    /// <summary>The value of a results column, by the name its header and facets use.</summary>
    public string ValueOf(string column) => column switch
    {
        nameof(Origin) => Origin, nameof(Category) => Category, nameof(Kind) => Kind, nameof(Date) => Date, nameof(App) => App,
        nameof(Device) => Device, nameof(Where) => Where, nameof(Snippet) => Snippet, nameof(Path) => Path, _ => "",
    };
}

/// <summary>Narrowing a search's results: text anywhere in the row (search syntax), one value per facet, a
/// condition per column; and each facet's values with their counts.</summary>
public static class SearchRows
{
    public static readonly IReadOnlyList<string> Facets = [nameof(SearchRow.Origin), nameof(SearchRow.Category), nameof(SearchRow.Kind),
                                                           nameof(SearchRow.App), nameof(SearchRow.Device)];
    static readonly string[] All = [.. Facets, nameof(SearchRow.Date), nameof(SearchRow.Where), nameof(SearchRow.Snippet), nameof(SearchRow.Path)];

    public static bool Keep(SearchRow r, string text, IReadOnlyDictionary<string, string> facets, IReadOnlyList<ColumnFilter> columns) =>
        (text == "" || ArtifactQuery.TextMatches(text, string.Join("\n", All.Select(r.ValueOf)))) &&
        facets.All(f => r.ValueOf(f.Key) == f.Value) &&
        columns.All(c => c.Matches(r.ValueOf(c.Sql), c.Sql == nameof(SearchRow.Date)));

    /// <summary>Each value of a facet with its number of rows, largest first ("" for rows without one).</summary>
    public static IReadOnlyList<(string Value, int Count)> Count(IEnumerable<SearchRow> rows, string facet) =>
        [.. rows.GroupBy(r => r.ValueOf(facet)).Select(g => (g.Key, g.Count()))
                .OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)];
}

/// <summary>Search across every artifact table of the case, in the <see cref="SearchQuery"/> syntax.</summary>
public static class GlobalSearch
{
    /// <param name="categories">Only the artifacts of these categories (null: all).</param>
    public static IReadOnlyList<SearchHit> Run(CaseSession s, string query, int maxRowsPerArtifact = 200,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default, IReadOnlyCollection<string>? categories = null)
    {
        var hits = new List<SearchHit>();
        var q = SearchQuery.Parse(query);
        if (q.Terms.Count == 0) return hits;
        using var con = s.OpenDb();
        var arts = s.Manifest.Artifacts.Where(a => categories is null || categories.Contains(a.Category)).ToList();
        for (int i = 0; i < arts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var skip = s.SearchSkips(arts[i]);
                var page = ArtifactQuery.Query(con, arts[i], new PageRequest(0, maxRowsPerArtifact, query, SkipColumns: skip));
                if (page.Total > 0)
                    hits.Add(new SearchHit(arts[i], page.Total, page.Columns, page.Rows,
                        q.HasAlternatives ? ArtifactQuery.TermCounts(con, arts[i], query, skip) : q.Terms.Select(_ => page.Total).ToList()));
            }
            catch (SqliteException) { } // table missing from a partial case: nothing to search there
            progress?.Report((i + 1, arts.Count));
        }
        return hits;
    }

    /// <summary>The hit's records as rows of the results: the first column holding a term with its text around the
    /// term, and the record's first date.</summary>
    public static IEnumerable<SearchRow> Rows(SearchHit h, string query, string device = "")
    {
        var terms = SearchQuery.Parse(query).Terms;
        var date = -1;
        for (int i = 0; i < h.Columns.Count && date < 0; i++) if (h.Columns[i].IsDateTime) date = i;
        foreach (var r in h.Rows)
        {
            long? epoch = date >= 0 && ValueFormatter.TryEpoch(r.Values[date], out var t) ? t.ToUnixTimeSeconds() : null;
            var (where, snippet) = Match(h.Columns, r.Values, terms);
            yield return new SearchRow(SearchRow.ReportData, h.Artifact.Category, h.Artifact.Name, epoch, h.Artifact.Module ?? "", device,
                where, snippet, (h.Artifact.SourcePath ?? "").ReplaceLineEndings(" | "), h.Artifact, r.RowId);
        }
    }

    /// <summary>A part of a file holding the query, as a row of the results.</summary>
    public static SearchRow Row(TextHit h, string query, string device = "") =>
        new(SearchRow.FileContent, h.File.Kind, h.File.Type, null, App(h.File.Path), device, h.Part == "" ? h.File.Method : h.Part,
            Match([TextColumn], [h.Text], SearchQuery.Parse(query).Terms).Snippet, h.File.Path, null, h.Id);

    /// <summary>A file whose text could not be searched because its pages are images: OCR would read it.</summary>
    public static SearchRow Row(TextFile f, string device = "") =>
        new(SearchRow.NeedsOcr, f.Kind, f.Type, null, App(f.Path), device, $"{f.Pages} pages", f.Status, f.Path);

    /// <summary>The AI results (translations, analyses, descriptions) holding the query, as rows of the results.</summary>
    public static IEnumerable<SearchRow> Rows(AiStore ai, string query, string device = "")
    {
        var q = SearchQuery.Parse(query);
        if (q.Terms.Count == 0) yield break;
        foreach (var r in ai.Results(items: true).Where(r => q.Matches([r.Output])))
            yield return new SearchRow(SearchRow.AiAnalysis, r.Kind, r.Model, r.At.ToUnixTimeSeconds(), "", device, r.Scope,
                                       Match([TextColumn], [r.Output], q.Terms).Snippet, r.Label, null, r.Id);
    }

    static readonly ColumnInfo TextColumn = new("text", "Text", null);
    static string App(string rel) => MediaOrigin.Package(EvidenceOrigin.DevicePath(rel)) ?? "";

    /// <summary>The first cell holding a term: its column, and up to 160 characters from just before the term.</summary>
    public static (string Column, string Snippet) Match(IReadOnlyList<ColumnInfo> cols, object?[] values, IReadOnlyList<SearchAtom> terms)
    {
        for (int i = 0; i < cols.Count; i++)
        {
            var v = ValueFormatter.Format(values[i], cols[i]);
            if (terms.FirstOrDefault(t => t.Matches(v)) is not { } t) continue;
            var at = t.Regex ? 0 : v.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, at - 40);
            return (cols[i].Display, (start > 0 ? "…" : "") + v.Substring(start, Math.Min(v.Length - start, 160)).ReplaceLineEndings(" "));
        }
        return ("", "");
    }
}
