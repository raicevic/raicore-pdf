using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Where bookmarked blocks are recorded and how they become the PDF outline. Uses a 200 x 200 pt page with 20 pt
/// margins (content from y = 20 to 180) and 10 pt lines, so 16 lines fit on a page.
/// </summary>
public class BookmarkLayoutTests
{
    private const double Top = 20;

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));

    [Fact]
    public void Anchor_is_the_top_left_of_the_border_box()
    {
        var heading = Para(1, new BoxStyle { Margin = new(10, 0, 0, 5) }, Mark("Heading"));

        var anchor = Assert.Single(Flow(Para(2), heading).Paginate().Anchors);

        Assert.Same(heading, anchor.Block);
        Assert.Equal((0, 50.0, 25.0), (anchor.Page, anchor.Top, anchor.Left));
    }

    [Fact]
    public void A_heading_that_moves_to_the_next_page_is_recorded_there()
    {
        // 15 lines leave one line of space; the heading needs two (orphans), so it starts the next page.
        var anchor = Assert.Single(Flow(Para(15), Para(2, bookmark: Mark("Heading"))).Paginate().Anchors);

        Assert.Equal((1, Top), (anchor.Page, anchor.Top));
    }

    [Fact]
    public void A_heading_kept_with_the_next_block_is_recorded_where_they_both_go()
    {
        // 14 lines leave two lines of space: enough for the heading, not for it and the start of the next paragraph.
        var heading = Para(1, new BoxStyle { BreakAfter = PageBreak.Avoid }, Mark("Heading"));

        var anchor = Assert.Single(Flow(Para(14), heading, Para(3)).Paginate().Anchors);

        Assert.Equal((1, Top), (anchor.Page, anchor.Top));
    }

    [Fact]
    public void An_anchor_follows_content_that_moves_to_a_later_page()
    {
        // The section's box starts in the last line of space, but its paragraph moves to the next page: the bookmark
        // points at the paragraph, not at the empty space at the bottom of the first page.
        var section = new ContainerBlock(null, new SpacerBlock(5), Para(3)) { Bookmark = Mark("Section") };

        var anchor = Assert.Single(Flow(Para(15), section).Paginate().Anchors);

        Assert.Equal((1, Top), (anchor.Page, anchor.Top));
    }

    [Fact]
    public void An_empty_block_marks_the_current_position()
    {
        var target = new ContainerBlock { Bookmark = Mark("Target") };

        var anchor = Assert.Single(Flow(Para(2), target, Para(1)).Paginate().Anchors);

        Assert.Equal((0, 40.0), (anchor.Page, anchor.Top));
    }

    [Fact]
    public void Flex_items_record_their_own_positions()
    {
        var left = Para(1, bookmark: Mark("Left"));
        var right = Para(1, bookmark: Mark("Right"));
        var row = new FlexBlock(null, new FlexItem(left), new FlexItem(right));
        var first = Para(1, bookmark: Mark("First"));
        var second = Para(1, bookmark: Mark("Second"));
        var column = new FlexBlock { Direction = FlexDirection.Column }.Add(first).Add(second);

        var anchors = Flow(Para(1), row, column).Paginate().Anchors;

        Assert.Equal([left, right, first, second], anchors.Select(a => a.Block));
        Assert.Equal([30.0, 30.0, 40.0, 50.0], anchors.Select(a => a.Top));
        Assert.Equal(20, anchors[0].Left);
        Assert.True(anchors[1].Left > anchors[0].Left);
    }

    [Fact]
    public void Table_cells_record_positions_from_their_rows()
    {
        // A header row, a row whose second cell is vertically centered next to a three-line cell, then one-line rows.
        var header = Para(1, bookmark: Mark("Header"));
        var centered = Para(1, bookmark: Mark("Centered"));
        var late = Para(1, bookmark: Mark("Late"));
        var table = new TableBlock()
            .Add(new TableRow(new TableCell(header)) { IsHeader = true })
            .Add(new TableRow(new TableCell(Para(3)), new TableCell(centered) { VerticalAlignment = VerticalAlignment.Middle }));
        for (var i = 0; i < 20; i++)
        {
            table.Add(new TableRow(new TableCell(i == 15 ? late : Para(1))));
        }

        var anchors = Flow(table).Paginate().Anchors;

        // The repeated header row is recorded once. Page 1: header (20-30), the tall row (30-60), rows 0-11 (60-180);
        // page 2: header, then rows 12, 13, 14 and 15 at 60.
        Assert.Equal([header, centered, late], anchors.Select(a => a.Block));
        Assert.Equal((0, Top), (anchors[0].Page, anchors[0].Top));
        Assert.Equal((0, 40.0), (anchors[1].Page, anchors[1].Top));
        Assert.Equal((1, 60.0), (anchors[2].Page, anchors[2].Top));
        Assert.True(anchors[1].Left > anchors[0].Left);
    }

    [Fact]
    public void Bookmarks_nest_by_level()
    {
        // X(2) A(1) B(3) C(2) D(1, closed) E(2) F(4)
        var flow = Flow(
            Para(1, bookmark: Mark("X", 2)), Para(1, bookmark: Mark("A", 1)), Para(1, bookmark: Mark("B", 3)), Para(1, bookmark: Mark("C", 2)),
            Para(1, bookmark: new Bookmark("D", 1) { IsOpen = false }), Para(1, bookmark: Mark("E", 2)), Para(1, bookmark: Mark("F", 4)));

        var outline = flow.Render().Outline;

        Assert.Equal(["X", "A", "D"], outline.Select(i => i.Title));
        Assert.Equal(["B", "C"], outline[1].Children.Select(i => i.Title));
        Assert.Equal(["E"], outline[2].Children.Select(i => i.Title));
        Assert.Equal(["F"], outline[2].Children[0].Children.Select(i => i.Title));
        Assert.Empty(outline[0].Children);
        Assert.Empty(outline[1].Children[0].Children);
        Assert.Equal([true, true, false], outline.Select(i => i.IsOpen));
    }

    [Fact]
    public void Outline_destinations_are_the_anchors_in_pdf_coordinates()
    {
        var doc = new PdfDocument();
        doc.AddPage(); // Rendering appends: destinations point at the flow's own pages.
        Flow(Para(1, bookmark: Mark("Start")), Para(15), Para(2, bookmark: Mark("Moved"))).RenderTo(doc);

        Assert.Equal(3, doc.Pages.Count);
        Assert.Collection(
            doc.Outline,
            item => Assert.Equal((doc.Pages[1], 180.0), (item.Page, item.Top)),
            item => Assert.Equal((doc.Pages[2], 180.0), (item.Page, item.Top)));
    }

    [Fact]
    public void Blocks_drawn_by_page_decorators_add_no_bookmarks()
    {
        var flow = Flow(Para(1, bookmark: Mark("Body")));
        flow.PageDecorator = (canvas, page) => FlowDocument.DrawBlocks(canvas, page.Setup.Size.Height, [Para(1, bookmark: Mark("Header"))], 20, 0, 160);

        Assert.Equal(["Body"], flow.Render().Outline.Select(i => i.Title));
    }

    [Fact]
    public void Levels_start_at_one() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bookmark("Zero", 0));

    private static Bookmark Mark(string title, int level = 1) => new(title, level);

    private static ParagraphBlock Para(int lines, BoxStyle? style = null, Bookmark? bookmark = null) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Line {i}"))), style) { Bookmark = bookmark };

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
