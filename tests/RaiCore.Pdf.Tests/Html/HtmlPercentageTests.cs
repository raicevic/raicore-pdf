using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// Percentages (also in <c>calc()</c>) resolve against the width the containing block actually gets in layout: a flex
/// item after flexing, a shrink-to-fit float or inline box, a table cell. A 500 x 800 pt page with 50 pt margins and no
/// body margin gives 400 pt of content width starting at x = 50.
/// </summary>
public class HtmlPercentageTests
{
    private const string NoBodyMargin = "<style>body { margin: 0 }</style>";
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
    private static readonly PageSetup Page = new(new PdfPageSize(500, 800), Thickness.All(50));

    /// <summary>Two items of 400 pt shrink equally into 300 pt: the first one is 150 pt wide, at x = 50.</summary>
    private static string Shrunk(string content) => $"""
        <div style="display: flex; width: 300pt">
          <div style="flex: 1 1 400pt; background: #eee">{content}</div>
          <div style="flex: 1 1 400pt">other</div>
        </div>
        """;

    [Fact]
    public void A_percentage_width_follows_the_shrunk_flex_item()
    {
        // The repro of issue #90: the child is 50% of 150 pt, not of the 300 pt the item had before shrinking.
        var (flow, placements) = Layout(Shrunk("""<div style="width: 50%; background: #ccc">child</div>"""));

        Assert.Equal(150, Box(flow, placements, "child", depth: 1).Width, 6);
        Assert.Equal((50.0, 75.0), Horizontal(Box(flow, placements, "child")));
    }

    [Fact]
    public void Nested_percentages_resolve_level_by_level()
    {
        var (flow, placements) = Layout(Shrunk("""
            <div style="width: 50%; background: #ddd"><div style="width: 50%; background: #ccc">inner</div></div>
            """));

        Assert.Equal(75, Box(flow, placements, "inner", depth: 1).Width, 6);
        Assert.Equal(37.5, Box(flow, placements, "inner").Width, 6);
    }

    [Theory]
    [InlineData("width: calc(50% - 10pt)", 65.0)]
    [InlineData("width: calc((100% - 2 * 15pt) / 3)", 40.0)]
    [InlineData("width: calc(50% + 10%)", 90.0)]
    [InlineData("max-width: 40%", 60.0)]
    [InlineData("width: 10pt; min-width: 80%", 120.0)]
    [InlineData("max-width: calc(100% - 100pt)", 50.0)]
    [InlineData("width: 50%; padding: 0 10%", 105.0)] // Content-box: 75 pt plus 2 x 15 pt of padding.
    [InlineData("width: 50%; padding: 0 10%; box-sizing: border-box", 75.0)]
    [InlineData("margin-right: 20%", 120.0)] // An auto width fills what the margins leave.
    public void Sizes_with_percentages_resolve_against_the_flexed_width(string css, double expected)
    {
        var (flow, placements) = Layout(Shrunk($"""<div style="{css}; background: #ccc">child</div>"""));

        Assert.Equal(expected, Box(flow, placements, "child").Width, 6);
    }

    [Theory]
    [InlineData("margin-left: 10%", 65.0, 135.0)]
    [InlineData("width: 50%; margin: 0 auto", 87.5, 75.0)] // Centered: 37.5 pt on each side.
    [InlineData("width: 50%; margin-left: auto", 125.0, 75.0)]
    [InlineData("width: 100pt; margin: 0 auto", 75.0, 100.0)] // A fixed width stays centered in the shrunk item.
    [InlineData("padding-left: 10%", 50.0, 150.0)]
    public void Margins_and_padding_with_percentages_follow_the_flexed_width(string css, double left, double width)
    {
        var (flow, placements) = Layout(Shrunk($"""<div style="{css}; background: #ccc">child</div>"""));

        Assert.Equal((left, width), Horizontal(Box(flow, placements, "child")));
    }

    [Fact]
    public void Percentage_padding_moves_the_content()
    {
        var (flow, placements) = Layout(Shrunk("""<div style="padding: 10% 0 0 20%; background: #ccc">child</div>"""));
        var box = Box(flow, placements, "child");
        var line = placements.First(p => p.Block == box.Block && p.FirstLine >= 0);

        Assert.Equal(50 + 30, line.Left, 6); // 20% of 150 pt.
        Assert.Equal(box.Top + 15, line.Top, 6); // Vertical padding is a percentage of the width too.
    }

    [Fact]
    public void A_content_box_height_and_its_percentage_padding_stay_consistent()
    {
        // The border-box height is the height plus the top padding, and the text starts below that padding.
        var (flow, placements) = Layout(Shrunk("""<div style="height: 20pt; padding-top: 10%; background: #ccc">child</div>"""));
        var box = Box(flow, placements, "child");
        var line = placements.First(p => p.Block == box.Block && p.FirstLine >= 0);

        Assert.Equal(box.Bottom - box.Top - 20, line.Top - box.Top, 6);
    }

