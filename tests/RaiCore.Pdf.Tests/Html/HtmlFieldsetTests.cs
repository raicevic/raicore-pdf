using PDFtoImage;
using RaiCore.Pdf.Html;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// <c>fieldset</c> and <c>legend</c> (#158): the legend straddles the top border, which is cut behind it, as in Chrome.
/// Pages are 200 x 100 pt without margins, rendered at 72 dpi (1 pt per pixel).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlFieldsetTests
{
    private const string Page = "<style>@page { size: 200pt 100pt; margin: 0 } body { margin: 0 }</style>";
    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Lime = new(0, 255, 0);

    [Fact]
    public void A_legend_higher_than_the_border_moves_the_border_to_its_middle_and_cuts_it()
    {
        // The legend is 20 pt high at 14-54 pt: the 4 pt border is centered on it (8-12 pt), the content starts below it.
        using var bitmap = Render($"""
            <fieldset style="margin: 0; border: 4pt solid #ff0000; padding: 0 10pt">
              <legend style="height: 20pt; width: 40pt; padding: 0"></legend>
              <div style="height: 10pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(White, bitmap.GetPixel(100, 2)); // Nothing above the moved border.
        Assert.Equal(Red, bitmap.GetPixel(100, 10));
        Assert.Equal(White, bitmap.GetPixel(30, 10)); // Cut behind the legend.
        Assert.Equal(White, bitmap.GetPixel(2, 5)); // The side borders start at the top border too.
        Assert.Equal(Red, bitmap.GetPixel(2, 20));
        Assert.Equal(White, bitmap.GetPixel(100, 17));
        Assert.Equal(Lime, bitmap.GetPixel(100, 25));
    }

    [Fact]
    public void A_legend_lower_than_the_border_is_centered_in_it()
    {
        // A 30 pt border: the 10 pt legend at 40-80 pt is 10 pt down; the border is cut from the top for its width.
        using var bitmap = Render($"""
            <fieldset style="margin: 0; border: 30pt solid #ff0000; padding: 0 10pt">
              <legend style="height: 10pt; width: 40pt; padding: 0; background: #0000ff"></legend>
              <div style="height: 10pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(Blue, bitmap.GetPixel(60, 15));
        Assert.Equal(White, bitmap.GetPixel(60, 5));
        Assert.Equal(White, bitmap.GetPixel(60, 25));
        Assert.Equal(Red, bitmap.GetPixel(100, 5));
        Assert.Equal(Lime, bitmap.GetPixel(100, 35));
    }

    [Fact]
    public void The_bottom_margin_of_the_legend_pushes_the_content_down_and_its_top_margin_is_ignored()
    {
        using var bitmap = Render($"""
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt">
              <legend style="height: 10pt; width: 40pt; padding: 0; margin: 30pt 0 10pt; background: #0000ff"></legend>
              <div style="height: 10pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(Blue, bitmap.GetPixel(30, 5));
        Assert.Equal(White, bitmap.GetPixel(100, 15));
        Assert.Equal(Lime, bitmap.GetPixel(100, 25));
    }

    [Theory]
    [InlineData("", "", 32)]
    [InlineData("", "align=\"right\"", 168)]
    [InlineData("", "align=\"center\"", 100)]
    [InlineData("", "style=\"margin: 0 auto\"", 100)]
    [InlineData("", "style=\"margin-left: auto\"", 168)]
    [InlineData("", "style=\"margin-left: 30pt\"", 62)]
    [InlineData("", "style=\"text-align: end\"", 168)]
    [InlineData("style=\"text-align: center\"", "align=\"right\"", 168)] // The attribute beats the inherited alignment.
    [InlineData("style=\"text-align: center\"", "", 100)]
    [InlineData("dir=\"rtl\"", "", 168)]
    [InlineData("dir=\"rtl\"", "style=\"text-align: left\"", 32)]
    public void The_legend_goes_where_its_auto_margins_or_its_alignment_put_it(string fieldset, string legend, int center)
    {
        // The content is 176 pt wide (12-188 pt) and the legend 40 pt.
        var legendStyle = legend.StartsWith("style", StringComparison.Ordinal) ? legend[..^1] + "; " : "style=\"";
        var legendAttributes = legend.StartsWith("style", StringComparison.Ordinal) ? string.Empty : legend;
        using var bitmap = Render($"""
            <fieldset {fieldset} style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt">
              <legend {legendAttributes} {legendStyle}height: 10pt; width: 40pt; padding: 0; background: #0000ff"></legend>
              x
            </fieldset>
            """);

        foreach (var x in new[] { 32, 62, 100, 168 })
        {
            Assert.Equal(x == center ? Blue : White, bitmap.GetPixel(x, 2));
        }
    }

    [Fact]
    public void The_first_legend_is_the_rendered_one_wherever_it_is_and_others_stay_in_the_content()
    {
        using var bitmap = Render($"""
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt">
              <div style="height: 10pt; background: #00ff00"></div>
              <legend style="height: 10pt; width: 40pt; padding: 0; background: #0000ff"></legend>
              <legend style="height: 10pt; width: 40pt; padding: 0; background: #ff00ff"></legend>
            </fieldset>
            """);

        Assert.Equal(Blue, bitmap.GetPixel(30, 5));
        Assert.Equal(Lime, bitmap.GetPixel(30, 15));
        Assert.Equal(new SKColor(255, 0, 255), bitmap.GetPixel(30, 25));
    }

    [Fact]
    public void A_floated_legend_stays_in_the_content()
    {
        using var bitmap = Render($"""
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 10pt">
              <legend style="float: left; height: 10pt; width: 40pt; padding: 0; background: #0000ff"></legend>
            </fieldset>
            """);

        Assert.Equal(Red, bitmap.GetPixel(30, 1)); // The border is not cut.
        Assert.Equal(Blue, bitmap.GetPixel(30, 15));
    }

    [Fact]
    public void A_fieldset_with_a_legend_moves_to_the_next_page_when_its_legend_and_first_content_do_not_fit()
    {
        // At 75 pt, the 20 pt legend and the 10 pt content below it would end at 105 pt.
        var pdf = HtmlToPdf.ConvertToBytes($"""
            {Page}
            <div style="height: 75pt"></div>
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt">
              <legend style="height: 20pt; width: 40pt; padding: 0; background: #0000ff"></legend>
              <div style="height: 10pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(2, Conversion.GetPageCount(pdf));
        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));
        Assert.Equal(White, first.GetPixel(30, 85));
        Assert.Equal(Blue, second.GetPixel(30, 10));
        Assert.Equal(Lime, second.GetPixel(100, 25));
    }

    [Fact]
    public void A_fieldset_has_the_browsers_default_border_and_padding()
    {
        // 2 px groove border in Chrome's light gray, 0.75 em padding on the sides (12 px at 16 px), 2 px margins on the sides.
        var pdf = HtmlToPdf.ConvertToBytes(Page + "<fieldset><legend>Legend</legend><div style=\"height: 20pt; background: #00ff00\"></div></fieldset>");
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 288));

        Assert.InRange(bitmap.GetPixel(10, 120).Red, 225, 245); // The inner half of the left groove (2.25-3 pt) is the color itself.
        Assert.InRange(bitmap.GetPixel(7, 120).Red, 100, 200); // The outer half is darker.
        Assert.Equal(White, bitmap.GetPixel(44, 120));
        Assert.Equal(Lime, bitmap.GetPixel(52, 120));
    }

    [Fact]
    public void A_legend_shrinks_to_fit_with_the_default_padding_on_its_sides()
    {
        // At 12 pt, 20 pt content and 1.5 pt (2 px) padding on each side: 12-35 pt.
        using var bitmap = Render($"""
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt">
              <legend style="background: #0000ff"><div style="width: 20pt; height: 10pt; background: #00ff00"></div></legend>
            </fieldset>
            """);

        Assert.Equal(Blue, bitmap.GetPixel(12, 2));
        Assert.Equal(Lime, bitmap.GetPixel(20, 2));
        Assert.Equal(Blue, bitmap.GetPixel(34, 2));
        Assert.Equal(White, bitmap.GetPixel(37, 2));
        Assert.Equal(White, bitmap.GetPixel(60, 2));
    }

    [Fact]
    public void A_fieldset_contains_its_floats()
    {
        using var bitmap = Render("""
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0">
              <div style="float: left; width: 20pt; height: 50pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(Red, bitmap.GetPixel(100, 53));
    }

    [Fact]
    public void A_fieldset_kept_together_is_measured_with_its_legend()
    {
        // The 10 pt legend, the 20 pt content and the 2 pt bottom border: 32 pt from 67 pt, so it fits on the first page.
        var pdf = HtmlToPdf.ConvertToBytes($"""
            {Page}
            <div style="height: 67pt"></div>
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt; break-inside: avoid">
              <legend style="height: 10pt; width: 40pt; padding: 0; background: #0000ff"></legend>
              <div style="height: 20pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(1, Conversion.GetPageCount(pdf));
    }

    [Fact]
    public void A_fieldset_kept_together_that_does_not_fit_with_its_legend_moves_to_the_next_page()
    {
        // 32 pt from 72 pt does not fit; without the legend's height it would (its start alone does).
        var pdf = HtmlToPdf.ConvertToBytes($"""
            {Page}
            <div style="height: 72pt"></div>
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt; break-inside: avoid">
              <legend style="height: 10pt; width: 40pt; padding: 0; background: #0000ff"></legend>
              <div style="height: 10pt; background: #00ff00"></div>
              <div style="height: 10pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(2, Conversion.GetPageCount(pdf));
        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        Assert.Equal(White, first.GetPixel(30, 77));
    }

    [Fact]
    public void The_background_of_a_split_fieldset_continues_from_where_the_border_starts()
    {
        // The box starts 10 pt down (the middle of the 20 pt legend) and ends at 170 pt: a 160 pt background, whose stop
        // at 75 % (120 pt) is 30 pt down the second page.
        var pdf = HtmlToPdf.ConvertToBytes($"""
            {Page}
            <fieldset style="margin: 0; border: 0; padding: 0; background: linear-gradient(#ff0000 75%, #0000ff 75%)">
              <legend style="height: 20pt; width: 40pt; padding: 0"></legend>
              <div style="height: 150pt"></div>
            </fieldset>
            """);
        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));

        Assert.Equal(White, first.GetPixel(100, 5));
        Assert.Equal(Red, first.GetPixel(100, 15));
        Assert.Equal(Red, second.GetPixel(100, 26));
        Assert.Equal(Blue, second.GetPixel(100, 34));
    }

    [Fact]
    public void Content_after_a_legend_longer_than_the_page_follows_it()
    {
        var pdf = HtmlToPdf.ConvertToBytes($"""
            {Page}
            <fieldset style="margin: 0; border: 2pt solid #ff0000; padding: 0 10pt">
              <legend style="padding: 0"><div style="width: 40pt; height: 150pt; background: #0000ff"></div></legend>
              <div style="height: 10pt; background: #00ff00"></div>
            </fieldset>
            """);

        Assert.Equal(2, Conversion.GetPageCount(pdf));
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));
        Assert.Equal(Blue, second.GetPixel(30, 45));
        Assert.Equal(Lime, second.GetPixel(100, 55));
    }

    private static SKBitmap Render(string body) =>
        Conversion.ToImage(HtmlToPdf.ConvertToBytes(Page + body), page: 0, options: new RenderOptions(Dpi: 72));
}
