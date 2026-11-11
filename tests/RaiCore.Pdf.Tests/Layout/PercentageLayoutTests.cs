using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// <see cref="BoxStyle.Percentages"/>: sizes resolved against the width the containing block gets in layout. Uses a
/// 200 x 200 pt page with 20 pt margins: the content area is 160 pt wide starting at x = 20. Boxes have backgrounds so
/// their placements are recorded.
/// </summary>
public class PercentageLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    [Fact]
    public void A_flex_items_child_resolves_its_percentage_against_the_flexed_width()
    {
        // Two 160 pt items shrink to 80 pt each; the child is half of that, whatever its points width says.
        var child = Rect(new BoxStyle { Width = 999, Percentages = new BoxPercentages { Width = BoxLength.FromPercent(50) } });
        var item = new ContainerBlock(new BoxStyle { Background = Gray }, child);
        var flex = new FlexBlock()
            .Add(new FlexItem(item) { Basis = FlexBasis.Points(160) })
            .Add(new FlexItem(Rect(BoxStyle.Default)) { Basis = FlexBasis.Points(160) });
        var placements = Layout(flex);

        Assert.Equal((20.0, 80.0), Horizontal(placements, item));
        Assert.Equal((20.0, 40.0), Horizontal(placements, child));
    }

    [Fact]
    public void Margins_padding_and_limits_resolve_against_the_containing_width()
    {
        var child = new ContainerBlock(
            new BoxStyle
            {
                Background = Gray,
                Percentages = new BoxPercentages
                {
                    MaxWidth = new BoxLength(20, 25), // 20 pt + 25% = 60 pt.
                    Margin = new RelativeThickness(default, default, default, BoxLength.FromPercent(10)),
                    Padding = new RelativeThickness(BoxLength.FromPercent(5), default, default, default),
                },
            },
            new SpacerBlock(10));
        var placements = Layout(new ContainerBlock(new BoxStyle { Width = 160 }, child));
        var box = Box(placements, child);

        Assert.Equal((36.0, 60.0), (box.Left, box.Width));
        Assert.Equal(8 + 10, box.Bottom - box.Top, 6); // 5% of 160 pt above the spacer.
    }

    [Fact]
    public void Resolving_keeps_padding_positive_and_border_boxes_around_it()
    {
        var style = new BoxStyle
        {
            Border = Borders.All(2, PdfColor.Black),
            Percentages = new BoxPercentages
            {
                Width = new BoxLength(-50, 10),
                Padding = new RelativeThickness(new BoxLength(-10, 5), new BoxLength(3), default, new BoxLength(1)),
            },
        };

        var resolved = style.Resolve(100);

        Assert.Equal(new Thickness(0, 3, 0, 1), resolved.Padding);
        Assert.Equal(3 + 1 + 4, resolved.Width); // Not -40 pt: at least the padding and border.
        Assert.Null(resolved.Percentages);
    }

    [Fact]
    public void Intrinsic_sizes_treat_percentage_widths_as_auto()
    {
        var style = new BoxStyle
        {
            Width = 80,
            MaxWidth = 90,
            Percentages = new BoxPercentages
            {
                Width = BoxLength.FromPercent(50),
                MinWidth = new BoxLength(30),
                MaxWidth = new BoxLength(10, 50),
                Margin = new RelativeThickness(new BoxLength(4, 10), default, default, default),
            },
        };

        var intrinsic = style.Intrinsic();

        Assert.Equal((null, 30.0, null), (intrinsic.Width, intrinsic.MinWidth, intrinsic.MaxWidth));
        Assert.Equal(4, intrinsic.Margin.Top);
    }

    [Fact]
    public void A_float_shrinks_to_its_content_and_its_child_takes_a_share_of_it()
    {
        var words = new ParagraphBlock(new Paragraph(Text).Add("Some words"), new BoxStyle { Background = Gray });
        var half = Rect(new BoxStyle { Percentages = new BoxPercentages { Width = BoxLength.FromPercent(50) } });
        var floated = new ContainerBlock(new BoxStyle { Float = FloatSide.Left, Background = Gray }, words, half);
        var placements = Layout(new ContainerBlock(null, floated, new ParagraphBlock(new Paragraph(Text).Add("After"))));
        var width = new Paragraph(Text).Add("Some words").MeasureIntrinsicWidths().Max;

        Assert.Equal(width, Box(placements, floated).Width, 6);
        Assert.Equal(width / 2, Box(placements, half).Width, 6);
    }

    [Fact]
    public void Blocks_without_percentages_keep_their_style()
    {
        var style = new BoxStyle { Width = 50, Padding = Thickness.All(3) };

        Assert.Same(style, style.Resolve(100));
        Assert.Same(style, style.Intrinsic());
    }

    private static ContainerBlock Rect(BoxStyle style) => new(style with { Background = Gray }, new SpacerBlock(10));

    private static IReadOnlyList<Placement> Layout(Block block) => new FlowDocument(Small).Add(block).Paginate().Placements;

    private static Placement Box(IReadOnlyList<Placement> placements, Block block) => placements.First(p => p.Block == block && p.FirstLine == -1);

    private static (double Left, double Width) Horizontal(IReadOnlyList<Placement> placements, Block block)
    {
        var box = Box(placements, block);
        return (box.Left, box.Width);
    }
}
