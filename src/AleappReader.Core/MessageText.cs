using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AleappReader.Core;

/// <summary>Readable text for message bodies an app stores as markup: WeChat's &lt;msg&gt; XML (voice, sticker, image,
/// video, location, link, contact card, call, system message) and Telegram's "Action: &lt;phoneCall (…)&gt;". Anything
/// else is returned as it is; the stored value stays in the record's details.</summary>
public static partial class MessageText
{
    [GeneratedRegex(@"^Action:\s*<(\w+)\s*(?:\((.*)\))?>\s*$", RegexOptions.Singleline)]
    private static partial Regex TelegramAction();

    // WeChat sticker bodies: "sender:0:0:md5:sender*#*\n<msg>…</msg>:0" or "sender:0:0:md5:<msg>…</msg>:0"; group
    // messages: "sender:\n<msg>…" or "sender: <msg>…".
    [GeneratedRegex(@"^[^\n<]*\*#\*\s*|^[^<\n]*?:\s*(?=<)")]
    private static partial Regex WeChatPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // The ":0" WeChat appends after a sticker's closing tag.
    [GeneratedRegex(@"(?<=>):\d+\s*$")]
    private static partial Regex WeChatTail();

    // WeChat video bodies hold only "sender_id:0:1".
    [GeneratedRegex(@"^[A-Za-z][\w.\-]*_[\w.\-]*:\d+:\d+\s*$")]
    private static partial Regex WeChatVideo();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    /// <summary>The body made readable; when it is empty, the first of <paramref name="others"/> (the row's other
    /// values) that is markup this reads.</summary>
    public static string Readable(string text, IEnumerable<string>? others = null)
    {
        var s = text.Trim();
        if (s != "") return Read(s) ?? text;
        foreach (var o in others ?? [])
            if (o.Contains('<') && Read(o.Trim()) is { Length: > 0 } r) return r;
        return text;
    }

    static string? Read(string s)
    {
        if (TelegramAction().Match(s) is { Success: true } a) return Action(a.Groups[1].Value, a.Groups[2].Value);
        if (WeChatVideo().IsMatch(s)) return "Video";
        var body = WeChatPrefix().Replace(s, "");
        body = WeChatTail().Replace(body, "");
        if (body.StartsWith('<'))
        {
            // Markup this does not read stays as stored. Inline markup that is not XML (WeChat system notices hold
            // <img …> and <_wc_custom_link_ …> around their text) is shown as its text.
            try { return Xml(XElement.Parse(body)); }
            catch (System.Xml.XmlException) { return Spaces().Replace(Tag().Replace(s, " "), " ").Trim() is { Length: > 0 } t && t != s ? t : null; }
        }
        if (s.Contains("<_wc_custom_link_")) return Tag().Replace(s, "");
        return null;
    }

