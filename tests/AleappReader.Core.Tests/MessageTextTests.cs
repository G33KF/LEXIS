using AleappReader.Core;
using Xunit;

namespace AleappReader.Core.Tests;

public class MessageTextTests
{
    [Theory]
    [InlineData("<msg><voicemsg voicelength=\"871\" length=\"909\" isPlayed=\"false\"></voicemsg></msg>", "Voice message · 0.9 s")]
    [InlineData("wxid_a1:0:0:abc123:wxid_a1*#*\n<msg><emoji fromusername=\"wxid_a1\" md5=\"abc\" width=\"165\" height=\"198\"></emoji> <gameext type=\"0\" content=\"0\"></gameext></msg>:0", "Sticker · 165×198")]
    [InlineData("wxid_b2:\n<msg><img length=\"10\" /></msg>", "Image")]
    [InlineData("wxid_c3x9:0:1\n", "Video")]
    [InlineData("<msg><location x=\"45.1\" y=\"9.2\" poiname=\"Station\" /></msg>", "Location · Station · 45.1, 9.2")]
    [InlineData("<msg><appmsg><title>Hello</title><type>5</type><url>http://example.test/a</url></appmsg></msg>", "Link · Hello · http://example.test/a")]
    [InlineData("<voipmsg type=\"VoIPBubbleMsg\"><VoIPBubbleMsg><msg><![CDATA[Call ended]]></msg><duration>42</duration></VoIPBubbleMsg></voipmsg>", "Call · Call ended · 42 s")]
    [InlineData("<msg username=\"wxid_d4\" nickname=\"Shop\" />", "Contact card · Shop")]
    [InlineData("<sysmsg type=\"sysmsgtemplate\"><sysmsgtemplate><content_template type=\"tmpl_type_profile\"><plain></plain><template><![CDATA[\"$adder$\" added \"$names$\"]]></template><link_list><link name=\"adder\" type=\"link_profile\"><memberlist><member><nickname>Ann</nickname></member></memberlist></link><link name=\"names\" type=\"link_profile\"><memberlist><member><nickname>Bo</nickname></member><member><nickname>Cy</nickname></member></memberlist></link></link_list></content_template></sysmsgtemplate></sysmsg>", "\"Ann\" added \"Bo, Cy\"")]
    [InlineData("Action: <phoneCall (id=123456, discard reason=missed)>", "Phone call · missed")]
    [InlineData("Action: <historyCleared>", "History cleared")]
    [InlineData("wxid_a1:0:0:abc123:<msg><emoji fromusername=\"wxid_a1\" cdnurl=\"http*#*//x.test/a\" width=\"64\" height=\"64\"></emoji></msg>:0", "Sticker · 64×64")]
    [InlineData("wxid_b2: <msg><voicemsg voicelength=\"3000\" /></msg>", "Voice message · 3 s")]
    [InlineData("<img src=\"x.png\"/> You received <_wc_custom_link_ href=\"y\">a gift</_wc_custom_link_>", "You received a gift")]
    [InlineData("<?xml version=\"1.0\"?><msg><appmsg><title></title><type>87</type><textannouncement>Meeting at 9</textannouncement></appmsg></msg>", "Group notice · Meeting at 9")]
    [InlineData("Action: <messageAutoremoveTimeoutUpdated (set by=42)>", "Message autoremove timeout updated · set by 42")]
    public void Stored_markup_reads_as_text(string body, string expected) => Assert.Equal(expected, MessageText.Readable(body));

    [Theory]
    [InlineData("Plain text stays as it is")]
    [InlineData("Meet at 10:30:00 <ok>")]
    [InlineData("<b>not a known message</b>")]
    public void Other_text_is_left_alone(string body) => Assert.Equal(body, MessageText.Readable(body));

    [Fact]
    public void WeChat_faces_show_as_emoji_and_other_brackets_stay() =>
        Assert.Equal("🙂 ok 👍 [Nope] [a b]", MessageText.WeChatEmoji("[Smile] ok [强] [Nope] [a b]"));

    [Fact]
    public void An_empty_body_takes_its_text_from_the_rows_stored_markup()
    {
        Assert.Equal("Voice message · 2 s", MessageText.Readable("", ["34", "Incoming", "<msg><voicemsg voicelength=\"2000\" /></msg>"]));
        Assert.Equal("", MessageText.Readable("", ["34", "plain"]));
    }
}
