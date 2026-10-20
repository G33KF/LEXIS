using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

public sealed class MessageItem
{
    public required long RowId { get; init; }
    public required object?[] Raw { get; init; }
    public string Text { get; init; } = "";
    public string Sender { get; init; } = "";
    /// <summary>The sender's identifier when the artifact carries one beside the display name (e.g. "Sender ID").</summary>
    public string SenderId { get; init; } = "";
    /// <summary>The message's identifiers in its source, e.g. "Message ID 1234", for tracing it back to the evidence.</summary>
    public string SourceRecord { get; init; } = "";
    public string Time { get; init; } = "";
    public string Extra { get; init; } = "";
    public bool IsSent { get; init; }
    /// <summary>The artifact marks the record as a system message (a "System Message" flag column, or a type column
    /// reading "System Message"): an app event, not text someone wrote.</summary>
    public bool IsSystem { get; init; }
    /// <summary>The record's type as the artifact stores it ("Picture", "Type 90"), "" when it has no type column.</summary>
    public string Kind { get; init; } = "";
    public IReadOnlyList<MediaRef> Media { get; init; } = [];
    /// <summary>A position the message shares: from its own latitude/longitude columns, or from a sibling artifact
    /// keyed by its message ID (then Location.TableName/RowId name that record).</summary>
    public GeoPoint? Location { get; init; }
    /// <summary>The message's own ID ("" when the artifact has no message ID column).</summary>
    public string MessageId { get; init; } = "";
    /// <summary>A reply: the ID of the message it answers, the quoted text the artifact stores, and that message when
    /// it is in the same chat.</summary>
    public string ReplyToId { get; init; } = "";
    public string Quote { get; init; } = "";
    public MessageItem? ReplyTo { get; internal set; }
    public string QuoteText => ReplyTo is { } r ? $"{r.Sender}: {(r.Text != "" ? r.Text : string.Join(", ", r.Media.Select(x => x.Name)))}"
        : Quote != "" ? Quote : ReplyToId != "" ? $"Reply to message {ReplyToId} (not in this chat)" : "";
    /// <summary>What a bubble shows when the record holds no text, media, location or extra values, so it is not drawn
    /// as an empty bubble.</summary>
    public string Placeholder => Text != "" || Media.Count > 0 || Location is not null || Extra != "" ? ""
        : IsSystem ? "System message · no text in the report" : Kind != "" ? $"{Kind} · no text in the report" : "No text in the report";
    public string LocationText => Location is { } p ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Lat:0.######}, {p.Lon:0.######}") : "";
}

/// <summary>Someone who wrote in a chat. IsOwner marks the app's local account (the sender of the sent messages).</summary>
public sealed record ChatParticipant(string Name, string Id, int Messages, bool IsOwner, string First = "", string Last = "")
{
    public string Display => Id != "" && Id != Name ? $"{Name} ({Id})" : Name;
    /// <summary>The identifier contacts are looked up by: the sender ID or a phone-shaped name. Never a display name
    /// or the local account, so nobody is given another person's contact record.</summary>
    public string? LookupId => IsOwner ? null : Id != "" ? Id : ContactDirectory.TelKey(Name) is not null ? Name : null;
}

public sealed class ConversationItem
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>The local account the chat belongs to, when the artifact records one ("" otherwise).</summary>
    public string Account { get; init; } = "";
    public string AccountName { get; init; } = "";
    public List<MessageItem> Messages { get; } = [];
    public string FirstTime => Messages.Count > 0 ? Messages[0].Time : "";
    public string LastTime => Messages.Count > 0 ? Messages[^1].Time : "";
    public string Preview => Messages.Count > 0 ? Messages[^1].Text : "";
    public int ParticipantCount => Participants.Count;

    /// <summary>The local account first, then everyone else by number of messages.</summary>
    public IReadOnlyList<ChatParticipant> Participants => _participants ??= BuildParticipants();
    IReadOnlyList<ChatParticipant>? _participants;

    List<ChatParticipant> BuildParticipants()
    {
        static (string First, string Last) Span(IEnumerable<MessageItem> ms)
        {
            var t = ms.Select(m => m.Time).Where(t => t != "").ToList();
            return t.Count == 0 ? ("", "") : (t.Min()!, t.Max()!);
        }
        var list = Messages.Where(m => !m.IsSent && (m.Sender != "" || m.SenderId != ""))
            .GroupBy(m => m.SenderId != "" ? m.SenderId : m.Sender)
            .Select(g =>
            {
                var (first, last) = Span(g);
                return new ChatParticipant(g.Select(m => m.Sender).FirstOrDefault(n => n != "") ?? g.Key, g.First().SenderId, g.Count(), false, first, last);
            })
            .OrderByDescending(p => p.Messages).ToList();
        var sent = Messages.Where(m => m.IsSent).ToList();
        if (sent.Count > 0 || Account != "")
        {
            var (first, last) = Span(sent);
            list.Insert(0, new ChatParticipant(AccountName != "" ? AccountName : Account != "" ? Account : sent[0].Sender,
                Account, sent.Count, true, first, last));
        }
        return list;
    }
}

