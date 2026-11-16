using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Percentages in a <see cref="BoxStyle"/> (<see cref="BoxStyle.HasPercent"/>): sizes resolved against the width the containing block gets in layout. Uses a
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
        var child = Rect(new BoxStyle { Width = Length.FromPercent(50) });
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
                MaxWidth = new Length(20, 25), // 20 pt + 25% = 60 pt.
                Margin = new Thickness(0, 0, 0, Length.FromPercent(10)),
                Padding = new Thickness(Length.FromPercent(5), 0, 0, 0),
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
            Width = new Length(-50, 10),
            Padding = new Thickness(new Length(-10, 5), 3, 0, 1),
        };

        var resolved = style.Resolve(100);

        Assert.Equal(new Thickness(0, 3, 0, 1), resolved.Padding);
        Assert.Equal(3 + 1 + 4, resolved.Width); // Not -40 pt: at least the padding and border.
        Assert.False(resolved.HasPercent);
    }

    [Fact]
    public void Intrinsic_sizes_treat_percentage_widths_as_auto()
    {
        var style = new BoxStyle
        {
            Width = Length.FromPercent(50),
            MinWidth = 30,
            MaxWidth = new Length(10, 50),
            Margin = new Thickness(new Length(4, 10), 0, 0, 0),
        };

        var intrinsic = style.Intrinsic();

        Assert.Equal((null, (Length?)30, null), (intrinsic.Width, intrinsic.MinWidth, intrinsic.MaxWidth));
        Assert.Equal(4, intrinsic.Margin.Top);
    }

    [Fact]
    public void A_float_shrinks_to_its_content_and_its_child_takes_a_share_of_it()
    {
        var words = new ParagraphBlock(new Paragraph(Text).Add("Some words"), new BoxStyle { Background = Gray });
        var half = Rect(new BoxStyle { Width = Length.FromPercent(50) });
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

    [Fact]
    public void Lengths_add_scale_and_resolve()
    {
        Length points = 12;
        var mixed = points + Length.FromPercent(50) - 2;

        Assert.Equal(Length.FromPoints(12), points);
        Assert.Equal(new Length(10, 50), mixed);
        Assert.Equal(110, mixed.Resolve(200), 9);
        Assert.Equal(new Length(5, 25), mixed * 0.5);
        Assert.Equal((true, false), (mixed.HasPercent, points.HasPercent));
        Assert.Equal(("12pt", "50%", "calc(50% - 2pt)"), (points.ToString(), Length.FromPercent(50).ToString(), new Length(-2, 50).ToString()));
        Assert.Equal(new Thickness(20, 10, 20, 10), new Thickness(Length.FromPercent(10), 10, Length.FromPercent(10), 10).Resolve(200));
    }

    [Fact]
    public void Page_margins_in_percent_are_of_the_page_width_and_height()
    {
        var setup = new PageSetup(new PdfPageSize(200, 400), new Thickness(Length.FromPercent(10), 0, 0, Length.FromPercent(10)));
        var block = Rect(BoxStyle.Default);
        var placement = Box(new FlowDocument(setup).Add(block).Paginate().Placements, block);

        Assert.Equal((180.0, 360.0), (setup.ContentWidth, setup.ContentHeight));
        Assert.Equal((20.0, 40.0, 180.0), (placement.Left, placement.Top, placement.Width));
    }

    [Fact]
    public void An_inline_boxs_percentage_margin_is_of_the_paragraphs_width()
    {
        var box = new ContainerBlock(new BoxStyle { Width = 20, Margin = new Thickness(0, 0, 0, Length.FromPercent(10)) }, new SpacerBlock(10));
        var layout = new Paragraph(Text).AddBox(box).Add("x").Layout(160);

        Assert.Equal(16 + 20, layout.Lines[0].Boxes.Single().OuterWidth, 6);
    }

    [Fact]
    public void A_first_childs_percentage_top_margin_collapses_through_its_parent()
    {
        // 10% of the parent's 100 pt content width: the parent (no border or padding) and its child start 10 pt down.
        var child = Rect(new BoxStyle { Margin = new Thickness(Length.FromPercent(10), 0, 0, 0) });
        var parent = new ContainerBlock(new BoxStyle { Width = 100, Background = Gray }, child);
        var placements = Layout(new ContainerBlock(null, parent));

        Assert.Equal((30.0, 30.0), (Box(placements, parent).Top, Box(placements, child).Top));
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
