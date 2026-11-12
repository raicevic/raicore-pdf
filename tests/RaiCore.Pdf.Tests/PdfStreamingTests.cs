using System.Text;
using System.Text.RegularExpressions;
using PDFtoImage;
using RaiCore.Pdf.Benchmarks;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Tests.Security;

namespace RaiCore.Pdf.Tests;

/// <summary>Writing a document page by page: <see cref="PdfDocument.BeginSave"/>, FlushPages and EndSave.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PdfStreamingTests
{
    private static readonly DateTimeOffset Date = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Writing_page_by_page_gives_the_same_file_as_Save()
    {
        var saved = Dated(EncryptionSamples.CreateRichSample(null)).ToArray();

        var document = Dated(EncryptionSamples.CreateRichSample(null));
        using var output = new MemoryStream();
        document.BeginSave(output);
        Assert.True(document.IsSaving);
        document.FlushPages();
        document.EndSave();

        Assert.False(document.IsSaving);
        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public void A_flow_document_rendered_page_by_page_is_the_same_as_rendered_and_saved()
    {
        var fonts = BundledFonts.CreateCollection();
        var saved = Dated(Documents.ReportFlow(fonts, chapters: 3).Render()).ToArray();

        var document = Dated(new PdfDocument());
        using var output = new MemoryStream();
        document.BeginSave(output);
        Documents.ReportFlow(fonts, chapters: 3).RenderTo(document);
        // Every page's content stream is written; the fonts, page dictionaries and outline are not yet.
        Assert.Equal(document.Pages.Count, Regex.Matches(Encoding.Latin1.GetString(output.ToArray()), "endobj").Count);
        document.EndSave();

        Assert.True(document.Pages.Count > 5);
        Assert.Equal(3, document.Outline.Count);
        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public void Flushed_pages_are_in_the_output_and_cannot_be_drawn_on()
    {
        var document = new PdfDocument { CompressContent = false };
        using var output = new MemoryStream();
        document.BeginSave(output);
        var first = document.AddPage(new PdfPageSize(200, 100));
        first.Canvas.Rectangle(10, 10, 20, 20).Fill();
        document.FlushPages();

        var written = Encoding.Latin1.GetString(output.ToArray());
        Assert.StartsWith("%PDF-1.7", written, StringComparison.Ordinal);
        Assert.Contains("10 10 20 20 re\nf\n", written, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => first.Canvas.Rectangle(0, 0, 1, 1));

        document.FlushPages(); // Nothing new: nothing is written twice.
        Assert.Equal(written.Length, output.Length);

        var second = document.AddPage(new PdfPageSize(200, 100));
        second.Canvas.Rectangle(30, 30, 5, 5).Fill();
        document.EndSave();

        var pdf = Encoding.Latin1.GetString(output.ToArray());
        Assert.Single(Regex.Matches(pdf, "10 10 20 20 re"));
        Assert.Contains("30 30 5 5 re", pdf, StringComparison.Ordinal);
        Assert.Equal(2, Conversion.GetPageCount(output.ToArray()));
        Assert.Matches(@"/Type /Pages/Kids \[\d+ 0 R \d+ 0 R\]/Count 2", pdf);
    }

    [Fact]
    public void Links_and_destinations_may_refer_to_pages_written_before_or_after()
    {
        var document = new PdfDocument { CompressContent = false };
        using var output = new MemoryStream();
        document.BeginSave(output);
        var first = document.AddPage(new PdfPageSize(200, 100));
        document.FlushPages();
        var second = document.AddPage(new PdfPageSize(200, 100));
        document.FlushPages();

        first.AddLink(0, 0, 50, 50, "later"); // On a written page, to a page written after it.
        second.AddLink(0, 0, 50, 50, "earlier");
        document.NamedDestinations["later"] = new PdfDestination(second, top: 80);
        document.NamedDestinations["earlier"] = new PdfDestination(first, top: 20);
        document.Outline.Add(new PdfOutlineItem("First", first));
        document.EndSave();

        var pdf = Encoding.Latin1.GetString(output.ToArray());
        var pages = Regex.Match(pdf, @"/Kids \[(\d+) 0 R (\d+) 0 R\]");
        var (firstRef, secondRef) = (pages.Groups[1].Value, pages.Groups[2].Value);
        Assert.Contains($"(earlier) [{firstRef} 0 R /XYZ null 20 null]", pdf, StringComparison.Ordinal);
        Assert.Contains($"(later) [{secondRef} 0 R /XYZ null 80 null]", pdf, StringComparison.Ordinal);
        Assert.Matches($@"\n{firstRef} 0 obj\n<</Type /Page.*/Annots \[\d+ 0 R\]>>", pdf);
        Assert.Contains("/Outlines", pdf, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    public void Encrypted_documents_written_page_by_page_render_like_the_plain_file(PdfEncryptionAlgorithm algorithm)
    {
        var plain = EncryptionSamples.CreateRichSample(null).ToArray();
        var document = EncryptionSamples.CreateRichSample(new PdfEncryption { UserPassword = "user", OwnerPassword = "owner", Algorithm = algorithm });
        using var output = new MemoryStream();
        document.BeginSave(output);
        document.FlushPages();
        document.EndSave();

        AssertSameRendering(plain, output.ToArray(), "user");
    }

    [Fact]
    public void Imported_pages_are_written_at_the_end()
    {
        var source = PdfReader.Open(PdfDocumentTests.CreateSample().ToArray());
        PdfDocument Create()
        {
            var document = Dated(new PdfDocument());
            var own = document.AddPage(new PdfPageSize(200, 100));
            own.Canvas.Rectangle(10, 10, 20, 20).Fill();
            document.ImportPages(source);
            document.AddPage(new PdfPageSize(200, 100)).Canvas.DrawPage(source.Pages[1], 0, 0, 100, 50);
            return document;
        }

        var saved = Create().ToArray();
        var streamed = Create();
        using var output = new MemoryStream();
        streamed.BeginSave(output);
        streamed.FlushPages();
        // Only the first page's content: from the first import on, everything is kept for the end.
        Assert.Single(Regex.Matches(Encoding.Latin1.GetString(output.ToArray()), "endobj"));
        streamed.EndSave();

        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
        Assert.Equal(4, Conversion.GetPageCount(output.ToArray()));
    }

    [Fact]
    public void Html_converted_to_a_stream_renders_like_the_converted_document()
    {
        var html = Documents.ReportHtml(chapters: 2);
        var converted = HtmlToPdf.Convert(html).ToArray();
        using var output = new MemoryStream();
        HtmlToPdf.ConvertToStream(html, output);

        var streamed = output.ToArray();
        Assert.Equal(Conversion.GetPageCount(converted), Conversion.GetPageCount(streamed));
        Assert.True(Conversion.GetPageCount(streamed) > 3);
        AssertSameRendering(converted, streamed, password: null, page: 3);
        Assert.Contains("/Outlines", Encoding.Latin1.GetString(streamed), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_reserved_objects_that_are_unset_can_be_set()
    {
        var writer = new IO.PdfFileWriter();
        var added = writer.Add(new Objects.PdfDictionary());
        var reserved = writer.Reserve();

        Assert.Throws<InvalidOperationException>(() => writer.Set(added, new Objects.PdfDictionary()));
        Assert.Throws<InvalidOperationException>(() => writer.Set(new Objects.PdfReference(3), new Objects.PdfDictionary()));
        writer.Set(reserved, new Objects.PdfDictionary());
        Assert.Throws<InvalidOperationException>(() => writer.Set(reserved, new Objects.PdfDictionary()));

        using var output = new MemoryStream();
        var unset = writer.Reserve();
        writer.Begin(output);
        Assert.Throws<InvalidOperationException>(() => writer.Finish(added, null)); // Object 3 was never set.
        Assert.Equal(3, unset.ObjectNumber);
    }

    [Fact]
    public void Misuse_throws()
    {
        var document = new PdfDocument();
        Assert.Throws<InvalidOperationException>(document.FlushPages);
        Assert.Throws<InvalidOperationException>(document.EndSave);

        using var output = new MemoryStream();
        document.BeginSave(output);
        Assert.Throws<InvalidOperationException>(() => document.BeginSave(output));
        Assert.Throws<InvalidOperationException>(() => document.Save(Stream.Null));
        Assert.Throws<InvalidOperationException>(document.ToArray);
        Assert.Throws<InvalidOperationException>(document.EndSave); // No pages.

        document.AddPage();
        document.EndSave();
        Assert.Throws<InvalidOperationException>(() => document.AddPage());
        Assert.Throws<InvalidOperationException>(document.ToArray);
        Assert.Throws<InvalidOperationException>(() => document.BeginSave(Stream.Null));
    }

    private static PdfDocument Dated(PdfDocument document)
    {
        document.Info.CreationDate = document.Info.ModificationDate = Date;
        return document;
    }

    private static void AssertSameRendering(byte[] expectedPdf, byte[] actualPdf, string? password, int page = 0)
    {
        using var expected = Conversion.ToImage(expectedPdf, page: page, options: new RenderOptions(Dpi: 72));
        using var actual = Conversion.ToImage(actualPdf, page: page, password: password, options: new RenderOptions(Dpi: 72));

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Pixels, actual.Pixels);
        Assert.Contains(expected.Pixels, c => c != SkiaSharp.SKColors.White);
    }
}
