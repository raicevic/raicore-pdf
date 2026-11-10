using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlInlineBlockTests
{
    private const double Px = 0.75;

    [Fact]
    public void An_inline_block_is_one_item_of_its_line_with_its_own_box()
    {
        var paragraph = Paragraph("""<p>Status: <span style="display: inline-block; padding: 2px 6px; background: #1a4f8b; color: white">PAID</span> today</p>""");

        Assert.Equal("Status: \uFFFC today", string.Concat(paragraph.Spans.Select(s => s.Text)));
        var box = Assert.Single(paragraph.Spans, s => s.Box is not null).Box!.Content;
        Assert.Equal(new Thickness(2 * Px, 6 * Px, 2 * Px, 6 * Px), box.Style.Padding);
        Assert.NotNull(box.Style.Background);
        Assert.Equal("PAID", Text(box));
    }

    [Fact]
    public void Inline_blocks_shrink_to_fit_and_keep_a_fixed_width()
    {
        var paragraph = Paragraph("""
            <p><span id="a" style="display: inline-block; max-width: 300px; margin: 0 auto">short</span>
               <span style="display: inline-block; width: 100px">fixed</span></p>
            """);
        var boxes = paragraph.Spans.Where(s => s.Box is not null).Select(s => s.Box!.Content).ToList();

        Assert.Null(boxes[0].Style.Width);               // max-width does not make it as wide as possible.
        Assert.Equal(0, boxes[0].Style.Margin.Left);     // Auto margins are 0 on inline blocks.
        Assert.Equal(100 * Px, boxes[1].Style.Width);

        var line = paragraph.Layout(400).Lines[0];
        Assert.Equal(paragraph.Spans.First(s => s.Box is not null).Box!.Content is ParagraphBlock p ? p.Paragraph.MeasureIntrinsicWidths().Max : 0, line.Boxes[0].Width, 3);
    }

    [Fact]
    public void Inline_tables_and_inline_flex_containers_are_inline_boxes()
    {
        var paragraph = Paragraph("""
            <p>Table <table style="display: inline-table"><tr><td>a</td></tr></table>
               and pills <span style="display: inline-flex"><b>one</b><b>two</b></span></p>
            """);
        var boxes = paragraph.Spans.Where(s => s.Box is not null).Select(s => s.Box!.Content).ToList();

        Assert.IsType<TableBlock>(boxes[0]);
        Assert.Equal(2, Assert.IsType<FlexBlock>(boxes[1]).Items.Count);
    }

    [Fact]
    public void Inline_block_text_is_extractable_in_reading_order()
    {
        var pdf = HtmlToPdf.Convert("""<p>Status: <span style="display: inline-block; border: 1px solid">PAID</span> on invoice 42</p>""");

        var text = System.Text.RegularExpressions.Regex.Replace(ExternalTools.ExtractText(pdf.ToArray()), @"\s+", " ");
        Assert.Contains("Status: PAID on invoice 42", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_inline_blocks_work()
    {
        var paragraph = Paragraph("""<p><span style="display: inline-block">outer <span style="display: inline-block; border: 1px solid">inner</span></span></p>""");
        var outer = (ParagraphBlock)paragraph.Spans.Single(s => s.Box is not null).Box!.Content;

        Assert.Single(outer.Paragraph.Spans, s => s.Box is not null);
        Assert.Single(paragraph.Layout(400).Lines);
    }

    private static Paragraph Paragraph(string html)
    {
        var flow = HtmlToPdf.CreateFlowDocument(html);
        return Flatten(flow.Blocks).OfType<ParagraphBlock>().First(p => p.Paragraph.Spans.Any(s => s.Box is not null)).Paragraph;
    }

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);

    private static string Text(Block block) =>
        string.Concat(Flatten([block]).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans).Select(s => s.Text)).Trim();
}
