using PDFtoImage;
using SkiaSharp;

namespace RaiCore.Pdf.Tests;

/// <summary>
/// Renders output with PDFium (the engine in Chrome) to prove real viewers accept it.
/// This is the seed of the visual-regression suite: later, full renders get compared with approved baselines.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class RenderingTests
{
    [Fact]
    public void Pdfium_renders_every_page_with_the_right_size()
    {
        var pdf = PdfDocumentTests.CreateSample().ToArray();

        Assert.Equal(2, Conversion.GetPageCount(pdf));
        var sizes = Conversion.GetPageSizes(pdf);
        Assert.Equal(595.28, sizes[0].Width, 0.01);
        Assert.Equal(841.89, sizes[0].Height, 0.01);
        Assert.Equal(792, sizes[1].Width, 0.01);
    }

    [Fact]
    public void Filled_rectangle_and_text_appear_where_drawn()
    {
        var pdf = PdfDocumentTests.CreateSample().ToArray();

        // 72 dpi: one pixel per point. PDF y runs upward, image y runs downward.
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        var pageHeight = bitmap.Height;

        var insideRectangle = bitmap.GetPixel(172, pageHeight - 650);
        Assert.InRange(insideRectangle.Red, 200, 240);
        Assert.InRange(insideRectangle.Green, 10, 50);

        var outside = bitmap.GetPixel(400, pageHeight - 400);
        Assert.Equal(SKColors.White, outside);

        // The headline baseline sits at y = 760; glyphs extend above it.
        Assert.True(CountDarkPixels(bitmap, x: 72, y: pageHeight - 780, width: 250, height: 30) > 100);
    }

    private static int CountDarkPixels(SKBitmap bitmap, int x, int y, int width, int height)
    {
        var count = 0;
        for (var px = x; px < x + width; px++)
        {
            for (var py = y; py < y + height; py++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.Red < 100 && c.Green < 100 && c.Blue < 100)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
