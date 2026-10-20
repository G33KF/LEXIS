namespace AleappReader.Core.Tests;

public class IndicatorTests : IDisposable
{
    readonly SyntheticCase _c = new();
    public void Dispose() => _c.Dispose();

    string Stix(params string[] patterns)
    {
        var path = Path.Combine(_c.Root, $"iocs{Guid.NewGuid():N}.stix2");
        var objects = patterns.Select((p, i) => $$"""{"type":"indicator","id":"indicator--{{i}}","pattern":"{{p.Replace("\"", "\\\"")}}"}""");
        File.WriteAllText(path, $$"""{"type":"bundle","objects":[{"type":"malware","name":"Spy"},{{string.Join(",", objects)}}]}""");
        return path;
    }

    [Fact]
    public void Indicators_match_by_type_rule_and_not_by_substring()
    {
        var set = IndicatorSet.Load([Stix(
            "[domain-name:value = 'evil.example']",
            "[url:value = 'https://cdn.example.net/x/payload'] ",
            "[process:name = 'bh' OR app:id = 'com.spy.agent']",
            "[file:path = '/private/var/db/spy.staging/']",
            "[file:hashes.'SHA-256' = 'ABCDEF01']")]);
        Assert.Equal(6, set.Count);
        string[] Types(string cell) => set.Match(cell).Select(i => i.Type).Order().ToArray();

        Assert.Equal(["domain-name:value"], Types("see https://a.b.evil.example/path"));
        Assert.Equal(["domain-name:value"], Types("EVIL.EXAMPLE"));
        Assert.Empty(Types("notevil.example and evil.examples.org"));
        Assert.Equal(["url:value"], Types("GET https://cdn.example.net/x/payload?id=1"));
        Assert.Empty(Types("https://cdn.example.net/other"));
        Assert.Equal(["process:name"], Types("/usr/libexec/bh"));
        Assert.Empty(Types("bh is short")); // a bare name matches a whole cell or a path end only
        Assert.Equal(["app:id"], Types("Containers/Bundle/com.spy.agent/Info.plist"));
        Assert.Equal(["file:path"], Types("private/var/db/spy.staging/x.db"));
        Assert.Equal(["file:hashes.sha256"], Types("abcdef01"));
        Assert.All(set.Match("evil.example"), i => Assert.Equal("Spy", i.Collection));
    }

    [Fact]
    public void Scan_reports_the_artifact_row_and_column_of_each_match()
    {
        using var s = CaseSession.Open(_c.Root);
        var hits = IndicatorSet.Load([Stix("[process:name = 'June note']")]).Scan(s);
        var hit = Assert.Single(hits);
        Assert.Equal("Chat Messages", hit.Artifact?.Name);
        Assert.Equal("Body", hit.Column);
        Assert.Throws<InvalidDataException>(() => IndicatorSet.Load([Path.Combine(_c.Root, Write("x.json", "{}"))]));
    }

    [Fact]
    public void The_file_list_scan_reports_each_matching_folder_once()
    {
        var set = RootTraces.Set();
        var hits = set.ScanFiles([
            "Dump/data/adb/magisk/busybox", "Dump/data/adb/magisk/magisk64",           // one folder, one hit
            "Dump/system/xbin/su", "Dump/system/xbin/sudo",                            // su yes, sudo no
            "Dump/data/data/com.topjohnwu.magisk/shared_prefs/a.xml",                  // app id as a folder
            "filesystem1/private/var/jb/usr/bin/x",                                    // /var/jb under /private
            "Dump/data/data/com.example.notes/files/su.txt"]);
        Assert.Equal(["Dump/data/adb/magisk", "Dump/data/data/com.topjohnwu.magisk", "Dump/system/xbin/su", "filesystem1/private/var/jb"],
                     hits.Select(h => h.Cell).Order().ToArray());
        Assert.All(hits, h => Assert.Equal("Extraction file list", h.Where));
        Assert.Equal(["app:id"], set.Match("installed: com.topjohnwu.magisk 27.0").Select(i => i.Type).ToArray());
    }

