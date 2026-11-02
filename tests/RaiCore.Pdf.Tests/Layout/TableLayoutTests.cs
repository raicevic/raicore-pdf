using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Uses a 300 x 300 pt page with 20 pt margins: content is 260 pt wide, from y = 20 to 280.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class TableLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 10) { LineHeight = LineHeight.Points(12) };
    private static readonly PageSetup Small = new(new PdfPageSize(300, 300), Thickness.All(20));

    [Fact]
    public void Columns_get_their_preferred_width_when_everything_fits()
    {
        var table = Table(new TableRow(Cell("A"), Cell("Hello world")));
        var layout = Paginated(table); // An auto-width table gets its shrink-to-fit width.

        Assert.Equal(Sans.MeasureText("A", 10), Width(layout, 0), 3);
        Assert.Equal(Sans.MeasureText("Hello world", 10), Width(layout, 1), 3);
    }

    [Fact]
    public void A_table_without_a_width_shrinks_to_fit_its_content()
    {
        var table = Table(new TableRow(Cell("A"), Cell("B")));
        var placement = Flow(table).Paginate().Placements.Single(p => p.Block == table && p.FirstLine == 0);

        var expected = Sans.MeasureText("A", 10) + Sans.MeasureText("B", 10);
        Assert.Equal(expected, Paginated(table).ColumnX[^1], 3);
        Assert.True(placement.Bottom - placement.Top < 20);
    }

    [Fact]
    public void Extra_width_is_shared_in_proportion_to_preferred_widths()
    {
        var table = StyledTable(new BoxStyle { Width = 260 }, new TableRow(Cell("aa"), Cell("aaaaaa")));
        var layout = TableLayouter.Layout(table, 0, 260);

        Assert.Equal(260, layout.ColumnX[^1], 3);
        Assert.Equal(3, Width(layout, 1) / Width(layout, 0), 2);
    }

    [Fact]
    public void Columns_shrink_toward_their_minimum_and_overflow_below_it()
    {
        var table = Table(new TableRow(Cell("Supercalifragilistic expialidocious words"), Cell("x")));

        var squeezed = TableLayouter.Layout(table, 0, 120);
        Assert.Equal(120, squeezed.ColumnX[^1], 3);
        Assert.True(Width(squeezed, 0) >= Sans.MeasureText("Supercalifragilistic", 10) - 1e-6);

        var overflowing = TableLayouter.Layout(table, 0, 20);
        Assert.Equal(Sans.MeasureText("Supercalifragilistic", 10) + Sans.MeasureText("x", 10), overflowing.ColumnX[^1], 3);
    }

    [Fact]
    public void Percentage_and_fixed_columns()
    {
        var table = StyledTable(new BoxStyle { Width = 260 }, new TableRow(Cell("a"), Cell("b"), Cell("c")));
        table.ColumnWidths.Add(TableWidth.Percent(50));
        table.ColumnWidths.Add(TableWidth.Points(40));
        var layout = TableLayouter.Layout(table, 0, 260);

        Assert.Equal(130, Width(layout, 0), 3);
        Assert.Equal(40, Width(layout, 1), 3);
        Assert.Equal(90, Width(layout, 2), 3);
    }

    [Fact]
    public void Fixed_layout_shares_the_rest_equally()
    {
        var table = new TableBlock(new BoxStyle { Width = 260 }) { LayoutMode = TableLayoutMode.Fixed };
        table.Add(new TableRow(Cell("a very long text that would want a lot of room"), Cell("b"), Cell("c")));
        table.ColumnWidths.Add(TableWidth.Points(60));
        var layout = TableLayouter.Layout(table, 0, 260);

        Assert.Equal([60, 100, 100], Enumerable.Range(0, 3).Select(c => Math.Round(Width(layout, c), 3)));
    }

    [Fact]
    public void Separate_borders_add_spacing_around_and_between_cells()
    {
        var table = new TableBlock(new BoxStyle { Width = 260 }) { HorizontalSpacing = 5, VerticalSpacing = 3 };
        table.Add(new TableRow(Cell("a"), Cell("b"))).Add(new TableRow(Cell("c"), Cell("d")));
        var layout = TableLayouter.Layout(table, 0, 260);

        Assert.Equal(5, layout.ColumnX[0]);
        Assert.Equal(260, layout.ColumnX[^1], 3); // 5 + w0 + 5 + w1 + 5
        Assert.Equal((12 * 2) + 3, layout.Height(0, 2), 3);
    }

    [Fact]
    public void Column_spans_widen_the_columns_they_cover()
    {
        var table = Table(
            new TableRow(new TableCell(Para("A much longer heading")) { ColumnSpan = 2 }),
            new TableRow(Cell("x"), Cell("y")));
        var layout = Paginated(table);

        // The shaped width (with kerning), which is what the paragraph occupies.
        var heading = RaiCore.Pdf.Layout.Text.TextShaper.Shape("A much longer heading", Sans).GetWidth(10);
        Assert.Equal(heading, layout.ColumnX[2] - layout.ColumnX[0], 3);
    }

    [Fact]
    public void Row_spans_make_the_last_spanned_row_tall_enough()
    {
        var tall = new TableCell(Para("1\n2\n3\n4")) { RowSpan = 2 }; // 48 pt
        var table = Table(new TableRow(tall, Cell("a")), new TableRow(Cell("b")));
        var layout = TableLayouter.Layout(table, 0, 260);

        Assert.Equal([12, 36], layout.RowHeights);
        Assert.Equal([(0, 2)], layout.Groups);
    }

    [Fact]
    public void Collapsed_borders_keep_the_widest_border()
    {
        var thin = new BoxStyle { Border = Borders.All(1, PdfColor.Black) };
        var thick = new BoxStyle { Border = Borders.All(3, PdfColor.FromRgb(255, 0, 0)) };
        var table = new TableBlock { BorderCollapse = BorderCollapse.Collapse };
        table.Add(new TableRow(new TableCell(Para("a")) { Style = thin }, new TableCell(Para("b")) { Style = thick }));
        table.Add(new TableRow(new TableCell(Para("c")) { Style = thin }, new TableCell(Para("d")) { Style = thin }));
        var layout = TableLayouter.Layout(table, 0, 260);

        Assert.Equal(3, layout.Vertical![0, 1].Width); // Between a (1) and b (3)
        Assert.Equal(1, layout.Vertical[1, 1].Width); // Between c and d
        Assert.Equal(3, layout.Horizontal![1, 1].Width); // Between b (3) and d (1)
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), layout.Horizontal[1, 1].Color);
    }

    [Fact]
    public void Header_rows_repeat_on_every_page_and_rows_are_not_split()
    {
        var table = new TableBlock(new BoxStyle { Width = 260 });
        table.Add(new TableRow(Cell("Header")) { IsHeader = true });
        for (var i = 0; i < 40; i++)
        {
            table.Add(new TableRow(Cell($"Row {i}")));
        }

        var placements = Flow(table).Paginate().Placements.Where(p => p.Block == table && p.FirstLine >= 0).ToList();
        var pages = placements.Max(p => p.Page) + 1;

        Assert.True(pages >= 2);
        for (var page = 0; page < pages; page++)
        {
            var onPage = placements.Where(p => p.Page == page).OrderBy(p => p.Top).ToList();
            Assert.Equal((0, 1), (onPage[0].FirstLine, onPage[0].EndLine)); // Header first.
            Assert.All(onPage, p => Assert.True(p.Bottom <= 280 + 1e-6));
        }

        var bodyRows = placements.Where(p => p.FirstLine > 0).SelectMany(p => Enumerable.Range(p.FirstLine, p.EndLine - p.FirstLine)).ToList();
        Assert.Equal(Enumerable.Range(1, 40), bodyRows); // Every row once, in order.
    }

    [Fact]
    public void Rows_joined_by_a_row_span_move_to_the_next_page_together()
    {
        var table = new TableBlock(new BoxStyle { Width = 260 });
        for (var i = 0; i < 20; i++)
        {
            table.Add(new TableRow(Cell($"Row {i}"), Cell("x")));
        }

        // Rows 18-20 are joined; they do not all fit after 18 rows of 12 pt on a 260 pt page.
        table.Add(new TableRow(new TableCell(Para("span")) { RowSpan = 3 }, Cell("a")));
        table.Add(new TableRow(Cell("b")));
        table.Add(new TableRow(Cell("c")));
        var placements = Flow(table).Paginate().Placements.Where(p => p.Block == table && p.FirstLine >= 0).ToList();

        var group = Assert.Single(placements, p => p.FirstLine == 20);
        Assert.Equal(23, group.EndLine);
        Assert.Equal(1, group.Page);
    }

    [Fact]
    public void Cells_align_their_content_vertically()
    {
        var tall = Cell("1\n2\n3\n4\n5\n6"); // 72 pt
        var middle = new TableCell(Para("M")) { VerticalAlignment = VerticalAlignment.Middle };
        var bottom = new TableCell(Para("B")) { VerticalAlignment = VerticalAlignment.Bottom };
        var table = Table(new TableRow(tall, middle, bottom));
        using var bitmap = Render(Flow(table));
        var layout = Paginated(table);

        int DarkRow(int column, bool fromTop)
        {
            var x0 = 20 + (int)layout.ColumnX[column] + 1;
            var x1 = 20 + (int)layout.ColumnX[column + 1] - 1;
            var rows = Enumerable.Range(20, 72);
            return (fromTop ? rows : rows.Reverse()).First(y => Enumerable.Range(x0, x1 - x0).Any(x => bitmap.GetPixel(x, y).Red < 128));
        }

        Assert.InRange(DarkRow(1, fromTop: true), 20 + 30, 20 + 40); // "M" in the middle third
        Assert.InRange(DarkRow(2, fromTop: false), 20 + 62, 20 + 72); // "B" at the bottom
    }

    [Fact]
    public void Tables_can_be_nested_and_forced_breaks_inside_cells_are_ignored()
    {
        var inner = Table(new TableRow(Cell("in1"), Cell("in2")));
        var forced = new ParagraphBlock(Para("after"), new BoxStyle { BreakBefore = PageBreak.Page });
        var outer = Table(new TableRow(new TableCell(inner, forced), Cell("out")));

        Assert.Equal(1, Flow(outer).CountPages());
        var (min, _) = TableLayouter.MeasureIntrinsic(outer);
        Assert.True(min >= Sans.MeasureText("in1", 10) + Sans.MeasureText("in2", 10));
    }

    [Fact]
    public void Collapsed_table_renders_single_lines_between_cells()
    {
        var border = new BoxStyle { Border = Borders.All(2, PdfColor.Black), Padding = Thickness.All(4) };
        var table = new TableBlock(new BoxStyle { Width = 200 }) { BorderCollapse = BorderCollapse.Collapse };
        table.Add(new TableRow(new TableCell(Para("a")) { Style = border }, new TableCell(Para("b")) { Style = border }));
        using var bitmap = Render(Flow(table));
        var layout = Paginated(table);

        // Around the middle line (x = 20 + ColumnX[1]) only one 2 pt dark band, not two.
        var y = 20 + 10;
        var middle = 20 + (int)Math.Round(layout.ColumnX[1]);
        var dark = Enumerable.Range(middle - 6, 12).Count(x => bitmap.GetPixel(x, y).Red < 100);
        Assert.InRange(dark, 1, 3);
    }

    /// <summary>The layout the paginator uses: the table's shrink-to-fit (or set) width within the 260 pt content.</summary>
    private static TableLayout Paginated(TableBlock table) => TableLayouter.Layout(table, 0, WidthOf(table));

    private static double WidthOf(TableBlock table)
    {
        var (min, max) = TableLayouter.MeasureIntrinsic(table);
        return table.Style.Width ?? Math.Max(min, Math.Min(260, max));
    }

    private static double Width(TableLayout layout, int column) => layout.ColumnX[column + 1] - layout.ColumnX[column] - layout.SpacingX;

    private static Paragraph Para(string text) => new Paragraph(Text).Add(text);

    private static TableCell Cell(string text) => new(Para(text));

    private static TableBlock Table(params TableRow[] rows) => StyledTable(null, rows);

    private static TableBlock StyledTable(BoxStyle? style, params TableRow[] rows)
    {
        var table = new TableBlock(style);
        foreach (var row in rows)
        {
            table.Add(row);
        }

        return table;
    }

    private static FlowDocument Flow(Block block) => new FlowDocument(Small).Add(block);

    private static SKBitmap Render(FlowDocument flow) => Conversion.ToImage(flow.Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
}
