using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Containers with columns (<see cref="ContainerBlock.Columns"/>, #312). Uses a 200 x 200 pt page with 20 pt margins
/// (content 160 x 160 pt from (20, 20)) and 10 pt lines, so 16 lines fit in a column. Two columns with a 10 pt gap are
/// 75 pt wide, at x = 20 and x = 105.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class ColumnLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly ColumnLayout Two = new() { Count = 2, Gap = 10 };

    [Fact]
    public void Content_is_balanced_over_the_columns()
    {
        var para = Para(9);
        var after = Para(1);
        var placements = Flow(Columns(Two, para), after).Paginate().Placements;

        var pieces = placements.Where(p => p.Block == para).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal((0, 0, 5, 20.0, 20.0, 70.0), (pieces[0].Page, pieces[0].FirstLine, pieces[0].EndLine, pieces[0].Left, pieces[0].Top, pieces[0].Bottom));
        Assert.Equal((0, 5, 9, 105.0, 20.0, 60.0), (pieces[1].Page, pieces[1].FirstLine, pieces[1].EndLine, pieces[1].Left, pieces[1].Top, pieces[1].Bottom));
        Assert.Equal(70, placements.Single(p => p.Block == after).Top, 6); // Below the tallest column.
    }

    [Fact]
    public void Columns_continue_on_the_next_page_and_the_last_row_is_balanced()
    {
        var para = Para(40);
        var after = Para(1);
        var placements = Flow(Columns(Two, para), after).Paginate().Placements;

        var pieces = placements.Where(p => p.Block == para).Select(p => (p.Page, p.FirstLine, p.EndLine, p.Left, p.Top)).ToList();
        Assert.Equal([(0, 0, 16, 20.0, 20.0), (0, 16, 32, 105.0, 20.0), (1, 32, 36, 20.0, 20.0), (1, 36, 40, 105.0, 20.0)], pieces);
        var next = placements.Single(p => p.Block == after);
        Assert.Equal((1, 60.0), (next.Page, next.Top));
    }

    [Fact]
    public void A_block_spanning_the_columns_goes_across_them_between_two_sets_of_columns()
    {
        var (before, span, below) = (Para(4), Para(1, new BoxStyle { SpanColumns = true }), Para(4));
        var placements = Flow(Columns(Two, before, span, below)).Paginate().Placements;

        Assert.Equal([(20.0, 20.0, 0), (105.0, 20.0, 2)], placements.Where(p => p.Block == before).Select(p => (p.Left, p.Top, p.FirstLine)));
        var spanning = placements.Single(p => p.Block == span);
        Assert.Equal((40.0, 20.0, 160.0), (spanning.Top, spanning.Left, spanning.Width));
        Assert.Equal([(20.0, 50.0), (105.0, 50.0)], placements.Where(p => p.Block == below).Select(p => (p.Left, p.Top)));
    }

    [Fact]
    public void A_column_break_starts_the_next_column()
    {
        var first = Para(2);
        var second = Para(2, new BoxStyle { BreakBefore = PageBreak.Column });
        var after = Para(1);
        var placements = Flow(Columns(Two, first, second), after).Paginate().Placements;

        Assert.Equal((105.0, 20.0), placements.Where(p => p.Block == second).Select(p => (p.Left, p.Top)).Single());
        Assert.Equal(40, placements.Single(p => p.Block == after).Top, 6);
    }

    [Fact]
    public void A_page_break_in_columns_starts_the_first_column_of_the_next_page()
    {
        var second = Para(2, new BoxStyle { BreakBefore = PageBreak.Page });
        var placement = Flow(Columns(Two, Para(2), second)).Paginate().Placements.Single(p => p.Block == second);

        Assert.Equal((1, 20.0, 20.0), (placement.Page, placement.Left, placement.Top));
    }

    [Fact]
    public void A_column_break_outside_columns_does_nothing()
    {
        var second = Para(2, new BoxStyle { BreakBefore = PageBreak.Column });
        var placement = Flow(Para(2), second).Paginate().Placements.Single(p => p.Block == second);

        Assert.Equal((0, 40.0), (placement.Page, placement.Top));
    }

    [Fact]
    public void Avoiding_a_column_break_inside_a_block_keeps_it_in_one_column()
    {
        // Balanced at 50 pt: without avoid-column the second paragraph starts with 2 lines in the first column.
        var first = Para(3);
        var split = Para(5);
        var kept = Para(5, new BoxStyle { BreakInside = PageBreakInside.AvoidColumn });

        Assert.Equal([20.0, 105.0], Flow(Columns(Two, first, split)).Paginate().Placements.Where(p => p.Block == split).Select(p => p.Left));
        Assert.Equal([105.0], Flow(Columns(Two, Para(3), kept)).Paginate().Placements.Where(p => p.Block == kept).Select(p => p.Left));
    }

    [Theory]
    [InlineData(50, null, new[] { 20.0, 105.0 })] // floor((160 + 10) / 60) = 2 columns of 75 pt.
    [InlineData(40, null, new[] { 20.0, 76.667, 133.333 })] // 3 columns of 46.67 pt.
    [InlineData(40, 2, new[] { 20.0, 105.0 })] // At most 2.
    [InlineData(50, 5, new[] { 20.0, 105.0 })] // At most as many as fit.
    [InlineData(200, null, new[] { 20.0 })] // Wider than the container: one column, as wide as the container.
    public void A_column_width_makes_as_many_columns_as_fit(double width, int? count, double[] lefts)
    {
        var paras = Enumerable.Range(0, lefts.Length).Select(_ => Para(1)).ToArray();
        var layout = new ColumnLayout { Width = width, Count = count, Gap = 10 };
        var placements = Flow(Columns(layout, [.. paras.Select((p, i) => i == 0 ? p : WithBreak(p))])).Paginate().Placements;

        Assert.Equal(lefts, placements.Select(p => Math.Round(p.Left, 3)));
        Assert.Equal(lefts.Length == 1 ? 160 : (170.0 / lefts.Length) - 10, placements[0].Width, 3);
    }

    [Fact]
    public void Columns_of_their_own_widths_keep_them()
    {
        var layout = new ColumnLayout { Widths = [(100, 10), (40, 0)] };
        var placements = Flow(Columns(layout, Para(4))).Paginate().Placements;

        Assert.Equal([20.0, 130.0], placements.Select(p => p.Left));
    }

    [Fact]
    public void A_paragraph_continuing_in_a_narrower_column_wraps_to_its_width()
    {
        // The second column is 130-170 pt: no text reaches past it (the page is 200 pt wide).
        var words = new ParagraphBlock(new Paragraph(Text).Add(string.Join(" ", Enumerable.Repeat("word", 120))));
        var layout = new ColumnLayout { Widths = [(100, 10), (40, 0)] };
        using var bitmap = Render(Flow(Columns(layout, words)));

        Assert.Contains(Enumerable.Range(20, 160), y => Enumerable.Range(100, 18).Any(x => Dark(bitmap.GetPixel(x, y)))); // Wide lines in the first.
        Assert.Contains(Enumerable.Range(20, 160), y => Enumerable.Range(150, 20).Any(x => Dark(bitmap.GetPixel(x, y))));
        Assert.DoesNotContain(Enumerable.Range(0, 200), y => Enumerable.Range(172, 28).Any(x => Dark(bitmap.GetPixel(x, y))));

        static bool Dark(SKColor color) => color.Red < 128;
    }

    [Fact]
    public void Columns_filled_one_after_the_other_in_a_box_of_a_fixed_height_overflow_beside_it()
    {
        // 30 pt high: 3 lines a column, the last 2 lines in a third column past the container's right edge.
        var para = Para(8);
        var after = Para(1);
        var container = new ContainerBlock(new BoxStyle { Height = 30 }, para) { Columns = Two with { Fill = ColumnFill.Auto } };
        var placements = Flow(container, after).Paginate().Placements;

        Assert.Equal([(20.0, 0), (105.0, 3), (190.0, 6)], placements.Where(p => p.Block == para).Select(p => (p.Left, p.FirstLine)));
        Assert.Equal(50, placements.Single(p => p.Block == after).Top, 6);
    }

    [Fact]
    public void Columns_filled_one_after_the_other_on_pages_end_where_the_content_does()
    {
        var para = Para(20);
        var placements = Flow(Columns(Two with { Fill = ColumnFill.Auto }, para)).Paginate().Placements;

        Assert.Equal([(0, 0, 16), (0, 16, 20)], placements.Where(p => p.Block == para).Select(p => (p.Page, p.FirstLine, p.EndLine)));
    }

    [Fact]
    public void Balancing_does_not_cut_lines_or_shrink_images()
    {
        // Short columns would cut the 30 pt line or scale the image down; neither is a fit.
        var tall = new ParagraphBlock(new Paragraph(Text with { LineHeight = LineHeight.Points(30) }).Add("Tall"));
        var image = new ImageBlock(Images(), 20, 40);
        var flow = Flow(Columns(Two, tall, image, Para(1)));
        flow.Diagnostics = _ => { };
        var result = flow.Paginate();
        var placements = result.Placements;

        Assert.Empty(result.Diagnostics);
        var placed = placements.Single(p => p.Block == image);
        Assert.Equal(40, placed.Bottom - placed.Top, 6);
        Assert.Equal(30, placements.Single(p => p.Block == tall).Bottom - 20, 6);
    }

    [Fact]
    public void Anchors_and_markers_in_columns_are_where_the_columns_put_them()
    {
        // 15 lines leave room for 1 on the first page: the columns move to the second, the target into its second column.
        var target = Para(2) is var p ? new ParagraphBlock(p.Paragraph) { Id = "target", Marker = "m" } : null!;
        var result = Flow(Para(15), Columns(Two, Para(2), target)).Paginate();

        var anchor = result.Anchors.Single(a => a.Block == target);
        var placement = result.Placements.Single(pl => pl.Block == target);
        Assert.Equal((1, 20.0, 105.0), (placement.Page, placement.Top, placement.Left));
        Assert.Equal((placement.Page, placement.Top, placement.Left), (anchor.Page, anchor.Top, anchor.Left));
        Assert.Equal(1, result.Markers.Single(m => Equals(m.Marker, "m")).Page);
    }

    [Fact]
    public void The_rule_goes_between_columns_with_content()
    {
        var red = PdfColor.FromRgb(255, 0, 0);
        var layout = Two with { Rule = new BorderSide(2, red) };

        using var two = Render(Flow(Columns(layout, Para(4))));
        Assert.Equal(new SKColor(255, 0, 0), two.GetPixel(100, 30));
        Assert.Equal(new SKColor(255, 255, 255), two.GetPixel(100, 45)); // Only as tall as the columns (20 pt).

        using var one = Render(Flow(Columns(layout, Para(1))));
        Assert.Equal(new SKColor(255, 255, 255), one.GetPixel(100, 25));
    }

    [Fact]
    public void Columns_lay_out_without_pages_inside_a_flex_item()
    {
        var para = Para(4);
        var flex = new FlexBlock(null, new FlexItem(Columns(Two, para)) { Grow = 1 });
        var after = Para(1);
        var placements = Flow(flex, after).Paginate().Placements;

        Assert.Equal(40, placements.Single(p => p.Block == after).Top, 6); // Balanced: 2 lines a column.
    }

    [Fact]
    public void The_intrinsic_widths_are_the_columns_side_by_side()
    {
        var para = new ParagraphBlock(new Paragraph(Text).Add("Some words here"));
        var (min, max) = Paginator.MeasureIntrinsic(para);
        var columns = Paginator.MeasureIntrinsic(Columns(Two, para));
        var withWidth = Paginator.MeasureIntrinsic(Columns(new ColumnLayout { Width = 10, Gap = 10 }, para));

        Assert.Equal(((2 * min) + 10, (2 * max) + 10), columns);
        Assert.Equal((10, max), withWidth); // One column of at least the column width; min is at most one column.
        Assert.Equal((min, 300.0), Paginator.MeasureIntrinsic(Columns(new ColumnLayout { Width = 300, Gap = 10 }, para)));
    }

    [Fact]
    public void Columns_nested_deeper_than_two_levels_lay_their_content_out_in_one_column()
    {
        // Balancing nested columns multiplies the work: the third level is one column, the second still has columns
        // ((75 + 10) / 2 - 10 = 32.5 pt wide; the third level's would be 11.25 pt).
        var innermost = Para(4);
        var second = Para(4);
        var placements = Flow(Columns(Two, Columns(Two, second, Columns(Two, innermost)))).Paginate().Placements;

        Assert.All(placements.Where(p => p.Block == second || p.Block == innermost), p => Assert.Equal(32.5, p.Width, 6));
        Assert.Contains(placements, p => p.Block == innermost);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("flex")]
    [InlineData("table")]
    public void Diagnostics_in_columns_name_the_page_they_are_on(string container)
    {
        // A box too wide for a 32.5 pt column of nested columns, on the second page after a forced break. (A flex item is
        // at least as wide as its content's min-content width, unless it has a width: the columns would make room.)
        var wide = new ContainerBlock(new BoxStyle { Width = 60, Height = 5 });
        Block columns = new ContainerBlock(new BoxStyle { Width = 160 }, Para(2), Columns(Two, wide)) { Columns = Two };
        var flow = Flow(Para(2), new ContainerBlock(new BoxStyle { BreakBefore = PageBreak.Page }, container switch
        {
            "flex" => new FlexBlock(null, new FlexItem(columns) { Grow = 1 }),
            "table" => new TableBlock().Add(new TableRow(new TableCell(columns))),
            _ => columns,
        }));
        flow.Diagnostics = _ => { };

        var diagnostic = Assert.Single(flow.Paginate().Diagnostics);
        Assert.Equal((LayoutDiagnosticKind.Overflow, 1), (diagnostic.Kind, diagnostic.Page));
    }

    [Fact]
    public void The_margin_below_a_spanning_block_is_kept_above_the_next_columns()
    {
        // 2 lines (kept together by orphans and widows) at 20-40, the spanning line at 40-50, then the 10 pt margin.
        var below = Para(2);
        var placements = Flow(Columns(Two, Para(2), Para(1, new BoxStyle { SpanColumns = true, Margin = new Thickness(0, 0, 10, 0) }), below)).Paginate().Placements;

        Assert.Equal([(20.0, 60.0)], placements.Where(p => p.Block == below).Select(p => (p.Left, p.Top)));
    }

    [Fact]
    public void Columns_filled_one_after_the_other_are_balanced_before_a_spanning_block()
    {
        var before = Para(4);
        var placements = Flow(Columns(Two with { Fill = ColumnFill.Auto }, before, Para(1, new BoxStyle { SpanColumns = true }), Para(1))).Paginate().Placements;

        Assert.Equal([(20.0, 0, 2), (105.0, 2, 4)], placements.Where(p => p.Block == before).Select(p => (p.Left, p.FirstLine, p.EndLine)));
    }

    [Fact]
    public void Columns_filled_one_after_the_other_without_a_height_or_pages_are_balanced()
    {
        // A table cell is laid out without pages: CSS balances columns whose height nothing limits.
        var after = Para(1);
        var placements = Flow(new TableBlock().Add(new TableRow(new TableCell(Columns(Two with { Fill = ColumnFill.Auto }, Para(4))))), after).Paginate().Placements;

        Assert.Equal(40, placements.Single(p => p.Block == after).Top, 6);
    }

    [Fact]
    public void A_maximum_height_limits_the_columns_like_a_height()
    {
        // Balanced, 8 lines would make 40 pt columns; the 30 pt limit puts the last 2 lines in a third column.
        var para = Para(8);
        var container = new ContainerBlock(new BoxStyle { MaxHeight = 30 }, para) { Columns = Two };
        var placements = Flow(container).Paginate().Placements;

        Assert.Equal([20.0, 105.0, 190.0], placements.Where(p => p.Block == para).Select(p => p.Left));
    }

    [Fact]
    public void A_column_break_after_a_block_starts_the_next_column()
    {
        // Balanced without the break, the first two lines would share the first column.
        var second = Para(1);
        var placements = Flow(Columns(Two, Para(1, new BoxStyle { BreakAfter = PageBreak.Column }), second, Para(1))).Paginate().Placements;

        Assert.Equal(105, placements.Single(p => p.Block == second).Left, 6);
    }

    [Fact]
    public void A_block_that_avoids_breaks_is_not_split_to_balance_the_columns()
    {
        var kept = Para(6, new BoxStyle { BreakInside = PageBreakInside.AvoidColumn });
        var placements = Flow(Columns(Two, kept)).Paginate().Placements;

        Assert.Equal((20.0, 80.0), placements.Where(p => p.Block == kept).Select(p => (p.Left, p.Bottom)).Single());
    }

    [Fact]
    public void Balancing_does_not_shrink_an_image_to_make_the_columns_shorter()
    {
        var image = new ImageBlock(Images(), 20, 40);
        var placements = Flow(Columns(Two, image, Para(1))).Paginate().Placements;

        var placed = placements.Single(p => p.Block == image);
        Assert.Equal(40, placed.Bottom - placed.Top, 6);
    }

    [Fact]
    public void The_top_margin_of_the_first_child_stays_inside_the_container()
    {
        // A new formatting context: the margin does not collapse through it (nor count twice).
        var first = Para(1, new BoxStyle { Margin = new Thickness(10, 0, 0, 0) });
        var container = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(0, 0, 255) }, first) { Columns = Two };
        var placements = Flow(container).Paginate().Placements;

        Assert.Equal(30, placements.Single(p => p.Block == first).Top, 6);
        Assert.Equal(20, placements.Single(p => p.Block == container).Top, 6);
    }

    [Fact]
    public void Rules_of_full_rows_reach_the_bottom_of_the_page_and_not_past_the_last_column()
    {
        // 20 lines from y = 120: the first row is 6 lines (120-180), the last row on the next page.
        var red = PdfColor.FromRgb(255, 0, 0);
        var flow = Flow(Para(10), Columns(Two with { Rule = new BorderSide(2, red) }, Para(20)));
        var pdf = flow.Render().ToArray();
        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(255, 0, 0), first.GetPixel(100, 175));
        Assert.Equal(new SKColor(255, 255, 255), first.GetPixel(100, 110));
        Assert.Equal(new SKColor(255, 255, 255), first.GetPixel(185, 150)); // No rule after the row's last column.
    }

    [Fact]
    public void The_rule_of_columns_filled_to_a_fixed_height_is_as_tall_as_the_box()
    {
        var red = PdfColor.FromRgb(255, 0, 0);
        var container = new ContainerBlock(new BoxStyle { Height = 35 }, Para(4)) { Columns = Two with { Fill = ColumnFill.Auto, Rule = new BorderSide(2, red) } };
        using var bitmap = Render(Flow(container));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 52));
    }

    [Fact]
    public void A_column_layout_rejects_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnLayout { Count = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnLayout { Width = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnLayout { Gap = -1 });
    }

    private static ContainerBlock Columns(ColumnLayout layout, params Block[] children) => new(null, children) { Columns = layout };

    private static ParagraphBlock WithBreak(ParagraphBlock para) => new(para.Paragraph, new BoxStyle { BreakBefore = PageBreak.Column });

    private static ParagraphBlock Para(int lines, BoxStyle? style = null) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Line {i}"))), style);

    private static PdfImage Images() => PdfImage.Load(Fixtures.Path(Path.Combine("Images", "rgb.jpg")));

    private static SKBitmap Render(FlowDocument flow) =>
        Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

    private static FlowDocument Flow(params Block[] blocks)
    {
        var flow = new FlowDocument(Small);
        foreach (var block in blocks)
        {
            flow.Add(block);
        }

        return flow;
    }
}
