using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Floats and clear. Uses a 200 x 200 pt page with 20 pt margins: the content area is x 20-180, y 20-180, with 10 pt
/// lines. Floats have backgrounds so their placements are recorded; paragraph anchors give the x of their line.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class FloatLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Gray = PdfColor.FromRgb(200, 200, 200);

    [Fact]
    public void Text_after_a_left_float_starts_beside_it_and_returns_below_it()
    {
        var box = Float(FloatSide.Left, 40, 30);
        var (paragraph, anchors) = Words(40);
        var result = Paginate(box, paragraph);

        Assert.Equal((20.0, 20.0), (Box(result, box).Left, Box(result, box).Top));
        var lines = LineStarts(result, anchors);
        Assert.Equal(60, lines[0].Left, 3); // Beside the float: its 40 pt plus the margin edge.
        Assert.Equal(20, lines[0].Top, 3); // Not below it.
        Assert.Contains(lines, l => l.Top >= 50 - 1e-6 && Math.Abs(l.Left - 20) < 1e-6); // Below it: the full width again.
        Assert.DoesNotContain(lines, l => l.Top < 50 - 1e-6 && l.Left < 60 - 1e-6);
    }

    [Fact]
    public void Text_beside_a_right_float_ends_before_it()
    {
        var box = Float(FloatSide.Right, 60, 40);
        var (paragraph, _) = Words(60);
        var flow = new FlowDocument(Small).Add(box).Add(paragraph);

        var result = flow.Paginate();
        Assert.Equal(120, Box(result, box).Left, 3);
        using var bitmap = Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
        Assert.False(HasInk(bitmap, 121, 21, 179, 59), "text was drawn over the right float");
        Assert.True(HasInk(bitmap, 21, 21, 119, 59), "no text beside the float");
        Assert.True(HasInk(bitmap, 121, 61, 179, 99), "no text below the float");
    }

    [Fact]
    public void Floats_on_the_same_side_stack_and_move_down_when_there_is_no_room()
    {
        var (a, b, c) = (Float(FloatSide.Left, 60, 30), Float(FloatSide.Left, 60, 20), Float(FloatSide.Left, 60, 10));
        var result = Paginate(a, b, c);

        Assert.Equal((20.0, 20.0), (Box(result, a).Left, Box(result, a).Top));
        Assert.Equal((80.0, 20.0), (Box(result, b).Left, Box(result, b).Top));
        Assert.Equal((80.0, 40.0), (Box(result, c).Left, Box(result, c).Top)); // 180 - 140 is too narrow: below b, which ends first.
    }

    [Fact]
    public void A_float_is_not_placed_above_an_earlier_one()
    {
        var right = Float(FloatSide.Right, 100, 30);
        var wide = Float(FloatSide.Left, 100, 10); // Does not fit beside the first: moves below it.
        var narrow = Float(FloatSide.Left, 40, 10); // Would fit at the top, but may not go above the one before it.
        var result = Paginate(right, wide, narrow);

        Assert.Equal(50, Box(result, wide).Top, 3);
        Assert.Equal(50, Box(result, narrow).Top, 3);
        Assert.Equal(120, Box(result, narrow).Left, 3);
    }

    [Fact]
    public void Floats_follow_the_pending_margin_but_do_not_collapse_theirs()
    {
        var before = new ContainerBlock(new BoxStyle { Margin = new Thickness(0, 0, 10, 0), Background = Gray }, new SpacerBlock(10));
        var box = Float(FloatSide.Left, 40, 20, new Thickness(5, 0, 0, 0));
        var result = Paginate(before, box);

        Assert.Equal(20 + 10 + 10 + 5, Box(result, box).Top, 3);
    }

    [Theory]
    [InlineData(ClearSide.Left, 60)]
    [InlineData(ClearSide.Right, 40)]
    [InlineData(ClearSide.Both, 60)]
    [InlineData(ClearSide.None, 20)]
    public void Clear_moves_a_block_below_floats_on_those_sides(ClearSide clear, double top)
    {
        var block = new ContainerBlock(new BoxStyle { Clear = clear, Background = Gray }, new SpacerBlock(10));
        var result = Paginate(Float(FloatSide.Left, 40, 40), Float(FloatSide.Right, 40, 20), block);

        Assert.Equal(top, Box(result, block).Top, 3);
    }

    [Fact]
    public void Clearance_replaces_the_top_margin_when_the_floats_end_lower()
    {
        var block = new ContainerBlock(new BoxStyle { Clear = ClearSide.Both, Margin = new Thickness(5, 0, 0, 0), Background = Gray }, new SpacerBlock(10));
        var result = Paginate(Float(FloatSide.Left, 40, 40), block);

        Assert.Equal(60, Box(result, block).Top, 3);
    }

    [Fact]
    public void An_empty_clearing_block_moves_what_follows_below_the_floats()
    {
        var after = new ContainerBlock(new BoxStyle { Background = Gray }, new SpacerBlock(10));
        var result = Paginate(Float(FloatSide.Left, 40, 40), new ContainerBlock(new BoxStyle { Clear = ClearSide.Both }), after);

        Assert.Equal(60, Box(result, after).Top, 3);
    }

    [Fact]
    public void A_new_formatting_context_contains_its_floats_and_an_ordinary_block_does_not()
    {
        var inner = Float(FloatSide.Left, 40, 60);
        var root = new ContainerBlock(new BoxStyle { ContainsFloats = true, Background = Gray }, inner, Line("x"));
        var plainFloat = Float(FloatSide.Left, 40, 60);
        var plain = new ContainerBlock(new BoxStyle { Background = Gray }, plainFloat, Line("x"));

        Assert.Equal(80, Box(Paginate(root), root).Bottom, 3);
        Assert.Equal(30, Box(Paginate(plain), plain).Bottom, 3); // Just its line: the float sticks out below it.
    }

    [Fact]
    public void Floats_inside_a_new_formatting_context_do_not_affect_text_outside_it()
    {
        var root = new ContainerBlock(new BoxStyle { ContainsFloats = true }, Float(FloatSide.Left, 40, 10));
        var (paragraph, anchors) = Words(5);
        var lines = LineStarts(Paginate(root, paragraph), anchors);

        Assert.All(lines, l => Assert.Equal(20, l.Left, 3));
    }

    [Fact]
    public void A_table_goes_beside_floats_and_below_them_when_it_does_not_fit()
    {
        var table = new TableBlock(new BoxStyle { Background = Gray }).Add(new TableRow(new TableCell(new Paragraph(Text).Add("cell"))));
        var beside = Paginate(Float(FloatSide.Left, 50, 40), table);
        Assert.Equal((70.0, 20.0), (Box(beside, table).Left, Box(beside, table).Top));

        var wide = new TableBlock(new BoxStyle { Width = 140, Background = Gray }).Add(new TableRow(new TableCell(new Paragraph(Text).Add("cell"))));
        var below = Paginate(Float(FloatSide.Left, 50, 40), wide);
        Assert.Equal((20.0, 60.0), (Box(below, wide).Left, Box(below, wide).Top));
    }

    [Fact]
    public void A_float_without_a_width_shrinks_to_its_content()
    {
        var content = new ParagraphBlock(new Paragraph(Text).Add("Hello"));
        var box = new ContainerBlock(new BoxStyle { Float = FloatSide.Right, Background = Gray }, content);
        var placed = Box(Paginate(box), box);

        Assert.Equal(Sans.MeasureText("Hello", 8), placed.Width, 3);
        Assert.Equal(180 - placed.Width, placed.Left, 3);
    }

    [Fact]
    public void A_line_moves_down_when_its_first_word_does_not_fit_beside_the_floats()
    {
        var box = Float(FloatSide.Left, 150, 30); // Leaves 10 pt: too narrow for any word.
        var (paragraph, anchors) = Words(3);
        var lines = LineStarts(Paginate(box, paragraph), anchors);

        Assert.Equal((20.0, 50.0), (lines[0].Left, lines[0].Top));
    }

    [Fact]
    public void A_float_that_does_not_fit_on_the_page_moves_to_the_next_with_what_follows()
    {
        var filler = new ContainerBlock(new BoxStyle { Background = Gray }, new SpacerBlock(130));
        var box = Float(FloatSide.Left, 40, 40);
        var (paragraph, anchors) = Words(3);
        var result = Paginate(filler, box, paragraph);

        Assert.Equal((1, 20.0), (Box(result, box).Page, Box(result, box).Top));
        var first = LineStarts(result, anchors)[0];
        Assert.Equal((1, 60.0), (first.Page, first.Left));
    }

    [Fact]
    public void Lines_on_the_next_page_avoid_a_float_that_continues_there()
    {
        var tall = new ContainerBlock(new BoxStyle { Float = FloatSide.Left, Width = 40, Background = Gray }, Lines(30));
        var (paragraph, anchors) = Words(160);
        var result = Paginate(tall, paragraph);

        var onSecondPage = LineStarts(result, anchors).Where(l => l.Page == 1 && l.Top < 120).ToList();
        Assert.NotEmpty(onSecondPage);
        Assert.All(onSecondPage, l => Assert.Equal(60, l.Left, 3));
        Assert.Contains(result.Placements, p => p.Block == tall && p.Page == 1);
    }

    [Fact]
    public void Lines_placed_beside_a_float_stay_the_same_when_the_paragraph_continues_on_the_next_page()
    {
        var box = Float(FloatSide.Left, 80, 150);
        var (paragraph, anchors) = Words(120);
        var result = Paginate(box, paragraph);

        // Every word is drawn exactly once: the lines left for the next page start where the placed ones ended.
        var named = result.Anchors.Where(a => a.Name is not null).Select(a => a.Name!).ToList();
        Assert.Equal(anchors, named);
        Assert.Contains(result.Anchors, a => a.Page == 1);
        Assert.All(LineStarts(result, anchors).Where(l => l.Page == 0 && l.Top < 170), l => Assert.Equal(100, l.Left, 3));
    }

    [Fact]
    public void A_first_line_moved_below_a_float_that_no_longer_fits_goes_to_the_next_page()
    {
        var filler = new ContainerBlock(new BoxStyle { Background = Gray }, Lines(12)); // y 20-140.
        var box = Float(FloatSide.Left, 150, 35); // y 140-175, leaving 10 pt beside it: too narrow for a word.
        var (paragraph, anchors) = Words(3);
        var first = LineStarts(Paginate(filler, box, paragraph), anchors)[0];

        Assert.Equal((1, 20.0, 20.0), first); // Below the float it would end at 185, below the page.
    }

    [Fact]
    public void A_line_moved_down_past_a_float_starts_the_next_page_at_its_top_when_widows_move_it()
    {
        var filler = new ContainerBlock(new BoxStyle { Background = Gray }, Lines(10)); // y 20-120.
        var narrow = Float(FloatSide.Left, 40, 20); // y 120-140.
        var wide = Float(FloatSide.Left, 150, 30); // No room beside the first: y 140-170.
        var (paragraph, anchors) = Words(23);
        var result = Paginate(filler, narrow, wide, paragraph);

        var lines = LineStarts(result, anchors);
        Assert.Equal(anchors, result.Anchors.Where(a => a.Name is not null).Select(a => a.Name!).ToList());
        var second = lines.Where(l => l.Page == 1).ToList();
        Assert.NotEmpty(second);
        Assert.Equal(20, second.Min(l => l.Top), 3);
    }

    [Fact]
    public void Margins_do_not_collapse_through_a_new_formatting_context()
    {
        var child = new ContainerBlock(new BoxStyle { Margin = new Thickness(10, 0, 0, 0), Background = Gray }, new SpacerBlock(10));
        var root = new ContainerBlock(new BoxStyle { ContainsFloats = true, Background = Gray }, child);
        var result = Paginate(root);

        Assert.Equal((20.0, 30.0), (Box(result, root).Top, Box(result, child).Top));
    }

    [Fact]
    public void Floats_are_painted_over_the_backgrounds_of_later_blocks()
    {
        var red = new ContainerBlock(new BoxStyle { Float = FloatSide.Left, Width = 40, Background = PdfColor.FromRgb(255, 0, 0) }, new SpacerBlock(40));
        var blue = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(0, 0, 255) }, new SpacerBlock(20));
        var flow = new FlowDocument(Small).Add(red).Add(blue);

        using var bitmap = Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(30, 30)); // The block's background is under the float.
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(100, 30));
    }

    [Fact]
    public void A_table_cell_contains_its_floats()
    {
        var cell = new TableCell(new ContainerBlock(new BoxStyle { Float = FloatSide.Left, Width = 30, Background = Gray }, new SpacerBlock(50)), Line("x"));
        var table = new TableBlock(new BoxStyle { Background = Gray }).Add(new TableRow(cell));
        var placed = Box(Paginate(table), table);

        Assert.Equal(50, placed.Bottom - placed.Top, 3);
    }

    private static ContainerBlock Float(FloatSide side, double width, double height, Thickness margin = default) =>
        new(new BoxStyle { Float = side, Width = width, Margin = margin, Background = Gray }, new SpacerBlock(height));

    private static ParagraphBlock Line(string text) => new(new Paragraph(Text).Add(text));

    private static ParagraphBlock Lines(int count) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, count).Select(i => $"L{i}"))));

    /// <summary>A paragraph of words with an anchor before each, so each line's start can be found.</summary>
    private static (ParagraphBlock Block, List<string> Anchors) Words(int count)
    {
        var paragraph = new Paragraph(Text);
        var anchors = new List<string>();
        for (var i = 0; i < count; i++)
        {
            anchors.Add($"w{i}");
            paragraph.AddAnchor($"w{i}");
            paragraph.Add(i == count - 1 ? "word" : "word ");
        }

        return (new ParagraphBlock(paragraph), anchors);
    }

    private static PaginationResult Paginate(params Block[] blocks)
    {
        var flow = new FlowDocument(Small);
        foreach (var block in blocks)
        {
            flow.Add(block);
        }

        return flow.Paginate();
    }

    private static Placement Box(PaginationResult result, Block block) => result.Placements.First(p => p.Block == block);

    /// <summary>The first anchor of each line: where each line starts.</summary>
    private static List<(int Page, double Top, double Left)> LineStarts(PaginationResult result, List<string> names) =>
        [.. result.Anchors.Where(a => a.Name is not null && names.Contains(a.Name))
            .GroupBy(a => (a.Page, a.Top)).Select(g => (g.Key.Page, g.Key.Top, g.Min(a => a.Left)))];

    private static bool HasInk(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (bitmap.GetPixel(x, y).Red < 100)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
