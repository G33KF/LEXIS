using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core.Tests;

/// <summary>An encrypted iTunes backup built here the way Apple's keybag works, read back through ExtractionFiles.</summary>
public class BackupTests : IDisposable
{
    readonly SyntheticCase _c = new();
    public void Dispose() => _c.Dispose();

    [Fact]
    public void Aes_key_wrap_matches_rfc3394()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var wrapped = Convert.FromHexString("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5");
        Assert.Equal(wrapped, Wrap(kek, key));
        Assert.Equal(key, ItunesBackup.Unwrap(kek, wrapped));
        Assert.Null(ItunesBackup.Unwrap(new byte[16], wrapped)); // the wrong key fails the integrity check
    }

    [Fact]
    public void An_encrypted_backup_opens_locked_and_reads_with_its_password()
    {
        byte[] Rnd(int n) => RandomNumberGenerator.GetBytes(n);
        var (dpsl, salt, classKey, manifestKey, fileKey) = (Rnd(20), Rnd(20), Rnd(32), Rnd(32), Rnd(32));
        var kek = Rfc2898DeriveBytes.Pbkdf2(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("pw"), dpsl, 10, HashAlgorithmName.SHA256, 32),
                                            salt, 1, HashAlgorithmName.SHA1, 32);
        var keybag = Tlv(("VERS", Be(4)), ("TYPE", Be(1)), ("UUID", Rnd(16)), ("WRAP", Be(0)), ("SALT", salt), ("ITER", Be(1)),
                         ("DPWT", Be(1)), ("DPIC", Be(10)), ("DPSL", dpsl),
                         ("UUID", Rnd(16)), ("CLAS", Be(3)), ("WRAP", Be(2)), ("KTYP", Be(0)), ("WPKY", Wrap(kek, classKey)));
        byte[] Classed(byte[] key) => [3, 0, 0, 0, .. Wrap(classKey, key)];

        var backup = Path.Combine(_c.Root, "enc-backup");
        var id = "3d0d7e5fb2ce288813306e4d4636395e047a3d28";
        Directory.CreateDirectory(Path.Combine(backup, id[..2]));
        File.WriteAllBytes(Path.Combine(backup, id[..2], id), Enc(fileKey, "sms"u8.ToArray()));
        File.WriteAllBytes(Path.Combine(backup, id[..2], id[..2] + "ff"), Enc(fileKey, "x"u8.ToArray()));
        var mbFile = Plist(new Dictionary<string, object>
        {
            ["$top"] = new Dictionary<string, object> { ["root"] = new Bplist.Uid(1) },
            ["$objects"] = new object[] { "$null", new Dictionary<string, object> { ["Size"] = 3L, ["ProtectionClass"] = 3L, ["EncryptionKey"] = new Bplist.Uid(2) },
                                          new Dictionary<string, object> { ["NS.data"] = Classed(fileKey) } },
        });
        var plainDb = Path.Combine(_c.Root, "plain-manifest.db");
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={plainDb};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE Files(fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER, file BLOB);" +
                              "INSERT INTO Files VALUES($id,'HomeDomain','Library/SMS/sms.db',1,$f),($id2,'HomeDomain','nokey.db',1,NULL);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$id2", id[..2] + "ff");
            cmd.Parameters.AddWithValue("$f", mbFile);
            cmd.ExecuteNonQuery();
        }
        File.WriteAllBytes(Path.Combine(backup, "Manifest.db"), Enc(manifestKey, File.ReadAllBytes(plainDb)));
        File.WriteAllBytes(Path.Combine(backup, "Manifest.plist"), Plist(new Dictionary<string, object>
        {
            ["IsEncrypted"] = true, ["BackupKeyBag"] = keybag, ["ManifestKey"] = Classed(manifestKey),
        }));

        using (var locked = ExtractionFiles.Open(backup)!) Assert.True(locked.Locked);
        Assert.Throws<CryptographicException>(() => ExtractionFiles.Open(backup, "wrong"));
        using var x = ExtractionFiles.Open(backup, "pw")!;
        Assert.Equal(("encrypted iTunes backup", false), (x.Kind, x.Locked));
        Assert.Contains(("HomeDomain/Library/SMS/sms.db", 3L), x.Files());
        Assert.Equal("sms", x.Read("HomeDomain/Library/SMS/sms.db", s => new StreamReader(s).ReadToEnd()));
        Assert.Equal(["HomeDomain/nokey.db"], x.ContentManifest().Unreadable); // no key recorded: listed as unreadable, never as ciphertext
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "AleappReader", "backup")) &&
                     Directory.EnumerateFiles(Path.Combine(Path.GetTempPath(), "AleappReader", "backup")).Any()); // the decrypted Manifest.db is gone
    }

    static byte[] Be(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }

    static byte[] Tlv(params (string Tag, byte[] V)[] items) => [.. items.SelectMany(t => (byte[])[.. Encoding.ASCII.GetBytes(t.Tag), .. Be(t.V.Length), .. t.V])];

    static byte[] Enc(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, new byte[16], PaddingMode.PKCS7);
    }

    /// <summary>RFC 3394 wrap, the other direction of the reader's unwrap.</summary>
    static byte[] Wrap(byte[] kek, byte[] key)
    {
        int n = key.Length / 8;
        var a = 0xA6A6A6A6A6A6A6A6UL;
        var r = Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadUInt64BigEndian(key.AsSpan(8 * i))).ToArray();
        using var aes = Aes.Create();
        aes.Key = kek;
        var block = new byte[16];
        for (int j = 0; j < 6; j++)
            for (int i = 0; i < n; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(block, a);
                BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(8), r[i]);
                var e = aes.EncryptEcb(block, PaddingMode.None);
                a = BinaryPrimitives.ReadUInt64BigEndian(e) ^ (ulong)(n * j + i + 1);
                r[i] = BinaryPrimitives.ReadUInt64BigEndian(e.AsSpan(8));
            }
        var outp = new byte[8 * (n + 1)];
        BinaryPrimitives.WriteUInt64BigEndian(outp, a);
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt64BigEndian(outp.AsSpan(8 * (i + 1)), r[i]);
        return outp;
    }

    /// <summary>A minimal bplist00 writer: dictionaries, arrays, data, ASCII strings, 8-byte ints, booleans, UIDs.</summary>
    static byte[] Plist(object root)
    {
        var objs = new List<byte[]>();
        static byte[] Len(int marker, int n) => n < 15 ? [(byte)(marker | n)] : [(byte)(marker | 0xF), 0x12, .. Be(n)];
        static byte[] Refs(List<int> r) => [.. r.SelectMany(x => new[] { (byte)(x >> 8), (byte)x })];
        int Add(object o)
        {
            var i = objs.Count;
            objs.Add([]);
            objs[i] = o switch
            {
                bool b => [(byte)(b ? 0x09 : 0x08)],
                long l => [0x13, .. BitConverter.GetBytes(l).Reverse()],
                string s => [.. Len(0x50, s.Length), .. Encoding.ASCII.GetBytes(s)],
                byte[] d => [.. Len(0x40, d.Length), .. d],
                Bplist.Uid u => [0x80, (byte)u.Value],
                object[] a => [.. Len(0xA0, a.Length), .. Refs(a.Select(Add).ToList())],
                Dictionary<string, object> d => [.. Len(0xD0, d.Count), .. Refs(d.Keys.Select(k => Add(k)).ToList()), .. Refs(d.Values.Select(Add).ToList())],
                _ => throw new ArgumentException(o.GetType().Name),
            };
            return i;
        }
        Add(root);
        var ms = new MemoryStream();
        ms.Write("bplist00"u8);
        var offsets = objs.Select(o => { var at = ms.Position; ms.Write(o); return (int)at; }).ToList();
        var table = ms.Position;
        foreach (var off in offsets) ms.Write(Be(off));
        var trailer = new byte[32];
        (trailer[6], trailer[7]) = (4, 2);
        BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(8), objs.Count);
        BinaryPrimitives.WriteInt64BigEndian(trailer.AsSpan(24), table);
        ms.Write(trailer);
        return ms.ToArray();
    }
}