    [Fact]
    public void A_flex_column_inside_a_shrunk_item_resolves_against_it()
    {
        var (flow, placements) = Layout(Shrunk("""
            <div style="display: flex; flex-direction: column; align-items: flex-start">
              <div style="width: 50%; background: #ccc">column item</div>
            </div>
            """));

        Assert.Equal((50.0, 75.0), Horizontal(Box(flow, placements, "column item")));
    }

    [Fact]
    public void Wrapped_column_items_resolve_against_the_container_not_their_line()
    {
        var (flow, placements) = Layout("""
            <div style="display: flex; flex-flow: column wrap; height: 30pt; width: 300pt; align-content: flex-start">
              <div style="height: 20pt; width: 50%; background: #ccc">first</div>
              <div style="height: 20pt; width: 20%; background: #ccc">second</div>
            </div>
            """);

        Assert.Equal(150, Box(flow, placements, "first").Width, 6);
        Assert.Equal((200.0, 60.0), Horizontal(Box(flow, placements, "second")));
    }

    [Fact]
    public void Percentage_flex_bases_follow_a_shrunk_container()
    {
        var (flow, placements) = Layout(Shrunk("""
            <div style="display: flex">
              <div style="flex: 0 0 50%; background: #ccc">left</div>
              <div style="flex: 0 0 50%; background: #ccc">right</div>
            </div>
            """));

        Assert.Equal((50.0, 75.0), Horizontal(Box(flow, placements, "left")));
        Assert.Equal((125.0, 75.0), Horizontal(Box(flow, placements, "right")));
    }

    [Fact]
    public void Items_of_a_flex_row_inside_a_shrunk_item_resolve_against_it()
    {
        var (flow, placements) = Layout(Shrunk("""
            <div style="display: flex"><div style="width: 40%; margin-left: 10%; background: #ccc">nested item</div></div>
            """));

        Assert.Equal((65.0, 60.0), Horizontal(Box(flow, placements, "nested item")));
    }

    [Fact]
    public void Vertical_percentage_margins_are_of_the_flexed_width()
    {
        // The empty block's margin collapses with the next one's; both are 10% of 150 pt.
        var (flow, placements) = Layout(Shrunk("""<div style="margin-top: 10%"></div><div style="margin-top: 5%; background: #ccc">after</div>"""));

        Assert.Equal(Box(flow, placements, "after", depth: 1).Top + 15, Box(flow, placements, "after").Top, 6);
    }

    [Fact]
    public void A_measured_item_height_uses_percentages_of_the_flexed_width()
    {
        // The line is as tall as the first item measured at 150 pt: 10 pt, then a 15 pt margin (10%) and a box with 15 pt
        // of top padding (10%) above its text. The second item is stretched to the line.
        var (flow, placements) = Layout("""
            <div style="display: flex; width: 300pt">
              <div style="flex: 1 1 400pt"><div style="height: 10pt"></div><div style="margin-top: 10%; padding-top: 10%; background: #ccc">measured</div></div>
              <div style="flex: 1 1 400pt; background: #ddd">stretched</div>
            </div>
            """);
        var measured = Box(flow, placements, "measured");
        var text = (ParagraphBlock)measured.Block;
        var stretched = Box(flow, placements, "stretched");

        Assert.Equal(15 + text.Paragraph.Layout(150).Height, measured.Bottom - measured.Top, 6);
        Assert.Equal(10 + 15 + (measured.Bottom - measured.Top), stretched.Bottom - stretched.Top, 6);
    }

    [Fact]
    public void Percentage_margins_in_table_cells_are_of_the_column_width()
    {
        var (flow, _) = Layout("""
            <table style="border-spacing: 0"><tr>
              <td style="padding: 0"><div style="height: 1pt"></div><div style="margin-top: 10%"></div><div id="after">x</div></td>
              <td style="padding: 0">A second cell with a few more words in it</td>
            </tr></table>
            """);
        var table = Flatten(flow.Blocks).OfType<TableBlock>().Single();
        var cell = TableLayouter.Layout(table, 0, 400).Cells.First(c => c.Grid.Column == 0);

        Assert.Equal(1 + (cell.InnerWidth / 10), cell.Anchors.Single(a => a.Block.Id == "after").Top, 6);
    }

    [Fact]
    public void An_inline_block_resolves_its_percentages_against_the_paragraph_width()
    {
        var (flow, _) = Layout(Shrunk("""<p>x <span style="display: inline-block; width: 50%; padding: 10% 10% 0">box</span></p>"""));
        var paragraph = Flatten(flow.Blocks).OfType<ParagraphBlock>().Single(p => p.Paragraph.Spans.Any(s => s.Box is not null));
        var box = paragraph.Paragraph.Layout(150).Lines[0].Boxes.Single();
        var text = Flatten([box.Box.Content]).OfType<ParagraphBlock>().First();

        Assert.Equal(75 + 30, box.Width, 6); // Content-box: 50% plus 10% of padding on each side.
        Assert.Equal(15 + text.Paragraph.Layout(75).Height, box.Height, 6);
    }

