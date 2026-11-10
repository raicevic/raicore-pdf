using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Block heights and flex containers with a definite size. Uses a 200 x 200 pt page with 20 pt margins: the content
/// area is x 20-180, y 20-180 (160 pt each way), and 10 pt lines.
/// </summary>
public class HeightLayoutTests
{
    private const double Top = 20;

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    // ---- Blocks ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fixed_height_box_takes_that_height_and_content_overflows_it()
    {
        var (tall, low) = (Box(new BoxStyle { Height = 50 }, Para(1)), Box(new BoxStyle { Height = 5 }, Para(2)));
        var after = Para(1);
        var placements = Layout(tall, low, after);

        Assert.Equal((20.0, 70.0), Span(placements, tall));
        Assert.Equal((70.0, 75.0), Span(placements, low));
        Assert.Equal(75, placements.Single(p => p.Block == after).Top); // Overlaps the overflowing text, as in CSS.
    }

    [Fact]
    public void Min_and_max_height_limit_the_content_height()
    {
        var (min, max) = (Box(new BoxStyle { MinHeight = 40 }, Para(1)), Box(new BoxStyle { MaxHeight = 15 }, Para(3)));
        var placements = Layout(min, max);

        Assert.Equal((20.0, 60.0), Span(placements, min));
        Assert.Equal((60.0, 75.0), Span(placements, max));
    }

    [Fact]
    public void Min_height_wins_over_max_height()
    {
        var box = Box(new BoxStyle { MinHeight = 30, MaxHeight = 10, Height = 20 }, Para(1));

        Assert.Equal((20.0, 50.0), Span(Layout(box), box));
    }

    [Fact]
    public void An_empty_box_with_a_height_takes_space()
    {
        var empty = new ContainerBlock(new BoxStyle { Height = 30 });
        var after = Para(1);

        Assert.Equal(50, Layout(empty, after).Single(p => p.Block == after).Top);
    }

    [Fact]
    public void A_fixed_height_box_moves_to_the_next_page_as_a_whole()
    {
        var box = Box(new BoxStyle { Height = 50 }, Para(1));
        var placements = Layout(Para(14), box);

        Assert.Equal((1, Top, 70.0), placements.Where(p => p.Block == box).Select(p => (p.Page, p.Top, p.Bottom)).Single());
    }

    [Fact]
    public void A_min_height_box_split_across_pages_grows_its_last_fragment()
    {
        // 140 pt are used on the first page; 20 lines need 200 pt (140 + 60), min-height asks for 250 in total.
        var box = Box(new BoxStyle { MinHeight = 250 }, Para(20));
        var placements = Layout(Para(2), box).Where(p => p.Block == box && p.FirstLine == -1).ToList();

        Assert.Equal([(0, 40.0, 180.0), (1, 20.0, 130.0)], placements.Select(p => (p.Page, p.Top, p.Bottom)));
    }

    [Fact]
    public void A_table_height_is_a_minimum()
    {
        var table = new TableBlock(new BoxStyle { Height = 60, Background = Gray }).Add(new TableRow(new TableCell(new Paragraph(Text).Add("x"))));

        Assert.Equal((20.0, 80.0), Span(Layout(table).Where(p => p.FirstLine == -1).ToList(), table));
    }

    // ---- Flex columns with a height -------------------------------------------------------------------------------

