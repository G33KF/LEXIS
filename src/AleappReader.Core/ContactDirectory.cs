using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>The contact record a chat participant's identifier was found in, and how it matched.</summary>
public sealed record ContactMatch(ArtifactInfo Artifact, string MatchedOn, IReadOnlyList<(string Field, string Value)> Fields, MediaRef? Picture);

/// <summary>
/// Looks chat participants up in the case's contact artifacts (any artifact whose name or category contains "contact", the chat
/// app's own first). Only identifier columns are indexed (IDs, usernames, aliases, phone numbers, e-mail), never
/// names. Identifiers match exactly — in another app's contacts only e-mail columns, since IDs and usernames are
/// app-specific; a phone number also matches a phone column when one number's digits end with
/// the other's (a country or trunk prefix), compared on at least nine digits. The first source that holds the
/// identifier decides: a value held by more than one of its records is ambiguous and nothing is matched, so a
/// participant is never given another person's details.
/// </summary>
public sealed class ContactDirectory
{
    const int MaxRows = 200_000; // ponytail: whole-table index in memory; query per id if contact tables get bigger
    const int MinPhoneDigits = 9;
    static readonly Regex PhoneLike = new(@"^\+?[\d\s().-]+$");
    static readonly Regex IdColumn = new(@"(?<![a-z])(ids?|username|user ?name|alias|uin|jid|handle|account|e-?mails?|e-?mail address|phone|phones|phone numbers?|number|numbers|mobile|tel)(?![a-z])", RegexOptions.IgnoreCase);
    static readonly Regex PhoneColumn = new(@"(?<![a-z])(phones?|numbers?|mobile|tel)(?![a-z])", RegexOptions.IgnoreCase);
    static readonly Regex Mail = new(@"(?<![a-z])e-?mail", RegexOptions.IgnoreCase);

    readonly string _chatCategory;
    readonly List<(ArtifactInfo A, IReadOnlyList<ColumnInfo> Cols, List<object?[]> Rows, Dictionary<string, List<(int Row, int Col, string Value)>> Index)> _sources = [];
    readonly SqliteConnection _con;
    readonly MediaResolver _media;

    public ContactDirectory(SqliteConnection con, CaseSession s, ArtifactInfo chat)
    {
        _con = con;
        _chatCategory = chat.Category;
        _media = new MediaResolver(s);
        foreach (var a in s.Manifest.Artifacts
                     .Where(a => a != chat && a.Conversation is null && a.RecordCount <= MaxRows &&
                                 (a.Name.Contains("contact", StringComparison.OrdinalIgnoreCase) || a.Category.Contains("contact", StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(a => a.Category == chat.Category ? 0 : 1))
        {
            var cols = ArtifactQuery.ExistingColumns(con, a);
            var idCols = Enumerable.Range(0, cols.Count).Where(c => !cols[c].IsMedia && IdColumn.IsMatch(cols[c].Display)).ToList();
            if (idCols.Count == 0) continue;
            var rows = new List<object?[]>();
            var index = new Dictionary<string, List<(int, int, string)>>(StringComparer.Ordinal);
            foreach (var r in ArtifactQuery.All(con, a))
            {
                foreach (var c in idCols)
                {
                    var v = ValueFormatter.Format(r.Values[c], cols[c]).Trim();
                    var key = PhoneColumn.IsMatch(cols[c].Display) && TelKey(v) is { } t ? t : Key(v);
                    if (key is null) continue;
                    if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                    list.Add((rows.Count, c, v));
                }
                rows.Add(r.Values);
            }
            _sources.Add((a, cols, rows, index));
        }
    }

    /// <summary>The exact-match key of an identifier, or null when it is too short to identify anyone.</summary>
    public static string? Key(string value)
    {
        var v = value.Trim();
        return v.Length >= 3 && !(v.All(char.IsAsciiDigit) && v.Length < 7) ? v : null;
    }

    /// <summary>"tel:" + the last nine digits of a phone-shaped value, or null.</summary>
    public static string? TelKey(string value)
    {
        if (!PhoneLike.IsMatch(value.Trim())) return null;
        var d = Digits(value);
        return d.Length >= MinPhoneDigits ? "tel:" + d[^MinPhoneDigits..] : null;
    }

    static string Digits(string v) => string.Concat(v.Where(char.IsAsciiDigit));

    public ContactMatch? Find(string id)
    {
        id = id.Trim();
        var tel = TelKey(id);
        var exact = Key(id);
        foreach (var (a, cols, rows, index) in _sources)
        {
            var hits = new List<(int Row, int Col, string Value, string Rule)>();
            if (exact is not null && index.TryGetValue(exact, out var e))   // another app's IDs and usernames mean nothing here: only its e-mail
                hits.AddRange(e.Where(h => a.Category == _chatCategory || Mail.IsMatch(cols[h.Col].Display))
                               .Select(h => (h.Row, h.Col, h.Value, "exact")));
            if (tel is not null && index.TryGetValue(tel, out var t))
            {
                var d = Digits(id);
                hits.AddRange(t.Where(h => Digits(h.Value) is var hd && (hd.EndsWith(d, StringComparison.Ordinal) || d.EndsWith(hd, StringComparison.Ordinal)))
                               .Select(h => (h.Row, h.Col, h.Value, Digits(h.Value) == d ? "same digits" : "phone digits, prefix ignored")));
            }
            if (hits.Count == 0) continue;                              // this source does not hold the identifier
            if (hits.Select(h => h.Row).Distinct().Count() > 1) return null; // ambiguous here: never fall through to another source
            var (rowIx, col, value, rule) = hits[0];
            var row = rows[rowIx];
            var fields = cols.Select((c, i) => (c, i)).Where(x => !x.c.IsMedia)
                .Select(x => (x.c.Display, ValueFormatter.Format(row[x.i], x.c))).Where(f => f.Item2 != "").ToList();
            var picture = cols.Select((c, i) => (c, i)).Where(x => x.c.IsMedia)
                .SelectMany(x => _media.Resolve(_con, row[x.i])).FirstOrDefault(m => m.IsImage);
            return new ContactMatch(a, $"{id} = {cols[col].Display} \"{value}\" ({rule}) in {a.Name}", fields, picture);
        }
        return null;
    }
}
