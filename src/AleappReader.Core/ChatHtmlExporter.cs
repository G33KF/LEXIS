using System.Security.Cryptography;

namespace AleappReader.Core;

public enum ChatTheme { WhatsApp, Telegram }

/// <summary>How a chat page looks, and whether the examiner's tags and notes go into it (never by default: they are
/// the examiner's work, not the evidence).</summary>
public sealed record ChatExportOptions(ChatTheme Theme = ChatTheme.WhatsApp, bool TagsAndNotes = false)
{
    public static ChatTheme ThemeFor(ArtifactInfo a) =>
        $"{a.Category} {a.Module}".Contains("telegram", StringComparison.OrdinalIgnoreCase) ? ChatTheme.Telegram : ChatTheme.WhatsApp;
}

/// <summary>
/// One self-contained page per chat: a header with the conversation's first and last message, the evidence and report
/// block (device, extraction, parser, source files with SHA-256, tool, whether tags are included), its participants
/// (with the contact record and picture each was matched to), then the bubbles paged in the browser, each ending with
/// its record reference.
/// </summary>
public static class ChatHtmlExporter
{
    const string Css = """
        .meta td:first-child{width:240px}
        .people td{vertical-align:middle}
        .people .media img{width:56px;height:56px;object-fit:cover;border-radius:50%;max-width:none}
        .people .src{max-width:220px}
        .chat{background:#efeae2;padding:12px 16px;border:1px solid #d6d0c4;border-radius:6px;margin:8px 0}
        .m.showday:not([data-day=""])::before{content:attr(data-day);display:table;margin:10px auto;background:#e1f2fb;color:#3b4a54;font-size:11.5px;padding:3px 10px;border-radius:8px}
        .msg{display:flex;margin:3px 0}
        .msg.out{justify-content:flex-end}
        .bubble{max-width:65%;padding:6px 9px 4px;border-radius:8px;background:#fff;box-shadow:0 1px .5px rgba(0,0,0,.13)}
        .out .bubble{background:#d9fdd3}
        .msg.sys{justify-content:center}
        .sys .bubble,.telegram .sys .bubble{background:#fff4d6;font-style:italic;color:#3b4a54}
        .sender{font-weight:600;font-size:12px;color:#0b6e4f;margin-bottom:2px}
        .text{white-space:pre-wrap;word-break:break-word}
        .foot{text-align:right;font-size:10.5px;color:#667781;margin-top:2px}
        .extra{font-size:10.5px;color:#667781}
        .chat .media img{max-width:260px;max-height:260px;border-radius:6px}
        .quote{border-left:3px solid #06cf9c;background:rgba(0,0,0,.05);border-radius:4px;padding:3px 7px;margin:2px 0 4px;font-size:12px;color:#3b4a54;white-space:pre-wrap;word-break:break-word}
        .quote a{color:inherit;text-decoration:none}
        .tagnote{font-size:11px;background:#fff4cc;border:1px solid #e8d48a;border-radius:4px;padding:2px 6px;margin-top:3px}
        .av{display:none}
        .chat.telegram{background:#c8d9b9}
        .telegram .bubble{border-radius:12px}
        .telegram .out .bubble{background:#effdde}
        .telegram .sender{color:#3a8acc}
        .telegram .foot{color:#6b8f71}
        .telegram .quote{border-left-color:#3a8acc}
        .telegram .av{display:flex;flex:none;width:34px;height:34px;border-radius:50%;margin:auto 6px 0 0;align-items:center;justify-content:center;
          color:#fff;font-weight:600;font-size:13px;overflow:hidden}
        .telegram .av img{width:34px;height:34px;object-fit:cover}
        .pager{display:none;flex-wrap:wrap;gap:8px;align-items:center;margin:8px 0;font-size:12px}
        .pager input[type=number]{width:64px}
        @media print{.pager{display:none!important}.m{display:block!important}}
        """;