    [Fact]
    public void A_hash_set_matches_media_of_the_extraction_by_content()
    {
        var ext = Path.Combine(_c.Root, "ext");
        Directory.CreateDirectory(Path.Combine(ext, "DCIM"));
        File.WriteAllText(Path.Combine(ext, "DCIM", "a.jpg"), "abc");   // MD5 900150983cd24fb0d6963f7d28e17f72
        File.WriteAllText(Path.Combine(ext, "DCIM", "b.jpg"), "abd");
        var report = Path.Combine(_c.Root, "rep");
        Directory.CreateDirectory(report);
        File.WriteAllText(Path.Combine(report, "_lava_data.lava"),
            $$$"""{"param_input": {{{System.Text.Json.JsonSerializer.Serialize(ext)}}}, "lava_db_name": "_lava_artifacts.db", "artifacts": {}}""");
        File.Copy(Path.Combine(_c.Report, "_lava_artifacts.db"), Path.Combine(report, "_lava_artifacts.db"));
        File.WriteAllText(Path.Combine(_c.Root, "known.txt"), "900150983CD24FB0D6963F7D28E17F72  known.jpg\n# a comment\n");
        using var s = CaseSession.Open(report);
        var set = IndicatorSet.Load([Path.Combine(_c.Root, "known.txt")]);
        Assert.Equal(1, set.Count);
        var hit = Assert.Single(set.ScanMediaHashes(s.Extraction!));
        Assert.Equal("DCIM/a.jpg", hit.Cell);
        Assert.Equal(("file:hashes.md5", "known"), (hit.Indicator.Type, hit.Indicator.Collection));
    }

    [Fact]
    public void Crypto_addresses_are_reported_only_when_their_checksum_holds()
    {
        string[] Types(string cell) => CryptoAddresses.Match(cell).Select(i => i.Type).Order().ToArray();
        // Public test vectors: the genesis block address, the Bitcoin wiki P2SH example, BIP-173 and BIP-350.
        Assert.Equal(["bitcoin address"], Types("pay 1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa now"));
        Assert.Equal(["bitcoin address"], Types("3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy"));
        Assert.Equal(["bitcoin address"], Types("BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4"));
        Assert.Equal(["bitcoin address"], Types("bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0"));
        Assert.Empty(Types("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNb"));          // one character changed
        Assert.Empty(Types("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5"));
        Assert.Empty(Types("Bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4"));  // mixed case
        // EIP-55 test vectors; one letter's case flipped breaks the checksum; one case has none.
        Assert.Equal(["ethereum address"], Types("to 0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed."));
        Assert.Equal(["ethereum address"], Types("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb"));
        Assert.Empty(Types("0x5AAeb6053F3E94C9b9A09f33669435E7Ef1BeAed"));
        Assert.Equal(["ethereum address (no checksum, one case)"], Types("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed"));
        // The Monero project's general fund address, and the same with one character changed.
        Assert.Equal(["monero address"], Types("44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3A"));
        Assert.Empty(Types("44AFFq5kSiGBoZ4NMDwYtN18obc8AemS33DBLWs3H7otXft3XjrpDtQGv7SqSsaBYBb98uNbr2VBBEt7f2wfn3RVGQBEP3B"));
        // BIP-39 test vectors (all-zero entropy): the last word carries the checksum; numbered lists and line breaks pass.
        var zero12 = string.Join(' ', Enumerable.Repeat("abandon", 11)) + " about";
        Assert.Equal(["BIP-39 seed phrase (12 words, checksum holds)"], Types("seed: " + zero12 + "."));
        Assert.Equal(["BIP-39 seed phrase (12 words, checksum holds)"],
            Types(string.Join("\n", zero12.Split(' ').Select((w, i) => $"{i + 1}. {w}"))));
        Assert.Equal(["BIP-39 seed phrase (24 words, checksum holds)"],
            Types(string.Join(' ', Enumerable.Repeat("Abandon", 23)) + " art"));
        Assert.Empty(Types(string.Join(' ', Enumerable.Repeat("abandon", 12))));
        // XRP's genesis account (Base58Check in Ripple's alphabet); Solana's token program (32 bytes, no checksum)
        Assert.Equal(["xrp address"], Types("rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh"));
        Assert.Empty(Types("rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTi"));
        Assert.Equal(["solana address (no checksum)"], Types("program address TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA"));
        // TON Foundation's published address (CRC-16); one character changed
        Assert.Equal(["ton address"], Types("EQCD39VS5jcptHL8vMjEXrzGaRcCVYto7HUn4bpAOg8xqB2N"));
        Assert.Empty(Types("EQCD39VS5jcptHL8vMjEXrzGaRcCVYto7HUn4bpAOg8xqB2M"));
    }

