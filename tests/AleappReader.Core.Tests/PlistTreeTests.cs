using System.Text;

namespace AleappReader.Core.Tests;

public class PlistTreeTests
{
    // plistlib FMT_BINARY of an NSKeyedArchiver NSDictionary { name: "AirTag", when: NSDate(700000000) }
    const string Archive = "YnBsaXN0MDDUAQIDBAUGIyZZJGFyY2hpdmVyWCRvYmplY3RzVCR0b3BYJHZlcnNpb25fEA9OU0tleWVkQXJjaGl2ZXKoBwgTFBUWGiBVJG51bGzTCQoLDA0QViRjbGFzc1dOUy5rZXlzWk5TLm9iamVjdHOAB6IOD4ACgAOiERKABIAFVG5hbWVUd2hlblZBaXJUYWfSCRcYGVdOUy50aW1lgAYjQcTck4AAAADSGxwdHlgkY2xhc3Nlc1okY2xhc3NuYW1loh4fVk5TRGF0ZVhOU09iamVjdNIbHCEioiIfXE5TRGljdGlvbmFyedEkJVRyb290gAESAAGGoAgRGyQpMkRNU1phaXR2eXt9gIKEiY6VmqKkrbK7xsnQ2d7h7vH2+AAAAAAAAAEBAAAAAAAAACcAAAAAAAAAAAAAAAAAAAD9";

    static string When => new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(700000000).ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    [Fact]
    public void A_binary_keyed_archive_is_resolved_into_the_objects_it_encodes()
    {
        var root = PlistTree.Read(Convert.FromBase64String(Archive))!;
        Assert.StartsWith("NSKeyedArchiver", root.Type);
        Assert.Equal(["name", "when"], root.Children.Select(c => c.Key));
        Assert.Equal("AirTag", root.Children[0].Value);
        Assert.Equal(("date", When, "when"), (root.Children[1].Type, root.Children[1].Value, root.Children[1].Path));
    }

    [Fact]
    public void Xml_plists_read_with_their_uids_nested_plists_and_blind_protobuf()
    {
        var inner = "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>serial</key><string>ABC</string><key>list</key><array><integer>7</integer></array></dict></plist>";
        byte[] proto = [0x08, 0x96, 0x01, 0x12, 0x04, 0x0A, 0x02, 0x68, 0x69];
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>when</key><date>2024-05-01T10:00:00Z</date>
              <key>odd key</key><real>1.5</real>
              <key>nested</key><data>{Convert.ToBase64String(Encoding.UTF8.GetBytes(inner))}</data>
              <key>blob</key><data>{Convert.ToBase64String(proto)}</data>
              <key>ref</key><dict><key>CF$UID</key><integer>3</integer></dict>
              <key>flag</key><true/>
            </dict></plist>
            """;
        var root = PlistTree.Read(Encoding.UTF8.GetBytes(xml))!;
        var all = Flatten(root).ToDictionary(n => n.Path);
        Assert.Equal("2024-05-01 10:00:00 UTC", all["when"].Value);
        Assert.Equal("1.5", all["[\"odd key\"]"].Value);
        Assert.Equal("data: nested plist", all["nested"].Type);
        Assert.Equal("ABC", all["nested.serial"].Value);
        Assert.Equal("7", all["nested.list[0]"].Value);
        Assert.Equal("data: protobuf (blind)", all["blob"].Type);
        Assert.Equal("150", all["blob.1"].Value);
        Assert.Equal("hi", all["blob.2.1"].Value);
        Assert.Equal(("UID", "3"), (all["ref"].Type, all["ref"].Value));
        Assert.Equal("true", all["flag"].Value);
        Assert.Contains("serial (string) = ABC", PlistTree.ToText(root));
    }

    [Fact]
    public void Not_a_plist_or_a_damaged_one_reads_as_nothing_and_text_is_not_taken_for_protobuf()
    {
        Assert.Null(PlistTree.Read(Encoding.UTF8.GetBytes("hello")));
        Assert.Null(PlistTree.Read(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><plist><dict><key>a</key>")));
        Assert.Null(PlistTree.Read([.. "bplist00"u8, 1, 2, 3]));
        var xml = "<?xml version=\"1.0\"?><plist><dict><key>t</key><data>aGVsbG8=</data></dict></plist>";
        Assert.Equal("data", PlistTree.Read(Encoding.UTF8.GetBytes(xml))!.Children[0].Type); // "hello": text, not a guessed message
        Assert.Null(PlistTree.Protobuf([0x08], 0)); // a key with no value
    }

    [Theory]
    [InlineData(1_700_000_000L, " · if a time: 2023-11-14 22:13:20 UTC (Unix s)")]
    [InlineData(1_700_000_000_123L, " · if a time: 2023-11-14 22:13:20 UTC (Unix ms)")]
    [InlineData(1_700_000_000_123_456L, " · if a time: 2023-11-14 22:13:20 UTC (Unix µs)")]
    [InlineData(1_700_000_000_123_456_789L, " · if a time: 2023-11-14 22:13:20 UTC (Unix ns)")]
    [InlineData(150L, "")]
    [InlineData(-1_700_000_000L, "")]
    public void Protobuf_numbers_in_the_range_of_a_unix_time_show_it(long v, string expected) => Assert.Equal(expected, PlistTree.AsTime(v));

    [Fact]
    public void Damaged_archives_and_blobs_never_throw()
    {
        var b = Convert.FromBase64String(Archive);
        var rnd = new Random(3);
        for (var i = 0; i < 3000; i++)
        {
            var d = (byte[])b.Clone();
            for (var k = 0; k < 1 + i % 3; k++) d[8 + rnd.Next(d.Length - 8)] = (byte)rnd.Next(256);
            if (PlistTree.Read(d) is { } t) PlistTree.ToText(t);
            var blob = new byte[rnd.Next(1, 64)];
            rnd.NextBytes(blob);
            if (PlistTree.Blob(blob) is { } p) PlistTree.ToText(p);
        }
    }

    static IEnumerable<PlistNode> Flatten(PlistNode n) => n.Children.SelectMany(c => Flatten(c).Prepend(c));
}
