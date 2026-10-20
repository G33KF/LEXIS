using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>The original HTML body of an email, which ALEAPP reduces to text. Gmail keeps it in the source database
/// (bigTopDataDB item_messages.zipped_message_proto: one byte, then zlib, then protobuf field 6.2[].3.2), read here
/// from the copy ALEAPP kept under data/.</summary>
public static class EmailBody
{
    public static string? Html(CaseSession s, ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, object?[] values)
    {
        if (a.Module != "gmailEmails") return null;
        string? Col(string name) { var i = cols.ToList().FindIndex(c => c.Display == name); return i < 0 ? null : values[i]?.ToString(); }
        var id = Col("Email ID");
        // ponytail: report copy only; read the store from the extraction too if a report ever lacks data/.
        var db = s.ResolveInCase(Path.Combine("data", (Col("Source File") ?? "").TrimStart('/', '\\')));
        if (string.IsNullOrEmpty(id) || db is null || !File.Exists(db)) return null;
        try
        {
            using var con = CaseSession.OpenReadOnly(db);
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT zipped_message_proto FROM item_messages WHERE server_perm_id = @id AND zipped_message_proto IS NOT NULL LIMIT 1";
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteScalar() is not byte[] blob || blob.Length < 2) return null;
            using var z = new ZLibStream(new MemoryStream(blob, 1, blob.Length - 1), CompressionMode.Decompress);
            var proto = new MemoryStream();
            z.CopyTo(proto);
            var html = new StringBuilder();
            foreach (var body in Sub(proto.ToArray(), 6))
                foreach (var part in Sub(body, 2))
                    foreach (var node in Sub(part, 3))
                        foreach (var text in Sub(node, 2)) html.Append(Encoding.UTF8.GetString(text));
            return html.Length > 0 ? html.ToString() : null;
        }
        catch (Exception e) when (e is SqliteException or InvalidDataException or IOException) { return null; }
    }

    /// <summary>The length-delimited values of one field of a protobuf message (no schema needed).</summary>
    public static List<byte[]> Sub(byte[] msg, int field)
    {
        var found = new List<byte[]>();
        int i = 0;
        while (i < msg.Length)
        {
            var key = Varint(msg, ref i);
            switch (key & 7)
            {
                case 0: Varint(msg, ref i); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    var len = (long)Varint(msg, ref i);
                    if (len < 0 || i + len > msg.Length) return found;
                    if ((int)(key >> 3) == field) found.Add(msg[i..(i + (int)len)]);
                    i += (int)len;
                    break;
                default: return found; // not a message (or groups, which Gmail does not use)
            }
        }
        return found;
    }

    static ulong Varint(byte[] b, ref int i)
    {
        ulong v = 0;
        for (int shift = 0; i < b.Length && shift < 64; shift += 7)
        {
            var x = b[i++];
            v |= (ulong)(x & 0x7F) << shift;
            if (x < 0x80) return v;
        }
        i = b.Length; // truncated: stop the walk
        return v;
    }
}
