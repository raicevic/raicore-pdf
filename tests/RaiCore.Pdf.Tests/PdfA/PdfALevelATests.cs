using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Tests.Tagging;

namespace RaiCore.Pdf.Tests.PdfA;

/// <summary>
/// PDF/A level A (PDF/A-2a, PDF/A-3a, #194): tagging implied, every glyph mapped to Unicode, and the level A rules
/// checked when saving. veraPDF validates the samples in CI (<see cref="PdfAVeraPdfTests"/>).
/// </summary>
public class PdfALevelATests
{
    private static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Bold = PdfFont.Load(Fixtures.Path("LiberationSans-Bold.ttf"));

    /// <summary>A level A document with a tagged heading, ready to have more drawn on its page.</summary>
    private static PdfDocument Tagged(PdfAConformance level = PdfAConformance.PdfA2A, string? language = "en-GB")
    {
        // Level A tags by itself; the others are tagged here for comparison.
        var doc = new PdfDocument { PdfAConformance = level, Language = language, Tagged = !level.IsLevelA() }.EmbedStandardFonts();
        doc.Info.CreationDate = PdfASamples.Created;
        doc.AddPage().Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Heading1))
            .DrawText("Archived", Sans, 12, 72, 720).EndTag();
        return doc;
    }

    private static string Message(Action save) => Assert.Throws<InvalidOperationException>(save).Message;

    [Theory]
    [InlineData(PdfAConformance.None, false)]
    [InlineData(PdfAConformance.PdfA2B, false)]
    [InlineData(PdfAConformance.PdfA3B, false)]
    [InlineData(PdfAConformance.PdfA2A, true)]
    [InlineData(PdfAConformance.PdfA3A, true)]
    [InlineData(PdfAConformance.PdfA1B, false)]
    [InlineData(PdfAConformance.PdfA1A, true)]
    public void Level_A_implies_tagging(PdfAConformance level, bool tagged)
    {
        var doc = new PdfDocument { PdfAConformance = level };

        Assert.Equal(tagged, doc.Tagged);
        Assert.Equal(tagged, doc.PdfAConformance.IsLevelA());
    }

    [Theory]
    [InlineData(PdfAConformance.None, 0)]
    [InlineData(PdfAConformance.PdfA2B, 2)]
    [InlineData(PdfAConformance.PdfA2A, 2)]
    [InlineData(PdfAConformance.PdfA3B, 3)]
    [InlineData(PdfAConformance.PdfA3A, 3)]
    [InlineData(PdfAConformance.PdfA1B, 1)]
    [InlineData(PdfAConformance.PdfA1A, 1)]
    public void Each_level_has_its_part(PdfAConformance level, int part)
    {
        Assert.Equal(part, level.Part());
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2B, "2", "B")]
    [InlineData(PdfAConformance.PdfA3B, "3", "B")]
    [InlineData(PdfAConformance.PdfA2A, "2", "A")]
    [InlineData(PdfAConformance.PdfA3A, "3", "A")]
    [InlineData(PdfAConformance.PdfA1B, "1", "B")]
    [InlineData(PdfAConformance.PdfA1A, "1", "A")]
    public void Xmp_identifies_the_part_and_conformance(PdfAConformance level, string part, string conformance)
    {
        var xmp = PdfMetadataReader.Xmp(Tagged(level).ToArray());

        Assert.Equal(part, Assert.Single(xmp.Descendants(PdfAId + "part")).Value);
        Assert.Equal(conformance, Assert.Single(xmp.Descendants(PdfAId + "conformance")).Value);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2A)]
    [InlineData(PdfAConformance.PdfA3A)]
    [InlineData(PdfAConformance.PdfA1A)]
    public void A_level_A_document_is_marked_and_has_a_structure_tree(PdfAConformance level)
    {
        var bytes = Tagged(level).ToArray();
        var reader = PdfReader.Open(bytes);

        Assert.Equal(PdfBoolean.True, ((PdfDictionary)reader.Resolve(reader.Catalog["MarkInfo"])!)["Marked"]);
        Assert.Equal("en-GB", PdfText.Decode(((PdfString)reader.Catalog["Lang"]!).Bytes));
        var root = TaggedPdfInspector.Inspect(bytes);
        Assert.Equal("Document(H1)", root.ToString());
        Assert.Equal("Archived", root.Children[0].Text);
        Assert.NotNull(reader.Catalog["OutputIntents"]);
    }

    [Fact]
    public void The_document_language_is_required()
    {
        Assert.Contains("requires the document's language", Message(() => Tagged(language: null).ToArray()), StringComparison.Ordinal);
        Assert.Contains("requires the document's language", Message(() => Tagged(language: " ").ToArray()), StringComparison.Ordinal);

        // Level B needs none.
        var levelB = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B }.EmbedStandardFonts().AddPageWithText("Archived");
        levelB.ToArray();
    }

    [Theory]
    [InlineData("en_GB")]
    [InlineData("en-")]
    [InlineData("toolongtag")]
    [InlineData("en GB")]
    [InlineData("1en")]
    public void Languages_must_be_BCP47_tags(string language)
    {
        Assert.Contains("BCP 47", Message(() => Tagged(language: language).ToArray()), StringComparison.Ordinal);

        var element = Tagged();
        var span = element.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        span.Language = language;
        element.Pages[0].Canvas.BeginTag(span).DrawText("Tak", StandardFont.Helvetica, 12, 72, 700).EndTag();
        var message = Message(() => element.ToArray());
        Assert.Contains("BCP 47", message, StringComparison.Ordinal);
        Assert.Contains("of a P element", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData(" de-CH ")]
    [InlineData("zh-Hant-TW")]
    [InlineData("x-private1")]
    public void Valid_languages_are_accepted(string language)
    {
        Tagged(language: language).ToArray();

        var doc = Tagged();
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        paragraph.Language = language;
        doc.Pages[0].Canvas.BeginTag(paragraph).DrawText("Tak", StandardFont.Helvetica, 12, 72, 700).EndTag();
        doc.ToArray();
    }

    [Fact]
    public void An_element_language_is_only_checked_when_set()
    {
        var doc = Tagged();
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        paragraph.Language = "  ";
        doc.Pages[0].Canvas.BeginTag(paragraph).DrawText("Tak", StandardFont.Helvetica, 12, 72, 700).EndTag();

        doc.ToArray();
    }

    [Fact]
    public void Non_standard_structure_types_are_rejected()
    {
        var doc = Tagged();
        var custom = doc.StructureRoot.AddChild(PdfStructureTypes.Section).AddChild("Chapter");
        doc.Pages[0].Canvas.BeginTag(custom).DrawText("One", StandardFont.Helvetica, 12, 72, 700).EndTag();

        var message = Message(() => doc.ToArray());
        Assert.Contains("\"Chapter\"", message, StringComparison.Ordinal);
        Assert.Contains("role map", message, StringComparison.Ordinal);

        // Under level B (and PDF/UA alone) the library does not check the types.
        doc.PdfAConformance = PdfAConformance.PdfA2B;
        doc.ToArray();
    }

    [Theory]
    [InlineData("NonStruct")]
    [InlineData("Private")]
    [InlineData("BibEntry")]
    [InlineData("Ruby")]
    [InlineData("WP")]
    [InlineData("TOCI")]
    public void Standard_types_without_a_constant_are_accepted(string type)
    {
        var doc = Tagged();
        doc.Pages[0].Canvas.BeginTag(doc.StructureRoot.AddChild(type)).DrawText("Text", StandardFont.Helvetica, 12, 72, 700).EndTag();

        doc.ToArray();
    }

    [Fact]
    public void An_element_without_content_is_not_checked()
    {
        var doc = Tagged();
        doc.StructureRoot.AddChild("Empty").AddChild(PdfStructureTypes.Figure);

        doc.ToArray();
    }

    [Theory]
    [InlineData(PdfStructureTypes.Figure)]
    [InlineData(PdfStructureTypes.Formula)]
    public void Figures_and_formulas_need_alternate_text(string type)
    {
        var doc = Tagged();
        var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Division).AddChild(type);
        doc.Pages[0].Canvas.BeginTag(figure).Rectangle(72, 600, 50, 50).Fill().EndTag();

        var message = Message(() => doc.ToArray());
        Assert.Contains($"a {type} element has none", message, StringComparison.Ordinal);
        Assert.Contains("alt=\"\"", message, StringComparison.Ordinal);

        figure.AlternateText = " ";
        Message(() => doc.ToArray());

        figure.AlternateText = "A black square";
        doc.ToArray();

        figure.AlternateText = null;
        figure.ActualText = "x\u00B2";
        doc.ToArray();
    }

    [Fact]
    public void Content_drawn_before_the_document_was_tagged_is_rejected()
    {
        var doc = new PdfDocument { Language = "en" }.EmbedStandardFonts();
        doc.AddPage();
        doc.AddPage().Canvas.DrawText("Untagged", StandardFont.Helvetica, 12, 72, 720);
        doc.PdfAConformance = PdfAConformance.PdfA3A;

        var message = Message(() => doc.ToArray());
        Assert.Contains("page 2 was drawn on before the document was tagged", message, StringComparison.Ordinal);

        // Level B does not mind.
        doc.PdfAConformance = PdfAConformance.PdfA3B;
        doc.ToArray();
    }

    [Fact]
    public void Untagged_content_in_a_tagged_document_is_an_artifact_and_allowed()
    {
        var doc = Tagged();
        doc.Pages[0].Canvas.DrawText("Page 1", StandardFont.Helvetica, 9, 300, 40).Rectangle(72, 60, 400, 1).Fill();

        doc.ToArray();
    }

    [Fact]
    public void Private_use_characters_need_actual_text()
    {
        var doc = Tagged();
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        doc.Pages[0].Canvas.BeginTag(paragraph).DrawText("Done \uE001 and \uE002", Sans, 12, 72, 700).DrawText("\uE003", Sans, 12, 72, 680).EndTag();

        var message = Message(() => doc.ToArray());
        Assert.Contains("Private Use Area", message, StringComparison.Ordinal);
        Assert.Contains("page 1 draws \"Done \uE001 and \uE002\"", message, StringComparison.Ordinal); // The first text.

        // Level B has no such rule (the font's missing glyph is what fails there).
        doc.PdfAConformance = PdfAConformance.PdfA2B;
        Assert.Contains(".notdef", Message(() => doc.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Private_use_characters_outside_the_basic_plane_need_actual_text_too()
    {
        var doc = Tagged();
        var glyph = new PositionedGlyph(36, 0.5, 0, 0, "\U000F0001");
        doc.Pages[0].Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawGlyphs([glyph], Sans, 12, 72, 700).EndTag();

        Assert.Contains("Private Use Area", Message(() => doc.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Glyphs_drawn_with_actual_text_may_be_private_use_characters()
    {
        var doc = Tagged();
        var glyphs = new[] { new PositionedGlyph(36, 0.5, 0, 0, "\uE001"), new PositionedGlyph(37, 0.5, 0, 0, null) };
        doc.Pages[0].Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawGlyphs(glyphs, Sans, 12, 72, 700, actualText: "Done").EndTag();

        var reader = PdfReader.Open(doc.ToArray());
        Assert.Contains("/ActualText", Encoding.Latin1.GetString(StreamDecoder.Decode((PdfStreamObject)reader.Resolve(reader.Pages[0].Dictionary["Contents"])!, reader.Resolve)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_standard_font_does_not_draw_private_use_characters()
    {
        // WinAnsi has no code for them: the text is encoded as '?', which maps to itself.
        var doc = Tagged();
        doc.Pages[0].Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("\uE001", StandardFont.Helvetica, 12, 72, 700).EndTag();

        doc.ToArray();
    }

    [Theory]
    [InlineData(0xDFFF, false)]
    [InlineData(0xE000, true)]
    [InlineData(0xF8FF, true)]
    [InlineData(0xF900, false)]
    [InlineData(0xEFFFF, false)]
    [InlineData(0xF0000, true)]
    [InlineData(0x10FFFD, true)]
    public void The_private_use_areas(int codePoint, bool privateUse)
    {
        Assert.Equal(privateUse, PdfCanvas.IsPrivateUse(codePoint));
    }

    [Fact]
    public void Every_glyph_maps_to_Unicode_at_level_A()
    {
        static string ToUnicode(PdfAConformance level)
        {
            var doc = Tagged(level);
            var glyphs = new[]
            {
                new PositionedGlyph(36, 0.5, 0, 0, "A"),
                new PositionedGlyph(37, 0.5, 0, 0, null), // An extra glyph of a cluster.
                new PositionedGlyph(38, 0.5, 0, 0, "\uFEFF"),
                new PositionedGlyph(39, 0.5, 0, 0, "D\0\uFFFE"),
            };
            doc.Pages[0].Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawGlyphs(glyphs, Bold, 12, 72, 700, actualText: "ABCD").EndTag();
            var reader = PdfReader.Open(doc.ToArray());
            var fonts = (PdfDictionary)reader.Resolve(reader.Pages[0].Resources["Font"])!;
            var font = fonts.Entries.Select(entry => (PdfDictionary)reader.Resolve(entry.Value)!).Single(f => ((PdfName)f["BaseFont"]!).Value.EndsWith("+LiberationSans-Bold", StringComparison.Ordinal));
            return Encoding.ASCII.GetString(StreamDecoder.Decode((PdfStreamObject)reader.Resolve(font["ToUnicode"])!, reader.Resolve));
        }

        var levelA = ToUnicode(PdfAConformance.PdfA2A);
        Assert.Contains("4 beginbfchar\n<0024> <0041>\n<0025> <FFFD>\n<0026> <FFFD>\n<0027> <0044>\n", levelA, StringComparison.Ordinal);

        // Level B leaves the glyph without text out, and writes the text as it is.
        var levelB = ToUnicode(PdfAConformance.PdfA2B);
        Assert.Contains("3 beginbfchar\n<0024> <0041>\n<0026> <FEFF>\n<0027> <00440000FFFE>\n", levelB, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "\uFFFD")]
    [InlineData("", "\uFFFD")]
    [InlineData("\0\uFEFF\uFFFE", "\uFFFD")]
    [InlineData("a\uFFFEb\0", "ab")]
    [InlineData("\uFEFFfi", "fi")]
    [InlineData("x", "x")]
    public void Glyph_text_is_made_mappable(string? text, string expected)
    {
        Assert.Equal(expected, CidFontWriter.MappableText(text));
    }

    [Fact]
    public void The_structure_is_checked_when_saving_page_by_page()
    {
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2A, Language = "en" };
        using var output = new MemoryStream();
        doc.BeginSave(output);
        var figure = doc.StructureRoot.AddChild(PdfStructureTypes.Figure);
        doc.AddPage().Canvas.BeginTag(figure).Rectangle(72, 600, 50, 50).Fill().EndTag();
        doc.FlushPages();

        Assert.Contains("alternate text", Message(doc.EndSave), StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_are_checked_when_saving_page_by_page()
    {
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2A, Language = "en" };
        using var output = new MemoryStream();
        doc.BeginSave(output);
        doc.AddPage().Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("\uE000", Sans, 12, 72, 700).EndTag();

        Assert.Contains("Private Use Area", Message(doc.FlushPages), StringComparison.Ordinal);
    }

    [Fact]
    public void PdfA2a_rejects_attachments_and_PdfA3a_allows_them()
    {
        var doc = Tagged();
        doc.Attachments.Add(new PdfAttachment("notes.txt", "Notes"u8, "text/plain"));

        Assert.Contains("PdfA3A", Message(() => doc.ToArray()), StringComparison.Ordinal);

        doc.PdfAConformance = PdfAConformance.PdfA3A;
        var reader = PdfReader.Open(doc.ToArray());
        Assert.NotNull(reader.Catalog["AF"]);
    }

    [Fact]
    public void An_invoice_keeps_level_A()
    {
        Assert.Throws<InvalidOperationException>(() => Tagged(PdfAConformance.PdfA2A).AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.Basic));

        var invoice = Tagged(PdfAConformance.PdfA3A).AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.Basic);
        Assert.Equal(PdfAConformance.PdfA3A, invoice.PdfAConformance);
        Assert.Equal("A", PdfMetadataReader.Xmp(invoice.ToArray()).Descendants(PdfAId + "conformance").Single().Value);

        Assert.Equal(PdfAConformance.PdfA3B, Tagged(PdfAConformance.PdfA3B).AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.Basic).PdfAConformance);
        Assert.Equal(PdfAConformance.PdfA3B, Tagged(PdfAConformance.None).AttachFacturXInvoice(PdfAttachmentTests.InvoiceXml, FacturXProfile.Basic).PdfAConformance);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2A)]
    [InlineData(PdfAConformance.PdfA3A)]
    public void Html_is_tagged_at_level_A(PdfAConformance level)
    {
        var bytes = PdfASamples.HtmlLevelA(level).ToArray();

        var root = TaggedPdfInspector.Inspect(bytes);
        Assert.Equal(
            "Document(H1 P(Link) H2 L(LI(Lbl LBody) LI(Lbl LBody)) P(Figure) Table(THead(TR(TH(P) TH(P))) TBody(TR(TD(P) TD(P)) TR(TD(P) TD(P)))) P(Figure) P)",
            root.ToString());
        Assert.Equal("A bar chart of sales by region", root.Descendants().Last(e => e.Type == "Figure").Entry("Alt"));
        Assert.Equal("A", PdfMetadataReader.Xmp(bytes).Descendants(PdfAId + "conformance").Single().Value);
    }

    [Fact]
    public void Html_streamed_page_by_page_is_tagged_at_level_A()
    {
        using var output = new MemoryStream();
        HtmlToPdf.ConvertToStream(PdfASamples.LevelAHtml, output, new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA2A });

        Assert.StartsWith("Document(H1 P(Link) H2 L(", TaggedPdfInspector.Inspect(output.ToArray()).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Html_images_need_alt_text_at_level_A()
    {
        const string Html = """<html lang="en"><body><p>Logo: <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" width="20" height="20"></p></body></html>""";
        var options = new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA2A };

        Assert.Contains("alt attribute", Message(() => HtmlToPdf.Convert(Html, options).ToArray()), StringComparison.Ordinal);

        // A decorative image (empty alt) is an artifact.
        HtmlToPdf.Convert(Html.Replace("width=\"20\"", "alt=\"\" width=\"20\"", StringComparison.Ordinal), options).ToArray();
    }

    [Fact]
    public void Html_needs_a_language_at_level_A()
    {
        var options = new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3A };

        Assert.Contains("language", Message(() => HtmlToPdf.Convert("<p>No language</p>", options).ToArray()), StringComparison.Ordinal);
        HtmlToPdf.Convert("<p>No language</p>", new HtmlConverterOptions { PdfAConformance = PdfAConformance.PdfA3A, Language = "en" }).ToArray();
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA2A)]
    [InlineData(PdfAConformance.PdfA3A)]
    public void A_layout_document_is_tagged_at_level_A(PdfAConformance level)
    {
        var root = TaggedPdfInspector.Inspect(PdfASamples.Layout(level).ToArray());

        Assert.Equal("Document(H1 P Figure Table(THead(TR(TH(P) TH(P))) TBody(TR(TD(P) TD(P)) TR(TD(P) TD(P)))))", root.ToString());
        Assert.Equal("Four colored squares", root.Children[2].Entry("Alt"));
    }
}
