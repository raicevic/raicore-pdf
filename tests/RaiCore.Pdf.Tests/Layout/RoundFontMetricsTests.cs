using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// <see cref="TextStyle.RoundFontMetrics"/> (#152): line boxes from the font's metrics in whole CSS pixels, as Chrome
/// lays them out. Liberation Sans: ascent 0.905, descent 0.212 and line gap 0.033 em.
/// </summary>
public class RoundFontMetricsTests
{
    private const double Px = 0.75;
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    [Theory]
    [InlineData(9, 14, 11)] // 12 px: 10.86 + 2.54 + 0.40 px: 11 + 3 + 0.
    [InlineData(12, 18, 14)] // 16 px: 14.48 + 3.39 + 0.53 px: 14 + 3 + 1; the 1 px gap goes below the text (half rounded down).
    [InlineData(16, 25, 19)] // 21.33 px: 19.31 + 4.52 + 0.70 px: 19 + 5 + 1.
    public void A_normal_line_is_the_sum_of_the_rounded_metrics(double fontSize, double height, double baseline)
    {
        var line = Line(new TextStyle(Sans, fontSize) { RoundFontMetrics = true }); // Points.

        Assert.Equal(height * Px, line.Height, 6);
        Assert.Equal(baseline * Px, line.Baseline, 6);
    }

    [Fact]
    public void The_leading_above_a_line_of_a_given_height_is_whole_pixels()
    {
        // 11.5 pt (15.33 px) at 1.45: a 22.23 px line around 14 + 3 px of text; 2.62 px of leading above becomes 2.
        var line = Line(new TextStyle(Sans, 11.5) { RoundFontMetrics = true, LineHeight = LineHeight.Multiple(1.45) });

        Assert.Equal(11.5 * 1.45, line.Height, 6);
        Assert.Equal(16 * Px, line.Baseline, 6);
    }

    [Fact]
    public void Without_rounding_the_metrics_are_exact()
    {
        var line = Line(new TextStyle(Sans, 9));

        Assert.Equal((Sans.Ascent - Sans.Descent + Sans.LineGap) * 9, line.Height, 6);
        Assert.Equal((Sans.Ascent + (Sans.LineGap / 2)) * 9, line.Baseline, 6);
    }

    private static LineLayout Line(TextStyle style) => new Paragraph(style).Add("Hg").Layout(500).Lines[0];
}
