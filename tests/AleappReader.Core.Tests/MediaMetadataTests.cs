using AleappReader.Core;
using Xunit;

namespace AleappReader.Core.Tests;

public class MediaMetadataTests
{
    /// <summary>A JPEG holding only an EXIF block with Make "Acme" and Model "X1".</summary>
    static byte[] Jpeg()
    {
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0, 2, 0 };
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        U16(0x010F); U16(2); U32(5); U32(38);                  // Make, ASCII, 5 bytes at offset 38
        U16(0x0110); U16(2); U32(3); tiff.AddRange("X1\0\0"u8.ToArray()); // Model, ASCII, inline
        U32(0);
        tiff.AddRange("Acme\0"u8.ToArray());
        var app1 = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var len = app1.Length + 2;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. app1, 0xFF, 0xD9];
    }

    [Fact]
    public void Exif_tags_are_read_with_a_summary_first()
    {
        var tags = MediaMetadata.Read(new MemoryStream(Jpeg()));
        Assert.Equal(new MediaMetadata.Tag("Summary", "Camera", "Acme X1"), tags[0]);
        Assert.Contains(new MediaMetadata.Tag("Exif IFD0", "Make", "Acme"), tags);
        Assert.Contains(new MediaMetadata.Tag("Exif IFD0", "Model", "X1"), tags);
        Assert.DoesNotContain(tags, t => t.Group == "File");
    }

    [Fact]
    public void Bytes_that_are_no_media_give_nothing() => Assert.Empty(MediaMetadata.Read(new MemoryStream("not a picture"u8.ToArray())));
}
