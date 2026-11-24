using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Tests.Tagging;

namespace RaiCore.Pdf.Tests;

/// <summary>Notes (text annotations): their dictionaries, appearance, place under a transform, and tagging.</summary>
public class PdfNoteTests
{
    private static string Text(PdfDocument doc) => Encoding.Latin1.GetString(doc.ToArray());

    [Fact]
    public void A_note_is_a_printed_text_annotation_with_an_appearance()
    {
        var doc = new PdfDocument { CompressContent = false };
        var page = doc.AddPage(PdfPageSize.A4);
        var note = page.AddNote(100, 700, 12, -10, "Check this total");
        note.Author = "Reviewer";

        Assert.Equal((100.0, 690.0, 12.0, 10.0), (note.X, note.Y, note.Width, note.Height)); // A negative height extends below.
        Assert.Same(note, Assert.Single(page.Notes));
        var pdf = Text(doc);
        Assert.Contains("/Subtype /Text", pdf, StringComparison.Ordinal);
        Assert.Contains("/Rect [100 690 112 700]", pdf, StringComparison.Ordinal);
        Assert.Contains("/Contents (Check this total)", pdf, StringComparison.Ordinal);
        Assert.Contains("/T (Reviewer)", pdf, StringComparison.Ordinal);
        Assert.Contains("/F 28", pdf, StringComparison.Ordinal);
        Assert.Contains("/AP <</N ", pdf, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Form", pdf, StringComparison.Ordinal);
        Assert.Contains("/BBox [0 0 12 10]", pdf, StringComparison.Ordinal);
        Assert.Contains("/Annots [", pdf, StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(doc.ToArray());
    }

    [Fact]
    public void Canvas_notes_follow_the_transform_and_bad_rectangles_are_refused()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A4);
        page.Canvas.SaveState().Transform(2, 0, 0, 2, 10, 20);
        var note = page.Canvas.AddNote(5, 5, 4, 3, "Scaled");
        page.Canvas.RestoreState();
        Assert.Equal((20.0, 30.0, 8.0, 6.0), (note.X, note.Y, note.Width, note.Height));
        Assert.Throws<ArgumentException>(() => page.AddNote(double.NaN, 0, 1, 1, "x"));
        Assert.Throws<ArgumentNullException>(() => page.AddNote(0, 0, 1, 1, null!));
    }

    [Fact]
    public void In_a_tagged_PdfA_document_a_note_is_an_annot_element()
    {
        var doc = new PdfDocument { Tagged = true, PdfUA = true, PdfAConformance = PdfAConformance.PdfA2A, Language = "en", Info = { Title = "Notes" } }.EmbedStandardFonts();
        var page = doc.AddPage(PdfPageSize.A4);
        var paragraph = doc.StructureRoot.AddChild(PdfStructureTypes.Paragraph);
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        page.Canvas.BeginTag(paragraph).DrawText("Total", font, 12, 72, 720).EndTag();
        page.AddNote(120, 716, 10, 10, "Rounded up").Author = "A";

        var bytes = doc.ToArray();
        Assert.Equal("Document(P Annot)", TaggedPdfInspector.Inspect(bytes).ToString());
        var pdf = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/StructParent ", pdf, StringComparison.Ordinal);
        Assert.Contains("/Type /OBJR", pdf, StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(bytes);
    }
}
