using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>One identifier of an account as its column calls it: "Phone Number", "+39…".</summary>
public sealed record AccountId(string Label, string Value)
{
    static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[a-z]{2,}$", RegexOptions.IgnoreCase);
    static readonly Regex PhoneLabel = new(@"phone|mobile|msisdn", RegexOptions.IgnoreCase);
    static readonly Regex PhoneValue = new(@"^\+?[\d\s\-().]+$");

    public bool IsEmail => Email.IsMatch(Value);
    /// <summary>A number in a phone column, or one written with its country prefix (+…); 7 to 15 digits either way.</summary>
    public bool IsPhone => PhoneValue.IsMatch(Value) && Value.Count(char.IsDigit) is >= 7 and <= 15 && (Value.StartsWith('+') || PhoneLabel.IsMatch(Label));
}

/// <summary>One account found on the device: the service it is for, its identifier (the first of <see cref="Ids"/>), the
/// names recorded with it and the artifacts it was found in.</summary>
public sealed record AccountEntry(string Service, string Account, string Names, string Sources, IReadOnlyList<AccountId> Ids);

/// <summary>A line of the accounts summary: a name, number, address or account, the apps and services it was found in,
/// and the rest (the artifacts, the account's other names and ids).</summary>
public sealed record AccountLine(string Value, string Where, string Detail);

/// <summary>One part of the accounts summary: a title, what its values are and its lines.</summary>
public sealed record AccountGroup(string Title, string ValueHeader, string DetailHeader, IReadOnlyList<AccountLine> Rows)
{
    public int Count => Rows.Count;
    public string Header => $"{Title} ({Count})";
    public bool Expanded => Count <= 12;
    /// <summary>The lines as text, value · where · detail.</summary>
    public string Lines => string.Join(Environment.NewLine, Rows.Select(r => string.Join("   ·   ", new[] { r.Value, r.Where, r.Detail }.Where(x => x != ""))));
}

/// <summary>
/// The accounts of the device in one list, each once per service: read from the artifacts about accounts, profiles and
/// logins. In each row the identifier is the first column of <see cref="IdColumns"/> it fills (account name,
/// username, e-mail, phone…), the service its account type, origin or description column, else the app the artifact
/// is named for ("WhatsApp - User Profile" → WhatsApp); the names come from its name columns. Never a password or key.
/// </summary>
public static class AccountRoster
{
    const int MaxRows = 20_000; // ponytail: an account artifact this big is a log, not a roster; open it instead

    internal static readonly Regex AccountArtifact = new(@"account|owner|apple ?id|profile|login",RegexOptions.IgnoreCase);
    // the SIM slots and calling services of the phone app ("Telecom Phone Accounts") name the device, not its user; the
    // device's own users and profiles (owner, work profile, Secure Folder) are in the Users tab
    internal static readonly Regex NotTheUser = new(@"telecom|users and profiles", RegexOptions.IgnoreCase);
    internal static readonly Regex Secret = new(@"password|passwd|token|secret|\bkey\b|hash|cookie", RegexOptions.IgnoreCase);
    // a value standing for no account (iOS's App Store daemons are signed in as "local")
    internal static readonly Regex Placeholder = new(@"^(local|unknown|null|none|n/?a|-+)$", RegexOptions.IgnoreCase);
    // the label an app gives the account it keeps for itself, not the user's (Google Meet's "Meet", Microsoft's "Work account")
    // ponytail: the labels seen so far; add one when a report shows it
    static readonly Regex AppLabel = new(@"^(meet|duo|work account|personal account)$", RegexOptions.IgnoreCase);

    /// <summary>A value that names no account of the user: a placeholder, an app's own label, or the app's name itself
    /// (WhatsApp's account on Android is called "WhatsApp", a bank's "Intesa Sanpaolo" for com.….intesasanpaolo).</summary>
    internal static bool NotAnId(string value, string family) =>
        Placeholder.IsMatch(value) || AppLabel.IsMatch(value) || family != "" && string.Equals(Letters(value), Letters(family), StringComparison.OrdinalIgnoreCase);

    static string Letters(string v) => new(v.Where(char.IsLetterOrDigit).ToArray());

    static readonly string[] IdColumns = ["account name", "username", "user name", "email", "e mail", "email address", "google account email",
        "apple id", "account", "mobile number", "phone number", "phone", "handle", "user id", "account id", "uid", "registration screen phone number"];
    static readonly string[] ServiceColumns = ["account type", "origin", "origin url", "signon realm", "account desc", "account description", "bundle id"];
    static readonly string[] NameColumns = ["full name", "display name", "holder name", "name", "nickname", "given name"];

    internal static string Norm(ColumnInfo c) => Regex.Replace(Regex.Replace(c.Display.ToLowerInvariant(), "[^a-z0-9]", " "), @"\s+", " ").Trim();

