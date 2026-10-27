using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>One value of an account card: what it is, the value, the artifacts and columns it was read from, and whether
/// the reader built it rather than read it (a number with its country code, a WhatsApp JID).</summary>
public sealed record AccountValue(string Label, string Value, string Source, bool Derived = false)
{
    public string Caption => Derived ? Label + " (derived)" : Label;
    public string Hint => (Derived ? "Derived from " : "From ") + Source;
}

/// <summary>An account of an app or service, merged from the artifacts that record it: its values, the Android user
/// whose files it was read from (none for the first), the artifacts, and where its picture may be in the extraction.</summary>
public sealed record AccountCard(string Service, string User, IReadOnlyList<AccountValue> Values, string Sources, IReadOnlyList<string> Pictures)
{
    public string Title => User == "" ? Service : $"{Service} ({User})";
}

/// <summary>
/// The accounts of the device as cards, one per account of each app or service: the rows of the artifacts about accounts
/// and profiles (as <see cref="AccountRoster"/> picks them), each column read as a field (full name, username, phone,
/// e-mail, UID…), a row joining the card of the same service and Android user it shares an identifier with; the Apple
/// ID that bought or installed the apps; an app's own profile among its peers (the peer whose id is the account's); the
/// local account a chat app's messages belong to. Never a password, token or key; not the device's own users, SIM
/// accounts, autofill profiles or the saved logins of sites.
/// </summary>
public static class AccountsOverview
{
    const int MaxRows = 20_000;
    static readonly Regex NotAnAccount = new(@"telecom|users and profiles|autofill|password manager|saved logins", RegexOptions.IgnoreCase);
    static readonly Regex SourceColumn = new(@"^source files?$");
    static readonly Regex AndroidUser = new(@"(?:^|[\\/])data[\\/](?:user|user_de|system_ce|system_de|misc_ce|misc_de)[\\/](\d+)[\\/]", RegexOptions.IgnoreCase);
    static readonly Regex AppFolder = new(@"^(.*?[\\/]data[\\/](?:data|user[\\/]\d+)[\\/]([a-z]\w*(?:\.\w+)+))[\\/]", RegexOptions.IgnoreCase);
    // columns that say whose account they are, whatever the row is about: the Apple ID that bought or installed an app
    static readonly Dictionary<string, string> OwnedBy = new()
    {
        ["apple id"] = "Apple", ["purchaser apple id"] = "Apple", ["account dsid"] = "Apple", ["purchaser id"] = "Apple",
    };
    // the local account each row of a chat app belongs to (LEXIS WhatsApp and Telegram on iOS)
    static readonly Regex OwnerId = new(@"^account (id|jid)$"), OwnerName = new(@"^account name$");
    // what older reports put there when WhatsApp's own JID could not be read: its container, or the app
    static readonly Regex NoOwner = new(@"^([a-z]\w*(\.\w+)+|whatsapp( business)?)$", RegexOptions.IgnoreCase);

    // the field each column is, by its normalized name, in the order a card shows them; other columns are not shown
    static readonly (string Label, string[] Columns)[] Fields =
    [
        ("Full name", ["full name", "display name", "holder name", "name", "peer name"]),
        ("Nickname", ["nickname"]),
        ("Username", ["username", "user name", "handle", "alias", "last signed in username as stored"]),
        ("Phone", ["phone", "phone number", "mobile number", "mobile", "msisdn", "registration screen phone number"]),
        ("E-mail", ["email", "e mail", "email address", "google account email"]),
        ("UID", ["uid", "user id", "gaia id"]),
        ("DSID", ["account dsid", "purchaser id", "dsid"]),
        ("Account", ["account name", "account", "apple id", "purchaser apple id"]),
        ("Installation source", []),
        ("Account ID", ["account id"]),
        ("Account type", ["account type"]), ("Service", ["account desc", "account description"]),
        ("Given name", ["given name"]), ("Date of birth", ["date of birth", "birthday"]), ("Nationality", ["nationality"]),
        ("Country code", ["country code", "registration screen country code"]), ("Locale", ["locale"]),
        ("About", ["user status", "about description"]), ("Status", ["status"]), ("Verification", ["registration screen verification state as stored"]),
        ("Version", ["version"]), ("Picture URL", ["picture url"]),
    ];
    static readonly string[] Identifiers = ["Username", "Phone", "E-mail", "UID", "DSID", "Account"];
    static readonly string[] ServiceColumns = ["account type", "account desc", "origin url", "package", "bundle id"];
    // WeChat's userinfo ids, as the LEAPP's WeChat module notes them
    static readonly (string Id, string Label)[] WeChatFields = [("2", "UID"), ("42", "Username"), ("6", "Phone"), ("4", "Nickname")];
    static readonly string[] Leading = ["Google", "Apple", "WhatsApp", "WhatsApp Business", "WeChat"];

