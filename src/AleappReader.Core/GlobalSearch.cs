using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>An artifact the search found: its matching records, a few of them, and per term of the query how many of
/// those records hold it.</summary>
public sealed record SearchHit(ArtifactInfo Artifact, long Total, IReadOnlyList<ColumnInfo> Columns, IReadOnlyList<RecordRow> Rows,
                               IReadOnlyList<long> TermCounts);

/// <summary>Search across every artifact table of the case, in the <see cref="SearchQuery"/> syntax.</summary>
public static class GlobalSearch
{
    public static IReadOnlyList<SearchHit> Run(CaseSession s, string query, int maxRowsPerArtifact = 200,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        var q = SearchQuery.Parse(query);
        if (q.Terms.Count == 0) return hits;
        using var con = s.OpenDb();
        var arts = s.Manifest.Artifacts;
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
}
