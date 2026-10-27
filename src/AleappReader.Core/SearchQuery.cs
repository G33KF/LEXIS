using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>A word, an exact phrase or a regular expression; plain text matches as "contains, ignoring case".</summary>
public sealed record SearchAtom(string Text, bool Regex)
{
    public bool Matches(string? value) =>
        value is not null && (Regex ? ArtifactQuery.RegexMatch(Text, value) : value.Contains(Text, StringComparison.OrdinalIgnoreCase));

    /// <summary>The atom as it is typed, so it can be searched again on its own.</summary>
    public override string ToString() =>
        Regex ? $"/{Text}/" : Text.Any(c => char.IsWhiteSpace(c) || c is '"' or '|' or '/') || Text.StartsWith('-') ? $"\"{Text}\"" : Text;
}

/// <summary>One condition of a query: any of its atoms in any column, or (Not) none of them in any column.</summary>
public sealed record SearchClause(IReadOnlyList<SearchAtom> AnyOf, bool Not);

/// <summary>
/// The search syntax shared by the global search, artifact filters and the timeline:
/// <c>a b</c> (both), <c>a | b</c> (either), <c>-a</c> (not), <c>"exact phrase"</c>, <c>/regex/</c>.
/// A row matches when every clause holds, each clause looking at all of the row's columns.
/// </summary>
public sealed class SearchQuery
{
    public IReadOnlyList<SearchClause> Clauses { get; }

    SearchQuery(IReadOnlyList<SearchClause> clauses) => Clauses = clauses;

    /// <summary>The terms searched for (not the excluded ones), each once, in the order typed.</summary>
    public IReadOnlyList<SearchAtom> Terms => Clauses.Where(c => !c.Not).SelectMany(c => c.AnyOf).Distinct().ToList();

    public bool IsEmpty => Clauses.Count == 0;

    /// <summary>Some clause accepts either of several terms (a | b): only then do the terms count differently.</summary>
    public bool HasAlternatives => Clauses.Any(c => !c.Not && c.AnyOf.Count > 1);

    static readonly ConcurrentDictionary<string, SearchQuery> Cache = new();

    public static SearchQuery Parse(string? text)
    {
        text ??= "";
        if (Cache.TryGetValue(text, out var q)) return q;
        if (Cache.Count > 1000) Cache.Clear(); // ponytail: indicator rules and filters repeat; a real LRU if this churns
        return Cache[text] = new SearchQuery(Build(Tokens(text)));
    }