    // Without script (or when printing) every message shows; the pager only hides what is off the current page.
    const string Script = """
        (function(){
          var ms=[].slice.call(document.querySelectorAll('#chat .m')),pagers=[].slice.call(document.querySelectorAll('.pager')),size=100,page=0;
          function pages(){return size?Math.max(1,Math.ceil(ms.length/size)):1}
          function show(p){
            page=Math.max(0,Math.min(p|0,pages()-1));
            var a=size?page*size:0,b=size?a+size:ms.length,prev=null;
            ms.forEach(function(m,i){var on=i>=a&&i<b;m.style.display=on?'':'none';
              if(on){m.classList.toggle('showday',m.getAttribute('data-day')!==prev);prev=m.getAttribute('data-day');}});
            pagers.forEach(function(g){var pg=g.querySelector('.pg');pg.value=page+1;pg.max=pages();
              g.querySelector('.pn').textContent=pages();g.querySelector('.ps').value=String(size);
              g.querySelector('.pr').textContent=ms.length?'Messages '+(a+1)+'-'+Math.min(b,ms.length)+' of '+ms.length:'';});
          }
          pagers.forEach(function(g){
            g.style.display='flex';
            g.addEventListener('click',function(e){var go=e.target.getAttribute('data-go');if(!go)return;
              show({first:0,prev:page-1,next:page+1,last:pages()-1}[go]);document.getElementById('chat').scrollIntoView();});
            g.querySelector('.pg').addEventListener('change',function(){show(this.value-1)});
            g.querySelector('.ps').addEventListener('change',function(){var first=size*page;size=+this.value;show(size?Math.floor(first/size):0)});
            g.querySelector('.pd').addEventListener('change',function(){var d=this.value;
              for(var i=0;i<ms.length;i++)if(ms[i].getAttribute('data-day')>=d){show(size?Math.floor(i/size):0);ms[i].scrollIntoView();return}
              show(pages()-1);});
          });
          show(0);
        })();
        """;

    const string Pager = """
        <div class="pager"><button data-go="first">&laquo;</button><button data-go="prev">&lsaquo;</button>
        <span>Page <input type="number" class="pg" min="1"> of <span class="pn"></span></span>
        <button data-go="next">&rsaquo;</button><button data-go="last">&raquo;</button>
        <label>Messages per page <select class="ps"><option>50</option><option>100</option><option>250</option><option>500</option><option value="0">All</option></select></label>
        <label>Go to date <input type="date" class="pd"></label><span class="pr"></span></div>
        """;

    sealed record Ctx(CaseSession S, ArtifactInfo A, ContactDirectory Contacts, IReadOnlyList<(string Path, string? Sha256, long Size, bool FromExtraction)> Sources,
        ChatExportOptions O, IReadOnlyDictionary<long, TagEntry> Tags);

    public static void Write(string path, CaseSession s, ArtifactInfo a, ConversationItem chat, string? filter = null, ChatExportOptions? o = null) =>
        Run(s, a, o, x => WriteChat(path, x, chat, filter));

