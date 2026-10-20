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
}
