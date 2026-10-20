using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>
/// Columns a text search can leave out: hashes, UUIDs, encoded blobs, and paths, file names and ids under a header
/// that says so. Decided on a sample of the values, not the header alone, so a column that holds what people wrote
/// is never dropped for its name.
/// </summary>
public static partial class TechnicalColumns
{
    [GeneratedRegex(@"^[0-9a-f]{32,}$", RegexOptions.IgnoreCase)] private static partial Regex Hash();
    [GeneratedRegex(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase)] private static partial Regex Uuid();
    [GeneratedRegex(@"^[A-Za-z0-9+/_-]{40,}={0,2}$")] private static partial Regex Base64();
    [GeneratedRegex(@"^(/|[A-Za-z]:\\|\\\\|[a-z]+://)?[^\s]*[/\\][^\s]*$|^[^\s/\\]+\.[A-Za-z0-9]{2,5}$")] private static partial Regex PathOrFile();
    [GeneratedRegex(@"^-?\d+$")] private static partial Regex Integer();
    [GeneratedRegex(@"(hash|md5|sha-?1|sha-?256|uuid|guid|path|file ?name|source|location of|\bid\b|_id\b|identifier|blob|key)", RegexOptions.IgnoreCase)] private static partial Regex Header();

    /// <summary>The SQL names of the technical columns of an artifact.</summary>
    public static HashSet<string> Of(SqliteConnection con, ArtifactInfo a)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ArtifactQuery.ExistingColumns(con, a))
        {
            if (c.IsDateTime) continue;
            if (c.IsMedia) { skip.Add(c.Sql); continue; }
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT CAST({ArtifactQuery.Q(c.Sql)} AS TEXT) FROM {ArtifactQuery.Q(a.TableName)} " +
                              $"WHERE {ArtifactQuery.Q(c.Sql)} IS NOT NULL AND CAST({ArtifactQuery.Q(c.Sql)} AS TEXT) <> '' LIMIT 200";
            var values = new List<string>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) values.Add(r.GetString(0).Trim());
            if (values.Count > 0 && Technical(c.Display + " " + c.Sql, values)) skip.Add(c.Sql);
        }
        return skip;
    }

    /// <summary>Nine in ten sampled values a hash, a UUID or an encoded blob; or, under a technical header, a path, a
    /// file name or an integer id.</summary>
    public static bool Technical(string header, IReadOnlyList<string> values)
    {
        bool Most(Func<string, bool> f) => values.Count(f) >= values.Count * 0.9;
        if (Most(v => Hash().IsMatch(v) || Uuid().IsMatch(v) || Base64().IsMatch(v) && !v.All(char.IsLetter))) return true;
        return Header().IsMatch(header) && Most(v => PathOrFile().IsMatch(v) || Integer().IsMatch(v) || Uuid().IsMatch(v));
    }
}
