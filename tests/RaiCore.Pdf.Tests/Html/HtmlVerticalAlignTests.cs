using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlVerticalAlignTests
{
    private const double Px = 0.75;

    [Fact]
    public void Sub_and_sup_shift_by_a_fifth_and_a_third_of_the_parent_font_size()
    {
        var spans = Spans("<p style='font-size: 15px'>H<sub>2</sub>O x<sup>2</sup></p>");

        Assert.Equal(-15 * Px / 5, Span(spans, "2", 0).Style.VerticalAlign.Shift, 6);
        Assert.Equal(15 * Px / 3, Span(spans, "2", 1).Style.VerticalAlign.Shift, 6);
        Assert.Equal(default, Span(spans, "H").Style.VerticalAlign);
    }

    [Fact]
    public void Nested_shifts_add_up()
    {
        var spans = Spans("<p style='font-size: 12px'>e<sup>i<b>x</b><span style='vertical-align: 3px'>y</span></sup></p>");
        var sup = 12 * Px / 3;

        Assert.Equal(sup, Span(spans, "x").Style.VerticalAlign.Shift, 6);
        Assert.Equal(sup + (3 * Px), Span(spans, "y").Style.VerticalAlign.Shift, 6);
    }

    [Fact]
    public void A_subscript_inside_a_superscript_is_lowered_from_the_raised_baseline()
    {
        // The sub is relative to the sup's baseline and font size (smaller: 10px for a 12px paragraph).
        var spans = Spans("<p style='font-size: 12px'>a<sup style='font-size: 10px'>b<sub>c</sub></sup></p>");

        Assert.Equal((12 * Px / 3) - (10 * Px / 5), Span(spans, "c").Style.VerticalAlign.Shift, 6);
    }

    [Theory]
    [InlineData("middle", InlineAlignmentKind.Middle)]
    [InlineData("text-top", InlineAlignmentKind.TextTop)]
    [InlineData("text-bottom", InlineAlignmentKind.TextBottom)]
    [InlineData("top", InlineAlignmentKind.Top)]
    [InlineData("bottom", InlineAlignmentKind.Bottom)]
    public void Keywords_map_to_alignment_kinds(string css, InlineAlignmentKind kind)
    {
        Assert.Equal(kind, Span(Spans($"<p>a<span style='vertical-align: {css}'>b</span></p>"), "b").Style.VerticalAlign.Kind);
    }

    [Fact]
    public void Percentages_refer_to_the_elements_line_height()
    {
        var spans = Spans("<p>a<span style='line-height: 20px; vertical-align: 50%'>b</span></p>");

        Assert.Equal(10 * Px, Span(spans, "b").Style.VerticalAlign.Shift, 6);
    }

    [Fact]
    public void A_blocks_own_vertical_align_does_not_move_its_text()
    {
        // On a table cell vertical-align positions the cell's content, and on a block it does nothing to its text.
        var flow = HtmlToPdf.CreateFlowDocument("<div style='vertical-align: super'>plain</div><table><tr><td style='vertical-align: bottom'>cell</td></tr></table>");
        var cellParagraph = Flatten(flow.Blocks).OfType<TableBlock>().Single().Rows[0].Cells[0].Content.OfType<ParagraphBlock>().Single();

        Assert.Equal(default, Span(Spans(flow), "plain").Style.VerticalAlign);
        Assert.Equal(default, cellParagraph.Paragraph.Spans[0].Style.VerticalAlign);
    }

    [Fact]
    public void Images_and_inline_blocks_get_their_alignment()
    {
        const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var spans = Spans($"<p>a <img src='{Pixel}' style='vertical-align: middle'> <span style='display: inline-block; vertical-align: top'>b</span></p>");

        Assert.Equal(InlineAlignmentKind.Middle, spans.Single(s => s.Image is not null).Image!.Alignment.Kind);
        Assert.Equal(InlineAlignmentKind.Top, spans.Single(s => s.Box is not null).Box!.Alignment.Kind);
    }

    private static List<ParagraphSpan> Spans(string html) => Spans(HtmlToPdf.CreateFlowDocument(html));

    private static List<ParagraphSpan> Spans(FlowDocument flow) =>
        [.. Flatten(flow.Blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans)];

    private static ParagraphSpan Span(List<ParagraphSpan> spans, string text, int index = 0) =>
        spans.Where(s => s.Text.Trim() == text).ElementAt(index);

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
