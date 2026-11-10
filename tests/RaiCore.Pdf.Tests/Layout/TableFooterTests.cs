using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Repeated table footers. Uses a 200 x 200 pt page with 20 pt margins (content y 20-180, 160 pt) and 10 pt lines;
/// footers up to a quarter of the page (40 pt) repeat.
/// </summary>
public class TableFooterTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));

    [Fact]
    public void The_footer_repeats_below_the_body_on_every_page()
    {
        // Rows: header 0, body 1-30, footer 31. 14 body rows fit between the header and the reserved footer.
        var table = Table(header: true, footerLines: 1, Enumerable.Range(0, 30).Select(_ => Para(1)).ToArray());
        var placements = Layout(table);

        Assert.Equal([(0, 170.0), (1, 170.0), (2, 50.0)], Footers(placements, table));
        Assert.Equal([(0, 1, 15), (1, 15, 29), (2, 29, 31)], BodyPages(placements, table));
    }

    [Fact]
    public void The_footer_follows_the_piece_of_a_split_row()
    {
        var body = Para(40);
        var table = Table(header: true, footerLines: 1, body);
        var placements = Layout(table);

        Assert.Equal([(0, 0, 14), (1, 14, 28), (2, 28, 40)], placements.Where(p => p.Block == body).Select(p => (p.Page, p.FirstLine, p.EndLine)));
        Assert.Equal([(0, 170.0), (1, 170.0), (2, 150.0)], Footers(placements, table));
    }

    [Fact]
    public void A_footer_taller_than_a_quarter_page_appears_only_at_the_end()
    {
        var table = Table(header: false, footerLines: 5, Enumerable.Range(0, 30).Select(_ => Para(1)).ToArray());
        var placements = Layout(table);

        Assert.Equal([(1, 160.0)], Footers(placements, table)); // 16 rows on the first page, 14 + footer on the second.
    }

    [Fact]
    public void Content_after_the_table_and_boxes_around_it_use_the_whole_page()
    {
        var table = Table(header: false, footerLines: 1, Enumerable.Range(0, 20).Select(_ => Para(1)).ToArray());
        var box = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(230, 230, 230) }, table);
        var after = Para(9);
        var placements = Layout(box, after);

        // 15 rows and the footer on the first page; 5 rows and the footer on the second, then the 9 lines from 80...
        Assert.Equal([(0, 170.0), (1, 70.0)], Footers(placements, table));
        Assert.Equal((1, 80.0, 170.0), placements.Where(p => p.Block == after).Select(p => (p.Page, p.Top, p.Bottom)).Single());

        // ... and the box's first piece reaches the bottom of the page, below the reserved footer space.
        Assert.Equal(180, placements.Single(p => p.Block == box && p.Page == 0).Bottom);
    }

    [Fact]
    public void A_table_starts_on_the_next_page_when_its_footer_would_not_fit_too()
    {
        // 20 pt are left: the header and a row would fit, but not the footer as well.
        var table = Table(header: true, footerLines: 1, Para(1), Para(1));
        var placements = Layout(Para(14), table);

        Assert.All(placements.Where(p => p.Block == table), p => Assert.Equal(1, p.Page));
    }

    [Fact]
    public void A_table_that_fits_places_its_footer_once()
    {
        var table = Table(header: true, footerLines: 1, Para(1), Para(1));

        Assert.Equal([(0, 50.0)], Footers(Layout(table), table));
    }

    private static TableBlock Table(bool header, int footerLines, params ParagraphBlock[] body)
    {
        var table = new TableBlock();
        if (header)
        {
            table.Add(new TableRow(new TableCell(Para(1))) { IsHeader = true });
        }

        foreach (var row in body)
        {
            table.Add(new TableRow(new TableCell(row)));
        }

        return table.Add(new TableRow(new TableCell(Para(footerLines))) { IsFooter = true });
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

    private static List<(int Page, double Top)> Footers(IReadOnlyList<Placement> placements, TableBlock table) =>
        [.. placements.Where(p => p.Block == table && p.EndLine == table.Rows.Count && table.Rows[p.FirstLine].IsFooter).Select(p => (p.Page, p.Top)).Order()];

    /// <summary>The body rows on each page, as (page, first row, end row).</summary>
    private static List<(int Page, int First, int End)> BodyPages(IReadOnlyList<Placement> placements, TableBlock table) =>
        [.. placements.Where(p => p.Block == table && p.FirstLine >= 0 && !table.Rows[p.FirstLine].IsHeader && !table.Rows[p.FirstLine].IsFooter)
            .GroupBy(p => p.Page).Select(g => (g.Key, g.Min(p => p.FirstLine), g.Max(p => p.EndLine))).Order()];
}
