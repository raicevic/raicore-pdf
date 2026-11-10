using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Images;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>
/// BMP and WebP decoding. Each fixture comes with a PNG of the pixels it must decode to (tools/generate_webp_bmp_fixtures.py):
/// for WebP that is libwebp's own output, so lossy decoding is checked bit for bit against the reference decoder.
/// </summary>
public partial class ImageTests
{
    public static TheoryData<string> BmpFixtures() => FixtureNames("Bmp", "*.bmp");

    public static TheoryData<string> WebPFixtures() => FixtureNames("WebP", "*.webp");

    [Theory]
    [MemberData(nameof(BmpFixtures))]
    public void Decodes_bmp_variants_exactly(string file) => AssertDecodesToReference("Bmp", file, ImageFormat.Bmp);

    [Theory]
    [MemberData(nameof(WebPFixtures))]
    public void Decodes_webp_like_libwebp(string file) => AssertDecodesToReference("WebP", file, ImageFormat.WebP);

    [Theory]
    [InlineData("Bmp/1bit.bmp", "Indexed", 1)]
    [InlineData("Bmp/4bit.bmp", "Indexed", 4)]
    [InlineData("Bmp/core-8bit.bmp", "Indexed", 8)]
    [InlineData("Bmp/24bit.bmp", "Rgb", 8)]
    [InlineData("Bmp/16bit-565.bmp", "Rgb", 8)]
    public void Bmp_palettes_stay_indexed(string file, string colorSpace, int bitsPerComponent)
    {
        var image = PdfImage.Load(Fixtures.Path(file)).Image;
        Assert.Equal((colorSpace, bitsPerComponent), (image.ColorSpace.ToString(), image.BitsPerComponent));
    }

    [Theory]
    [InlineData("Bmp/24bit.bmp", false)]
    [InlineData("Bmp/32bit-rgb.bmp", false)] // All alpha bytes zero: opaque, as browsers show it.
    [InlineData("Bmp/32bit-rgb-alpha.bmp", true)]
    [InlineData("Bmp/32bit-v5-alpha.bmp", true)]
    [InlineData("Bmp/16bit-4444-v3.bmp", true)]
    [InlineData("Bmp/rle8.bmp", true)] // Pixels skipped by a delta escape are transparent.
    [InlineData("WebP/lossy.webp", false)]
    [InlineData("WebP/lossless.webp", false)]
    [InlineData("WebP/lossy-alpha.webp", true)]
    [InlineData("WebP/lossless-alpha.webp", true)]
    [InlineData("WebP/animated.webp", true)] // The first frame does not cover the canvas.
    public void Reports_transparency(string file, bool transparent) =>
        Assert.Equal(transparent, PdfImage.Load(Fixtures.Path(file)).HasTransparency);

    [Theory]
    [InlineData("WebP/lossy-alpha.webp")]
    [InlineData("WebP/lossless-alpha.webp")]
    [InlineData("WebP/alpha-lossless-gradient.webp")]
    [InlineData("Bmp/32bit-v4-alpha-topdown.bmp")]
    [InlineData("Bmp/rle4.bmp")]
    public void Alpha_composites_over_a_colored_background(string file)
    {
        var image = PdfImage.Load(Fixtures.Path(file));
        var expected = ToRgba(PngDecoder.Decode(File.ReadAllBytes(Fixtures.Path(Path.ChangeExtension(file, ".png")))));
        const int scale = 3;
        var background = new SKColor(40, 160, 220);
        var doc = new PdfDocument();
        var page = doc.AddPage(new PdfPageSize(400, 400));
        var (width, height) = (image.PixelWidth * scale, image.PixelHeight * scale);
        page.Canvas.SetFillColor(PdfColor.FromRgb(40, 160, 220)).Rectangle(50, 50, 300, 300).Fill()
            .DrawImage(image, 100, page.Height - 100 - height, width, height);
        using var bitmap = Conversion.ToImage(doc.ToArray(), page: 0, options: new RenderOptions(Dpi: 72));

        for (var y = 0; y < expected.Height; y += 2)
        {
            for (var x = 0; x < expected.Width; x += 2)
            {
                var source = expected.Pixels[(y * expected.Width) + x];
                var a = source.Alpha / 255.0;
                var composite = new SKColor(
                    (byte)Math.Round((source.Red * a) + (background.Red * (1 - a))),
                    (byte)Math.Round((source.Green * a) + (background.Green * (1 - a))),
                    (byte)Math.Round((source.Blue * a) + (background.Blue * (1 - a))));
                AssertColor(composite, bitmap.GetPixel(100 + (x * scale) + 1, 100 + (y * scale) + 1), file, $"({x}, {y})", tolerance: 12);
            }
        }
    }

