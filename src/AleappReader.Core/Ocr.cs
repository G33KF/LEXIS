using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UglyToad.PdfPig;

namespace AleappReader.Core;

/// <summary>
/// Offline OCR with Tesseract 5 (tesseract50.dll, Leptonica, beside LEXIS) and the tessdata_fast 4.1.0 models embedded in
/// LEXIS, written once to %LOCALAPPDATA%\AleappReader\tessdata-4.1.0\tessdata. One instance loads its languages once and
/// reads image after image; it is not shared between threads.
/// </summary>
public sealed class TesseractOcr : IDisposable
{
    /// <summary>The languages carried (tessdata_fast codes).</summary>
    public static readonly IReadOnlyList<string> Languages = ["eng", "ita", "chi_sim", "chi_tra", "ara", "rus", "sqi", "fra", "deu", "spa", "por", "ron"];
    public const string Engine = "Tesseract 5";

    const string Tess = "tesseract50", Lept = "leptonica-1.82.0";
    [DllImport(Tess, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr TessBaseAPICreate();
    // ponytail: the data path goes as ANSI (Tesseract opens it with fopen); a profile folder outside the code page fails Init.
    [DllImport(Tess, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    static extern int TessBaseAPIInit3(IntPtr api, string datapath, string language);
    [DllImport(Tess, CallingConvention = CallingConvention.Cdecl)] static extern void TessBaseAPISetImage2(IntPtr api, IntPtr pix);
    [DllImport(Tess, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr TessBaseAPIGetUTF8Text(IntPtr api);
    [DllImport(Tess, CallingConvention = CallingConvention.Cdecl)] static extern void TessDeleteText(IntPtr text);
    [DllImport(Tess, CallingConvention = CallingConvention.Cdecl)] static extern void TessBaseAPIDelete(IntPtr api);
    [DllImport(Lept, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr pixReadMem(byte[] data, UIntPtr size);
    [DllImport(Lept, CallingConvention = CallingConvention.Cdecl)]
    static extern int pixReadHeaderMem(byte[] data, UIntPtr size, out int format, out int w, out int h, out int bps, out int spp, out int iscmap);
    [DllImport(Lept, CallingConvention = CallingConvention.Cdecl)] static extern void pixDestroy(ref IntPtr pix);

    // the Visual C++ runtime beside Tesseract is loaded first, by name, so a PC without it installed still runs Tesseract
    static TesseractOcr() => NativeLibrary.SetDllImportResolver(typeof(TesseractOcr).Assembly, (name, asm, path) =>
    {
        if (name is not (Tess or Lept)) return IntPtr.Zero;
        foreach (var dep in new[] { "vcruntime140", "vcruntime140_1", "msvcp140", Lept }) NativeLibrary.TryLoad(dep, asm, path, out _);
        return NativeLibrary.TryLoad(name, asm, path, out var h) ? h : IntPtr.Zero;
    });

    IntPtr _api;
    public string Method { get; }

    /// <summary>Loads the languages (e.g. eng, ita). Throws when the models cannot be written or Tesseract cannot load them.</summary>
    public TesseractOcr(IReadOnlyCollection<string> languages)
    {
        var langs = languages.Where(Languages.Contains).ToList();
        if (langs.Count == 0) langs = ["eng"];
        var data = DataPath.Value ?? throw new IOException("The OCR models cannot be written to %LOCALAPPDATA%\\AleappReader.");
        var join = string.Join('+', langs);
        _api = TessBaseAPICreate();
        if (TessBaseAPIInit3(_api, data, join) != 0)
        {
            Dispose();
            throw new InvalidOperationException($"Tesseract could not load the languages {join} from {data}.");
        }
        Method = $"OCR · {Engine} · {join}";
    }

    /// <summary>The text in an image (JPEG, PNG, TIFF, GIF, WebP, BMP); null when Leptonica cannot decode it, or its header
    /// declares more than <see cref="OcrIndex.MaxPixels"/> (a small file can declare a huge picture): read before decoding.</summary>
    public string? Read(byte[] image)
    {
        if (pixReadHeaderMem(image, (UIntPtr)image.Length, out _, out var w, out var h, out _, out _, out _) != 0
            || w <= 0 || h <= 0 || (long)w * h > OcrIndex.MaxPixels) return null;
        var pix = pixReadMem(image, (UIntPtr)image.Length);
        if (pix == IntPtr.Zero) return null;
        try
        {
            TessBaseAPISetImage2(_api, pix);
            var t = TessBaseAPIGetUTF8Text(_api);
            try { return Marshal.PtrToStringUTF8(t) ?? ""; }
            finally { if (t != IntPtr.Zero) TessDeleteText(t); }
        }
        finally { pixDestroy(ref pix); }
    }

    public void Dispose()
    {
        if (_api != IntPtr.Zero) TessBaseAPIDelete(_api);
        _api = IntPtr.Zero;
    }

    /// <summary>The folder of the models, written out of LEXIS on first use; each model rewritten when its SHA-256 differs.</summary>
    static readonly Lazy<string?> DataPath = new(() =>
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AleappReader", "tessdata-4.1.0", "tessdata");
        try
        {
            Directory.CreateDirectory(dir);
            var asm = typeof(TesseractOcr).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("tessdata/", StringComparison.Ordinal)))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                var bytes = new byte[s.Length];
                s.ReadExactly(bytes);
                var path = Path.Combine(dir, name["tessdata/".Length..]);
                if (File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) continue;
                var temp = path + $".{Environment.ProcessId}.tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, true);
            }
            return dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    });
}

/// <summary>OCR of files of the extraction into the <see cref="TextStore"/>, whatever engine reads the images.</summary>
public static class OcrIndex
{
    /// <summary>The largest picture read, in pixels (a page scanned at 600 dpi is about 35 million).</summary>
    public const long MaxPixels = 80_000_000;

    /// <summary>Reads one file and records it (anything but a picture or a PDF is left as it was): an image whole; a PDF's scanned pages, from their images, at most
    /// <paramref name="maxPages"/>, its text pages kept as they were. <paramref name="read"/> gives an image's text, or null
    /// when it cannot decode it; <paramref name="method"/> says which engine and languages.</summary>
    public static async Task<TextFile> Read(TextStore store, string rel, byte[] bytes, Func<byte[], Task<string?>> read, string method,
                                            int maxPages, CancellationToken ct = default)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var type = RawFiles.Sniff(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        var kind = RawFiles.Category(rel, type);
        if (type != "PDF document" && kind != RawFiles.Pictures)
            return new TextFile(rel, bytes.Length, sha, kind, type, TextStore.NotSupported + ": OCR reads pictures and PDFs", 0, ""); // its text, if any, stays as read
        if (type != "PDF document")
        {
            var text = await read(bytes);
            var f = new TextFile(rel, bytes.Length, sha, kind, type, text is null ? TextStore.Unreadable + ": not an image the OCR can decode"
                : text.Trim() == "" ? TextStore.NoText : TextStore.HasText, 0, method);
            store.Save(f, text is { } t && t.Trim() != "" ? [("", t)] : []);
            return f;
        }
        var (_, pages, pdfMethod, parts) = ContentIndex.Read(rel, type, kind, bytes);
        int done = 0, left = 0;
        using (var doc = PdfDocument.Open(bytes))
            foreach (var page in doc.GetPages())
            {
                if (page.Text.Trim() != "" || page.GetImages().ToList() is not { Count: > 0 } images) continue;
                if (done++ >= maxPages) { left++; continue; }
                ct.ThrowIfCancellationRequested();
                // one image decoded at a time, when it is read; one declaring more pixels than MaxPixels is not decoded.
                // A JPEG stays in the PDF as it was (DCT): its raw bytes are the JPEG file; the other images are decoded to PNG
                List<string> texts = [];
                foreach (var i in images.Where(i => (long)i.WidthInSamples * i.HeightInSamples is > 0 and <= MaxPixels))
                    if (await read(i.TryGetPng(out var png) ? png : i.RawBytes.ToArray()) is { } t && t.Trim() != "") texts.Add(t);
                if (texts.Count > 0) parts.Add(($"page {page.Number}", string.Join("\n", texts)));
            }
        parts.Sort((a, b) => PageOf(a.Part).CompareTo(PageOf(b.Part)));
        var status = parts.Count == 0 ? TextStore.NoText : TextStore.HasText;
        if (left > 0) status += $"; {left} scanned pages over the limit of {maxPages} not read";
        var pdf = new TextFile(rel, bytes.Length, sha, kind, type, status, pages, done == 0 ? pdfMethod : $"{pdfMethod} + {method}");
        store.Save(pdf, parts);
        return pdf;
    }

    static int PageOf(string part) => int.TryParse(part.AsSpan(part.LastIndexOf(' ') + 1), out var n) ? n : 0;
}
