using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Repeated table header rows. Uses a 200 x 200 pt page with 20 pt margins (content y 20-180, 160 pt) and 10 pt
/// lines. Pagination runs with at most 20 pages, so a header that keeps a table from ending fails fast.
/// </summary>
public class TableHeaderTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));

    [Fact]
    public void A_short_header_repeats_on_every_page()
    {
        var table = Table(headerLines: 2, rows: 40);

        var placements = Layout(table);

        Assert.Equal([0, 1, 2], HeaderPages(placements, table));
        Assert.Equal(40, BodyRows(placements, table));
    }

    [Theory]
    [InlineData(20, 1)] // Taller than a page: it is cut at the bottom of the first.
    [InlineData(16, 1)] // As tall as a page.
    [InlineData(15, 2)] // Leaving no room for a row (#249: spreadsheet print titles).
    public void A_header_without_room_for_the_body_is_shown_once(int headerLines, int rowLines)
    {
        var table = Table(headerLines, rows: 12, rowLines);

        var placements = Layout(table);

        Assert.Equal([0], HeaderPages(placements, table));
        Assert.Equal(12, BodyRows(placements, table));
        Assert.Equal(rowLines == 1 ? [1] : [1, 2], BodyPagesOf(placements, table));
    }

    [Theory]
    [InlineData(14, 5)] // 140 pt of header and two 10 pt rows a page.
    [InlineData(15, 3)] // Room for exactly one row a page.
    public void A_header_leaving_room_for_a_row_repeats(int headerLines, int rows)
    {
        var table = Table(headerLines, rows);

        var placements = Layout(table);

        Assert.Equal([0, 1, 2], HeaderPages(placements, table));
        Assert.Equal([0, 1, 2], BodyPagesOf(placements, table));
    }

    [Fact]
    public void A_row_that_cannot_start_below_a_repeated_header_still_ends_the_table()
    {
        // The second row starts with an 80 pt line, which fits on a page but not below the 100 pt header.
        var table = Table(headerLines: 10, rows: 1);
        table.Add(new TableRow(new TableCell(new ParagraphBlock(new Paragraph(Text with { LineHeight = LineHeight.Points(80) }).Add("Tall")))));
        table.Add(new TableRow(new TableCell(Para(1))));

        var placements = Layout(table);

        Assert.Equal(3, BodyRows(placements, table));
        Assert.True(placements.Where(p => p.Block == table).Max(p => p.Page) <= 2);
    }

    private static TableBlock Table(int headerLines, int rows, int rowLines = 1)
    {
        var table = new TableBlock();
        table.Add(new TableRow(new TableCell(Para(headerLines))) { IsHeader = true });
        for (var i = 0; i < rows; i++)
        {
            table.Add(new TableRow(new TableCell(Para(rowLines))));
        }

        return table;
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

        var limits = ResourceLimits.Default with { MaxPages = 20 };
        using (limits.Enforce(TestContext.Current.CancellationToken))
        {
            return flow.Paginate().Placements;
        }
    }

    private static List<int> HeaderPages(IReadOnlyList<Placement> placements, TableBlock table) =>
        [.. placements.Where(p => p.Block == table && p.FirstLine == 0).Select(p => p.Page).Distinct().Order()];

    private static List<int> BodyPagesOf(IReadOnlyList<Placement> placements, TableBlock table) =>
        [.. placements.Where(p => p.Block == table && p.FirstLine > 0).Select(p => p.Page).Distinct().Order()];

    /// <summary>How many body rows were placed (whole or the first piece of a split row).</summary>
    private static int BodyRows(IReadOnlyList<Placement> placements, TableBlock table) =>
        placements.Where(p => p.Block == table && p.FirstLine > 0).SelectMany(p => Enumerable.Range(p.FirstLine, p.EndLine - p.FirstLine)).Distinct().Count();
}