    [Fact]
    public void Html_img_shows_bmp_and_webp_from_data_uris()
    {
        static string DataUri(string mime, string file) => $"data:{mime};base64," + Convert.ToBase64String(File.ReadAllBytes(Fixtures.Path(file)));
        var html = $$"""
            <style>@page { margin: 0 } body { margin: 0 }</style>
            <body>
            <div style="background: #2896dc; padding: 20px">
              <img src="{{DataUri("image/webp", "WebP/lossy-alpha.webp")}}" style="display: block; width: 300px">
              <img src="{{DataUri("image/bmp", "Bmp/32bit-v5-alpha.bmp")}}" style="display: block; width: 260px">
            </div>
            </body>
            """;
        using var bitmap = Conversion.ToImage(HtmlToPdf.ConvertToBytes(html), page: 0, options: new RenderOptions(Dpi: 96));
        var background = new SKColor(0x28, 0x96, 0xDC);

        // The WebP (75 x 53) at 4x, the BMP (26 x 18) at 10x below it.
        foreach (var (file, scale, top) in new[] { ("WebP/lossy-alpha.png", 4, 20), ("Bmp/32bit-v5-alpha.png", 10, 20 + (53 * 4)) })
        {
            var expected = ToRgba(PngDecoder.Decode(File.ReadAllBytes(Fixtures.Path(file))));
            for (var y = 1; y < expected.Height; y += 4)
            {
                for (var x = 1; x < expected.Width; x += 4)
                {
                    var source = expected.Pixels[(y * expected.Width) + x];
                    var a = source.Alpha / 255.0;
                    var composite = new SKColor(
                        (byte)Math.Round((source.Red * a) + (background.Red * (1 - a))),
                        (byte)Math.Round((source.Green * a) + (background.Green * (1 - a))),
                        (byte)Math.Round((source.Blue * a) + (background.Blue * (1 - a))));
                    AssertColor(composite, bitmap.GetPixel(20 + (x * scale) + (scale / 2), top + (y * scale) + (scale / 2)), file, $"({x}, {y})", tolerance: 16);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(BmpFixtures))]
    [MemberData(nameof(WebPFixtures))]
    public void Truncated_bmp_and_webp_fail_with_an_image_format_error(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(file.EndsWith(".bmp", StringComparison.Ordinal) ? "Bmp" : "WebP", file)));
        for (var length = 0; length < data.Length; length += Math.Max(1, data.Length / 120))
        {
            var ex = Record.Exception(() => PdfImage.Load(data[..length]));
            Assert.True(ex is null or ImageFormatException, $"{file} cut at {length}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(BmpFixtures))]
    [MemberData(nameof(WebPFixtures))]
    public void Corrupt_bmp_and_webp_fail_with_an_image_format_error(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(file.EndsWith(".bmp", StringComparison.Ordinal) ? "Bmp" : "WebP", file)));

        // Keep the image dimensions intact (a corrupt size only makes a test slow); damage everything else.
        var protectedBytes = new bool[data.Length];
        foreach (var (start, count) in SizeFields(data))
        {
            protectedBytes.AsSpan(start, Math.Min(count, data.Length - start)).Fill(true);
        }

        var random = new Random(file.Length);
        for (var trial = 0; trial < 300; trial++)
        {
            var corrupt = (byte[])data.Clone();
            for (var n = 0; n < 1 + (trial % 4); n++)
            {
                var at = random.Next(corrupt.Length);
                if (!protectedBytes[at])
                {
                    corrupt[at] ^= (byte)(1 << random.Next(8));
                }
            }

            var ex = Record.Exception(() => PdfImage.Load(corrupt));
            Assert.True(ex is null or ImageFormatException, $"{file} trial {trial}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Theory]
    [InlineData("jpeg", "embedded JPEG or PNG")]
    [InlineData("os2", "OS/2")]
    [InlineData("bits", "Unsupported BMP format")]
    [InlineData("offset", "pixel data offset")]
    [InlineData("pixels", "Truncated BMP pixel data")]
    public void Unsupported_bmp_variants_are_rejected_clearly(string variant, string message)
    {
        var data = File.ReadAllBytes(Fixtures.Path("Bmp/24bit.bmp"));
        switch (variant)
        {
            case "jpeg":
                data[30] = 4; // BI_JPEG
                break;
            case "os2":
                data[14] = 64; // OS/2 2.x header size
                break;
            case "bits":
                data[28] = 7; // 7 bits per pixel
                break;
            case "offset":
                data[10] = 20; // Pixel data inside the header.
                break;
            default:
                data = data[..^200];
                break;
        }

        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("interframe", "not a key frame")]
    [InlineData("startcode", "start code")]
    [InlineData("chunk", "Unsupported WebP chunk")]
    [InlineData("riff", "Truncated WebP file")]
    [InlineData("canvas", "does not match its canvas")]
    [InlineData("alpha", "alpha header")]
    [InlineData("vp8l", "VP8L")]
    public void Malformed_webp_is_rejected_clearly(string variant, string message)
    {
        var lossy = File.ReadAllBytes(Fixtures.Path("WebP/lossy.webp"));
        var extended = File.ReadAllBytes(Fixtures.Path("WebP/alpha-raw-none.webp"));
        byte[] data;
        switch (variant)
        {
            case "interframe":
                data = lossy;
                data[20] |= 1; // Frame tag: inter frame.
                break;
            case "startcode":
                data = lossy;
                data[23] = 0;
                break;
            case "chunk":
                data = lossy;
                "VP9 "u8.CopyTo(data.AsSpan(12));
                break;
            case "riff":
                data = lossy[..^10];
                break;
            case "canvas":
                data = extended;
                data[24]++; // VP8X canvas width.
                break;
            case "alpha":
                data = extended;
                data[38] = 0xC0; // ALPH header: reserved bits set.
                break;
            default:
                data = File.ReadAllBytes(Fixtures.Path("WebP/lossless.webp"));
                data[20] = 0x2E; // VP8L signature.
                break;
        }

        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Webp_and_bmp_images_embed_with_soft_masks()
    {
        var doc = new PdfDocument();
        doc.AddPage().Canvas
            .DrawImage(PdfImage.Load(Fixtures.Path("WebP/lossy-alpha.webp")), 10, 10, 75, 53)
            .DrawImage(PdfImage.Load(Fixtures.Path("WebP/lossless.webp")), 100, 10, 75, 53)
            .DrawImage(PdfImage.Load(Fixtures.Path("Bmp/8bit.bmp")), 200, 10, 31, 23);
        var pdf = doc.ToArray();
        var text = System.Text.Encoding.Latin1.GetString(pdf);

        Assert.Equal(4, ImageObjectRegex().Matches(text).Count); // Three images, one soft mask.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "/SMask"));
        Assert.Contains("/Indexed", text, StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(pdf);
    }

    private static TheoryData<string> FixtureNames(string folder, string pattern) =>
        [.. Directory.GetFiles(Fixtures.Path(folder), pattern).Select(f => Path.GetFileName(f)).Order()];

    private static void AssertDecodesToReference(string folder, string file, ImageFormat format)
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine(folder, file)));
        Assert.Equal(format, image.Format);
        var ours = ToRgba(image.Image);
        var expected = ToRgba(PngDecoder.Decode(File.ReadAllBytes(Fixtures.Path(Path.Combine(folder, Path.ChangeExtension(file, ".png"))))));

        Assert.Equal((expected.Width, expected.Height), (ours.Width, ours.Height));
        Assert.Equal(expected.Pixels.Any(p => p.Alpha != 255), image.HasTransparency);
        for (var i = 0; i < ours.Pixels.Length; i++)
        {
            var (a, b) = (ours.Pixels[i], expected.Pixels[i]);
            Assert.True(a.Alpha == b.Alpha, $"{file}: alpha at ({i % ours.Width}, {i / ours.Width}): ours {a}, expected {b}");
            if (b.Alpha != 0)
            {
                Assert.True(a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue, $"{file}: pixel ({i % ours.Width}, {i / ours.Width}): ours {a}, expected {b}");
            }
        }
    }

    /// <summary>Byte ranges holding image dimensions: BMP headers, VP8X/ANMF headers and VP8/VP8L frame headers.</summary>
    private static IEnumerable<(int Start, int Count)> SizeFields(byte[] data)
    {
        if (data[0] == 'B')
        {
            yield return (0, 14 + data[14] + 16);
            yield break;
        }

        yield return (0, 12);
        for (var i = 12; i + 4 <= data.Length; i++)
        {
            var tag = System.Text.Encoding.ASCII.GetString(data, i, 4);
            if (tag is "VP8X" or "ANMF" or "VP8L" or "ALPH" or "ANIM")
            {
                yield return (i, 8 + 16);
            }
            else if (tag == "VP8 ")
            {
                yield return (i, 8 + 10);
            }
        }
    }
}
