using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>A top-level group ("Messages", "Calls", ...) with its MDL2 glyph.</summary>
public sealed record MacroCategory(string Name, string Glyph);

/// <summary>
/// Sorts any artifact into a group and data type ("Search &amp; Web" › "Cookies") by keywords in its name
/// and ALEAPP category, so new LEAPP modules land somewhere sensible without code changes. First matching rule wins.
/// </summary>
public static partial class MacroCategories
{
    public static readonly MacroCategory Application = new("Application", "");
    public static readonly MacroCategory Calendar = new("Calendar", "");
    public static readonly MacroCategory Calls = new("Calls", "");
    public static readonly MacroCategory Contacts = new("Contacts", "");
    public static readonly MacroCategory DeviceInfo = new("Device Info", "");
    public static readonly MacroCategory Finance = new("Finance & Purchase", "");
    public static readonly MacroCategory Location = new("Location Related", "");
    public static readonly MacroCategory Media = new("Media", "");
    public static readonly MacroCategory Memos = new("Memos", "");
    public static readonly MacroCategory Messages = new("Messages", "");
    public static readonly MacroCategory Networks = new("Networks & Connections", "");
    public static readonly MacroCategory Physical = new("Physical Activities", "");
    public static readonly MacroCategory Web = new("Search & Web", "");
    public static readonly MacroCategory Social = new("Social Media", "");
    public static readonly MacroCategory System = new("System & Logs", "");
    public static readonly MacroCategory Accounts = new("User Accounts & Details", "");
    public static readonly MacroCategory Other = new("Other", "");

    // Word-bounded so "messaging" (FCM) is not "messages" and "calendar" is not "call".
    static readonly (Regex Pattern, MacroCategory Macro, string Type)[] Rules =
    [
        (R(@"e-?mails?|gmail|mail"), Messages, "Emails"),
        (R(@"sms|mms|imessages?"), Messages, "SMS / MMS"),
        (R(@"messages?|chats?|conversations?|im"), Messages, "Instant Messages"),
        (R(@"voicemails?"), Calls, "Voicemail"),
        (R(@"calls?|call ?logs?|voip|dialer"), Calls, "Call Log"),
        (R(@"contacts?|address ?book|group members?|participants?"), Contacts, "Contacts"),
        (R(@"payments?|transactions?|transfers?|wallet|bank\w*|monetary|account holders?|balance|bunq|alipay|paypal|revolut|purchases?|orders?"), Finance, "Transfers & Payments"),
        (R(@"passwords?|credentials?|logins?|keychain|passkeys?"), Accounts, "Passwords"),
        (R(@"accounts?|tokens?|authentication|users? ?accounts?|user ?profiles?"), Accounts, "User Accounts"),
        (R(@"trips?|journeys?|directions?"), Location, "Journeys"),
        (R(@"locations?|geo\w*|gps|maps?|airtags?|places|semantic|ulr|find ?my"), Location, "Locations"),
        (R(@"moments|posts?|feeds?|social|stories|tiktok|facebook|instagram|twitter|snapchat|telegram|whatsapp"), Social, "Social Media Activity"),
        (R(@"calendars?|reminders?|events? calendar"), Calendar, "Calendar"),
        (R(@"notes?|memos?"), Memos, "Notes"),
        (R(@"videos?"), Media, "Videos"),
        (R(@"audio|recordings?|music|tracks?|songs?|playlists?|podcasts?"), Media, "Audio"),
        (R(@"images?|photos?|gallery|camera|thumbnails?|screenshots?"), Media, "Images"),
        (R(@"media|storage|files?|drive|documents?"), Media, "Files"),
        (R(@"cookies?"), Web, "Cookies"),
        (R(@"bookmarks?|top ?sites|shortcuts"), Web, "Web Bookmarks"),
        (R(@"searche?s?|queries|translat\w*"), Web, "Searched Items"),
        (R(@"autofill|form ?data"), Web, "Autofill"),
        (R(@"downloads?"), Web, "Downloads"),
        (R(@"visits?|browser\w*|chrome|chromium|web|firefox|safari|tabs"), Web, "Web History"),
        (R(@"powerlog|wake|tombstones?|anrs?|walstrings|journal\w*|crash\w*|diagnostics?"), System, "Logs & Events"),
        (R(@"health|fitness|steps|workouts?|sleep|heart"), Physical, "Activity Sensor Data"),
        (R(@"notifications?|fcm|firebase"), Application, "Notifications"),
        (R(@"installed\w*|packages?|dex|uninstalled"), Application, "Installed Applications"),
        (R(@"usage|wellbeing|standby|recent activity|biome|knowledge ?graph|knowledgec|screen ?time|app ?intents?"), Application, "Application Usage"),
        (R(@"permissions?|app ops?|privacy|roles?"), Application, "Permissions"),
        (R(@"apps?|spotlight|screens?|control center|cache data|ai provenance"), Application, "Application Data"),
        (R(@"bluetooth|paired"), Networks, "Bluetooth"),
        (R(@"wi-?fi|wlan|hotspots?"), Networks, "Wireless Networks"),
        (R(@"cellular|cell ?towers?|sim|siminfo|sim ?cards?|carriers?"), Networks, "Cellular"),
        (R(@"netstats|network|connections?|vpn|ip"), Networks, "Network Usage"),
        (R(@"devices?|settings\w*|os version|users?|ios build|identifiers?|identity lookup service|trusted peers|properties|build"), DeviceInfo, "Device Info"),
        (R(@"logs?|system|power|shutdown|boot|clock|os updates?|battery"), System, "Logs & Events"),
    ];