public sealed record ConversationSet(List<ConversationItem> Conversations, IReadOnlyList<ColumnInfo> Columns, bool Truncated);

/// <summary>Groups the rows of any artifact that declares data_views.conversation into threads.</summary>
public static class ConversationBuilder
{
    static readonly Regex SenderIdColumn = new(@"^(sender|from|sending party)[ _]?(id|address|jid)$", RegexOptions.IgnoreCase);
    static readonly Regex RecordColumn = new(@"^((msg|message)[ _]?id|source (file|table|db|database))$", RegexOptions.IgnoreCase);
    static readonly Regex AccountColumn = new(@"^(account|owner|local account)[ _]?(id|uin|jid)$", RegexOptions.IgnoreCase);
    static readonly Regex AccountNameColumn = new(@"^(account|owner|local account)[ _]?name$", RegexOptions.IgnoreCase);
    // Signal "Quoted Message"/"Quoted Author", SimpleX "Quoted Content (as stored)"; GroupMe/meWe/Discord "Reply To Message ID",
    // Potato "Reply (Message-ID)", Signal/Gettr "Quoted Message ID", Snapchat "Quoted Server Message ID".
    static readonly Regex SystemFlagColumn = new(@"^(is )?system( message)?$", RegexOptions.IgnoreCase);
    static readonly Regex TypeColumn = new(@"type|kind", RegexOptions.IgnoreCase);
    // WhatsApp "System Event": the app event a textless row records, named by the artifact; a value marks the row as system.
    static readonly Regex SystemEventColumn = new(@"^system event( \(as stored\))?$", RegexOptions.IgnoreCase);
    static readonly Regex SystemValue = new(@"^system( message)?$", RegexOptions.IgnoreCase);
    static readonly Regex DescribeColumn = new(@"^(action( data)?|content( \(as stored\))?|description|event|notification|call (type|status|result))$", RegexOptions.IgnoreCase);
    static readonly Regex QuoteColumn = new(@"^quoted (message|content|text)( \(as stored\))?$", RegexOptions.IgnoreCase);
    static readonly Regex QuoteAuthorColumn = new(@"^quoted (author|sender)$", RegexOptions.IgnoreCase);
    static readonly Regex ReplyIdColumn = new(@"^(reply( to)?|in reply to|quoted( server)?) \(?(message|msg)[ _-]?id\)?$", RegexOptions.IgnoreCase);

    static readonly Regex ChatListName = new(@"\b(conversations?|chats|chat list|threads)\b", RegexOptions.IgnoreCase);

    /// <summary>The messages a chat list belongs to: for an artifact named for an app's conversations or chats with no
    /// conversation view of its own ("WeChat - Conversations"), the artifact of its category that has one.</summary>
    public static ArtifactInfo? MessagesOf(IEnumerable<ArtifactInfo> artifacts, ArtifactInfo list) =>
        list.Conversation is null && ChatListName.IsMatch(list.Name)
            ? artifacts.FirstOrDefault(a => a.Conversation is not null && a.Category == list.Category && a.RecordCount > 0)
            : null;

    /// <summary>The column naming the local account each row belongs to, if the artifact has one.</summary>
    public static ColumnInfo? AccountOf(ArtifactInfo a) => a.Columns.FirstOrDefault(c => AccountColumn.IsMatch(c.Display));

