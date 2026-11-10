using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlTextDecorationTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData("<a href='x'>t</a>", TextDecorationLines.Underline)]
    [InlineData("<u>t</u>", TextDecorationLines.Underline)]
    [InlineData("<ins>t</ins>", TextDecorationLines.Underline)]
    [InlineData("<del>t</del>", TextDecorationLines.LineThrough)]
    [InlineData("<s>t</s>", TextDecorationLines.LineThrough)]
    [InlineData("<strike>t</strike>", TextDecorationLines.LineThrough)]
    [InlineData("<span style='text-decoration: overline underline'>t</span>", TextDecorationLines.Overline | TextDecorationLines.Underline)]
    public void Elements_and_css_give_decorations(string inner, TextDecorationLines lines)
    {
        Assert.Equal(lines, Assert.Single(Span(Spans($"<p>{inner}</p>"), "t").Style.Decorations).Lines);
    }

    [Fact]
    public void Style_color_thickness_and_offset_are_mapped()
    {
        var decoration = Span(Spans("""
            <p style="font-size: 20px; color: blue"><span style="text-decoration: underline wavy red; text-decoration-thickness: 10%; text-underline-offset: 3px">t</span>
               <span style="text-decoration: line-through">c</span></p>
            """), "t").Style.Decorations[0];
        var current = Span(Spans("<p style='color: blue'><span style='text-decoration: line-through'>c</span></p>"), "c").Style.Decorations[0];

        Assert.Equal((TextDecorationStyle.Wavy, PdfColor.FromRgb(255, 0, 0)), (decoration.Style, decoration.Color));
        Assert.Equal(20 * Px / 10, decoration.Thickness!.Value, 6); // 10% of 1em.
        Assert.Equal(3 * Px, decoration.UnderlineOffset!.Value, 6);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), current.Color);     // currentColor.
    }

    [Fact]
    public void Decorations_propagate_to_descendants_and_cannot_be_removed()
    {
        var spans = Spans("""
            <div style="text-decoration: underline"><p>para <b>bold</b> <span style="text-decoration: none">still</span></p></div>
            """);

        Assert.All(["para", "bold", "still"], text => Assert.Single(Span(spans, text).Style.Decorations));
    }

    [Fact]
    public void Nested_decorations_add_up_and_keep_the_declaring_elements_font()
    {
        var spans = Spans("<p style='font-size: 12px'><u>a <span style='font-size: 30px'>big</span> <del>gone</del></u></p>");

        var underline = Span(spans, "a").Style.Decorations[0];
        Assert.Same(underline, Span(spans, "big").Style.Decorations[0]); // One decoration: one continuous line.
        Assert.Equal(12 * Px, underline.FontSize, 6);
        Assert.Equal(2, Span(spans, "gone").Style.Decorations.Count);
    }

    [Fact]
    public void Decorations_stay_on_the_declaring_baseline_and_skip_atomic_inline_boxes()
    {
        var paragraph = Paragraphs("<p><u>H<sub>2</sub>O <span style='display: inline-block'>box</span></u></p>")[0];
        var sub = paragraph.Spans.Single(s => s.Text == "2");
        var box = (ParagraphBlock)paragraph.Spans.Single(s => s.Box is not null).Box!.Content;

        Assert.Equal(0, sub.Style.Decorations[0].BaselineShift);
        Assert.True(sub.Style.VerticalAlign.Shift < 0);
        Assert.Empty(box.Paragraph.Spans[0].Style.Decorations);
    }

    [Fact]
    public void A_raised_element_draws_its_decoration_on_its_own_baseline()
    {
        var spans = Spans("<p>x<sup style='text-decoration: underline'>2</sup></p>");

        Assert.True(Span(spans, "2").Style.Decorations[0].BaselineShift > 0);
    }

    [Fact]
    public void Running_elements_do_not_inherit_decorations()
    {
        var flow = HtmlToPdf.CreateFlowDocument("""
            <style>@page { @top-center { content: element(head); } } .head { position: running(head); }</style>
            <div style="text-decoration: underline"><div class="head">header</div><p>body</p></div>
            """);
        var running = Flatten(flow.Blocks).Select(b => b.Marker).OfType<RaiCore.Pdf.Html.Paged.RunningMarker>().Single();
        var header = Flatten(running.Blocks).OfType<ParagraphBlock>().Single();

        Assert.Empty(header.Paragraph.Spans[0].Style.Decorations);
    }

    private static List<Paragraph> Paragraphs(string html) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).OfType<ParagraphBlock>().Select(p => p.Paragraph)];

    private static List<ParagraphSpan> Spans(string html) => [.. Paragraphs(html).SelectMany(p => p.Spans)];

    private static ParagraphSpan Span(List<ParagraphSpan> spans, string text) => spans.First(s => s.Text.Trim() == text);

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