    /// <summary>Tokens: atoms with their minus sign, and "|" between them.</summary>
    static IEnumerable<(SearchAtom? Atom, bool Not)> Tokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            bool not = false;
            if (s[i] == '|' && (i + 1 == s.Length || char.IsWhiteSpace(s[i + 1]))) { i++; yield return (null, false); continue; }
            if (s[i] == '-' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1])) { not = true; i++; }
            if (s[i] == '"')
            {
                var end = s.IndexOf('"', i + 1);
                var phrase = end < 0 ? s[(i + 1)..] : s[(i + 1)..end];
                i = end < 0 ? s.Length : end + 1;
                if (phrase.Trim() != "") yield return (new SearchAtom(phrase, false), not);
                continue;
            }
            if (s[i] == '/')
            {
                // a regex runs to the next "/" that ends a word, so it may hold spaces: /a b/
                for (int e = s.IndexOf('/', i + 1); e > i + 1; e = s.IndexOf('/', e + 1))
                    if (e + 1 == s.Length || char.IsWhiteSpace(s[e + 1]))
                    {
                        if (ValidRegex(s[(i + 1)..e]) is { } p) { i = e + 1; yield return (new SearchAtom(p, true), not); goto next; }
                        break;
                    }
            }
            {
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                yield return (new SearchAtom(s[start..i], false), not);
            }
            next:;
        }
    }

    static List<SearchClause> Build(IEnumerable<(SearchAtom? Atom, bool Not)> tokens)
    {
        var clauses = new List<SearchClause>();
        bool or = false;
        foreach (var (atom, not) in tokens)
        {
            if (atom is null) { or = clauses.Count > 0; continue; }
            if (or) clauses[^1] = clauses[^1] with { AnyOf = [.. clauses[^1].AnyOf, atom] };
            else clauses.Add(new SearchClause([atom], not));
            or = false;
        }
        return clauses;
    }

    public enum Mode { AllWords, AnyWord, ExactPhrase, RegularExpression }

    /// <summary>The query the advanced search form stands for, in the typed syntax; null for an invalid regular
    /// expression. <paramref name="list"/> adds keywords loaded from a file, each kept whole (a phrase when it has
    /// spaces, a regex in that mode): all of them with "All of the words", any of them otherwise. Whole words bound
    /// each word (or the phrase) by \b, on a side only where it starts or ends with a letter of a spaced script.</summary>
    public static string? Compose(string words, Mode mode, bool wholeWords, string exclude, IReadOnlyList<string>? list = null)
    {
        static string[] Split(string s) => s.Replace("\"", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var atoms = new List<string>();
        if (mode == Mode.RegularExpression)
            foreach (var p in (string[])[words.Trim(), .. list ?? []])
            {
                if (p == "") continue;
                if (ValidRegex(p) is null) return null;
                atoms.Add($"/{p}/");
            }
        else
        {
            if (mode != Mode.ExactPhrase) atoms.AddRange(Split(words).Select(w => Atom(w, wholeWords)));
            else if (string.Join(" ", Split(words)) is { Length: > 0 } p) atoms.Add(Atom(p, wholeWords));
            atoms.AddRange((list ?? []).Select(k => k.Replace("\"", "").Trim()).Where(k => k != "").Select(k => Atom(k, wholeWords)));
        }
        var main = string.Join(mode == Mode.AllWords ? " " : " | ", atoms);
        return string.Join(" ", Split(exclude).Select(w => "-" + Atom(w, wholeWords)).Prepend(main).Where(s => s != ""));
    }

    static string Atom(string w, bool whole)
    {
        var (left, right) = whole ? (SpacedLetter(w[0]), SpacedLetter(w[^1])) : (false, false);
        return left || right ? $"/{(left ? "\\b" : "")}{Regex.Escape(w)}{(right ? "\\b" : "")}/" : new SearchAtom(w, false).ToString();
    }

    /// <summary>A letter or digit of a script that separates words with spaces. Chinese, Japanese, Thai, Lao, Khmer
    /// and Myanmar do not, so \b next to them never matches inside a sentence: there the keyword matches as written.</summary>
    static bool SpacedLetter(char c) =>
        char.IsLetterOrDigit(c) && c is not ((>= '฀' and <= '໿') or (>= 'က' and <= '႟') or (>= 'ក' and <= '៿')
                                             or (>= '⺀' and <= '鿿') or (>= '豈' and <= '﫿') or (>= 'ｦ' and <= 'ﾟ'));

    static string? ValidRegex(string p)
    {
        try { _ = new Regex(p); return p; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Whether a row with these cell texts matches.</summary>
    public bool Matches(IReadOnlyCollection<string?> cells) =>
        Clauses.All(c => c.AnyOf.Any(a => cells.Any(a.Matches)) != c.Not);

    /// <summary>The WHERE condition over these columns, with every value bound on the command ("1" for an empty query).</summary>
    internal string Sql(SqliteCommand cmd, IReadOnlyList<ColumnInfo> cols)
    {
        if (IsEmpty) return "1";
        var sb = new StringBuilder("(");
        for (int i = 0; i < Clauses.Count; i++)
        {
            if (i > 0) sb.Append(" AND ");
            var any = "(" + string.Join(" OR ", Clauses[i].AnyOf.Select(a => AtomSql(cmd, cols, a))) + ")";
            sb.Append(Clauses[i].Not ? "NOT " + any : any);
        }
        return sb.Append(')').ToString();
    }

    /// <summary>"The atom is in some column", never NULL, so NOT works on rows with empty cells.</summary>
    internal static string AtomSql(SqliteCommand cmd, IReadOnlyList<ColumnInfo> cols, SearchAtom a)
    {
        if (cols.Count == 0) return "0";
        var p = "@q" + cmd.Parameters.Count;
        // SQLite's LIKE ignores case for A-Z only: text with other cased letters (Cyrillic, Greek, accented) goes through
        // the case-insensitive REGEXP, so it matches as SearchAtom.Matches does in memory
        var fold = !a.Regex && a.Text.Any(c => c > 127 && char.ToUpperInvariant(c) != char.ToLowerInvariant(c));
        cmd.Parameters.AddWithValue(p, a.Regex ? a.Text : fold ? Regex.Escape(a.Text) : "%" + ArtifactQuery.EscapeLike(a.Text) + "%");
        var op = a.Regex || fold ? $"REGEXP {p}" : $"LIKE {p} ESCAPE '\\'";
        // Datetime cells are epochs: also match their UTC text so "2024-05" finds them.
        return "(" + string.Join(" OR ", cols.Select(c =>
        {
            var q = ArtifactQuery.Q(c.Sql);
            var text = $"IFNULL(CAST({q} AS TEXT), '') {op}";
            var date = $"IFNULL(datetime({q}, 'unixepoch'), '') {op}";
            return !c.IsDateTime ? text : a.Regex ? date : $"{text} OR {date}";
        })) + ")";
    }
}
