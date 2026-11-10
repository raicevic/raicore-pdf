using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>border-radius and background-clip from CSS to the box model.</summary>
public class HtmlBorderRadiusTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    // A 500 x 800 pt page with 50 pt margins: content 400 pt wide. Body margins are removed.
    private static readonly PageSetup Page = new(new PdfPageSize(500, 800), Thickness.All(50));

    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void Unset_is_square()
    {
        Assert.Equal(CornerRadii.None, Radii("color: red"));
        Assert.True(Radii("border-radius: 0").IsSquare);
    }

    [Theory]
    [InlineData("10px", "10 10, 10 10, 10 10, 10 10")]
    [InlineData("10px 20px", "10 10, 20 20, 10 10, 20 20")]
    [InlineData("10px 20px 30px", "10 10, 20 20, 30 30, 20 20")]
    [InlineData("10px 20px 30px 40px", "10 10, 20 20, 30 30, 40 40")]
    [InlineData("10px / 20px", "10 20, 10 20, 10 20, 10 20")]
    [InlineData("10px 20px / 5px 6px 7px", "10 5, 20 6, 10 7, 20 6")]
    [InlineData("1px 2px 3px 4px / 5px 6px 7px 8px", "1 5, 2 6, 3 7, 4 8")]
    public void The_shorthand_takes_one_to_four_values_and_an_elliptical_form(string value, string expected)
    {
        Assert.Equal(expected, Describe(Radii($"border-radius: {value}")));
    }

    [Fact]
    public void Longhands_set_one_corner_with_one_or_two_radii()
    {
        var radii = Radii("border-radius: 4px; border-top-left-radius: 10px 20px; border-bottom-right-radius: 2em; font-size: 10px");

        Assert.Equal("10 20, 4 4, 20 20, 4 4", Describe(radii));
    }

    [Fact]
    public void Percentages_stay_percentages_until_the_box_is_painted()
    {
        var radii = Radii("border-radius: 50%; border-top-right-radius: 10% 30%");

        Assert.Equal(CornerRadius.Percentage(50), radii.TopLeft);
        Assert.Equal(new CornerRadius(RadiusLength.FromPercent(10), RadiusLength.FromPercent(30)), radii.TopRight);
    }

    [Fact]
    public void Calc_with_a_percentage_keeps_both_parts_and_min_max_are_evaluated_against_the_width()
    {
        // The box is 200 px (150 pt) wide: min(10px, 50%) is 10 px.
        var radii = Radii("width: 200px; border-radius: calc(50% - 10px); border-top-right-radius: min(10px, 50%)");

        Assert.Equal(new RadiusLength(-10 * Px, 50), radii.TopLeft.Horizontal);
        Assert.Equal(RadiusLength.FromPoints(10 * Px), radii.TopRight.Horizontal);
        Assert.Equal(new RadiusLength(-10 * Px, 50), radii.BottomRight.Vertical);
    }

    [Fact]
    public void Negative_and_invalid_values_are_square()
    {
        Assert.True(Radii("border-radius: -5px").IsSquare);
        Assert.True(Radii("border-radius: auto").IsSquare);
        Assert.True(Radii("border-radius: 10px 0 0 0 / 0 0 0 0").IsSquare); // One radius 0: a square corner.
    }

    [Fact]
    public void Images_tables_and_cells_get_their_radii()
    {
        var flow = Flow($"""
            <p><img src="{Pixel}" style="width: 40px; height: 40px; border-radius: 50%"></p>
            <img src="{Pixel}" style="display: block; width: 40px; border-radius: 8px">
            <table style="border-radius: 6px"><tr><td style="border-radius: 4px">x</td></tr></table>
            """);
        var blocks = Flatten(flow.Blocks).ToList();

        var inline = blocks.OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Image).OfType<InlineImage>().Single();
        Assert.Equal(CornerRadii.All(CornerRadius.Percentage(50)), inline.BorderRadius);
        Assert.Equal(CornerRadii.All(8 * Px), blocks.OfType<ImageBlock>().Single().Style.BorderRadius);
        var table = blocks.OfType<TableBlock>().Single();
        Assert.Equal(CornerRadii.All(6 * Px), table.Style.BorderRadius);
        Assert.Equal(CornerRadii.All(4 * Px), table.Rows[0].Cells[0].Style.BorderRadius);
    }

    [Theory]
    [InlineData("", BackgroundClip.BorderBox)]
    [InlineData("background-clip: padding-box", BackgroundClip.PaddingBox)]
    [InlineData("background-clip: content-box", BackgroundClip.ContentBox)]
    [InlineData("background-clip: border-box, content-box", BackgroundClip.ContentBox)]
    public void Background_clip_follows_the_bottom_layer(string css, BackgroundClip expected)
    {
        Assert.Equal(expected, Box(css).Style.BackgroundClip);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void A_rounded_div_renders_with_clear_corners()
    {
        var pdf = HtmlToPdf.Convert(
            "<style>body { margin: 0 }</style><div style='background: #f00; width: 200px; height: 100px; border-radius: 20px / 40px'></div>",
            new HtmlConverterOptions { Page = Page }).ToArray();
        using var page = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        // The box is 150 x 75 pt at (50, 50); its corners have 15 pt horizontal and 30 pt vertical radii.
        Assert.Equal(255, page.GetPixel(51, 51).Green); // White outside the curve.
        Assert.Equal(255, page.GetPixel(52, 60).Green); // Still outside (inside a 15 pt circle): the curve is taller than wide.
        Assert.True(page.GetPixel(60, 60).Green < 60 && page.GetPixel(60, 60).Red > 200);
        Assert.True(page.GetPixel(125, 51).Green < 60, "The top edge between the corners.");
    }

    private static FlowDocument Flow(string html) =>
        HtmlToPdf.CreateFlowDocument("<style>body { margin: 0 }</style>" + html, new HtmlConverterOptions { Page = Page });

    /// <summary>The div's block: the innermost one with a style (or its paragraph, when it has none).</summary>
    private static Block Box(string css)
    {
        var blocks = Flatten(Flow($"<div style=\"{css}\">x</div>").Blocks).ToList();
        return blocks.LastOrDefault(b => b.Style != BoxStyle.Default) ?? blocks.OfType<ParagraphBlock>().First();
    }

    private static CornerRadii Radii(string css) => Box(css).Style.BorderRadius;

    /// <summary>Each corner's radii in CSS pixels: "h v" per corner, clockwise from the top left.</summary>
    private static string Describe(CornerRadii radii) =>
        string.Join(", ", new[] { radii.TopLeft, radii.TopRight, radii.BottomRight, radii.BottomLeft }.Select(c =>
            FormattableString.Invariant($"{c.Horizontal.Points / Px:0.##} {c.Vertical.Points / Px:0.##}")));

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            TableBlock t => t.Rows.SelectMany(r => r.Cells).SelectMany(cell => Flatten(cell.Content)).Prepend(b),
            _ => [b],
        });
}
