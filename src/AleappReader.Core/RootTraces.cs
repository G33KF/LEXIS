namespace AleappReader.Core;

/// <summary>
/// Well-known traces of rooting (Android) and jailbreaking (iOS): the apps' package or bundle ids and the files the tools
/// leave behind. A hit says the device carries the trace, not that it is rooted now; a su binary can sit in an image whose
/// root was removed, and a package name can appear in a web history. Compiled from public documentation of the tools.
/// </summary>
public static class RootTraces
{
    public const string Collection = "Root and jailbreak traces";

    static readonly string[] AndroidApps =
    [
        // Android root managers, one-click rooting apps and hooking frameworks
        "com.topjohnwu.magisk", "io.github.huskydg.magisk", "io.github.vvb2060.magisk", "me.weishu.kernelsu", "me.bmax.apatch",
        "eu.chainfire.supersu", "com.koushikdutta.superuser", "com.noshufou.android.su", "com.noshufou.android.su.elite",
        "com.thirdparty.superuser", "com.yellowes.su", "com.kingroot.kinguser", "com.kingo.root", "com.zhiqupk.root.global",
        "com.smedialink.oneclickroot", "com.alephzain.framaroot", "com.genymotion.superuser",
        "de.robv.android.xposed.installer", "org.meowcat.edxposed.manager", "org.lsposed.manager",
    ];

    static readonly string[] IosApps =
    [
        // iOS jailbreak package managers, jailbreak apps and their usual companions
        "com.saurik.Cydia", "org.coolstar.SileoStore", "xyz.willy.Zebra", "science.xnu.undecimus", "com.opa334.Dopamine",
        "com.opa334.TrollStore", "com.tigisoftware.Filza",
    ];

    static readonly string[] AndroidPaths =
    [
        "/system/bin/su", "/system/xbin/su", "/sbin/su", "/su/bin/su", "/system/sbin/su", "/vendor/bin/su",
        "/data/local/su", "/data/local/bin/su", "/data/local/xbin/su", "/system/app/Superuser.apk", "/system/app/SuperSU",
        "/data/adb/magisk", "/data/adb/ksu", "/data/adb/ap", "/cache/.disable_magisk", "/system/xbin/daemonsu",
    ];

    static readonly string[] IosPaths =
    [
        "/Applications/Cydia.app", "/Applications/Sileo.app", "/Applications/Zebra.app", "/Library/MobileSubstrate",
        "/usr/lib/libsubstrate.dylib", "/usr/lib/TweakInject", "/var/jb", "/var/lib/apt", "/var/lib/cydia", "/etc/apt",
        "/usr/libexec/cydia", "/.installed_unc0ver", "/.bootstrapped_electra", "/var/binpack",
    ];

    /// <summary>The traces of one platform (iOS: jailbreak; Android: root), or of both when it is not known.</summary>
    public static IndicatorSet Set(bool? ios = null) => IndicatorSet.From(
        (ios == true ? [] : AndroidApps).Concat(ios == false ? [] : IosApps).Select(a => new Indicator("app:id", a, Collection, "built-in"))
            .Concat((ios == true ? [] : AndroidPaths).Concat(ios == false ? [] : IosPaths).Select(p => new Indicator("file:path", p, Collection, "built-in"))));
}
