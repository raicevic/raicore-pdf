using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// Translucent colors and <c>opacity</c> in HTML. Render tests draw on a blue background and render at 96 dpi, so
/// one pixel is one CSS pixel: 50% red over blue is (128, 0, 128).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlTransparencyTests
{
    private const string RedSvg = "data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10' fill='red'/></svg>";

    [Fact]
    public void Colors_keep_their_alpha()
    {
        var block = Flatten(Blocks("""
            <div style="color: rgba(255, 0, 0, 0.5); background: #0000ff80; border: 2px solid hsla(120, 100%, 50%, 0.25)">x</div>
            """)).OfType<ParagraphBlock>().Single();

        Assert.Equal(new PdfColor(1, 0, 0, 0.5), block.Paragraph.Spans[0].Style.Color);
        Assert.Equal(new PdfColor(0, 0, 1, 0.5), block.Style.Background); // AngleSharp rounds the alpha to 2 decimals.
        Assert.Equal(new PdfColor(0, 1, 0, 0.25), block.Style.Border.Top.Color);
    }

    [Theory]
    [InlineData(BorderStyle.Inset, true)]
    [InlineData(BorderStyle.Outset, true)]
    [InlineData(BorderStyle.Groove, false)]
    public void Shaded_border_colors_keep_their_alpha(BorderStyle style, bool outer)
    {
        Assert.Equal(0.5, BorderPainter.Shade(new PdfColor(1, 0, 0, 0.5), style, BorderPainter.Side.Top, outer).Alpha);
        Assert.Equal(0.5, BorderPainter.Shade(new PdfColor(0.1, 0, 0, 0.5), style, BorderPainter.Side.Top, outer).Alpha); // Too dark to darken.
    }

    [Fact]
    public void Transparent_is_no_background_and_invisible_text_and_borders()
    {
        var block = Flatten(Blocks("""
            <div style="color: transparent; background: transparent; border: 2px solid transparent">x</div>
            """)).OfType<ParagraphBlock>().Single();

        Assert.Null(block.Style.Background);
        Assert.Equal(0, block.Paragraph.Spans[0].Style.Color.Alpha);
        Assert.Equal(0, block.Style.Border.Left.Color.Alpha);
    }

    [Theory]
    [InlineData("opacity: 0.4", 0.4)]
    [InlineData("opacity: 25%", 0.25)]
    [InlineData("opacity: 3", 1)]
    [InlineData("opacity: -1", 0)]
    [InlineData("opacity: bogus", 1)]
    [InlineData("", 1)]
    public void Opacity_is_computed_per_element(string css, double expected)
    {
        var blocks = Flatten(Blocks($"<div id='a' style='{css}; padding: 1px'><div style='padding: 1px'>x</div></div>")).ToList();

        Assert.Equal(expected, blocks.Single(b => b.Id == "a").Style.Opacity, 3);
        Assert.Equal(1, blocks.OfType<ParagraphBlock>().Single().Style.Opacity); // Not inherited.
    }

    [Fact]
    public void Translucent_backgrounds_borders_and_text_blend_with_the_parent()
    {
        using var bitmap = Render("""
            <div style="background: rgba(255, 0, 0, 0.5); height: 40px; border-left: 20px solid rgba(0, 255, 0, 0.5)"></div>
            <div style="color: rgba(0, 0, 0, 0.5); font: bold 60px sans-serif; line-height: 70px">IIII</div>
            """);

        AssertColor(bitmap, 100, 20, 128, 0, 128);  // 50% red over blue.
        AssertColor(bitmap, 10, 20, 64, 128, 64);   // 50% green over that (the background extends under the border).
        var text = Pixels(bitmap, 0, 40, 300, 110).ToList();
        Assert.Contains(text, c => Near(c, 0, 0, 128));       // Glyphs: 50% black over blue ...
        Assert.DoesNotContain(text, c => c.Blue < 120);       // ... and nothing darker.
    }

    [Fact]
    public void Translucent_dashes_do_not_overlap_in_the_corners()
    {
        using var bitmap = Render("""<div style="height: 40px; border: 10px dashed rgba(255, 0, 0, 0.5)"></div>""");

        AssertColor(bitmap, 5, 5, 128, 0, 128);   // The corner, where the top and left dashes meet.
        AssertColor(bitmap, 15, 5, 128, 0, 128);  // A top dash.
        AssertColor(bitmap, 5, 15, 128, 0, 128);  // A left dash.
    }

    [Fact]
    public void Opacity_composites_the_element_with_its_children_as_one_group()
    {
        using var bitmap = Render("""
            <div style="opacity: 0.5; background: #f00; padding: 20px 0">
              <div style="background: #0f0; height: 40px"></div>
              <div style="background: #fff; height: 40px; margin-top: -20px"></div>
            </div>
            """);

        AssertColor(bitmap, 100, 10, 128, 0, 128);    // The element's own background.
        AssertColor(bitmap, 100, 30, 0, 128, 128);    // A child: green on top of the red, at 50%, without red.
        AssertColor(bitmap, 100, 50, 128, 128, 255);  // White overlapping green: just white at 50%.
        AssertColor(bitmap, 100, 70, 128, 128, 255);
        AssertColor(bitmap, 100, 90, 128, 0, 128);
    }

    [Fact]
    public void Nested_opacity_multiplies()
    {
        using var bitmap = Render("""
            <div style="opacity: 0.5"><div style="opacity: 0.5; background: #f00; height: 40px"></div></div>
            """);

        AssertColor(bitmap, 100, 20, 64, 0, 191);
    }

    [Fact]
    public void An_element_split_across_pages_gets_a_group_on_each_page()
    {
        var html = $"""<div style="opacity: 0.5; background: #f00">{string.Concat(Enumerable.Repeat("<div style='height: 50px'>x</div>", 12))}</div>""";
        var pdf = Pdf(html);
        var document = HtmlToPdf.Convert(html, Options());
        document.CompressContent = false;
        var text = System.Text.Encoding.Latin1.GetString(document.ToArray());

        Assert.Equal(2, Conversion.GetPageCount(pdf));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(text, @"q /GS\d+ gs /Fm\d+ Do Q")); // One group per page.
        for (var page = 0; page < 2; page++)
        {
            using var bitmap = Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: 96));
            AssertColor(bitmap, 100, 100, 128, 0, 128);
        }
    }

    [Fact]
    public void Stretched_flex_items_split_across_pages_are_groups_on_each_page()
    {
        // The stretched item's box is drawn once the row's end is known, after both items are laid out.
        var html = $"""
            <div style="display: flex">
              <div style="width: 50px">{string.Concat(Enumerable.Repeat("<div style='height: 50px'>x</div>", 12))}</div>
              <div style="opacity: 0.5; background: #f00; width: 50px"></div>
            </div>
            """;
        var pdf = Pdf(html);

        Assert.Equal(2, Conversion.GetPageCount(pdf));
        for (var page = 0; page < 2; page++)
        {
            using var bitmap = Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: 96));
            AssertColor(bitmap, 75, 100, 128, 0, 128);
        }
    }

    [Fact]
    public void Inline_blocks_and_table_cell_content_are_groups_too()
    {
        using var bitmap = Render("""
            <div style="height: 30px; margin-top: 60px"><span style="display: inline-block; opacity: 0.5; background: #f00; width: 50px; height: 20px"></span></div>
            <table style="border-spacing: 0"><tr><td style="padding: 0"><div style="opacity: 0.5; background: #f00; width: 50px; height: 20px"></div></td></tr></table>
            """);

        AssertColor(bitmap, 25, 70, 128, 0, 128);  // Inline block.
        AssertColor(bitmap, 25, 100, 128, 0, 128); // In a table cell.
    }

    [Fact]
    public void Images_and_inline_svg_take_opacity_and_translucent_colors()
    {
        using var bitmap = Render($"""
            <img src="{RedSvg}" style="display: block; width: 40px; height: 40px; opacity: 0.5">
            <p style="margin: 0; line-height: 0"><img src="{RedSvg}" style="width: 40px; height: 40px; opacity: 0.5"></p>
            <div style="color: rgba(255, 0, 0, 0.5)"><svg style="display: block" width="40" height="40"><rect width="40" height="40" fill="currentColor"/></svg></div>
            """);

        AssertColor(bitmap, 20, 20, 128, 0, 128);   // Block image.
        AssertColor(bitmap, 20, 60, 128, 0, 128);   // Inline image.
        AssertColor(bitmap, 20, 100, 128, 0, 128);  // The SVG's currentColor (the inherited CSS color) keeps the alpha.
    }

    // ---- helpers ----

    private static IReadOnlyList<Block> Blocks(string html) =>
        HtmlToPdf.CreateFlowDocument("<body style='margin: 0'>" + html, Options()).Blocks;

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);

    private static HtmlConverterOptions Options() => new() { Page = new PageSetup(new PdfPageSize(300, 300), Thickness.Zero) };

    /// <summary>The HTML on a blue body, on 300 x 300 point (400 x 400 pixel) pages without margins.</summary>
    private static byte[] Pdf(string html) =>
        HtmlToPdf.ConvertToBytes("<body style='margin: 0; background: #00f'>" + html, Options());

    private static SKBitmap Render(string html) => Conversion.ToImage(Pdf(html), page: 0, options: new RenderOptions(Dpi: 96));

    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    private static bool Near(SKColor color, int red, int green, int blue, int tolerance = 4) =>
        Math.Abs(red - color.Red) <= tolerance && Math.Abs(green - color.Green) <= tolerance && Math.Abs(blue - color.Blue) <= tolerance;

    private static void AssertColor(SKBitmap bitmap, int x, int y, int red, int green, int blue)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(Near(actual, red, green, blue), $"({x}, {y}): expected ({red}, {green}, {blue}), got {actual}");
    }
}
