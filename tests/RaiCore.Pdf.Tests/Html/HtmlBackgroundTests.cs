using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>CSS background images and gradients: the shorthand, the longhand lists and the gradient functions.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlBackgroundTests
{
    private const double Px = 0.75;

    // A 2 x 2 pixel red PNG.
    private const string RedPng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGP4z8AARAwQCgAf7gP9i18U1AAAAABJRU5ErkJggg==";

    [Fact]
    public void A_shorthand_with_several_layers_gives_one_layer_per_image()
    {
        var layers = Layers($"background: url({RedPng}) no-repeat right 10px top 20% / 50% auto content-box padding-box, linear-gradient(red, blue) #eee");

        Assert.Equal(2, layers.Count);
        var image = layers[0];
        Assert.IsType<ImageBackground>(image.Image);
        Assert.Equal((BackgroundRepeat.NoRepeat, BackgroundRepeat.NoRepeat), (image.RepeatX, image.RepeatY));
        Assert.Equal(new BackgroundPosition(Inset.Points(10 * Px), FromEnd: true), image.PositionX);
        Assert.Equal(new BackgroundPosition(Inset.Percent(20)), image.PositionY);
        Assert.Equal(new BackgroundSize(BackgroundSizeKind.Explicit, Inset.Percent(50)), image.Size);
        Assert.Equal((BackgroundClip.ContentBox, BackgroundClip.PaddingBox), (image.Origin, image.Clip));
        Assert.IsType<LinearGradient>(layers[1].Image);
        Assert.Equal((BackgroundClip.PaddingBox, BackgroundClip.BorderBox), (layers[1].Origin, layers[1].Clip)); // The initial values.
    }

    [Fact]
    public void The_shorthand_keeps_its_color_and_a_color_alone_has_no_layers()
    {
        var box = Box("background: linear-gradient(red, blue) #abcdef");
        var plain = Box("background: #eee");

        Assert.Equal(PdfColor.FromRgb(0xAB, 0xCD, 0xEF), box.Background);
        Assert.Single(box.BackgroundLayers);
        Assert.Equal(PdfColor.FromRgb(0xEE, 0xEE, 0xEE), plain.Background);
        Assert.Empty(plain.BackgroundLayers);
    }

    [Fact]
    public void The_shorthand_resets_earlier_longhands()
    {
        var box = Box("background-repeat: no-repeat; background: linear-gradient(red, blue)");

        Assert.Equal(BackgroundRepeat.Repeat, Assert.Single(box.BackgroundLayers).RepeatX);
    }

    [Fact]
    public void Important_carries_to_every_longhand()
    {
        var box = Box("background-repeat: repeat", "background: linear-gradient(red, blue) no-repeat !important");

        Assert.Equal(BackgroundRepeat.NoRepeat, Assert.Single(box.BackgroundLayers).RepeatX);
    }

    [Fact]
    public void Shorter_lists_repeat_to_match_the_images()
    {
        var layers = Layers("background-image: linear-gradient(red, blue), linear-gradient(red, blue), linear-gradient(red, blue); background-repeat: no-repeat, repeat-x");

        Assert.Equal([BackgroundRepeat.NoRepeat, BackgroundRepeat.Repeat, BackgroundRepeat.NoRepeat], layers.Select(l => l.RepeatX));
        Assert.Equal([BackgroundRepeat.NoRepeat, BackgroundRepeat.NoRepeat, BackgroundRepeat.NoRepeat], layers.Select(l => l.RepeatY));
    }

    [Theory]
    [InlineData("center", 50.0, 50.0)]
    [InlineData("top", 50.0, 0.0)]
    [InlineData("right bottom", 0.0, 0.0)] // From the end.
    [InlineData("25% 75%", 25.0, 75.0)]
    [InlineData("bottom right", 0.0, 0.0)] // Keywords in either order.
    [InlineData("top left", 0.0, 0.0)]
    [InlineData("top center", 50.0, 0.0)]
    public void Positions_are_read(string position, double x, double y)
    {
        var layer = Assert.Single(Layers($"background-image: linear-gradient(red, blue); background-position: {position}"));

        Assert.Equal((x, y), (layer.PositionX.Offset.Value, layer.PositionY.Offset.Value));
        Assert.Equal(position is "right bottom" or "bottom right", layer.PositionX.FromEnd);
        Assert.Equal(position is "right bottom" or "bottom right", layer.PositionY.FromEnd);
    }

    [Theory]
    [InlineData("cover", BackgroundSizeKind.Cover)]
    [InlineData("contain", BackgroundSizeKind.Contain)]
    [InlineData("auto", BackgroundSizeKind.Auto)]
    [InlineData("10px 20px", BackgroundSizeKind.Explicit)]
    public void Sizes_are_read(string size, BackgroundSizeKind kind) =>
        Assert.Equal(kind, Assert.Single(Layers($"background-image: linear-gradient(red, blue); background-size: {size}")).Size.Kind);

    [Theory]
    [InlineData("linear-gradient(red, blue)", 180.0)]
    [InlineData("linear-gradient(to right, red, blue)", 90.0)]
    [InlineData("linear-gradient(0.25turn, red, blue)", 90.0)]
    [InlineData("linear-gradient(-45deg, red, blue)", -45.0)]
    public void Linear_gradient_angles_are_read(string value, double angle)
    {
        var gradient = Assert.IsType<LinearGradient>(Assert.Single(Layers($"background-image: {value}")).Image);

        Assert.Null(gradient.Corner);
        Assert.Equal(angle, gradient.Angle, 6);
    }

    [Theory]
    [InlineData("to top right", 1, -1)]
    [InlineData("to bottom left", -1, 1)]
    [InlineData("to left top", -1, -1)]
    public void A_gradient_toward_a_corner_keeps_the_corner(string direction, int x, int y)
    {
        var gradient = Assert.IsType<LinearGradient>(Assert.Single(Layers($"background: linear-gradient({direction}, red, blue)")).Image);

        Assert.Equal((x, y), gradient.Corner);
    }

    [Fact]
    public void Stops_with_two_positions_become_two_stops_and_hints_are_skipped()
    {
        var gradient = Assert.IsType<LinearGradient>(Assert.Single(Layers("background-image: linear-gradient(red 0 20%, 40%, blue 80%)")).Image);

        Assert.Equal([Inset.Points(0), Inset.Percent(20), Inset.Percent(80)], gradient.Stops.Select(s => s.Position!.Value));
    }

    [Fact]
    public void Radial_gradients_are_read()
    {
        var gradient = Assert.IsType<RadialGradient>(Assert.Single(Layers("background-image: repeating-radial-gradient(circle closest-side at 30% 40%, red, blue 20px)")).Image);

        Assert.Equal((RadialShape.Circle, RadialExtent.ClosestSide, true), (gradient.Shape, gradient.Extent, gradient.Repeating));
        Assert.Equal((30.0, 40.0), (gradient.CenterX.Offset.Value, gradient.CenterY.Offset.Value));
        Assert.Equal(2, gradient.Stops.Count);
    }

    [Fact]
    public void Images_that_do_not_load_leave_no_layer()
    {
        var layers = Layers("background-image: url(missing.png), linear-gradient(red, blue)");

        Assert.IsType<LinearGradient>(Assert.Single(layers).Image);
    }

    [Fact]
    public void Urls_in_a_linked_stylesheet_are_relative_to_it()
    {
        var requested = new List<Uri>();
        var options = new HtmlConverterOptions
        {
            BaseUri = new Uri("https://example.com/site/"),
            ResourceLoader = uri =>
            {
                requested.Add(uri);
                return uri.AbsolutePath.EndsWith(".css", StringComparison.Ordinal) ? "div { background: url(../img/bg.png) }"u8.ToArray() : null;
            },
        };
        HtmlToPdf.CreateFlowDocument("<link rel=\"stylesheet\" href=\"css/site.css\"><div>x</div>", options);

        Assert.Contains(new Uri("https://example.com/site/img/bg.png"), requested);
    }

    [Fact]
    public void A_url_with_a_slash_and_a_data_uri_survive_the_shorthand()
    {
        var css = CssBackgrounds.RewriteShorthand("div{background:url(img/a.png) center/cover,url(\"data:image/png;base64,AA==\") no-repeat}");

        Assert.Contains("background-image: url(img/a.png), url(\"data:image/png;base64,AA==\")", css, StringComparison.Ordinal);
        Assert.Contains("background-size: cover, auto", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gradient_background_is_drawn()
    {
        var pdf = HtmlToPdf.Convert("<body style=\"margin: 0\"><div style=\"height: 100px; background: linear-gradient(to right, red 50%, blue 50%)\"></div></body>").ToArray();
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 80));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(500, 80));
    }

    private static BoxStyle Box(string css, string? sheet = null)
    {
        var html = (sheet is null ? string.Empty : $"<style>#b {{ {sheet} }}</style>") + $"<div id=\"b\" style=\"{css}\">x</div>";
        return Flatten(HtmlToPdf.CreateFlowDocument(html).Blocks).Single(b => b.Id == "b").Style;
    }

    private static IReadOnlyList<BackgroundLayer> Layers(string css) => Box(css).BackgroundLayers;

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
