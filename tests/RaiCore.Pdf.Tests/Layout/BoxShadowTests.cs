using PDFtoImage;
using RaiCore.Pdf.Layout;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Box shadows (<see cref="BoxStyle.Shadows"/>, #155). A 50 x 20 pt box at (20, 10) on a 200 x 100 pt page without
/// margins, rendered at 72 dpi (1 pt per pixel).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class BoxShadowTests
{
    private static readonly PageSetup Small = new(new PdfPageSize(200, 100), Thickness.Zero);
    private static readonly PdfColor Red = PdfColor.FromRgb(255, 0, 0);
    private static readonly PdfColor Blue = PdfColor.FromRgb(0, 0, 255);
    private static readonly SKColor White = new(255, 255, 255);

    [Fact]
    public void A_sharp_shadow_is_the_box_moved_by_its_offset_and_only_outside_the_box()
    {
        using var bitmap = Render(new BoxShadow(5, 5, 0, 0, Red));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(72, 32)); // Below and right of the box.
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(30, 33));
        Assert.Equal(White, bitmap.GetPixel(40, 20)); // Not under the box (it has no background).
        Assert.Equal(White, bitmap.GetPixel(72, 12)); // The offset leaves the top right free.
        Assert.Equal(White, bitmap.GetPixel(77, 32));
    }

    [Fact]
    public void The_spread_grows_the_shadow_and_rounded_corners_with_it()
    {
        using var square = Render(new BoxShadow(0, 0, 0, 4, Blue));
        Assert.Equal(new SKColor(0, 0, 255), square.GetPixel(17, 20));
        Assert.Equal(new SKColor(0, 0, 255), square.GetPixel(17, 7)); // Square corners stay square.
        Assert.Equal(White, square.GetPixel(14, 20));

        using var rounded = Render(new BoxShadow(0, 0, 0, 4, Blue), radius: 8);
        Assert.Equal(new SKColor(0, 0, 255), rounded.GetPixel(17, 20));
        Assert.Equal(White, rounded.GetPixel(17, 7)); // Rounded by 8 + 4 pt.
    }

    [Fact]
    public void A_blurred_shadow_fades_out_over_twice_its_blur()
    {
        using var bitmap = Render(new BoxShadow(0, 0, 10, 0, PdfColor.Black));

        var near = bitmap.GetPixel(71, 20).Red; // Just outside the edge: about half.
        var middle = bitmap.GetPixel(76, 20).Red;
        Assert.InRange(near, 60, 190);
        Assert.True(middle > near, $"{middle} should be lighter than {near}");
        Assert.Equal(White, bitmap.GetPixel(85, 20)); // Beyond the blur.

        // A Gaussian with a standard deviation of half the blur (5 pt): 5 pt out, about 16% of the color, in both directions.
        Assert.InRange(bitmap.GetPixel(75, 20).Red, 205, 228);
        Assert.InRange(bitmap.GetPixel(45, 35).Red, 205, 228);
        Assert.Equal(White, bitmap.GetPixel(45, 20)); // Not under the box.
        Assert.Equal(bitmap.GetPixel(18, 20), bitmap.GetPixel(71, 20)); // Symmetric.
    }

    [Fact]
    public void An_inset_shadow_is_inside_the_padding_box_over_the_background()
    {
        var box = new ContainerBlock(new BoxStyle
        {
            Width = 50, Height = 20, Margin = new Thickness(10, 0, 0, 20), Background = PdfColor.FromRgb(0, 255, 0),
            Border = Borders.All(2, PdfColor.Black), Shadows = [new BoxShadow(5, 5, 0, 0, Red) { Inset = true }],
        });
        using var bitmap = Draw(box);

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(24, 14)); // The top-left band inside the border.
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(60, 16)); // From the padding edge (12) down 5 pt.
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(60, 17));
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(60, 25)); // The rest shows the background.
        Assert.Equal(new SKColor(0, 0, 0), bitmap.GetPixel(21, 14)); // The border stays on top.
        Assert.Equal(White, bitmap.GetPixel(30, 35)); // Nothing outside.
    }

    [Fact]
    public void A_blurred_inset_shadow_is_darkest_at_the_edges()
    {
        var box = new ContainerBlock(new BoxStyle
        {
            Width = 50, Height = 20, Margin = new Thickness(10, 0, 0, 20), Shadows = [new BoxShadow(0, 0, 8, 0, PdfColor.Black) { Inset = true }],
        });
        using var bitmap = Draw(box);

        Assert.True(bitmap.GetPixel(21, 20).Red < bitmap.GetPixel(30, 20).Red);
        Assert.True(bitmap.GetPixel(30, 20).Red < bitmap.GetPixel(45, 20).Red + 1);
        Assert.Equal(White, bitmap.GetPixel(15, 20));
    }

    [Fact]
    public void The_first_shadow_is_on_top_and_translucent_colors_show_what_is_below()
    {
        using var stacked = Render(new BoxShadow(5, 5, 0, 0, Red), new BoxShadow(5, 5, 0, 0, Blue));
        Assert.Equal(new SKColor(255, 0, 0), stacked.GetPixel(72, 32));

        using var translucent = Render(new BoxShadow(5, 5, 0, 0, PdfColor.Black.WithAlpha(0.5)));
        Assert.InRange(translucent.GetPixel(72, 32).Red, 120, 135);

        using var blurred = Render(new BoxShadow(0, 0, 10, 0, PdfColor.Black));
        using var lighter = Render(new BoxShadow(0, 0, 10, 0, PdfColor.Black.WithAlpha(0.5)));
        Assert.True(lighter.GetPixel(71, 20).Red > blurred.GetPixel(71, 20).Red + 30);
    }

    [Fact]
    public void An_inset_spread_narrows_the_hole_and_the_shadow_stays_inside_the_box()
    {
        var box = new ContainerBlock(new BoxStyle
        {
            Width = 50, Height = 20, Margin = new Thickness(10, 0, 0, 20), Shadows = [new BoxShadow(0, 0, 0, 4, Red) { Inset = true }],
        });
        using var bitmap = Draw(box);

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(23, 20));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(66, 20));
        Assert.Equal(White, bitmap.GetPixel(26, 20));
        Assert.Equal(White, bitmap.GetPixel(19, 20)); // Clipped to the box.
        Assert.Equal(White, bitmap.GetPixel(45, 9));
    }

    [Fact]
    public void A_shadow_of_nothing_draws_nothing()
    {
        using var bitmap = Render(new BoxShadow(0, 0, 0, -30, Red), new BoxShadow(5, 5, 4, 0, PdfColor.Black.WithAlpha(0)));

        Assert.All(Enumerable.Range(0, 100), x => Assert.Equal(White, bitmap.GetPixel(x, 32)));
    }

    private static SKBitmap Render(params BoxShadow[] shadows) => Render(shadows, 0);

    private static SKBitmap Render(BoxShadow shadow, double radius) => Render([shadow], radius);

    private static SKBitmap Render(BoxShadow[] shadows, double radius) =>
        Draw(new ContainerBlock(new BoxStyle
        {
            Width = 50, Height = 20, Margin = new Thickness(10, 0, 0, 20), Shadows = shadows,
            BorderRadius = radius > 0 ? CornerRadii.All(radius) : default,
        }));

    private static SKBitmap Draw(Block block) =>
        Conversion.ToImage(new FlowDocument(Small).Add(block).Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
}