    public static IReadOnlyList<AccountEntry> Collect(SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var found = new Dictionary<(string, string), (string Service, string Account, SortedSet<string> Names, SortedSet<string> Sources, List<AccountId> Ids)>();
        foreach (var a in artifacts.Where(a => a.RecordCount is > 0 and <= MaxRows && a.Conversation is null && AccountArtifact.IsMatch(a.Name) && !NotTheUser.IsMatch(a.Name)))
        {
            IReadOnlyList<ColumnInfo> cols;
            try { cols = ArtifactQuery.ExistingColumns(con, a); }
            catch (SqliteException) { continue; }
            int[] All(string[] names) => names.Select(n => cols.ToList().FindIndex(c => Norm(c) == n && !c.IsMedia && !Secret.IsMatch(c.Display))).Where(i => i >= 0).ToArray();
            int Find(string[] names) => All(names).FirstOrDefault(-1);
            var app = a.Name.Split(" - ")[0].Trim();
            if (KeyValueFields.TryGetValue(app, out var fields)) { KeyValue(con, a, cols, app, fields, found); continue; }
            var ids = All(IdColumns);
            if (ids.Length == 0) continue;
            var service = Find(ServiceColumns);
            var name = Find(NameColumns);
            var (first, last) = (Find(["first name"]), Find(["last name"]));
            try
            {
                foreach (var r in ArtifactQuery.All(con, a))
                {
                    string V(int i) => i < 0 ? "" : ValueFormatter.Format(r.Values[i], cols[i]).Trim();
                    // every identifier the row fills, the first one naming the account; "tel:+39…" is the number
                    var svc = V(service) is { Length: > 0 } s ? s : app;
                    var family = Family(svc, [a.Name]);
                    var rowIds = ids.Select(i => new AccountId(cols[i].Display, Regex.Replace(V(i), "^tel:", "", RegexOptions.IgnoreCase).Trim()))
                        .Where(x => x.Value != "" && !NotAnId(x.Value, family)).DistinctBy(x => x.Value, StringComparer.OrdinalIgnoreCase).ToList();
                    Add(found, svc, rowIds, [V(name), $"{V(first)} {V(last)}".Trim()], a.Name);
                }
            }
            catch (SqliteException) { } // an unreadable artifact adds nothing
        }
        return found.Values.OrderBy(e => e.Service, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Account, StringComparer.OrdinalIgnoreCase)
            .Select(e => new AccountEntry(e.Service, e.Account, string.Join("\n", e.Names), string.Join("\n", e.Sources), e.Ids)).ToList();
    }

