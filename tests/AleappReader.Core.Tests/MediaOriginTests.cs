using AleappReader.Core;
using Xunit;

namespace AleappReader.Core.Tests;

public class MediaOriginTests
{
    [Theory]
    [InlineData("/data/media/0/DCIM/Camera/IMG_1.jpg", true)]
    [InlineData("/storage/emulated/0/Pictures/Screenshots/s.png", true)]
    [InlineData("/data/media/0/WhatsApp/Media/WhatsApp Images/a.jpg", true)]
    [InlineData("/data/media/0/Android/media/com.whatsapp/WhatsApp/Media/a.jpg", false)]
    [InlineData("/data/media/0/DCIM/.thumbnails/1.jpg", false)]
    [InlineData("/private/var/mobile/Media/DCIM/100APPLE/IMG_0001.HEIC", true)]
    [InlineData("/data/data/com.whatsapp/files/a.jpg", false)]
    public void Gallery_is_shared_storage_outside_android_and_hidden_folders(string path, bool expected) =>
        Assert.Equal(expected, MediaOrigin.IsGallery(path));

    [Theory]
    [InlineData("/data/data/com.facebook.orca/cache/image/x.jpg", true)]
    [InlineData("/private/var/mobile/Containers/Data/Application/ABC/Library/Caches/x.png", true)]
    [InlineData("/data/media/0/DCIM/.thumbnails/1.jpg", true)]
    [InlineData("/data/media/0/DCIM/Camera/IMG_1.jpg", false)]
    public void Cache_folders_are_recognised(string path, bool expected) => Assert.Equal(expected, MediaOrigin.IsCache(path));

    [Theory]
    [InlineData("/system/media/audio/ringtones/a.ogg", true)]
    [InlineData("/private/var/containers/Bundle/Application/X/App.app/icon.png", true)]
    [InlineData("/data/media/0/Music/a.mp3", false)]
    public void System_partitions_and_app_packages_are_system(string path, bool expected) => Assert.Equal(expected, MediaOrigin.IsSystem(path));

    [Theory]
    [InlineData("/data/data/com.whatsapp/files/a.jpg", "com.whatsapp", "WhatsApp")]
    [InlineData("/data/user/10/org.telegram.messenger/cache/a.jpg", "org.telegram.messenger", "Telegram")]
    [InlineData("/data/media/0/Android/data/com.example.unknown/files/a.jpg", "com.example.unknown", null)]
    [InlineData("/data/media/0/DCIM/Camera/a.jpg", null, null)]
    public void Package_and_app_name_come_from_the_data_folder(string path, string? package, string? app)
    {
        Assert.Equal(package, MediaOrigin.Package(path));
        Assert.Equal(app, MediaOrigin.AppName(MediaOrigin.Package(path)));
    }

    [Theory]
    [InlineData("WhatsApp - Messages", "WhatsApp")]
    [InlineData("WhatsApp Business Messages", "WhatsApp Business")]
    [InlineData("Instagramish", null)]
    public void Artifact_names_starting_with_an_app_name_are_that_app(string name, string? app) => Assert.Equal(app, MediaOrigin.AppName(name));

    [Theory]
    [InlineData("/data/media/0/DCIM/Camera/IMG_20240101_120000.jpg", Capture.Camera)]
    [InlineData("/storage/emulated/0/DCIM/100ANDRO/DSC_0001.JPG", Capture.Camera)]
    [InlineData("/data/media/0/DCIM/Screenshots/Screenshot_20240101-120000.png", Capture.Screenshot)]
    [InlineData("/data/media/0/Pictures/Screenshots/a.png", Capture.Screenshot)]
    [InlineData("/data/media/0/DCIM/Screen recordings/a.mp4", Capture.Screenshot)]
    [InlineData("/data/media/0/Movies/Screen_Recording_20240101.mp4", Capture.Screenshot)]
    [InlineData("/data/media/0/DCIM/Camera/.thumbnails/a.jpg", Capture.None)]
    [InlineData("/data/media/0/WhatsApp/Media/WhatsApp Images/IMG-20240101-WA0001.jpg", Capture.None)]
    [InlineData("/data/media/0/Download/photo.jpg", Capture.None)]
    [InlineData("/data/data/com.example/files/Screenshot_1.png", Capture.None)]
    [InlineData("/private/var/mobile/Media/DCIM/100APPLE/IMG_0001.HEIC", Capture.None)] // iOS: only the Photos library says
    public void Android_camera_and_screenshots_come_from_the_folder_and_the_name(string path, Capture expected) =>
        Assert.Equal(expected, MediaOrigin.CaptureOf(path));

    [Fact]
    public void Ios_camera_and_screenshots_come_from_the_photos_library()
    {
        using var con = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        con.Open();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE ph (dir TEXT, file TEXT, sub TEXT, shot TEXT, imp TEXT);
                INSERT INTO ph VALUES ('DCIM/100APPLE', 'IMG_0001.HEIC', '0-Still-Photo-0', '0-Not_Screenshot-0', '1-Native-Back-Camera-1'),
                  ('DCIM/100APPLE', 'IMG_0002.HEIC', '2-Live-Photo-2', NULL, '2-Native-Front-Camera-2'),
                  ('DCIM/100APPLE', 'IMG_0003.PNG', '10-SpringBoard-Screenshot-10', '1-Screenshot-1', '9-Native-App-9'),
                  ('DCIM/100APPLE', 'IMG_0004.MP4', '103-Replay_Screen_Recording-103', NULL, NULL),
                  ('DCIM/100APPLE', 'IMG_0005.JPG', '0-Still-Photo-0', '0-Not_Screenshot-0', '3-Third-Party-App-3'),
                  ('DCIM/100APPLE', 'IMG_0006.JPG', NULL, NULL, 'Unknown-New-Value!: 13');
                """;
            cmd.ExecuteNonQuery();
        }
        var art = new ArtifactInfo
        {
            Category = "Photos", Name = "Ph050", TableName = "ph", Columns =
            [
                new("dir", "zAsset-Directory-Path-35", null), new("file", "zAsset-Filename-36", null), new("sub", "zAsset-Kind-Sub-Type-95", null),
                new("shot", "zAsset-Is_Detected_Screenshot-iOS18-94", null), new("imp", "zAddAssetAttr-Imported by-33", null),
            ],
        };
        var photos = MediaOrigin.Photos(con, [art]);
        Capture Of(string f) => MediaOrigin.CaptureOf("/private/var/mobile/Media/DCIM/100APPLE/" + f, photos);
        Assert.Equal([Capture.Camera, Capture.Camera, Capture.Screenshot, Capture.Screenshot, Capture.None, Capture.None, Capture.None],
            new[] { "IMG_0001.HEIC", "img_0002.heic", "IMG_0003.PNG", "IMG_0004.MP4", "IMG_0005.JPG", "IMG_0006.JPG", "IMG_0099.JPG" }.Select(Of));
        Assert.Empty(MediaOrigin.Photos(con, [new ArtifactInfo { Category = "x", Name = "x", TableName = "ph", Columns = [new("dir", "Directory", null)] }]));
    }
}
