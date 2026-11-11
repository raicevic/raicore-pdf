using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

public class HyphenationLayoutTests
{
    private const string Text = "The hyphenation";

    private const string English =
        "Typography is the art and technique of arranging type to make written language legible, readable and " +
        "appealing when displayed. The arrangement of type involves selecting typefaces, point sizes, line lengths, " +
        "line spacing, letter spacing and adjusting the space between pairs of letters. Justification distributes " +
        "the remaining space between the words of every line, which works much better when long words can be divided.";

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
    private static readonly TextStyle Body = new(Sans, 12);
    private static readonly TextStyle Auto = Body with { Language = "en-US", Hyphens = Hyphens.Auto };

    private static double Width(string text) => Sans.MeasureText(text, 12);

    private static double HyphenWidth => Width("\u2010");

    [Fact]
    public void Auto_hyphenates_a_word_that_does_not_fit()
    {
        // "The hyphen" and its hyphen fit, the whole word does not.
        var width = Width("The hyphen\u2010") + 1;
        var layout = new Paragraph(Auto).Add(Text).Layout(width);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal("The hyphen".Length, layout.Lines[0].End);
        Assert.Equal("The hyphen\u2010", string.Concat(layout.Lines[0].Fragments.Select(f => f.Run.Text)));
        Assert.Equal(Width("The hyphen") + HyphenWidth, layout.Lines[0].Width, 3);
        Assert.Equal("ation", string.Concat(layout.Lines[1].Fragments.Select(f => f.Run.Text)));
    }

    [Fact]
    public void The_hyphen_counts_when_fitting_the_line()
    {
        // "The hyphen" fits, but not with its hyphen: the earlier point ("hy-") is taken.
        var width = Width("The hyphen") + (HyphenWidth / 2);
        var layout = new Paragraph(Auto).Add(Text).Layout(width);

        Assert.Equal("The hy".Length, layout.Lines[0].End);
        Assert.All(layout.Lines, l => Assert.True(l.Width <= width + 1e-6, $"Line is {l.Width} wide, more than {width}"));
    }

    [Fact]
    public void Manual_is_the_default_and_breaks_only_between_words()
    {
        var width = Width("The hyphen\u2010") + 1;

        Assert.Equal(Hyphens.Manual, Body.Hyphens);
        Assert.Equal("The ".Length, new Paragraph(Body).Add(Text).Layout(width).Lines[0].End);
        Assert.Equal("The ".Length, new Paragraph(Auto with { Hyphens = Hyphens.Manual }).Add(Text).Layout(width).Lines[0].End);
    }

    [Fact]
    public void Auto_needs_a_language_with_patterns()
    {
        var width = Width("The hyphen\u2010") + 1;

        Assert.Equal("The ".Length, new Paragraph(Auto with { Language = null }).Add(Text).Layout(width).Lines[0].End);
        Assert.Equal("The ".Length, new Paragraph(Auto with { Language = "sv" }).Add(Text).Layout(width).Lines[0].End);
    }

    [Fact]
    public void None_ignores_even_soft_hyphens()
    {
        const string text = "extra\u00ADordinary";

        Assert.Equal(2, new Paragraph(Body).Add(text).Layout(45).Lines.Count);
        var none = new Paragraph(Body with { Hyphens = Hyphens.None }).Add(text).Layout(45);
        Assert.Single(none.Lines);
        Assert.Equal(Width("extraordinary"), none.Lines[0].Width, 1);
    }

    [Fact]
    public void Soft_hyphens_in_a_word_replace_its_automatic_points()
    {
        // Patterns give hy-phen-ation; the soft hyphen allows only hyphena-tion.
        const string text = "The hyphena\u00ADtion";
        var layout = new Paragraph(Auto).Add(text).Layout(Width("The hyphen\u2010") + 1);

        Assert.Equal("The ".Length, layout.Lines[0].End);
        Assert.Equal(2, layout.Lines.Count);
    }

    [Fact]
    public void The_hyphenate_character_is_drawn_and_measured()
    {
        var width = Width("The hyphen*") + 0.5;
        var layout = new Paragraph(Auto with { HyphenateCharacter = "*" }).Add(Text).Layout(width);

        Assert.Equal("The hyphen*", string.Concat(layout.Lines[0].Fragments.Select(f => f.Run.Text)));
        Assert.Equal(Width("The hyphen*"), layout.Lines[0].Width, 3);

        var invisible = new Paragraph(Auto with { HyphenateCharacter = string.Empty }).Add(Text).Layout(Width("The hyphen") + 0.5);
        Assert.Equal("The hyphen", string.Concat(invisible.Lines[0].Fragments.Select(f => f.Run.Text)));
    }

