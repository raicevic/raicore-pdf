using System.Buffers.Binary;
using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Images;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>
/// TIFF decoding. Each fixture in Fixtures/Images/Tiff comes with a PNG of the pixels each page must display as
/// (tools/generate_tiff_fixtures.py): computed from the source picture for decoded files, and libtiff's and libjpeg's
/// own decoding (through Pillow) for fax and JPEG data, which is embedded without decoding and checked by rendering.
/// </summary>
public partial class ImageTests
{
    private const string TiffFolder = "Images/Tiff";

    public static TheoryData<string> TiffFixtures() =>
        [.. FixtureNames(TiffFolder, "*.tif").Select(row => row.Data).Where(f => !f.StartsWith("unsupported-", StringComparison.Ordinal))];

    /// <summary>Fixtures decoded to samples (not fax or JPEG data, which PDF viewers decode).</summary>
    public static TheoryData<string> DecodedTiffFixtures() =>
        [.. TiffFixtures().Select(row => row.Data).Where(f => !IsPassedThrough(f))];

    [Theory]
    [MemberData(nameof(DecodedTiffFixtures))]
    public void Decodes_tiff_variants_exactly(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)));
        for (var page = 0; page < PdfImage.GetPageCount(data); page++)
        {
            var image = PdfImage.Load(data, page);
            Assert.Equal(PdfImageFormat.Tiff, image.Format);
            Assert.Equal(ImageEncoding.Raw, image.Image.Encoding);
            var ours = Oriented(ToRgba(image.Image), image.Image.Orientation);
            var expected = ToRgba(PngDecoder.Decode(File.ReadAllBytes(TiffReference(file, page))));

            Assert.Equal((expected.Width, expected.Height), (ours.Width, ours.Height));
            Assert.Equal((expected.Width, expected.Height), (image.PixelWidth, image.PixelHeight));
            Assert.Equal(expected.Pixels.Any(p => p.Alpha != 255), image.HasTransparency);
            for (var i = 0; i < ours.Pixels.Length; i++)
            {
                var (a, b) = (ours.Pixels[i], expected.Pixels[i]);
                Assert.True(a.Alpha == b.Alpha, $"{file} page {page}: alpha at ({i % ours.Width}, {i / ours.Width}): ours {a}, expected {b}");
                if (b.Alpha != 0)
                {
                    Assert.True(a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue, $"{file} page {page}: pixel ({i % ours.Width}, {i / ours.Width}): ours {a}, expected {b}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TiffFixtures))]
    public void Renders_tiff_like_the_reference(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)));
        var background = new SKColor(40, 160, 220);
        for (var page = 0; page < PdfImage.GetPageCount(data); page++)
        {
            var image = PdfImage.Load(data, page);
            var expected = ToRgba(PngDecoder.Decode(File.ReadAllBytes(TiffReference(file, page))));
            var doc = new PdfDocument();
            var pdfPage = doc.AddPage(new PdfPageSize(expected.Width + 20, expected.Height + 20));
            pdfPage.Canvas.SetFillColor(PdfColor.FromRgb(40, 160, 220)).Rectangle(0, 0, pdfPage.Width, pdfPage.Height).Fill()
                .DrawImage(image, 10, 10, expected.Width, expected.Height);
            var pdf = doc.ToArray();
            using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

            // PDFium converts DeviceCMYK with a print-like formula, not naively; JPEG decoders differ by a few levels.
            var tolerance = file.StartsWith("cmyk", StringComparison.Ordinal) ? 110 : file.Contains("jpeg", StringComparison.Ordinal) ? 12 : 2;
            var wrong = 0;
            for (var y = 0; y < expected.Height; y++)
            {
                if (file == "ycbcr-jpeg-strips.tif" && y % 16 is 0 or 15 && y > 0)
                {
                    // Merged into one JPEG, the 16-row strips' subsampled colors are interpolated across the strip
                    // boundaries, which decoding the strips one by one (the reference) cannot do.
                    continue;
                }

                for (var x = 0; x < expected.Width; x++)
                {
                    var source = expected.Pixels[(y * expected.Width) + x];
                    var a = source.Alpha / 255.0;
                    var composite = new SKColor(
                        (byte)Math.Round((source.Red * a) + (background.Red * (1 - a))),
                        (byte)Math.Round((source.Green * a) + (background.Green * (1 - a))),
                        (byte)Math.Round((source.Blue * a) + (background.Blue * (1 - a))));
                    var actual = bitmap.GetPixel(10 + x, 10 + y);
                    if (Math.Abs(composite.Red - actual.Red) > tolerance || Math.Abs(composite.Green - actual.Green) > tolerance
                        || Math.Abs(composite.Blue - actual.Blue) > tolerance)
                    {
                        Assert.True(++wrong <= expected.Width * expected.Height / 100, $"{file} page {page} ({x}, {y}): expected {composite}, got {actual}");
                    }
                }
            }

            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    [Theory]
    [InlineData("fax-g4.tif", "/K -1/", true, false)]
    [InlineData("fax-g4-miniswhite.tif", "/K -1/", false, false)]
    [InlineData("fax-g3-1d.tif", "/K 0/", true, false)]
    [InlineData("fax-g3-2d-aligned.tif", "/K 1/", true, false)]
    [InlineData("fax-mh.tif", "/K 0/", true, true)]
    [InlineData("fax-mh-strips.tif", "/K 0/", true, true)]
    public void Fax_data_is_embedded_as_it_is(string file, string k, bool blackIs1, bool byteAligned)
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine(TiffFolder, file)));
        Assert.Equal(ImageEncoding.CcittFax, image.Image.Encoding);
        Assert.Equal((ImageColorSpace.Gray, 1), (image.Image.ColorSpace, image.Image.BitsPerComponent));
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawImage(image, 10, 10, 120, 64);
        var text = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Contains("/Filter /CCITTFaxDecode", text, StringComparison.Ordinal);
        Assert.Contains(k, text, StringComparison.Ordinal);
        Assert.Contains("/Columns 120/Rows 64/EndOfBlock false", text, StringComparison.Ordinal);
        Assert.Equal(blackIs1, text.Contains("/BlackIs1 true", StringComparison.Ordinal));
        Assert.Equal(byteAligned, text.Contains("/EncodedByteAlign true", StringComparison.Ordinal));
        Assert.False(image.HasTransparency);
    }

    [Fact]
    public void Single_strip_fax_data_is_not_changed()
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, "fax-g4.tif")));
        var image = PdfImage.Load(data);
        Assert.Equal(data.AsSpan(8, image.Image.Data.Length).ToArray(), image.Image.Data); // Pillow writes the strip right after the header.
    }

    [Theory]
    [InlineData("rgb8-jpeg.tif", "Rgb", 0)]
    [InlineData("rgb8-jpeg-strips.tif", "Rgb", 0)]
    [InlineData("ycbcr-jpeg-strips.tif", "Rgb", null)]
    [InlineData("gray8-jpeg-strips.tif", "Gray", null)]
    [InlineData("cmyk8-jpeg.tif", "Cmyk", null)]
    public void Jpeg_data_is_embedded_as_one_jpeg(string file, string colorSpace, int? colorTransform)
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine(TiffFolder, file)));
        Assert.Equal(ImageEncoding.Jpeg, image.Image.Encoding);
        Assert.Equal(colorSpace, image.Image.ColorSpace.ToString());
        Assert.Equal(colorTransform, image.Image.ColorTransform);
        Assert.False(image.Image.InvertCmyk); // libtiff writes CMYK JPEG data without the Adobe marker, not inverted.

        // One JPEG, which JpegReader (and so any viewer) reads as the whole image.
        var jpeg = JpegReader.Read(image.Image.Data);
        Assert.Equal((48, 40), (jpeg.Width, jpeg.Height));
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawImage(image, 10, 10, 48, 40);
        var text = Encoding.Latin1.GetString(doc.ToArray());
        Assert.Contains("/Filter /DCTDecode", text, StringComparison.Ordinal);
        Assert.Equal(colorTransform is not null, text.Contains("/DecodeParms <</ColorTransform 0>>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("multipage.tif", 3)]
    [InlineData("fax-g4-multipage.tif", 3)]
    [InlineData("rgb8-lzw-predictor.tif", 1)]
    public void Counts_the_pages_of_a_tiff(string file, int pages)
    {
        var path = Fixtures.Path(Path.Combine(TiffFolder, file));
        Assert.Equal(pages, PdfImage.GetPageCount(path));
        Assert.Equal(PdfImage.Load(path, pages - 1).PixelWidth, PdfImage.Load(File.ReadAllBytes(path), pages - 1).PixelWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.Load(path, pages));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.Load(path, -1));
    }

    [Fact]
    public void The_first_page_loads_by_default()
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, "multipage.tif")));
        Assert.Equal(PdfImage.Load(data, 0).Image.Data, PdfImage.Load(data).Image.Data);
        Assert.NotEqual(PdfImage.Load(data, 1).Image.ColorSpace, PdfImage.Load(data).Image.ColorSpace);
    }

    [Theory]
    [InlineData("PngSuite/basn2c08.png")]
    [InlineData("Images/rgb.jpg")]
    [InlineData("Images/animated.gif")]
    [InlineData("Bmp/24bit.bmp")]
    [InlineData("WebP/lossy.webp")]
    [InlineData("Svg/logo.svg")]
    public void Other_formats_have_one_page(string file)
    {
        var path = Fixtures.Path(file);
        Assert.Equal(1, PdfImage.GetPageCount(path));
        Assert.Equal(PdfImage.Load(path).Format, PdfImage.Load(path, 0).Format);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImage.Load(path, 1));
    }

    [Fact]
    public void Page_count_of_unknown_data_is_an_image_format_error()
    {
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.GetPageCount("not an image"u8.ToArray()));
        Assert.Contains("TIFF", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fax-g4.tif", 204, 196)]
    [InlineData("fax-g4-multipage.tif", 204, 98)]
    [InlineData("gray8-150x75dpi.tif", 150, 75)]
    [InlineData("gray8-59x59dpcm.tif", 59 * 2.54, 59 * 2.54)]
    [InlineData("rgb8-lzw-predictor.tif", 96, 96)] // No absolute resolution: one CSS pixel per pixel, as other formats.
    [InlineData("rgb8-orientation6.tif", 96, 96)]
    public void Natural_size_follows_the_resolution(string file, double dpiX, double dpiY)
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine(TiffFolder, file)));
        Assert.Equal(image.PixelWidth * 72 / dpiX, image.NaturalWidth, 6);
        Assert.Equal(image.PixelHeight * 72 / dpiY, image.NaturalHeight, 6);
    }

    [Fact]
    public void Orientation_swaps_the_axes_and_their_resolutions()
    {
        // The 150 x 75 dpi file turned by orientation 6: the stored rows become columns.
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, "gray8-150x75dpi.tif")));
        PatchTiffTag(data, 274, 6, add: true);
        var image = PdfImage.Load(data);

        Assert.Equal((28, 40), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(28 * 72 / 75.0, image.NaturalWidth, 6);
        Assert.Equal(40 * 72 / 150.0, image.NaturalHeight, 6);
    }

    [Theory]
    [InlineData("unsupported-fax-g4-strips.tif", "CCITT Group 4) in 4 strips are not supported")]
    [InlineData("unsupported-float32.tif", "Only unsigned integer TIFF samples are supported")]
    [InlineData("unsupported-ycbcr-jpeg-strips-of-8-rows.tif", "the rows per strip must be a multiple of 16")]
    public void Unsupported_tiffs_are_rejected_clearly(string file, string message)
    {
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(Fixtures.Path(Path.Combine(TiffFolder, file))));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rgb8-lzw-predictor.tif", 259, 6, "Old-style JPEG")]
    [InlineData("rgb8-lzw-predictor.tif", 259, 34712, "TIFF compression 34712 is not supported")]
    [InlineData("rgb8-lzw-predictor.tif", 262, 6, "YCbCr images are supported only with JPEG")]
    [InlineData("rgb8-lzw-predictor.tif", 262, 8, "photometric interpretation 8 is not supported")]
    [InlineData("rgb8-lzw-predictor.tif", 262, 5, "needs 4 samples per pixel, not 3")]
    [InlineData("rgb8-lzw-predictor.tif", 317, 3, "TIFF predictor 3 with 8-bit samples is not supported")]
    [InlineData("rgb8-lzw-predictor.tif", 277, 9, "9 samples per pixel are not supported")]
    [InlineData("rgb8-lzw-predictor.tif", 256, 0, "TIFF size must be positive")]
    [InlineData("rgb8-lzw-predictor.tif", 273, 5000, "strip 0 is outside the file")]
    [InlineData("rgb8-lzw-predictor.tif", 279, 10, "bytes of image data; its rows need 3360")]
    [InlineData("rgba8-unassociated-lzw.tif", 259, 7, "JPEG-compressed TIFF images with 4 samples per pixel are not supported")]
    [InlineData("rgb8-deflate-tiled.tif", 259, 7, "JPEG-compressed TIFF images in several tiles are not supported")]
    [InlineData("gray2-lzw.tif", 262, 3, "palette TIFF has no complete color map")]
    [InlineData("gray2-lzw.tif", 259, 3, "must have one 1-bit sample per pixel")]
    [InlineData("bilevel-none-mm.tif", 262, 2, "needs 3 samples per pixel, not 1")]
    [InlineData("bilevel-none-mm.tif", 258, 3, "3-bit TIFF images are not supported")]
    [InlineData("palette4-packbits.tif", 258, 16, "16-bit TIFF palette images are not supported")]
    [InlineData("fax-g4.tif", 262, 2, "fax (CCITT) images with photometric interpretation 2 are not supported")]
    [InlineData("fax-g3-1d.tif", 292, 2, "uncompressed mode")]
    [InlineData("fax-g4.tif", 293, 2, "uncompressed mode")]
    [InlineData("rgb8-jpeg.tif", 262, 0, "JPEG-compressed TIFF images with photometric interpretation 0 are not supported")]
    [InlineData("rgb8-jpeg.tif", 284, 2, "in separate planes are not supported")]
    [InlineData("rgb8-jpeg-strips.tif", 278, 24, "does not match the strip's size")]
    [InlineData("rgb8-jpeg.tif", 256, 40, "is 48 x 40 pixels with 3 components, not 40 x 40 with 3")]
    public void Unsupported_and_damaged_tiffs_are_rejected_clearly(string file, ushort tag, uint value, string message)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)));
        PatchTiffTag(data, tag, value, add: tag is 292 or 293);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rgb8-lzw-predictor.tif")]
    [InlineData("rgb8-bigtiff-lzw.tif")]
    [InlineData("rgb8-lzw-tiled-planar-mm.tif")]
    [InlineData("fax-g4.tif")]
    [InlineData("rgb8-jpeg-strips.tif")]
    [InlineData("multipage.tif")]
    public void Truncated_tiffs_fail_with_an_image_format_error(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)));
        for (var length = 0; length < data.Length; length += Math.Max(1, data.Length / 200))
        {
            var ex = Record.Exception(() => LoadAllPages(data[..length]));
            Assert.True(ex is null or ImageFormatException, $"{file} cut at {length}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(TiffFixtures))]
    public void Corrupt_tiffs_fail_with_an_image_format_error(string file)
    {
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)));
        var random = new Random(file.Length);
        for (var trial = 0; trial < 200; trial++)
        {
            var corrupt = (byte[])data.Clone();
            for (var n = 0; n < 1 + (trial % 4); n++)
            {
                var at = random.Next(4, corrupt.Length); // Keep the header.
                corrupt[at] ^= (byte)(1 << random.Next(8));
            }

            // A damaged size can exceed the pixel limit (kept low, so that a damaged size does not make the test slow);
            // the limit's exception is documented too.
            var ex = Record.Exception(() =>
            {
                using ((ResourceLimits.Default with { MaxImagePixels = 100_000 }).Enforce(TestContext.Current.CancellationToken))
                {
                    LoadAllPages(corrupt);
                }
            });
            Assert.True(ex is null or ImageFormatException or ResourceLimitExceededException, $"{file} trial {trial}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }

    [Fact]
    public void Decompression_stops_at_the_image_size()
    {
        // A huge image of tiny strips claims little data: the pixel limit applies before anything is allocated.
        var data = File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, "rgb8-lzw-predictor.tif")));
        PatchTiffTag(data, 257, 1_000_000);
        PatchTiffTag(data, 256, 1_000_000);
        var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfImage.Load(data));
        Assert.Equal(nameof(ResourceLimits.MaxImagePixels), ex.LimitName);
    }

    [Fact]
    public void Cmyk_tiffs_are_refused_in_pdf_a()
    {
        var image = PdfImage.Load(Fixtures.Path(Path.Combine(TiffFolder, "cmyk8-lzw.tif")));
        var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B };
        doc.AddPage().Canvas.DrawImage(image, 10, 10, 40, 28);
        var ex = Assert.Throws<InvalidOperationException>(() => doc.ToArray());
        Assert.Contains("convert the CMYK image to RGB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_img_and_backgrounds_show_tiffs_from_data_uris()
    {
        static string DataUri(string file) => "data:image/tiff;base64," + Convert.ToBase64String(File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file))));
        var html = $$"""
            <style>@page { margin: 0 } body { margin: 0 }</style>
            <body>
            <div style="background: #2896dc; padding: 20px">
              <img src="{{DataUri("rgba8-unassociated-lzw.tif")}}" style="display: block; width: 320px">
              <div style="width: 320px; height: 224px; background: url('{{DataUri("rgb8-deflate-tiled.tif")}}') 0 0 / 320px 224px no-repeat"></div>
            </div>
            </body>
            """;
        using var bitmap = Conversion.ToImage(HtmlToPdf.ConvertToBytes(html), page: 0, options: new RenderOptions(Dpi: 96));
        var background = new SKColor(0x28, 0x96, 0xDC);

        // Both 40 x 28 pictures at 8x, one below the other.
        foreach (var (file, top) in new[] { ("rgba8-unassociated-lzw.png", 20), ("rgb8-deflate-tiled.png", 20 + 224) })
        {
            var expected = ToRgba(PngDecoder.Decode(File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)))));
            for (var y = 1; y < expected.Height; y += 3)
            {
                for (var x = 1; x < expected.Width; x += 3)
                {
                    var source = expected.Pixels[(y * expected.Width) + x];
                    var a = source.Alpha / 255.0;
                    var composite = new SKColor(
                        (byte)Math.Round((source.Red * a) + (background.Red * (1 - a))),
                        (byte)Math.Round((source.Green * a) + (background.Green * (1 - a))),
                        (byte)Math.Round((source.Blue * a) + (background.Blue * (1 - a))));
                    AssertColor(composite, bitmap.GetPixel(20 + (x * 8) + 4, top + (y * 8) + 4), file, $"({x}, {y})", tolerance: 16);
                }
            }
        }
    }

    [Fact]
    public void Html_img_takes_a_tiffs_physical_size()
    {
        // A 120 x 64 fax at 204 x 196 dpi is 0.588 x 0.327 inches: 56.5 x 31.3 CSS pixels.
        var uri = "data:image/tiff;base64," + Convert.ToBase64String(File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, "fax-g4.tif"))));
        var html = $"""<style>@page {"{"} margin: 0 {"}"} body {"{"} margin: 0 {"}"}</style><img src="{uri}" style="display: block">""";
        using var bitmap = Conversion.ToImage(HtmlToPdf.ConvertToBytes(html), page: 0, options: new RenderOptions(Dpi: 96));
        var lastDark = Enumerable.Range(0, 120).Last(x => Enumerable.Range(0, 40).Any(y => bitmap.GetPixel(x, y).Red < 128));
        Assert.InRange(lastDark, 45, 57);
    }

    private static bool IsPassedThrough(string file) =>
        file.StartsWith("fax-", StringComparison.Ordinal) || file.Contains("jpeg", StringComparison.Ordinal);

    private static string TiffReference(string file, int page) =>
        Fixtures.Path(Path.Combine(TiffFolder, Path.GetFileNameWithoutExtension(file) + (page == 0 ? ".png" : $".page{page + 1}.png")));

    private static void LoadAllPages(byte[] data)
    {
        var pages = Math.Min(PdfImage.GetPageCount(data), 4);
        for (var page = 0; page < pages; page++)
        {
            PdfImage.Load(data, page);
        }
    }

    /// <summary>The pixels as displayed with an EXIF/TIFF orientation (1-8).</summary>
    private static (int Width, int Height, SKColor[] Pixels) Oriented((int Width, int Height, SKColor[] Pixels) image, int orientation)
    {
        var (w, h) = (image.Width, image.Height);
        var (dw, dh) = orientation >= 5 ? (h, w) : (w, h);
        var pixels = new SKColor[w * h];
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                var (sx, sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    8 => (w - 1 - y, x),
                    _ => (x, y),
                };
                pixels[(y * dw) + x] = image.Pixels[(sy * w) + sx];
            }
        }

        return (dw, dh, pixels);
    }

    /// <summary>
    /// Sets the first value of a SHORT or LONG tag in the first directory of a classic TIFF; with <paramref name="add"/>,
    /// a missing tag replaces the directory's last entry (whatever it was) instead.
    /// </summary>
    private static void PatchTiffTag(byte[] data, ushort tag, uint value, bool add = false)
    {
        var little = data[0] == 'I';
        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
        void Set16(int at, ushort v)
        {
            if (little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), v);
            }
        }

        void Set32(int at, uint v)
        {
            if (little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), v);
            }
        }

        var ifd = (int)U32(4);
        var count = U16(ifd);
        var entry = Enumerable.Range(0, count).Select(i => ifd + 2 + (i * 12)).FirstOrDefault(e => U16(e) == tag, -1);
        if (entry < 0)
        {
            Assert.True(add, $"The fixture has no tag {tag}.");
            entry = ifd + 2 + ((count - 1) * 12);

            // Keep the entries sorted: the new tag takes the last place only if no later tag needs it.
            Set16(entry, tag);
            Set16(entry + 2, 3);
            Set32(entry + 4, 1);
        }

        var type = U16(entry + 2);
        if (type == 3)
        {
            Set16(entry + 8, (ushort)value);
        }
        else if (U32(entry + 4) == 1)
        {
            Set32(entry + 8, value);
        }
        else
        {
            Set32((int)U32(entry + 8), value); // The first of several values, stored elsewhere.
        }
    }
}
