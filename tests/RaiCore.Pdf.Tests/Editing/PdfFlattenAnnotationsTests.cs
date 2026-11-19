using RaiCore.Pdf.Editing;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Tests.Reading;
using SkiaSharp;
using static RaiCore.Pdf.Tests.Editing.PdfEditorTests;

namespace RaiCore.Pdf.Tests.Editing;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PdfFlattenAnnotationsTests
{
    private const int Dpi = 100;

    /// <summary>The fixture's annotation that is shown on screen but not printed (160, 460, 220, 490), in points from the page's top left.</summary>
    private static readonly SKRect ScreenOnly = new(158, 458, 222, 492); // With its border.

    private static byte[] Fixture() => Fixtures.Read("Annotations/pymupdf-annotations.pdf");

    private static List<string> Subtypes(byte[] pdf, int page = 0)
    {
        var reader = PdfReader.Open(pdf);
        return reader.Resolve(reader.Pages[page].Dictionary["Annots"]) is PdfArray annots
            ? [.. annots.Items.Select(a => ((PdfName)((PdfDictionary)reader.Resolve(a)!)["Subtype"]!).Value)]
            : [];
    }

    /// <summary>Pixels that differ beyond anti-aliased edges, both ways, outside <paramref name="ignore"/> (in points from the top left).</summary>
    private static int Differences(SKBitmap a, SKBitmap b, SKRect? ignore = null)
    {
        if (ignore is { } r)
        {
            foreach (var bitmap in new[] { a, b })
            {
                using var canvas = new SKCanvas(bitmap);
                using var paint = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(SKRect.Create(r.Left * Dpi / 72, r.Top * Dpi / 72, r.Width * Dpi / 72, r.Height * Dpi / 72), paint);
            }
        }

        return Pixels.CountDifferencesBeyondEdges(a, b) + Pixels.CountDifferencesBeyondEdges(b, a);
    }

    [Fact]
    public void Annotations_look_the_same_after_flattening_and_are_gone_but_the_link()
    {
        var before = Fixture();
        var editor = PdfEditor.Open(before);

        var result = editor.FlattenAnnotations();
        var after = editor.ToArray();

        // Drawn: 4 text markups, ink, square, circle, line, polygon, free text, stamp and the note. Removed: the note's
        // pop-up, the hidden square and the one not printed.
        Assert.Equal(new PdfFlattenResult(12, 0, 3), result);
        Assert.Equal(["Link"], Subtypes(after));
        using var expected = Pixels.RenderWithAnnotations(before, 0, Dpi);
        using var actual = Pixels.Render(after, 0, Dpi);
        Assert.Equal(0, Differences(expected, actual, ScreenOnly)); // A viewer shows the one not printed; it is gone.
        Assert.Equal(SKColors.White, actual.GetPixel((int)(ScreenOnly.MidX * Dpi / 72), (int)(ScreenOnly.MidY * Dpi / 72)));
        using var withLink = Pixels.RenderWithAnnotations(after, 0, Dpi);
        Assert.Equal(0, Differences(actual, withLink)); // The link draws nothing.
        AssertValid(after);
    }

    [Fact]
    public void Types_pick_what_is_flattened_and_pop_ups_stay_with_their_note()
    {
        var editor = PdfEditor.Open(Fixture());

        var result = editor.FlattenAnnotations(new PdfAnnotationFlattenOptions { Types = new HashSet<string> { "Highlight", "Ink" } });
        var after = editor.ToArray();

        Assert.Equal(new PdfFlattenResult(2, 0, 0), result);
        var left = Subtypes(after);
        Assert.DoesNotContain("Highlight", left);
        Assert.DoesNotContain("Ink", left);
        Assert.Equal(14, left.Count);
        Assert.Contains("Popup", left);
        AssertValid(after);
    }

    [Fact]
    public void Links_go_too_when_they_are_not_kept()
    {
        var editor = PdfEditor.Open(Fixture());

        Assert.Equal(new PdfFlattenResult(12, 0, 4), editor.FlattenAnnotations(new PdfAnnotationFlattenOptions { KeepLinks = false }));
        Assert.Empty(Subtypes(editor.ToArray()));
    }

    [Fact]
    public void Form_fields_are_not_touched()
    {
        var editor = PdfEditor.Open(Fixtures.Read("Forms/pymupdf-form.pdf"));
        var fields = editor.GetForm().Fields.Count;

        Assert.Equal(new PdfFlattenResult(0, 0, 0), editor.FlattenAnnotations());
        Assert.Equal(fields, PdfEditor.Open(editor.ToArray()).GetForm().Fields.Count);
    }

    [Fact]
    public void An_annotation_is_drawn_in_its_current_state_and_pop_ups_without_a_parent_stay()
    {
        var pdf = TestPdf.Build([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [4 0 R 7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Square /F 4 /Rect [50 50 150 150] /AS /On /AP << /N << /On 5 0 R /Off 6 0 R >> >> >>",
            TestPdf.Stream("0 0 1 rg 0 0 100 100 re f", "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"),
            TestPdf.Stream("1 0 0 rg 0 0 100 100 re f", "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"),
            "<< /Type /Annot /Subtype /Popup /Rect [0 0 40 40] >>",
            "<< /Type /Annot /Subtype /Popup /Rect [0 0 40 40] /Parent 8 0 R >>", // Its own parent (malformed).
        ]);
        var editor = PdfEditor.Open(pdf);

        Assert.Equal(new PdfFlattenResult(1, 0, 0), editor.FlattenAnnotations());
        var after = editor.ToArray();
        using var page = Pixels.Render(after, 0, 72);
        Assert.Equal(new SKColor(0, 0, 255), Pixels.At(page, 100, 100));
        Assert.Equal(["Popup", "Popup"], Subtypes(after));
        AssertValid(after);
    }
}
