using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Table rows split across pages. Uses a 200 x 200 pt page with 20 pt margins (content y 20-180, 160 pt) and 10 pt
/// lines, so 16 lines fit on a page. Cells have no padding unless a test says so.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class TableSplitTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));

    [Fact]
    public void A_row_that_does_not_fit_continues_on_the_next_page()
    {
        var (tall, small) = (Para(10), Para(2));
        var table = new TableBlock().Add(new TableRow(new TableCell(tall), new TableCell(small)));
        var after = Para(1);
        var placements = Layout(Para(10), table, after);

        // 60 pt are left: six lines here, four on the next page; the short cell stays on the first page.
        Assert.Equal([(0, 0, 6), (1, 6, 10)], Lines(placements, tall));
        Assert.Equal([(0, 0, 2)], Lines(placements, small));
        Assert.Equal([(0, 120.0, 180.0), (1, 20.0, 60.0)], Fragments(placements, table));
        Assert.Equal((1, 60.0), placements.Where(p => p.Block == after).Select(p => (p.Page, p.Top)).Single());
    }

    [Fact]
    public void Header_rows_repeat_above_the_continuation()
    {
        var body = Para(30);
        var table = new TableBlock()
            .Add(new TableRow(new TableCell(Para(1))) { IsHeader = true })
            .Add(new TableRow(new TableCell(body)));
        var placements = Layout(table);

        Assert.Equal([(0, 20.0, 30.0, 0, 1), (0, 30.0, 180.0, 1, 2), (1, 20.0, 30.0, 0, 1), (1, 30.0, 180.0, 1, 2)],
            placements.Where(p => p.Block == table).Select(p => (p.Page, p.Top, p.Bottom, p.FirstLine, p.EndLine)).Order());
        Assert.Equal([(0, 0, 15), (1, 15, 30)], Lines(placements, body));
    }

    [Fact]
    public void Pages_created_by_any_cell_get_the_header()
    {
        // The second cell is the one that needs a third page.
        var (shorter, longer) = (Para(20), Para(40));
        var header = Para(1);
        var table = new TableBlock()
            .Add(new TableRow(new TableCell(header)) { IsHeader = true })
            .Add(new TableRow(new TableCell(shorter), new TableCell(longer)));
        var placements = Layout(table);

        Assert.Equal([0, 1, 2], placements.Where(p => p.Block == table && p.FirstLine == 0).Select(p => p.Page));
        Assert.Equal([(0, 0, 15), (1, 15, 30), (2, 30, 40)], Lines(placements, longer));
        Assert.Equal(30, placements.Single(p => p.Block == longer && p.Page == 2).Top); // Below the header.
    }

    [Fact]
    public void A_row_whose_cells_cannot_be_split_moves_whole()
    {
        // Three lines cannot be split with orphans = widows = 2, so the row moves although two lines would fit.
        var cell = Para(3);
        var table = new TableBlock().Add(new TableRow(new TableCell(cell)));
        var placements = Layout(Para(14), table);

        Assert.Equal([(1, 20.0, 50.0)], Fragments(placements, table)); // Placed whole: no fragment on the first page.
    }

    [Fact]
    public void Break_inside_avoid_keeps_a_row_whole_when_it_fits_on_a_page()
    {
        var cell = Para(10);
        var table = new TableBlock().Add(new TableRow(new TableCell(cell)) { BreakInside = PageBreakInside.Avoid });
        var placements = Layout(Para(10), table);

        Assert.Equal([(1, 20.0, 120.0)], Fragments(placements, table));
    }

    [Fact]
    public void A_row_taller_than_a_page_splits_even_with_break_inside_avoid()
    {
        var cell = Para(20);
        var table = new TableBlock().Add(new TableRow(new TableCell(cell)) { BreakInside = PageBreakInside.Avoid });
        var placements = Layout(Para(10), table);

        Assert.Equal([(1, 0, 16), (2, 16, 20)], Lines(placements, cell));
    }

    [Fact]
    public void Rows_joined_by_a_row_span_move_together()
    {
        var spanning = Para(4);
        var table = new TableBlock()
            .Add(new TableRow(new TableCell(spanning) { RowSpan = 2 }, new TableCell(Para(2))))
            .Add(new TableRow(new TableCell(Para(2))));
        var placements = Layout(Para(13), table);

        Assert.Equal([(1, 20.0, 60.0)], Fragments(placements, table));
    }

    [Fact]
    public void A_table_starts_on_the_page_when_the_start_of_its_first_row_fits()
    {
        var body = Para(10);
        var table = new TableBlock()
            .Add(new TableRow(new TableCell(Para(1))) { IsHeader = true })
            .Add(new TableRow(new TableCell(body)));
        var placements = Layout(Para(10), table);

        // 60 pt left: the header and five lines of the row.
        Assert.Equal((0, 120.0), placements.Where(p => p.Block == table).Select(p => (p.Page, p.Top)).First());
        Assert.Equal([(0, 0, 5), (1, 5, 10)], Lines(placements, body));
    }

    [Fact]
    public void Cell_padding_and_spacing_are_kept_around_the_split()
    {
        var cell = Para(20);
        var table = new TableBlock { HorizontalSpacing = 4, VerticalSpacing = 4 }
            .Add(new TableRow(new TableCell(cell) { Style = new BoxStyle { Padding = Thickness.All(5) } }));
        var placements = Layout(table);

        // Spacing 4 and padding 5 above the first line; the continuation page starts with the spacing, then the cell.
        Assert.Equal((0, 29.0), placements.Where(p => p.Block == cell).Select(p => (p.Page, p.Top)).First());
        Assert.Equal([(0, 24.0, 180.0), (1, 24.0, 79.0)], Fragments(placements, table));
    }

    [Fact]
    public void Split_rows_draw_their_borders_like_sliced_boxes()
    {
        // Collapsed 2 pt black borders around one cell; half of each line is inside the cell, so 15 lines fit.
        var black = PdfColor.FromRgb(0, 0, 0);
        var table = new TableBlock(new BoxStyle { Width = 160 }) { BorderCollapse = BorderCollapse.Collapse }
            .Add(new TableRow(new TableCell(Para(20)) { Style = new BoxStyle { Border = Borders.All(2, black), Padding = new Thickness(0, 0, 0, 10) } }));
        var pdf = new FlowDocument(Small).Add(table).Render().ToArray();

        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));
        var dark = new SKColor(0, 0, 0);

        Assert.Equal(dark, first.GetPixel(100, 20));           // Top line on the first page.
        Assert.NotEqual(dark, first.GetPixel(100, 179));       // No bottom line where the row continues.
        Assert.Equal(dark, first.GetPixel(20, 170));            // Left line runs to the bottom.
        Assert.NotEqual(dark, second.GetPixel(100, 20));       // No top line on the continuation.
        Assert.Equal(dark, second.GetPixel(20, 30));            // Left line continues.
        Assert.Equal(dark, second.GetPixel(100, 70));           // Bottom line where the row ends: 5 lines + 1 pt inset.
    }

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

    private static List<(int Page, int First, int End)> Lines(IReadOnlyList<Placement> placements, ParagraphBlock block) =>
        [.. placements.Where(p => p.Block == block).Select(p => (p.Page, p.FirstLine, p.EndLine))];

    private static List<(int Page, double Top, double Bottom)> Fragments(IReadOnlyList<Placement> placements, TableBlock table) =>
        [.. placements.Where(p => p.Block == table).Select(p => (p.Page, p.Top, p.Bottom))];
}
