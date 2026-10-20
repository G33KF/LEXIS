using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>Where a media file sits on the device, read from its path only: the gallery (shared storage the phone's
/// gallery indexes, iOS's camera roll), an app's cache, the system partitions; and the app a path belongs to.</summary>
public static partial class MediaOrigin
{
    // Android shared storage, as an extraction spells it.
    [GeneratedRegex(@"^/(data/media/\d+|sdcard|storage/emulated/\d+|mnt/sdcard|storage/[0-9A-F]{4}-[0-9A-F]{4})/", RegexOptions.IgnoreCase)]
    private static partial Regex SharedStorage();

    // An app's private or external data folder: /data/data/<pkg>/, /data/user/0/<pkg>/, …/Android/data/<pkg>/.
    [GeneratedRegex(@"/(?:data/data|data/user(?:_de)?/\d+|Android/(?:data|media|obb))/([A-Za-z][\w]*(?:\.[\w]+)+)(?:/|$)")]
    private static partial Regex PackageDir();

    static readonly string[] SystemRoots = ["/system/", "/system_ext/", "/vendor/", "/product/", "/odm/", "/apex/", "/data/app/",
        "/System/", "/usr/", "/Library/", "/Applications/", "/private/var/containers/Bundle/"];

    /// <summary>On the phone's gallery: Android shared storage outside Android/ and hidden folders (what the media store
    /// indexes), or iOS's DCIM.</summary>
    // ponytail: a folder holding a .nomedia file is still counted; check the extraction's file list if that matters.
    public static bool IsGallery(string devicePath)
    {
        var p = Slash(devicePath);
        if (p.Contains("/Media/DCIM/", StringComparison.OrdinalIgnoreCase)) return true;
        if (SharedStorage().Match(p) is not { Success: true } m) return false;
        var rest = p[m.Length..];
        return !rest.StartsWith("Android/", StringComparison.OrdinalIgnoreCase) && !rest.Split('/')[..^1].Any(s => s.StartsWith('.')) && !IsCache(p);
    }

    /// <summary>In a cache, thumbnail or temporary folder.</summary>
    public static bool IsCache(string devicePath) =>
        Slash(devicePath).Split('/')[..^1].Any(s => s.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
            s.Equals(".thumbnails", StringComparison.OrdinalIgnoreCase) || s.Equals("thumbnails", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("tmp", StringComparison.OrdinalIgnoreCase) || s.Equals("temp", StringComparison.OrdinalIgnoreCase));

    /// <summary>On a system partition or inside an installed app's package: shipped with the system or the app, not made by the user.</summary>
    public static bool IsSystem(string devicePath)
    {
        var p = Slash(devicePath);
        return SystemRoots.Any(r => p.StartsWith(r, StringComparison.Ordinal));
    }

    /// <summary>The Android package whose data folder holds the path, or null.</summary>
    public static string? Package(string devicePath) => PackageDir().Match(Slash(devicePath)) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The app's name when the package (or a name containing it) is a known one: "com.whatsapp" → "WhatsApp".</summary>
    public static string? AppName(string? packageOrName)
    {
        if (string.IsNullOrWhiteSpace(packageOrName)) return null;
        if (Known.TryGetValue(packageOrName, out var n)) return n;
        return Names.FirstOrDefault(x => packageOrName.StartsWith(x, StringComparison.OrdinalIgnoreCase) &&
                                         (packageOrName.Length == x.Length || !char.IsLetterOrDigit(packageOrName[x.Length])));
    }

    static string Slash(string p) => "/" + p.Replace('\\', '/').TrimStart('/');

    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.whatsapp"] = "WhatsApp", ["com.whatsapp.w4b"] = "WhatsApp Business", ["org.telegram.messenger"] = "Telegram",
        ["org.telegram.messenger.web"] = "Telegram", ["org.thunderdog.challegram"] = "Telegram X", ["org.thoughtcrime.securesms"] = "Signal",
        ["com.facebook.katana"] = "Facebook", ["com.facebook.orca"] = "Messenger", ["com.facebook.lite"] = "Facebook Lite",
        ["com.instagram.android"] = "Instagram", ["com.snapchat.android"] = "Snapchat", ["com.tencent.mm"] = "WeChat",
        ["jp.naver.line.android"] = "LINE", ["com.viber.voip"] = "Viber", ["com.skype.raider"] = "Skype", ["com.discord"] = "Discord",
        ["com.zhiliaoapp.musically"] = "TikTok", ["com.ss.android.ugc.trill"] = "TikTok", ["com.twitter.android"] = "X (Twitter)",
        ["com.kakao.talk"] = "KakaoTalk", ["com.imo.android.imoim"] = "imo", ["kik.android"] = "Kik", ["com.wire"] = "Wire",
        ["ch.threema.app"] = "Threema", ["com.microsoft.teams"] = "Teams", ["us.zoom.videomeetings"] = "Zoom",
        ["com.google.android.gm"] = "Gmail", ["com.google.android.apps.photos"] = "Google Photos", ["com.android.chrome"] = "Chrome",
        ["com.google.android.apps.messaging"] = "Messages", ["com.samsung.android.messaging"] = "Samsung Messages",
        ["com.android.mms"] = "Messages", ["com.google.android.youtube"] = "YouTube", ["com.google.android.apps.maps"] = "Google Maps",
        ["com.google.android.googlequicksearchbox"] = "Google", ["com.google.android.apps.docs"] = "Google Drive",
        ["com.dropbox.android"] = "Dropbox", ["com.microsoft.skydrive"] = "OneDrive", ["com.sec.android.gallery3d"] = "Samsung Gallery",
        ["com.miui.gallery"] = "Xiaomi Gallery", ["com.android.camera"] = "Camera", ["com.sec.android.app.camera"] = "Camera",
        ["com.google.android.GoogleCamera"] = "Camera", ["com.linkedin.android"] = "LinkedIn", ["com.pinterest"] = "Pinterest",
        ["com.reddit.frontpage"] = "Reddit", ["com.tinder"] = "Tinder", ["com.bumble.app"] = "Bumble", ["com.grindrapp.android"] = "Grindr",
        ["com.badoo.mobile"] = "Badoo", ["org.mozilla.firefox"] = "Firefox", ["com.sec.android.app.sbrowser"] = "Samsung Internet",
        ["com.opera.browser"] = "Opera", ["com.brave.browser"] = "Brave", ["com.microsoft.emmx"] = "Edge",
        ["com.paypal.android.p2pmobile"] = "PayPal", ["com.revolut.revolut"] = "Revolut", ["com.binance.dev"] = "Binance",
        ["com.coinbase.android"] = "Coinbase", ["com.ubercab"] = "Uber", ["com.spotify.music"] = "Spotify", ["com.netflix.mediaclient"] = "Netflix",
        ["com.amazon.mShop.android.shopping"] = "Amazon", ["com.ebay.mobile"] = "eBay", ["com.alibaba.aliexpresshd"] = "AliExpress",
        ["com.shazam.android"] = "Shazam", ["com.truecaller"] = "Truecaller", ["com.google.android.apps.tachyon"] = "Google Meet",
        ["com.google.android.keep"] = "Google Keep", ["com.google.android.calendar"] = "Google Calendar", ["com.google.android.contacts"] = "Contacts",
        ["com.samsung.android.app.notes"] = "Samsung Notes", ["com.evernote"] = "Evernote", ["com.microsoft.office.outlook"] = "Outlook",
    };

    // App names an artifact's name may start with ("WhatsApp - Messages", "Telegram Chats").
    static readonly string[] Names = [.. Known.Values.Distinct().OrderByDescending(n => n.Length), "iMessage", "Photos", "Safari"];
}