    sealed class Card(string service, string user)
    {
        public readonly string Service = service, User = user;
        public readonly List<(string Label, string Value, SortedSet<string> From, bool Derived)> Values = [];
        public readonly SortedSet<string> Sources = new(StringComparer.OrdinalIgnoreCase), Packages = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Pictures = [];

        public void Add(string label, string value, string from, bool derived = false)
        {
            if (value == "") return;
            // a number built with its country code stays beside the one stored without it
            var i = Values.FindIndex(v => v.Label == label && (v.Derived == derived ? Same(label, v.Value, value) : string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase)));
            if (i < 0) { Values.Add((label, value, new(StringComparer.OrdinalIgnoreCase) { from }, derived)); return; }
            Values[i].From.Add(from);
            if (!derived && Values[i].Derived) Values[i] = (label, Values[i].Value, Values[i].From, false);
        }

        public bool Shares(IEnumerable<(string Label, string Value)> ids) =>
            ids.Any(x => Values.Any(v => v.Label == x.Label && Identifiers.Contains(v.Label) && Same(x.Label, v.Value, x.Value)));
    }

    static string Digits(string v) => new(v.Where(char.IsDigit).ToArray());

    /// <summary>The same value: a phone number with or without its country code, any other value ignoring case.</summary>
    static bool Same(string label, string a, string b)
    {
        if (label != "Phone") return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var (x, y) = (Digits(a), Digits(b));
        return x.Length >= 7 && y.Length >= 7 && (x.EndsWith(y) || y.EndsWith(x));
    }

    /// <summary>The Android user whose files a path is in, when not the first ("Android user 10"); else "".</summary>
    public static string UserOf(string? path) =>
        path is not null && AndroidUser.Match(path) is { Success: true } m && m.Groups[1].Value != "0" ? "Android user " + m.Groups[1].Value : "";

    /// <summary>Where a WeChat account's picture is: in its folder (md5 of "mm" + its UIN), avatar/xx/yy/user_hd_ or
    /// user_ + the md5 of its WeChat ID; the folder found among the paths of the artifact.</summary>
    public static IEnumerable<string> WeChatAvatars(string uin, string wxid, IEnumerable<string> sourcePaths)
    {
        static string Md5(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
        var folder = Md5("mm" + uin);
        var h = Md5(wxid);
        foreach (var p in sourcePaths.Select(p => p.Replace('\\', '/')))
            if (Path.GetDirectoryName(p)?.Replace('\\', '/') is { } dir && dir.EndsWith("/" + folder, StringComparison.OrdinalIgnoreCase))
                foreach (var name in new[] { "user_hd_", "user_" })
                    yield return $"{dir}/avatar/{h[..2]}/{h[2..4]}/{name}{h}.png";
    }

    public static IReadOnlyList<AccountCard> Build(SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var arts = artifacts.ToList();
        var cards = new List<Card>();
        foreach (var a in arts.Where(a => a.RecordCount is > 0 and <= MaxRows && a.Conversation is null &&
                     (AccountRoster.AccountArtifact.IsMatch(a.Name) && !NotAnAccount.IsMatch(a.Name) || a.Columns.Any(c => OwnedBy.ContainsKey(AccountRoster.Norm(c))))))
        {
            IReadOnlyList<ColumnInfo> cols;
            try { cols = ArtifactQuery.ExistingColumns(con, a); }
            catch (SqliteException) { continue; }
            var norm = cols.Select(AccountRoster.Norm).ToList();
            var app = a.Name.Split(" - ")[0].Trim();
            var paths = (a.SourcePath ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // an artifact read only for the Apple ID in it (the apps bought or used) says nothing of the apps' own accounts
            var onlyOwned = !AccountRoster.AccountArtifact.IsMatch(a.Name) || NotAnAccount.IsMatch(a.Name);
            try
            {
                if (app.Equals("WeChat", StringComparison.OrdinalIgnoreCase) && norm.Any(n => n.StartsWith("field id"))) { WeChat(con, a, cols, norm, paths, cards); continue; }
                foreach (var r in ArtifactQuery.All(con, a))
                {
                    string V(int i) => i < 0 ? "" : ValueFormatter.Format(r.Values[i], cols[i]).Trim();
                    var (values, service, path) = Read(a, cols, norm, V, paths.Length == 1 ? paths[0] : "");
                    // the Apple ID that bought an app: a card of its own service, not of the app
                    var owned = values.Where(x => x.Owner != "").ToList();
                    foreach (var g in owned.GroupBy(x => x.Owner).Where(g => g.Any(x => Identifiers.Contains(x.Label))))
                    {
                        var oc = Join(cards, g.Key, "", g.Where(x => Identifiers.Contains(x.Label)).Select(x => (x.Label, x.Value)));
                        foreach (var x in g) oc.Add(x.Label, x.Value, x.From);
                        oc.Sources.Add(a.Name);
                    }
                    values.RemoveAll(owned.Contains);
                    if (onlyOwned || AccountRoster.Site.IsMatch(service) || !values.Any(x => Identifiers.Contains(x.Label))) continue;
                    var family = AccountRoster.Family(service, [a.Name]);
                    values.RemoveAll(x => AccountRoster.NotAnId(x.Value, family));
                    if (family == "" || !values.Any(x => Identifiers.Contains(x.Label))) continue;
                    // a WhatsApp number with its country code and the JID it is known by
                    string Of(string col) => norm.IndexOf(col) is var i and >= 0 ? V(i) : "";
                    if (family.StartsWith("WhatsApp") && (Of("country code") is { Length: > 0 } c1 ? c1 : Of("registration screen country code")) is { Length: > 0 } cc &&
                        (Of("mobile number") is { Length: > 0 } n1 ? n1 : Of("registration screen phone number")) is { Length: > 0 } num && !num.StartsWith('+'))
                    {
                        var from = $"{a.Name} · Country Code, Phone Number";
                        values.Add(("Phone", "+" + Digits(cc) + Digits(num), from, true, ""));
                        values.Add(("UID", Digits(cc) + Digits(num) + "@s.whatsapp.net", from, true, ""));
                    }
                    var card = Join(cards, family, UserOf(path), values.Where(x => Identifiers.Contains(x.Label)).Select(x => (x.Label, x.Value)));
                    foreach (var x in values) card.Add(x.Label, x.Value, x.From, x.Derived);
                    card.Sources.Add(a.Name);
                    if (AppFolder.Match(path) is { Success: true } m)
                    {
                        card.Packages.Add(m.Groups[2].Value);
                        if (family.StartsWith("WhatsApp")) card.Pictures.Add($"{m.Groups[1].Value.Replace('\\', '/')}/files/me.jpg");
                    }
                    if (service != "" && family.StartsWith("WhatsApp")) card.Packages.Add(service);
                }
            }
            catch (SqliteException) { } // an unreadable artifact adds nothing
        }
        Peers(con, arts, cards);
        ChatOwners(con, arts, cards);
        Installers(con, arts, cards);
        return cards.OrderBy(c => Array.IndexOf(Leading, c.Service) is var i and >= 0 ? i : Leading.Length).ThenBy(c => c.Service, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.User)
            .Select(c => new AccountCard(c.Service, c.User,
                c.Values.OrderBy(v => Array.FindIndex(Fields, f => f.Label == v.Label)).Select(v => new AccountValue(v.Label, v.Value, string.Join("; ", v.From), v.Derived)).ToList(),
                string.Join(", ", c.Sources), c.Pictures.Distinct().ToList()))
            .ToList();
    }

    /// <summary>The fields of a row: each column read as one (an address or a number in an account or username column taken
    /// as what it is, a name split in two joined), with the family a column says owns it if any; its service and source path.</summary>
    static (List<(string Label, string Value, string From, bool Derived, string Owner)> Values, string Service, string Path) Read(
        ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, List<string> norm, Func<int, string> V, string path)
    {
        var values = new List<(string Label, string Value, string From, bool Derived, string Owner)>();
        var service = "";
        for (var i = 0; i < cols.Count; i++)
        {
            if (cols[i].IsMedia || AccountRoster.Secret.IsMatch(cols[i].Display) || V(i) is not { Length: > 0 } v || AccountRoster.Placeholder.IsMatch(v)) continue;
            if (SourceColumn.IsMatch(norm[i])) { path = v.Split([',', '\n'])[0].Trim(); continue; }
            if (service == "" && ServiceColumns.Contains(norm[i])) service = v;
            if (Fields.FirstOrDefault(f => f.Columns.Contains(norm[i])).Label is not { } label) continue;
            if (label is "Account" or "Phone" or "Username" && new AccountId(cols[i].Display, Regex.Replace(v, "^tel:", "", RegexOptions.IgnoreCase)) is var id)
                (label, v) = id.IsEmail ? ("E-mail", id.Value) : id.IsPhone || label == "Phone" ? ("Phone", id.Value) : (label, v);
            values.Add((label, v, $"{a.Name} · {cols[i].Display}", false, OwnedBy.GetValueOrDefault(norm[i], "")));
        }
        string Of(string col) => norm.IndexOf(col) is var j and >= 0 ? V(j) : "";
        if (!values.Any(x => x.Label == "Full name") && $"{Of("first name")} {Of("last name")}".Trim() is { Length: > 0 } full)
            values.Add(("Full name", full, $"{a.Name} · First Name, Last Name", true, ""));
        return (values, service, path);
    }

    /// <summary>An app's own profile among its peers: in an artifact with a peer id column, of a family that has cards, the
    /// row whose peer is a card's UID (and whose account, when both say, is the card's) adds its fields to that card.</summary>
    static void Peers(SqliteConnection con, List<ArtifactInfo> arts, List<Card> cards)
    {
        foreach (var a in arts.Where(a => a.RecordCount is > 0 and <= MaxRows && a.Conversation is null && a.Columns.Any(c => AccountRoster.Norm(c) == "peer id")))
        {
            var family = AccountRoster.Family("", [a.Name]);
            if (!cards.Any(c => c.Service == family)) continue;
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, a);
                var norm = cols.Select(AccountRoster.Norm).ToList();
                int peer = norm.IndexOf("peer id"), account = norm.IndexOf("account id");
                if (peer < 0) continue;
                foreach (var r in ArtifactQuery.All(con, a))
                {
                    string V(int i) => i < 0 ? "" : ValueFormatter.Format(r.Values[i], cols[i]).Trim();
                    bool Is(Card c, string label, string v) => c.Values.Any(x => x.Label == label && x.Value == v);
                    if (V(peer) == "" || cards.FirstOrDefault(c => c.Service == family && Is(c, "UID", V(peer)) &&
                            (V(account) == "" || !c.Values.Any(x => x.Label == "Account ID") || Is(c, "Account ID", V(account)))) is not { } card) continue;
                    // its ids are the card's already
                    var (values, _, _) = Read(a, cols, norm, V, "");
                    foreach (var x in values.Where(x => x.Label is not ("UID" or "Account ID")))
                    {
                        card.Add(x.Label, x.Value, x.From, x.Derived);
                        card.Sources.Add(a.Name);
                    }
                }
            }
            catch (SqliteException) { }
        }
    }

    /// <summary>The local account a chat app's messages belong to (an Account ID or JID column, and the Account Name the app
    /// shows): added to the card of that app holding that value, else a card of its own; a WhatsApp JID gives its number.</summary>
    static void ChatOwners(SqliteConnection con, List<ArtifactInfo> arts, List<Card> cards)
    {
        foreach (var a in arts.Where(a => a.RecordCount > 0 && a.Conversation is not null))
        {
            if (a.Columns.FirstOrDefault(c => OwnerId.IsMatch(AccountRoster.Norm(c))) is not { } id) continue;
            var name = a.Columns.FirstOrDefault(c => OwnerName.IsMatch(AccountRoster.Norm(c)));
            var app = a.Columns.FirstOrDefault(c => AccountRoster.Norm(c) == "app");
            try
            {
                var existing = ArtifactQuery.ExistingColumns(con, a);
                string Sql(ColumnInfo? c) => c is not null && existing.Contains(c) ? $"COALESCE(CAST({ArtifactQuery.Q(c.Sql)} AS TEXT), '')" : "''";
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"SELECT {Sql(id)}, MAX({Sql(name)}), {Sql(app)} FROM {ArtifactQuery.Q(a.TableName)} GROUP BY 1, 3";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var (account, label) = (r.GetString(0).Trim(), r.GetString(1).Trim());
                    var family = AccountRoster.Family(r.GetString(2), [a.Name]);
                    if (account == "" || NoOwner.IsMatch(account) || family == "") continue;
                    var card = cards.FirstOrDefault(c => c.Service == family && c.Values.Any(v => Identifiers.Contains(v.Label) && Same(v.Label, v.Value, account)));
                    if (card is null) cards.Add(card = new Card(family, ""));
                    var from = $"{a.Name} · {id.Display}";
                    card.Add(card.Values.FirstOrDefault(v => Identifiers.Contains(v.Label) && Same(v.Label, v.Value, account)).Label ?? "UID", account, from);
                    if (family.StartsWith("WhatsApp") && Regex.Match(account, @"^(\d{7,15})@s\.whatsapp\.net$") is { Success: true } jid)
                        card.Add("Phone", "+" + jid.Groups[1].Value, from, true);
                    // the name it shows: a number, address or @username as what it is, else a name
                    var kind = new AccountId("", label).IsEmail ? "E-mail" : Regex.IsMatch(label, @"^\+?\d{7,15}$") ? "Phone" : label.StartsWith('@') ? "Username" : "Full name";
                    if (label != "" && !card.Values.Any(v => v.Label != kind && Same(v.Label, v.Value, label)))
                        card.Add(kind, label, $"{a.Name} · {name!.Display}");
                    card.Sources.Add(a.Name);
                }
            }
            catch (SqliteException) { }
        }
    }

    static Card Join(List<Card> cards, string service, string user, IEnumerable<(string Label, string Value)> ids)
    {
        var list = ids.ToList();
        if (cards.FirstOrDefault(c => c.Service == service && c.User == user && c.Shares(list)) is { } card) return card;
        cards.Add(card = new Card(service, user));
        return card;
    }

    /// <summary>The WeChat accounts, one per UIN: the fields of its userinfo known to be its ids and nickname, and its picture.</summary>
    static void WeChat(SqliteConnection con, ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, List<string> norm, string[] paths, List<Card> cards)
    {
        int field = norm.FindIndex(n => n.StartsWith("field id")), value = norm.IndexOf("value"), uin = norm.FindIndex(n => n.EndsWith("uin"));
        if (value < 0) return;
        var byAccount = new Dictionary<string, Dictionary<string, string>>();
        foreach (var r in ArtifactQuery.All(con, a))
        {
            string V(int i) => i < 0 ? "" : ValueFormatter.Format(r.Values[i], cols[i]).Trim();
            if (!byAccount.TryGetValue(V(uin), out var f)) byAccount[V(uin)] = f = [];
            f.TryAdd(V(field), V(value));
        }
        foreach (var (u, f) in byAccount)
        {
            var values = WeChatFields.Where(x => f.GetValueOrDefault(x.Id) is { Length: > 0 }).Select(x => (x.Label, Value: f[x.Id], From: $"{a.Name} · field {x.Id}")).ToList();
            if (!values.Any(x => Identifiers.Contains(x.Label))) continue;
            var card = Join(cards, "WeChat", "", values.Select(x => (x.Label, x.Value)));
            foreach (var x in values) card.Add(x.Label, x.Value, x.From);
            if (u != "") card.Add("Account ID", u, $"{a.Name} · {cols[uin].Display}");
            card.Sources.Add(a.Name);
            card.Packages.Add("com.tencent.mm");
            if (f.GetValueOrDefault("2") is { Length: > 0 } wxid && u != "") card.Pictures.AddRange(WeChatAvatars(u, wxid, paths));
        }
    }

    /// <summary>Where each card's app was installed from: the installer of its package in the package list, Google Play
    /// named. Not for Google, an account of the system.</summary>
    static void Installers(SqliteConnection con, List<ArtifactInfo> arts, List<Card> cards)
    {
        if (!cards.Any(c => c.Packages.Count > 0 && c.Service != "Google")) return;
        foreach (var a in arts.Where(a => a.RecordCount > 0 && a.Name.Contains("package", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var cols = ArtifactQuery.ExistingColumns(con, a);
                var norm = cols.Select(AccountRoster.Norm).ToList();
                int name = norm.IndexOf("name"), installer = norm.IndexOf("installer");
                if (name < 0 || installer < 0) continue;
                var of = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in ArtifactQuery.All(con, a))
                    of.TryAdd(ValueFormatter.Format(r.Values[name], cols[name]).Trim(), ValueFormatter.Format(r.Values[installer], cols[installer]).Trim());
                foreach (var c in cards.Where(c => c.Service != "Google"))
                    foreach (var p in c.Packages)
                        if (of.GetValueOrDefault(p) is { Length: > 0 } i)
                            c.Add("Installation source", i == "com.android.vending" ? "Google Play (com.android.vending)" : i, $"{a.Name} · {cols[installer].Display} of {p}");
                return;
            }
            catch (SqliteException) { }
        }
    }
}
