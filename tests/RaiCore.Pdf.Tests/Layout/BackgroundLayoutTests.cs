using System.IO.Compression;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Background images and gradients. Uses a 200 x 200 pt page with 20 pt margins, rendered at 72 dpi (1 pt per pixel);
/// boxes are 160 pt wide and start at (20, 20).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class BackgroundLayoutTests
{
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PdfColor Red = PdfColor.FromRgb(255, 0, 0);
    private static readonly PdfColor Blue = PdfColor.FromRgb(0, 0, 255);
    private static readonly PdfColor Green = PdfColor.FromRgb(0, 255, 0);

    [Fact]
    public void A_linear_gradient_goes_from_its_first_to_its_last_color()
    {
        using var bitmap = Render(Box(Layer(new LinearGradient(90, [new GradientStop(Red), new GradientStop(Blue)]))));

        Assert.True(IsNear(bitmap.GetPixel(21, 50), Red), $"{bitmap.GetPixel(21, 50)}");
        Assert.True(IsNear(bitmap.GetPixel(178, 50), Blue), $"{bitmap.GetPixel(178, 50)}");
        var middle = bitmap.GetPixel(100, 50);
        Assert.InRange(middle.Red, 100, 155);
        Assert.InRange(middle.Blue, 100, 155);
    }

    [Fact]
    public void Hard_stops_make_sharp_edges_and_the_last_color_continues()
    {
        var gradient = new LinearGradient(90, [new GradientStop(Red, Inset.Percent(50)), new GradientStop(Blue, Inset.Percent(50))]);
        using var bitmap = Render(Box(Layer(gradient)));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(98, 50));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(102, 50));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(178, 50));
    }

    [Fact]
    public void A_hard_stop_at_the_end_keeps_its_color()
    {
        var gradient = new LinearGradient(90, [new GradientStop(Red), new GradientStop(Green, Inset.Percent(80)), new GradientStop(Blue, Inset.Percent(80))]);
        using var bitmap = Render(Box(Layer(gradient)));

        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(170, 50));
    }

    [Fact]
    public void Translucent_stops_let_the_background_color_show_through()
    {
        var gradient = new LinearGradient(90, [new GradientStop(Red), new GradientStop(PdfColor.FromRgba(255, 0, 0, 0))]);
        using var bitmap = Render(Box(Layer(gradient), background: Blue));

        Assert.True(IsNear(bitmap.GetPixel(21, 50), Red), $"{bitmap.GetPixel(21, 50)}");
        Assert.True(IsNear(bitmap.GetPixel(178, 50), Blue), $"{bitmap.GetPixel(178, 50)}");
    }

    [Fact]
    public void A_corner_gradient_gives_the_other_two_corners_the_middle_color()
    {
        // Toward the top right of a 160 x 60 box: the top-left and bottom-right corners are on the 50% line.
        using var bitmap = Render(Box(Layer(new LinearGradient(right: true, bottom: false, [new GradientStop(Red), new GradientStop(Blue)]))));

        var topLeft = bitmap.GetPixel(22, 22);
        var bottomRight = bitmap.GetPixel(177, 77);
        Assert.InRange(Math.Abs(topLeft.Red - bottomRight.Red), 0, 12);
        Assert.InRange(topLeft.Red, 100, 155);
    }

    [Fact]
    public void A_radial_gradient_spreads_from_its_center()
    {
        var gradient = new RadialGradient([new GradientStop(Red), new GradientStop(Blue)]) { Shape = RadialShape.Circle, Extent = RadialExtent.ClosestSide };
        using var bitmap = Render(Box(Layer(gradient)));

        Assert.True(IsNear(bitmap.GetPixel(100, 50), Red), $"{bitmap.GetPixel(100, 50)}"); // The center.
        Assert.True(IsNear(bitmap.GetPixel(25, 50), Blue), $"{bitmap.GetPixel(25, 50)}"); // Beyond the 30 pt radius.
        Assert.True(IsNear(bitmap.GetPixel(100, 22), Blue), $"{bitmap.GetPixel(100, 22)}");
    }

    [Fact]
    public void A_radial_ellipse_has_a_radius_per_axis()
    {
        var gradient = new RadialGradient([new GradientStop(Red), new GradientStop(Blue)]) { Extent = RadialExtent.ClosestSide };
        using var bitmap = Render(Box(Layer(gradient)));

        // Closest-side ellipse of a 160 x 60 box: radii 80 and 30; 40 pt right of the center is halfway out.
        var halfway = bitmap.GetPixel(140, 50);
        Assert.InRange(halfway.Red, 100, 155);
        Assert.True(IsNear(bitmap.GetPixel(100, 21), Blue), $"{bitmap.GetPixel(100, 21)}");
    }

    [Fact]
    public void A_repeating_gradient_repeats_its_stops()
    {
        var gradient = new LinearGradient(
            90,
            [new GradientStop(Red, Inset.Points(0)), new GradientStop(Red, Inset.Points(10)), new GradientStop(Blue, Inset.Points(10)), new GradientStop(Blue, Inset.Points(20))],
            repeating: true);
        using var bitmap = Render(Box(Layer(gradient)));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(25, 50));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(35, 50));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(145, 50));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(155, 50));
    }

    [Fact]
    public void An_image_is_placed_by_position_and_size_without_repeating()
    {
        var layer = Layer(new ImageBackground(Solid(255, 0, 0))) with
        {
            RepeatX = BackgroundRepeat.NoRepeat,
            RepeatY = BackgroundRepeat.NoRepeat,
            PositionX = new BackgroundPosition(Inset.Points(10), FromEnd: true),
            PositionY = new BackgroundPosition(Inset.Percent(100)),
            Size = new BackgroundSize(BackgroundSizeKind.Explicit, Inset.Points(20)),
        };
        using var bitmap = Render(Box(layer, background: Blue));

        // 20 x 20, its right edge 10 from the box's right edge (180), at the bottom (80): x 150-170, y 60-80.
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(160, 70));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(145, 70));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(175, 70));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(160, 55));
    }

    [Fact]
    public void A_repeated_image_tiles_the_box_from_its_position()
    {
        var layer = Layer(new ImageBackground(Quadrants())) with { Size = new BackgroundSize(BackgroundSizeKind.Explicit, Inset.Points(20), Inset.Points(20)) };
        using var bitmap = Render(Box(layer));

        // Each 20 pt tile has red top-left and bottom-right quadrants; tiles start at (20, 20).
        foreach (var (x, y) in new[] { (25, 25), (45, 45), (165, 65), (145, 25) })
        {
            Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(x, y));
        }

        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel(35, 25));
    }

    [Theory]
    [InlineData(BackgroundSizeKind.Cover, true)]
    [InlineData(BackgroundSizeKind.Contain, false)]
    public void Cover_fills_the_box_and_contain_fits_inside_it(BackgroundSizeKind kind, bool fills)
    {
        var layer = Layer(new ImageBackground(Solid(255, 0, 0))) with
        {
            Size = new BackgroundSize(kind),
            RepeatX = BackgroundRepeat.NoRepeat,
            RepeatY = BackgroundRepeat.NoRepeat,
        };
        using var bitmap = Render(Box(layer, background: Blue));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(30, 50)); // A square image: 60 x 60 contained, 160 x 160 covering.
        Assert.Equal(fills ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255), bitmap.GetPixel(170, 50));
    }

    [Fact]
    public void The_clip_box_limits_the_painting_and_the_origin_box_positions_it()
    {
        var layer = Layer(new ImageBackground(Solid(255, 0, 0))) with
        {
            RepeatX = BackgroundRepeat.NoRepeat,
            RepeatY = BackgroundRepeat.NoRepeat,
            Size = new BackgroundSize(BackgroundSizeKind.Explicit, Inset.Points(10), Inset.Points(10)),
            Origin = BackgroundClip.ContentBox,
            Clip = BackgroundClip.PaddingBox,
        };
        var style = new BoxStyle
        {
            Border = Borders.All(5, PdfColor.FromRgb(0, 255, 0)),
            Padding = Thickness.All(10),
            BackgroundLayers = [layer],
            Height = 60,
        };
        using var bitmap = Render(new ContainerBlock(style));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(40, 40)); // At the content box: x 35-45, y 35-45.
        Assert.NotEqual(new SKColor(255, 0, 0), bitmap.GetPixel(32, 32));
    }

    [Fact]
    public void The_first_layer_is_painted_on_top()
    {
        var top = Layer(new LinearGradient(0, [new GradientStop(Red), new GradientStop(Red)]));
        var bottom = Layer(new LinearGradient(0, [new GradientStop(Blue), new GradientStop(Blue)]));
        using var bitmap = Render(new ContainerBlock(new BoxStyle { BackgroundLayers = [top, bottom], Height = 60 }));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(100, 50));
    }

    [Fact]
    public void A_box_split_across_pages_continues_its_gradient()
    {
        // A 300 pt tall box from y 20: 160 on page 1 and 140 on page 2; the gradient runs top to bottom over all 300.
        var style = new BoxStyle { BackgroundLayers = [Layer(new LinearGradient(180, [new GradientStop(Red), new GradientStop(Blue)]))] };
        var box = new ContainerBlock(style, new ParagraphBlock(new Paragraph(new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 8) { LineHeight = LineHeight.Points(10) })
            .Add(string.Join("\n", Enumerable.Range(1, 30).Select(i => $"L{i}")))));
        var pdf = new FlowDocument(Small).Add(box).Render().ToArray();
        using var first = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        using var second = Conversion.ToImage(pdf, page: 1, options: new RenderOptions(Dpi: 72));

        // The bottom of page 1 is 160/300 down the gradient; the top of page 2 continues from there.
        var end = first.GetPixel(150, 178);
        var start = second.GetPixel(150, 22);
        Assert.InRange(end.Blue, 125, 155);
        Assert.InRange(Math.Abs(end.Blue - start.Blue), 0, 8);
        Assert.True(second.GetPixel(150, 155).Blue > 230, $"{second.GetPixel(150, 155)}");
    }

    [Theory]
    [InlineData(BackgroundRepeat.Space, false)] // 6 tiles of 23 in 160: the 22 left over makes 4.4 pt gaps.
    [InlineData(BackgroundRepeat.Round, true)] // 160 / 23 rounds to 7 tiles of 22.86: no gaps.
    public void Space_and_round_fit_whole_tiles(BackgroundRepeat repeat, bool covered)
    {
        var layer = Layer(new ImageBackground(Solid(255, 0, 0))) with
        {
            RepeatX = repeat,
            RepeatY = BackgroundRepeat.NoRepeat,
            Size = new BackgroundSize(BackgroundSizeKind.Explicit, Inset.Points(23), Inset.Points(10)),
        };
        using var bitmap = Render(Box(layer, background: Blue));

        // Both start with a tile at the left edge and end with one at the right edge.
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(21, 25));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(178, 25));
        Assert.Equal(covered ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255), bitmap.GetPixel(45, 25)); // After the first tile.
    }

    [Fact]
    public void A_vertical_gradient_runs_over_the_boxs_height()
    {
        using var bitmap = Render(Box(Layer(new LinearGradient(180, [new GradientStop(Red), new GradientStop(Blue)]))));

        Assert.True(IsNear(bitmap.GetPixel(100, 78), Blue), $"{bitmap.GetPixel(100, 78)}"); // The bottom of the 60 pt box.
        Assert.True(IsNear(bitmap.GetPixel(100, 21), Red), $"{bitmap.GetPixel(100, 21)}");
    }

    [Fact]
    public void Stops_without_positions_are_spaced_evenly()
    {
        using var bitmap = Render(Box(Layer(new LinearGradient(90, [new GradientStop(Red), new GradientStop(Green), new GradientStop(Blue)]))));

        Assert.True(IsNear(bitmap.GetPixel(100, 50), Green), $"{bitmap.GetPixel(100, 50)}");
    }

    [Fact]
    public void A_stop_before_the_previous_one_moves_up_to_it()
    {
        var gradient = new LinearGradient(90, [new GradientStop(Red, Inset.Percent(50)), new GradientStop(Blue, Inset.Percent(20))]);
        using var bitmap = Render(Box(Layer(gradient)));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(95, 50));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(105, 50));
    }

    [Fact]
    public void Each_layer_is_painted_in_its_own_clip_box()
    {
        var layer = Layer(new ImageBackground(Solid(255, 0, 0))) with { Clip = BackgroundClip.PaddingBox };
        var style = new BoxStyle { Border = Borders.All(5, PdfColor.FromRgba(0, 255, 0, 0)), BackgroundLayers = [layer], Height = 60 };
        using var bitmap = Render(new ContainerBlock(style));

        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(30, 30));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(22, 50)); // Under the (transparent) border: not painted.
    }

    [Fact]
    public void Round_scales_the_tiles_to_fit_a_whole_number()
    {
        // 160 / 30 rounds to 5 tiles of 32: the first tile's right half (white at the top) reaches x = 52.
        var layer = Layer(new ImageBackground(Quadrants())) with
        {
            RepeatX = BackgroundRepeat.Round,
            RepeatY = BackgroundRepeat.NoRepeat,
            Size = new BackgroundSize(BackgroundSizeKind.Explicit, Inset.Points(30), Inset.Points(30)),
        };
        using var bitmap = Render(Box(layer));

        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(51, 25)); // With 30 pt tiles it would be the next tile's red.
    }

    [Fact]
    public void A_gradients_color_function_has_increasing_bounds()
    {
        var gradient = new LinearGradient(90, [new GradientStop(Red, Inset.Percent(50)), new GradientStop(Blue, Inset.Percent(50)), new GradientStop(Green)]);
        var document = new FlowDocument(Small).Add(Box(Layer(gradient))).Render();
        document.CompressContent = false;
        var text = System.Text.Encoding.Latin1.GetString(document.ToArray());

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"/Bounds \[([^\]]*)\]"))
        {
            var bounds = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Assert.True(bounds.Zip(bounds.Skip(1)).All(pair => pair.First < pair.Second), match.Value);
        }
    }

    private static BackgroundLayer Layer(BackgroundImage image) => new(image);

    private static ContainerBlock Box(BackgroundLayer layer, PdfColor? background = null) =>
        new(new BoxStyle { BackgroundLayers = [layer], Background = background, Height = 60 });

    private static SKBitmap Render(Block block) =>
        Conversion.ToImage(new FlowDocument(Small).Add(block).Render().ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

    private static bool IsNear(SKColor actual, PdfColor expected) =>
        Math.Abs(actual.Red - (expected.Red * 255)) < 30 && Math.Abs(actual.Green - (expected.Green * 255)) < 30 && Math.Abs(actual.Blue - (expected.Blue * 255)) < 30;

    /// <summary>A 4 x 4 pixel PNG of one color.</summary>
    private static PdfImage Solid(byte r, byte g, byte b) => PdfImage.Load(Png(4, 4, (_, _) => (r, g, b)));

    /// <summary>A 4 x 4 pixel PNG: red top-left and bottom-right quadrants, white elsewhere.</summary>
    private static PdfImage Quadrants() => PdfImage.Load(Png(4, 4, (x, y) => (x < 2) == (y < 2) ? ((byte)255, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255)));

    private static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                raw.Write([r, g, b]);
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        static void Chunk(Stream stream, string type, byte[] data)
        {
            var typed = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
            data.CopyTo(typed, 4);
            stream.Write(BigEndian(data.Length));
            stream.Write(typed);
            stream.Write(BigEndian((int)Crc32(typed)));
        }

        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return ~crc;
        }
    }
}