    /// <summary>One page per chat in <paramref name="dir"/>, named after the chat: the pages written, and the chats
    /// that could not be written with the reason.</summary>
    public static (IReadOnlyList<string> Written, IReadOnlyList<string> Failed) WriteEach(string dir, CaseSession s, ArtifactInfo a,
        IReadOnlyList<ConversationItem> chats, string? filter = null, ChatExportOptions? o = null)
    {
        var written = new List<string>();
        var failed = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Run(s, a, o, x =>
        {
            foreach (var chat in chats)
            {
                var name = FileName(chat.Label);
                var file = name;
                for (int n = 2; !used.Add(file); n++) file = $"{name} ({n})";
                var path = Path.Combine(dir, file + ".html");
                try
                {
                    WriteChat(path, x, chat, filter);
                    written.Add(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add($"{chat.Label}: {e.Message}"); }
            }
        });
        return (written, failed);
    }

    /// <summary>A page name from a chat label (device data): safe characters, at most 80 of them.</summary>
    public static string FileName(string label)
    {
        var safe = HtmlReport.SafeName(label);
        return (safe.Length > 80 ? safe[..80] : safe).Trim() is { Length: > 0 } n ? n : "chat";
    }

    static void Run(CaseSession s, ArtifactInfo a, ChatExportOptions? o, Action<Ctx> body)
    {
        using var con = s.OpenDb();
        o ??= new ChatExportOptions(ChatExportOptions.ThemeFor(a));
        body(new Ctx(s, a, new ContactDirectory(con, s, a), EvidenceOrigin.Hashes(s, a), o,
            o.TagsAndNotes ? s.Tags.ForTable(a.TableName) : new Dictionary<long, TagEntry>()));
    }

    static void WriteChat(string path, Ctx x, ConversationItem chat, string? filter)
    {
        static string E(string? s) => HtmlReport.E(s);
        using var r = new HtmlReport(path, x.S, $"{x.A.Category} chat — {chat.Label}", Css, caseInfo: false);
        var ms = chat.Messages;
        var times = ms.Select(m => m.Time).Where(t => t != "").ToList();
        // Contacts are looked up by the sender's identifier only (ChatParticipant.LookupId), never by the
        // conversation ID: a group with one active member would otherwise give that member the group's record.
        var people = chat.Participants;
        var matches = people.Select(p => p.LookupId is { } id ? x.Contacts.Find(id) : null).ToList();

        void Row(string k, string? v, bool html = false)
        {
            if (!string.IsNullOrWhiteSpace(v)) r.WriteLine($"<tr><td>{E(k)}</td><td>{(html ? v : E(v))}</td></tr>");
        }
        string Contact(ContactMatch c) =>
            string.Concat(c.Fields.Select(f => $"<div>{E(f.Field)}: {E(f.Value)}</div>")) +
            $"<div class=\"src\">Matched: {E(c.MatchedOn)}</div>";

        r.WriteLine("<table class=\"meta\">");
        Row("Application", x.A.Category);
        Row("Conversation", chat.Label);
        Row("Conversation ID", chat.Key);
        if (!people.Any(p => p.LookupId == chat.Key) && x.Contacts.Find(chat.Key) is { } conv)
            Row("Conversation record", (conv.Picture is { } cp ? $"<div class=\"people\">{r.Media([cp])}</div>" : "") + Contact(conv), true);
        Row("First message", times.DefaultIfEmpty("").Min());
        Row("Last message", times.DefaultIfEmpty("").Max());
        Row("Messages", $"{ms.Count:N0} ({ms.Count(m => m.IsSent):N0} sent, {ms.Count(m => !m.IsSent):N0} received) · {ms.Sum(m => m.Media.Count):N0} media");
        Row("Selection", filter);
        r.WriteLine("</table>");

        r.WriteLine("<h2>Evidence and report</h2><table class=\"meta\">");
        r.WriteProvenance();
        foreach (var (src, sha, size, inExtraction) in x.Sources)
        {
            Row("Source file on the device", src);
            Row(inExtraction ? "SHA-256 (read from the extraction)" : "SHA-256 (copy in the case; extraction not reachable)",
                sha is null ? "file not found in the extraction or the case" : $"{sha} ({size:N0} bytes)");
        }
        r.WriteLine("<tr><th colspan=\"2\">Report</th></tr>");
        Row("Made with", $"LEXIS {typeof(ChatHtmlExporter).Assembly.GetName().Version?.ToString(3)}");
        Row("Examiner tags and notes", x.O.TagsAndNotes ? $"Included ({ms.Count(m => x.Tags.ContainsKey(m.RowId)):N0} tagged messages)" : "Not included");
        Row("Record reference", "Each message ends with its identifiers in the source database (e.g. Message ID), or its row in the parser's output when it has none");
        Row("Report generated", ValueFormatter.FormatTime(DateTimeOffset.UtcNow));
        r.WriteLine("</table>");

        r.WriteLine("<h2>Participants</h2><table class=\"people\"><tr><th>Picture</th><th>Name</th><th>Identifier</th>" +
                    "<th>Messages</th><th>First message</th><th>Last message</th><th>Contact record</th></tr>");
        foreach (var (p, c) in people.Zip(matches))
        {
            r.WriteLine($"<tr><td>{(c?.Picture is { } pic ? r.Media([pic]) : "")}</td><td>{E(p.Name)}</td>" +
                        $"<td>{E(p.IsOwner ? "Device user (sent messages)" + (p.Id != "" ? $" · {p.Id}" : "") : p.Id != "" ? p.Id : p.Name)}</td>" +
                        $"<td>{p.Messages:N0}</td><td>{E(p.First)}</td>" +
                        $"<td>{E(p.Last)}</td><td>{(c is null ? "" : Contact(c))}</td></tr>");
        }
        r.WriteLine("</table>");

        // Avatars (Telegram theme): the matched contact's picture, else the sender's initials on a colour of their own.
        var avatars = new Dictionary<string, string>();
        foreach (var (p, c) in people.Zip(matches).Where(z => !z.First.IsOwner))
            avatars.TryAdd(p.Id != "" ? p.Id : p.Name, c?.Picture is { } pic && r.Url(pic) is { } u ? $"<img src=\"{E(u)}\" alt=\"\">" : "");
        string Avatar(MessageItem m)
        {
            var key = m.SenderId != "" ? m.SenderId : m.Sender;
            var inner = avatars.GetValueOrDefault(key) is { Length: > 0 } img ? img
                : E(string.Concat(m.Sender.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0]))));
            return $"<span class=\"av\" style=\"background:hsl({key.Aggregate(0u, (h, ch) => h * 31 + ch) % 360u},45%,52%)\">{inner}</span>";
        }

        var theme = x.O.Theme == ChatTheme.Telegram ? " telegram" : "";
        r.WriteLine($"<h2>Messages</h2>{Pager}<div class=\"chat{theme}\" id=\"chat\">");
        var lastDay = "";
        foreach (var m in ms)
        {
            var day = m.Time.Length >= 10 && DateOnly.TryParseExact(m.Time[..10], "yyyy-MM-dd", out _) ? m.Time[..10] : "";
            r.Write($"<div class=\"m{(day != lastDay ? " showday" : "")}\" id=\"r{m.RowId}\" data-day=\"{E(day)}\"><div class=\"msg {(m.IsSystem ? "sys" : m.IsSent ? "out" : "in")}\">" +
                    $"{(m.IsSent || m.IsSystem ? "" : Avatar(m))}<div class=\"bubble\">");
            lastDay = day;
            if (m.Sender != "") r.Write($"<div class=\"sender\">{E(m.Sender)}</div>");
            if (m.QuoteText != "")
                r.Write($"<div class=\"quote\">{(m.ReplyTo is { } q ? $"<a href=\"#r{q.RowId}\">{E(m.QuoteText)}</a>" : E(m.QuoteText))}</div>");
            if (m.Media.Count > 0) r.Write(r.Media(m.Media));
            if (m.Text != "") r.Write($"<div class=\"text\">{E(m.Text)}</div>");
            if (m.Placeholder != "") r.Write($"<div class=\"extra\">{E(m.Placeholder)}</div>");
            if (m.Extra != "") r.Write($"<div class=\"extra\">{E(m.Extra)}</div>");
            // ponytail: a link, not an embedded map: tiles would send every position to a tile server on opening the page.
            if (m.Location is { } p)
                r.Write($"<div class=\"extra\">Location {E(m.LocationText)}{(p.TableName != x.A.TableName ? $" ({E(p.Artifact)}, record {p.RowId})" : "")} · " +
                        FormattableString.Invariant($"<a rel=\"noreferrer\" title=\"Opens openstreetmap.org\" href=\"https://www.openstreetmap.org/?mlat={p.Lat}&amp;mlon={p.Lon}#map=16/{p.Lat}/{p.Lon}\">map</a></div>"));
            if (x.Tags.GetValueOrDefault(m.RowId) is { } t)
                r.Write($"<div class=\"tagnote\">Examiner tag: {E(t.Tag)}{(t.Note != "" ? $" · Note: {E(t.Note)}" : "")}</div>");
            r.WriteLine($"<div class=\"foot\">{E(string.Join(" · ", new[] { m.Time, m.SourceRecord != "" ? m.SourceRecord : $"parser row {m.RowId}" }.Where(t => t != "")))}</div></div></div></div>");
        }
        r.WriteLine($"</div>{Pager}");
        r.WriteLine($"<script>{Script}</script>");
        r.Complete();
    }
}
