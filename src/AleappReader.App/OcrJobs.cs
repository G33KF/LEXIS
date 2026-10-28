using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Controls;
using AleappReader.App.ViewModels;
using AleappReader.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AleappReader.App;

/// <summary>
/// OCR on request, by the engine the examiner chooses each time, both offline: Tesseract (carried by LEXIS) or the OCR
/// built into Windows. The AI's "Describe and read the text" is the third way, in the AI menu. The text goes into the
/// case's text base: Search finds it, and the file's details show it.
/// </summary>
public static class OcrJobs
{
    public enum Engine { Tesseract, Windows }

    static OcrSettings O => Settings.Current.Ocr;

    /// <summary>Windows OCR in the language set, else the first of the user's languages Windows can read; null when none is installed.</summary>
    static OcrEngine? WindowsEngine() => O.WindowsLanguage is { Length: > 0 } tag && OcrEngine.IsLanguageSupported(new Language(tag))
        ? OcrEngine.TryCreateFromLanguage(new Language(tag)) : OcrEngine.TryCreateFromUserProfileLanguages();

    /// <summary>The languages Windows can read here (its OCR language packs).</summary>
    public static IReadOnlyList<string> WindowsLanguages => [.. OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag)];

    public static string TesseractLabel => $"Tesseract, in LEXIS ({string.Join("+", O.Languages)})";
    /// <summary>"Windows OCR (it-IT)"; null when Windows has no OCR language installed.</summary>
    public static string? WindowsLabel => WindowsEngine() is { } e ? $"Windows OCR ({e.RecognizerLanguage.LanguageTag})" : null;

    /// <summary>The engine a menu parameter names (Tesseract when it names none).</summary>
    public static Engine Of(object? p) => p is Engine e ? e : Enum.TryParse<Engine>(p as string, out var x) ? x : Engine.Tesseract;

    /// <summary>"Read the text (OCR) ▸" with both engines; Windows OCR disabled, saying why, when Windows has no OCR language.</summary>
    public static MenuItem Menu(Action<Engine> run)
    {
        var m = new MenuItem { Header = "Read the text (OCR, offline)" };
        var t = new MenuItem { Header = TesseractLabel };
        t.Click += (_, _) => run(Engine.Tesseract);
        m.Items.Add(t);
        var label = WindowsLabel;
        var w = new MenuItem { Header = label ?? "Windows OCR (no OCR language installed in Windows)", IsEnabled = label is not null };
        w.Click += (_, _) => run(Engine.Windows);
        m.Items.Add(w);
        return m;
    }

    /// <summary>Reads the files into the text base in the background, each under its key (its path in the extraction);
    /// <c>File</c> gives a readable copy, null when it cannot be read. The status bar says how far and then what came of it.</summary>
    public static async Task Run(MainViewModel main, CaseSession s, IReadOnlyList<(string Key, Func<string?> File)> files, Engine engine)
    {
        if (files.Count == 0) return;
        int read = 0, text = 0;
        List<string> skipped = [];
        var store = TextStore.For(s);
        var (maxBytes, maxPages) = (O.MaxMegabytes * (1L << 20), O.MaxPages);
        string method = "";
        main.Status = $"OCR of {files.Count:N0} file(s)…";
        IProgress<int> progress = new Progress<int>(n => main.Status = $"OCR: {n:N0} of {files.Count:N0} files read…");
        try
        {
            await Task.Run(async () =>
            {
                using var tess = engine == Engine.Tesseract ? new TesseractOcr(O.Languages) : null;
                var win = engine == Engine.Windows ? WindowsEngine() ?? throw new InvalidOperationException(
                    "Windows has no OCR language installed (Settings › Time & language › Language: a language's optical character recognition).") : null;
                Func<byte[], Task<string?>> reader = tess is not null ? b => Task.FromResult(tess.Read(b)) : b => ReadWindows(win!, b);
                method = tess?.Method ?? $"OCR · Windows · {win!.RecognizerLanguage.LanguageTag}";
                foreach (var (key, file) in files)
                {
                    if (file() is not { } path) { skipped.Add($"{key}: cannot be read"); continue; }
                    if (new FileInfo(path).Length > maxBytes) { skipped.Add($"{key}: over {O.MaxMegabytes} MB"); continue; }
                    var f = await OcrIndex.Read(store, key, await File.ReadAllBytesAsync(path), reader, method, maxPages);
                    read++;
                    if (f.Status.StartsWith(TextStore.HasText, StringComparison.Ordinal)) text++;
                    else if (f.Status.StartsWith(TextStore.Unreadable, StringComparison.Ordinal)) skipped.Add($"{key}: {f.Status}");
                    progress.Report(read);
                }
            });
            main.Status = $"OCR ({method}): {read:N0} file(s) read, text found in {text:N0}" +
                          (skipped.Count > 0 ? $"; {skipped.Count:N0} left out — {string.Join("; ", skipped.Take(3))}{(skipped.Count > 3 ? "…" : "")}" : "") +
                          ". The text is in Search (file content) and in each file's details.";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or DllNotFoundException or UnauthorizedAccessException)
        {
            main.Status = "OCR stopped: " + e.Message;
        }
    }

    /// <summary>The text Windows OCR reads in an image; null when Windows cannot decode it. Larger than the OCR allows, it is scaled down.</summary>
    static async Task<string?> ReadWindows(OcrEngine engine, byte[] image)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(image.AsBuffer());
        stream.Seek(0);
        BitmapDecoder dec;
        try { dec = await BitmapDecoder.CreateAsync(stream); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; } // not an image Windows decodes
        var t = new BitmapTransform();
        var max = OcrEngine.MaxImageDimension;
        if (dec.PixelWidth > max || dec.PixelHeight > max)
        {
            var k = (double)max / Math.Max(dec.PixelWidth, dec.PixelHeight);
            (t.ScaledWidth, t.ScaledHeight) = ((uint)(dec.PixelWidth * k), (uint)(dec.PixelHeight * k));
        }
        using var bmp = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t,
                                                         ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var r = await engine.RecognizeAsync(bmp);
        return string.Join("\n", r.Lines.Select(l => l.Text));
    }
}
