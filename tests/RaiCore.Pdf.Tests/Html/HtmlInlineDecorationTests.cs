using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlInlineDecorationTests
{
    private const double Px = 0.75;

    [Fact]
    public void Spans_with_a_box_become_decorations()
    {
        var paragraph = Paragraphs("<p>a <span style='background: yellow; padding: 0 4px; border: 1px solid red; margin-left: 2px'>b</span> <span>c</span> <b>d</b></p>")[0];

        var decoration = Assert.Single(paragraph.Decorations);
        Assert.Equal("b", Text(paragraph, decoration));
        Assert.Equal(new Thickness(0, 4 * Px, 0, 4 * Px), decoration.Box.Padding);
        Assert.Equal(2 * Px, decoration.Box.Margin.Left);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), decoration.Box.Border.Top.Color);
    }

    [Fact]
    public void Nested_spans_and_their_ranges()
    {
        var paragraph = Paragraphs("<p><mark>one <span style='background: pink'>two</span> three</mark></p>")[0];

        Assert.Equal(["two", "one two three"], paragraph.Decorations.Select(d => Text(paragraph, d)));
    }

    [Fact]
    public void A_block_inside_an_inline_element_splits_its_box()
    {
        var paragraphs = Paragraphs("<div>a <span style='border: 1px solid'>start<div>block</div>end</span> b</div>");

        var first = Assert.Single(paragraphs[0].Decorations);
        var last = Assert.Single(paragraphs[2].Decorations);
        Assert.Equal((true, false), (first.HasStartEdge, first.HasEndEdge));
        Assert.Equal((false, true), (last.HasStartEdge, last.HasEndEdge));
        Assert.Empty(paragraphs[1].Decorations);
    }

    [Fact]
    public void A_raised_elements_box_is_raised_too()
    {
        var paragraph = Paragraphs("<p>x<sup style='background: yellow'>2</sup></p>")[0];

        Assert.True(Assert.Single(paragraph.Decorations).Style.VerticalAlign.Shift > 0);
    }

    [Fact]
    public void Mark_elements_are_highlighted_by_default()
    {
        var paragraph = Paragraphs("<p>a <mark>b</mark></p>")[0];

        Assert.NotNull(Assert.Single(paragraph.Decorations).Box.Background);
    }

    private static List<Paragraph> Paragraphs(string html) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).OfType<ParagraphBlock>().Select(p => p.Paragraph)];

    private static string Text(Paragraph paragraph, InlineDecoration decoration) =>
        string.Concat(paragraph.Spans.Select(s => s.Text))[decoration.Start..decoration.End];

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
