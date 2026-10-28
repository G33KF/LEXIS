using AleappReader.Core;

namespace AleappReader.Core.Tests;

public class OcrTests
{
    // "Forensic OCR test 2026" in Arial 28, black on white, drawn with System.Drawing: synthetic, no case data
    static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-sample.png");

    [Fact]
    public void Tesseract_reads_the_text_of_an_image_offline()
    {
        using var ocr = new TesseractOcr(["eng", "ita"]);
        Assert.Equal("OCR · Tesseract 5 · eng+ita", ocr.Method);
        Assert.Contains("Forensic OCR test 2026", ocr.Read(File.ReadAllBytes(Sample)));
    }

    [Fact]
    public async Task A_scanned_pdf_page_is_read_from_its_image_and_kept_beside_the_text_pages()
    {
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        b.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Typed page", 12, new(50, 700), font);
        b.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddPng(File.ReadAllBytes(Sample), new(50, 600, 570, 690));
        b.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddPng(File.ReadAllBytes(Sample), new(50, 600, 570, 690));
        var pdf = b.Build();
        var store = new TextStore(Path.Combine(Path.GetTempPath(), $"ocr-{Guid.NewGuid():N}.db"));
        using var ocr = new TesseractOcr(["eng"]);

        var f = await OcrIndex.Read(store, "sdcard/scan.pdf", pdf, x => Task.FromResult(ocr.Read(x)), ocr.Method, maxPages: 1);

        Assert.StartsWith(TextStore.HasText + "; 1 scanned pages over the limit of 1", f.Status);
        Assert.EndsWith("+ OCR · Tesseract 5 · eng", f.Method);
        var parts = store.Parts("sdcard/scan.pdf");
        Assert.Equal(["page 1", "page 2"], parts.Select(p => p.Part));
        Assert.Contains("Typed page", parts[0].Text);
        Assert.Contains("Forensic OCR test 2026", parts[1].Text);
    }

    [Fact]
    public async Task OCR_leaves_the_text_already_read_of_a_file_that_is_not_a_picture_or_a_pdf()
    {
        var store = new TextStore(Path.Combine(Path.GetTempPath(), $"ocr-{Guid.NewGuid():N}.db"));
        var note = "meeting at noon"u8.ToArray();
        store.Save(new TextFile("sdcard/note.txt", note.Length, "", RawFiles.PlainText, "Text", TextStore.HasText, 0, "text"), [("", "meeting at noon")]);

        var f = await OcrIndex.Read(store, "sdcard/note.txt", note, _ => Task.FromResult<string?>("never asked"), "OCR · test", 10);

        Assert.StartsWith(TextStore.NotSupported, f.Status);
        Assert.Equal("meeting at noon", Assert.Single(store.Parts("sdcard/note.txt")).Text);
    }

    [Fact]
    public void An_image_it_cannot_decode_gives_no_text_rather_than_an_empty_one()
    {
        using var ocr = new TesseractOcr(["eng"]);
        Assert.Null(ocr.Read("not an image"u8.ToArray()));
    }
}
