using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Benchmarks;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Tests.PdfA;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// Tagged PDF, PDF/UA, PDF/A and attachments written page by page (<see cref="PdfDocument.BeginSave"/>): the file is
/// byte for byte the one <see cref="PdfDocument.Save(Stream)"/> writes, also when pages are flushed while the structure
/// is still being built.
/// </summary>
public partial class PdfStreamingStandardsTests
{
    private static readonly DateTimeOffset Date = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PdfAConformance.None)]
    [InlineData(PdfAConformance.PdfA2B)]
    [InlineData(PdfAConformance.PdfA3B)]
    public void A_tagged_PdfUA_document_drawn_and_flushed_page_by_page_is_the_saved_file(PdfAConformance level)
    {
        var saved = Tagged(level, afterPage: null).ToArray();

        using var output = new MemoryStream();
        Tagged(level, afterPage: null, begin: output);

        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
        var pdf = Encoding.Latin1.GetString(saved);
        Assert.Equal(4, Regex.Matches(pdf, "/StructParents ").Count); // Three pages and the group's form.
        Assert.Single(Regex.Matches(pdf, "/StructParent ")); // The link annotation.
        Assert.Contains("/Type /OBJR", pdf, StringComparison.Ordinal);
        Assert.Contains("/ParentTreeNextKey 5", pdf, StringComparison.Ordinal);
        Assert.Equal(level != PdfAConformance.None, pdf.Contains("/OutputIntents", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_tagged_as_PdfUA_written_page_by_page_is_the_saved_file()
    {
        var html = Documents.ReportHtml(chapters: 2).Replace("<p>", "<p>See <a href=\"#c2\">chapter 2</a>. ", StringComparison.Ordinal);
        PdfDocument Configure(PdfDocument document)
        {
            document.Tagged = document.PdfUA = true;
            document.Language = "en";
            document.Info.Title = "Annual report";
            document.Info.CreationDate = document.Info.ModificationDate = Date;
            return document;
        }

        var saved = Configure(new PdfDocument());
        HtmlToPdf.CreateFlowDocument(html).RenderTo(saved);
        var expected = saved.ToArray();

        var streamed = Configure(new PdfDocument());
        using var output = new MemoryStream();
        streamed.BeginSave(output);
        HtmlToPdf.CreateFlowDocument(html).RenderTo(streamed); // Flushes each page after its decorator (an artifact).
        streamed.EndSave();

        Assert.True(streamed.Pages.Count > 3);
        Assert.True(expected.AsSpan().SequenceEqual(output.ToArray()));
        Assert.Contains("/StructTreeRoot", Encoding.Latin1.GetString(expected), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlToPdf_converts_to_a_stream_like_Convert_with_tagging_and_PdfA()
    {
        var html = Documents.InvoiceHtml();
        var options = new HtmlConverterOptions { Tagged = true, PdfUA = true, PdfAConformance = PdfAConformance.PdfA3B };
        var converted = HtmlToPdf.Convert(html, options).ToArray();
        using var output = new MemoryStream();
        HtmlToPdf.ConvertToStream(html, output, options);

        // The same file, but for the time it was written (in the Info dictionary and XMP) and the /ID derived from it.
        Assert.Equal(WithoutDates(converted), WithoutDates(output.ToArray()));
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B)]
    [InlineData(PdfAConformance.PdfA3B)]
    public void A_PdfA_document_written_page_by_page_is_the_saved_file(PdfAConformance level)
    {
        var saved = PdfASamples.Canvas(level).ToArray();
        var streamed = PdfASamples.Canvas(level);
        using var output = new MemoryStream();
        streamed.BeginSave(output);
        streamed.FlushPages();
        streamed.EndSave();

        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
        Assert.Contains("/OutputIntents", Encoding.Latin1.GetString(saved), StringComparison.Ordinal);
    }

    [Fact]
    public void A_PdfA_and_PdfUA_document_from_HTML_written_page_by_page_is_the_saved_file()
    {
        var saved = PdfASamples.Html(PdfAConformance.PdfA2B, pdfUA: true).ToArray();
        var streamed = PdfASamples.Html(PdfAConformance.PdfA2B, pdfUA: true);
        using var output = new MemoryStream();
        streamed.BeginSave(output);
        streamed.FlushPages();
        streamed.EndSave();

        Assert.True(saved.AsSpan().SequenceEqual(output.ToArray()));
    }

    [Fact]
    public void A_FacturX_invoice_written_page_by_page_is_the_saved_file_with_attachments_added_after_the_pages()
    {
        var csv = Encoding.UTF8.GetBytes("line;amount\n1;100.00\n");
        PdfDocument Invoice()
        {
            var document = PdfAttachmentTests.Invoice(FacturXProfile.EN16931);
            document.Info.ModificationDate = Date;
            return document;
        }

        var saved = Invoice();
        saved.Attachments.Add(new PdfAttachment("lines.csv", csv, "text/csv") { ModificationDate = Date });
        var expected = saved.ToArray();

        var streamed = Invoice();
        using var output = new MemoryStream();
        streamed.BeginSave(output);
        streamed.FlushPages();
        streamed.Attachments.Add(new PdfAttachment("lines.csv", csv, "text/csv") { ModificationDate = Date }); // Read by EndSave.
        streamed.EndSave();

        Assert.True(expected.AsSpan().SequenceEqual(output.ToArray()));
        var pdf = Encoding.Latin1.GetString(expected);
        Assert.Contains("factur-x.xml", pdf, StringComparison.Ordinal);
        Assert.Contains("lines.csv", pdf, StringComparison.Ordinal);
        Assert.Contains("/AF ", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_written_later_are_checked_against_PdfA_and_PdfUA_when_they_are_flushed()
    {
        var pdfA = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B }.EmbedStandardFonts();
        pdfA.BeginSave(Stream.Null);
        pdfA.ImportPages(PdfReader.Open(PdfDocumentTests.CreateSample().ToArray()));
        Assert.Contains("PDF/A", Assert.Throws<InvalidOperationException>(pdfA.FlushPages).Message, StringComparison.Ordinal);

        var pdfUA = new PdfDocument { Tagged = true, PdfUA = true, Language = "en", Info = { Title = "Accessible" } };
        pdfUA.BeginSave(Stream.Null);
        pdfUA.AddPage().Canvas.DrawText("Not embedded", StandardFont.Helvetica, 12, 72, 720);
        Assert.Contains("PDF/UA requires embedded fonts", Assert.Throws<InvalidOperationException>(pdfUA.FlushPages).Message, StringComparison.Ordinal);

        var attachments = new PdfDocument();
        attachments.BeginSave(Stream.Null);
        attachments.AddPage();
        attachments.Attachments.Add(new PdfAttachment("a.txt", "a"u8));
        attachments.Attachments.Add(new PdfAttachment("a.txt", "b"u8));
        Assert.Contains("unique", Assert.Throws<InvalidOperationException>(attachments.EndSave).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_cannot_be_inserted_before_written_pages()
    {
        var document = new PdfDocument();
        document.BeginSave(Stream.Null);
        document.AddPage();
        document.AddPage();
        document.FlushPages();

        Assert.Throws<InvalidOperationException>(() => document.InsertPage(1, PdfPageSize.A4));
        Assert.Throws<InvalidOperationException>(() => document.InsertPages(0, PdfReader.Open(PdfDocumentTests.CreateSample().ToArray())));
        document.InsertPage(2, PdfPageSize.A5); // After them is fine.
        document.EndSave();
        Assert.Equal(3, document.Pages.Count);
    }

    /// <summary>
    /// A tagged PDF/UA document of three pages: a paragraph continued on the next page, a transparency group with tagged
    /// content, a tagged link to the last page, artifacts, and an element first used on the last page. With
    /// <paramref name="begin"/> it is written page by page to that stream, each page flushed as soon as it is drawn.
    /// </summary>
    private static PdfDocument Tagged(PdfAConformance level, Action<PdfDocument>? afterPage, Stream? begin = null)
    {
        var document = new PdfDocument { PdfAConformance = level, Tagged = true, PdfUA = true, Language = "en-GB", Info = { Title = "Written page by page" } }
            .EmbedStandardFonts();
        document.Info.CreationDate = document.Info.ModificationDate = Date;
        if (begin is not null)
        {
            document.BeginSave(begin);
            afterPage = d => d.FlushPages();
        }

        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var heading = document.StructureRoot.AddChild(PdfStructureTypes.Heading1);
        var paragraph = document.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        var link = paragraph.AddChild(PdfStructureTypes.Link);

        var first = document.AddPage();
        first.Canvas.BeginTag(heading).DrawText("Written page by page", font, 20, 72, 760).EndTag()
            .BeginTag(paragraph).DrawText("A paragraph that goes on", StandardFont.TimesRoman, 12, 72, 720)
            .BeginTag(link).DrawText("to the end", StandardFont.TimesRoman, 12, 220, 720);
        first.Canvas.AddLink(220, 716, 60, 14, "end").Description = "Go to the end";
        first.Canvas.EndTag().EndTag()
            .BeginArtifact(PdfArtifactKind.Pagination).DrawText("1", StandardFont.Helvetica, 9, 300, 40).EndTag();
        afterPage?.Invoke(document);

        var second = document.AddPage();
        second.Canvas.BeginTag(paragraph).DrawText("on the second page,", StandardFont.TimesRoman, 12, 72, 760)
            .BeginGroup(0.5).DrawText("half transparent,", font, 12, 72, 740).EndGroup()
            .DrawText("and ends here.", StandardFont.TimesRoman, 12, 72, 720).EndTag()
            .BeginArtifact(PdfArtifactKind.Pagination).DrawText("2", StandardFont.Helvetica, 9, 300, 40).EndTag();
        afterPage?.Invoke(document);

        var third = document.AddPage();
        var figure = document.StructureRoot.AddChild(PdfStructureTypes.Paragraph); // Created after pages were written.
        third.Canvas.BeginTag(figure).DrawText("The end", font, 14, 72, 760).EndTag();
        document.NamedDestinations["end"] = new PdfDestination(third, 780);
        afterPage?.Invoke(document);

        if (begin is not null)
        {
            document.EndSave();
        }

        return document;
    }

    private static string WithoutDates(byte[] pdf) =>
        IdOrDate().Replace(Encoding.Latin1.GetString(pdf), match => new string('x', match.Length));

    [GeneratedRegex(@"\(D:[0-9+\-Z']+\)|\d{4}-\d\d-\d\dT[0-9:.+\-Z]+|/ID \[<[0-9A-F]+> <[0-9A-F]+>\]")]
    private static partial Regex IdOrDate();
}
