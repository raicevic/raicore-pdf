using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// <see cref="BoxStyle.MinWidth"/> and <see cref="BoxStyle.MaxWidth"/> in the layout engine. Uses a 200 x 200 pt
/// page with 20 pt margins: the content area is 160 pt wide starting at x = 20. Boxes have backgrounds so their
/// placements are recorded.
/// </summary>
public class MinMaxWidthTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    // 64 x 48 px (48 x 36 pt at 96 dpi).
    private static readonly PdfImage Quadrants = PdfImage.Load(Fixtures.Path(Path.Combine("Images", "rgb.jpg")));

    [Theory]
    [InlineData(null, 100.0, null, 100.0)]  // Fills the available width up to the maximum.
    [InlineData(null, 300.0, null, 160.0)]  // A maximum wider than the space changes nothing.
    [InlineData(120.0, 80.0, null, 80.0)]   // Caps a fixed width.
    [InlineData(50.0, null, 70.0, 70.0)]    // Raises a fixed width.
    [InlineData(50.0, 60.0, 80.0, 80.0)]    // The minimum wins over the maximum.
    [InlineData(null, null, 250.0, 250.0)]  // Wider than the space: the box overflows, as in CSS.
    public void Blocks_are_limited_by_their_min_and_max_widths(double? width, double? max, double? min, double expected)
    {
        var block = Rect(new BoxStyle { Width = width, MaxWidth = max, MinWidth = min });

        Assert.Equal((20.0, expected), Horizontal(Layout(block), block));
    }

    [Fact]
    public void The_content_of_a_narrowed_block_wraps_at_its_max_width()
    {
        var box = new ContainerBlock(new BoxStyle { MaxWidth = 60, Padding = Thickness.All(5), Background = Gray }, new ParagraphBlock(Words()));
        var placement = Box(Layout(box), box);

        Assert.Equal(60, placement.Width);
        Assert.Equal(Words().Layout(50).Height + 10, placement.Bottom - placement.Top, 6); // 50 pt of content width.
    }

    [Theory]
    [InlineData(24.0, null, 24.0, 18.0)]  // Scaled down, keeping the 4:3 aspect ratio.
    [InlineData(null, 96.0, 96.0, 72.0)]  // Scaled up.
    [InlineData(null, 400.0, 160.0, 120.0)] // Scaled up, then down to fit the page.
    public void Images_scale_to_their_min_and_max_widths(double? max, double? min, double expectedWidth, double expectedHeight)
    {
        var image = new ImageBlock(Quadrants, style: new BoxStyle { MaxWidth = max, MinWidth = min });
        var placement = Box(Layout(image), image);

        Assert.Equal((expectedWidth, expectedHeight), (placement.Width, placement.Bottom - placement.Top));
    }

    [Fact]
    public void Image_limits_include_the_border_and_padding()
    {
        var image = new ImageBlock(Quadrants, style: new BoxStyle { MaxWidth = 34, Padding = Thickness.All(5) });
        var placement = Box(Layout(image), image);

        Assert.Equal((24.0, 18.0), (placement.Width, placement.Bottom - placement.Top)); // 34 - 2 * 5 of content.
    }

    [Theory]
    [InlineData(null, 40.0, 40.0)]
    [InlineData(10.0, null, 10.0)]
    public void A_stretched_image_keeps_its_height_when_the_limits_change_its_width(double? max, double? min, double expectedWidth)
    {
        var image = new ImageBlock(Quadrants, width: 20, height: 30, new BoxStyle { MaxWidth = max, MinWidth = min });
        var placement = Box(Layout(image), image);

        Assert.Equal((expectedWidth, 30.0), (placement.Width, placement.Bottom - placement.Top));
    }

    [Fact]
    public void A_centered_image_is_centered_at_its_max_width()
    {
        var image = new ImageBlock(Quadrants, style: new BoxStyle { MaxWidth = 24 }) { Alignment = TextAlignment.Center };

        Assert.Equal((88.0, 24.0), Horizontal(Layout(image), image)); // 20 + (160 - 24) / 2
    }

    [Theory]
    [InlineData(null, 150.0, 150.0)] // A minimum widens a shrink-to-fit table.
    [InlineData(80.0, null, 80.0)]   // A maximum narrows it (its text wraps).
    [InlineData(5.0, null, -1.0)]    // But not below its content's minimum width.
    public void Tables_shrink_to_fit_within_their_min_and_max_widths(double? max, double? min, double expected)
    {
        var table = new TableBlock(new BoxStyle { MaxWidth = max, MinWidth = min, Background = Gray })
            .Add(new TableRow(new TableCell(new Paragraph(Text).Add("Several words in one cell")), new TableCell(new Paragraph(Text).Add("x"))));
        var width = Horizontal(Layout(table), table).Width;

        if (expected < 0)
        {
            Assert.Equal(Paginator.MeasureIntrinsic(new TableBlock().Add(new TableRow(
                new TableCell(new Paragraph(Text).Add("Several words in one cell")), new TableCell(new Paragraph(Text).Add("x"))))).Min, width, 6);
        }
        else
        {
            Assert.Equal(expected, width, 6);
        }
    }

    [Theory]
    [InlineData(50.0, null, 50.0)]  // Long text wraps at the maximum instead of filling the line.
    [InlineData(null, 40.0, 40.0)]  // Short text gets the minimum.
    public void Inline_boxes_shrink_to_fit_within_their_min_and_max_widths(double? max, double? min, double expected)
    {
        var content = new ParagraphBlock(new Paragraph(Text).Add(min is null ? "Many words that do not fit in fifty points" : "ab"), new BoxStyle { MaxWidth = max, MinWidth = min });
        var line = new Paragraph(Text).AddBox(content).Layout(160).Lines[0];

        Assert.Equal(expected, Assert.Single(line.Boxes).Width, 6);
    }

    [Fact]
    public void Intrinsic_widths_respect_the_limits()
    {
        // An inline box shrinks to its widest child, which is capped by its max width.
        var capped = new ContainerBlock(null, new ParagraphBlock(new Paragraph(Text).Add("Many words that would make a wide box"), new BoxStyle { MaxWidth = 70 }));
        var raised = new ContainerBlock(null, new ParagraphBlock(new Paragraph(Text).Add("ab"), new BoxStyle { MinWidth = 90 }));

        Assert.Equal(70, Paginator.MeasureIntrinsic(capped).Max, 6);
        Assert.Equal((90.0, 90.0), Paginator.MeasureIntrinsic(raised));
    }

    [Fact]
    public void A_growing_flex_item_stops_at_its_max_width()
    {
        var (a, b) = (Rect(new BoxStyle { MaxWidth = 50 }), Rect(new BoxStyle()));
        var placements = Layout(new FlexBlock().Add(new FlexItem(a) { Grow = 1 }).Add(new FlexItem(b) { Grow = 1 }));

        Assert.Equal((20.0, 50.0), Horizontal(placements, a));
        Assert.Equal((70.0, 110.0), Horizontal(placements, b)); // Takes what a cannot.
    }

    [Fact]
    public void A_shrinking_flex_item_stops_at_its_min_width()
    {
        var (a, b) = (Rect(new BoxStyle { Width = 100, MinWidth = 90 }), Rect(new BoxStyle { Width = 100 }));
        var placements = Layout(new FlexBlock().Add(a).Add(b));

        Assert.Equal(90, Horizontal(placements, a).Width, 6);
        Assert.Equal(70, Horizontal(placements, b).Width, 6);
    }

    [Fact]
    public void Column_items_are_limited_across_the_container()
    {
        var (a, b) = (Rect(new BoxStyle { MaxWidth = 60 }), Rect(new BoxStyle { MinWidth = 180 }));
        var placements = Layout(new FlexBlock { Direction = FlexDirection.Column }.Add(a).Add(b));

        Assert.Equal(60, Horizontal(placements, a).Width, 6); // Stretched up to its maximum.
        Assert.Equal(180, Horizontal(placements, b).Width, 6);
    }

    private static Paragraph Words() => new Paragraph(Text).Add("Several words that wrap onto more lines when narrow");

    private static ContainerBlock Rect(BoxStyle style) => new(style with { Background = Gray }, new SpacerBlock(10));

    private static IReadOnlyList<Placement> Layout(Block block) => new FlowDocument(Small).Add(block).Paginate().Placements;

    private static Placement Box(IReadOnlyList<Placement> placements, Block block) => placements.First(p => p.Block == block);

    private static (double Left, double Width) Horizontal(IReadOnlyList<Placement> placements, Block block)
    {
        var box = Box(placements, block);
        return (box.Left, box.Width);
    }
}
