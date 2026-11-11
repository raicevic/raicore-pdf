using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlHyphenationTests
{
    [Theory]
    [InlineData("", Hyphens.Manual)]
    [InlineData("hyphens: manual", Hyphens.Manual)]
    [InlineData("hyphens: auto", Hyphens.Auto)]
    [InlineData("hyphens: none", Hyphens.None)]
    [InlineData("-webkit-hyphens: auto", Hyphens.Auto)]
    [InlineData("hyphens: bogus", Hyphens.Manual)]
    public void Hyphens_is_read(string css, Hyphens expected) =>
        Assert.Equal(expected, Spans($"<p style='{css}'>text</p>")[0].Style.Hyphens);

    [Fact]
    public void Hyphens_is_inherited_and_turned_off_where_text_does_not_wrap()
    {
        var spans = Spans("<style>p { hyphens: auto }</style><p>a <b>b</b> <span style='white-space: nowrap'>c</span> <span style='hyphens: none'>d</span></p>");

        Assert.Equal(Hyphens.Auto, Span(spans, "b").Style.Hyphens);
        Assert.Equal(Hyphens.None, Span(spans, "c").Style.Hyphens);
        Assert.Equal(Hyphens.None, Span(spans, "d").Style.Hyphens);
    }

    [Fact]
    public void The_language_comes_from_the_nearest_lang_attribute()
    {
        var spans = Spans("<html lang='en-US'><body><p>a <i lang='de'>b <b>c</b></i> <span lang=''>d</span></p></body></html>");

        Assert.Equal("en-US", Span(spans, "a").Style.Language);
        Assert.Equal("de", Span(spans, "b").Style.Language);
        Assert.Equal("de", Span(spans, "c").Style.Language);
        Assert.Equal("de", Span(spans, "c").Style.Shaping.Language); // Shaping uses it too, e.g. for localized forms.
        Assert.Null(Span(spans, "d").Style.Language); // An empty lang means unknown.
        Assert.Null(Spans("<p>x</p>")[0].Style.Language);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("hyphenate-character: auto", null)]
    [InlineData("hyphenate-character: \"*\"", "*")]
    [InlineData("hyphenate-character: \"\"", "")]
    [InlineData("hyphenate-character: \"\\2010\"", "\u2010")]
    [InlineData("hyphenate-character: \"=\\\"\"", "=\"")]
    [InlineData("hyphenate-character: \"a\\9 b\"", "a\tb")] // AngleSharp keeps control characters escaped.
    [InlineData("-webkit-hyphenate-character: \"~\"", "~")]
    public void Hyphenate_character_is_read(string css, string? expected)
    {
        var html = $"<style>p {{ {css} }}</style><p>text <b>bold</b></p>";

        Assert.Equal(expected, Spans(html)[0].Style.HyphenateCharacter);
        Assert.Equal(expected, Span(Spans(html), "bold").Style.HyphenateCharacter); // Inherited.
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("auto", null, null, null)]
    [InlineData("6 3 2", 6, 3, 2)]
    [InlineData("6 3", 6, 3, 3)]
    [InlineData("8", 8, null, null)]
    [InlineData("auto 3", null, 3, 3)]
    [InlineData("auto auto 4", null, null, 4)]
    [InlineData("6 3 2 1", null, null, null)]
    [InlineData("six", null, null, null)]
    [InlineData("-1", null, null, null)]
    public void Hyphenate_limit_chars_is_read(string value, int? word, int? before, int? after)
    {
        var css = value.Length > 0 ? $"hyphenate-limit-chars: {value}" : string.Empty;

        Assert.Equal(new HyphenationLimits(word, before, after), Spans($"<style>p {{ {css} }}</style><p>text</p>")[0].Style.HyphenationLimits);
        Assert.Equal(new HyphenationLimits(word, before, after), Spans($"<p style='{css}'>text</p>")[0].Style.HyphenationLimits);
    }

    [Fact]
    public void Hyphenate_limit_chars_is_inherited()
    {
        var spans = Spans("<div style='hyphenate-limit-chars: 7 3 3'><p>a <b style='hyphenate-limit-chars: 9'>b</b></p></div>");

        Assert.Equal(new HyphenationLimits(7, 3, 3), Span(spans, "a").Style.HyphenationLimits);
        Assert.Equal(new HyphenationLimits(9, null, null), Span(spans, "b").Style.HyphenationLimits);
    }

    [Fact]
    public void Narrow_paragraphs_are_hyphenated_in_their_language()
    {
        int HyphenatedLines(string css, string lang) => Flatten(HtmlToPdf.CreateFlowDocument(
                $"<style>p {{ font: 12pt sans-serif; {css} }}</style><p lang='{lang}'>Extraordinary typography demonstrates hyphenation</p>").Blocks)
            .OfType<ParagraphBlock>().Single().Paragraph.Layout(90).Lines.Count(l => l.Fragments[^1].Run.Text.EndsWith('\u2010'));

        Assert.True(HyphenatedLines("hyphens: auto", "en") > 0);
        Assert.Equal(0, HyphenatedLines(string.Empty, "en")); // manual, the initial value
        Assert.Equal(0, HyphenatedLines("hyphens: auto", string.Empty)); // unknown language
    }

    private static List<ParagraphSpan> Spans(string html) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).OfType<ParagraphBlock>().SelectMany(p => p.Paragraph.Spans)];

    private static ParagraphSpan Span(List<ParagraphSpan> spans, string text) => spans.First(s => s.Text.Trim() == text);

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
