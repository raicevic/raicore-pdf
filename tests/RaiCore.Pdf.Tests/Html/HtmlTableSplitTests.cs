using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlTableSplitTests
{
    // A 400 x 400 pt page with 40 pt margins: content y 40-360.
    private static readonly PageSetup Page = new(new PdfPageSize(400, 400), Thickness.All(40));

    private static readonly string LongCell = string.Concat(Enumerable.Range(1, 40).Select(i => $"Sentence {i} of a long description. "));

    [Fact]
    public void Break_inside_on_rows_is_mapped()
    {
        var table = Table("""
            <table><tr style="break-inside: avoid"><td>a</td></tr><tr style="page-break-inside: avoid"><td>b</td></tr><tr><td>c</td></tr></table>
            """);

        Assert.Equal([PageBreakInside.Avoid, PageBreakInside.Avoid, PageBreakInside.Auto], table.Rows.Select(r => r.BreakInside));
    }

    [Fact]
    public void A_long_cell_splits_across_pages_with_the_header_repeated()
    {
        var html = $"""
            <table border="1">
              <thead><tr><th>Item</th><th>Description</th></tr></thead>
              <tr><td>1</td><td>{LongCell}{LongCell}</td></tr>
              <tr><td>2</td><td>Short</td></tr>
            </table>
            """;
        var pdf = HtmlToPdf.Convert(html, new HtmlConverterOptions { Page = Page });

        Assert.True(pdf.Pages.Count >= 2, $"Expected the long row to continue on another page, got {pdf.Pages.Count} page(s).");
        var pages = ExternalTools.ExtractText(pdf.ToArray()).Split('\f');
        Assert.All(pages.Take(pdf.Pages.Count), page => Assert.Contains("Description", page, StringComparison.Ordinal));
        Assert.Contains("Sentence 1 ", pages[0], StringComparison.Ordinal);
        Assert.Contains("Sentence 40 ", pages[pdf.Pages.Count - 1], StringComparison.Ordinal);
        Assert.Contains("Short", pages[pdf.Pages.Count - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void Avoided_rows_move_to_the_next_page_whole()
    {
        var flow = HtmlToPdf.CreateFlowDocument($$"""
            <style>body { margin: 0 } p { margin: 0 }</style>
            <div style="height: 250pt"></div>
            <table><tr style="break-inside: avoid"><td>{{LongCell}}</td></tr></table>
            """, new HtmlConverterOptions { Page = Page });
        var table = flow.Blocks.SelectMany(Flatten).OfType<TableBlock>().Single();

        Assert.Equal([1], flow.Paginate().Placements.Where(p => p.Block == table).Select(p => p.Page));
    }

    private static TableBlock Table(string html) =>
        HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Page = Page }).Blocks.SelectMany(Flatten).OfType<TableBlock>().Single();

    private static IEnumerable<Block> Flatten(Block block) =>
        block is ContainerBlock c ? c.Children.SelectMany(Flatten).Prepend(block) : [block];
}
