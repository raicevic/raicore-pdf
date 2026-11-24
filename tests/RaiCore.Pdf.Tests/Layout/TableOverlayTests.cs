using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// A table's overlay (internal, for spreadsheet drawings): it learns where the table's columns and rows went, the
/// pieces of rows split across pages and the repeated header rows, and what it draws goes over the page's content.
/// </summary>
public class TableOverlayTests
{
    private static readonly TextStyle Style = new(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);

    private static TableBlock Table(int rows, Func<TablePlacement, IEnumerable<(int Page, Action<PdfCanvas, double> Draw)>>? overlay, double rowHeight = 20, int headers = 1)
    {
        var table = new TableBlock(new BoxStyle { Width = 300 })
        {
            LayoutMode = TableLayoutMode.Fixed,
            BorderCollapse = BorderCollapse.Collapse,
            ColumnWidths = [100, 200],
            Overlay = overlay,
        };
        for (var r = 0; r < rows; r++)
        {
            table.Add(new TableRow(new TableCell(new Paragraph(Style).Add($"R{r}")), new TableCell()) { IsHeader = r < headers, MinHeight = rowHeight });
        }

        return table;
    }

    [Fact]
    public void The_overlay_gets_the_columns_and_every_rows_place_with_the_repeated_headers_marked()
    {
        TablePlacement? seen = null;
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(400, 300), Thickness.All(50)));
        flow.Add(Table(12, placement =>
        {
            seen = placement;
            return [];
        }));
        Assert.Equal(2, flow.Render().Pages.Count);
        Assert.NotNull(seen);
        Assert.Equal([50, 150, 350], seen.ColumnEdges);

        // 200 points a page: the header and 9 rows, then the header again and the last 2 rows.
        var rows = seen.Rows.OrderBy(r => r.Page).ThenBy(r => r.Top).ToList();
        Assert.Equal(13, rows.Count);
        Assert.Equal(new PlacedRow(0, 0, 50, 20, false), rows[0]);
        Assert.Equal(new PlacedRow(9, 0, 230, 20, false), rows[9]);
        Assert.Equal(new PlacedRow(0, 1, 50, 20, true), rows[10]);
        Assert.Equal(new PlacedRow(10, 1, 70, 20, false), rows[11]);
        Assert.Equal(new PlacedRow(11, 1, 90, 20, false), rows[12]);
    }

    [Fact]
    public void Rows_split_across_pages_are_given_piece_by_piece()
    {
        // A row of 30 lines does not fit: it is split.
        TablePlacement? seen = null;
        var table = new TableBlock(new BoxStyle { Width = 200 }) { ColumnWidths = [200], LayoutMode = TableLayoutMode.Fixed, Overlay = p => { seen = p; return []; } };
        table.Add(new TableRow(new TableCell(new Paragraph(Style).Add(string.Join("\n", Enumerable.Range(0, 30).Select(i => $"Line {i}"))))));
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(300, 300), Thickness.All(50)));
        flow.Add(new SpacerBlock(100));
        flow.Add(table);
        Assert.True(flow.Render().Pages.Count >= 2);
        Assert.NotNull(seen);
        Assert.True(seen.Rows.Count >= 2);
        Assert.All(seen.Rows, r => Assert.Equal((0, false), (r.Row, r.Repeated)));
        Assert.Equal(Enumerable.Range(0, seen.Rows.Count), seen.Rows.Select(r => r.Page));
        Assert.Equal(150, seen.Rows[0].Top);
    }

    [Fact]
    public void What_the_overlay_draws_goes_on_its_page_over_the_content()
    {
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(400, 300), Thickness.All(50)));
        flow.Add(Table(12, _ =>
        [
            (1, (canvas, height) => canvas.SetFillColor(PdfColor.FromRgb(255, 0, 0)).Rectangle(60, height - 80, 10, 10).Fill()),
            (7, (canvas, _) => canvas.Rectangle(0, 0, 1, 1).Fill()), // No such page: left out.
        ]));
        flow.Add(new ParagraphBlock(new Paragraph(Style).Add("After")));
        var pdf = flow.Render().ToArray();
        var reader = PdfReader.Open(pdf);
        var first = System.Text.Encoding.Latin1.GetString(reader.Pages[0].GetContent());
        var second = System.Text.Encoding.Latin1.GetString(reader.Pages[1].GetContent());
        Assert.DoesNotContain("1 0 0 rg", first, StringComparison.Ordinal);
        Assert.Contains("1 0 0 rg", second, StringComparison.Ordinal);

        // After the table's rows and the paragraph that follows it on the page.
        Assert.True(second.IndexOf("1 0 0 rg", StringComparison.Ordinal) > second.LastIndexOf("Tj", StringComparison.Ordinal));
    }

    [Fact]
    public void Tables_without_an_overlay_are_laid_out_as_before()
    {
        var with = new FlowDocument(new PageSetup(new PdfPageSize(400, 300), Thickness.All(50)));
        with.Add(Table(12, _ => []));
        var without = new FlowDocument(new PageSetup(new PdfPageSize(400, 300), Thickness.All(50)));
        without.Add(Table(12, null));
        Assert.Equal(without.Render().ToArray().Length, with.Render().ToArray().Length);
    }
}
