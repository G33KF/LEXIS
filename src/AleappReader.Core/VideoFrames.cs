using System.Globalization;
using System.Text.RegularExpressions;

namespace AleappReader.Core;

/// <summary>The frames of a video the AI is shown: when they are taken, and their extraction with ffmpeg as JPEGs.</summary>
public static partial class VideoFrames
{
    /// <summary>The middle of each stretch: every <paramref name="percent"/> % of the duration (10 → 5 %, 15 % … 95 %), or
    /// every <paramref name="seconds"/> s; at most <paramref name="max"/>, evenly picked (<c>Capped</c>); a video shorter
    /// than one stretch gives its middle frame.</summary>
    public static (IReadOnlyList<TimeSpan> Times, bool Capped) Times(TimeSpan duration, int? percent, int? seconds, int max)
    {
        var d = duration.TotalSeconds;
        var step = percent is > 0 and <= 100 ? d * percent.Value / 100 : Math.Max(1, seconds ?? 5);
        if (d < step) return ([duration / 2], false);
        var n = (int)Math.Floor(d / step + 1e-9);
        var all = Enumerable.Range(0, n).Select(i => TimeSpan.FromSeconds(step * (i + 0.5))).Where(t => t.TotalSeconds < d)
            .DefaultIfEmpty(duration / 2).ToList();
        max = Math.Max(1, max);
        if (all.Count <= max) return (all, false);
        return ([.. Enumerable.Range(0, max).Select(i => all[(int)((i + 0.5) * all.Count / max)])], true);
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex Dur();

    /// <summary>The duration ffmpeg prints for its input ("Duration: 00:01:02.50"), null when it prints none.</summary>
    public static TimeSpan? Duration(string ffmpegOutput) => Dur().Match(ffmpegOutput) is { Success: true } m
        ? TimeSpan.FromHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
          + TimeSpan.FromSeconds(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))
        : null;

    /// <summary>The duration of <paramref name="video"/>, read by ffmpeg (which ends with an error without an output: what it prints is what counts).</summary>
    public static async Task<TimeSpan?> DurationOf(string ffmpeg, string video, CancellationToken ct) =>
        Duration(await WhisperCpp.Run(ffmpeg, ["-nostdin", "-hide_banner", "-i", video], Path.GetTempPath(), WhisperCpp.FfmpegLimit, ct));

    /// <summary>One JPEG per time, at most 1280 px wide, in <paramref name="folder"/>; a frame ffmpeg cannot write is left out.</summary>
    public static async Task<IReadOnlyList<(TimeSpan At, string File)>> Extract(string ffmpeg, string video, IReadOnlyList<TimeSpan> times,
                                                                                string folder, CancellationToken ct)
    {
        var got = new List<(TimeSpan, string)>();
        for (var i = 0; i < times.Count; i++)
        {
            var file = Path.Combine(folder, $"frame_{i:D3}.jpg");
            try
            {
                await WhisperCpp.Exec(ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", times[i].TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", video,
                    "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "3", file], folder, "ffmpeg", WhisperCpp.FfmpegLimit, ct);
            }
            catch (AiException) { }
            if (File.Exists(file)) got.Add((times[i], file));
        }
        return got;
    }

    /// <summary>The request for one frame: what is visible, any text read exactly.</summary>
    public static string Prompt(TimeSpan at, string language) =>
        $"This is the frame at {Clock(at)} of a video from a forensic extraction. Describe in {language}, in one short paragraph, " +
        "what is visible: people, objects, places, actions; read any text exactly as written. Only what is visible, no guesses.";

    /// <summary>What was seen and said, in time order, one line each: "mm:ss — …" for a frame, "mm:ss 🗣 "…"" for speech.</summary>
    public static string Timeline(IEnumerable<(TimeSpan At, string Said)> frames, IEnumerable<(TimeSpan At, string Text)> speech) =>
        string.Join("\n", frames.Select(f => (f.At, Order: 0, Line: $"{Clock(f.At)} — {f.Said.ReplaceLineEndings(" ").Trim()}"))
            .Concat(speech.Select(s => (s.At, Order: 1, Line: $"{Clock(s.At)} 🗣 \"{s.Text}\"")))
            .OrderBy(x => x.At).ThenBy(x => x.Order).Select(x => x.Line));

    /// <summary>"mm:ss" (or "h:mm:ss") of a time in a video.</summary>
    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
