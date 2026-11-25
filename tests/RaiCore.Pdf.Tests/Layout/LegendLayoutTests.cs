using PDFtoImage;
using RaiCore.Pdf.Layout;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Containers with a legend (<see cref="ContainerBlock.HasLegend"/>), like an HTML fieldset. Uses a 200 x 100 pt page
/// without margins, rendered at 72 dpi (1 pt per pixel).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class LegendLayoutTests
{
    private static readonly PageSetup Small = new(new PdfPageSize(200, 100), Thickness.Zero);

    [Theory]
    [InlineData(BlockAlignment.Left, 30)]
    [InlineData(BlockAlignment.Center, 100)]
    [InlineData(BlockAlignment.Right, 170)]
    public void The_legend_straddles_the_top_border_where_its_alignment_puts_it(BlockAlignment alignment, int center)
    {
        // Content 10-190 pt; the 40 pt legend 20 pt high; the 4 pt border moves down to its middle (8-12 pt).
        var legend = new ContainerBlock(new BoxStyle { Width = 40, Height = 20, Background = PdfColor.FromRgb(0, 0, 255) });
        var content = new ContainerBlock(new BoxStyle { Height = 10, Background = PdfColor.FromRgb(0, 255, 0) });
        var fieldset = new ContainerBlock(
            new BoxStyle { Border = Borders.All(4, PdfColor.FromRgb(255, 0, 0)), Padding = Thickness.Symmetric(0, 6) }, legend, content)
        {
            HasLegend = true,
            LegendAlignment = alignment,
        };

        using var bitmap = Conversion.ToImage(new FlowDocument(Small).Add(fieldset).Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(center, 3));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(center == 100 ? 30 : 100, 3));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(center == 100 ? 30 : 100, 10));
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(100, 25));
    }

    [Fact]
    public void Margins_do_not_collapse_through_a_container_with_a_legend()
    {
        // No border or padding: the legend's top margin is ignored and the content's stays below the legend (10 + 10 pt).
        var legend = new ContainerBlock(new BoxStyle { Width = 40, Height = 10, Margin = new Thickness(5, 0, 0, 0), Background = PdfColor.FromRgb(0, 0, 255) });
        var content = new ContainerBlock(new BoxStyle { Height = 10, Margin = new Thickness(10, 0, 0, 0), Background = PdfColor.FromRgb(0, 255, 0) });
        var fieldset = new ContainerBlock(null, legend, content) { HasLegend = true };

        using var bitmap = Conversion.ToImage(new FlowDocument(Small).Add(fieldset).Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(20, 2));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(100, 15));
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(100, 25));
    }
}
