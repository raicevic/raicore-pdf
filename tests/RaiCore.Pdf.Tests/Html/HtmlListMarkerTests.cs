using PDFtoImage;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>Where outside list markers go, measured on the rendered page (one pixel per 0.25 pt).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlListMarkerTests
{
    private const int Dpi = 288;
    private const double Scale = 72.0 / Dpi;

    [Theory]
    [InlineData("ltr", 12)]
    [InlineData("rtl", 12)]
    [InlineData("ltr", 9)]
    [InlineData("rtl", 20)]
    public void A_bullet_sits_where_browsers_draw_it(string dir, double size)
    {
        // Blink: the marker box starts ascent * 2/3 + 8 px before the text, and its disc, (ascent * 2/3 + 1) / 2 px wide,
        // is 1 px into it (whole pixels). Liberation Sans' ascent is 0.905 em.
        var px = size / 0.75;
        var offset = Math.Floor(Math.Round(0.905 * px) * 2 / 3);
        var disc = Math.Floor((offset + 1) / 2);
        var expected = (offset + 7 - (disc / 2)) * 0.75; // From the text's start edge to the disc's center.

        var (text, bullet) = Measure(dir, $"<ul dir='{dir}' style='font: {size}pt Liberation Sans; margin: 0; padding-inline-start: 60pt'><li>Ill</li></ul>");

        Assert.Equal(expected, Math.Abs(text - bullet), 0.6);
        Assert.Equal(dir == "rtl", bullet > text);
    }

    [Fact]
    public void A_hanging_marker_in_a_right_to_left_item_is_not_an_overflow()
    {
        var diagnostics = new List<HtmlDiagnostic>();
        HtmlToPdf.ConvertToBytes(
            "<ul dir='rtl'><li>שלום</li></ul><ol dir='rtl'><li>שלום</li></ol>",
            new HtmlConverterOptions { Diagnostics = diagnostics.Add });

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_line_of_a_right_to_left_item_wider_than_its_box_is_still_an_overflow()
    {
        var diagnostics = new List<HtmlDiagnostic>();
        HtmlToPdf.ConvertToBytes(
            "<ul dir='rtl' style='width: 100pt'><li>" + new string('ש', 60) + "</li></ul>",
            new HtmlConverterOptions { Diagnostics = diagnostics.Add });

        Assert.Contains(diagnostics, d => d.Kind == HtmlDiagnosticKind.Overflow);
    }

    /// <summary>The text's start edge and the bullet's center, in points from the page's left edge.</summary>
    private static (double Text, double Bullet) Measure(string dir, string html)
    {
        var pdf = HtmlToPdf.ConvertToBytes($"<style>@page {{ size: 300pt 100pt; margin: 0 }} body {{ margin: 0 }}</style>{html}");
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: Dpi));

        // Inked columns, grouped into runs: the bullet is the run at the outer end (left in ltr, right in rtl), on a
        // 300 pt wide page.
        var inked = Enumerable.Range(0, bitmap.Width).Where(x => Enumerable.Range(0, bitmap.Height / 2).Any(y => bitmap.GetPixel(x, y).Red < 128)).ToList();
        var runs = new List<(int Start, int End)>();
        foreach (var x in inked)
        {
            if (runs.Count > 0 && x - runs[^1].End <= 1)
            {
                runs[^1] = (runs[^1].Start, x);
            }
            else
            {
                runs.Add((x, x));
            }
        }

        // The text starts 60 pt from the page's start edge (its padding); the bullet's ink is measured.
        var bullet = dir == "rtl" ? runs[^1] : runs[0];
        return (dir == "rtl" ? 240 : 60, (bullet.Start + bullet.End + 1) / 2.0 * Scale);
    }
}