    /// <summary>Per local account (one "" entry when the artifact records none): its name, chats and messages.</summary>
    public static IReadOnlyList<(string Account, string Name, long Chats, long Messages)> Accounts(SqliteConnection con, ArtifactInfo a)
    {
        var cols = ArtifactQuery.ExistingColumns(con, a);
        string Sql(ColumnInfo? c) => c is not null && cols.Contains(c) ? $"COALESCE(CAST({ArtifactQuery.Q(c.Sql)} AS TEXT), '')" : "''";
        var acc = AccountOf(a);
        var name = a.Columns.FirstOrDefault(c => AccountNameColumn.IsMatch(c.Display));
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT {Sql(acc)}, MAX({Sql(name)}), COUNT(DISTINCT {Sql(a.Column(a.Conversation?.Discriminator))}), COUNT(*) " +
                          $"FROM {ArtifactQuery.Q(a.TableName)} GROUP BY 1 ORDER BY 4 DESC";
        var list = new List<(string, string, long, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    /// <summary>Groups the rows into chats; with <paramref name="account"/> only that local account's rows.</summary>
    public static ConversationSet Build(SqliteConnection con, CaseSession s, ArtifactInfo a, int max, IReadOnlyCollection<long>? rowIds = null,
        string? account = null)
    {
        if (account is not null && AccountOf(a) is { } ac)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT rowid FROM {ArtifactQuery.Q(a.TableName)} WHERE COALESCE({ArtifactQuery.Q(ac.Sql)}, '') = @a";
            cmd.Parameters.AddWithValue("@a", account);
            var ids = new List<long>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
            rowIds = rowIds is null ? ids : rowIds.Intersect(ids).ToList();
        }
        var cv = a.Conversation!;
        var resolver = new MediaResolver(s);
        var time = a.Column(cv.Time);
        var page = ArtifactQuery.Query(con, a, new PageRequest(0, max, SortColumn: time?.Sql, RowIds: rowIds));
        var cols = page.Columns.ToList();
        int Ix(string? name) => a.Column(name) is { } c ? cols.IndexOf(c) : -1;
        int iKey = Ix(cv.Discriminator), iLabel = Ix(cv.LabelColumn), iText = Ix(cv.Text), iDir = Ix(cv.Direction),
            iTime = Ix(cv.Time), iSender = Ix(cv.Sender), iMedia = Ix(cv.Media);
        var extra = cv.Extra.Select(Ix).Where(i => i >= 0).ToList();
        var iSenderId = cols.FindIndex(c => SenderIdColumn.IsMatch(c.Display));
        int iAccount = cols.FindIndex(c => AccountColumn.IsMatch(c.Display)), iAccountName = cols.FindIndex(c => AccountNameColumn.IsMatch(c.Display));
        var record = cols.Select((c, i) => (c, i)).Where(x => RecordColumn.IsMatch(x.c.Display)).Select(x => x.i).ToList();
        string F(object?[] v, int i) => i < 0 ? "" : ValueFormatter.Format(v[i], cols[i]);
        var geo = GeoExtractor.GeoPairs(a).Select(p => (Lat: cols.IndexOf(p.Lat), Lon: cols.IndexOf(p.Lon))).Where(p => p.Lat >= 0 && p.Lon >= 0).ToList();
        var iMsgId = cols.FindIndex(c => GeoExtractor.MessageIdRx().IsMatch(c.Display));
        var iSysEvent = cols.FindIndex(c => SystemEventColumn.IsMatch(c.Display));
        int iQuote = cols.FindIndex(c => QuoteColumn.IsMatch(c.Display)), iQuoteBy = cols.FindIndex(c => QuoteAuthorColumn.IsMatch(c.Display)),
            iReply = cols.FindIndex(c => ReplyIdColumn.IsMatch(c.Display));
        var sysFlag = cols.Select((c, i) => (c, i)).Where(x => x.i != iText && SystemFlagColumn.IsMatch(x.c.Display)).Select(x => x.i).ToList();
        var sysType = cols.Select((c, i) => (c, i)).Where(x => x.i != iText && TypeColumn.IsMatch(x.c.Display)).Select(x => x.i).ToList();
        bool IsSystem(object?[] v) => sysFlag.Any(i => F(v, i).Trim().ToLowerInvariant() is "1" or "true" or "yes")
                                      || sysType.Any(i => SystemValue.IsMatch(F(v, i).Trim()));
        var describe = cols.Select((c, i) => (c, i)).Where(x => x.i != iText && DescribeColumn.IsMatch(x.c.Display)).Select(x => x.i).ToList();
        var wechat = (a.Module + " " + a.Name).Contains("wechat", StringComparison.OrdinalIgnoreCase);
        var linked = geo.Count == 0 && iMsgId >= 0 ? GeoExtractor.ByMessageId(con, s.Manifest, a) : [];
        GeoPoint? Where(RecordRow r)
        {
            foreach (var (la, lo) in geo)
                if (GeoExtractor.Coord(r.Values[la], r.Values[lo]) is var (lat, lon))
                    return new GeoPoint(lat, lon, a.Name, a.TableName, r.RowId, F(r.Values, iTime), "");
            return linked.Count > 0 ? linked.GetValueOrDefault(F(r.Values, iMsgId)) : null;
        }

        var groups = new Dictionary<(string, string), ConversationItem>();
        foreach (var r in page.Rows)
        {
            var key = F(r.Values, iKey);
            var acct = F(r.Values, iAccount);
            if (!groups.TryGetValue((acct, key), out var conv))
            {
                var label = F(r.Values, iLabel);
                groups[(acct, key)] = conv = new ConversationItem { Key = key, Account = acct, AccountName = F(r.Values, iAccountName),
                    Label = label != "" ? label : (key != "" ? key : "(no thread)") };
            }
            var sent = iDir >= 0 && cv.IsSent(F(r.Values, iDir));
            var sender = sent && !string.IsNullOrEmpty(cv.SentStaticLabel) ? cv.SentStaticLabel : F(r.Values, iSender);
            var media = iMedia >= 0 ? resolver.Resolve(con, r.Values[iMedia]) : [];
            // An empty body takes its text from the row's stored markup (WeChat keeps voice, calls, cards… in another column),
            // unless the media shown already says what it is.
            var text = MessageText.Readable(F(r.Values, iText), media.Count > 0 ? null : r.Values.Where((_, i) => i != iText).OfType<string>());
            if (wechat) text = MessageText.WeChatEmoji(text);
            // Still nothing to show: a column describing the record (Telegram's Action Data, WeChat's stored content,
            // WhatsApp's Call Result), or the app event the artifact names in its System Event column.
            var sysEvent = F(r.Values, iSysEvent).Trim();
            if (text.Trim() == "" && media.Count == 0 && sysEvent != "") text = $"System Message: {sysEvent}";
            if (text.Trim() == "" && media.Count == 0 && describe.Select(i => F(r.Values, i)).FirstOrDefault(v => v.Trim() != "" && !v.TrimStart().StartsWith('<')) is { } d) text = d;
            conv.Messages.Add(new MessageItem
            {
                RowId = r.RowId, Raw = r.Values, Text = text, Sender = sender, Time = F(r.Values, iTime), IsSent = sent,
                IsSystem = sysEvent != "" || IsSystem(r.Values),
                Kind = sysType.Select(i => F(r.Values, i).Trim()).FirstOrDefault(v => v != "") is { } k ? (long.TryParse(k, out _) ? $"Type {k}" : k) : "",
                SenderId = iSenderId != iSender ? F(r.Values, iSenderId) : "",
                SourceRecord = string.Join(" · ", record.Select(i => (cols[i].Display, V: F(r.Values, i))).Where(x => x.V != "").Select(x => $"{x.Display} {x.V}")),
                Extra = string.Join(" · ", extra.Select(i => $"{cols[i].Display}: {F(r.Values, i)}").Where(x => !x.EndsWith(": "))),
                Media = media,
                Location = Where(r),
                MessageId = F(r.Values, iMsgId),
                ReplyToId = F(r.Values, iReply) is var rid && rid != "0" ? rid : "",
                Quote = F(r.Values, iQuote) is { Length: > 0 } q ? (F(r.Values, iQuoteBy) is { Length: > 0 } by ? $"{by}: {q}" : q) : "",
            });
        }
        if (iReply >= 0 && iMsgId >= 0)
            foreach (var c in groups.Values)
            {
                var byId = c.Messages.Where(m => m.MessageId != "").GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => g.First());
                foreach (var m in c.Messages.Where(m => m.ReplyToId != "")) m.ReplyTo = byId.GetValueOrDefault(m.ReplyToId);
            }
        // Label-less threads take the first counterpart that is not the local user.
        foreach (var c in groups.Values.Where(c => c.Label == c.Key).ToList())
            if (c.Messages.FirstOrDefault(m => !m.IsSent && m.Sender != "") is { } m) groups[(c.Account, c.Key)] = Relabel(c, m.Sender);
        return new ConversationSet(groups.Values.OrderByDescending(c => c.LastTime).ToList(), cols, page.Total > max);
    }

    static ConversationItem Relabel(ConversationItem c, string label)
    {
        var n = new ConversationItem { Key = c.Key, Account = c.Account, AccountName = c.AccountName, Label = $"{label} ({c.Key})" };
        n.Messages.AddRange(c.Messages);
        return n;
    }
}
