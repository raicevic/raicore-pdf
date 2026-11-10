using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Uses a 200 x 200 pt page with 20 pt margins: the content area is x 20-180, y 20-180, and 10 pt lines,
/// so 16 lines fit on a page. Boxes have backgrounds so their placements are recorded.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class FlexLayoutTests
{
    private const double Left = 20;
    private const double Top = 20;

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    [Fact]
    public void Grow_fills_the_line_and_fixed_items_keep_their_width()
    {
        var a = Rect(null, 10);
        var b = Rect(40, 10);
        var placements = Layout(new FlexBlock().Add(new FlexItem(a) { Grow = 1 }).Add(b));

        Assert.Equal((Left, 120.0), Horizontal(placements, a));
        Assert.Equal((140.0, 40.0), Horizontal(placements, b));
    }

    [Theory]
    [InlineData(JustifyContent.FlexStart, 20, 60)]
    [InlineData(JustifyContent.FlexEnd, 100, 140)]
    [InlineData(JustifyContent.Center, 60, 100)]
    [InlineData(JustifyContent.SpaceBetween, 20, 140)]
    [InlineData(JustifyContent.SpaceAround, 40, 120)]
    [InlineData(JustifyContent.SpaceEvenly, 20 + (80.0 / 3), 60 + (160.0 / 3))]
    public void Justify_content_positions_items(JustifyContent justify, double first, double second)
    {
        var (a, b) = (Rect(40, 10), Rect(40, 10));
        var placements = Layout(new FlexBlock { JustifyContent = justify }.Add(a).Add(b));

        Assert.Equal(first, Horizontal(placements, a).Left, 6);
        Assert.Equal(second, Horizontal(placements, b).Left, 6);
    }

    [Fact]
    public void Row_reverse_starts_from_the_right()
    {
        var (a, b) = (Rect(40, 10), Rect(40, 10));
        var placements = Layout(new FlexBlock { Direction = FlexDirection.RowReverse }.Add(a).Add(b));

        Assert.Equal(140, Horizontal(placements, a).Left);
        Assert.Equal(100, Horizontal(placements, b).Left);
    }

    [Fact]
    public void Order_rearranges_items()
    {
        var (a, b) = (Rect(40, 10), Rect(40, 10));
        var placements = Layout(new FlexBlock().Add(new FlexItem(a) { Order = 2 }).Add(new FlexItem(b) { Order = 1 }));

        Assert.Equal(60, Horizontal(placements, a).Left);
        Assert.Equal(20, Horizontal(placements, b).Left);
    }

    [Fact]
    public void Items_wrap_onto_lines_separated_by_the_row_gap()
    {
        var (a, b, c) = (Rect(70, 10), Rect(70, 10), Rect(70, 10));
        var placements = Layout(new FlexBlock { Wrap = FlexWrap.Wrap, ColumnGap = 10, RowGap = 5 }.Add(a).Add(b).Add(c));

        Assert.Equal((20.0, Top), (Horizontal(placements, a).Left, Box(placements, a).Top));
        Assert.Equal((100.0, Top), (Horizontal(placements, b).Left, Box(placements, b).Top));
        Assert.Equal((20.0, 35.0), (Horizontal(placements, c).Left, Box(placements, c).Top));
    }

    [Fact]
    public void Wrap_reverse_stacks_lines_upwards()
    {
        var (a, b, c) = (Rect(70, 10), Rect(70, 10), Rect(70, 10));
        var placements = Layout(new FlexBlock { Wrap = FlexWrap.WrapReverse, ColumnGap = 10 }.Add(a).Add(b).Add(c));

        Assert.Equal(Top, Box(placements, c).Top);
        Assert.Equal(30, Box(placements, a).Top);
    }

    [Fact]
    public void Stretched_items_are_as_tall_as_the_line()
    {
        var (a, b) = (Rect(40, 30), Rect(40, 10));
        var placements = Layout(new FlexBlock().Add(a).Add(b));

        Assert.Equal((Top, 50.0), (Box(placements, b).Top, Box(placements, b).Bottom));
    }

    [Theory]
    [InlineData(FlexAlignment.FlexStart, 20)]
    [InlineData(FlexAlignment.FlexEnd, 40)]
    [InlineData(FlexAlignment.Center, 30)]
    public void Align_items_positions_shorter_items(FlexAlignment alignment, double top)
    {
        var (a, b) = (Rect(40, 30), Rect(40, 10));
        var placements = Layout(new FlexBlock { AlignItems = alignment }.Add(a).Add(b));

        Assert.Equal((top, top + 10), (Box(placements, b).Top, Box(placements, b).Bottom));
    }

    [Fact]
    public void Align_self_overrides_align_items_and_explicit_heights_are_not_stretched()
    {
        var (a, b, c) = (Rect(40, 30), Rect(40, 10), Rect(40, 10));
        var placements = Layout(new FlexBlock()
            .Add(a)
            .Add(new FlexItem(b) { AlignSelf = FlexAlignment.FlexEnd })
            .Add(new FlexItem(c) { Height = 15 }));

        Assert.Equal(40, Box(placements, b).Top);
        Assert.Equal((Top, 35.0), (Box(placements, c).Top, Box(placements, c).Bottom));
    }

    [Fact]
    public void Baseline_alignment_lines_up_the_first_baselines()
    {
        var small = new ParagraphBlock(new Paragraph(Text).Add("small"));
        var large = new ParagraphBlock(new Paragraph(Text with { FontSize = 24, LineHeight = LineHeight.Points(30) }).Add("Large"));
        var placements = Layout(new FlexBlock { AlignItems = FlexAlignment.Baseline }.Add(small).Add(large));

        double Baseline(ParagraphBlock block) =>
            placements.Single(p => p.Block == block).Top + block.Paragraph.Layout(1000).Lines[0].Baseline;
        Assert.Equal(Baseline(large), Baseline(small), 6);
        Assert.True(placements.Single(p => p.Block == small).Top > Top);
    }

    [Fact]
    public void Items_do_not_shrink_below_their_min_content_width_unless_allowed()
    {
        var word = new ParagraphBlock(new Paragraph(Text).Add("Supercalifragilistic"));
        var wordWidth = word.Paragraph.MeasureIntrinsicWidths().Min;
        var wide = Rect(150, 10);
        var placements = Layout(new FlexBlock().Add(word).Add(new FlexItem(wide)));

        Assert.Equal(wordWidth, placements.Single(p => p.Block == word).Width, 3);
        Assert.Equal(160 - wordWidth, Horizontal(placements, wide).Width, 3);

        var free = new ParagraphBlock(new Paragraph(Text).Add("Supercalifragilistic"));
        var shrunk = Layout(new FlexBlock().Add(new FlexItem(free) { MinWidth = 0 }).Add(Rect(150, 10)));
        Assert.True(shrunk.First(p => p.Block == free).Width < wordWidth);
    }

    [Fact]
    public void An_auto_margin_pushes_an_item_to_the_end()
    {
        var (a, b) = (Rect(40, 10), Rect(40, 10));
        var placements = Layout(new FlexBlock().Add(a).Add(new FlexItem(b) { AutoMargins = AutoMargins.Left }));

        Assert.Equal(20, Horizontal(placements, a).Left);
        Assert.Equal(140, Horizontal(placements, b).Left);
    }

    [Fact]
    public void Basis_sets_the_starting_size()
    {
        var (a, b) = (Rect(null, 10), Rect(null, 10));
        var placements = Layout(new FlexBlock()
            .Add(new FlexItem(a) { Basis = FlexBasis.Percent(50), Shrink = 0 })
            .Add(new FlexItem(b) { Basis = FlexBasis.Points(30), Grow = 1 }));

        Assert.Equal(80, Horizontal(placements, a).Width);
        Assert.Equal(80, Horizontal(placements, b).Width);
    }

    [Fact]
    public void Columns_stack_items_with_gaps_and_align_them()
    {
        var (a, b, c) = (Rect(40, 10), Rect(60, 20), Rect(null, 10));
        var placements = Layout(new FlexBlock { Direction = FlexDirection.Column, AlignItems = FlexAlignment.Center, RowGap = 5 }
            .Add(a).Add(b).Add(new FlexItem(c) { AlignSelf = FlexAlignment.Stretch }));

        Assert.Equal((80.0, Top), (Horizontal(placements, a).Left, Box(placements, a).Top));
        Assert.Equal((70.0, 35.0), (Horizontal(placements, b).Left, Box(placements, b).Top));
        Assert.Equal((Left, 160.0), Horizontal(placements, c));
        Assert.Equal(60, Box(placements, c).Top);
    }

    [Fact]
    public void Column_reverse_reverses_the_order()
    {
        var (a, b) = (Rect(40, 10), Rect(40, 20));
        var placements = Layout(new FlexBlock { Direction = FlexDirection.ColumnReverse }.Add(a).Add(b));

        Assert.Equal(Top, Box(placements, b).Top);
        Assert.Equal(40, Box(placements, a).Top);
    }

    [Fact]
    public void Margins_of_an_items_children_stay_inside_the_item()
    {
        var inner = Para(1, new BoxStyle { Margin = new Thickness(10, 0, 10, 0) });
        var item = new ContainerBlock(new BoxStyle { Background = Gray }, inner);
        var placements = Layout(new FlexBlock().Add(item));

        Assert.Equal(Top + 10, placements.Single(p => p.Block == inner).Top);
        Assert.Equal((Top, Top + 30), (Box(placements, item).Top, Box(placements, item).Bottom));
    }

    [Fact]
    public void A_row_taller_than_the_page_splits_its_items_side_by_side()
    {
        var tall = new ContainerBlock(new BoxStyle { Width = 80, Background = Gray }, Para(30));
        var shortText = Para(2);
        var stretched = new ContainerBlock(new BoxStyle { Background = Gray }, shortText);
        var after = Para(1);
        var placements = new FlowDocument(Small).Add(new FlexBlock().Add(tall).Add(stretched)).Add(after).Paginate().Placements;

        // 30 lines: 16 on the first page, 14 on the second.
        Assert.Equal([(0, 20.0, 180.0), (1, 20.0, 160.0)], placements.Where(p => p.Block == tall).Select(p => (p.Page, p.Top, p.Bottom)));

        // The short item stays on the first page, but its stretched box runs down to where the tall one ends.
        Assert.Equal(0, placements.Single(p => p.Block == shortText).Page);
        Assert.Equal([(0, 20.0, 180.0), (1, 20.0, 160.0)], placements.Where(p => p.Block == stretched).Select(p => (p.Page, p.Top, p.Bottom)));
        Assert.All(placements.Where(p => p.Block == stretched), p => Assert.Equal(100, p.Left));

        // Content after the row continues below its tallest item.
        Assert.Equal((1, 160.0), placements.Where(p => p.Block == after).Select(p => (p.Page, p.Top)).Single());
    }

    [Fact]
    public void A_row_moves_to_the_next_page_when_not_even_its_start_fits()
    {
        // One line is left after 15; the items' paragraphs need two (orphans), so the whole row moves.
        var (a, b) = (Para(5), Para(5));
        var placements = new FlowDocument(Small).Add(Para(15)).Add(new FlexBlock().Add(a).Add(b)).Paginate().Placements;

        Assert.All(placements.Where(p => p.Block == a || p.Block == b), p => Assert.Equal((1, Top), (p.Page, p.Top)));
    }

    [Fact]
    public void A_column_splits_between_and_inside_items()
    {
        var (a, b) = (Para(10), Para(10));
        var placements = new FlowDocument(Small).Add(new FlexBlock { Direction = FlexDirection.Column }.Add(a).Add(b)).Paginate().Placements;

        Assert.Equal([(0, 0, 10)], placements.Where(p => p.Block == a).Select(p => (p.Page, p.FirstLine, p.EndLine)));
        Assert.Equal([(0, 0, 6), (1, 6, 10)], placements.Where(p => p.Block == b).Select(p => (p.Page, p.FirstLine, p.EndLine)));
    }

    [Fact]
    public void Intrinsic_widths_add_up_items_in_a_row_and_take_the_widest_in_a_column()
    {
        FlexBlock Make(FlexDirection direction, FlexWrap wrap) =>
            new FlexBlock { Direction = direction, Wrap = wrap, ColumnGap = 10 }.Add(Rect(40, 10)).Add(Rect(60, 10));

        Assert.Equal((110.0, 110.0), Paginator.MeasureIntrinsic(Make(FlexDirection.Row, FlexWrap.NoWrap)));
        Assert.Equal((60.0, 110.0), Paginator.MeasureIntrinsic(Make(FlexDirection.Row, FlexWrap.Wrap)));
        Assert.Equal((60.0, 60.0), Paginator.MeasureIntrinsic(Make(FlexDirection.Column, FlexWrap.NoWrap)));
    }

    [Fact]
    public void Renders_items_where_they_are_placed()
    {
        var red = new ContainerBlock(new BoxStyle { Width = 40, Background = PdfColor.FromRgb(255, 0, 0) }, new SpacerBlock(20));
        var blue = new ContainerBlock(new BoxStyle { Width = 40, Background = PdfColor.FromRgb(0, 0, 255) }, new SpacerBlock(40));
        var flow = new FlowDocument(Small).Add(new FlexBlock { JustifyContent = JustifyContent.SpaceBetween }.Add(red).Add(blue));

        using var bitmap = Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(40, 50)); // Red is stretched to the line's 40 pt.
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(160, 50));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(100, 50));
    }

    private static ContainerBlock Rect(double? width, double height) =>
        new(new BoxStyle { Width = width, Background = Gray }, new SpacerBlock(height));

    private static ParagraphBlock Para(int lines, BoxStyle? style = null) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Line {i}"))), style);

    private static IReadOnlyList<Placement> Layout(FlexBlock flex) => new FlowDocument(Small).Add(flex).Paginate().Placements;

    private static Placement Box(IReadOnlyList<Placement> placements, Block block) => placements.Single(p => p.Block == block);

    private static (double Left, double Width) Horizontal(IReadOnlyList<Placement> placements, Block block)
    {
        var box = Box(placements, block);
        return (box.Left, box.Width);
    }
}
