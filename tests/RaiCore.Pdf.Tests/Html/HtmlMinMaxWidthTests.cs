using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// CSS <c>min-width</c> and <c>max-width</c> end to end. A 500 x 800 pt page with 50 pt margins and no body margin
/// gives 400 pt of content width starting at x = 50.
/// </summary>
public class HtmlMinMaxWidthTests
{
    private const double Px = 0.75;
    private const string NoBodyMargin = "<style>body { margin: 0 }</style>";
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    private static readonly PageSetup Page = new(new PdfPageSize(500, 800), Thickness.All(50));

    [Fact]
    public void A_max_width_block_with_auto_margins_is_centered()
    {
        var (flow, placements) = Layout("""<div style="max-width: 200px; margin: 0 auto; background: #ccc">a</div>""");
        var div = Box(flow, placements, "a");

        Assert.Equal((175.0, 150.0), (div.Left, div.Width)); // 50 + (400 - 150) / 2
    }

    [Theory]
    [InlineData("max-width: 50%", 200.0)]
    [InlineData("max-width: 100px; padding: 0 10px", (100 + 20) * Px)] // Content-box: padding adds to the limit.
    [InlineData("max-width: 100px; padding: 0 10px; box-sizing: border-box", 100 * Px)]
    [InlineData("width: 50%; min-width: 300px", 300 * Px)]
    [InlineData("min-width: 600px", 600 * Px)] // Wider than the page content: it overflows.
    [InlineData("width: 300px; max-width: 200px; min-width: 250px", 250 * Px)] // The minimum wins.
    public void Blocks_get_their_limited_width(string css, double expected)
    {
        var (flow, placements) = Layout($"""<div style="{css}; background: #ccc">a</div>""");

        Assert.Equal(expected, Box(flow, placements, "a").Width, 6);
    }

    [Theory]
    [InlineData("max-width: 200px", 75.0)] // Half of 150 pt.
    [InlineData("min-width: 600px", 225.0)] // Half of 450 pt.
    public void Children_resolve_percentages_against_the_limited_width(string css, double expected)
    {
        var (flow, placements) = Layout($"""<div style="{css}"><div style="width: 50%; background: #ccc">a</div></div>""");

        Assert.Equal(expected, Box(flow, placements, "a").Width, 6);
    }

    [Fact]
    public void Inline_blocks_shrink_to_fit_within_their_limits()
    {
        var (flow, _) = Layout("""
            <p><span style="display: inline-block; max-width: 100px">Some text that is much wider than a hundred pixels</span></p>
            <p><span style="display: inline-block; min-width: 100px">ab</span></p>
            """);
        var widths = Flatten(flow.Blocks).OfType<ParagraphBlock>().Where(p => p.Paragraph.Spans.Any(s => s.Box is not null))
            .Select(p => p.Paragraph.Layout(400).Lines[0].Boxes.Single().Width).ToList();

        Assert.Equal([100 * Px, 100 * Px], widths);
    }

    [Theory]
    [InlineData("max-width: 100%", 150.0, 150.0)] // A square 400 px wide in a 200 px box: 150 pt.
    [InlineData("max-width: 100px", 75.0, 75.0)]
    [InlineData("width: 40px; min-width: 80px", 60.0, 60.0)]
    [InlineData("width: 400px; height: 100px; max-width: 100px", 75.0, 75.0)] // Both sizes given: only the width changes.
    public void Inline_images_scale_to_their_limits(string css, double width, double height)
    {
        var (flow, _) = Layout($"""<div style="width: 200px"><img width="400" style="{css}" src="{Pixel}"></div>""");
        var image = Flatten(flow.Blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Image).Single(i => i is not null)!;

        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Theory]
    [InlineData(400, "max-width: 100%; height: auto", 150.0, 150.0)] // height: auto overrides the attribute: square again.
    [InlineData(400, "max-width: 100%", 150.0, 37.5)]                // The height attribute stays: stretched, as in browsers.
    [InlineData(100, "", 100 * Px, 50 * Px)]
    public void A_declared_height_overrides_the_height_attribute(int widthAttribute, string css, double width, double height)
    {
        var (flow, _) = Layout($"""<div style="width: 200px"><img width="{widthAttribute}" height="50" style="{css}" src="{Pixel}"></div>""");
        var image = Flatten(flow.Blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Image).Single(i => i is not null)!;

        Assert.Equal((width, height), (image.Width, image.Height));
    }

    [Fact]
    public void Block_images_scale_to_their_max_width_in_layout()
    {
        var (flow, placements) = Layout($"""<img style="display: block; max-width: 50%; height: auto; margin: 0 auto" width="1000" height="500" src="{Pixel}">""");
        var image = Flatten(flow.Blocks).OfType<ImageBlock>().Single();
        var placement = placements.Single(p => p.Block == image);

        Assert.Equal(200, image.Style.MaxWidth);
        Assert.Equal((150.0, 200.0, 200.0), (placement.Left, placement.Width, placement.Bottom - placement.Top)); // Square.
    }

    [Fact]
    public void Tables_shrink_to_fit_within_their_limits()
    {
        var (flow, placements) = Layout("""
            <table style="min-width: 100%; background: #ccc"><tr><td>a</td></tr></table>
            <table style="max-width: 100px; background: #ccc"><tr><td>Many words in a single table cell that would make it wide</td></tr></table>
            """);
        var tables = Flatten(flow.Blocks).OfType<TableBlock>().Select(t => placements.First(p => p.Block == t).Width).ToList();

        Assert.Equal([400, 100 * Px], tables);
    }

    private static (FlowDocument Flow, IReadOnlyList<Placement> Placements) Layout(string html)
    {
        var flow = HtmlToPdf.CreateFlowDocument(NoBodyMargin + html, new HtmlConverterOptions { Page = Page });
        return (flow, flow.Paginate().Placements);
    }

    private static Placement Box(FlowDocument flow, IReadOnlyList<Placement> placements, string text)
    {
        var block = Flatten(flow.Blocks).First(b => b is ParagraphBlock p && string.Concat(p.Paragraph.Spans.Select(s => s.Text)).Trim() == text);
        return placements.First(p => p.Block == block && p.FirstLine == -1); // The box, not its lines.
    }

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
