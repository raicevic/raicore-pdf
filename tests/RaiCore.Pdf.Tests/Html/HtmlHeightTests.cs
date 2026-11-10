using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlHeightTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    // A 500 x 800 pt page with 50 pt margins: content x 50-450, y 50-750 (700 pt tall).
    private static readonly PageSetup Page = new(new PdfPageSize(500, 800), Thickness.All(50));

    [Fact]
    public void Heights_become_border_box_heights()
    {
        var blocks = Blocks("""
            <div id="a" style="height: 100px; padding: 10px; border: 2px solid">a</div>
            <div id="b" style="box-sizing: border-box; height: 100px; min-height: 50px; max-height: 200px; padding: 10px">b</div>
            <div id="c" style="min-height: 0">c</div>
            """);

        Assert.Equal((100 + 20 + 4) * Px, blocks["a"].Style.Height!.Value, 6);
        Assert.Equal((100 * Px, 50 * Px, 200 * Px), (blocks["b"].Style.Height!.Value, blocks["b"].Style.MinHeight!.Value, blocks["b"].Style.MaxHeight!.Value));
        Assert.Equal(0, blocks["c"].Style.MinHeight); // An explicit 0 is kept (it differs from auto for flex items).
        Assert.Null(blocks["c"].Style.Height);
    }

    [Fact]
    public void Percentage_heights_need_a_definite_containing_height()
    {
        var blocks = Flatten(Flow("""
            <style>html, body { height: 100%; margin: 0 }</style>
            <div style="height: 50%"><div style="height: 50%">quarter</div></div>
            <div><div style="height: 50%">lost</div></div>
            """).Blocks).ToList();

        // html and body fill the 700 pt page area; the nested divs take half of their parent each.
        Assert.Equal([700, 700, 350], blocks.OfType<ContainerBlock>().Select(b => b.Style.Height).OfType<double>());
        Assert.Equal(175, blocks.Single(b => Text(b) == "quarter" && b is ParagraphBlock).Style.Height);
        Assert.Null(blocks.Single(b => Text(b) == "lost" && b is ParagraphBlock).Style.Height); // Its parent's height is auto.
    }

    [Fact]
    public void Viewport_height_units_are_the_page_area()
    {
        // The viewport is a whole number of CSS pixels, so 100vh can be up to a pixel short of the page area.
        Assert.Equal(700, Blocks("<div id='page' style='height: 100vh'>x</div>")["page"].Style.Height!.Value, 0.75);
    }

    [Fact]
    public void An_empty_div_with_a_height_is_a_spacer()
    {
        var flow = Flow("<style>body { margin: 0 }</style><p style='margin: 0'>a</p><div style='height: 40px'></div><p style='margin: 0'>b</p>");
        var placements = flow.Paginate().Placements;
        var (a, b) = (placements.Single(p => Text(p.Block) == "a"), placements.Single(p => Text(p.Block) == "b"));

        Assert.Equal(a.Bottom + (40 * Px), b.Top, 6);
    }

    [Fact]
    public void A_full_page_column_pushes_the_footer_to_the_bottom()
    {
        var flow = Flow("""
            <style>
              html, body { height: 100%; margin: 0 }
              body { display: flex; flex-direction: column }
              main { flex: 1 }
              footer { background: #eee }
            </style>
            <header>Header</header>
            <main>Content</main>
            <footer>Footer</footer>
            """);
        var placements = flow.Paginate().Placements;
        var footer = placements.Single(p => p.FirstLine == -1 && Text(p.Block) == "Footer");

        Assert.Equal(1, flow.CountPages());
        Assert.Equal(750, footer.Bottom, 6);
    }

    [Fact]
    public void Flex_basis_percentages_in_a_column_resolve_in_layout()
    {
        var flex = Flex("<div style='display: flex; flex-direction: column; height: 200px'><div style='flex: 1'>a</div><div style='flex-basis: 25%'>b</div></div>");

        Assert.Equal(FlexBasis.Percent(0), flex.Items[0].Basis);
        Assert.Equal(FlexBasis.Percent(25), flex.Items[1].Basis);
        Assert.Equal(200 * Px, flex.Style.Height);
    }

    [Theory]
    [InlineData("align-content: center", AlignContent.Center)]
    [InlineData("align-content: space-between", AlignContent.SpaceBetween)]
    [InlineData("align-content: flex-end", AlignContent.FlexEnd)]
    [InlineData("align-content: normal", AlignContent.Stretch)]
    [InlineData("place-content: space-evenly start", AlignContent.SpaceEvenly)]
    public void Align_content_values(string css, AlignContent expected)
    {
        Assert.Equal(expected, Flex($"<div style='display: flex; flex-wrap: wrap; {css}'></div>").AlignContent);
    }

    [Fact]
    public void A_row_with_a_height_centers_its_items_vertically()
    {
        var flow = Flow("""
            <style>body { margin: 0 }</style>
            <div style="display: flex; align-items: center; height: 200px; background: #eee"><span style="background: #ccc">Centered</span></div>
            """);
        var item = flow.Paginate().Placements.Single(p => p.FirstLine == -1 && p.Block is ParagraphBlock && Text(p.Block) == "Centered");

        Assert.Equal(50 + (100 * Px), (item.Top + item.Bottom) / 2, 6);
    }

    [Fact]
    public void Images_ignore_box_heights()
    {
        const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var image = Flatten(Flow($"<img style='display: block; height: 40px; min-height: 10px' src='{Pixel}'>").Blocks).OfType<ImageBlock>().Single();

        Assert.Equal(40 * Px, image.Height);
        Assert.Null(image.Style.Height);
        Assert.Null(image.Style.MinHeight);
    }

    private static FlowDocument Flow(string html) => HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Page = Page });

    private static Dictionary<string, Block> Blocks(string html)
    {
        // Each test element's text is its id, so blocks can be found by their text.
        var ids = System.Text.RegularExpressions.Regex.Matches(html, "id=\"([a-z]+)\"|id='([a-z]+)'").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        var blocks = Flatten(Flow(html).Blocks).ToList();
        var result = new Dictionary<string, Block>();
        foreach (var id in ids)
        {
            result[id] = id switch
            {
                "page" => blocks.First(b => b.Style.Height is > 0),
                _ => blocks.First(b => b is ParagraphBlock && Text(b) == id),
            };
        }

        return result;
    }

    private static FlexBlock Flex(string html) => Flatten(Flow(html).Blocks).OfType<FlexBlock>().First();

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });

    private static string Text(Block block) =>
        string.Concat(Flatten([block]).OfType<ParagraphBlock>().Select(p => string.Concat(p.Paragraph.Spans.Select(s => s.Text)))).Trim();
}