    [Fact]
    public void Addresses_inside_tokens_or_secrets_and_seed_like_word_runs_are_not_reported()
    {
        string[] Types(string cell, string context = "") => CryptoAddresses.Match(cell, context).Select(i => i.Type).Order().ToArray();
        const string sol = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", btc = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa";
        Assert.Empty(Types(sol));                                       // no checksum and nothing names it
        Assert.Single(Types(sol, "wallet.pubkey"));
        Assert.Empty(Types("wallet " + sol + "x9_Qe-Zz"));               // part of a longer url-safe token
        Assert.Empty(Types($"eyJhbGciOiJIUzI1NiJ9.{btc}.c2lnbmF0dXJl")); // a JWT
        Assert.Empty(Types(btc, "auth_token"));                           // a value named as a secret
        Assert.Empty(Types("aGVsbG8+" + btc + "/d29ybGQ="));             // inside base64
        Assert.Single(Types("bitcoin:" + btc + "?amount=1"));            // a payment URI
        // 12 wordlist words whose checksum holds, inside longer text and named by nothing, are not a seed phrase
        var zero12 = string.Join(' ', Enumerable.Repeat("abandon", 11)) + " about";
        Assert.Empty(Types("the plan was " + zero12 + " and so on today"));
        Assert.Single(Types("the plan was " + zero12 + " and so on today", "notes.mnemonic"));
        Assert.Single(Types(zero12));                                    // the whole value
    }

