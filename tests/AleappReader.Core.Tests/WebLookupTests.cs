using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class WebLookupTests
{
    [Fact]
    public void Only_format_words_leave_never_identifying_ones()
    {
        // digits runs, e-mail, phone, hashes and long tokens are dropped; names of formats stay
        Assert.Equal("WhatsApp msgstore.db key_remote_jid status meaning",
            WebLookup.Clean("WhatsApp msgstore.db key_remote_jid 393331234567 +39 a.b@mail.com d41d8cd98f00b204e980 status meaning"));
        Assert.Equal("Telegram cache4.db", WebLookup.Clean("Telegram account-123456789 cache4.db")); // the whole token goes
    }

    [Fact]
    public void Duck_results_are_read_with_their_real_address()
    {
        const string html = """
            <div><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.org%2Fschema&amp;rut=x">The <b>schema</b></a>
            <a class="result__snippet" href="x">key_remote_jid is the &amp; chat id</a></div>
            """;
        Assert.Equal([("The schema", "key_remote_jid is the & chat id", "https://example.org/schema")], WebLookup.Parse(html));
    }
}
