using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AleappReader.Core;

/// <summary>How a gallery file was made, when the device says so.</summary>
public enum Capture { None, Camera, Screenshot }

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

    // Android: the folders screenshots and screen recordings go to, and the names the system gives them.
    [GeneratedRegex(@"^(screenshots?|screen[ _]?records?|screen[ _]?recordings?|screen[ _]?recorder)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenFolder();
    [GeneratedRegex(@"^(screenshot|screen[_-]?record)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenName();
    // The camera's folders: DCIM/Camera, or a DCF folder (DCIM/100ANDRO, DCIM/100MEDIA).
    [GeneratedRegex(@"^DCIM/(Camera|\d{3}[A-Z0-9_]{5})/", RegexOptions.IgnoreCase)]
    private static partial Regex CameraDir();

    /// <summary>Taken with the camera, or a screenshot or screen recording. Android: from the gallery folder and the name the
    /// system gives the file. iOS keeps both in DCIM/1xxAPPLE, so there it is only what the Photos library says
    /// (<paramref name="photos"/>, from <see cref="Photos"/>).</summary>
    public static Capture CaptureOf(string devicePath, IReadOnlyDictionary<string, Capture>? photos = null)
    {
        var p = Slash(devicePath);
        var i = p.IndexOf("/Media/DCIM/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return photos?.GetValueOrDefault(p[(i + "/Media/".Length)..]) ?? Capture.None;
        if (!IsGallery(p) || SharedStorage().Match(p) is not { Success: true } m) return Capture.None;
        var rest = p[m.Length..];
        var parts = rest.Split('/');
        if (parts[..^1].Any(s => ScreenFolder().IsMatch(s)) || ScreenName().IsMatch(parts[^1])) return Capture.Screenshot;
        return CameraDir().IsMatch(rest) ? Capture.Camera : Capture.None;
    }

    /// <summary>iOS: the camera roll's files that iLEAPP's Photos.sqlite artifacts mark as a screenshot or screen recording
    /// (kind subtype 10 or 103, or detected screenshot) or as taken with the back or front camera (imported by 1 or 2), keyed
    /// by their path under Media ("DCIM/100APPLE/IMG_0001.HEIC"). Empty when the report holds none of those artifacts.</summary>
    public static Dictionary<string, Capture> Photos(SqliteConnection con, IEnumerable<ArtifactInfo> artifacts)
    {
        var kinds = new Dictionary<string, Capture>(StringComparer.OrdinalIgnoreCase);
        // iLEAPP numbers a header per query ("zAsset-Directory-Path-35")
        static bool Is(ColumnInfo c, string header) => c.Display == header || c.Display.StartsWith(header + "-", StringComparison.Ordinal);
        foreach (var a in artifacts.Where(a => a.Columns.Any(c => Is(c, "zAsset-Directory-Path"))))
        {
            IReadOnlyList<ColumnInfo> cols;
            try { cols = ArtifactQuery.ExistingColumns(con, a); }
            catch (SqliteException) { continue; }
            ColumnInfo? C(string header) => cols.FirstOrDefault(c => Is(c, header));
            if (C("zAsset-Directory-Path") is not { } dir || C("zAsset-Filename") is not { } file) continue;
            var (sub, shot, by) = (C("zAsset-Kind-Sub-Type"), C("zAsset-Is_Detected_Screenshot"), C("zAddAssetAttr-Imported by"));
            if (sub is null && shot is null && by is null) continue;
            string Sel(ColumnInfo? c) => c is null ? "NULL" : ArtifactQuery.Q(c.Sql);
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"SELECT {Sel(dir)}, {Sel(file)}, {Sel(sub)}, {Sel(shot)}, {Sel(by)} FROM {ArtifactQuery.Q(a.TableName)}";
            try
            {
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    // values read "10-SpringBoard-Screenshot-10": the code leads
                    int? N(int i) => r.IsDBNull(i) || !int.TryParse(r.GetValue(i).ToString()?.Split('-')[0], out var n) ? null : n;
                    var kind = N(2) is 10 or 103 || N(3) == 1 ? Capture.Screenshot : N(4) is 1 or 2 ? Capture.Camera : Capture.None;
                    if (kind == Capture.None || r.IsDBNull(0) || r.IsDBNull(1)) continue;
                    var key = $"{r.GetValue(0)}/{r.GetValue(1)}";
                    if (kind == Capture.Screenshot || !kinds.ContainsKey(key)) kinds[key] = kind;
                }
            }
            catch (SqliteException) { } // a table that does not read adds nothing
        }
        return kinds;
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
