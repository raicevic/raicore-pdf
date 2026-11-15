using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Tagging;

public class StrictTaggingTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static PdfDocument Strict() => new() { Tagged = true, StrictTagging = true };

    [Fact]
    public void Untagged_text_throws_naming_the_page()
    {
        var doc = Strict();
        doc.AddPage();
        var canvas = doc.AddPage().Canvas;

        var error = Assert.Throws<InvalidOperationException>(() => canvas.DrawText("Forgotten", Sans, 12, 72, 720));

        Assert.Contains("text", error.Message, StringComparison.Ordinal);
        Assert.Contains("page 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Untagged_paths_images_and_pages_throw_when_painted()
    {
        var doc = Strict();
        var canvas = doc.AddPage().Canvas;
        var image = PdfImage.LoadSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"/>""");

        Assert.Contains("filled path", Assert.Throws<InvalidOperationException>(() => canvas.Rectangle(0, 0, 10, 10).Fill()).Message, StringComparison.Ordinal);
        canvas.EndPath();
        Assert.Contains("stroked path", Assert.Throws<InvalidOperationException>(() => canvas.MoveTo(0, 0).LineTo(10, 10).Stroke()).Message, StringComparison.Ordinal);
        canvas.EndPath();
        Assert.Contains("image", Assert.Throws<InvalidOperationException>(() => canvas.DrawImage(image, 0, 0, 4, 4)).Message, StringComparison.Ordinal);
        Assert.Contains("text", Assert.Throws<InvalidOperationException>(() => canvas.DrawGlyphs([new PositionedGlyph(1, 0.5, 0, 0, "a")], Sans, 12, 0, 0)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tagged_content_artifacts_and_clipping_are_allowed()
    {
        var doc = Strict();
        var canvas = doc.AddPage().Canvas;
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);

        canvas.SaveState().Rectangle(0, 0, 500, 500).Clip() // Clipping paints nothing.
            .MoveTo(0, 0).LineTo(5, 5).EndPath()
            .BeginTag(paragraph).DrawText("Tagged", Sans, 12, 72, 720).EndTag()
            .BeginArtifact(PdfArtifactKind.Pagination).DrawText("Page 1", Sans, 9, 72, 40).Rectangle(0, 0, 10, 10).Fill().EndTag()
            .BeginArtifact().BeginGroup(0.5).Rectangle(0, 0, 10, 10).Fill().EndGroup().EndTag()
            .RestoreState();

        Assert.Equal("Document(P)", TaggedPdfInspector.Inspect(doc.ToArray()).ToString());
    }

    [Fact]
    public void Strict_tagging_does_nothing_in_an_untagged_document_and_is_off_by_default()
    {
        var untagged = new PdfDocument { StrictTagging = true };
        untagged.AddPage().Canvas.DrawText("Plain", Sans, 12, 72, 720).Rectangle(0, 0, 10, 10).Fill();
        Assert.NotEmpty(untagged.ToArray());

        var lenient = new PdfDocument { Tagged = true };
        Assert.False(lenient.StrictTagging);
        lenient.AddPage().Canvas.DrawText("Becomes an artifact", Sans, 12, 72, 720);
        Assert.NotEmpty(lenient.ToArray());
    }
}