    [Fact]
    public void A_block_image_scales_to_a_percentage_max_width_of_the_shrunk_item()
    {
        var (flow, placements) = Layout(Shrunk($"""<img style="display: block; max-width: 100%" width="400" height="40" src="{Pixel}">"""));
        var image = Flatten(flow.Blocks).OfType<ImageBlock>().Single();

        Assert.Equal(150, placements.Single(p => p.Block == image).Width, 6);
    }

    [Fact]
    public void A_shrink_to_fit_float_resolves_percentages_against_its_own_width()
    {
        var (flow, placements) = Layout("""
            <div style="float: left; background: #eee"><div style="background: #ddd">Some words</div><div style="width: 50%; background: #ccc">half</div></div>
            <p>After</p>
            """);
        var full = Box(flow, placements, "Some words");
        var half = Box(flow, placements, "half");

        // The float is as wide as its text (the percentage width does not count for its shrink-to-fit width).
        Assert.True(full.Width < 100, $"The float should shrink to its text, got {full.Width} pt.");
        Assert.Equal(full.Width / 2, half.Width, 6);
    }

    [Fact]
    public void A_shrink_to_fit_inline_block_is_not_widened_by_a_percentage_child()
    {
        var (flow, _) = Layout("""
            <p><span style="display: inline-block"><span style="display: block">Some words</span><span style="display: block; width: 50%">x</span></span></p>
            """);
        var paragraph = Flatten(flow.Blocks).OfType<ParagraphBlock>().Single(p => p.Paragraph.Spans.Any(s => s.Box is not null));
        var box = paragraph.Paragraph.Layout(400).Lines[0].Boxes.Single();
        var words = Flatten([box.Box.Content]).OfType<ParagraphBlock>().First();

        Assert.Equal(words.Paragraph.MeasureIntrinsicWidths().Max, box.Width, 6);
    }

    [Fact]
    public void Table_cells_resolve_percentages_against_their_column()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 12));
        var (flow, _) = Layout($"""
            <table style="border-spacing: 0"><tr>
              <td style="padding: 0"><div id="half" style="width: 50%">{words}</div><div id="end"></div></td>
              <td style="padding: 0">{words} {words} {words}</td>
            </tr></table>
            """);
        var table = Flatten(flow.Blocks).OfType<TableBlock>().Single();
        var layout = TableLayouter.Layout(table, 0, 400);
        var cell = layout.Cells.First(c => c.Grid.Column == 0);
        var top = cell.Anchors.Single(a => a.Block.Id == "half").Top;
        var bottom = cell.Anchors.Single(a => a.Block.Id == "end").Top;
        var paragraph = Flatten(cell.Grid.Cell.Content).OfType<ParagraphBlock>().First();

        Assert.True(cell.InnerWidth < 300, $"The first column should share the table's width, got {cell.InnerWidth} pt.");
        Assert.Equal(paragraph.Paragraph.Layout(cell.InnerWidth / 2).Height, bottom - top, 6);
    }

    [Fact]
    public void An_absolute_box_resolves_percentages_against_its_containing_block()
    {
        var (flow, placements) = Layout("""
            <div style="position: relative; width: 200pt; padding: 0 20pt; height: 50pt">
              <div style="position: absolute; left: 0; top: 0; width: 50%; background: #ccc">abs</div>
            </div>
            """);
        var block = Flatten(flow.Blocks).First(b => b.Style.Position == Positioning.Absolute);

        // The containing block is the padding box: 240 pt wide.
        Assert.Equal(120, placements.First(p => p.Block == block).Width, 6);
    }

    private static (double Left, double Width) Horizontal(Placement placement) => (Math.Round(placement.Left, 6), Math.Round(placement.Width, 6));

    private static (FlowDocument Flow, IReadOnlyList<Placement> Placements) Layout(string html)
    {
        var flow = HtmlToPdf.CreateFlowDocument(NoBodyMargin + html, new HtmlConverterOptions { Page = Page });
        return (flow, flow.Paginate().Placements);
    }

    /// <summary>The box of the block with this text (or of its <paramref name="depth"/>-th ancestor).</summary>
    private static Placement Box(FlowDocument flow, IReadOnlyList<Placement> placements, string text, int depth = 0)
    {
        var path = PathTo(flow.Blocks, text) ?? throw new InvalidOperationException($"No block with text '{text}'.");
        var block = path[^(1 + depth)];
        return placements.First(p => p.Block == block && p.FirstLine == -1); // The box, not its lines.
    }

    private static List<Block>? PathTo(IEnumerable<Block> blocks, string text)
    {
        foreach (var block in blocks)
        {
            if (block is ParagraphBlock p && string.Concat(p.Paragraph.Spans.Select(s => s.Text)).Trim() == text)
            {
                return [block];
            }

            if (PathTo(Children(block), text) is { } path)
            {
                path.Insert(0, block);
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<Block> Children(Block block) => block switch
    {
        ContainerBlock c => c.Children,
        FlexBlock f => f.Items.Select(i => i.Content),
        _ => [],
    };

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) => blocks.SelectMany(b => Flatten(Children(b)).Prepend(b));
}