    /// <summary>"phoneCall" with "id=1, discard reason=missed, duration=12" → "Phone call · missed · 12 s".</summary>
    static string Action(string name, string args)
    {
        var what = Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        what = char.ToUpperInvariant(what[0]) + what[1..];
        var parts = new List<string> { what };
        foreach (var kv in args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i <= 0) continue;
            var (k, v) = (kv[..i].Trim(), kv[(i + 1)..].Trim());
            if (k == "id" || v == "" || v == "nil" || v == "null") continue;
            parts.Add(k switch
            {
                "discard reason" => v,
                "duration" => $"{v} s",
                _ => $"{k} {v}",
            });
        }
        return string.Join(" · ", parts);
    }

    static string? Xml(XElement root)
    {
        XElement? E(string n) => root.Name.LocalName == n ? root : root.Descendants(n).FirstOrDefault();
        static string A(XElement e, string n) => e.Attribute(n)?.Value.Trim() ?? "";
        static string T(XElement? e) => e?.Value.Trim() ?? "";
        static string Join(params string[] p) => string.Join(" · ", p.Where(x => x != ""));
        static string Seconds(string ms) => double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s" : "";

        if (E("voicemsg") is { } voice) return Join("Voice message", Seconds(A(voice, "voicelength")));
        if (E("emoji") is { } emoji) return Join("Sticker", A(emoji, "width") is { Length: > 0 } w ? $"{w}×{A(emoji, "height")}" : "");
        if (E("videomsg") is { } video) return Join("Video", A(video, "playlength") is { Length: > 0 } pl ? pl + " s" : "");
        if (E("img") is not null && root.Name.LocalName == "msg") return "Image";
        if (E("location") is { } loc)
            return Join("Location", A(loc, "poiname") is { Length: > 0 } p ? p : A(loc, "label"),
                A(loc, "x") is { Length: > 0 } x ? $"{x}, {A(loc, "y")}" : "");
        if (E("VoIPBubbleMsg") is not null || E("voipmsg") is not null)
            return Join("Call", T(E("msg")), int.TryParse(T(E("duration")), out var d) && d > 0 ? $"{d} s" : "");
        if (E("appmsg") is { } app)
        {
            var title = T(app.Element("title"));
            var des = T(app.Element("des"));
            return T(app.Element("type")) switch
            {
                "3" => Join("Music", title, des),
                "4" or "5" => Join("Link", title, T(app.Element("url"))),
                "6" => Join("File", title),
                "19" => Join("Forwarded chat history", title),
                "33" or "36" => Join("Mini program", title),
                "51" => Join("Channels post", title),
                "57" => title != "" ? title : "Reply",
                "87" => Join("Group notice", T(app.Element("textannouncement")) is { Length: > 0 } ta ? ta : title),
                "2000" => Join("Transfer", des != "" ? des : title),
                "2001" => Join("Red packet", title),
                _ => Join(title, des) is { Length: > 0 } t ? t : null,
            };
        }
        if (root.Name.LocalName == "sysmsg")
        {
            if (E("template") is { } tpl)
            {
                var t = T(tpl);
                foreach (var link in root.Descendants("link"))
                {
                    var names = link.Descendants("nickname").Select(T).Where(n => n != "").ToList();
                    var value = names.Count > 0 ? string.Join(", ", names) : T(link.Element("plain")) is { Length: > 0 } pl ? pl : T(link.Element("title"));
                    t = t.Replace($"${A(link, "name")}$", value);
                }
                return t;
            }
            return T(root.Element("revokemsg")) is { Length: > 0 } rv ? rv : T(root) is { Length: > 0 } all ? all : null;
        }
        if (root.Name.LocalName == "msg" && (A(root, "nickname") != "" || A(root, "username") != ""))
            return Join("Contact card", A(root, "nickname") is { Length: > 0 } n ? n : A(root, "username"));
        return null;
    }

    [GeneratedRegex(@"\[([^\[\]\r\n]{1,14})\]")]
    private static partial Regex Bracket();

    // ponytail: WeChat's built-in faces drawn as the nearest Unicode emoji; WeChat's own artwork differs, and the
    // stored code stays in the record. Codes not listed stay as they are.
    static readonly Dictionary<string, string> WeChatFaces = Faces(
        "微笑 Smile 🙂|撇嘴 Grimace 😬|色 Drool 😍|发呆 Scowl 😳|得意 CoolGuy 😎|流泪 Sob 😭|害羞 Shy ☺️|闭嘴 Silent 🤐|睡 Sleep 😴|" +
        "大哭 Cry 😢|尴尬 Awkward 😅|发怒 Angry 😡|调皮 Tongue 😜|呲牙 Grin 😁|惊讶 Surprise 😲|难过 Frown 🙁|" +
        "抓狂 Scream 😫|吐 Puke 🤮|偷笑 Chuckle 🤭|愉快 Joyful 😊|白眼 Slight 🙄|傲慢 Smug 😏|困 Drowsy 😪|惊恐 Panic 😱|" +
        "流汗 Sweat 😓|憨笑 Laugh 😄|悠闲 Commando 😌|奋斗 Determined 💪|咒骂 Scold 🤬|疑问 Shocked ❓|嘘 Shhh 🤫|晕 Dizzy 😵|" +
        "衰 Toasted 😩|骷髅 Skull 💀|敲打 Hammer 🔨|再见 Wave 👋|擦汗 Speechless 😥|抠鼻 NosePick 🤥|鼓掌 Clap 👏|坏笑 Trick 😈|" +
        "哈欠 Yawn 🥱|鄙视 Pooh-pooh 😒|委屈 Shrunken 🥺|快哭了 TearingUp 🥲|阴险 Sly 😼|亲亲 Kiss 😚|可怜 Whimper 🥺|菜刀 Cleaver 🔪|" +
        "西瓜 Watermelon 🍉|啤酒 Beer 🍺|咖啡 Coffee ☕|猪头 Pig 🐷|玫瑰 Rose 🌹|凋谢 Wilt 🥀|嘴唇 Lips 💋|爱心 Heart ❤️|" +
        "心碎 BrokenHeart 💔|蛋糕 Cake 🎂|炸弹 Bomb 💣|便便 Poop 💩|月亮 Moon 🌙|太阳 Sun ☀️|拥抱 Hug 🤗|强 ThumbsUp 👍|" +
        "弱 ThumbsDown 👎|握手 Shake 🤝|胜利 Peace ✌️|抱拳 Fight 🙏|勾引 Beckon 🫴|拳头 Fist ✊|OK OK 👌|跳跳 Waddle 💃|发抖 Tremble 🫨|" +
        "怄火 Aaagh! 😤|转圈 Twirl 💫|捂脸 Facepalm 🤦|奸笑 Smirk 😏|机智 Smart 🤓|皱眉 Concerned 😟|耶 Yeah! ✌️|红包 Packet 🧧|" +
        "吃瓜 Onlooker 🍉|加油 GoForIt 💪|汗 Sweats 😅|天啊 OMG 😱|社会社会 Respect 🙏|旺柴 Doge 🐶|好的 NoProb 👌|打脸 MyBad 🤕|" +
        "哇 Wow 🤩|發 Rich 💰|裂开 Broken 💔|苦涩 Hurt 😣|叹气 Sigh 😮‍💨|让我看看 LetMeSee 👀|翻白眼 Boring 🙄|烟花 Fireworks 🎆|" +
        "爆竹 Firecracker 🧨|庆祝 Party 🎉|福 Blessing 🧧|礼物 Gift 🎁|合十 Worship 🙏|破涕为笑 Lol 😂|NO NO 🙅");

    static Dictionary<string, string> Faces(string table)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in table.Split('|'))
        {
            var p = e.Split(' ');
            d[p[0]] = d[p[1]] = p[2];
        }
        return d;
    }

    /// <summary>WeChat text with its built-in faces ("[Smile]", "[微笑]") shown as emoji.</summary>
    public static string WeChatEmoji(string text) =>
        text.Contains('[') ? Bracket().Replace(text, m => WeChatFaces.GetValueOrDefault(m.Groups[1].Value) ?? m.Value) : text;
}
