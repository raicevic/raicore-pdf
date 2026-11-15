using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Tests.Reading;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Tagging;

/// <summary>Pages imported from other files in a tagged document: their own content is an artifact, what is drawn on them is tagged.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class ImportedPageTaggingTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static PdfReader Letterhead() =>
        PdfReader.Open(TestPdf.Pages("/MediaBox [0 0 300 300]", ("", "0 0 1 rg 10 250 280 40 re f 0.5 g 10 10 280 5 re f")));

    private static PdfDocument Tagged(bool pdfUA = false) =>
        new() { Tagged = true, PdfUA = pdfUA, StrictTagging = true, Language = "en", Info = { Title = "Imported" }, CompressContent = false };

    [Fact]
    public void The_content_of_an_imported_page_is_an_artifact()
    {
        var doc = Tagged();
        doc.ImportPages(Letterhead());
        var bytes = doc.ToArray();

        Assert.Equal("Document", TaggedPdfInspector.Inspect(bytes).ToString()); // Every operator is marked: the inspector checks.
        Assert.Contains("/Artifact BMC\nq\n", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Content_drawn_on_an_imported_page_is_tagged_in_its_form()
    {
        var doc = Tagged();
        var page = doc.ImportPages(Letterhead())[0];
        var heading = doc.StructureRoot.AddChild(PdfStructureTypes.Heading1);
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        page.Canvas.BeginTag(heading).DrawText("Invoice", Sans, 18, 20, 200).EndTag()
            .BeginTag(paragraph).DrawText("Amount due", Sans, 10, 20, 170).EndTag()
            .BeginArtifact().Rectangle(20, 160, 100, 1).Fill().EndTag();
        var bytes = doc.ToArray();

        var root = TaggedPdfInspector.Inspect(bytes);
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Equal("Document(H1 P)", root.ToString());
        Assert.Equal("Invoice", root.Children[0].Text);
        Assert.Equal("Amount due", root.Children[1].Text);
        Assert.All(root.Children, element => Assert.All(element.Content, c => Assert.NotNull(c.Stream))); // References name the form (/Stm).
        Assert.Contains("/Subtype /Form", text, StringComparison.Ordinal);
        Assert.Contains("/StructParents 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/StructParents 1", text, StringComparison.Ordinal); // The page itself has no marked content of its own.
    }

    [Fact]
    public void Several_imported_pages_and_generated_pages_mix()
    {
        var doc = Tagged();
        var letterhead = Letterhead();
        var first = doc.ImportPages(letterhead)[0];
        var generated = doc.AddPage(new PdfPageSize(300, 300));
        var second = doc.ImportPages(letterhead)[0];
        first.Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("One", Sans, 12, 20, 200).EndTag();
        generated.Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph)).DrawText("Two", Sans, 12, 20, 200).EndTag();
        _ = second; // Nothing drawn: only the artifact.

        var root = TaggedPdfInspector.Inspect(doc.ToArray());

        Assert.Equal("Document(P P)", root.ToString());
        Assert.Equal(["One", "Two"], root.Children.Select(c => c.Text));
        Assert.NotNull(root.Children[0].Content.Single().Stream); // In the imported page's form.
        Assert.Null(root.Children[1].Content.Single().Stream); // In the generated page's own stream.
    }

    [Fact]
    public void Untagged_documents_import_pages_as_before()
    {
        var doc = new PdfDocument { CompressContent = false };
        var page = doc.ImportPages(Letterhead())[0];
        page.Canvas.DrawText("Drawn", Sans, 12, 20, 200);
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.DoesNotContain("/Artifact", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/StructParents", text, StringComparison.Ordinal);
        Assert.Contains("q /RcStamp", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tagging_does_not_change_how_an_imported_page_looks()
    {
        static byte[] Draw(PdfDocument doc)
        {
            var page = doc.ImportPages(Letterhead())[0];
            if (doc.Tagged)
            {
                page.Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph));
            }

            page.Canvas.SetFillColor(new PdfColor(0.8, 0.1, 0.1)).DrawText("Drawn", Sans, 24, 20, 200).Rectangle(20, 100, 60, 40).Fill();
            if (doc.Tagged)
            {
                page.Canvas.EndTag();
            }

            return doc.ToArray();
        }

        using var untagged = Pixels.Render(Draw(new PdfDocument()), 0);
        using var tagged = Pixels.Render(Draw(Tagged()), 0);

        Pixels.AssertSame(untagged, tagged, maxDifferentFraction: 0);
        Pixels.AssertColor(SKColors.Blue, Pixels.At(tagged, 150, 270)); // The letterhead's band.
    }

    [Fact]
    public void A_PdfUA_document_with_imported_pages_validates_with_veraPDF()
    {
        var doc = Tagged(pdfUA: true);
        var letterhead = Letterhead();
        doc.ImportPages(letterhead); // Nothing drawn.
        var page = doc.ImportPages(letterhead)[0];
        page.Canvas.BeginTag(doc.StructureRoot.AddChild(PdfStructureTypes.Heading1)).DrawText("Invoice", Sans, 18, 20, 200).EndTag();

        ExternalTools.AssertVeraPdfCompliant(doc.ToArray(), "ua1");
    }
}
