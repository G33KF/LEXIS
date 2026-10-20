using System.IO.Compression;

namespace AleappReader.Core.Tests;

public class KeychainDiscoveryTests
{
    const string Udid = "00008110-001A2B3C4D5E6F70";

    [Fact]
    public void A_keychain_beside_the_extraction_is_found_preferring_the_one_naming_the_device()
    {
        var root = Directory.CreateTempSubdirectory("kc").FullName;
        try
        {
            var fs = Directory.CreateDirectory(Path.Combine(root, Udid, "fs")).FullName;
            var zip = Path.Combine(fs, "full.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
                z.CreateEntry("extra/KeychainDump/backup_keychain_v2.plist");
            Assert.Null(ExtractionRunner.KeychainBeside(zip));
            Assert.Equal("extra/KeychainDump/backup_keychain_v2.plist", ExtractionRunner.KeychainInside(zip));

            var other = Path.Combine(root, "AAAAAAAA-BBBBBBBBBBBBBBBB_keychain.plist");
            File.WriteAllText(other, "");
            Assert.Equal(other, ExtractionRunner.KeychainBeside(zip)); // the only one
            var mine = Path.Combine(root, Udid, Udid + "_keychain.plist");
            File.WriteAllText(mine, "");
            Assert.Equal(mine, ExtractionRunner.KeychainBeside(zip)); // the device's, among two
            File.Delete(mine);
            File.WriteAllText(Path.Combine(root, "x_keychain.plist"), "");
            Assert.Null(ExtractionRunner.KeychainBeside(zip)); // two, neither the device's: not guessed
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_android_keystore_export_beside_the_extraction_is_found_and_a_keychain_is_not_taken_for_one()
    {
        var root = Directory.CreateTempSubdirectory("ks").FullName;
        try
        {
            var zip = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "fs")).FullName, "full.zip");
            File.WriteAllText(zip, "");
            File.WriteAllText(Path.Combine(root, "x_keychain.plist"), "");
            Assert.Null(ExtractionRunner.KeychainBeside(zip, ExtractionRunner.KeystorePatterns));
            var keystore = Path.Combine(root, "SERIAL_android_keystore.zip");
            File.WriteAllText(keystore, "");
            Assert.Equal(keystore, ExtractionRunner.KeychainBeside(zip, ExtractionRunner.KeystorePatterns));
            Assert.Equal(Path.Combine(root, "x_keychain.plist"), ExtractionRunner.KeychainBeside(zip));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_keychain_lines_of_the_ileapp_log_are_read_back()
    {
        var root = Directory.CreateTempSubdirectory("kc").FullName;
        try
        {
            Assert.Null(ExtractionRunner.KeychainLog(root));
            var logs = Directory.CreateDirectory(Path.Combine(root, "_HTML", "_Script_Logs")).FullName;
            File.WriteAllText(Path.Combine(logs, "Screen_Output.html"),
                "Processing started<br>\nKeychain: read 3 entries from C:\\k &amp; c.plist<br>\nFind My safe locations: no BeaconStore key in a keychain, records not decrypted<br>\n");
            Assert.Equal(["Keychain: read 3 entries from C:\\k & c.plist", "Find My safe locations: no BeaconStore key in a keychain, records not decrypted"],
                ExtractionRunner.KeychainLog(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
