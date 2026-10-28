using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class VideoFramesTests
{
    static double[] Secs(IReadOnlyList<TimeSpan> t) => [.. t.Select(x => Math.Round(x.TotalSeconds, 3))];

    [Fact]
    public void Every_ten_percent_takes_the_middle_of_each_stretch()
    {
        var (t, capped) = VideoFrames.Times(TimeSpan.FromSeconds(100), 10, null, 30);
        Assert.Equal([5, 15, 25, 35, 45, 55, 65, 75, 85, 95], Secs(t));
        Assert.False(capped);
    }

    [Fact]
    public void Every_n_seconds_and_the_cap()
    {
        Assert.Equal([15, 45, 75], Secs(VideoFrames.Times(TimeSpan.FromSeconds(100), null, 30, 30).Times));
        var (t, capped) = VideoFrames.Times(TimeSpan.FromSeconds(1000), null, 2, 30);
        Assert.Equal(30, t.Count);
        Assert.True(capped);
        Assert.Equal(t.OrderBy(x => x), t);
        Assert.Equal([1.5], Secs(VideoFrames.Times(TimeSpan.FromSeconds(3), null, 5, 30).Times));
    }

    [Fact]
    public void The_duration_is_read_from_what_ffmpeg_prints()
    {
        Assert.Equal(TimeSpan.FromSeconds(62.5), VideoFrames.Duration("  Duration: 00:01:02.50, start: 0.000000"));
        Assert.Null(VideoFrames.Duration("Invalid data found when processing input"));
    }

    /// <summary>The bundled ffmpeg on a synthetic H.264 + AAC clip of 4 s (FFmpeg's testsrc and sine): duration, 10 frames, the audio track.</summary>
    [Fact]
    public async Task The_bundled_ffmpeg_reads_an_h264_video()
    {
        var ff = WhisperCpp.BundledFfmpeg!;
        var video = Path.Combine(AppContext.BaseDirectory, "Fixtures", "testsrc-4s.mp4");
        var d = await VideoFrames.DurationOf(ff, video, default);
        Assert.Equal(TimeSpan.FromSeconds(4), d);
        var dir = Directory.CreateTempSubdirectory("lexis-frames-");
        try
        {
            var frames = await VideoFrames.Extract(ff, video, VideoFrames.Times(d!.Value, 10, null, 30).Times, dir.FullName, default);
            Assert.Equal(10, frames.Count);
            Assert.All(frames, f => Assert.Equal([0xFF, 0xD8], File.ReadAllBytes(f.File)[..2]));
            var wav = Path.Combine(dir.FullName, "a.wav");
            await WhisperCpp.ToWav(ff, video, wav, default);
            Assert.True(new FileInfo(wav).Length > 100_000);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_timeline_puts_frames_and_speech_in_time_order()
    {
        var s = VideoFrames.Timeline([(TimeSpan.FromSeconds(15), "a car\nat night"), (TimeSpan.FromSeconds(5), "a street")],
                                     [(TimeSpan.FromSeconds(7), "hello")]);
        Assert.Equal("00:05 — a street\n00:07 🗣 \"hello\"\n00:15 — a car at night", s);
    }
}
