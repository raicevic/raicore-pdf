using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

public class LetterSpacingTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
    private static readonly TextStyle Body = new(Sans, 12);

    [Theory]
    [InlineData("abc", 2, 0, 3 * 2)]
    [InlineData("abc", -1, 0, 3 * -1)]
    [InlineData("a b\u00A0c", 0, 5, 2 * 5)]            // Spaces and no-break spaces are word separators.
    [InlineData("a b", 1, 3, (3 * 1) + 3)]             // A space gets both.
    [InlineData("x\u0301x", 2, 0, 2 * 2)]              // A character with its combining mark (no precomposed form) is spaced once.
    public void Spacing_is_added_after_characters_and_word_separators(string text, double letter, double word, double extra)
    {
        var natural = Width(text, Body);
        var spaced = Width(text, Body with { LetterSpacing = letter, WordSpacing = word });

        Assert.Equal(natural + extra, spaced, 6);
    }

    [Fact]
    public void Glyphs_carry_the_spacing_so_drawing_matches_measuring()
    {
        var line = new Paragraph(Body with { LetterSpacing = 3 }).Add("ab").Layout(500).Lines[0];
        var fragment = Assert.Single(line.Fragments);

        Assert.Equal(Sans.MeasureText("a", 12) + 3, fragment.Glyphs[0].Advance * 12, 6);
        Assert.Equal(fragment.Width, line.Width, 6);
    }

    [Fact]
    public void Line_breaking_and_intrinsic_widths_include_the_spacing()
    {
        const string text = "spaced words";
        var natural = Width(text, Body);
        var paragraph = new Paragraph(Body with { LetterSpacing = 1, WordSpacing = 2 }).Add(text);

        Assert.Single(new Paragraph(Body).Add(text).Layout(natural + 1).Lines);
        Assert.Equal(2, paragraph.Layout(natural + 1).Lines.Count);

        var (min, max) = paragraph.MeasureIntrinsicWidths();
        Assert.Equal(natural + text.Length + 2, max, 6);
        Assert.Equal(Sans.MeasureText("spaced", 12) + 6, min, 6); // The hanging space is not counted.
    }

    [Fact]
    public void Spacing_applies_per_span()
    {
        var line = new Paragraph(Body).Add("ab").Add("cd", Body with { LetterSpacing = 4 }).Layout(500).Lines[0];

        Assert.Equal(Sans.MeasureText("abcd", 12) + 8, line.Width, 1); // Kerning between b and c may differ slightly.
        Assert.Equal(Sans.MeasureText("ab", 12), line.Fragments[0].Width, 6);
    }

    [Fact]
    public void Justified_lines_still_fill_the_width()
    {
        const string text = "one two three four five six seven eight nine ten eleven twelve";
        var layout = new Paragraph(Body with { LetterSpacing = 0.5, WordSpacing = 2 }) { Alignment = TextAlignment.Justify }.Add(text).Layout(150);

        Assert.True(layout.Lines.Count > 2);
        Assert.All(layout.Lines.SkipLast(1), line => Assert.Equal(150, line.Width, 6));
        Assert.True(layout.Lines[^1].Width < 150);
    }

    [Fact]
    public void Cursive_scripts_get_word_spacing_but_no_letter_spacing()
    {
        const string text = "\u0628\u0627\u0628 \u0628\u0627\u0628";
        var style = new TextStyle(Arabic, 14);

        Assert.Equal(Width(text, style) + 5, Width(text, style with { LetterSpacing = 2, WordSpacing = 5 }), 6);
    }

    [Fact]
    public void Letter_spacing_turns_off_optional_ligatures_unless_requested()
    {
        Assert.Same(Body.Shaping, ParagraphLayouter.ShapingFor(Body with { WordSpacing = 2 }));

        var options = ParagraphLayouter.ShapingFor(Body with { LetterSpacing = 1, Shaping = new() { Features = ["dlig"], Language = "he" } });
        Assert.Equal(["-liga", "-clig", "-dlig", "-hlig", "dlig"], options.Features); // Later features win in HarfBuzz.
        Assert.Equal("he", options.Language);
    }

    private static double Width(string text, TextStyle style) => new Paragraph(style).Add(text).Layout(10_000).Lines[0].Width;
}
