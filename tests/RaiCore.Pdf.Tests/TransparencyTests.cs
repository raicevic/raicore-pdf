using System.Text;
using System.Text.RegularExpressions;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using SkiaSharp;

namespace RaiCore.Pdf.Tests;

/// <summary>Alpha colors (extended graphics states) and transparency groups on the canvas.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public partial class TransparencyTests
{
    private static readonly PdfColor Red = new(1, 0, 0);
    private static readonly PdfColor Blue = new(0, 0, 1);

    [Fact]
    public void Colors_are_opaque_unless_given_an_alpha()
    {
        Assert.Equal(1, default(PdfColor).Alpha);
        Assert.True(PdfColor.Black.IsOpaque);
        Assert.Equal(0.25, new PdfColor(1, 0, 0, 0.25).Alpha);
        Assert.Equal((0.0, 1.0), (new PdfColor(0, 0, 0, -1).Alpha, new PdfColor(0, 0, 0, 7).Alpha));
        Assert.Equal(new PdfColor(1, 0, 0, 0.5), Red.WithAlpha(0.5));
        Assert.Equal(new PdfColor(1, 0, 0, 0.4), PdfColor.FromRgba(255, 0, 0, 0.4));
        Assert.NotEqual(Red, Red.WithAlpha(0.5));
    }

    [Fact]
    public void Alpha_colors_set_extended_graphics_states_only_when_the_alpha_changes()
    {
        var canvas = new PdfDocument().AddPage().Canvas
            .SetFillColor(Red)
            .SetFillColor(Red.WithAlpha(0.5))
            .SetFillColor(Blue.WithAlpha(0.5))
            .SetStrokeColor(Blue.WithAlpha(0.25))
            .SetFillColor(Red);

        Assert.Equal(
            ["1 0 0 rg", "1 0 0 rg", "/GS1 gs", "0 0 1 rg", "0 0 1 RG", "/GS2 gs", "1 0 0 rg", "/GS3 gs"],
            Ops(canvas));
    }

    [Fact]
    public void Restoring_the_graphics_state_restores_the_alpha()
    {
        var canvas = new PdfDocument().AddPage().Canvas
            .SaveState().SetFillColor(Red.WithAlpha(0.5)).RestoreState()
            .SetFillColor(Red.WithAlpha(0.5)); // The alpha is 1 again after Q, so it is set again.

        Assert.Equal(["q", "1 0 0 rg", "/GS1 gs", "Q", "1 0 0 rg", "/GS1 gs"], Ops(canvas));
    }

    [Fact]
    public void Extended_graphics_states_hold_fill_and_stroke_alpha_and_are_written_once_per_document()
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.AddPage().Canvas.SetFillColor(Red.WithAlpha(0.5)).Rectangle(0, 0, 10, 10).Fill();
        doc.AddPage().Canvas.SetFillColor(Blue.WithAlpha(0.5)).Rectangle(0, 0, 10, 10).Fill()
            .SetStrokeColor(Blue.WithAlpha(0.2)).MoveTo(0, 0).LineTo(9, 9).Stroke();
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Equal(["<</Type /ExtGState/CA 1/ca 0.5>>", "<</Type /ExtGState/CA 0.2/ca 0.5>>"], ExtGStateRegex().Matches(text).Select(m => m.Value));
        var references = Regex.Matches(text, @"/ExtGState <</GS1 (\d+) 0 R").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, references.Count);
        Assert.Single(references.Distinct()); // Both pages share the state object.
        Assert.Equal(2, Regex.Matches(text, "/Group <</Type /Group/S /Transparency/CS /DeviceRGB>>").Count); // Both pages blend in RGB.
    }

    [Fact]
    public void Opaque_pages_have_no_transparency_group()
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.AddPage().Canvas.SetFillColor(Red).Rectangle(0, 0, 10, 10).Fill();
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.DoesNotContain("/Group", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/ExtGState", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_are_drawn_opaque_whatever_the_fill_alpha()
    {
        var image = PdfImage.Load(Fixtures.Read(Path.Combine("Images", "transparent.gif")));
        var canvas = new PdfDocument().AddPage().Canvas
            .DrawImage(image, 0, 0, 10, 10)
            .SetFillColor(Red.WithAlpha(0.5))
            .DrawImage(image, 0, 0, 10, 10);

        Assert.Equal(["q 10 0 0 10 0 0 cm /Im1 Do Q", "1 0 0 rg", "/GS1 gs", "q /GS2 gs 10 0 0 10 0 0 cm /Im1 Do Q"], Ops(canvas));
    }

    [Fact]
    public void A_group_is_a_transparency_form_drawn_with_its_opacity()
    {
        var doc = new PdfDocument { CompressContent = false };
        var page = doc.AddPage(new PdfPageSize(200, 100));
        page.Canvas.SetFillColor(Blue).Rectangle(0, 0, 200, 100).Fill()
            .BeginGroup(0.4)
            .SetFillColor(Red).Rectangle(10, 10, 50, 50).Fill()
            .DrawText("A", StandardFont.Helvetica, 12, 20, 20)
            .EndGroup();
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Equal(["0 0 1 rg", "0 0 200 100 re", "f", "q /GS1 gs /Fm1 Do Q"], Ops(page.Canvas));
        Assert.Contains("<</Type /ExtGState/CA 0.4/ca 0.4>>", text, StringComparison.Ordinal);
        var form = FormRegex().Match(text);
        Assert.True(form.Success, text);
        Assert.Contains("/BBox [0 0 200 100]", form.Value, StringComparison.Ordinal);
        Assert.Contains("/Group <</Type /Group/S /Transparency/CS /DeviceRGB/I true>>", form.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("/Matrix", form.Value, StringComparison.Ordinal);
        Assert.Contains("stream\n1 0 0 rg\n10 10 50 50 re\nf\nBT /F1 12 Tf", text, StringComparison.Ordinal);

        // The font is used only inside the group: it is in the group's resources, not the page's.
        Assert.Matches(@"/Resources <</Font <</F1 \d+ 0 R>>>>", form.Value);
        Assert.Matches(@"/Type /Page/Parent \d+ 0 R/MediaBox \[0 0 200 100\]/Resources <</XObject <</Fm1 \d+ 0 R>>/ExtGState <</GS1 \d+ 0 R>>>>", text);
    }

    [Fact]
    public void Groups_nest_and_start_at_full_alpha()
    {
        var doc = new PdfDocument { CompressContent = false };
        var canvas = doc.AddPage(new PdfPageSize(100, 100)).Canvas
            .SetFillColor(Red.WithAlpha(0.3))
            .BeginGroup(0.5)
            .SetFillColor(Red.WithAlpha(0.3)) // The group starts at alpha 1, so this sets 0.3 again.
            .BeginGroup(0.25).Rectangle(0, 0, 5, 5).Fill().EndGroup()
            .EndGroup()
            .SetFillColor(Red.WithAlpha(0.3)); // Still 0.3 outside: nothing to write.
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Equal(["1 0 0 rg", "/GS1 gs", "q /GS3 gs /Fm2 Do Q", "1 0 0 rg"], Ops(canvas));
        Assert.Contains("stream\n1 0 0 rg\n/GS1 gs\nq /GS2 gs /Fm1 Do Q", text, StringComparison.Ordinal); // The outer group draws the inner one.
        Assert.Contains("<</Type /ExtGState/CA 0.25/ca 0.25>>", text, StringComparison.Ordinal);
        Assert.Matches(@"/Resources <</XObject <</Fm1 \d+ 0 R>>/ExtGState <</GS1 \d+ 0 R/GS2 \d+ 0 R>>>>", text);
    }

    [Fact]
    public void A_group_under_a_transformation_keeps_the_page_space()
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.AddPage(new PdfPageSize(100, 100)).Canvas
            .SaveState().Transform(2, 0, 0, 2, 10, 20)
            .BeginGroup(0.5).Rectangle(0, 0, 5, 5).Fill().EndGroup()
            .Rectangle(0, 0, 1, 1).Fill() // Back in the transformed space: the group's cm ended with it.
            .RestoreState();
        var text = Encoding.Latin1.GetString(doc.ToArray());

        var form = FormRegex().Match(text).Value;
        Assert.Contains("/Matrix [0.5 0 0 0.5 -5 -10]", form, StringComparison.Ordinal);
        Assert.Contains("stream\n2 0 0 2 10 20 cm\n0 0 5 5 re\nf\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_must_be_balanced()
    {
        var canvas = new PdfDocument().AddPage().Canvas;
        Assert.Throws<InvalidOperationException>(() => canvas.EndGroup());
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.BeginGroup(double.NaN));

        canvas.BeginGroup(0.5).SaveState();
        Assert.Throws<InvalidOperationException>(() => canvas.EndGroup()); // The saved state is not restored.
        canvas.RestoreState().EndGroup();

        var unclosed = new PdfDocument();
        unclosed.AddPage().Canvas.BeginGroup(0.5);
        Assert.Throws<InvalidOperationException>(() => unclosed.ToArray());
    }

    [Fact]
    public void Translucent_colors_blend_with_what_is_underneath()
    {
        using var bitmap = Render(canvas => canvas
            .SetFillColor(Blue).Rectangle(0, 0, 100, 100).Fill()
            .SetFillColor(Red.WithAlpha(0.5)).Rectangle(0, 50, 50, 50).Fill()
            .SetStrokeColor(Red.WithAlpha(0.25)).SetLineWidth(20).MoveTo(50, 10).LineTo(100, 10).Stroke()
            .DrawText("H", StandardFont.HelveticaBold, 60, 55, 40)); // 50% red fill alpha still in effect for text.

        AssertColor(bitmap, 25, 25, 128, 0, 128);  // 50% red over blue.
        AssertColor(bitmap, 75, 90, 64, 0, 191);   // 25% red stroke over blue.
        AssertColor(bitmap, 25, 75, 0, 0, 255);    // Plain blue.
        AssertColor(bitmap, 62, 35, 128, 0, 128);  // The H's stem.
    }

    [Fact]
    public void A_group_composites_overlapping_shapes_once()
    {
        using var bitmap = Render(canvas => canvas
            .SetFillColor(Blue).Rectangle(0, 0, 100, 100).Fill()
            .BeginGroup(0.5)
            .SetFillColor(Red).Rectangle(10, 10, 50, 50).Fill()
            .SetFillColor(new PdfColor(0, 1, 0)).Rectangle(40, 40, 50, 50).Fill()
            .EndGroup());

        AssertColor(bitmap, 20, 80, 128, 0, 128);  // Red only.
        AssertColor(bitmap, 50, 50, 0, 128, 128);  // The overlap shows the green on top at 50%, without red.
        AssertColor(bitmap, 80, 20, 0, 128, 128);  // Green only.
        AssertColor(bitmap, 5, 5, 0, 0, 255);
    }

    [Fact]
    public void Transparency_produces_a_valid_pdf()
    {
        var doc = new PdfDocument();
        doc.AddPage().Canvas.SetFillColor(Red.WithAlpha(0.5)).Rectangle(0, 0, 100, 100).Fill()
            .SaveState().Transform(1, 0, 0, 1, 50, 50).BeginGroup(0.5).BeginGroup(0.5)
            .DrawText("A", StandardFont.Helvetica, 12, 0, 0).EndGroup().EndGroup().RestoreState();
        ExternalTools.AssertQpdfClean(doc.ToArray());
    }

    // ---- helpers ----

    private static string[] Ops(PdfCanvas canvas) => Encoding.Latin1.GetString(canvas.ToBytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Draws on a 100 x 100 point page rendered at 72 dpi: pixel (x, y) is PDF point (x, 100 - y).</summary>
    private static SKBitmap Render(Action<PdfCanvas> draw)
    {
        var doc = new PdfDocument();
        draw(doc.AddPage(new PdfPageSize(100, 100)).Canvas);
        return Conversion.ToImage(doc.ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
    }

    private static void AssertColor(SKBitmap bitmap, int x, int y, int red, int green, int blue, int tolerance = 4)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(
            Math.Abs(red - actual.Red) <= tolerance && Math.Abs(green - actual.Green) <= tolerance && Math.Abs(blue - actual.Blue) <= tolerance,
            $"({x}, {y}): expected ({red}, {green}, {blue}), got {actual}");
    }

    [GeneratedRegex(@"<</Type /ExtGState/CA [\d.]+/ca [\d.]+>>")]
    private static partial Regex ExtGStateRegex();

    [GeneratedRegex(@"<</Type /XObject/Subtype /Form[^\n]*/Group <</Type /Group/S /Transparency[^\n]*")]
    private static partial Regex FormRegex();
}
