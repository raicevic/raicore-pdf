using System.Text.RegularExpressions;
using PDFtoImage;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Reading;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// Whole CSS pixels where Chrome uses them (#152): line boxes from rounded font metrics, border widths snapped to
/// pixels, and collapsed tables holding half their outer grid lines. Pages are 200 x 100 pt without margins.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlPixelMetricsTests
{
    private const string Page = "<style>@page { size: 200pt 100pt; margin: 0 } body { margin: 0 }</style>";
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Lime = new(0, 255, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    [Fact]
    public void Lines_of_normal_height_are_whole_pixels_as_in_chrome()
    {
        // 9 pt (12 px) Liberation Sans: 14 px lines (10.5 pt), not 13.8 px, so that long tables do not drift.
        var pdf = HtmlToPdf.ConvertToBytes(Page + "<div style=\"font: 9pt 'Liberation Sans'\">" + string.Concat(Enumerable.Range(1, 6).Select(i => $"<div>Line{i}</div>")) + "</div>");

        var words = new TextExtractor(PdfReader.Open(pdf)).FindOnPage(0, new Regex(@"Line\d")).Select(m => m.Bounds.Y).ToList();
        Assert.Equal(6, words.Count);
        Assert.Equal(5 * 10.5, words[0] - words[5], 3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.075, 0.75)] // Thinner than a pixel: one pixel.
    [InlineData(0.375, 0.75)] // 0.5 px.
    [InlineData(0.5, 0.75)] // 0.5 pt.
    [InlineData(1, 0.75)] // 1.33 px: rounded down.
    [InlineData(1.5, 1.5)] // 2 px.
    [InlineData(2, 1.5)] // 2.67 px.
    [InlineData(3, 3)] // 4 px.
    public void Border_widths_snap_to_whole_pixels(double points, double snapped) =>
        Assert.Equal(snapped, ComputedStyle.SnapBorderWidth(points), 9);

    [Fact]
    public void A_border_thinner_than_its_pixels_takes_them()
    {
        // A 1 pt top border is 1 px (0.75 pt): what follows starts there. At 288 dpi, 4 pixels per point.
        var pdf = HtmlToPdf.ConvertToBytes(Page + "<div style=\"border-top: 1pt solid #ff0000\"><div style=\"height: 10pt; background: #00ff00\"></div></div>");
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 288));

        Assert.Equal(Red, bitmap.GetPixel(100, 1));
        Assert.Equal(Lime, bitmap.GetPixel(100, 3));
    }

    [Fact]
    public void A_collapsed_table_holds_half_of_its_outer_grid_lines()
    {
        // A 4 px (3 pt) border: the table's box is 0-26 pt and its lines 0-3 and 23-26 pt; what follows starts at 26 pt.
        var pdf = HtmlToPdf.ConvertToBytes(Page + """
            <table style="border-collapse: collapse; margin: 0">
              <tr><td style="border: 4px solid #ff0000; padding: 0; background: #00ff00"><div style="width: 50pt; height: 20pt"></div></td></tr>
            </table>
            <div style="height: 10pt; background: #0000ff"></div>
            """);
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(Red, bitmap.GetPixel(25, 2));
        Assert.Equal(Red, bitmap.GetPixel(2, 12));
        Assert.Equal(Lime, bitmap.GetPixel(25, 12));
        Assert.Equal(Red, bitmap.GetPixel(25, 24));
        Assert.Equal(Blue, bitmap.GetPixel(25, 27));
    }
}
