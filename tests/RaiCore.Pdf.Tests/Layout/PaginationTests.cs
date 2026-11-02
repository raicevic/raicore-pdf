using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Uses a 200 x 200 pt page with 20 pt margins (content area 160 pt tall, starting at y = 20) and 10 pt lines,
/// so exactly 16 lines fit on a page.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PaginationTests
{
    private const double Top = 20;
    private const double Bottom = 180;

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));

    [Fact]
    public void Content_that_fits_uses_one_page()
    {
        var flow = Flow(Para(16));

        Assert.Equal(1, flow.CountPages());
        Assert.Equal(Bottom, Assert.Single(flow.Paginate().Placements).Bottom, 6);
    }

    [Fact]
    public void Paragraphs_continue_on_the_next_page()
    {
        var para = Para(20);
        var placements = Flow(para).Paginate().Placements;

        Assert.Collection(
            placements,
            p => Assert.Equal((0, 0, 16, Top), (p.Page, p.FirstLine, p.EndLine, p.Top)),
            p => Assert.Equal((1, 16, 20, Top), (p.Page, p.FirstLine, p.EndLine, p.Top)));
    }

    [Fact]
    public void Widows_pull_lines_to_the_next_page()
    {
        // 17 lines: 16 fit, but one line alone on the next page violates widows = 2, so 15 + 2.
        var placements = Flow(Para(17)).Paginate().Placements;

        Assert.Equal([(0, 0, 15), (1, 15, 17)], placements.Select(p => (p.Page, p.FirstLine, p.EndLine)));
    }

    [Fact]
    public void Orphans_move_a_paragraph_to_the_next_page()
    {
        // After 15 lines, one line of space is left; orphans = 2 needs two, so the paragraph moves.
        var second = Para(5);
        var placements = Flow(Para(15), second).Paginate().Placements;

        var placed = Assert.Single(placements, p => p.Block == second);
        Assert.Equal((1, 0, 5, Top), (placed.Page, placed.FirstLine, placed.EndLine, placed.Top));
    }

    [Fact]
    public void Custom_orphans_and_widows_are_honored()
    {
        var para = Para(20, new BoxStyle { Orphans = 1, Widows = 6 });
        var placements = Flow(Para(15), para).Paginate().Placements.Where(p => p.Block == para);

        // Orphans = 1: one line fits at the bottom of page 1 and may stay there. Page 2 could take 16 more lines,
        // but that would leave 3 for page 3; widows = 6 requires 6, so page 2 stops after line 14.
        Assert.Equal([(0, 0, 1), (1, 1, 14), (2, 14, 20)], placements.Select(p => (p.Page, p.FirstLine, p.EndLine)));
    }

    [Fact]
    public void Forced_page_breaks_start_a_new_page_but_never_a_blank_first_page()
    {
        var first = Para(2, new BoxStyle { BreakBefore = PageBreak.Page });
        var second = Para(2, new BoxStyle { BreakBefore = PageBreak.Page });
        var third = Para(2, new BoxStyle { BreakAfter = PageBreak.Page });
        var fourth = Para(2);
        var placements = Flow(first, second, third, fourth).Paginate().Placements;

        Assert.Equal([0, 1, 1, 2], placements.Select(p => p.Page));
    }

    [Fact]
    public void Adjacent_margins_collapse_to_the_larger_one()
    {
        var a = Para(2, new BoxStyle { Margin = new(0, 0, 10, 0) });
        var b = Para(2, new BoxStyle { Margin = new(25, 0, 0, 0) });
        var placements = Flow(a, b).Paginate().Placements;

        Assert.Equal(placements[0].Bottom + 25, placements[1].Top, 6);
    }

    [Fact]
    public void Negative_margins_are_subtracted_from_the_largest_positive_one()
    {
        var a = Para(2, new BoxStyle { Margin = new(0, 0, 20, 0) });
        var b = Para(2, new BoxStyle { Margin = new(-5, 0, 0, 0) });
        var placements = Flow(a, b).Paginate().Placements;

        Assert.Equal(placements[0].Bottom + 15, placements[1].Top, 6);
    }

    [Fact]
    public void Top_margin_applies_at_the_start_of_the_document()
    {
        var placements = Flow(Para(2, new BoxStyle { Margin = new(15, 0, 0, 0) })).Paginate().Placements;

        Assert.Equal(Top + 15, placements[0].Top, 6);
    }

    [Fact]
    public void Top_margin_is_dropped_after_an_automatic_page_break_but_kept_after_a_forced_one()
    {
        var automatic = Para(4, new BoxStyle { Margin = new(30, 0, 0, 0) });
        var forced = Para(2, new BoxStyle { Margin = new(30, 0, 0, 0), BreakBefore = PageBreak.Page });
        var placements = Flow(Para(15), automatic, forced).Paginate().Placements;

        var a = Assert.Single(placements, p => p.Block == automatic);
        var f = Assert.Single(placements, p => p.Block == forced);
        Assert.Equal((1, Top), (a.Page, a.Top));
        Assert.Equal((2, Top + 30), (f.Page, f.Top));
    }

    [Fact]
    public void First_and_last_child_margins_collapse_through_a_parent_without_padding()
    {
        var child = Para(2, new BoxStyle { Margin = new(30, 0, 40, 0) });
        var after = Para(2);
        var placements = Flow(new ContainerBlock(new BoxStyle { Margin = new(10, 0, 0, 0) }, child), after).Paginate().Placements;

        var c = Assert.Single(placements, p => p.Block == child);
        var n = Assert.Single(placements, p => p.Block == after);
        Assert.Equal(Top + 30, c.Top, 6); // max(10, 30), not 10 + 30.
        Assert.Equal(c.Bottom + 40, n.Top, 6); // The child's bottom margin collapses out of the container.
    }

    [Fact]
    public void Padding_stops_margin_collapsing_through_a_parent()
    {
        var child = Para(2, new BoxStyle { Margin = new(30, 0, 40, 0) });
        var container = new ContainerBlock(new BoxStyle { Margin = new(10, 0, 0, 0), Padding = Thickness.All(5) }, child);
        var after = Para(2);
        var placements = Flow(container, after).Paginate().Placements;

        var c = Assert.Single(placements, p => p.Block == child);
        var n = Assert.Single(placements, p => p.Block == after);
        Assert.Equal(Top + 10 + 5 + 30, c.Top, 6);
        Assert.Equal(c.Bottom + 40 + 5, n.Top, 6);
    }

    [Fact]
    public void Break_after_avoid_keeps_a_heading_with_the_next_paragraph()
    {
        var heading = Para(1, new BoxStyle { BreakAfter = PageBreak.Avoid });
        var body = Para(5);
        var placements = Flow(Para(14), heading, body).Paginate().Placements;

        // The heading would fit (2 lines left) but its paragraph's first two lines would not.
        Assert.Equal(1, Assert.Single(placements, p => p.Block == heading).Page);
        Assert.Equal(1, placements.First(p => p.Block == body).Page);
    }

    [Fact]
    public void Break_inside_avoid_moves_the_whole_block()
    {
        var box = Para(4, new BoxStyle { BreakInside = PageBreakInside.Avoid });
        var placements = Flow(Para(14), box).Paginate().Placements;

        var placed = Assert.Single(placements, p => p.Block == box);
        Assert.Equal((1, 0, 4), (placed.Page, placed.FirstLine, placed.EndLine));
    }

    [Fact]
    public void Every_line_is_placed_exactly_once_in_order()
    {
        var blocks = Enumerable.Range(0, 12).Select(i => Para(3 + (i * 5 % 11), new BoxStyle { Margin = new(i % 3 * 6, 0, 4, 0) })).ToArray();
        var placements = Flow(blocks).Paginate().Placements;

        foreach (var block in blocks)
        {
            var pieces = placements.Where(p => p.Block == block).ToList();
            Assert.Equal(0, pieces[0].FirstLine);
            for (var i = 1; i < pieces.Count; i++)
            {
                Assert.Equal(pieces[i - 1].EndLine, pieces[i].FirstLine);
                Assert.Equal(pieces[i - 1].Page + 1, pieces[i].Page);
            }

            Assert.Equal(((ParagraphBlock)block).Paragraph.Layout(160).Lines.Count, pieces[^1].EndLine);
        }

        Assert.All(placements, p => Assert.True(p.Top >= Top - 1e-6 && p.Bottom <= Bottom + 1e-6, $"{p} is outside the content area"));
        Assert.Equal(placements.OrderBy(p => p.Page).ThenBy(p => p.Top), placements);
    }

    [Fact]
    public void A_line_taller_than_the_page_is_still_placed()
    {
        var huge = new ParagraphBlock(new Paragraph(Text with { LineHeight = LineHeight.Points(500) }).Add("one\ntwo"));

        Assert.Equal(2, Flow(huge).CountPages());
    }

    [Fact]
    public void Boxes_split_across_pages_are_sliced()
    {
        var red = PdfColor.FromRgb(255, 0, 0);
        var box = new ContainerBlock(
            new BoxStyle { Background = PdfColor.FromRgb(200, 230, 255), Border = Borders.All(4, red), Padding = Thickness.All(6) },
            Para(25));
        var flow = Flow(box);
        var fragments = flow.Paginate().Placements.Where(p => p.Block == box).ToList();

        Assert.Equal(2, fragments.Count);
        Assert.Equal((0, Top, Bottom), (fragments[0].Page, fragments[0].Top, fragments[0].Bottom));
        Assert.Equal((1, Top), (fragments[1].Page, fragments[1].Top));

        var pdf = flow.Render().ToArray();
        using var page1 = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        using var page2 = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));
        static bool IsRed(SkiaSharp.SKColor c) => c.Red > 200 && c.Green < 60;

        Assert.True(IsRed(page1.GetPixel(100, 21)), "Top border on the first fragment");
        Assert.False(IsRed(page1.GetPixel(100, 178)), "No bottom border where the box is cut");
        Assert.False(IsRed(page2.GetPixel(100, 21)), "No top border on the continuation");
        Assert.True(IsRed(page2.GetPixel(21, 60)), "Side borders on every fragment");
        Assert.True(IsRed(page2.GetPixel(100, (int)fragments[1].Bottom - 2)), "Bottom border on the last fragment");
    }

    [Fact]
    public void Fixed_width_blocks_are_narrower()
    {
        var narrow = new ParagraphBlock(new Paragraph(Text).Add("A fairly long sentence that has to wrap."), new BoxStyle { Width = 60 });
        var wide = new ParagraphBlock(new Paragraph(Text).Add("A fairly long sentence that has to wrap."));
        var placements = Flow(narrow, wide).Paginate().Placements;

        Assert.True(placements[0].Bottom - placements[0].Top > placements[1].Bottom - placements[1].Top);
    }

    [Fact]
    public void Page_decorator_draws_page_x_of_y_and_text_flows_in_order()
    {
        var blocks = Enumerable.Range(1, 30).Select(i => new ParagraphBlock(new Paragraph(Text).Add($"Paragraph number {i}"), new BoxStyle { Margin = new(0, 0, 6, 0) })).ToArray();
        var flow = Flow(blocks);
        flow.PageDecorator = (canvas, page) =>
            canvas.DrawText($"Page {page.Number} of {page.Count}", PdfFont.Standard(StandardFont.Helvetica), 7, 20, 8);
        var pdf = flow.Render().ToArray();

        ExternalTools.AssertQpdfClean(pdf);
        var text = ExternalTools.ExtractText(pdf);
        var pages = flow.CountPages();
        Assert.True(pages >= 2);
        for (var p = 1; p <= pages; p++)
        {
            Assert.Contains($"Page {p} of {pages}", text, StringComparison.Ordinal);
        }

        var positions = Enumerable.Range(1, 30).Select(i => text.IndexOf($"Paragraph number {i}\n", StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void Margins_that_leave_no_content_area_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new FlowDocument(new PageSetup(new PdfPageSize(100, 100), Thickness.All(60))));

    private static ParagraphBlock Para(int lines, BoxStyle? style = null) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Line {i}"))), style);

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
