using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlTableTests
{
    private const double Px = 0.75;

    [Fact]
    public void Maps_sections_with_headers_first_and_footers_last()
    {
        var table = Table("""
            <table>
              <tfoot><tr><td>foot</td></tr></tfoot>
              <tbody><tr><td>body 1</td></tr><tr><td>body 2</td></tr></tbody>
              <thead><tr><th>head</th></tr></thead>
            </table>
            """);

        Assert.Equal(["head", "body 1", "body 2", "foot"], table.Rows.Select(r => CellText(r.Cells[0])));
        Assert.Equal([true, false, false, false], table.Rows.Select(r => r.IsHeader));
    }

    [Fact]
    public void Rows_directly_in_the_table_are_body_rows()
    {
        var table = Table("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td></tr></table>");

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(2, table.Rows[0].Cells.Count);
    }

    [Fact]
    public void Reads_spans_alignment_and_cell_styles()
    {
        var table = Table("""
            <table style="border-collapse: collapse">
              <tr style="background: #eeeeee"><td colspan="2" rowspan="3" style="padding: 8px; border: 2px solid red; vertical-align: bottom">x</td><td valign="top">y</td></tr>
              <tr><th>h</th></tr>
            </table>
            """);

        var cell = table.Rows[0].Cells[0];
        Assert.Equal((2, 3), (cell.ColumnSpan, cell.RowSpan)); // As written; the grid clips it to the 2 rows that exist.
        Assert.Equal(2, TableLayouter.BuildGrid(table).Cells[0].RowSpan);
        Assert.Equal(VerticalAlignment.Bottom, cell.VerticalAlignment);
        Assert.Equal(8 * Px, cell.Style.Padding.Top, 3);
        Assert.Equal(2 * Px, cell.Style.Border.Top.Width, 3);
        Assert.Equal(VerticalAlignment.Top, table.Rows[0].Cells[1].VerticalAlignment);
        Assert.Equal(VerticalAlignment.Middle, table.Rows[1].Cells[0].VerticalAlignment); // The default
        Assert.Equal(PdfColor.FromRgb(238, 238, 238), table.Rows[0].Background);
        Assert.Equal(BorderCollapse.Collapse, table.BorderCollapse);
        Assert.Equal(700, CellParagraph(table.Rows[1].Cells[0]).Style.Fonts[0].Weight); // th is bold
        Assert.Equal(TextAlignment.Center, CellParagraph(table.Rows[1].Cells[0]).Alignment); // and centered
    }

    [Fact]
    public void Uses_border_spacing_and_the_default_two_pixels()
    {
        Assert.Equal((2 * Px, 2 * Px), Spacing(Table("<table><tr><td>a</td></tr></table>")));
        Assert.Equal((4 * Px, 6 * Px), Spacing(Table("<table style='border-spacing: 4px 6px'><tr><td>a</td></tr></table>")));
    }

    [Fact]
    public void Applies_legacy_border_cellpadding_cellspacing_and_width_attributes()
    {
        var table = Table("<table border='1' cellpadding='5' cellspacing='0' width='50%'><tr><td align='right'>a</td></tr></table>", 400);

        Assert.Equal(1 * Px, table.Style.Border.Top.Width, 3);
        Assert.Equal(1 * Px, table.Rows[0].Cells[0].Style.Border.Left.Width, 3);
        Assert.Equal(5 * Px, table.Rows[0].Cells[0].Style.Padding.Left, 3);
        Assert.Equal((0, 0), Spacing(table));
        Assert.Equal(200, table.Style.Width!.Value, 3);
        Assert.Equal(TextAlignment.Right, CellParagraph(table.Rows[0].Cells[0]).Alignment);
    }

    [Fact]
    public void Reads_column_widths_from_col_elements()
    {
        var table = Table("<table><colgroup><col style='width: 40px'><col span='2' width='20%'></colgroup><tr><td>a</td><td>b</td><td>c</td></tr></table>");

        Assert.Equal([TableWidth.Points(30), TableWidth.Percent(20), TableWidth.Percent(20)], table.ColumnWidths);
    }

    [Fact]
    public void Captions_come_before_the_table_and_auto_margins_center_it()
    {
        var blocks = Flatten(Blocks("<table style='margin: 0 auto'><caption>Prices</caption><tr><td>a</td></tr></table>")).ToList();
        var caption = blocks.OfType<ParagraphBlock>().First(p => Text(p) == "Prices");
        var table = blocks.OfType<TableBlock>().Single();

        Assert.True(blocks.IndexOf(caption) < blocks.IndexOf(table));
        Assert.Equal(TextAlignment.Center, table.Alignment);
        Assert.Equal(TextAlignment.Center, caption.Paragraph.Alignment);
    }

    [Fact]
    public void Long_tables_repeat_the_header_and_keep_rows_in_order()
    {
        var rows = string.Concat(Enumerable.Range(1, 120).Select(i => $"<tr><td>Item {i:D3}</td><td>{i * 10} kr.</td></tr>"));
        var html = $"<table style='width: 100%; border-collapse: collapse'><thead><tr><th>Item</th><th>Price</th></tr></thead><tbody>{rows}</tbody></table>";
        var pdf = HtmlToPdf.ConvertToBytes(html);

        ExternalTools.AssertQpdfClean(pdf);
        var text = ExternalTools.ExtractText(pdf);
        var pages = text.Split('\f', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(pages.Length >= 2);
        Assert.All(pages, page => Assert.Contains("Item Price", page, StringComparison.Ordinal)); // Header on every page.
        var positions = Enumerable.Range(1, 120).Select(i => text.IndexOf($"Item {i:D3}", StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
    }

    // ---- helpers ----

    private static IReadOnlyList<Block> Blocks(string html, double contentWidth = 400) =>
        HtmlToPdf.CreateFlowDocument("<body style='margin: 0'>" + html, new HtmlConverterOptions
        {
            Page = new PageSetup(new PdfPageSize(contentWidth + 100, 800), Thickness.All(50)),
        }).Blocks;

    private static TableBlock Table(string html, double contentWidth = 400) => Flatten(Blocks(html, contentWidth)).OfType<TableBlock>().Single();

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);

    private static (double, double) Spacing(TableBlock table) => (table.HorizontalSpacing, table.VerticalSpacing);

    private static Paragraph CellParagraph(TableCell cell) => cell.Content.OfType<ParagraphBlock>().Single().Paragraph;

    private static string CellText(TableCell cell) => string.Concat(CellParagraph(cell).Spans.Select(s => s.Text)).Trim();

    private static string Text(ParagraphBlock block) => string.Concat(block.Paragraph.Spans.Select(s => s.Text)).Trim();
}
