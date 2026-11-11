using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Leaders (Paragraph.AddLeader, CSS leader()): a pattern repeated to fill the rest of the line.</summary>
public class LeaderTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Body = new(Sans, 12);
    private static readonly TextStyle Auto = Body with { Language = "en-US", Hyphens = Hyphens.Auto };

    [Fact]
    public void A_leader_fills_the_line_so_what_follows_ends_at_the_right_edge()
    {
        var layout = new Paragraph(Body).Add("Introduction").AddLeader(". ").Add("12").Layout(300);

        var line = Assert.Single(layout.Lines);
        Assert.Equal(300, line.X + line.Width, 6);

        var leader = line.Fragments.Single(f => f.Glyphs.Count > 2 && f.Glyphs.All(g => g.Text is "." or " "));
        var dots = leader.Glyphs.Count(g => g.Text == ".");
        var patternWidth = Width(". ");
        var free = 300 - Width("Introduction") - Width("12");
        Assert.Equal((int)Math.Floor(free / patternWidth), dots);

        // The repetitions end where the page number starts: the space left over goes before them.
        var number = line.Fragments.Single(f => f.Glyphs.Count == 2 && f.Glyphs[0].Text == "1");
        Assert.Equal(number.X, leader.X + leader.Width, 6);
        Assert.True(leader.Glyphs[0].OffsetX > 0);
    }

    [Fact]
    public void Leaders_on_a_line_share_the_space()
    {
        var layout = new Paragraph(Body).Add("a").AddLeader("-").Add("b").AddLeader("-").Add("c").Layout(200);

        var line = Assert.Single(layout.Lines);
        Assert.Equal(200, line.X + line.Width, 6);
        var leaders = line.Fragments.Where(f => f.Glyphs.Count > 1 && f.Glyphs.All(g => g.Text == "-")).ToList();
        Assert.Equal(2, leaders.Count);
        Assert.Equal(leaders[0].Width, leaders[1].Width, 6);
    }

    [Fact]
    public void A_leader_on_a_full_line_is_drawn_once_and_wrapped_text_keeps_its_leader_on_the_last_line()
    {
        var full = new Paragraph(Body).Add("Introduction").AddLeader(".").Add("12").Layout(Width("Introduction.12"));
        Assert.Equal(1, Assert.Single(full.Lines).Fragments.Sum(f => f.Glyphs.Count(g => g.Text == ".")));

        var wrapped = new Paragraph(Body).Add("A rather long title that wraps ").AddLeader(". ").Add("7").Layout(120);
        Assert.True(wrapped.Lines.Count > 1);
        Assert.Equal(120, wrapped.Lines[^1].X + wrapped.Lines[^1].Width, 6);
        Assert.True(wrapped.Lines[0].Width < 120); // Lines without the leader are not stretched.
    }

    [Fact]
    public void Right_aligned_and_centered_lines_with_a_leader_fill_the_width()
    {
        var right = new Paragraph(Body) { Alignment = TextAlignment.Right }.Add("Title").AddLeader(".").Add("3").Layout(250);
        var center = new Paragraph(Body) { Alignment = TextAlignment.Center }.Add("Title").AddLeader(".").Add("3").Layout(250);

        Assert.Equal(0, right.Lines[0].X, 6);
        Assert.Equal(250, right.Lines[0].Width, 6);
        Assert.Equal(250, center.Lines[0].Width, 6);
    }

    [Fact]
    public void Leaders_are_not_hyphenated()
    {
        // The pattern is a word English patterns hyphenate ("hy-phen-ation"); a leader is never broken inside.
        var layout = new Paragraph(Auto).Add("A ").AddLeader("hyphenation", Auto).Add(" 1").Layout(Width("A hyphen\u2010") + 1);

        Assert.All(layout.Lines, line => Assert.False(line.End is > 2 and < 13, $"A line ends at {line.End}, inside the leader."));
        Assert.DoesNotContain(layout.Lines.SelectMany(l => l.Fragments).SelectMany(f => f.Glyphs), g => g.Text is "\u2010" or "-");
    }

    [Fact]
    public void A_hyphenated_entry_with_a_leader_still_fills_its_last_line()
    {
        var width = Width("The hyphen\u2010") + 1;
        var layout = new Paragraph(Auto).Add("The hyphenation ").AddLeader(". ").Add("12").Layout(width);

        Assert.Equal(2, layout.Lines.Count);
        Assert.EndsWith("\u2010", layout.Lines[0].Fragments[^1].Run.Text, StringComparison.Ordinal);
        Assert.Equal(width, layout.Lines[1].X + layout.Lines[1].Width, 6);
        Assert.True(layout.Lines[0].Width < width); // The hyphenated line has no leader and is not stretched.
    }

    private static double Width(string text) => TextShaper.Shape(text, Body.Fonts, Body.Shaping).GetWidth(Body.FontSize);
}
