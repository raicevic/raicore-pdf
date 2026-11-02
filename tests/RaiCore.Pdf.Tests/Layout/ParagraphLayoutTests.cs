using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class ParagraphLayoutTests
{
    private const string Lorem =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et " +
        "dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip.";

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont SansBold = PdfFont.Load(Fixtures.Path("LiberationSans-Bold.ttf"));
    private static readonly PdfFont Arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
    private static readonly TextStyle Body = new(Sans, 12);

    [Fact]
    public void Wraps_at_word_boundaries_within_the_width()
    {
        var layout = new Paragraph(Body).Add(Lorem).Layout(200);

        Assert.True(layout.Lines.Count >= 5, $"{layout.Lines.Count} lines");
        Assert.All(layout.Lines, l => Assert.True(l.Width <= 200 + 1e-6, $"Line is {l.Width} wide"));
        AssertLinesCoverText(layout, Lorem.Length);
        foreach (var line in layout.Lines.Take(layout.Lines.Count - 1))
        {
            Assert.Equal(' ', Lorem[line.End - 1]); // Every break is after a space.
        }
    }

    [Fact]
    public void Lines_are_as_full_as_possible()
    {
        var layout = new Paragraph(Body).Add(Lorem).Layout(200);

        // Greedy filling: adding the next word to any line would overflow.
        for (var i = 0; i < layout.Lines.Count - 1; i++)
        {
            var next = layout.Lines[i + 1];
            var nextWordEnd = Lorem.IndexOf(' ', next.Start) is var space and >= 0 ? space : Lorem.Length;
            var extended = Sans.MeasureText(Lorem[layout.Lines[i].Start..nextWordEnd], 12);
            Assert.True(extended > 200, $"Line {i} could also fit '{Lorem[next.Start..nextWordEnd]}'");
        }
    }

    [Fact]
    public void Hard_breaks_start_new_lines_and_empty_lines_keep_their_height()
    {
        var layout = new Paragraph(Body).Add("one\ntwo\n\nfour").Layout(500);

        Assert.Equal(4, layout.Lines.Count);
        Assert.Empty(layout.Lines[2].Fragments);
        Assert.Equal(layout.Lines[0].Height, layout.Lines[2].Height, 6);
    }

    [Fact]
    public void Normal_line_height_comes_from_the_font()
    {
        var line = new Paragraph(Body).Add("Hello").Layout(500).Lines.Single();

        // Liberation Sans: ascent 0.905, descent 0.212, line gap 0.033 (em).
        Assert.Equal((0.905 + 0.212 + 0.033) * 12, line.Height, 2);
        Assert.Equal((0.905 + (0.033 / 2)) * 12, line.Baseline, 2);
    }

    [Theory]
    [InlineData(1.5, 18)]
    [InlineData(1.0, 12)]
    public void Multiple_line_height_scales_the_font_size(double factor, double expected)
    {
        var style = Body with { LineHeight = LineHeight.Multiple(factor) };
        var layout = new Paragraph(style).Add(Lorem).Layout(200);

        Assert.All(layout.Lines, l => Assert.Equal(expected, l.Height, 6));
        Assert.Equal(expected * layout.Lines.Count, layout.Height, 6);
    }

    [Fact]
    public void Larger_text_on_a_line_makes_the_line_taller()
    {
        var big = Body with { FontSize = 24, Fonts = [SansBold] };
        var layout = new Paragraph(Body).Add("small ").Add("BIG", big).Add(" small").Layout(500);
        var line = layout.Lines.Single();

        Assert.Equal(new Paragraph(big).Add("BIG").Layout(500).Lines[0].Height, line.Height, 6);
        Assert.Equal(3, line.Fragments.Count);
    }

    [Theory]
    [InlineData(TextAlignment.Left)]
    [InlineData(TextAlignment.Right)]
    [InlineData(TextAlignment.Center)]
    public void Aligns_lines(TextAlignment alignment)
    {
        var layout = new Paragraph(Body) { Alignment = alignment }.Add(Lorem).Layout(200);

        foreach (var line in layout.Lines)
        {
            var expected = alignment switch
            {
                TextAlignment.Right => 200 - line.Width,
                TextAlignment.Center => (200 - line.Width) / 2,
                _ => 0,
            };
            Assert.Equal(expected, line.X, 6);
        }
    }

    [Fact]
    public void Justify_fills_every_line_but_the_last()
    {
        var layout = new Paragraph(Body) { Alignment = TextAlignment.Justify }.Add(Lorem).Layout(200);

        foreach (var line in layout.Lines.Take(layout.Lines.Count - 1))
        {
            Assert.Equal(200, line.Width, 3);
        }

        Assert.True(layout.Lines[^1].Width < 200);
    }

    [Fact]
    public void Justify_does_not_stretch_lines_ending_in_a_hard_break()
    {
        var layout = new Paragraph(Body) { Alignment = TextAlignment.Justify }.Add("short line\nnext").Layout(200);

        Assert.True(layout.Lines[0].Width < 100);
    }

    [Fact]
    public void Long_words_overflow_unless_breaking_is_allowed()
    {
        const string word = "Donaudampfschifffahrtsgesellschaftskapit\u00E4n";

        var overflowing = new Paragraph(Body).Add(word).Layout(100);
        Assert.Single(overflowing.Lines);
        Assert.True(overflowing.Lines[0].Width > 100);

        var broken = new Paragraph(Body) { BreakLongWords = true }.Add(word).Layout(100);
        Assert.True(broken.Lines.Count > 1);
        Assert.All(broken.Lines, l => Assert.True(l.Width <= 100 + 1e-6));
        AssertLinesCoverText(broken, word.Length);
    }

    [Fact]
    public void Soft_hyphens_become_visible_only_at_a_break()
    {
        const string text = "extra\u00ADordinary";
        var unbroken = new Paragraph(Body).Add(text).Layout(500);
        var broken = new Paragraph(Body).Add(text).Layout(45);

        Assert.Single(unbroken.Lines);
        Assert.Equal(Sans.MeasureText("extraordinary", 12), unbroken.Lines[0].Width, 1);
        Assert.Equal(2, broken.Lines.Count);
        Assert.EndsWith("\u2010", broken.Lines[0].Fragments[^1].Run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void First_line_indent_shifts_and_narrows_the_first_line()
    {
        var layout = new Paragraph(Body) { FirstLineIndent = 30 }.Add(Lorem).Layout(200);

        Assert.Equal(30, layout.Lines[0].X, 6);
        Assert.True(layout.Lines[0].Width <= 170 + 1e-6);
        Assert.Equal(0, layout.Lines[1].X, 6);
    }

    [Fact]
    public void Wraps_arabic_text()
    {
        const string arabic = "\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645 \u0648\u0631\u062D\u0645\u0629 \u0627\u0644\u0644\u0647 \u0648\u0628\u0631\u0643\u0627\u062A\u0647";
        var layout = new Paragraph(new TextStyle(Arabic, 14)).Add(arabic).Layout(90);

        Assert.True(layout.Lines.Count > 1);
        AssertLinesCoverText(layout, arabic.Length);
        Assert.All(layout.Lines.SelectMany(l => l.Fragments), f => Assert.Equal("Arab", f.Run.Script));
    }

    [Fact]
    public void Empty_paragraph_has_no_lines() => Assert.Equal(0, new Paragraph(Body).Layout(100).Height);

    [Fact]
    public void Drawn_paragraph_is_valid_renders_and_extracts()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A4);
        var red = Body with { Color = PdfColor.FromRgb(200, 0, 0), Fonts = [SansBold] };
        var paragraph = new Paragraph(Body) { Alignment = TextAlignment.Justify }
            .Add("The quick brown fox ")
            .Add("jumps", red)
            .Add(" over the lazy dog. " + Lorem);
        var layout = paragraph.Layout(300);
        layout.Draw(page.Canvas, 72, 770);
        var pdf = doc.ToArray();

        ExternalTools.AssertQpdfClean(pdf);
        var text = ExternalTools.ExtractText(pdf);
        Assert.Contains("jumps", text, StringComparison.Ordinal);
        Assert.Contains("consectetur", text, StringComparison.Ordinal);

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        var redPixels = 0;
        for (var x = 72; x < 372; x++)
        {
            for (var y = (int)(bitmap.Height - 770); y < bitmap.Height - 770 + layout.Lines[0].Height; y++)
            {
                var c = bitmap.GetPixel(x, y);
                redPixels += c.Red > 150 && c.Green < 80 ? 1 : 0;
            }
        }

        Assert.True(redPixels > 20, "The red word was not drawn on the first line.");
    }

    [Fact]
    public void Splitting_lines_across_pages_draws_each_line_once()
    {
        var layout = new Paragraph(Body).Add(Lorem).Layout(200);
        var doc = new PdfDocument { CompressContent = false };
        var half = layout.Lines.Count / 2;
        layout.Draw(doc.AddPage().Canvas, 72, 700, 0, half);
        layout.Draw(doc.AddPage().Canvas, 72, 700, half, layout.Lines.Count);

        var pdf = Encoding.Latin1.GetString(doc.ToArray());
        Assert.Equal(layout.Lines.Sum(l => l.Fragments.Count), CountOccurrences(pdf, " TJ"));
    }

    private static void AssertLinesCoverText(ParagraphLayout layout, int length)
    {
        Assert.Equal(0, layout.Lines[0].Start);
        for (var i = 1; i < layout.Lines.Count; i++)
        {
            Assert.Equal(layout.Lines[i - 1].End, layout.Lines[i].Start);
        }

        Assert.Equal(length, layout.Lines[^1].End);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
