using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>
/// Cryptocurrency addresses in free text, reported only when their checksum holds, so a random token that looks like an
/// address is not one: Base58Check (Bitcoin, Litecoin, Dogecoin, Tron, XRP in its own alphabet), Bech32/Bech32m (bc1,
/// ltc1, tb1, Cosmos chains, Cardano), Monero standard and subaddresses (Keccak-256), Ethereum in mixed case (EIP-55),
/// TON user-friendly addresses (CRC-16). An Ethereum address written in one case, and a Solana address (32 bytes in Base58,
/// letters of both cases and a digit) carry no checksum and their type says so. BIP-39 seed phrases of 12 to 24 words from the English wordlist, when the checksum the
/// last word carries holds.
/// Precision: a candidate that is only part of a longer token (a JWT, base64, a URL-safe id) is not an address, nor is one in
/// a value named as a secret (token, auth, cookie, session, signature, hash...). A Solana address, having no checksum, also
/// needs a word of its own around it (solana, wallet, address, pubkey...). A seed phrase shorter than 24 words, whose
/// checksum one random run in 16 to 128 passes, needs to be the whole value or to have a word such as seed, mnemonic or
/// recovery in its value or name.
/// </summary>
public static partial class CryptoAddresses
{
    public const string Collection = "Crypto addresses";
    const string B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    const string B32 = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    const string Ripple = "rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz";

    /// <summary>Bech32 prefixes and the chain each names.</summary>
    static readonly Dictionary<string, string> Hrps = new()
    {
        ["bc"] = "bitcoin", ["tb"] = "bitcoin testnet", ["ltc"] = "litecoin", ["cosmos"] = "cosmos", ["osmo"] = "osmosis",
        ["terra"] = "terra", ["inj"] = "injective", ["sei"] = "sei", ["celestia"] = "celestia", ["juno"] = "juno", ["kava"] = "kava",
        ["akash"] = "akash", ["bnb"] = "bnb beacon chain", ["thor"] = "thorchain", ["addr"] = "cardano", ["stake"] = "cardano stake",
    };

    [GeneratedRegex(@"(?<![1-9A-HJ-NP-Za-km-z])[13LMDT][1-9A-HJ-NP-Za-km-z]{25,34}(?![1-9A-HJ-NP-Za-km-z])")]
    private static partial Regex Base58Token();

