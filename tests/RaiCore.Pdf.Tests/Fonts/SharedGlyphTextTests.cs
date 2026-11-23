using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Fonts;

/// <summary>
/// One glyph drawn for different texts (#268): each text gets a character code of its own, mapped to the glyph through
/// a CIDToGIDMap stream, so copying, searching and reading aloud give each text, not the first one drawn.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class SharedGlyphTextTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static int GlyphOf(char c) => ((OpenTypePdfFont)Sans).Font.GetGlyphId(c);

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    [Fact]
    public void One_glyph_drawn_for_two_texts_extracts_as_each_and_draws_the_glyph_for_both()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(new PdfPageSize(200, 100));
        var a = GlyphOf('A');
        page.Canvas.DrawGlyphs([new PositionedGlyph(a, 0.667, 0, 0, "A")], Sans, 40, 20, 40);
        page.Canvas.DrawGlyphs([new PositionedGlyph(a, 0.667, 0, 0, "\u0391")], Sans, 40, 120, 40); // Greek capital alpha
        var pdf = doc.ToArray();

        var text = Pages(pdf)[0];
        Assert.Contains("A", text, StringComparison.Ordinal);
        Assert.Contains("\u0391", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/CIDToGIDMap /Identity", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        // Both codes draw the A: the same dark pixels in each half.
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        int Dark(int from) => Enumerable.Range(from, 100).Sum(x => Enumerable.Range(0, 100).Count(y => bitmap.GetPixel(x, y).Red < 128));
        Assert.True(Dark(0) > 50);
        Assert.Equal(Dark(0), Dark(100));
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Fact]
    public void A_font_whose_glyphs_have_one_text_each_keeps_the_identity_map()
    {
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawText("Plain text", Sans, 12, 72, 700);

        Assert.Contains("/CIDToGIDMap /Identity", Encoding.Latin1.GetString(doc.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Spaces_drawn_after_a_zero_width_space_extract_as_spaces()
    {
        // HarfBuzz draws a default-ignorable character the font lacks (U+200B) with the space glyph.
        var zwsp = TextShaper.Shape("\u200B", Sans).Runs.Single().Glyphs.Single();
        Assert.Equal(GlyphOf(' '), zwsp.GlyphId);

        var style = new TextStyle(Sans, 12);
        var flow = new FlowDocument(PageSetup.A4)
            .Add(new ParagraphBlock(new Paragraph(style).Add("\u200B")))
            .Add(new ParagraphBlock(new Paragraph(style).Add("simple result")));

        Assert.Contains("simple result", Pages(flow.Render().ToArray())[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Codes_are_shared_by_the_pages_of_a_document()
    {
        var doc = new PdfDocument();
        var a = GlyphOf('A');
        foreach (var text in new[] { "A", "\u0391", "A", "\u0391" })
        {
            doc.AddPage(new PdfPageSize(100, 100)).Canvas.DrawGlyphs([new PositionedGlyph(a, 0.667, 0, 0, text)], Sans, 20, 20, 40);
        }

        Assert.Equal(["A", "\u0391", "A", "\u0391"], Pages(doc.ToArray()).Select(t => t.Trim()));
    }

    [Fact]
    public void Glyphs_shared_through_the_text_api_and_the_glyph_api_get_their_own_codes_in_streamed_documents()
    {
        // Pages flushed early (BeginSave/FlushPages) were encoded before later pages drew the glyph for another text.
        using var output = new MemoryStream();
        var doc = new PdfDocument();
        doc.BeginSave(output);
        doc.AddPage(new PdfPageSize(100, 100)).Canvas.DrawText("A", Sans, 20, 20, 40);
        doc.FlushPages();
        doc.AddPage(new PdfPageSize(100, 100)).Canvas.DrawGlyphs([new PositionedGlyph(GlyphOf('A'), 0.667, 0, 0, "\u0391")], Sans, 20, 20, 40);
        doc.EndSave();

        Assert.Equal(["A", "\u0391"], Pages(output.ToArray()).Select(t => t.Trim()));
    }
}
