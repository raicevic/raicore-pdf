using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// CSS <c>box-shadow</c> (#155). A 50 x 20 pt box at (20, 10) on a 200 x 100 pt page without margins, rendered at 72 dpi
/// (1 pt per pixel).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlBoxShadowTests
{
    private const string Page = "<style>@page { size: 200pt 100pt; margin: 0 } body { margin: 0 } .box { width: 50pt; height: 20pt; margin: 10pt 20pt }</style>";
    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    [Theory]
    [InlineData("box-shadow: 5pt 5pt red")]
    [InlineData("box-shadow: red 5pt 5pt 0 0")]
    [InlineData("--s: 5pt 5pt #f00; box-shadow: var(--s)")]
    [InlineData("box-shadow: 1pt 1pt blue; box-shadow: 5pt 5pt red")]
    [InlineData("color: red; box-shadow: 5pt 5pt")] // currentColor.
    public void A_shadow_is_drawn_from_its_declaration(string declarations)
    {
        using var bitmap = Render($"""<div class="box" style="{declarations}"></div>""");

        Assert.Equal(Red, bitmap.GetPixel(72, 32));
        Assert.Equal(White, bitmap.GetPixel(40, 20));
    }

    [Fact]
    public void Several_shadows_and_inset_ones_are_drawn()
    {
        using var bitmap = Render("""<div class="box" style="background: #fff; box-shadow: 0 0 0 3pt blue, 5pt 5pt red, inset 4pt 0 red"></div>""");

        Assert.Equal(Blue, bitmap.GetPixel(18, 20)); // The spread ring, on top of the offset one.
        Assert.Equal(Blue, bitmap.GetPixel(71, 31));
        Assert.Equal(Red, bitmap.GetPixel(74, 33));
        Assert.Equal(Red, bitmap.GetPixel(22, 20)); // Inset, over the background.
        Assert.Equal(White, bitmap.GetPixel(40, 20));
    }

    [Fact]
    public void A_shadow_follows_the_border_radius_and_is_blurred()
    {
        using var bitmap = Render("""<div class="box" style="border-radius: 10pt; box-shadow: 0 0 10px black"></div>""");

        Assert.InRange(bitmap.GetPixel(71, 20).Red, 40, 220);
        Assert.Equal(White, bitmap.GetPixel(90, 20));
        Assert.True(bitmap.GetPixel(18, 8).Red > bitmap.GetPixel(18, 20).Red); // Lighter round the corner.
    }

    [Theory]
    [InlineData("box-shadow: none")]
    [InlineData("box-shadow: 1px1px")]
    [InlineData("box-shadow: 5pt 5pt -2pt red")] // A negative blur is invalid.
    [InlineData("box-shadow: 5pt red")]
    [InlineData("box-shadow: 5pt 5pt red blue")]
    [InlineData("box-shadow: inset inset 5pt 5pt red")]
    [InlineData("box-shadow: 5pt 5pt 0 0 1pt red")]
    public void Invalid_or_no_shadows_draw_nothing(string declarations)
    {
        using var bitmap = Render($"""<div class="box" style="{declarations}"></div>""");

        Assert.All(Enumerable.Range(0, 100), x => Assert.Equal(White, bitmap.GetPixel(x, 32)));
        Assert.All(Enumerable.Range(0, 100), x => Assert.Equal(White, bitmap.GetPixel(x, 12))); // Nor inset.
    }

    [Fact]
    public void A_shadow_is_not_inherited()
    {
        using var bitmap = Render("""<div style="box-shadow: 0 40pt red; height: 0"><div class="box" style="margin-top: 0"></div></div>""");

        Assert.All(Enumerable.Range(0, 100), x => Assert.Equal(White, bitmap.GetPixel(x, 45)));
    }

    [Fact]
    public void Text_shadow_is_still_left_out()
    {
        var bytes = HtmlToPdf.ConvertToBytes(Page + """<p style="text-shadow: 1px1px; box-shadow: 2pt 2pt red">Text</p>""");

        Assert.NotEmpty(bytes);
    }

    [Theory]
    [InlineData(PdfAConformance.PdfA1B)]
    [InlineData(PdfAConformance.PdfA2B)]
    public void Pdf_a_1_leaves_shadows_out_as_it_allows_no_transparency(PdfAConformance conformance)
    {
        var pdf = HtmlToPdf.ConvertToBytes(
            Page + """<div class="box" style="box-shadow: 0 0 10px rgba(0, 0, 0, .5), 5pt 5pt red"></div>""",
            new HtmlConverterOptions { PdfAConformance = conformance });
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        var pixel = bitmap.GetPixel(74, 33);
        Assert.Equal(conformance != PdfAConformance.PdfA1B, pixel.Red > 200 && pixel.Green < 50); // Red (sRGB output intent) or white.
    }

    [Fact]
    public void Box_shadow_is_not_reported_as_unsupported()
    {
        var found = new List<HtmlDiagnostic>();
        HtmlToPdf.ConvertToBytes(Page + """<div class="box" style="box-shadow: 0 1px 2px gray"></div>""", new HtmlConverterOptions { Diagnostics = found.Add });

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("1em 2pt", 12, 2, 0, 0, false)]
    [InlineData("inset 0 0 3pt 1pt", 0, 0, 3, 1, true)]
    [InlineData("0 0 4px -2px", 0, 0, 3, -1.5, false)]
    public void Shadow_values_are_parsed(string value, double x, double y, double blur, double spread, bool inset)
    {
        var shadow = Assert.Single(CssShadows.Parse(value, v => CssValues.TryParseLength(v, 12, 16, default), PdfColor.Black));

        Assert.Equal(new BoxShadow(x, y, blur, spread, PdfColor.Black) { Inset = inset }, shadow);
    }

    private static SKBitmap Render(string html) =>
        Conversion.ToImage(HtmlToPdf.ConvertToBytes(Page + html), page: 0, options: new RenderOptions(Dpi: 72));
}
