using System.Text;
using System.Text.RegularExpressions;
using PDFtoImage;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>
/// SVG drawn as PDF vectors, checked by rasterizing. Each drawing has a 100 x 100 view box and is drawn on a 100 x 100
/// point page at 72 dpi, so one user unit is one pixel and pixel (x, y) is SVG point (x, y).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public partial class SvgRenderingTests
{
    [Fact]
    public void Shapes_are_filled_and_stroked()
    {
        using var bitmap = Render("""
            <rect x="10" y="10" width="30" height="30" fill="#f00"/>
            <rect x="60" y="10" width="30" height="30" fill="none" stroke="#00f" stroke-width="4"/>
            <circle cx="25" cy="75" r="15" fill="lime"/>
            <ellipse cx="75" cy="75" rx="20" ry="8" fill="black"/>
            <polygon points="45,55 55,55 50,65" fill="#f0f"/>
            """);

        AssertColor(bitmap, 25, 25, "#ff0000");
        AssertColor(bitmap, 60, 25, "#0000ff");     // Stroke on the left edge ...
        AssertColor(bitmap, 75, 25, "#ffffff");     // ... none inside.
        AssertColor(bitmap, 25, 75, "#00ff00");
        AssertColor(bitmap, 25, 58, "#ffffff");     // Above the circle (r = 15 from y = 75).
        AssertColor(bitmap, 75, 75, "#000000");
        AssertColor(bitmap, 75, 85, "#ffffff");     // Below the ellipse (ry = 8).
        AssertColor(bitmap, 50, 58, "#ff00ff");
    }

    [Fact]
    public void Paths_with_arcs_and_rounded_rectangles_are_drawn()
    {
        using var bitmap = Render("""
            <path d="M10 50 a20 20 0 0 1 40 0 z" fill="#00f"/>
            <rect x="60" y="10" width="30" height="80" rx="15" fill="#0a0"/>
            """);

        AssertColor(bitmap, 30, 40, "#0000ff");     // Inside the upper half circle.
        AssertColor(bitmap, 30, 55, "#ffffff");     // The lower half is not drawn.
        AssertColor(bitmap, 12, 33, "#ffffff");     // Outside the arc near its corner.
        AssertColor(bitmap, 75, 50, "#00aa00");
        AssertColor(bitmap, 61, 11, "#ffffff");     // The rounded corner is cut off.
    }

    [Fact]
    public void Transforms_nest_in_the_right_order()
    {
        using var bitmap = Render("""
            <g transform="rotate(90 50 50)"><rect x="10" y="10" width="80" height="10" fill="#f00"/></g>
            <g transform="translate(10 60)"><g transform="scale(2)"><rect width="10" height="10" fill="#00f"/></g></g>
            """);

        // rotate(90) about the center maps (x, y) to (100 - y, x): the bar becomes vertical at x 80-90.
        AssertColor(bitmap, 85, 50, "#ff0000");
        AssertColor(bitmap, 50, 15, "#ffffff");

        // Scaled first, then translated: 20 x 20 at (10, 60). The other order would give 10 x 10 at (20, 120).
        AssertColor(bitmap, 28, 78, "#0000ff");
        AssertColor(bitmap, 12, 62, "#0000ff");
    }

    [Theory]
    [InlineData(null, 50, 10, "#ffffff", 50, 50, "#ff0000")]          // xMidYMid meet: 100 x 50, centered vertically.
    [InlineData("xMidYMin meet", 50, 10, "#ff0000", 50, 60, "#ffffff")]
    [InlineData("none", 50, 10, "#ff0000", 50, 90, "#ff0000")]        // Stretched to fill.
    public void The_view_box_is_mapped_with_preserve_aspect_ratio(string? aspect, int x1, int y1, string color1, int x2, int y2, string color2)
    {
        var attribute = aspect is null ? string.Empty : $"preserveAspectRatio=\"{aspect}\"";
        using var bitmap = RenderDocument($"""
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 20 10" {attribute}>
              <rect width="20" height="10" fill="#f00"/>
            </svg>
            """);

        AssertColor(bitmap, x1, y1, color1);
        AssertColor(bitmap, x2, y2, color2);
    }

    [Fact]
    public void Drawn_at_another_aspect_ratio_the_drawing_keeps_its_own()
    {
        var image = PdfImage.LoadSvg("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="#f00"/></svg>""");
        var doc = new PdfDocument();
        doc.AddPage(new PdfPageSize(100, 100)).Canvas.DrawImage(image, 0, 0, 100, 50);
        using var bitmap = Conversion.ToImage(doc.ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

        // A 50 x 50 square centered in the 100 x 50 rectangle at the bottom of the page.
        AssertColor(bitmap, 50, 75, "#ff0000");
        AssertColor(bitmap, 10, 75, "#ffffff");
        AssertColor(bitmap, 90, 75, "#ffffff");
    }

    [Fact]
    public void Linear_gradients_interpolate_between_stops()
    {
        using var bitmap = Render("""
            <defs>
              <linearGradient id="g"><stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#00f"/></linearGradient>
              <linearGradient id="three" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stop-color="#f00"/><stop offset="50%" stop-color="#0f0"/><stop offset="100%" stop-color="#00f"/>
              </linearGradient>
            </defs>
            <rect width="100" height="40" fill="url(#g)"/>
            <rect x="0" y="50" width="100" height="50" fill="url(#three)"/>
            """);

        AssertColor(bitmap, 1, 20, "#ff0000", tolerance: 12);
        AssertColor(bitmap, 98, 20, "#0000ff", tolerance: 12);
        AssertColor(bitmap, 50, 20, "#800080", tolerance: 12);
        AssertColor(bitmap, 50, 51, "#ff0000", tolerance: 16);
        AssertColor(bitmap, 50, 75, "#00ff00", tolerance: 16); // The middle stop (the box is 50 tall from y = 50).
        AssertColor(bitmap, 50, 99, "#0000ff", tolerance: 16);
    }

    [Fact]
    public void Gradients_in_user_space_with_transforms_and_references()
    {
        using var bitmap = Render("""
            <defs>
              <linearGradient id="base"><stop offset="0.5" stop-color="#0f0"/><stop offset="0.5" stop-color="#00f"/></linearGradient>
              <linearGradient id="g" href="#base" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="100" y2="0"
                              gradientTransform="rotate(90 50 50)"/>
            </defs>
            <rect width="100" height="100" fill="url(#g)"/>
            """);

        // Inherited stops with a hard edge at 50%; the rotation turns the horizontal gradient vertical.
        AssertColor(bitmap, 10, 25, "#00ff00");
        AssertColor(bitmap, 90, 25, "#00ff00");
        AssertColor(bitmap, 10, 75, "#0000ff");
    }

    [Fact]
    public void Radial_gradients_and_stop_opacity()
    {
        using var bitmap = Render("""
            <defs>
              <radialGradient id="r"><stop offset="0" stop-color="#fff"/><stop offset="1" stop-color="#000"/></radialGradient>
              <radialGradient id="o"><stop offset="0" stop-color="#00f" stop-opacity="0.5"/><stop offset="1" stop-color="#00f" stop-opacity="0.5"/></radialGradient>
            </defs>
            <rect width="100" height="50" fill="url(#r)"/>
            <rect y="60" width="100" height="40" fill="url(#o)"/>
            """);

        AssertColor(bitmap, 50, 25, "#ffffff", tolerance: 16);  // White center ...
        AssertColor(bitmap, 50, 1, "#000000", tolerance: 30);   // ... black at the radius (half the box height here).
        AssertColor(bitmap, 1, 1, "#000000", tolerance: 30);    // Padded beyond it.
        AssertColor(bitmap, 50, 80, "#8080ff", tolerance: 12);  // Half-transparent blue on white.
    }

    [Fact]
    public void Stroke_gradients_use_the_fill_bounding_box()
    {
        using var bitmap = Render("""
            <defs><linearGradient id="g"><stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#00f"/></linearGradient></defs>
            <rect x="10" y="10" width="80" height="80" fill="none" stroke="url(#g)" stroke-width="10"/>
            """);

        AssertColor(bitmap, 10, 50, "#ff0000", tolerance: 20);
        AssertColor(bitmap, 90, 50, "#0000ff", tolerance: 20);
    }

    [Fact]
    public void Clip_paths_clip_in_user_space_and_bounding_box_units()
    {
        using var bitmap = Render("""
            <defs>
              <clipPath id="c"><circle cx="25" cy="50" r="20"/></clipPath>
              <clipPath id="half" clipPathUnits="objectBoundingBox"><rect width="0.5" height="1"/></clipPath>
            </defs>
            <rect width="50" height="100" fill="#f00" clip-path="url(#c)"/>
            <rect x="60" y="10" width="40" height="80" fill="#00f" clip-path="url(#half)"/>
            """);

        AssertColor(bitmap, 25, 50, "#ff0000");
        AssertColor(bitmap, 25, 25, "#ffffff"); // Outside the circle.
        AssertColor(bitmap, 6, 50, "#ff0000");
        AssertColor(bitmap, 70, 50, "#0000ff"); // The left half of the box ...
        AssertColor(bitmap, 90, 50, "#ffffff"); // ... not the right half.
    }

    [Fact]
    public void Use_symbols_and_style_sheets()
    {
        using var bitmap = Render("""
            <style>.st0{fill:#1a4f8b} .big { stroke: #f00; stroke-width: 4 }</style>
            <defs>
              <symbol id="sq" viewBox="0 0 10 10"><rect width="10" height="10" class="st0"/></symbol>
              <rect id="r" width="20" height="20"/>
            </defs>
            <use href="#sq" x="10" y="10" width="30" height="30"/>
            <use xlink:href="#r" x="60" y="10" fill="#0a0"/>
            <use href="#r" x="60" y="60" class="big" fill="none"/>
            """);

        AssertColor(bitmap, 25, 25, "#1a4f8b");
        AssertColor(bitmap, 42, 25, "#ffffff");  // The symbol is scaled to 30 x 30.
        AssertColor(bitmap, 70, 20, "#00aa00");  // Fill inherited from the use element.
        AssertColor(bitmap, 60, 70, "#ff0000");  // Stroke from the use element's class.
        AssertColor(bitmap, 70, 70, "#ffffff");
    }

    [Fact]
    public void Opacity_is_blended_with_white()
    {
        using var bitmap = Render("""
            <rect width="50" height="50" fill="#f00" fill-opacity="0.5"/>
            <g opacity="0.5"><rect x="50" width="50" height="50" fill="#00f" opacity="0.5"/></g>
            <rect y="50" width="50" height="50" fill="rgba(0, 0, 255, 0.25)"/>
            """);

        AssertColor(bitmap, 25, 25, "#ff8080", tolerance: 4);
        AssertColor(bitmap, 75, 25, "#bfbfff", tolerance: 4);  // Group and element opacity multiply: 0.25.
        AssertColor(bitmap, 25, 75, "#bfbfff", tolerance: 4);
    }

    [Fact]
    public void Fill_rules_dashes_and_visibility()
    {
        using var bitmap = Render("""
            <path d="M10 10 h40 v40 h-40 z M20 20 h20 v20 h-20 z" fill="#000" fill-rule="evenodd"/>
            <path d="M60 10 h30 v30 h-30 z M70 20 h10 v10 h-10 z" fill="#000"/>
            <line x1="0" y1="70" x2="100" y2="70" stroke="#00f" stroke-width="6" stroke-dasharray="10 10"/>
            <rect y="80" width="100" height="20" fill="#f00" visibility="hidden"/>
            <rect y="80" width="100" height="20" fill="#f00" display="none"/>
            """);

        AssertColor(bitmap, 15, 15, "#000000");
        AssertColor(bitmap, 30, 30, "#ffffff");   // The even-odd hole.
        AssertColor(bitmap, 75, 25, "#000000");   // Nonzero: same direction, no hole.
        AssertColor(bitmap, 5, 70, "#0000ff");
        AssertColor(bitmap, 15, 70, "#ffffff");   // A gap.
        AssertColor(bitmap, 25, 70, "#0000ff");
        AssertColor(bitmap, 50, 90, "#ffffff");
    }

    [Fact]
    public void Nested_svg_elements_are_viewports_that_clip()
    {
        using var bitmap = Render("""
            <svg x="50" y="50" width="40" height="40" viewBox="0 0 4 4"><rect x="-10" y="-10" width="30" height="30" fill="#f00"/></svg>
            """);

        AssertColor(bitmap, 70, 70, "#ff0000");
        AssertColor(bitmap, 45, 70, "#ffffff");
        AssertColor(bitmap, 95, 95, "#ffffff");
    }

    [Fact]
    public void Text_is_drawn_with_standard_fonts_and_anchored()
    {
        using var bitmap = Render("""
            <text x="50" y="30" font-size="24" font-family="Arial" text-anchor="middle" fill="#00f">HHH</text>
            <text x="95" y="80" font-size="24" font-family="serif" text-anchor="end">HH<tspan fill="#f00">H</tspan></text>
            """);

        var blue = Ink(bitmap, c => c.Blue > 200 && c.Red < 80);
        Assert.Equal(50, (blue.Left + blue.Right) / 2.0, 2.5);   // Centered on x = 50.
        Assert.InRange(blue.Bottom, 27, 31);                     // On the baseline at y = 30.
        Assert.InRange(blue.Top, 10, 16);                        // Cap height of 24 pt Helvetica: about 17.
        var red = Ink(bitmap, c => c.Red > 200 && c.Blue < 80 && c.Green < 80);
        Assert.InRange(red.Right, 91, 96);                       // Ends at x = 95, the tspan last.
        Assert.InRange(red.Top, 60, 66);
        Assert.Contains("/Times-Roman", Encoding.Latin1.GetString(Pdf("""<text y="10">x</text><text y="20" font-family="serif">y</text>""")));
    }

    [Fact]
    public void Text_is_extractable()
    {
        var pdf = Pdf("""<text x="10" y="50" font-size="10">Hello   SVG</text>""");

        Assert.Contains("Hello SVG", ExternalTools.ExtractText(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_data_images_are_drawn()
    {
        var png = Convert.ToBase64String(File.ReadAllBytes(Fixtures.Path(Path.Combine("PngSuite", "basn2c08.png"))));
        var svg = Uri.EscapeDataString("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1 1"><rect width="1" height="1" fill="#0f0"/></svg>""");
        using var bitmap = Render($"""
            <image href="data:image/png;base64,{png}" x="0" y="0" width="40" height="40"/>
            <image xlink:href="data:image/svg+xml,{svg}" x="50" y="50" width="40" height="40"/>
            <image href="https://example.com/remote.png" width="100" height="100"/>
            """);

        Assert.NotEqual(SKColors.White, bitmap.GetPixel(20, 20)); // The PNG ...
        AssertColor(bitmap, 45, 20, "#ffffff");                    // ... is 40 wide.
        AssertColor(bitmap, 70, 70, "#00ff00");
        AssertColor(bitmap, 95, 45, "#ffffff");                    // The remote image is not loaded.
    }

    [Fact]
    public void Unsupported_features_are_skipped()
    {
        using var bitmap = Render("""
            <defs>
              <filter id="f"><feGaussianBlur stdDeviation="3"/></filter>
              <pattern id="p" width="4" height="4"><rect width="2" height="2"/></pattern>
              <mask id="m"><rect width="100" height="100" fill="white"/></mask>
              <marker id="arrow"><path d="M0 0 L10 5 L0 10 z"/></marker>
            </defs>
            <script>alert(1)</script>
            <foreignObject width="100" height="100"><div xmlns="http://www.w3.org/1999/xhtml">HTML</div></foreignObject>
            <rect width="40" height="40" fill="#f00" filter="url(#f)">
              <animate attributeName="x" from="0" to="50" dur="1s"/>
            </rect>
            <rect x="50" width="40" height="40" fill="url(#p) #00f"/>
            <rect y="50" width="40" height="40" fill="url(#p)"/>
            <rect x="50" y="50" width="40" height="40" fill="url(#missing) #0f0" marker-end="url(#arrow)" mask="url(#m)"/>
            <unknown><rect width="100" height="100"/></unknown>
            """);

        AssertColor(bitmap, 20, 20, "#ff0000");
        AssertColor(bitmap, 70, 20, "#0000ff");   // A pattern falls back to the color after it ...
        AssertColor(bitmap, 20, 70, "#ffffff");   // ... or paints nothing.
        AssertColor(bitmap, 70, 70, "#00ff00");
        AssertColor(bitmap, 95, 95, "#ffffff");   // Unknown elements are not rendered.
    }

    [Fact]
    public void Svg_images_are_form_xobjects_stored_once()
    {
        var image = PdfImage.LoadSvg("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10">
              <defs><linearGradient id="g"><stop stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient></defs>
              <rect width="10" height="10" fill="url(#g)"/><text y="8">A</text>
            </svg>
            """);
        var doc = new PdfDocument { CompressContent = false };
        for (var p = 0; p < 3; p++)
        {
            doc.AddPage().Canvas.DrawImage(image, 10, 10, 50, 50).DrawImage(image, 100, 10, 20, 20);
        }

        var pdf = doc.ToArray();
        var text = Encoding.Latin1.GetString(pdf);

        Assert.Single(FormRegex().Matches(text));
        Assert.DoesNotContain("/Subtype /Image", text, StringComparison.Ordinal); // Vectors, not pixels.
        Assert.Contains("/ShadingType 2", text, StringComparison.Ordinal);
        Assert.Contains("/PatternType 2", text, StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Theory]
    [InlineData("Svg/logo.svg")]
    [InlineData("Svg/icons.svg")]
    [InlineData("Svg/chart.svg")]
    [InlineData("Svg/exported.svg")]
    [InlineData("Svg/unsupported.svg")]
    public void Fixtures_render(string file)
    {
        var image = PdfImage.Load(Fixtures.Path(file));
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawImage(image, 50, 400, image.NaturalWidth, image.NaturalHeight);
        var pdf = doc.ToArray();

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        Assert.True(Ink(bitmap, c => c != SKColors.White).Right > 50, $"{file} draws nothing");
        ExternalTools.AssertQpdfClean(pdf);
    }

    // ---- helpers ----

    /// <summary>Renders SVG content in a 100 x 100 view box drawn at one point per unit.</summary>
    private static SKBitmap Render(string content) =>
        RenderDocument($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">{content}</svg>""");

    private static SKBitmap RenderDocument(string markup)
    {
        var doc = new PdfDocument();
        doc.AddPage(new PdfPageSize(100, 100)).Canvas.DrawImage(PdfImage.LoadSvg(markup), 0, 0, 100, 100);
        return Conversion.ToImage(doc.ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
    }

    private static byte[] Pdf(string content)
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.AddPage(new PdfPageSize(100, 100)).Canvas.DrawImage(
            PdfImage.LoadSvg($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">{content}</svg>"""), 0, 0, 100, 100);
        return doc.ToArray();
    }

    private static void AssertColor(SKBitmap bitmap, int x, int y, string expected, int tolerance = 8)
    {
        var e = SKColor.Parse(expected);
        var actual = bitmap.GetPixel(x, y);
        Assert.True(
            Math.Abs(e.Red - actual.Red) <= tolerance && Math.Abs(e.Green - actual.Green) <= tolerance && Math.Abs(e.Blue - actual.Blue) <= tolerance,
            $"({x}, {y}): expected {expected}, got {actual}");
    }

    /// <summary>The bounding box of the pixels matching <paramref name="predicate"/>.</summary>
    private static (int Left, int Top, int Right, int Bottom) Ink(SKBitmap bitmap, Func<SKColor, bool> predicate)
    {
        var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, -1, -1);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (predicate(bitmap.GetPixel(x, y)))
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                }
            }
        }

        return (left, top, right, bottom);
    }

    [GeneratedRegex(@"/Subtype /Form")]
    private static partial Regex FormRegex();
}
