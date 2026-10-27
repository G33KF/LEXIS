using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>The chat message a media file was sent or received in; <paramref name="Group"/> when its chat id is a group's.</summary>
public sealed record MediaUse(ArtifactInfo Artifact, long RowId, string Chat, string Sender, bool? Sent, long? Epoch, bool Group = false)
{
    public string Direction => Sent switch { true => "Sent", false => "Received", null => "" };
}

/// <summary>Media → message, read from the media column of every artifact with a conversation view.</summary>
public static class MediaUses
{
    /// <summary>For each media ref id, every message that carries it, the earliest first (undated last).</summary>
    public static Dictionary<string, List<MediaUse>> Build(SqliteConnection con, LavaManifest m, CancellationToken ct = default)
    {
        var uses = new Dictionary<string, List<MediaUse>>(StringComparer.Ordinal);
        foreach (var a in m.Artifacts.Where(a => a.Conversation?.Media is not null))
        {
            ct.ThrowIfCancellationRequested();
            var cv = a.Conversation!;
            IReadOnlyList<ColumnInfo> cols;
            try { cols = ArtifactQuery.ExistingColumns(con, a); }
            catch (SqliteException) { continue; }
            ColumnInfo? C(string? name) => a.Column(name) is { } c && cols.Contains(c) ? c : null;
            if (C(cv.Media) is not { } media) continue;
            var (label, key, dir, sender, time) = (C(cv.LabelColumn), C(cv.Discriminator), C(cv.Direction), C(cv.Sender), C(cv.Time));
            string Sel(ColumnInfo? c) => c is null ? "NULL" : ArtifactQuery.Q(c.Sql);
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT rowid, {Sel(media)}, {Sel(label)}, {Sel(key)}, {Sel(dir)}, {Sel(sender)}, {Sel(time)} " +
                              $"FROM {ArtifactQuery.Q(a.TableName)} WHERE {Sel(media)} IS NOT NULL AND {Sel(media)} <> ''";
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string T(int i) => r.IsDBNull(i) ? "" : r.GetValue(i).ToString() ?? "";
                    bool? sent = dir is null ? null : cv.IsSent(T(4));
                    long? epoch = time is { IsDateTime: true } && !r.IsDBNull(6) && double.TryParse(T(6), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var e) ? (long)e : null;
                    var use = new MediaUse(a, r.GetInt64(0), T(2) != "" ? T(2) : T(3),
                        sent == true && !string.IsNullOrEmpty(cv.SentStaticLabel) ? cv.SentStaticLabel : T(5), sent, epoch,
                        ConversationItem.GroupId.IsMatch(T(3)));
                    foreach (var id in MediaResolver.RefIds(T(1)).Distinct())
                        (uses.TryGetValue(id, out var list) ? list : uses[id] = []).Add(use);
                }
            }
            catch (SqliteException) { } // a table that does not read: its media keep no message
        }
        foreach (var list in uses.Values) list.Sort(Earliest);
        return uses;
    }

    /// <summary>The groups of items whose files hold the same bytes: sizes compared first, SHA-256 only on equal sizes;
    /// an empty file, or one that does not read, is left out.</summary>
    public static List<List<T>> SameContent<T>(IEnumerable<T> items, Func<T, long> size, Func<T, Stream> open)
    {
        string? Hash(T i)
        {
            try { using var s = open(i); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return items.Select(i => (i, s: size(i))).Where(x => x.s > 0).GroupBy(x => x.s).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(x => (x.i, h: Hash(x.i))).Where(x => x.h is not null).GroupBy(x => x.h).Where(h => h.Count() > 1))
            .Select(h => h.Select(x => x.i).ToList()).ToList();
    }

    /// <summary>Earliest message first, undated last.</summary>
    public static int Earliest(MediaUse a, MediaUse b) =>
        (a.Epoch ?? long.MaxValue).CompareTo(b.Epoch ?? long.MaxValue) is var c and not 0 ? c : a.RowId.CompareTo(b.RowId);
}
