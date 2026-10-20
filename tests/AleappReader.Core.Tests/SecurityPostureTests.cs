using Microsoft.Data.Sqlite;

namespace AleappReader.Core.Tests;

public class SecurityPostureTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "AleappReaderTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void Dangerous_settings_and_the_patch_age_are_read_from_name_value_artifacts()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "_lava_data.lava"), """
            {"parser_info": {"leapp_name": "ALEAPP", "start_timestamp": 1714557600}, "processing_status": "Complete",
             "lava_db_name": "_lava_artifacts.db", "modules": [],
             "artifacts": {"Device Information": [
               {"name": "Settings Global", "tablename": "settingsglobal", "module": "settingsGlobalSystem",
                "column_map": {"user": "User", "name": "Name", "value": "Value"}, "record_count": 4},
               {"name": "Build Info", "tablename": "build", "module": "build",
                "column_map": {"key": "Key", "value": "Value"}, "record_count": 1}]}}
            """);
        using (var con = new SqliteConnection($"Data Source={Path.Combine(_root, "_lava_artifacts.db")};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settingsglobal (user TEXT, name TEXT, value TEXT);
                INSERT INTO settingsglobal VALUES ('0','adb_enabled','1'), ('0','package_verifier_enable','0'),
                                                  ('0','development_settings_enabled','0'), ('0','wifi_on','1');
                CREATE TABLE build (key TEXT, value TEXT);
                INSERT INTO build VALUES ('ro.build.version.security_patch','2022-02-05');
                """;
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_root);
        var hits = SecurityPosture.Check(s);
        Assert.Equal(["adb_enabled = 1", "package_verifier_enable = 0", "ro.build.version.security_patch = 2022-02-05"],
                     hits.Select(h => h.Cell).Order().ToArray());
        Assert.Equal("patch of 2022-02-05, 27 months older than the report",
                     hits.Single(h => h.Indicator.Type == "security patch level").Indicator.Value);
    }

    [Fact]
    public void On_iOS_the_version_age_and_passcode_changes_are_read_and_android_settings_are_not()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "_lava_data.lava"), """
            {"parser_info": {"leapp_name": "iLEAPP", "start_timestamp": 1714557600}, "processing_status": "Complete",
             "lava_db_name": "_lava_artifacts.db", "modules": [],
             "artifacts": {"IOS Build": [
               {"name": "iOS Information", "tablename": "last_build", "module": "lastBuild",
                "column_map": {"property": "Property", "property_value": "Property Value"}, "record_count": 3},
               {"name": "Lockdownd Events", "tablename": "lockdownevents", "module": "lockdownEvents",
                "column_map": {"timestamp": "Timestamp", "event": "Event"}, "record_count": 2}]}}
            """);
        using (var con = new SqliteConnection($"Data Source={Path.Combine(_root, "_lava_artifacts.db")};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE last_build (property TEXT, property_value TEXT);
                INSERT INTO last_build VALUES ('ProductVersion','15.4'), ('ProductName','iPhone OS'), ('adb_enabled','1');
                CREATE TABLE lockdownevents (timestamp TEXT, event TEXT);
                INSERT INTO lockdownevents VALUES ('01/02/24 10:00:00.000','Device passcode changed'), ('01/02/24 10:00:00.000','Lockdownd startup');
                """;
            cmd.ExecuteNonQuery();
        }
        using var s = CaseSession.Open(_root);
        Assert.True(s.Manifest.IsIos);
        var hits = SecurityPosture.Check(s);
        Assert.Equal(["iOS version", "passcode change"], hits.Select(h => h.Indicator.Type).Order().ToArray());
        Assert.Equal("iOS 15.4: its major release came out in September 2021, 2 year(s) before the report",
                     hits.Single(h => h.Indicator.Type == "iOS version").Indicator.Value);
    }

    [Theory]
    [InlineData("18.7", "September 2024")]
    [InlineData("26.1", "September 2025")]
    [InlineData("7.1.2", "September 2013")]
    public void Ios_major_releases_are_dated(string v, string when) => Assert.Contains(when, SecurityPosture.IosAge(v, null));

    [Fact]
    public void Root_traces_follow_the_platform()
    {
        string[] files = ["Dump/data/adb/magisk/busybox", "filesystem1/private/var/jb/usr/bin/x"];
        Assert.Equal(["filesystem1/private/var/jb"], RootTraces.Set(ios: true).ScanFiles(files).Select(h => h.Cell));
        Assert.Equal(["Dump/data/adb/magisk"], RootTraces.Set(ios: false).ScanFiles(files).Select(h => h.Cell));
        Assert.Equal(RootTraces.Set(ios: true).Count + RootTraces.Set(ios: false).Count, RootTraces.Set().Count);
    }
}
