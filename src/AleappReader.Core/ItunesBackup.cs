using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AleappReader.Core;

/// <summary>
/// Encrypted iTunes/Finder backups, as Apple documents its backup keybag: the password derives a key (PBKDF2-SHA256, then
/// PBKDF2-SHA1) that unwraps the class keys (AES key wrap, RFC 3394); a class key unwraps Manifest.db's key and each
/// file's own key; data is AES-256-CBC with a zero IV. The password is used once and never kept or written.
/// </summary>
public static class ItunesBackup
{
    /// <summary>Manifest.plist of a backup folder as values; null when absent or not a binary plist.</summary>
    public static Dictionary<string, object?>? Manifest(string dir) =>
        Path.Combine(dir, "Manifest.plist") is var p && File.Exists(p) ? Bplist.Parse(File.ReadAllBytes(p)) as Dictionary<string, object?> : null;

    public static bool IsEncrypted(string dir) => Manifest(dir)?.GetValueOrDefault("IsEncrypted") is true;

    /// <summary>The class keys the password unlocks, by protection class. A wrong password throws CryptographicException.</summary>
    public static Dictionary<int, byte[]> ClassKeys(byte[] keybag, string password)
    {
        var top = new Dictionary<string, byte[]>();
        var classes = new List<Dictionary<string, byte[]>>();
        Dictionary<string, byte[]>? cur = null;
        for (int i = 0; i + 8 <= keybag.Length;)
        {
            var tag = Encoding.ASCII.GetString(keybag, i, 4);
            var len = BinaryPrimitives.ReadInt32BigEndian(keybag.AsSpan(i + 4));
            if (len < 0 || len > keybag.Length - i - 8) throw new InvalidDataException("Damaged backup keybag.");
            if (tag == "UUID" && top.ContainsKey("UUID")) classes.Add(cur = []); // the first UUID is the keybag's, then one per class
            (cur ?? top)[tag] = keybag.AsSpan(i + 8, len).ToArray();
            i += 8 + len;
        }
        byte[] Need(string tag) => top.GetValueOrDefault(tag) ?? throw new InvalidDataException($"Backup keybag without {tag}.");
        var pw = Encoding.UTF8.GetBytes(password);
        if (top.TryGetValue("DPSL", out var dpsl)) pw = Rfc2898DeriveBytes.Pbkdf2(pw, dpsl, Int(Need("DPIC")), HashAlgorithmName.SHA256, 32);
        var kek = Rfc2898DeriveBytes.Pbkdf2(pw, Need("SALT"), Int(Need("ITER")), HashAlgorithmName.SHA1, 32);
        var keys = new Dictionary<int, byte[]>();
        foreach (var c in classes)
            if (c.TryGetValue("CLAS", out var cls) && c.TryGetValue("WPKY", out var wpky) && c.TryGetValue("WRAP", out var wrap) && (Int(wrap) & 2) != 0)
                keys[Int(cls)] = Unwrap(kek, wpky) ?? throw new CryptographicException("Wrong backup password.");
        return keys.Count > 0 ? keys : throw new InvalidDataException("The backup keybag has no key the password can unlock.");
    }

    static int Int(byte[] v) => v.Length is > 0 and <= 4 ? (int)v.Aggregate(0u, (a, x) => a << 8 | x) : throw new InvalidDataException("Damaged backup keybag.");

    /// <summary>A key wrapped as Apple stores it: 4 bytes of protection class (little-endian), then the RFC 3394 wrap.</summary>
    public static byte[] UnwrapClassed(IReadOnlyDictionary<int, byte[]> classKeys, ReadOnlySpan<byte> wrapped) =>
        wrapped.Length > 4 && classKeys.TryGetValue(BinaryPrimitives.ReadInt32LittleEndian(wrapped), out var kek) && Unwrap(kek, wrapped[4..]) is { } k
            ? k : throw new InvalidDataException("The file's key cannot be unwrapped with the backup's class keys.");

    /// <summary>AES key unwrap (RFC 3394); null when the integrity check fails, i.e. the wrong key.</summary>
    public static byte[]? Unwrap(byte[] kek, ReadOnlySpan<byte> wrapped)
    {
        if (wrapped.Length < 24 || wrapped.Length % 8 != 0) return null;
        int n = wrapped.Length / 8 - 1;
        var a = BinaryPrimitives.ReadUInt64BigEndian(wrapped);
        var r = new ulong[n];
        for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadUInt64BigEndian(wrapped[(8 + 8 * i)..]);
        using var aes = Aes.Create();
        aes.Key = kek;
        Span<byte> block = stackalloc byte[16], plain = stackalloc byte[16];
        for (int j = 5; j >= 0; j--)
            for (int i = n - 1; i >= 0; i--)
            {
                BinaryPrimitives.WriteUInt64BigEndian(block, a ^ (ulong)(n * j + i + 1));
                BinaryPrimitives.WriteUInt64BigEndian(block[8..], r[i]);
                aes.DecryptEcb(block, plain, PaddingMode.None);
                a = BinaryPrimitives.ReadUInt64BigEndian(plain);
                r[i] = BinaryPrimitives.ReadUInt64BigEndian(plain[8..]);
            }
        if (a != 0xA6A6A6A6A6A6A6A6) return null;
        var key = new byte[8 * n];
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8 * i), r[i]);
        return key;
    }

    /// <summary>The plaintext of an encrypted backup file, cut at its recorded size (AES-256-CBC, zero IV, padded).</summary>
    public static Stream Decrypt(Stream encrypted, byte[] key, long size)
    {
        var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Padding = PaddingMode.None; // the recorded size, not the padding, says where the data ends
        return new Head(new CryptoStream(encrypted, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: true), size, aes);
    }

    /// <summary>The (size, wrapped key) of a backup file from its Files.file record (an NSKeyedArchiver plist).</summary>
    public static (long Size, byte[] Key)? FileRecord(byte[] blob)
    {
        if (Bplist.Parse(blob) is not Dictionary<string, object?> d || d.GetValueOrDefault("$objects") is not List<object?> objs) return null;
        object? At(object? uid) => uid is Bplist.Uid u && u.Value >= 0 && u.Value < objs.Count ? objs[(int)u.Value] : null;
        var root = (d.GetValueOrDefault("$top") as Dictionary<string, object?>)?.GetValueOrDefault("root") is { } r ? At(r) as Dictionary<string, object?> : null;
        var key = At(root?.GetValueOrDefault("EncryptionKey")) switch
        {
            Dictionary<string, object?> ns => ns.GetValueOrDefault("NS.data") as byte[],
            byte[] raw => raw,
            _ => null,
        };
        return root?.GetValueOrDefault("Size") is long size && key is not null ? (size, key) : null;
    }

    /// <summary>At most <c>size</c> bytes of a stream; owns the stream and the cipher.</summary>
    sealed class Head(Stream inner, long size, IDisposable cipher) : Stream
    {
        readonly long _size = size;
        long _left = size;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _size;
        public override long Position { get => _size - _left; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            int n;
            try { n = inner.Read(buffer, offset, (int)Math.Min(count, _left)); }
            catch (CryptographicException e) { throw new InvalidDataException("The file cannot be decrypted: " + e.Message, e); }
            _left -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); cipher.Dispose(); } base.Dispose(disposing); }
    }
}