    [Fact]
    public void A_json_cell_is_scanned_value_by_value_with_the_path_of_each_match()
    {
        using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_c.Report, "_lava_artifacts.db")};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            var words = string.Join(",", Enumerable.Repeat("\"abandon\"", 11).Append("\"about\""));
            cmd.CommandText = "INSERT INTO get_chat (timestamp, thread_id, body) VALUES (1714557600, 't', $b)";
            cmd.Parameters.AddWithValue("$b", $$"""{"wallet":{"address":"1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa"},"backup":[{{words}}],"note":"line\nbreak"}""");
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_c.Root);
        var hits = IndicatorSet.Scan(s, (Func<string, string, IEnumerable<Indicator>>)CryptoAddresses.Match);
        Assert.Contains(hits, h => h.Column == "Body › wallet.address" && h.Cell == "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa");
        Assert.Contains(hits, h => h.Column == "Body › backup" && h.Indicator.Type.StartsWith("BIP-39 seed phrase (12 words"));
    }

    [Fact]
    public void Tag_rules_match_keywords_and_regexes_and_carry_their_tag()
    {
        var rules = TagRules.Load(Path.Combine(_c.Root, Write("r.txt", "# rules\nMeeting: june NOTE\nDigits: /\\d{4}-\\d{2}/\n")));
        var match = TagRules.Matcher(rules);
        Assert.Equal(["Meeting"], match("the June note").Select(i => i.Collection));
        Assert.Equal(["Digits"], match("on 2024-05").Select(i => i.Collection));
        using var s = CaseSession.Open(_c.Root);
        Assert.Contains(IndicatorSet.Scan(s, match), h => h.Indicator.Collection == "Meeting" && h.Column == "Body");
        Assert.Throws<InvalidDataException>(() => TagRules.Load(Path.Combine(_c.Root, Write("bad.txt", "no colon here"))));
    }

    CaseSession WithExtraction(string input, string name)
    {
        var report = Path.Combine(_c.Root, name);
        Directory.CreateDirectory(report);
        File.WriteAllText(Path.Combine(report, "_lava_data.lava"),
            $$$"""{"param_input": {{{System.Text.Json.JsonSerializer.Serialize(input)}}}, "lava_db_name": "_lava_artifacts.db", "artifacts": {}}""");
        File.Copy(Path.Combine(_c.Report, "_lava_artifacts.db"), Path.Combine(report, "_lava_artifacts.db"));
        return CaseSession.Open(report);
    }

    [Fact]
    public void The_content_root_names_the_files_not_the_folder_or_zip_holding_them()
    {
        var ext = Path.Combine(_c.Root, "dump");
        Directory.CreateDirectory(Path.Combine(ext, "b"));
        File.WriteAllText(Path.Combine(ext, "b", "x.db"), "abc");
        File.WriteAllText(Path.Combine(ext, "a.txt"), "");
        var zip = Path.Combine(_c.Root, "dump.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(ext, zip, System.IO.Compression.CompressionLevel.Fastest, false);
        using var d = WithExtraction(ext, "r1");
        using var z = WithExtraction(zip, "r2");
        var (manifest, root, unreadable) = d.Extraction!.ContentManifest();
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  a.txt\n" +
                     "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  b/x.db\n", manifest);
        Assert.Empty(unreadable);
        Assert.Equal(root, z.Extraction!.ContentManifest().Root);
        File.WriteAllText(Path.Combine(ext, "a.txt"), " ");
        Assert.NotEqual(root, d.Extraction.ContentManifest().Root);
    }

    [Fact]
    public void Raw_files_are_typed_by_content_and_searched_as_utf8_and_utf16()
    {
        var ext = Path.Combine(_c.Root, "raw");
        Directory.CreateDirectory(ext);
        File.WriteAllBytes(Path.Combine(ext, "noext"), [.. "SQLite format 3\0"u8, .. new byte[100], .. System.Text.Encoding.Unicode.GetBytes("Secret Plan")]);
        var big = new byte[(1 << 20) + 10];
        "SECRET"u8.CopyTo(big.AsSpan((1 << 20) - 3)); // across the 1 MB read boundary
        File.WriteAllBytes(Path.Combine(ext, "big.bin"), big);
        File.WriteAllText(Path.Combine(ext, "a.txt"), "secret, secret");
        File.WriteAllText(Path.Combine(ext, "b.txt"), "nothing");
        using var s = WithExtraction(ext, "r3");
        var x = s.Extraction!;
        Assert.Equal(4, x.Files().Count);
        var hits = RawFiles.Search(x, x.Files().Select(f => f.Path).ToList(), "secret").OrderBy(h => h.Path).ToList();
        Assert.Equal([("a.txt", 2, 0L, "Text"), ("big.bin", 1, (1L << 20) - 3, "Data"), ("noext", 1, 116L, "SQLite database")],
                     hits.Select(h => (h.Path, h.Count, h.FirstOffset, h.Type)));
        Assert.StartsWith("00000000  53 51 4C 69 74 65 20 66  6F 72 6D 61 74 20 33 00  SQLite format 3.", RawFiles.HexDump(RawFiles.ReadAt(x, "noext", 0, 16), 0));
    }

    [Fact]
    public void Binary_plists_render_as_text_and_damage_never_throws()
    {
        // plistlib: {"a": -5, "b": [True, "x"], "d": 2024-05-01 10:00, "f": 1.5, "u": "café", "z": b"\x01\x02"}
        var b = Convert.FromHexString("62706c6973743030d601020304050607080b0c0d0e51615162516451665175517a13fffffffffffffffba2090a0951783341c5f12510000000233ff80000000000006400630061006600e9420102081517191b1d1f212a2d2e3039424b0000000000000101000000000000000f0000000000000000000000000000004e");
        Assert.Equal("{\n  a = -5\n  b = [ 2 items\n    true\n    \"x\"\n  ]\n  d = 2024-05-01 10:00:00 UTC\n  f = 1.5\n  u = \"café\"\n  z = <data 2 bytes> 0102\n}\n",
                     Bplist.ToText(b));
        var rnd = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            var d = (byte[])b.Clone();
            d[8 + rnd.Next(d.Length - 8)] = (byte)rnd.Next(256);
            Bplist.ToText(d); // must not throw
        }
    }

    string Write(string name, string text) { File.WriteAllText(Path.Combine(_c.Root, name), text); return name; }
}
