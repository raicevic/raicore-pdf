using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>Text decoration lines drawn by <see cref="ParagraphLayout"/>, checked in the content stream. Text is 10 pt.</summary>
public class TextDecorationTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 10) { LineHeight = LineHeight.Points(12) };
    private static readonly PdfColor Red = PdfColor.FromRgb(255, 0, 0);

    [Fact]
    public void The_font_provides_underline_and_strikeout_metrics()
    {
        // Liberation Sans: post underline position -67 and thickness 150, OS/2 strikeout 530 and 102, at 2048 units/em.
        Assert.Equal(-67 / 2048.0, Sans.UnderlinePosition, 9);
        Assert.Equal(150 / 2048.0, Sans.UnderlineThickness, 9);
        Assert.Equal(530 / 2048.0, Sans.StrikeoutPosition, 9);
        Assert.Equal(102 / 2048.0, Sans.StrikeoutThickness, 9);
        Assert.Equal(-0.1, PdfFont.Standard(StandardFont.Helvetica).UnderlinePosition); // Defaults for standard fonts.
    }

    [Fact]
    public void An_underline_is_one_rectangle_below_the_baseline_across_adjacent_text()
    {
        var underline = new TextDecoration(TextDecorationLines.Underline, Red);
        var style = Text with { Decorations = [underline] };
        var (ops, baseline) = Draw(new Paragraph(Text).Add("one ", style).Add("two", style with { Color = PdfColor.Black }));

        var rect = Assert.Single(Rectangles(ops));
        Assert.Equal(baseline + (Sans.UnderlinePosition * 10) - (Sans.UnderlineThickness * 10), rect.Y, 3);
        Assert.Equal(Sans.UnderlineThickness * 10, rect.Height, 3);
        Assert.Equal(0, rect.X, 3);
        Assert.Equal(W("one two"), rect.Width, 3);
        Assert.Contains("1 0 0 rg", ops, StringComparison.Ordinal); // Drawn in the decoration's color.
    }

    [Fact]
    public void Lines_through_and_over_use_the_font_metrics()
    {
        var decoration = new TextDecoration(TextDecorationLines.LineThrough | TextDecorationLines.Overline, Red);
        var (ops, baseline) = Draw(new Paragraph(Text).Add("text", Text with { Decorations = [decoration] }));

        var tops = Rectangles(ops).Select(r => r.Y + r.Height).Order().ToList();
        Assert.Equal(baseline + (Sans.StrikeoutPosition * 10), tops[0], 3);
        Assert.Equal(baseline + (Sans.Ascent * 10), tops[1], 3);
    }

    [Fact]
    public void Different_decorations_and_gaps_give_separate_segments()
    {
        var a = new TextDecoration(TextDecorationLines.Underline, Red);
        var b = new TextDecoration(TextDecorationLines.Underline, Red);
        var box = new ContainerBlock(new BoxStyle { Width = 20 }, new SpacerBlock(5));
        var (ops, _) = Draw(new Paragraph(Text)
            .Add("x", Text with { Decorations = [a] }).Add("y", Text with { Decorations = [b] })
            .Add("z", Text with { Decorations = [b] }).AddBox(box).Add("w", Text with { Decorations = [b] }));

        Assert.Equal(3, Rectangles(ops).Count); // a over "x"; b over "yz"; the box breaks b, so b again over "w".
    }

    [Fact]
    public void The_declaring_elements_font_and_baseline_place_the_line()
    {
        var serif = PdfFont.Load(Fixtures.Path("LiberationSerif-Regular.ttf"));
        var decoration = new TextDecoration(TextDecorationLines.Underline, Red) { Font = serif, FontSize = 20, BaselineShift = 3 };
        var (ops, baseline) = Draw(new Paragraph(Text).Add("x", Text with { Decorations = [decoration], VerticalAlign = InlineAlignment.Raised(-4) }));

        var rect = Assert.Single(Rectangles(ops));
        Assert.NotEqual(Sans.UnderlinePosition, serif.UnderlinePosition);
        Assert.Equal(baseline + 3 + (serif.UnderlinePosition * 20), rect.Y + rect.Height, 3);
        Assert.Equal(serif.UnderlineThickness * 20, rect.Height, 3);
    }

    [Fact]
    public void Thickness_and_offset_can_be_set()
    {
        var decoration = new TextDecoration(TextDecorationLines.Underline, Red) { Thickness = 2, UnderlineOffset = 5 };
        var (ops, baseline) = Draw(new Paragraph(Text).Add("x", Text with { Decorations = [decoration] }));

        var rect = Assert.Single(Rectangles(ops));
        Assert.Equal(baseline - 5, rect.Y + rect.Height, 3);
        Assert.Equal(2, rect.Height, 3);
    }

    [Theory]
    [InlineData(TextDecorationStyle.Double, @"\bre\b.*\bre\b")]
    [InlineData(TextDecorationStyle.Dotted, @"\[0 [\d.]+\] 0 d")]
    [InlineData(TextDecorationStyle.Dashed, @"\[[\d.]+ [\d.]+\] 0 d")]
    [InlineData(TextDecorationStyle.Wavy, @" c\n")]
    public void Styles_are_drawn_as_expected(TextDecorationStyle style, string pattern)
    {
        var decoration = new TextDecoration(TextDecorationLines.Underline, Red) { Style = style };
        var (ops, _) = Draw(new Paragraph(Text).Add("decorated text", Text with { Decorations = [decoration] }));

        Assert.Matches(new Regex(pattern, RegexOptions.Singleline), ops);
    }

    private static (string Ops, double Baseline) Draw(Paragraph paragraph)
    {
        var layout = paragraph.Layout(1000);
        var page = new PdfDocument().AddPage(PdfPageSize.A4);
        layout.Draw(page.Canvas, 0, 500);
        return (Encoding.Latin1.GetString(page.Canvas.ToBytes()), 500 - layout.Lines[0].Baseline);
    }

    private static List<(double X, double Y, double Width, double Height)> Rectangles(string ops) =>
        [.. Regex.Matches(ops, @"(\S+) (\S+) (\S+) (\S+) re").Select(m => (D(m.Groups[1]), D(m.Groups[2]), D(m.Groups[3]), D(m.Groups[4])))];

    private static double D(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);

    private static double W(string text) => RaiCore.Pdf.Layout.Text.TextShaper.Shape(text, Text.Fonts).GetWidth(10);
}
