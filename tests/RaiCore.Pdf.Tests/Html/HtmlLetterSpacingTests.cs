using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlLetterSpacingTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData("letter-spacing: 2px", 2 * Px, 0)]
    [InlineData("letter-spacing: 0.1em; font-size: 20px", 2 * Px, 0)]
    [InlineData("letter-spacing: normal; word-spacing: normal", 0, 0)]
    [InlineData("word-spacing: 4pt", 0, 4)]
    [InlineData("word-spacing: -0.25em; font-size: 16px", 0, -4 * Px)]
    public void Spacing_is_read_in_points(string css, double letter, double word)
    {
        var style = Spans($"<p style='{css}'>text</p>")[0].Style;

        Assert.Equal(letter, style.LetterSpacing, 6);
        Assert.Equal(word, style.WordSpacing, 6);
    }

    [Fact]
    public void Spacing_is_inherited()
    {
        var spans = Spans("<p style='letter-spacing: 3px; word-spacing: 1px'>a <b>b</b> <span style='letter-spacing: 0'>c</span></p>");

        Assert.Equal(3 * Px, Span(spans, "b").Style.LetterSpacing, 6);
        Assert.Equal(1 * Px, Span(spans, "b").Style.WordSpacing, 6);
        Assert.Equal(0, Span(spans, "c").Style.LetterSpacing, 6);
    }

    [Fact]
    public void Em_spacing_is_inherited_as_the_parents_length()
    {
        var spans = Spans("<p style='font-size: 20px; letter-spacing: 0.1em'>a <span style='font-size: 10px'>b</span></p>");

        Assert.Equal(2 * Px, Span(spans, "b").Style.LetterSpacing, 6); // Computed on the p, not on the span's 10px.
    }

    [Fact]
    public void Spaced_text_is_wider_in_the_document()
    {
        double LineWidth(string css) => Flatten(HtmlToPdf.CreateFlowDocument($"<p style='{css}'>Spaced out</p>").Blocks)
            .OfType<ParagraphBlock>().Single().Paragraph.Layout(1000).Lines[0].Width;

        Assert.Equal(LineWidth("") + (10 * 2 * Px) + (1 * 4 * Px), LineWidth("letter-spacing: 2px; word-spacing: 4px"), 6);
    }

    private static List<ParagraphSpan> Spans(string html) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans)];

    private static ParagraphSpan Span(List<ParagraphSpan> spans, string text) => spans.First(s => s.Text.Trim() == text);

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
