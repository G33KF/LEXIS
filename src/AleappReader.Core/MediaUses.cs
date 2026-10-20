using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>The chat message a media file was sent or received in.</summary>
public sealed record MediaUse(ArtifactInfo Artifact, long RowId, string Chat, string Sender, bool? Sent, long? Epoch)
{
    public string Direction => Sent switch { true => "Sent", false => "Received", null => "" };
}

/// <summary>Media → message, read from the media column of every artifact with a conversation view.</summary>
public static class MediaUses
{
    /// <summary>For each media ref id, the earliest message that carries it.</summary>
    public static Dictionary<string, MediaUse> Build(SqliteConnection con, LavaManifest m, CancellationToken ct = default)
    {
        var uses = new Dictionary<string, MediaUse>(StringComparer.Ordinal);
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
                        sent == true && !string.IsNullOrEmpty(cv.SentStaticLabel) ? cv.SentStaticLabel : T(5), sent, epoch);
                    foreach (var id in MediaResolver.RefIds(T(1)))
                        if (!uses.TryGetValue(id, out var old) || (use.Epoch ?? long.MaxValue) < (old.Epoch ?? long.MaxValue)) uses[id] = use;
                }
            }
            catch (SqliteException) { } // a table that does not read: its media keep no message
        }
        return uses;
    }
}