    static void Add(Dictionary<(string, string), (string Service, string Account, SortedSet<string> Names, SortedSet<string> Sources, List<AccountId> Ids)> found,
        string svc, IReadOnlyList<AccountId> rowIds, IEnumerable<string> names, string source)
    {
        if (rowIds.Count == 0) return;
        var account = rowIds[0].Value;
        var key = (svc.ToLowerInvariant(), account.ToLowerInvariant());
        if (!found.TryGetValue(key, out var e)) found[key] = e = (svc, account, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), []);
        foreach (var n in names.Where(n => n != "" && !string.Equals(n, account, StringComparison.OrdinalIgnoreCase)))
            e.Names.Add(n);
        e.Sources.Add(source);
        e.Ids.AddRange(rowIds.Where(x => !e.Ids.Any(y => string.Equals(y.Value, x.Value, StringComparison.OrdinalIgnoreCase))));
    }

    // apps whose account artifact is one field per row (field id, value): which field is the account id, the name, the
    // others; WeChat's userinfo ids (2 WeChat ID, 4 nickname, 6 phone, 42 alias) as the LEAPP's WeChat module notes them
    static readonly Dictionary<string, (string Id, string Label)[]> KeyValueFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WeChat"] = [("2", "WeChat ID"), ("42", "Alias"), ("6", "Phone Number"), ("4", "")],
    };

    /// <summary>The accounts of a key-value account artifact: per account (its UIN column if it has one), the fields known
    /// to be its identifiers, the first naming it, and the one with an empty label as its name.</summary>
    static void KeyValue(SqliteConnection con, ArtifactInfo a, IReadOnlyList<ColumnInfo> cols, string app, (string Id, string Label)[] fields,
        Dictionary<(string, string), (string Service, string Account, SortedSet<string> Names, SortedSet<string> Sources, List<AccountId> Ids)> found)
    {
        var list = cols.ToList();
        int field = list.FindIndex(c => Norm(c).StartsWith("field id")), value = list.FindIndex(c => Norm(c) == "value"), uin = list.FindIndex(c => Norm(c).EndsWith("uin"));
        if (field < 0 || value < 0) return;
        try
        {
            var byAccount = new Dictionary<string, Dictionary<string, string>>();
            foreach (var r in ArtifactQuery.All(con, a))
            {
                string V(int i) => i < 0 ? "" : ValueFormatter.Format(r.Values[i], cols[i]).Trim();
                if (!byAccount.TryGetValue(V(uin), out var f)) byAccount[V(uin)] = f = [];
                f.TryAdd(V(field), V(value));
            }
            foreach (var f in byAccount.Values)
            {
                string Of(string id) => f.TryGetValue(id, out var v) ? v : "";
                var ids = fields.Where(x => x.Label != "" && Of(x.Id) != "").Select(x => new AccountId(x.Label, Of(x.Id))).ToList();
                Add(found, app, ids, fields.Where(x => x.Label == "").Select(x => Of(x.Id)), a.Name);
            }
        }
        catch (SqliteException) { }
    }

    internal static readonly Regex Site = new(@"^(https?|android)://", RegexOptions.IgnoreCase);
    static readonly Regex Package = new(@"^[a-z][\w]*(\.[\w]+)+$", RegexOptions.IgnoreCase);
    static readonly Regex GenericSource = new(@"^(accounts?([ _](ce|de|data|extras|sync.*))?|sync accounts.*)$", RegexOptions.IgnoreCase);
    static readonly (string Name, Regex Match)[] Families = [.. new[]
    {
        // Chrome's signed-in account is a Google account; WhatsApp Business is an account of its own
        ("Google", @"google|gmail|com\.android\.vending|youtube|chrome preferences - accounts"), ("WhatsApp Business", @"com\.whatsapp\.w4b|whatsapp business"), ("WhatsApp", "whatsapp"), ("Telegram", @"telegram|telegra\.ph"),
        ("Messenger", @"facebook\.orca|messenger"), ("Facebook", "facebook"), ("Instagram", "instagram"), ("WeChat", @"wechat|tencent\.mm"),
        ("Signal", @"\bsignal\b|thoughtcrime"), ("Snapchat", "snapchat"), ("TikTok", "tiktok|musically|zhiliaoapp"), ("X (Twitter)", "twitter"),
        ("Microsoft", "microsoft|outlook|skype|hotmail|exchange"), ("Samsung", @"samsung|com\.osp\.app\.signin"), ("Viber", "viber"), ("Chrome", "chrome"),
        // the services of an Apple ID as iOS lists them (Accounts3: CloudKit, Game Center, iTunes Store, Messages…)
        ("Apple", @"\bapple\b|icloud|^(cloudkit|game ?center|device locator|find my.*|idms|itunes.*|messages|facetime|imessage)$"),
        // the phone maker's own mail app (com.android.email, com.hihonor.email)
        ("E-mail app", @"^com\.[\w.]+\.email$"),
    }.Select(f => (f.Item1, new Regex(f.Item2, RegexOptions.IgnoreCase)))];

    /// <summary>The app or service family an account belongs to: a well-known one named in its service or artifacts
    /// (com.google, "Google Account History" → Google), else the app of a package id (com.bunq → bunq), else the app its
    /// artifact is named for, unless that name is only about accounts in general ("Account Data"): then its service.</summary>
    static string Family(AccountEntry e) => Family(e.Service, e.Sources.Split('\n'));

    internal static string Family(string service, IReadOnlyList<string> sources)
    {
        if (Families.FirstOrDefault(f => f.Match.IsMatch(service) || sources.Any(f.Match.IsMatch)) is { Name: { } known }) return known;
        if (Package.IsMatch(service))
            return service.Split('.').Reverse().FirstOrDefault(p => !Regex.IsMatch(p, "^(android|app|apps|mobile|client|activity)$", RegexOptions.IgnoreCase)) ?? service;
        var app = sources[0].Split(" - ")[0].Trim();
        return GenericSource.IsMatch(app) ? service : app;
    }

    /// <summary>The site a saved login is for, by its domain (a.b.example.com → example.com, x.example.co.uk → example.co.uk),
    /// or the app of an android:// login.</summary>
    static string SiteOf(string service)
    {
        if (service.StartsWith("android://", StringComparison.OrdinalIgnoreCase)) return "app " + service[(service.LastIndexOf('@') + 1)..].TrimEnd('/');
        if (!Uri.TryCreate(service, UriKind.Absolute, out var u)) return service;
        if (u.HostNameType != UriHostNameType.Dns) return u.Host;
        var p = u.Host.Split('.');
        // ponytail: a short second level under a country code is taken as a public suffix (co.uk, com.au); a suffix list if it misleads
        var keep = p.Length >= 3 && p[^1].Length == 2 && p[^2].Length <= 3 ? 3 : 2;
        return string.Join('.', p.TakeLast(keep));
    }

    static string Digits(string v) => new(v.Where(char.IsDigit).ToArray());

    /// <summary>
    /// What identifies the user, each once: the names recorded (a name whose words are all in a longer one goes), the phone
    /// numbers (one number with or without its country code counts once) and e-mail addresses, each with the apps and
    /// services it was found in and the artifacts; then the user IDs and usernames of the apps that are neither, one line
    /// per account; then the sites and apps with a login saved by a password manager, one line per domain with its usernames.
    /// </summary>
    public static IReadOnlyList<AccountGroup> Summarize(IReadOnlyList<AccountEntry> entries)
    {
        var family = entries.ToDictionary(e => e, e => Site.IsMatch(e.Service) ? "" : Family(e));
        string Where(IEnumerable<AccountEntry> es) => string.Join(", ", es.Select(e => family[e] is { Length: > 0 } f ? f : "saved logins").Distinct(StringComparer.OrdinalIgnoreCase));
        static string Arts(IEnumerable<AccountEntry> es) => string.Join(", ", es.SelectMany(e => e.Sources.Split('\n')).Distinct(StringComparer.OrdinalIgnoreCase));
        static IEnumerable<string> NameList(AccountEntry e) => e.Names.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim());

        // names: one per spelling, without those whose words are all in a longer one ("Ann" in "Ann Example")
        var names = entries.SelectMany(e => NameList(e).Select(n => (Value: n, E: e))).GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase).ToList();
        static HashSet<string> Words(string n) => new(n.Split([' ', '.', ',', '_'], StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var nameLines = names.Where(g => !names.Any(o => o.Key.Length > g.Key.Length && Words(g.Key).IsSubsetOf(Words(o.Key))))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var es = names.Where(o => Words(o.Key).IsSubsetOf(Words(g.Key))).SelectMany(o => o.Select(x => x.E)).ToList();
                return new AccountLine(g.Key, Where(es), Arts(es));
            });

        // phone numbers: by their digits, a national number joining the international one it ends ("3001234567" in "+39 300 123 4567")
        var phones = entries.SelectMany(e => e.Ids.Where(i => i.IsPhone).Select(i => (i.Value, E: e))).ToList();
        var phoneLines = phones.Where(p => !phones.Any(o => Digits(o.Value).Length > Digits(p.Value).Length && Digits(o.Value).EndsWith(Digits(p.Value))))
            .GroupBy(p => Digits(p.Value)).OrderBy(g => g.Key)
            .Select(g =>
            {
                var es = phones.Where(o => g.Key.EndsWith(Digits(o.Value))).Select(o => o.E).ToList();
                return new AccountLine(g.OrderByDescending(p => p.Value.StartsWith('+')).First().Value, Where(es), Arts(es));
            });

        var emailLines = entries.SelectMany(e => e.Ids.Where(i => i.IsEmail).Select(i => (i.Value, E: e)))
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AccountLine(g.First().Value, Where(g.Select(x => x.E)), Arts(g.Select(x => x.E))));

        // the apps' own identifiers: what is not a number or an address, those being listed above
        var appLines = entries.Where(e => family[e] != "" && e.Ids[0] is { IsEmail: false, IsPhone: false })
            .GroupBy(e => (Family: family[e], Account: e.Account.ToLowerInvariant()))
            .OrderBy(g => g.Key.Family, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Account, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var account = g.First().Account;
                var also = g.SelectMany(e => e.Ids.Skip(1)).Where(i => !i.IsEmail && !i.IsPhone && i.Value.Length >= 3 && !string.Equals(i.Value, account, StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(i => i.Value, StringComparer.OrdinalIgnoreCase).Select(i => $"{i.Label} {i.Value}");
                var rest = new[] { string.Join(", ", g.SelectMany(NameList).Distinct(StringComparer.OrdinalIgnoreCase)), string.Join(", ", also) };
                return new AccountLine($"{g.First().Ids[0].Label} {account}", g.Key.Family, string.Join("   ·   ", rest.Where(p => p != "")));
            });

        var siteLines = entries.Where(e => family[e] == "").GroupBy(e => SiteOf(e.Service), StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(s => new AccountLine(s.Key, "", string.Join(", ", s.Select(e => e.Account).Distinct(StringComparer.OrdinalIgnoreCase))));

        return new AccountGroup[]
        {
            new("Names and nicknames", "Name", "Artifacts", [.. nameLines]),
            new("Phone numbers", "Number", "Artifacts", [.. phoneLines]),
            new("E-mail addresses", "Address", "Artifacts", [.. emailLines]),
            new("User IDs and usernames in apps", "Identifier", "Names and other identifiers", [.. appLines]),
            new("Sites and apps with a saved login (password manager)", "Site or app", "Usernames", [.. siteLines]),
        }.Where(g => g.Count > 0).ToList();
    }
}
