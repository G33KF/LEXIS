using AleappReader.Core;
using Xunit;

namespace AleappReader.Core.Tests;

public class StructuredValueTests
{
    [Theory]
    [InlineData("{\"a\": 1}", StructuredValue.Json)]
    [InlineData(" [1, 2] ", StructuredValue.Json)]
    [InlineData("<root><x a=\"1\">t</x></root>", StructuredValue.Xml)]
    [InlineData("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>", StructuredValue.Plist)]
    [InlineData("<html><body><p>Hi</p></body></html>", StructuredValue.Html)]
    [InlineData("plain text", null)]
    [InlineData("12345", null)]
    [InlineData("<b", null)]
    public void Kind_is_named_from_the_value(string v, string? kind) => Assert.Equal(kind, StructuredValue.Kind(v));

    [Fact]
    public void Blobs_are_plists_or_blobs()
    {
        Assert.Equal(StructuredValue.Plist, StructuredValue.Kind("bplist00xxxx"u8.ToArray()));
        Assert.Equal(StructuredValue.Blob, StructuredValue.Kind(new byte[] { 1, 2 }));
    }

    [Fact]
    public void Json_opens_as_a_tree_with_nested_json_strings_opened_too()
    {
        var root = StructuredValue.Parse("{\"user\": {\"name\": \"Ann\"}, \"tags\": [\"a\", \"b\"], \"meta\": \"{\\\"k\\\": 2}\"}", StructuredValue.Json)!;
        Assert.Equal("Ann", root.Children[0].Children[0].Value);
        Assert.Equal("user.name", root.Children[0].Children[0].Path);
        Assert.Equal(2, root.Children[1].Children.Count);
        Assert.Equal("k", root.Children[2].Children[0].Key);
    }

    [Fact]
    public void Xml_opens_with_attributes_and_damaged_values_do_not()
    {
        var root = StructuredValue.Parse("<root><x a=\"1\">t</x></root>", StructuredValue.Xml)!;
        Assert.Equal("@a", root.Children[0].Children[0].Key);
        Assert.Equal("t", root.Children[0].Value);
        Assert.Null(StructuredValue.Parse("<root><x></root>", StructuredValue.Xml));
        Assert.Null(StructuredValue.Parse("<!DOCTYPE r [<!ENTITY e \"x\">]><r>&e;</r>", StructuredValue.Xml));
    }

    [Fact]
    public void Brief_puts_the_leaves_on_one_line()
    {
        Assert.Equal("name: Ann; [0]: a; [1]: b", StructuredValue.Brief("{\"user\": {\"name\": \"Ann\"}, \"tags\": [\"a\", \"b\"], \"empty\": \"\"}"));
        Assert.Equal("x: t; a: 1", StructuredValue.Brief("<root><x a=\"1\">t</x></root>"));
        Assert.Equal("Hi · there", StructuredValue.Brief("<html><p>Hi</p>there</html>"));
        Assert.Null(StructuredValue.Brief("plain"));
        Assert.Null(StructuredValue.Brief("{broken"));
        Assert.EndsWith("…", StructuredValue.Brief("[" + string.Join(",", Enumerable.Range(0, 100)) + "]", 20));
    }

    [Fact]
    public void Html_reads_as_its_text() =>
        Assert.Equal("Hello & bye\nNext", StructuredValue.HtmlText("<html><script>x()</script><p>Hello &amp; <b>bye</b></p>Next</html>"));
}
