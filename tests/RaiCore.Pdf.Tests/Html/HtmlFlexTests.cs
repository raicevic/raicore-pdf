using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlFlexTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    // A 500 x 800 pt page with 50 pt margins: content x 50-450, y 50-750. Body margins are removed.
    private static readonly PageSetup Page = new(new PdfPageSize(500, 800), Thickness.All(50));

    [Fact]
    public void Display_flex_maps_the_container_properties()
    {
        var flex = Flex("""
            <div style="display: flex; flex-flow: row-reverse wrap; justify-content: space-between; align-items: center; gap: 10px 20px"></div>
            """);

        Assert.Equal(FlexDirection.RowReverse, flex.Direction);
        Assert.Equal(FlexWrap.Wrap, flex.Wrap);
        Assert.Equal(JustifyContent.SpaceBetween, flex.JustifyContent);
        Assert.Equal(FlexAlignment.Center, flex.AlignItems);
        Assert.Equal(10 * Px, flex.RowGap, 6);
        Assert.Equal(20 * Px, flex.ColumnGap, 6);
    }

    [Theory]
    [InlineData("justify-content: flex-end", JustifyContent.FlexEnd)]
    [InlineData("justify-content: end", JustifyContent.FlexEnd)]
    [InlineData("justify-content: safe center", JustifyContent.Center)]
    [InlineData("justify-content: space-evenly", JustifyContent.SpaceEvenly)]
    [InlineData("place-content: center space-around", JustifyContent.SpaceAround)]
    [InlineData("place-content: center", JustifyContent.Center)]
    public void Justify_content_values(string css, JustifyContent expected)
    {
        Assert.Equal(expected, Flex($"<div style='display: flex; {css}'></div>").JustifyContent);
    }

    [Fact]
    public void Inline_flex_is_a_flex_container_inside_the_line()
    {
        var flow = HtmlToPdf.CreateFlowDocument(
            "<p>Before <span style='display: inline-flex'><b>a</b><i>b</i></span> after</p>", new HtmlConverterOptions { Page = Page });
        var paragraph = Flatten(flow.Blocks).OfType<ParagraphBlock>().Single();

        var box = Assert.Single(paragraph.Paragraph.Spans, s => s.Box is not null).Box!;
        Assert.Equal(2, Assert.IsType<FlexBlock>(box.Content).Items.Count);
        Assert.Equal("Before \uFFFC after", string.Concat(paragraph.Paragraph.Spans.Select(s => s.Text)));
    }

    [Fact]
    public void Child_elements_and_text_runs_become_items()
    {
        var flex = Flex("""
            <div style="display: flex">
              loose <em>text</em>
              <span>span</span>
              <div style="display: none">hidden</div>
              <p>para</p>
            </div>
            """);

        // "loose", <em>, <span> and <p>: an inline element is an item of its own, whitespace between items is dropped.
        Assert.Equal(["loose", "text", "span", "para"], flex.Items.Select(i => Text(i.Content)));
    }

    [Fact]
    public void Item_properties_are_mapped()
    {
        var flex = Flex("""
            <div style="display: flex">
              <div style="flex: 2 3 40%; order: -1; align-self: flex-end; margin-left: auto">a</div>
              <div style="flex: none; flex-basis: content; margin: auto 0">b</div>
              <div style="min-width: 0; max-width: 100px; padding: 0 10px">c</div>
              <div style="box-sizing: border-box; min-width: 50px; height: 30px; padding: 5px">d</div>
            </div>
            """);
        var (a, b, c, d) = (flex.Items[0], flex.Items[1], flex.Items[2], flex.Items[3]);

        Assert.Equal((2.0, 3.0, -1, FlexAlignment.FlexEnd), (a.Grow, a.Shrink, a.Order, a.AlignSelf));
        Assert.Equal(FlexBasis.Points(0.4 * 400), a.Basis); // 40% of the 400 pt content width.
        Assert.Equal(AutoMargins.Left, a.AutoMargins);
        Assert.Equal(0, a.Content.Style.Margin.Left);

        Assert.Equal((0.0, 0.0, FlexBasis.Content), (b.Grow, b.Shrink, b.Basis));
        Assert.Equal(AutoMargins.Top | AutoMargins.Bottom, b.AutoMargins);
        Assert.Null(b.AlignSelf);

        // Content-box sizes become border-box sizes.
        Assert.Equal(0 + (20 * Px), c.Content.Style.MinWidth);
        Assert.Equal((100 + 20) * Px, c.Content.Style.MaxWidth!.Value, 6);
        Assert.Null(c.Content.Style.Width); // max-width does not make a flex item as wide as possible.

        Assert.Equal(50 * Px, d.Content.Style.MinWidth);
        Assert.Equal(30 * Px, d.Content.Style.Height);
    }

    [Fact]
    public void Equal_columns_share_the_width_minus_the_gaps()
    {
        var (flow, flex) = Flow("""
            <div style="display: flex; gap: 20px">
              <div style="flex: 1; background: #eee">One</div>
              <div style="flex: 1; background: #eee">Two</div>
              <div style="flex: 1; background: #eee">Three, which is a longer text</div>
            </div>
            """);
        var placements = flow.Paginate().Placements;
        var width = (400 - (2 * 20 * Px)) / 3;

        var boxes = flex.Items.Select(i => BoxOf(placements, i.Content)).ToList();
        Assert.All(boxes, b => Assert.Equal(width, b.Width, 6));
        Assert.Equal(50, boxes[0].Left, 6);
        Assert.Equal(50 + width + 15, boxes[1].Left, 6);
        Assert.Equal(50 + (2 * (width + 15)), boxes[2].Left, 6);

        // Stretched: every box is as tall as the tallest.
        Assert.Single(boxes.Select(b => Math.Round(b.Bottom - b.Top, 6)).Distinct());
    }

    [Fact]
    public void An_auto_margin_pushes_the_last_item_right()
    {
        var (flow, flex) = Flow("""
            <nav style="display: flex"><span style="background: #ccc; width: 50px">A</span><span style="background: #ccc; width: 50px; margin-left: auto">B</span></nav>
            """);
        var last = BoxOf(flow.Paginate().Placements, flex.Items[1].Content);

        Assert.Equal(450 - (50 * Px), last.Left, 6);
        Assert.Equal(AutoMargins.Left, flex.Items[1].AutoMargins);
        Assert.Equal(0, flex.Items[1].Content.Style.Margin.Left); // Not pre-resolved like a block's auto margin.
    }

    [Fact]
    public void Columns_center_shrink_to_fit_items()
    {
        var (flow, flex) = Flow("""
            <div style="display: flex; flex-direction: column; align-items: center; row-gap: 8px">
              <div style="background: #ccc; width: 100px">a</div>
              <div style="background: #ccc; width: 200px">b</div>
            </div>
            """);
        var placements = flow.Paginate().Placements;
        var (a, b) = (BoxOf(placements, flex.Items[0].Content), BoxOf(placements, flex.Items[1].Content));

        Assert.Equal(250 - (50 * Px), a.Left, 6);
        Assert.Equal(250 - (100 * Px), b.Left, 6);
        Assert.Equal(a.Bottom + (8 * Px), b.Top, 6);
    }

    [Fact]
    public void Nested_flex_containers_and_images_are_items()
    {
        const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var flex = Flex($"""
            <div style="display: flex"><img src="{Pixel}" width="40"><div style="display: flex; flex-direction: column"><b>x</b><b>y</b></div></div>
            """);

        Assert.IsType<ImageBlock>(flex.Items[0].Content);
        var inner = Assert.IsType<FlexBlock>(flex.Items[1].Content);
        Assert.Equal(FlexDirection.Column, inner.Direction);
        Assert.Equal(2, inner.Items.Count);
    }

    [Fact]
    public void List_items_in_a_flex_list_keep_their_markers()
    {
        var flex = Flex("<ol style='display: flex; gap: 10px'><li>a</li><li>b</li></ol>");

        Assert.Equal(["1.\u00A0a", "2.\u00A0b"], flex.Items.Select(i => Text(i.Content)));
    }

    [Fact]
    public void A_flex_running_header_stays_flex()
    {
        var flow = HtmlToPdf.CreateFlowDocument("""
            <style>
              @page { @top-center { content: element(header); } }
              .header { position: running(header); display: flex; justify-content: space-between; }
            </style>
            <div class="header"><span>Logo</span><span>Address</span></div>
            <p>Body</p>
            """, new HtmlConverterOptions { Page = Page });

        Assert.NotNull(flow.PageDecorator);
        var running = Flatten(flow.Blocks).Select(b => b.Marker).OfType<RaiCore.Pdf.Html.Paged.RunningMarker>().Single();
        Assert.IsType<FlexBlock>(Assert.Single(running.Blocks));
    }

    [Fact]
    public void A_sidebar_layout_splits_across_pages_with_the_sidebar_stretched()
    {
        var paragraphs = string.Concat(Enumerable.Range(1, 40).Select(i => $"<p>Paragraph {i} with enough words to wrap onto a second line in the main column of this layout.</p>"));
        var (flow, flex) = Flow($"""
            <div style="display: flex; gap: 10px">
              <aside style="width: 100px; background: #eef">Notes</aside>
              <main style="flex: 1">{paragraphs}</main>
            </div>
            <p>After</p>
            """);
        var placements = flow.Paginate().Placements;

        var sidebar = placements.Where(p => p.Block == flex.Items[0].Content && p.FirstLine == -1).ToList(); // Box fragments.
        Assert.True(sidebar.Count >= 2, $"The sidebar should continue on every page of the layout, got {sidebar.Count} fragment(s).");
        Assert.Equal(Enumerable.Range(0, sidebar.Count), sidebar.Select(p => p.Page));

        var lastText = placements.Last(p => p.Block is ParagraphBlock pb && Text(pb).StartsWith("Paragraph 40", StringComparison.Ordinal));
        // The main column ends below its last paragraph's 1em bottom margin, which stays inside the flex item.
        Assert.Equal(lastText.Page, sidebar[^1].Page);
        Assert.Equal(lastText.Bottom + (16 * Px), sidebar[^1].Bottom, 6);
        var after = placements.Single(p => p.Block is ParagraphBlock pb && Text(pb) == "After");
        Assert.Equal(lastText.Page, after.Page);
        Assert.True(after.Top >= lastText.Bottom);
    }

    [Fact]
    public void Flex_works_inside_table_cells_on_the_body_and_when_empty()
    {
        var pdf = HtmlToPdf.Convert("""
            <body style="display: flex; flex-direction: column; gap: 4px">
              <div style="display: flex"></div>
              <table border="1"><tr><td><div style="display: flex; gap: 6px"><b>Left</b><i style="margin-left: auto">Right</i></div></td></tr></table>
              <p>End</p>
            </body>
            """);

        Assert.Single(pdf.Pages);
        var text = ExternalTools.ExtractText(pdf.ToArray());
        Assert.Contains("Left", text, StringComparison.Ordinal);
        Assert.Contains("Right", text, StringComparison.Ordinal);
        Assert.Contains("End", text, StringComparison.Ordinal);
    }

    private static FlexBlock Flex(string html) => Flow(html).Flex;

    /// <summary>The first box fragment of a block (paragraph blocks also record their text, with line numbers).</summary>
    private static Placement BoxOf(IReadOnlyList<Placement> placements, Block block) => placements.First(p => p.Block == block && p.FirstLine == -1);

    private static (FlowDocument Flow, FlexBlock Flex) Flow(string html)
    {
        var flow = HtmlToPdf.CreateFlowDocument("<style>body { margin: 0 }</style>" + html, new HtmlConverterOptions { Page = Page });
        return (flow, Flatten(flow.Blocks).OfType<FlexBlock>().First());
    }

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });

    private static string Text(Block block) => string.Concat(Flatten([block]).OfType<ParagraphBlock>().Select(Text));

    private static string Text(ParagraphBlock block) => string.Concat(block.Paragraph.Spans.Select(s => s.Text)).Trim();
}