    static Regex R(string words) => new($@"(?<![a-z])(?:{words})(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<MacroCategory> All { get; } =
        [Application, Calendar, Calls, Contacts, DeviceInfo, Finance, Location, Media, Memos, Messages, Networks, Physical, Web, Social, System, Accounts, Other];

    // An app's own data folder on the device: Android /data/data/<package>, iOS app containers and backup domains.
    static readonly Regex AppFolder = new(@"^/(?:data/(?:data|user(?:_de)?/\d+)|(?:sdcard|storage/emulated/\d+)/Android/data" +
        @"|private/var/mobile/Containers/(?:Data/Application|Shared/AppGroup))/(?<app>[^/]+)/|^AppDomain(?:Group)?-(?<app>[^/]+)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // System stores and services, not apps a user opens: their data stays in the macro categories.
    // Vendor stores count too: com.samsung.android.providers.contacts, com.sec.android.provider.logsprovider.
    static readonly Regex SystemPackage = new(@"^(?:android|com\.google\.android\.gms|com\.(?:google\.)?android\.(?:server|settings|systemui|phone|bluetooth|launcher\d*)(?:\..+)?)$" +
        @"|^com\.(?:android|google\.android|samsung\.android|sec\.android|lge|motorola|huawei\.android|miui|oneplus|coloros)\.providers?\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // A category named after a kind of data (Call Logs, Contacts, Bluetooth, SMS) is the system's, whoever stores it.
    static readonly HashSet<MacroCategory> SystemKinds = [Calls, Contacts, Networks, DeviceInfo, System];

    /// <summary>The app folder (package or container) a device path is in, or null.</summary>
    public static string? AppOf(string devicePath) =>
        AppFolder.Match(devicePath) is { Success: true } m && !SystemPackage.IsMatch(m.Groups["app"].Value) ? m.Groups["app"].Value : null;

    static bool IsDataKind(string category) =>
        Classify(new ArtifactInfo { Category = category, Name = category, TableName = "", Columns = [] }) is var (macro, type)
        && (SystemKinds.Contains(macro) || type == "SMS / MMS");

    /// <summary>
    /// Artifacts grouped by the app they come from: an ALEAPP category is an app's when at least half of its
    /// artifacts read one and the same app folder; categories reading the same folder (Chrome Sync, Chromium) are
    /// one app, named after its largest category. Modules that scan many apps (SQLite journals, MMKV) are not apps.
    /// </summary>
    public static IReadOnlyList<(string Name, string App, IReadOnlyList<ArtifactInfo> Artifacts)> Applications(IEnumerable<ArtifactInfo> artifacts) =>
        artifacts.GroupBy(a => a.Category).Where(g => !IsDataKind(g.Key))
            .Select(g => (Category: g, Apps: g.Select(a => EvidenceOrigin.Sources(a).Select(AppOf).OfType<string>().Distinct().ToList()).ToList()))
            .Select(x => (x.Category, App: x.Apps.SelectMany(a => a).Distinct().ToList() is [var one] && x.Apps.Count(a => a.Count > 0) * 2 >= x.Apps.Count ? one : null))
            .Where(x => x.App is not null)
            .GroupBy(x => x.App!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.OrderByDescending(x => x.Category.Count()).ThenBy(x => x.Category.Key.Length).First().Category.Key, App: g.Key,
                          Artifacts: (IReadOnlyList<ArtifactInfo>)g.SelectMany(x => x.Category).ToList()))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // ALEAPP's Chromium artifacts name Chrome, Brave, Edge and Opera, call Samsung Internet "Browser" (its app_sbrowser
    // folder) and give any other build its package.
    static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Browser"] = "Samsung Internet", ["com.sec.android.app.sbrowser"] = "Samsung Internet",
        ["com.sec.android.app.sbrowser.beta"] = "Samsung Internet Beta", ["com.chrome.beta"] = "Chrome Beta",
        ["com.chrome.dev"] = "Chrome Dev", ["com.chrome.canary"] = "Chrome Canary", ["com.kiwibrowser.browser"] = "Kiwi",
        ["com.vivaldi.browser"] = "Vivaldi", ["com.yandex.browser"] = "Yandex", ["com.duckduckgo.mobile.android"] = "DuckDuckGo",
        ["com.mi.globalbrowser"] = "Mi Browser", ["com.heytap.browser"] = "HeyTap Browser", ["com.huawei.browser"] = "Huawei Browser",
        ["com.hihonor.baidu.browser"] = "Honor Browser", ["com.UCMobile.intl"] = "UC Browser",
    };

    /// <summary>The browser a Chromium "Browser Name" value stands for, with the recorded value when they differ.</summary>
    public static string BrowserLabel(string recorded) =>
        Browsers.TryGetValue(recorded, out var name) ? $"{name} ({recorded})" : recorded;

    public static MacroCategory Of(ArtifactInfo a) => Classify(a).Macro;

    /// <summary>The group and the data type within it: what the artifact holds ("Biome - Battery Level" → battery) is
    /// tried before the app it comes from.</summary>
    public static (MacroCategory Macro, string Type) Classify(ArtifactInfo a)
    {
        var own = a.Name.StartsWith(a.Category + " - ", StringComparison.OrdinalIgnoreCase) ? a.Name[(a.Category.Length + 3)..] : a.Name;
        foreach (var text in new[] { own, a.Name + " " + a.Category })
        {
            if (a.Conversation is not null && !Rules[0].Pattern.IsMatch(text))
                return Rules[1].Pattern.IsMatch(text) ? (Messages, Rules[1].Type) : (Messages, Rules[2].Type);
            foreach (var (pattern, macro, type) in Rules)
                if (pattern.IsMatch(text)) return (macro, type);
        }
        return (Other, "Other");
    }

    // apps known by name, so an app sorts by what it is rather than by its biggest artifact (WhatsApp's contacts, Chrome's cookies)
    static readonly (Regex Pattern, string Kind)[] AppKinds =
    [
        (R(@"whats ?app|telegram|signal|we ?chat|viber|line|kik|threema|wire|messenger|discord|skype|teams|imessages?|sms|mms|zalo|kakao\w*|session|element|wickr|icq|textnow|google voice"), "Messaging"),
        (R(@"facebook|instagram|tik ?tok|twitter|x|snapchat|linked ?in|reddit|pinterest|threads|bereal|tumblr|vk|vkontakte|weibo|douyin|kuaishou|likee|tinder|bumble|grindr|hinge|badoo"), "Social Network"),
        (R(@"chrome|chromium|firefox|safari|edge|opera|brave|samsung internet|s ?browser|duck ?duck ?go|vivaldi|yandex|kiwi|browser\w*|web ?view"), "Web Navigation"),
        (R(@"e-?mails?|gmail|outlook|mail|proton ?mail|yahoo mail"), "Email"),
        (R(@"photos?|gallery|camera|media ?store|screenshots?|albums?"), "Gallery"),
    ];

    // the data a user makes, by group: the system's logs, settings, networks and app bookkeeping are not here
    static readonly Dictionary<MacroCategory, string> UserKinds = new()
    {
        [Messages] = "Messaging", [Social] = "Social Network", [Web] = "Web Navigation", [Media] = "Gallery", [Calls] = "Calls",
        [Contacts] = "Contacts", [Memos] = "Notes", [Calendar] = "Calendar", [Location] = "Locations", [Finance] = "Finance & Purchases",
        [Physical] = "Health & Fitness",
    };

    /// <summary>The order the user's contents are shown in.</summary>
    public static IReadOnlyList<string> UserContentKinds { get; } =
        ["Messaging", "Social Network", "Calls", "Contacts", "Email", "Web Navigation", "Gallery", "Locations", "Notes", "Calendar", "Finance & Purchases", "Health & Fitness"];

    /// <summary>
    /// What kind of user content an ALEAPP category holds (Messaging, Social Network, Web Navigation, Gallery…): an app
    /// known by its name is what it is, any other category is the kind of most of its records. Null when its records are
    /// not the user's (logs, settings, networks, app usage, device information).
    /// </summary>
    public static string? UserContentKind(string category, IEnumerable<ArtifactInfo> artifacts)
    {
        var kinds = artifacts.Select(a => (Kind: UserKinds.GetValueOrDefault(Of(a)), a.RecordCount)).Where(x => x.Kind is not null).ToList();
        if (kinds.Count == 0) return null;
        return AppKinds.FirstOrDefault(k => k.Pattern.IsMatch(category)).Kind
            ?? kinds.GroupBy(x => x.Kind).MaxBy(g => g.Sum(x => x.RecordCount))!.Key;
    }
}
