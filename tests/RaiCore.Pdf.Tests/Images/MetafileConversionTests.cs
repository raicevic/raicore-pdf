using PDFtoImage;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Reading;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Tests.Slides;
using RaiCore.Pdf.Tests.WordDocuments;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>
/// WMF and EMF pictures in Word documents (DOCX, DOC, RTF, ODT) and presentations (PPTX, PPT): drawn as vectors with
/// their text in the converter's fonts, where they used to be reported as images that cannot be drawn.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class MetafileConversionTests
{
    private static readonly byte[] Emf = Fixtures.Read(Path.Combine("Metafiles", "shapes.emf"));
    private static readonly byte[] Wmf = Fixtures.Read(Path.Combine("Metafiles", "shapes.wmf"));

    [Fact]
    public void Docx_draws_emf_and_wmf_pictures_with_its_fonts()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var docx = Docx.Document(
            Picture("rE") + Picture("rW"),
            parts: [("word/media/image1.emf", Emf), ("word/media/image2.wmf", Wmf)],
            relationships: [("rE", "image", "media/image1.emf", false), ("rW", "image", "media/image2.wmf", false)]);
        var pdf = DocxToPdf.ConvertToBytes(docx, new DocxConverterOptions { Diagnostics = diagnostics.Add });
        Assert.Empty(diagnostics);
        var text = Text(pdf);
        Assert.Contains("Enhanced", text, StringComparison.Ordinal);
        Assert.Contains("Metafile", text, StringComparison.Ordinal);
        Assert.Contains("LiberationSans", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal); // The document's fonts.

        // The EMF is 200 x 100 points at the top left of the text area (72 points from the page's corner).
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        var red = bitmap.GetPixel(72 + 25, 72 + 25);
        Assert.True(red.Red > 240 && red.Green < 20 && red.Blue < 20, red.ToString());
    }

    [Fact]
    public void Doc_draws_metafile_blips_and_old_metafile_pictures()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var pdf = DocToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Poi", "vector_image.doc")), new DocConverterOptions { Diagnostics = diagnostics.Add });
        Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
        Assert.Contains("Company", Text(pdf), StringComparison.Ordinal);          // The EMF's own text.
        Assert.True(Forms(pdf) > 0);

        pdf = DocToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Poi", "equation.doc")), new DocConverterOptions { Diagnostics = diagnostics.Add });
        Assert.DoesNotContain(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded);
        Assert.True(Forms(pdf) > 0);
    }

    [Fact]
    public void Rtf_draws_emf_and_wmf_pictures_at_their_goal_size()
    {
        // An RTF WMF has no placeable header: the window comes from its records, the size from the goal size.
        var body = @"{\pict\emfblip\picwgoal4000\pichgoal2000 " + Convert.ToHexString(Emf) + "}"
            + @"{\pict\wmetafile8\picw7056\pich3528\picwgoal2000\pichgoal1000 " + Convert.ToHexString(Wmf[22..]) + @"}\par";
        var (pdf, diagnostics) = Rtf.Convert(Rtf.Document(body));
        Assert.Empty(diagnostics);
        var data = pdf.ToArray();
        var text = Text(data);
        Assert.Contains("Enhanced", text, StringComparison.Ordinal);
        Assert.Contains("Metafile", text, StringComparison.Ordinal);
        var extractor = new TextExtractor(PdfReader.Open(data));
        var emf = extractor.FindOnPage(0, "Enhanced")[0].Bounds;
        var wmf = extractor.FindOnPage(0, "Metafile")[0].Bounds;
        Assert.Equal(10, emf.X - 90, 1.0);                        // At the left margin (RTF's default, 1.25 inches), at full size ...
        Assert.Equal(200 + (10 / 2.0), wmf.X - 90, 1.0);          // ... and after it (200 points wide), at half size.
        Assert.Equal(emf.Height / 2, wmf.Height, 1.0);
    }

    [Fact]
    public void Odt_draws_a_metafile_when_it_has_no_other_image()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var odt = Odt.Document(
            Odt.P("<draw:frame text:anchor-type=\"as-char\" svg:width=\"200pt\" svg:height=\"100pt\"><draw:image xlink:href=\"Pictures/a.emf\"/></draw:frame>"),
            parts: [("Pictures/a.emf", Emf)]);
        var pdf = OdtToPdf.ConvertToBytes(odt, new OdtConverterOptions { Diagnostics = diagnostics.Add });
        Assert.Empty(diagnostics);
        Assert.Contains("Enhanced", Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Pptx_draws_metafile_pictures_cropped_like_bitmaps()
    {
        var reports = new List<SlideDiagnostic>();
        var picture = "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"Pic\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId7\"/>"
            + "<a:srcRect r=\"50000\"/></p:blipFill><p:spPr>" + Deck.Xfrm(0, 0, 100, 100) + "</p:spPr></p:pic>";
        var deck = new Deck { SlideRelationships = $"<Relationship Id=\"rId7\" Type=\"{Deck.R}/image\" Target=\"../media/image1.emf\"/>" }.With(picture);
        deck.Parts.Add(("ppt/media/image1.emf", Emf));
        var pdf = PptxToPdf.ConvertToBytes(deck.Build(), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Empty(reports);

        // The left half (x 0 to 100) fills the 100 x 100 picture: its red square spans x and y 10 to 40, its blue bar x 70
        // to 80 is just inside, and the triangle (from x 90) is cut off at x 100.
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(25, 25));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(75, 25));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(120, 50)); // Nothing of the cropped half outside the picture.
    }

    [Fact]
    public void Ppt_draws_wmf_and_emf_pictures_and_ole_previews()
    {
        var reports = new List<SlideDiagnostic>();
        var pdf = PptToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Slides", "Ppt", "pictures.ppt")), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.DoesNotContain(reports, r => r.Message.Contains("WMF", StringComparison.Ordinal) || r.Message.Contains("EMF", StringComparison.Ordinal));
        Assert.True(Forms(pdf) >= 2);

        // An embedded workbook shown as its EMF preview.
        reports.Clear();
        pdf = PptToPdf.ConvertToBytes(Fixtures.Read(Path.Combine("Poi", "testPPT_oleWorkbook.ppt")), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.True(Forms(pdf) > 0);
        Assert.DoesNotContain(reports, r => r.Subject == "picture");
    }

    private static string Picture(string id) =>
        "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"2540000\" cy=\"1270000\"/><wp:docPr id=\"1\" name=\"p\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
        + $"<pic:pic><pic:blipFill><a:blip r:embed=\"{id}\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";

    private static string Text(byte[] pdf) => new TextExtractor(PdfReader.Open(pdf)).ExtractText();

    /// <summary>The number of form XObjects (vector pictures) the pages draw.</summary>
    private static int Forms(byte[] pdf)
    {
        var reader = PdfReader.Open(pdf);
        return reader.Pages.Sum(page => reader.Resolve(page.Resources[new PdfName("XObject")]) is PdfDictionary xobjects
            ? xobjects.Entries.Count(entry => reader.Resolve(entry.Value) is PdfStreamObject stream && stream.Dictionary[new PdfName("Subtype")] is PdfName { Value: "Form" })
            : 0);
    }
}