    [Fact]
    public void Column_items_grow_into_the_container_height()
    {
        var (a, b, c) = (Rect(null, 10), Rect(null, 10), Rect(null, 10));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 160 }) { Direction = FlexDirection.Column }
            .Add(a).Add(new FlexItem(b) { Grow = 1 }).Add(c));

        Assert.Equal((20.0, 30.0), Span(placements, a));
        Assert.Equal((30.0, 170.0), Span(placements, b));
        Assert.Equal((170.0, 180.0), Span(placements, c));
    }

    [Theory]
    [InlineData(JustifyContent.FlexStart, 20, 30)]
    [InlineData(JustifyContent.Center, 60, 70)]
    [InlineData(JustifyContent.FlexEnd, 100, 110)]
    [InlineData(JustifyContent.SpaceBetween, 20, 110)]
    public void Column_items_are_justified_vertically(JustifyContent justify, double first, double second)
    {
        var (a, b) = (Rect(40, 10), Rect(40, 10));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { Direction = FlexDirection.Column, JustifyContent = justify }.Add(a).Add(b));

        Assert.Equal(first, Span(placements, a).Top);
        Assert.Equal(second, Span(placements, b).Top);
    }

    [Fact]
    public void An_auto_top_margin_pushes_a_footer_to_the_bottom()
    {
        var (header, footer) = (Rect(null, 10), Rect(null, 10));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 160 }) { Direction = FlexDirection.Column }
            .Add(header).Add(new FlexItem(footer) { AutoMargins = AutoMargins.Top }));

        Assert.Equal((20.0, 30.0), Span(placements, header));
        Assert.Equal((170.0, 180.0), Span(placements, footer));
    }

    [Fact]
    public void Column_reverse_starts_at_the_bottom()
    {
        var (a, b) = (Rect(40, 10), Rect(40, 20));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { Direction = FlexDirection.ColumnReverse }.Add(a).Add(b));

        Assert.Equal((110.0, 120.0), Span(placements, a));
        Assert.Equal((90.0, 110.0), Span(placements, b));
    }

    [Fact]
    public void Min_height_gives_a_column_room_to_grow()
    {
        var (a, b) = (Rect(null, 10), Rect(null, 10));
        var container = new FlexBlock(new BoxStyle { MinHeight = 100, Background = Gray }) { Direction = FlexDirection.Column }
            .Add(new FlexItem(a) { Grow = 1 }).Add(b);
        var placements = Layout(container);

        Assert.Equal((20.0, 120.0), Span(placements, container));
        Assert.Equal((20.0, 110.0), Span(placements, a));
    }

    [Fact]
    public void Column_items_shrink_but_not_below_their_content()
    {
        // Both ask for 80 in a 100 container: they shrink to 50 each. The third cannot shrink below its 30 pt content.
        var (a, b) = (Box(new BoxStyle { Height = 80, Background = Gray }, new SpacerBlock(10)), Box(new BoxStyle { Height = 80, Background = Gray }, new SpacerBlock(10)));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { Direction = FlexDirection.Column }.Add(a).Add(b));

        Assert.Equal((20.0, 70.0), Span(placements, a));
        Assert.Equal((70.0, 120.0), Span(placements, b));

        var content = Box(new BoxStyle { Height = 80, Background = Gray }, new SpacerBlock(70));
        var other = Box(new BoxStyle { Height = 80, Background = Gray }, new SpacerBlock(10));
        var shrunk = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { Direction = FlexDirection.Column }.Add(content).Add(other));
        Assert.Equal((20.0, 90.0), Span(shrunk, content));
        Assert.Equal((90.0, 120.0), Span(shrunk, other));
    }

    [Fact]
    public void Percent_basis_in_a_column_refers_to_the_container_height()
    {
        var (a, b) = (Rect(null, 10), Rect(null, 10));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { Direction = FlexDirection.Column }
            .Add(new FlexItem(a) { Basis = FlexBasis.Percent(30), Grow = 1 })
            .Add(new FlexItem(b) { Basis = FlexBasis.Percent(10), Grow = 1 }));

        // 40 pt of bases, 60 pt shared equally.
        Assert.Equal((20.0, 80.0), Span(placements, a));
        Assert.Equal((80.0, 120.0), Span(placements, b));
    }

    [Fact]
    public void A_column_wraps_into_columns_side_by_side()
    {
        var (a, b, c) = (Rect(40, 20), Rect(40, 20), Rect(50, 20));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 45 }) { Direction = FlexDirection.Column, Wrap = FlexWrap.Wrap, AlignContent = AlignContent.FlexStart, ColumnGap = 10, RowGap = 5 }
            .Add(a).Add(b).Add(c));

        Assert.Equal((20.0, 20.0), (Horizontal(placements, a), Span(placements, a).Top));
        Assert.Equal((20.0, 45.0), (Horizontal(placements, b), Span(placements, b).Top));
        Assert.Equal((70.0, 20.0), (Horizontal(placements, c), Span(placements, c).Top));
    }

    [Fact]
    public void A_grown_item_is_a_definite_height_for_its_own_flex_content()
    {
        // The cover page pattern: main grows between header and footer and centers its content.
        var title = Rect(40, 10);
        var main = new FlexBlock { Direction = FlexDirection.Column, JustifyContent = JustifyContent.Center, AlignItems = FlexAlignment.Center }.Add(title);
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 160 }) { Direction = FlexDirection.Column }
            .Add(Rect(null, 10)).Add(new FlexItem(main) { Grow = 1 }).Add(Rect(null, 10)));

        Assert.Equal((95.0, 105.0), Span(placements, title)); // Centered in 30-170.
    }

    [Fact]
    public void A_stretched_item_is_a_definite_height_for_its_own_flex_content()
    {
        var bottom = Rect(40, 10);
        var card = new FlexBlock { Direction = FlexDirection.Column, JustifyContent = JustifyContent.FlexEnd }.Add(bottom);
        var placements = Layout(new FlexBlock().Add(Rect(40, 80)).Add(card));

        Assert.Equal((90.0, 100.0), Span(placements, bottom)); // At the bottom of the 80 pt line.
    }

    // ---- Flex rows with a height ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(FlexAlignment.Stretch, 20, 120)]
    [InlineData(FlexAlignment.Center, 65, 75)]
    [InlineData(FlexAlignment.FlexEnd, 110, 120)]
    public void A_single_line_fills_the_container_height(FlexAlignment align, double top, double bottom)
    {
        var item = Rect(40, 10);
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { AlignItems = align }.Add(item));

        Assert.Equal((top, bottom), Span(placements, item));
    }

    [Theory]
    [InlineData(AlignContent.FlexStart, 20, 30, 30)]
    [InlineData(AlignContent.FlexEnd, 100, 110, 110)]
    [InlineData(AlignContent.Center, 60, 70, 70)]
    [InlineData(AlignContent.SpaceBetween, 20, 110, 30)]
    [InlineData(AlignContent.Stretch, 20, 70, 70)]
    public void Align_content_shares_the_extra_height_between_lines(AlignContent align, double firstTop, double secondTop, double firstBottom)
    {
        var (a, b) = (Rect(100, 10), Rect(100, 10));
        var placements = Layout(new FlexBlock(new BoxStyle { Height = 100 }) { Wrap = FlexWrap.Wrap, AlignContent = align }.Add(a).Add(b));

        Assert.Equal(firstTop, Span(placements, a).Top);
        Assert.Equal(firstBottom, Span(placements, a).Bottom); // Stretched with its line under align-content: stretch.
        Assert.Equal(secondTop, Span(placements, b).Top);
    }

    [Fact]
    public void Max_height_clamps_a_rows_container_but_not_its_overflowing_lines()
    {
        var container = new FlexBlock(new BoxStyle { MaxHeight = 15, Background = Gray }).Add(Rect(40, 30));
        var after = Para(1);
        var placements = Layout(container, after);

        Assert.Equal((20.0, 35.0), Span(placements, container));
        Assert.Equal(35, placements.Single(p => p.Block == after).Top);
    }

    private static ContainerBlock Rect(double? width, double height) =>
        new(new BoxStyle { Width = width, Background = Gray }, new SpacerBlock(height));

    private static ContainerBlock Box(BoxStyle style, params Block[] children) =>
        new(style with { Background = style.Background ?? Gray }, children);

    private static ParagraphBlock Para(int lines) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Line {i}"))));

    private static IReadOnlyList<Placement> Layout(params Block[] blocks)
    {
        var flow = new FlowDocument(Small);
        foreach (var block in blocks)
        {
            flow.Add(block);
        }

        return flow.Paginate().Placements;
    }

    private static (double Top, double Bottom) Span(IReadOnlyList<Placement> placements, Block block)
    {
        var box = placements.Single(p => p.Block == block);
        return (box.Top, box.Bottom);
    }

    private static double Horizontal(IReadOnlyList<Placement> placements, Block block) => placements.Single(p => p.Block == block).Left;
}