    [GeneratedRegex(@"(?<![a-z0-9])(?:bc|ltc|tb|cosmos|osmo|terra|inj|sei|celestia|juno|kava|akash|bnb|thor|addr|stake)1[ac-hj-np-z02-9]{11,110}(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Bech32Token();

    [GeneratedRegex(@"(?<![0-9a-z])0x[0-9a-f]{40}(?![0-9a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex EthereumToken();

    [GeneratedRegex(@"(?<![1-9A-HJ-NP-Za-km-z])[48][1-9A-HJ-NP-Za-km-z]{94}(?![1-9A-HJ-NP-Za-km-z])")]
    private static partial Regex MoneroToken();

    [GeneratedRegex(@"(?<![0-9A-Za-z])r[1-9A-HJ-NP-Za-km-z]{24,34}(?![0-9A-Za-z])")]
    private static partial Regex RippleToken();

    [GeneratedRegex(@"(?<![1-9A-HJ-NP-Za-km-z])[1-9A-HJ-NP-Za-km-z]{32,44}(?![1-9A-HJ-NP-Za-km-z])")]
    private static partial Regex SolanaToken();

    [GeneratedRegex(@"(?<![A-Za-z0-9_+/=-])[EUk0]Q[A-Za-z0-9_+/-]{46}(?![A-Za-z0-9_+/=-])")]
    private static partial Regex TonToken();

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex WordToken();

    /// <summary>The BIP-39 English wordlist (github.com/bitcoin/bips, bip-0039/english.txt), word to index.</summary>
    static readonly Lazy<Dictionary<string, int>> Bip39 = new(() =>
    {
        using var r = new StreamReader(typeof(CryptoAddresses).Assembly.GetManifestResourceStream("bip39-english.txt")!);
        var d = new Dictionary<string, int>(2048);
        for (string? w; (w = r.ReadLine()) is not null;) if (w.Length > 0) d[w] = d.Count;
        return d;
    });

    static readonly Dictionary<byte, string> Versions = new()
    {
        [0x00] = "bitcoin", [0x05] = "bitcoin", [0x30] = "litecoin", [0x32] = "litecoin", [0x1E] = "dogecoin", [0x41] = "tron",
    };

    [GeneratedRegex(@"token|auth|jwt|bearer|session|cookie|signature|secret|nonce|hmac|csrf|api.?key|password|passwd|hash|digest|checksum|fingerprint|cipher|encrypted|salt|\biv\b|uuid|guid", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"solana|\bsol\b|phantom|solflare|solscan|wallet|address|pubkey|public.?key|recipient|receiver|sender|account|deposit|withdraw|indirizzo|portafoglio", RegexOptions.IgnoreCase)]
    private static partial Regex SolanaContext();

    [GeneratedRegex(@"seed|mnemonic|recovery|backup|secret|phrase|wallet|words|frase|parole|ripristino|recupero|semi", RegexOptions.IgnoreCase)]
    private static partial Regex SeedContext();

    [GeneratedRegex(@"[^A-Za-z]+")]
    private static partial Regex NotLetters();

    /// <summary>The characters of tokens addresses can hide in: base64, base64url, JWT segments.</summary>
    static bool TokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '_' or '-' or '.';

    /// <summary>True when the match is a token of its own: the run of token characters around it, less a sentence's
    /// closing full stop, is the match itself.</summary>
    static bool Standalone(string cell, Match m)
    {
        int a = m.Index, b = m.Index + m.Length;
        while (a > 0 && TokenChar(cell[a - 1])) a--;
        while (b < cell.Length && TokenChar(cell[b])) b++;
        while (b > m.Index + m.Length && cell[b - 1] is '.' or '-' or '_') b--;
        return a == m.Index && b == m.Index + m.Length;
    }

    public const string WalletCollection = "Crypto wallet apps";

    /// <summary>Android package names of wallet and exchange apps (from their store listings), matched as app ids in the
    /// records and as folders of the extraction: an app's presence, not its use.</summary>
    static readonly string[] WalletPackages =
    [
        "com.wallet.crypto.trustapp", "io.metamask", "org.toshi", "com.coinbase.android", "com.binance.dev", "exodusmovement.exodus",
        "app.phantom", "io.atomicwallet", "com.crypto.multiwallet", "com.m2049r.xmrwallet", "org.electrum.electrum",
        "io.bluewallet.bluewallet", "com.mycelium.wallet", "com.samourai.wallet", "com.ledger.live", "co.mona.android",
        "piuk.blockchain.android", "com.bitpay.wallet", "io.horizontalsystems.bankwallet", "com.coinomi.wallet", "com.zengo.wallet",
        "vip.mytokenpocket", "im.token.app", "com.tronlinkpro.wallet", "com.bitcoin.mwallet", "com.okinc.okex.gp", "com.kubi.kucoin",
        "com.bybit.app", "me.rainbow", "com.solflare.mobile", "io.muun.apollo", "co.edgesecure.app", "com.livingroomofsatoshi.wallet",
        "de.schildbach.wallet", "com.greenaddress.greenbits_android_wallet",
    ];

    public static IndicatorSet WalletApps() =>
        IndicatorSet.From(WalletPackages.Select(p => new Indicator("app:id", p, WalletCollection, "built-in")));

    public static IEnumerable<Indicator> Match(string cell) => Match(cell, "");

    /// <summary>The addresses and seed phrases of a cell; <paramref name="context"/> is what the value is called (its
    /// column, its JSON path).</summary>
    public static IEnumerable<Indicator> Match(string cell, string context)
    {
        var found = new HashSet<Indicator>();
        var secret = SecretName().IsMatch(context);
        var jwt = cell.TrimStart().StartsWith("eyJ", StringComparison.Ordinal) && cell.Count(c => c == '.') >= 2;
        foreach (var phrase in SeedPhrases(cell))
        {
            var words = phrase.Split(' ').Length;
            var whole = NotLetters().Replace(cell, " ").Trim().Equals(phrase, StringComparison.OrdinalIgnoreCase);
            if (words == 24 || whole || SeedContext().IsMatch(context) || SeedContext().IsMatch(NotLetters().Replace(cell, " ").Replace(phrase, "", StringComparison.OrdinalIgnoreCase)))
                found.Add(Hit($"BIP-39 seed phrase ({words} words, checksum holds)", phrase));
        }
        if (secret || jwt) return found; // a value named as a secret holds random bytes, not addresses
        foreach (var hit in Addresses(cell, context)) found.Add(hit);
        return found;
    }

    static IEnumerable<Indicator> Addresses(string cell, string context)
    {
        var found = new HashSet<Indicator>();
        foreach (Match m in Base58Token().Matches(cell))
            if (Base58Check(m.Value, B58) is { Length: 21 } p && Versions.TryGetValue(p[0], out var coin))
                found.Add(Hit($"{coin} address", m.Value));
        foreach (Match m in RippleToken().Matches(cell))
            if (Base58Check(m.Value, Ripple) is [0, ..] and { Length: 21 }) found.Add(Hit("xrp address", m.Value));
        foreach (Match m in Bech32Token().Matches(cell))
            if (Bech32(m.Value) is { } hrp && Hrps.TryGetValue(hrp, out var chain)) found.Add(Hit($"{chain} address", m.Value));
        foreach (Match m in TonToken().Matches(cell))
            if (TonValid(m.Value)) found.Add(Hit("ton address", m.Value));
        foreach (Match m in SolanaToken().Matches(cell))
            if (m.Value.Any(char.IsUpper) && m.Value.Any(char.IsLower) && m.Value.Any(char.IsDigit) && !found.Any(f => f.Value == m.Value)
                && Standalone(cell, m) && SolanaNamed(cell, m, context) && Base58(m.Value, B58) is { Length: 32 })
                found.Add(Hit("solana address (no checksum)", m.Value));
        foreach (Match m in EthereumToken().Matches(cell))
            if (Eip55(m.Value[2..]) is { } checksummed) found.Add(Hit(checksummed ? "ethereum address" : "ethereum address (no checksum, one case)", m.Value));
        foreach (Match m in MoneroToken().Matches(cell))
            if (MoneroValid(m.Value)) found.Add(Hit("monero address", m.Value));
        // a checksum can still hold by chance inside a long random token: an address is a token of its own
        return found.Where(f => Regex.Matches(cell, Regex.Escape(f.Value)).Any(m => Standalone(cell, m)));
    }

    /// <summary>A word naming a Solana address in the value's name, or within 60 characters of it.</summary>
    static bool SolanaNamed(string cell, Match m, string context)
    {
        if (SolanaContext().IsMatch(context)) return true;
        var from = Math.Max(0, m.Index - 60);
        var around = cell[from..m.Index] + " " + cell[(m.Index + m.Length)..Math.Min(cell.Length, m.Index + m.Length + 60)];
        return SolanaContext().IsMatch(around);
    }

    /// <summary>Runs of wordlist words, separated by anything without letters (spaces, line breaks, "2."), read as
    /// 24, 21, 18, 15 or 12 words, longest first, and kept when the checksum holds. A 12-word checksum is 4 bits, so one in
    /// sixteen random runs of 12 wordlist words passes; the type says the checksum holds, not that a wallet used it.</summary>
    static IEnumerable<string> SeedPhrases(string cell)
    {
        var list = Bip39.Value;
        var run = new List<(int Word, string Text, int End)>();
        foreach (Match? m in WordToken().Matches(cell).Append(null))
        {
            var known = m is not null && list.TryGetValue(m.Value.ToLowerInvariant(), out _);
            if (m is null || !known || (run.Count > 0 && m.Index - run[^1].End > 6))
            {
                for (int i = 0; i + 12 <= run.Count;)
                {
                    var len = new[] { 24, 21, 18, 15, 12 }.FirstOrDefault(n => i + n <= run.Count && ChecksumHolds(run, i, n));
                    if (len == 0) { i++; continue; }
                    yield return string.Join(' ', run.Skip(i).Take(len).Select(w => w.Text));
                    i += len;
                }
                run.Clear();
            }
            if (known) run.Add((list[m!.Value.ToLowerInvariant()], m.Value.ToLowerInvariant(), m.Index + m.Length));
        }
    }

    /// <summary>11 bits per word: the entropy, then its SHA-256's first entropy/32 bits (BIP-39).</summary>
    static bool ChecksumHolds(List<(int Word, string Text, int End)> run, int start, int count)
    {
        BigInteger n = 0;
        for (int i = start; i < start + count; i++) n = (n << 11) | run[i].Word;
        var cs = count * 11 / 33;
        var entropy = (n >> cs).ToByteArray(isUnsigned: true, isBigEndian: true);
        var bytes = new byte[count * 11 * 32 / 33 / 8];
        entropy.CopyTo(bytes, bytes.Length - entropy.Length);
        return SHA256.HashData(bytes)[0] >> (8 - cs) == (int)(n & ((1 << cs) - 1));
    }

    static Indicator Hit(string type, string value) => new(type, value, Collection, "built-in");

    /// <summary>The bytes a Base58 string in <paramref name="alphabet"/> holds, each leading zero character a zero byte.</summary>
    static byte[] Base58(string s, string alphabet)
    {
        BigInteger n = 0;
        foreach (var c in s) n = n * 58 + alphabet.IndexOf(c);
        var body = n.IsZero ? [] : n.ToByteArray(isUnsigned: true, isBigEndian: true);
        var bytes = new byte[s.TakeWhile(c => c == alphabet[0]).Count() + body.Length];
        body.CopyTo(bytes, bytes.Length - body.Length);
        return bytes;
    }

    /// <summary>The payload without its 4-byte checksum, or null when the checksum fails.</summary>
    static byte[]? Base58Check(string s, string alphabet)
    {
        var bytes = Base58(s, alphabet);
        if (bytes.Length < 5) return null;
        var payload = bytes[..^4];
        return SHA256.HashData(SHA256.HashData(payload)).AsSpan(0, 4).SequenceEqual(bytes.AsSpan(^4)) ? payload : null;
    }

    /// <summary>A TON user-friendly address: 36 bytes in base64 or base64url, a flag (0x11 bounceable, 0x51 not, 0x80 added on
    /// testnet), the workchain, the 32-byte account and a CRC-16/XMODEM of the 34 before it.</summary>
    static bool TonValid(string s)
    {
        byte[] b;
        try { b = Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/')); }
        catch (FormatException) { return false; }
        if (b.Length != 36 || (b[0] & 0x7F) is not (0x11 or 0x51)) return false;
        ushort crc = 0;
        foreach (var x in b.AsSpan(0, 34))
        {
            crc ^= (ushort)(x << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return b[34] == crc >> 8 && b[35] == (crc & 0xFF);
    }

    /// <summary>True when the mixed-case hex follows EIP-55, false when it is all one case (no checksum), null when the
    /// case is mixed but wrong.</summary>
    static bool? Eip55(string hex)
    {
        var letters = hex.Where(char.IsLetter).ToList();
        if (letters.All(char.IsLower) || letters.All(char.IsUpper)) return false;
        var hash = Keccak256(System.Text.Encoding.ASCII.GetBytes(hex.ToLowerInvariant()));
        for (int i = 0; i < 40; i++)
        {
            if (!char.IsLetter(hex[i])) continue;
            var nibble = (hash[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF;
            if (char.IsUpper(hex[i]) != nibble >= 8) return null;
        }
        return true;
    }

    /// <summary>Monero's Base58 works in blocks of 8 bytes (11 characters); 95 characters are 69 bytes: network byte
    /// (18 standard, 42 subaddress), spend key, view key and the first 4 bytes of Keccak-256 over the 65 before them.</summary>
    static bool MoneroValid(string s)
    {
        int[] charsForBytes = [0, 2, 3, 5, 6, 7, 9, 10, 11];
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i += 11)
        {
            var block = s.Substring(i, Math.Min(11, s.Length - i));
            var n = Array.IndexOf(charsForBytes, block.Length);
            BigInteger v = 0;
            foreach (var c in block) v = v * 58 + B58.IndexOf(c);
            var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (n <= 0 || b.Length > n) return false;
            bytes.AddRange(new byte[n - b.Length]);
            bytes.AddRange(b);
        }
        var all = bytes.ToArray();
        return all.Length == 69 && all[0] is (18 or 42) && Keccak256(all.AsSpan(0, 65)).AsSpan(0, 4).SequenceEqual(all.AsSpan(65));
    }

    /// <summary>Keccak-256 as Ethereum and Monero use it (the original padding, not SHA3-256).</summary>
    internal static byte[] Keccak256(ReadOnlySpan<byte> data)
    {
        const int rate = 136;
        var st = new ulong[25];
        for (; data.Length >= rate; data = data[rate..]) Absorb(st, data[..rate]);
        var last = new byte[rate];
        data.CopyTo(last);
        last[data.Length] ^= 0x01;
        last[rate - 1] ^= 0x80;
        Absorb(st, last);
        var digest = new byte[32];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(i * 8), st[i]);
        return digest;
    }

    static void Absorb(ulong[] st, ReadOnlySpan<byte> block)
    {
        for (int i = 0; i < 17; i++) st[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);
        KeccakF(st);
    }

    static readonly ulong[] RoundConstants =
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B, 0x0000000080000001,
        0x8000000080008081, 0x8000000000008009, 0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
        0x000000000000800A, 0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ];
    static readonly int[] Rotations = [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];
    static readonly int[] Lanes = [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

    static void KeccakF(ulong[] st)
    {
        Span<ulong> bc = stackalloc ulong[5];
        for (int r = 0; r < 24; r++)
        {
            for (int i = 0; i < 5; i++) bc[i] = st[i] ^ st[i + 5] ^ st[i + 10] ^ st[i + 15] ^ st[i + 20];
            for (int i = 0; i < 5; i++)
            {
                var t = bc[(i + 4) % 5] ^ BitOperations.RotateLeft(bc[(i + 1) % 5], 1);
                for (int j = 0; j < 25; j += 5) st[j + i] ^= t;
            }
            var carry = st[1];
            for (int i = 0; i < 24; i++) (st[Lanes[i]], carry) = (BitOperations.RotateLeft(carry, Rotations[i]), st[Lanes[i]]);
            for (int j = 0; j < 25; j += 5)
            {
                for (int i = 0; i < 5; i++) bc[i] = st[j + i];
                for (int i = 0; i < 5; i++) st[j + i] ^= ~bc[(i + 1) % 5] & bc[(i + 2) % 5];
            }
            st[0] ^= RoundConstants[r];
        }
    }

    /// <summary>The human-readable part when the Bech32 (BIP-173) or Bech32m (BIP-350) checksum holds, else null.</summary>
    static string? Bech32(string s)
    {
        if (s != s.ToLowerInvariant() && s != s.ToUpperInvariant()) return null; // mixed case is invalid by definition
        s = s.ToLowerInvariant();
        var sep = s.LastIndexOf('1');
        var hrp = s[..sep];
        var values = hrp.Select(c => c >> 5).Append(0).Concat(hrp.Select(c => c & 31)).Concat(s[(sep + 1)..].Select(c => B32.IndexOf(c)));
        uint chk = 1;
        uint[] gen = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ (uint)v;
            for (int i = 0; i < 5; i++) if (((top >> i) & 1) != 0) chk ^= gen[i];
        }
        return chk is 1 or 0x2bc830a3 ? hrp : null;
    }
}
