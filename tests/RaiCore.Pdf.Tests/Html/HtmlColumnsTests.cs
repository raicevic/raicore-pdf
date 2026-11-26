using PDFtoImage;
using RaiCore.Pdf.Html;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// CSS multi-column layout (#73): <c>columns</c>, <c>column-count</c>, <c>column-width</c>, <c>column-gap</c>,
/// <c>column-rule</c>, <c>column-fill</c>, <c>column-span</c> and column breaks. Pages are 200 x 100 pt without
/// margins, rendered at 72 dpi (1 pt per pixel).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlColumnsTests
{
    private const string Page = "<style>@page { size: 200pt 100pt; margin: 0 } body { margin: 0 } div div { height: 20pt }</style>";
    private static readonly SKColor White = new(255, 255, 255);
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Lime = new(0, 255, 0);

    [Fact]
    public void Blocks_are_balanced_over_the_columns()
    {
        // Two 100 pt columns without a gap: red and blue in the first, lime in the second; the next block below them.
        using var bitmap = Render("""
            <div style="columns: 2; column-gap: 0">
              <div style="background: #ff0000"></div><div style="background: #0000ff"></div><div style="background: #00ff00"></div>
            </div>
            <p style="margin: 0; height: 10pt; background: #ff0000"></p>
            """);

        Assert.Equal(Red, bitmap.GetPixel(50, 10));
        Assert.Equal(Blue, bitmap.GetPixel(50, 30));
        Assert.Equal(Lime, bitmap.GetPixel(150, 10));
        Assert.Equal(White, bitmap.GetPixel(150, 30));
        Assert.Equal(Red, bitmap.GetPixel(150, 45)); // The paragraph after the columns, below the taller one.
    }

    [Fact]
    public void The_gap_and_the_rule_are_between_the_columns()
    {
        // column-gap 20pt: columns 0-90 and 110-200; a 4 px (3 pt) blue rule centered at 100.
        using var bitmap = Render("""
            <div style="column-count: 2; column-gap: 20pt; column-rule: 4px solid #0000ff">
              <div style="background: #ff0000"></div><div style="background: #00ff00"></div>
            </div>
            """);

        Assert.Equal(Red, bitmap.GetPixel(85, 10));
        Assert.Equal(White, bitmap.GetPixel(95, 10));
        Assert.Equal(Blue, bitmap.GetPixel(100, 10));
        Assert.Equal(White, bitmap.GetPixel(105, 10));
        Assert.Equal(Lime, bitmap.GetPixel(115, 10));
        Assert.Equal(White, bitmap.GetPixel(100, 25)); // As tall as the columns.
    }

    [Fact]
    public void An_element_spanning_the_columns_goes_across_them()
    {
        using var bitmap = Render("""
            <div style="columns: 2; column-gap: 0">
              <div style="background: #ff0000"></div><div style="background: #ff0000"></div>
              <h2 style="column-span: all; margin: 0; height: 10pt; background: #0000ff"></h2>
              <div style="background: #00ff00"></div>
            </div>
            """);

        Assert.Equal(Red, bitmap.GetPixel(50, 10));
        Assert.Equal(Red, bitmap.GetPixel(150, 10));
        Assert.Equal(Blue, bitmap.GetPixel(10, 25));
        Assert.Equal(Blue, bitmap.GetPixel(190, 25));
        Assert.Equal(Lime, bitmap.GetPixel(50, 40));
        Assert.Equal(White, bitmap.GetPixel(150, 40)); // One block: it stays in the first column.
    }

    [Fact]
    public void A_column_break_starts_the_next_column()
    {
        using var bitmap = Render("""
            <div style="columns: 2; column-gap: 0">
              <div style="background: #ff0000"></div><div style="break-before: column; background: #00ff00"></div><div style="background: #0000ff"></div>
            </div>
            """);

        // Balanced without the break, red and lime would share the first column.
        Assert.Equal(Red, bitmap.GetPixel(50, 10));
        Assert.Equal(White, bitmap.GetPixel(50, 30));
        Assert.Equal(Lime, bitmap.GetPixel(150, 10));
        Assert.Equal(Blue, bitmap.GetPixel(150, 30));
    }

    [Fact]
    public void A_column_width_makes_as_many_columns_as_fit()
    {
        // 55pt columns: three fit in 200pt with the default 1em (16px = 12pt) gap; each is (200 + 12) / 3 - 12 = 58.67 pt.
        using var bitmap = Render("""
            <div style="column-width: 55pt">
              <div style="background: #ff0000"></div><div style="background: #00ff00"></div><div style="background: #0000ff"></div>
            </div>
            """);

        Assert.Equal(Red, bitmap.GetPixel(30, 10));
        Assert.Equal(Lime, bitmap.GetPixel(100, 10));
        Assert.Equal(Blue, bitmap.GetPixel(170, 10));
        Assert.Equal(White, bitmap.GetPixel(64, 10));
    }

    [Fact]
    public void Text_alone_in_a_container_with_columns_flows_through_them()
    {
        using var bitmap = Render($"""
            <div style="columns: 2; column-gap: 40pt; font: 10pt/10pt sans-serif; color: #0000ff">{string.Join(" ", Enumerable.Repeat("word", 30))}</div>
            """);

        // Columns 0-80 and 120-200: text in both, none in the gap; balanced, so short.
        Assert.Contains(Enumerable.Range(0, 100), y => Enumerable.Range(0, 80).Any(x => bitmap.GetPixel(x, y) != White));
        Assert.Contains(Enumerable.Range(0, 100), y => Enumerable.Range(120, 80).Any(x => bitmap.GetPixel(x, y) != White));
        Assert.DoesNotContain(Enumerable.Range(0, 100), y => Enumerable.Range(85, 30).Any(x => bitmap.GetPixel(x, y) != White));
        Assert.DoesNotContain(Enumerable.Range(70, 30), y => Enumerable.Range(0, 200).Any(x => bitmap.GetPixel(x, y) != White));
    }

    [Fact]
    public void Columns_in_a_flex_item_are_balanced()
    {
        // Like a boarding pass: four labels in four columns side by side, 20pt tall.
        using var bitmap = Render("""
            <div style="display: flex"><section style="flex: 1">
              <dl style="columns: 4; column-gap: 0; margin: 0">
                <dt style="height: 10pt; background: #ff0000"></dt><dd style="margin: 0; height: 10pt; background: #00ff00"></dd>
                <dt style="height: 10pt; background: #ff0000"></dt><dd style="margin: 0; height: 10pt; background: #00ff00"></dd>
                <dt style="height: 10pt; background: #ff0000"></dt><dd style="margin: 0; height: 10pt; background: #00ff00"></dd>
                <dt style="height: 10pt; background: #ff0000"></dt><dd style="margin: 0; height: 10pt; background: #00ff00"></dd>
              </dl>
            </section></div>
            """);

        foreach (var x in new[] { 25, 75, 125, 175 })
        {
            Assert.Equal(Red, bitmap.GetPixel(x, 5));
            Assert.Equal(Lime, bitmap.GetPixel(x, 15));
            Assert.Equal(White, bitmap.GetPixel(x, 25));
        }
    }

    [Fact]
    public void Column_properties_are_not_reported_as_unsupported()
    {
        var found = new List<HtmlDiagnostic>();
        HtmlToPdf.ConvertToBytes(
            Page + """<div style="columns: 2 50pt; column-rule: 1px dotted red; column-fill: auto; height: 20pt"><p style="column-span: all; break-inside: avoid-column">x</p></div>""",
            new HtmlConverterOptions { Diagnostics = found.Add });

        Assert.Empty(found);
    }

    private static SKBitmap Render(string html) =>
        Conversion.ToImage(HtmlToPdf.ConvertToBytes(Page + html), page: 0, options: new RenderOptions(Dpi: 72));
}
