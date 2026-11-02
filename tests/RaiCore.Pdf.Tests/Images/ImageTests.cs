using System.Text;
using System.Text.RegularExpressions;
using PDFtoImage;
using RaiCore.Pdf.Images;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Images;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public partial class ImageTests
{
    private static readonly SKColor Red = new(255, 0, 0), Green = new(0, 200, 0), Blue = new(0, 0, 255), Yellow = new(255, 220, 0);

    public static TheoryData<string> ValidPngSuiteFiles() =>
        [.. Directory.GetFiles(Fixtures.Path("PngSuite"), "*.png").Select(f => Path.GetFileName(f)).Where(f => !f.StartsWith('x')).Order()];

    public static TheoryData<string> CorruptPngSuiteFiles() =>
        [.. Directory.GetFiles(Fixtures.Path("PngSuite"), "x*.png").Select(f => Path.GetFileName(f)).Order()];

    [Theory]
    [MemberData(nameof(ValidPngSuiteFiles))]
    public void Decodes_pngsuite_like_skia(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine("PngSuite", file)));
        var ours = ToRgba(PngDecoder.Decode(data));
        var skia = DecodeWithSkia(data);

        Assert.Equal(skia.Width, ours.Width);
        Assert.Equal(skia.Height, ours.Height);
        var tolerance = file.Contains("16", StringComparison.Ordinal) ? 1 : 0; // Skia rounds 16-bit samples; we take the high byte.
        for (var i = 0; i < ours.Pixels.Length; i++)
        {
            var (a, b) = (ours.Pixels[i], skia.Pixels[i]);
            Assert.True(Math.Abs(a.Alpha - b.Alpha) <= tolerance, $"{file}: alpha at pixel {i}: {a} vs {b}");
            if (b.Alpha != 0)
            {
                Assert.True(
                    Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance,
                    $"{file}: pixel {i} ({i % ours.Width}, {i / ours.Width}): ours {a}, Skia {b}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(CorruptPngSuiteFiles))]
    public void Rejects_corrupt_pngsuite_files(string file) =>
        Assert.Throws<ImageFormatException>(() => PdfImage.Load(Fixtures.Path(Path.Combine("PngSuite", file))));

    [Theory]
    [InlineData("rgb.jpg", "Rgb")]
    [InlineData("rgb-progressive.jpg", "Rgb")]
    [InlineData("gray.jpg", "Gray")]
    [InlineData("cmyk.jpg", "Cmyk")]
    public void Reads_jpeg_headers(string file, string colorSpace)
    {
        var bytes = File.ReadAllBytes(Fixtures.Path(Path.Combine("Images", file)));
        var image = PdfImage.Load(bytes);

        Assert.Equal(ImageFormat.Jpeg, image.Format);
        Assert.Equal((64, 48), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(colorSpace, image.Image.ColorSpace.ToString());
        Assert.Equal(colorSpace == "Cmyk", image.Image.InvertCmyk);
        Assert.Equal(bytes, image.Image.Data); // Embedded as-is, no re-encoding.
    }

    [Theory]
    [InlineData("rgb.jpg")]
    [InlineData("rgb-progressive.jpg")]
    [InlineData("cmyk.jpg")]
    [InlineData("orientation-2.jpg")]
    [InlineData("orientation-3.jpg")]
    [InlineData("orientation-4.jpg")]
    [InlineData("orientation-5.jpg")]
    [InlineData("orientation-6.jpg")]
    [InlineData("orientation-7.jpg")]
    [InlineData("orientation-8.jpg")]
    [InlineData("quadrants.gif")]
    [InlineData("quadrants-interlaced.gif")]
    [InlineData("animated.gif")]
    public void Renders_upright_with_the_right_colors(string file)
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine("Images", file)));
        Assert.Equal((64, 48), (image.PixelWidth, image.PixelHeight)); // Orientations 5-8 swap the stored axes back.

        using var bitmap = RenderAt72Dpi(image, out var left, out var top);

        // Sample the middle of each quadrant: red, green / blue, yellow.
        var (topLeft, topRight) = (bitmap.GetPixel(left + 16, top + 12), bitmap.GetPixel(left + 48, top + 12));
        var (bottomLeft, bottomRight) = (bitmap.GetPixel(left + 16, top + 36), bitmap.GetPixel(left + 48, top + 36));
        if (file == "cmyk.jpg")
        {
            // DeviceCMYK is converted to RGB with a print-like formula, so colors shift (pure green comes out
            // darker); check the dominant channels. Wrong inversion would make every quadrant dark instead.
            Assert.True(topLeft.Red > 180 && topLeft.Green < 100 && topLeft.Blue < 100, $"top-left {topLeft}");
            Assert.True(topRight.Green > 100 && topRight.Red < 80, $"top-right {topRight}");
            Assert.True(bottomLeft.Blue > 120 && bottomLeft.Red < 100, $"bottom-left {bottomLeft}");
            Assert.True(bottomRight.Red > 180 && bottomRight.Green > 150 && bottomRight.Blue < 100, $"bottom-right {bottomRight}");
            return;
        }

        AssertColor(Red, topLeft, file, "top-left");
        AssertColor(Green, topRight, file, "top-right");
        AssertColor(Blue, bottomLeft, file, "bottom-left");
        AssertColor(Yellow, bottomRight, file, "bottom-right");
    }

    [Fact]
    public void Transparent_gif_pixels_show_the_page_behind()
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine("Images", "transparent.gif")));
        Assert.True(image.HasTransparency);

        using var bitmap = RenderAt72Dpi(image, out var left, out var top);
        AssertColor(SKColors.White, bitmap.GetPixel(left + 16, top + 36), "transparent.gif", "transparent quadrant");
        AssertColor(Red, bitmap.GetPixel(left + 16, top + 12), "transparent.gif", "opaque quadrant");
    }

    [Theory]
    [InlineData("basn6a08.png")] // RGBA 8-bit
    [InlineData("basn4a16.png")] // Gray + alpha 16-bit
    [InlineData("tbrn2c08.png")] // RGB with a tRNS color key
    [InlineData("tbbn3p08.png")] // Palette with tRNS
    [InlineData("basi3p02.png")] // Interlaced 2-bit palette
    public void Png_transparency_composites_over_the_page(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine("PngSuite", file)));
        var image = PdfImage.Load(data);
        var expected = DecodeWithSkia(data);

        using var bitmap = RenderAt72Dpi(image, out var left, out var top, scale: 4);
        for (var y = 0; y < expected.Height; y += 3)
        {
            for (var x = 0; x < expected.Width; x += 3)
            {
                var source = expected.Pixels[(y * expected.Width) + x];
                var a = source.Alpha / 255.0;
                var composite = new SKColor(
                    (byte)Math.Round((source.Red * a) + (255 * (1 - a))),
                    (byte)Math.Round((source.Green * a) + (255 * (1 - a))),
                    (byte)Math.Round((source.Blue * a) + (255 * (1 - a))));
                AssertColor(composite, bitmap.GetPixel(left + (x * 4) + 2, top + (y * 4) + 2), file, $"({x}, {y})", tolerance: 12);
            }
        }
    }

    [Fact]
    public void Images_are_stored_once_and_masks_are_written()
    {
        var logo = PdfImage.Load(Fixtures.Path(Path.Combine("PngSuite", "basn6a08.png")));
        var keyed = PdfImage.Load(Fixtures.Path(Path.Combine("PngSuite", "tbrn2c08.png")));
        var photo = PdfImage.Load(Fixtures.Path(Path.Combine("Images", "rgb.jpg")));
        var doc = new PdfDocument();
        for (var p = 0; p < 3; p++)
        {
            doc.AddPage().Canvas.DrawImage(logo, 10, 10, 32, 32).DrawImage(logo, 50, 10, 64, 64).DrawImage(photo, 10, 100, 64, 48).DrawImage(keyed, 200, 10, 32, 32);
        }

        var pdf = doc.ToArray();
        var text = Encoding.Latin1.GetString(pdf);

        Assert.Equal(4, ImageObjectRegex().Matches(text).Count); // logo + its soft mask + photo + keyed
        Assert.Contains("/SMask", text, StringComparison.Ordinal);
        Assert.Matches(@"/Mask \[\d+ \d+ \d+ \d+ \d+ \d+\]", text);
        Assert.Contains("/DCTDecode", text, StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(pdf);
    }

    [Theory]
    [InlineData("PngSuite/basn2c08.png")]
    [InlineData("PngSuite/basi0g16.png")]
    [InlineData("Images/quadrants-interlaced.gif")]
    [InlineData("Images/rgb.jpg")]
    public void Truncated_images_fail_with_an_image_format_error(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(file));
        for (var length = 0; length < data.Length; length += Math.Max(1, data.Length / 150))
        {
            var ex = Record.Exception(() => PdfImage.Load(data[..length]));
            Assert.True(ex is null or ImageFormatException, $"{file} cut at {length}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Fact]
    public void Unknown_formats_are_rejected() =>
        Assert.Throws<ImageFormatException>(() => PdfImage.Load("BM not an image we read"u8.ToArray()));

    /// <summary>Draws the image at (100, 100) from the page's top-left, one point per pixel times <paramref name="scale"/>.</summary>
    private static SKBitmap RenderAt72Dpi(PdfImage image, out int left, out int top, int scale = 1)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(new PdfPageSize(400, 400));
        left = 100;
        top = 100;
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        page.Canvas.DrawImage(image, left, page.Height - top - height, width, height);
        return Conversion.ToImage(doc.ToArray(), page: 0, options: new RenderOptions(Dpi: 72));
    }

    private static void AssertColor(SKColor expected, SKColor actual, string file, string where, int tolerance = 40) =>
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"{file} {where}: expected {expected}, got {actual}");

    private static (int Width, int Height, SKColor[] Pixels) DecodeWithSkia(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        Assert.True(result is SKCodecResult.Success or SKCodecResult.IncompleteInput, $"Skia: {result}");
        return (info.Width, info.Height, bitmap.Pixels);
    }

    /// <summary>Converts our decoded samples to 8-bit RGBA the way a viewer would display them.</summary>
    private static (int Width, int Height, SKColor[] Pixels) ToRgba(DecodedImage image)
    {
        var components = image.Components;
        var bpc = image.BitsPerComponent;
        var rowBytes = ((image.Width * components * bpc) + 7) / 8;
        var max = (1 << bpc) - 1;
        int Raw(int x, int y, int c)
        {
            var sample = (x * components) + c;
            return bpc switch
            {
                8 => image.Data[(y * rowBytes) + sample],
                16 => (image.Data[(y * rowBytes) + (sample * 2)] << 8) | image.Data[(y * rowBytes) + (sample * 2) + 1],
                _ => (image.Data[(y * rowBytes) + (sample * bpc / 8)] >> (8 - ((sample * bpc) % 8) - bpc)) & max,
            };
        }

        byte Scale(int raw) => bpc == 16 ? (byte)(raw >> 8) : (byte)(raw * 255 / max);

        var pixels = new SKColor[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                byte r, g, b;
                if (image.ColorSpace == ImageColorSpace.Indexed)
                {
                    var index = Raw(x, y, 0);
                    (r, g, b) = (image.Palette![index * 3], image.Palette[(index * 3) + 1], image.Palette[(index * 3) + 2]);
                }
                else if (components == 1)
                {
                    r = g = b = Scale(Raw(x, y, 0));
                }
                else
                {
                    (r, g, b) = (Scale(Raw(x, y, 0)), Scale(Raw(x, y, 1)), Scale(Raw(x, y, 2)));
                }

                var alpha = image.Alpha?[(y * image.Width) + x] ?? 255;
                if (image.ColorKey is { } key && Enumerable.Range(0, components).All(c => Raw(x, y, c) == key[c * 2]))
                {
                    alpha = 0;
                }

                pixels[(y * image.Width) + x] = new SKColor(r, g, b, alpha);
            }
        }

        return (image.Width, image.Height, pixels);
    }

    [GeneratedRegex(@"/Subtype /Image")]
    private static partial Regex ImageObjectRegex();
}