    [Fact]
    public void A_font_without_the_hyphen_gets_a_hyphen_minus()
    {
        Assert.False(Arabic.HasGlyph(0x2010));
        var style = new TextStyle([Arabic, Sans], 12) { Language = "en", Hyphens = Hyphens.Auto };
        var layout = new Paragraph(style).Add(Text).Layout(Width("The hyphen-") + 1);

        Assert.EndsWith("-", layout.Lines[0].Fragments[^1].Run.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12, null, null, "The ")] // "hyphenation" has 11 letters: too short.
    [InlineData(11, null, null, "The hyphen")]
    [InlineData(null, 3, null, "The hyphen")]
    [InlineData(null, null, 6, "The hy")] // "ation" is too short to start a line.
    [InlineData(null, 7, null, "The ")]
    public void Limits_set_the_shortest_word_and_fragments(int? word, int? before, int? after, string firstLine)
    {
        var style = Auto with { HyphenationLimits = new HyphenationLimits(word, before, after) };
        var layout = new Paragraph(style).Add(Text).Layout(Width("The hyphen\u2010") + 1);

        Assert.Equal(firstLine.Length, layout.Lines[0].End);
    }

    [Fact]
    public void Each_language_uses_its_own_patterns()
    {
        // British patterns allow hy-phen-a-tion, American ones only hy-phen-ation.
        var width = Width("The hyphena\u2010") + 1;

        Assert.Equal("The hyphen".Length, new Paragraph(Auto).Add(Text).Layout(width).Lines[0].End);
        Assert.Equal("The hyphena".Length, new Paragraph(Auto with { Language = "en-GB" }).Add(Text).Layout(width).Lines[0].End);
    }

    [Fact]
    public void The_style_of_a_words_first_letter_decides()
    {
        var width = Width("The hyphen\u2010") + 1;
        var mixed = new Paragraph(Auto).Add("The hyp").Add("henation", Body).Layout(width);
        var off = new Paragraph(Body).Add("The hyp").Add("henation", Auto).Layout(width);

        Assert.Equal("The hyphen".Length, mixed.Lines[0].End);
        Assert.Equal("The ".Length, off.Lines[0].End); // The second span does not start a word of its own.
    }

    [Fact]
    public void Justified_text_gets_tighter_with_hyphenation()
    {
        const double width = 150;
        var plain = new Paragraph(Body with { Language = "en" }) { Alignment = TextAlignment.Justify }.Add(English).Layout(width);
        var hyphenated = new Paragraph(Auto) { Alignment = TextAlignment.Justify }.Add(English).Layout(width);

        Assert.Contains(hyphenated.Lines, l => l.Fragments[^1].Run.Text.EndsWith('\u2010'));
        Assert.True(MaxSpace(hyphenated) < MaxSpace(plain) - 1, $"Widest space {MaxSpace(hyphenated)} vs {MaxSpace(plain)}");
        Assert.True(TotalStretch(hyphenated) < TotalStretch(plain) * 0.7, $"Stretch {TotalStretch(hyphenated)} vs {TotalStretch(plain)}");
        Assert.True(hyphenated.Lines.Count <= plain.Lines.Count);
        Assert.All(hyphenated.Lines.SkipLast(1), l => Assert.Equal(width, l.Width, 3));

        static double MaxSpace(ParagraphLayout layout) => layout.Lines.SkipLast(1)
            .SelectMany(l => l.Fragments.SelectMany(f => f.Glyphs.Where(g => g.Text == " ").Select(g => g.Advance * f.Style.FontSize))).Max();

        static double TotalStretch(ParagraphLayout layout) => layout.Lines.SkipLast(1)
            .Sum(l => l.Fragments.Sum(f => f.Glyphs.Count(g => g.Text == " ")) * (MaxSpaceIn(l) - Width(" ")));

        static double MaxSpaceIn(LineLayout line) =>
            line.Fragments.SelectMany(f => f.Glyphs.Where(g => g.Text == " ").Select(g => g.Advance * f.Style.FontSize)).DefaultIfEmpty(Width(" ")).Max();
    }

    [Fact]
    public void Hyphenated_lines_cover_the_text_without_gaps()
    {
        var layout = new Paragraph(Auto).Add(English).Layout(120);

        Assert.Equal(0, layout.Lines[0].Start);
        for (var i = 1; i < layout.Lines.Count; i++)
        {
            Assert.Equal(layout.Lines[i - 1].End, layout.Lines[i].Start);
        }

        Assert.Equal(English.Length, layout.Lines[^1].End);
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 120 + 1e-6, $"Line is {l.Width} wide"));
    }

    [Fact]
    public void The_hyphen_goes_at_the_logical_end_in_right_to_left_paragraphs()
    {
        // An English word in a right-to-left paragraph: the hyphen follows its first part, on that part's right.
        var width = Width("The hyphen\u2010") + 1;
        var layout = new Paragraph(Auto) { Direction = ParagraphDirection.RightToLeft }.Add(Text).Layout(width);

        Assert.Equal("The hyphen".Length, layout.Lines[0].End);
        var last = layout.Lines[0].Fragments.MaxBy(f => f.X)!;
        Assert.EndsWith("hyphen\u2010", last.Run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Min_content_width_counts_hyphenation_points_and_their_hyphens()
    {
        var (plain, _) = new Paragraph(Body with { Language = "en" }).Add("hyphenation").MeasureIntrinsicWidths();
        var (hyphenated, max) = new Paragraph(Auto).Add("hyphenation").MeasureIntrinsicWidths();

        Assert.Equal(Width("hyphenation"), plain, 3);
        Assert.Equal(Math.Max(Width("hy"), Width("phen")) + HyphenWidth, hyphenated, 3); // hy- | phen- | ation
        Assert.Equal(Width("hyphenation"), max, 3);
    }
}
