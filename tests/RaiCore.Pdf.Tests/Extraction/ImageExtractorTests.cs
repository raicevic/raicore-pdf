using System.Buffers.Binary;
using System.Text;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Tests.Reading;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

public class ImageExtractorTests
{
    private const double Tolerance = 0.01;

    private static PageImages Extract(byte[] pdf, int page = 0) => new ImageExtractor(PdfReader.Open(pdf)).ExtractPage(page);

    /// <summary>A page that draws the image XObject <c>/Im1</c> (object 5; more objects from 6) with <paramref name="content"/>.</summary>
    private static PageImages Drawn(string content, string image, string pageEntries = "/MediaBox [0 0 600 800]", params string[] more) =>
        Extract(OnePage(content, "<< /XObject << /Im1 5 0 R >> >>", pageEntries, [image, .. more]));

    /// <summary>An image XObject of raw samples.</summary>
    private static string Image(string entries, params int[] samples) =>
        TestPdf.Stream(Latin1(samples), $"/Type /XObject /Subtype /Image {entries}");

    private static string Latin1(params int[] bytes) => Encoding.Latin1.GetString([.. bytes.Select(b => (byte)b)]);

    private static void AssertNear(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"Expected {expected}, got {actual}.");

    private static void AssertBounds(PdfRectangle bounds, double x, double y, double width, double height)
    {
        AssertNear(x, bounds.X);
        AssertNear(y, bounds.Y);
        AssertNear(width, bounds.Width);
        AssertNear(height, bounds.Height);
    }

    /// <summary>A PNG's pixels as RGBA bytes, 8 bits per channel (16-bit samples rounded to 8 bits).</summary>
    private static (int Width, int Height, byte[] Rgba) Rgba(byte[] png)
    {
        var image = PngDecoder.Decode(png);
        var (width, height, bits) = (image.Width, image.Height, image.BitsPerComponent);
        var components = image.ColorSpace switch { ImageColorSpace.Rgb => 3, _ => 1 };
        var rowBytes = ((width * components * bits) + 7) / 8;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int Sample(int c)
                {
                    var bit = ((x * components) + c) * bits;
                    var at = (y * rowBytes) + (bit / 8);
                    return bits switch
                    {
                        16 => (int)Math.Round(((image.Data[at] << 8) | image.Data[at + 1]) / 257.0),
                        8 => image.Data[at],
                        _ => (image.Data[at] >> (8 - bits - (bit % 8))) & ((1 << bits) - 1),
                    };
                }

                var i = ((y * width) + x) * 4;
                switch (image.ColorSpace)
                {
                    case ImageColorSpace.Indexed:
                        var index = Sample(0);
                        rgba[i] = image.Palette![3 * index];
                        rgba[i + 1] = image.Palette[(3 * index) + 1];
                        rgba[i + 2] = image.Palette[(3 * index) + 2];
                        break;
                    case ImageColorSpace.Rgb:
                        (rgba[i], rgba[i + 1], rgba[i + 2]) = ((byte)Sample(0), (byte)Sample(1), (byte)Sample(2));
                        break;
                    default:
                        var gray = bits >= 8 ? Sample(0) : Sample(0) * 255 / ((1 << bits) - 1);
                        (rgba[i], rgba[i + 1], rgba[i + 2]) = ((byte)gray, (byte)gray, (byte)gray);
                        break;
                }

                rgba[i + 3] = image.Alpha?[(y * width) + x] ?? 255;
                if (image.ColorKey is { } key && Enumerable.Range(0, components).All(c => Sample(c) >= key[2 * c] && Sample(c) <= key[(2 * c) + 1]))
                {
                    rgba[i + 3] = 0;
                }
            }
        }

        return (width, height, rgba);
    }

    [Fact]
    public void A_jpeg_is_found_where_it_was_drawn_and_exported_as_it_is()
    {
        var jpeg = Fixtures.Read("Images/rgb.jpg");
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(PdfImage.Load(jpeg), 100, 200, 300, 150);

        var page = Extract(document.ToArray());

        var image = Assert.Single(page.Images);
        AssertBounds(image.Bounds, 100, 200, 300, 150);
        AssertNear(300, image.Width);
        AssertNear(150, image.Height);
        Assert.Equal(0, image.Rotation);
        Assert.Equal(ExtractedImageFormat.Jpeg, image.Format);
        Assert.Equal(".jpg", image.FileExtension);
        Assert.Equal("DeviceRGB", image.ColorSpace);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.False(image.IsInline);
        Assert.False(image.HasMask);
        Assert.NotNull(image.ObjectNumber);
        Assert.Equal(jpeg, image.GetData());
        var decoded = JpegReader.Read(jpeg);
        Assert.Equal((decoded.Width, decoded.Height), (image.PixelWidth, image.PixelHeight));
        Assert.Empty(page.Diagnostics);
    }

    [Theory]
    [InlineData("basn0g01.png")]
    [InlineData("basn0g02.png")]
    [InlineData("basn0g04.png")]
    [InlineData("basn0g08.png")]
    [InlineData("basn0g16.png")]
    [InlineData("basn2c08.png")]
    [InlineData("basn2c16.png")]
    [InlineData("basn3p01.png")]
    [InlineData("basn3p02.png")]
    [InlineData("basn3p04.png")]
    [InlineData("basn3p08.png")]
    [InlineData("basn4a08.png")]
    [InlineData("basn4a16.png")]
    [InlineData("basn6a08.png")]
    [InlineData("basn6a16.png")]
    [InlineData("basi6a08.png")]
    [InlineData("tbbn3p08.png")]
    [InlineData("tbrn2c08.png")]
    [InlineData("tbbn0g04.png")]
    [InlineData("tbwn3p08.png")]
    public void Png_images_export_to_their_source_pixels(string name)
    {
        var source = Fixtures.Read("PngSuite/" + name);
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(PdfImage.Load(source), 50, 60, 64, 64);

        var image = Assert.Single(Extract(document.ToArray()).Images);

        Assert.Equal(ExtractedImageFormat.Png, image.Format);
        var expected = Rgba(source);
        var actual = Rgba(image.GetData());
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        Assert.Equal((image.PixelWidth, image.PixelHeight), (actual.Width, actual.Height));
        Assert.Equal(expected.Rgba, actual.Rgba);
    }

    [Fact]
    public void Turned_and_flipped_images_have_their_size_and_rotation()
    {
        var image = Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", 0, 255);

        // Turned a quarter counterclockwise: the image's bottom edge (100 long) points up.
        var turned = Assert.Single(Drawn("q 0 100 -50 0 300 200 cm /Im1 Do Q", image).Images);
        AssertBounds(turned.Bounds, 250, 200, 50, 100);
        AssertNear(100, turned.Width);
        AssertNear(50, turned.Height);
        AssertNear(90, turned.Rotation);

        // Upside down (a negative height, as some producers draw).
        var flipped = Assert.Single(Drawn("q 40 0 0 -30 10 100 cm /Im1 Do Q", image).Images);
        AssertBounds(flipped.Bounds, 10, 70, 40, 30);
        Assert.Equal(0, flipped.Rotation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Positions_are_in_user_space_on_rotated_and_cropped_pages(int rotate)
    {
        var image = Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", 128);

        var placed = Assert.Single(Drawn("q 80 0 0 40 120 300 cm /Im1 Do Q", image, $"/MediaBox [0 0 600 800] /CropBox [20 30 580 770] /Rotate {rotate}").Images);

        AssertBounds(placed.Bounds, 120, 300, 80, 40);
        Assert.Equal(0, placed.Rotation);
    }

    [Fact]
    public void Images_in_forms_follow_the_form_matrix()
    {
        var image = Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", 0);
        var form = TestPdf.Stream("q 10 0 0 20 0 0 cm /Im1 Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [2 0 0 2 30 40] /Resources << /XObject << /Im1 5 0 R >> >>");

        var page = Extract(OnePage("/Fm1 Do", "<< /XObject << /Fm1 6 0 R >> >>", extra: [image, form]));

        var placed = Assert.Single(page.Images);
        AssertBounds(placed.Bounds, 30, 40, 20, 40);
        Assert.Equal("Im1", placed.ResourceName);
    }

    [Fact]
    public void Images_drawn_twice_are_listed_twice_and_hidden_ones_not_at_all()
    {
        var image = Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", 0);
        var content = "q 10 0 0 10 0 0 cm /Im1 Do Q q 10 0 0 10 100 100 cm /Im1 Do Q "
            + "q 10 0 0 10 700 100 cm /Im1 Do Q " // Off the page.
            + "q 0 0 5 5 re W n 10 0 0 10 300 300 cm /Im1 Do Q"; // Clipped away.

        var images = Drawn(content, image).Images;

        Assert.Equal(2, images.Count);
        Assert.Equal(images[0].ObjectNumber, images[1].ObjectNumber);
        AssertBounds(images[1].Bounds, 100, 100, 10, 10);
    }

    [Fact]
    public void Inline_images_are_listed_with_their_abbreviations_expanded()
    {
        var content = "q 20 0 0 10 50 60 cm BI /W 2 /H 1 /CS /RGB /BPC 8 /F /AHx ID ff000000ff00> EI Q "
            + "q 20 0 0 10 50 90 cm BI /W 2 /H 1 /CS /Pal /BPC 8 ID \u0001\u0000 EI Q "
            + "q 20 0 0 10 50 120 cm BI /W 1 /H 1 /CS [/I /G 1 <0080>] /BPC 8 ID \u0001 EI Q";
        var resources = "<< /ColorSpace << /Pal [/Indexed /DeviceRGB 1 <0000ff00ffff>] >> >>";

        var images = Extract(OnePage(content, resources)).Images;

        Assert.Equal(3, images.Count);
        Assert.True(images[0].IsInline);
        Assert.Null(images[0].ResourceName);
        Assert.Null(images[0].ObjectNumber);
        AssertBounds(images[0].Bounds, 50, 60, 20, 10);
        Assert.Equal(ExtractedImageFormat.Png, images[0].Format);
        Assert.Equal([255, 0, 0, 255, 0, 255, 0, 255], Rgba(images[0].GetData()).Rgba);
        Assert.Equal("Indexed", images[1].ColorSpace);
        Assert.Equal([0, 255, 255, 255, 0, 0, 255, 255], Rgba(images[1].GetData()).Rgba);
        Assert.Equal([128, 128, 128, 255], Rgba(images[2].GetData()).Rgba);
    }

    [Fact]
    public void Colors_are_converted_and_decode_arrays_applied()
    {
        // CMYK: white, black, cyan, half magenta.
        var cmyk = Drawn("/Im1 Do", Image("/Width 4 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8", 0, 0, 0, 0, 0, 0, 0, 255, 255, 0, 0, 0, 0, 128, 0, 0)).Images[0];
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255, 0, 255, 255, 255, 255, 127, 255, 255], Rgba(cmyk.GetData()).Rgba);

        // A /Decode array that is not numbers is ignored.
        var badDecode = Drawn("/Im1 Do", Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 /Zero]", 200)).Images[0];
        Assert.Equal([200, 200, 200, 255], Rgba(badDecode.GetData()).Rgba);

        // Gray, inverted by /Decode, 4 bits per sample.
        var gray = Drawn("/Im1 Do", Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 4 /Decode [1 0]", 0x0F)).Images[0];
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255], Rgba(gray.GetData()).Rgba);

        // ICC-based RGB, its profile not applied; an indexed space with its table in a stream.
        var icc = TestPdf.Stream("profile", "/N 3");
        var iccImage = Drawn("/Im1 Do", Image("/Width 1 /Height 1 /ColorSpace [/ICCBased 6 0 R] /BitsPerComponent 8", 1, 2, 3), more: icc).Images[0];
        Assert.Equal("ICCBased", iccImage.ColorSpace);
        Assert.Equal([1, 2, 3, 255], Rgba(iccImage.GetData()).Rgba);
        var table = TestPdf.Stream(Latin1(10, 20, 30, 40, 50, 60));
        var indexed = Drawn("/Im1 Do", Image("/Width 2 /Height 1 /ColorSpace [/Indexed /DeviceRGB 1 6 0 R] /BitsPerComponent 1", 0b0100_0000), more: table).Images[0];
        Assert.Equal([10, 20, 30, 255, 40, 50, 60, 255], Rgba(indexed.GetData()).Rgba);

        // A short color table is padded with black; an index past the table's end takes its last color.
        var padded = Drawn("/Im1 Do", Image("/Width 3 /Height 1 /ColorSpace [/Indexed /DeviceRGB 1 <0a141e>] /BitsPerComponent 8", 0, 1, 7)).Images[0];
        Assert.Equal([10, 20, 30, 255, 0, 0, 0, 255, 0, 0, 0, 255], Rgba(padded.GetData()).Rgba);
    }

    [Fact]
    public void Masks_become_alpha()
    {
        // A stencil mask image: black where it paints (0 bits), transparent elsewhere.
        var stencil = Drawn("/Im1 Do", Image("/Width 2 /Height 1 /ImageMask true", 0b0100_0000)).Images[0];
        Assert.True(stencil.IsStencilMask);
        Assert.Null(stencil.ColorSpace);
        Assert.Equal([0, 0, 0, 255, 0, 0, 0, 0], Rgba(stencil.GetData()).Rgba);
        var inverted = Drawn("/Im1 Do", Image("/Width 2 /Height 1 /ImageMask true /Decode [1 0]", 0b0100_0000)).Images[0];
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 255], Rgba(inverted.GetData()).Rgba);

        // An image with a stencil /Mask: shown where the mask has 0 bits (as viewers draw it).
        var masked = Drawn("/Im1 Do", Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask 6 0 R", 10, 20), more: Image("/Width 2 /Height 1 /ImageMask true", 0b0100_0000)).Images[0];
        Assert.True(masked.HasMask);
        Assert.Equal([10, 10, 10, 255, 20, 20, 20, 0], Rgba(masked.GetData()).Rgba);

        // Color key masking: gray values 15 to 25 are transparent.
        var keyed = Drawn("/Im1 Do", Image("/Width 3 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Mask [15 25]", 10, 20, 30)).Images[0];
        Assert.Equal([10, 10, 10, 255, 20, 20, 20, 0, 30, 30, 30, 255], Rgba(keyed.GetData()).Rgba);

        // A soft mask of another size is scaled to the image: here each of its pixels covers 2 x 2 of the image's.
        var soft = Drawn("/Im1 Do", Image("/Width 4 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /SMask 6 0 R", [.. Enumerable.Repeat(7, 16)]), more: Image("/Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", 10, 20, 30, 40)).Images[0];
        Assert.Equal(
            [10, 10, 20, 20, 10, 10, 20, 20, 30, 30, 40, 40, 30, 30, 40, 40],
            Rgba(soft.GetData()).Rgba.Where((_, i) => i % 4 == 3).Select(a => (int)a));

        // A soft mask that cannot be read here (a JPEG) leaves the image opaque.
        var jpegMask = TestPdf.Stream("data", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode");
        var opaque = Drawn("/Im1 Do", Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /SMask 6 0 R", 7), more: jpegMask).Images[0];
        Assert.True(opaque.HasMask);
        Assert.Equal([7, 7, 7, 255], Rgba(opaque.GetData()).Rgba);
    }

    [Fact]
    public void Ccitt_fax_images_are_wrapped_in_tiff()
    {
        // 16 x 4 pixels in Group 4: white, a black pixel at the top-left and a black bar across the middle rows.
        var g4 = Convert.FromHexString("23861173fc60020020");
        var data = Latin1([.. g4]);
        var image = Drawn("/Im1 Do", TestPdf.Stream(data, "/Type /XObject /Subtype /Image /Width 16 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 16 /Rows 4 >>")).Images[0];

        Assert.Equal(ExtractedImageFormat.Tiff, image.Format);
        Assert.Equal(".tif", image.FileExtension);
        var tiff = image.GetData();
        Assert.Equal("II*\0"u8.ToArray(), tiff[..4]);
        var tags = Tags(tiff);
        Assert.Equal(16u, tags[256]);
        Assert.Equal(4u, tags[257]);
        Assert.Equal(4u, tags[259]); // Group 4.
        Assert.Equal(0u, tags[262]); // White is zero.
        Assert.Equal(g4, tiff[(int)tags[273]..((int)tags[273] + (int)tags[279])]);

        // Black is 1, or inverted by /Decode: the other photometric interpretation.
        var blackIs1 = Drawn("/Im1 Do", TestPdf.Stream(data, "/Type /XObject /Subtype /Image /Width 16 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K 0 /BlackIs1 true >>")).Images[0];
        var g3 = Tags(blackIs1.GetData());
        Assert.Equal(1u, g3[262]);
        Assert.Equal(3u, g3[259]);
        Assert.Equal(0u, g3[292]);
        var decoded = Drawn("/Im1 Do", TestPdf.Stream(data, "/Type /XObject /Subtype /Image /Width 16 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 1 /Decode [1 0] /Filter /CCITTFaxDecode /DecodeParms << /K 1 >>")).Images[0];
        var twoDimensional = Tags(decoded.GetData());
        Assert.Equal(1u, twoDimensional[262]);
        Assert.Equal(1u, twoDimensional[292]);

        static Dictionary<int, uint> Tags(byte[] tiff)
        {
            var tags = new Dictionary<int, uint>();
            var ifd = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
            for (var i = 0; i < count; i++)
            {
                var entry = tiff.AsSpan(ifd + 2 + (12 * i));
                var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
                tags[BinaryPrimitives.ReadUInt16LittleEndian(entry)] = type == 3
                    ? BinaryPrimitives.ReadUInt16LittleEndian(entry[8..])
                    : BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            }

            return tags;
        }
    }

    [Fact]
    public void Other_codecs_and_color_spaces_are_exported_raw()
    {
        string Coded(string filter) => TestPdf.Stream("data", $"/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /Filter {filter}");

        // Filters before a codec are decoded; the codec's data is kept.
        var jpeg = Drawn("/Im1 Do", TestPdf.Stream("FFD8 FFD9>", "/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter [/ASCIIHexDecode /DCTDecode]")).Images[0];
        Assert.Equal(ExtractedImageFormat.Jpeg, jpeg.Format);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], jpeg.GetData());

        var jpx = Drawn("/Im1 Do", Coded("/JPXDecode")).Images[0];
        Assert.Equal((ExtractedImageFormat.Jpeg2000, ".jp2"), (jpx.Format, jpx.FileExtension));
        Assert.Null(jpx.ColorSpace);
        Assert.Equal("data"u8.ToArray(), jpx.GetData());

        var jbig2 = Drawn("/Im1 Do", Coded("/JBIG2Decode /ColorSpace /DeviceGray")).Images[0];
        Assert.Equal((ExtractedImageFormat.Jbig2, ".jbig2"), (jbig2.Format, jbig2.FileExtension));
        Assert.Equal("data"u8.ToArray(), jbig2.GetData());

        // A codec after a filter that cannot be decoded: the stored data.
        var hidden = Drawn("/Im1 Do", Coded("[/FancyDecode /DCTDecode] /ColorSpace /DeviceRGB")).Images[0];
        Assert.Equal(ExtractedImageFormat.Raw, hidden.Format);
        Assert.Equal("data"u8.ToArray(), hidden.GetData());

        // An unknown filter: the stored data.
        var unknown = Drawn("/Im1 Do", Coded("/FancyDecode /ColorSpace /DeviceGray")).Images[0];
        Assert.Equal((ExtractedImageFormat.Raw, ".bin"), (unknown.Format, unknown.FileExtension));
        Assert.Equal("data"u8.ToArray(), unknown.GetData());

        // A separation: the samples, decoded.
        var spot = Drawn("/Im1 Do", TestPdf.Stream("<0102>", "/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /Filter /ASCIIHexDecode /ColorSpace [/Separation /Gold /DeviceCMYK 6 0 R]"), more: "<< /FunctionType 2 /Domain [0 1] /N 1 /C1 [0 0.2 1 0] >>").Images[0];
        Assert.Equal("Separation", spot.ColorSpace);
        Assert.Equal(ExtractedImageFormat.Raw, spot.Format);
        Assert.Equal([1, 2], spot.GetData());
    }

    [Fact]
    public void Short_data_is_padded_and_damaged_data_skipped_with_diagnostics()
    {
        // Two of four rows: the rest is black, as viewers draw it.
        var shortImage = Drawn("/Im1 Do", Image("/Width 1 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8", 200, 100)).Images[0];
        Assert.Equal([200, 200, 200, 255, 100, 100, 100, 255, 0, 0, 0, 255, 0, 0, 0, 255], Rgba(shortImage.GetData()).Rgba);

        var page = Extract(OnePage("/Im9 Do q 10 0 0 10 0 0 cm /Im1 Do Q", "<< /XObject << /Im1 5 0 R >> >>", extra: Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", 0)));
        Assert.Single(page.Images);
        Assert.Contains(page.Diagnostics, d => d.Contains("/Im9", StringComparison.Ordinal));
    }

    [Fact]
    public void Pages_are_extracted_one_at_a_time_in_any_order()
    {
        var jpeg = PdfImage.Load(Fixtures.Read("Images/gray.jpg"));
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(jpeg, 0, 0, 10, 10);
        document.AddPage(PdfPageSize.A4);
        document.AddPage(PdfPageSize.A4).Canvas.DrawImage(jpeg, 0, 0, 10, 10).DrawImage(jpeg, 20, 0, 10, 10);
        var extractor = new ImageExtractor(PdfReader.Open(document.ToArray()));

        Assert.Equal([1, 0, 2], extractor.ExtractPages().Select(p => p.Images.Count));
        Assert.Equal([2, 0], extractor.ExtractPages([2, 0]).Select(p => p.PageIndex));
        Assert.All(extractor.ExtractPage(2).Images, i => Assert.Equal(2, i.PageIndex));
        var first = extractor.ExtractPage(0).Images[0];
        Assert.StartsWith($"{first.PixelWidth} x {first.PixelHeight} Jpeg image on page 1 at PdfRectangle {{ X = 0, Y = 0, Width = 10,", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_of_image_in_one_file_is_listed_and_exported()
    {
        // Fixtures/Extraction/images.pdf (make-images-fixture.py): also a seed of the extract fuzz target.
        var page = Extract(Fixtures.Read("Extraction/images.pdf"));

        Assert.Equal(
            ["Png", "Png", "Png", "Png", "Png", "Png", "Jpeg", "Tiff", "Jpeg2000", "Png", "Png", "Png"],
            page.Images.Select(i => i.Format.ToString()));
        Assert.Equal([true, false, false, false, true, true, false, false, false, false, true, false], page.Images.Select(i => i.HasMask)); // The turned copy of the first has its soft mask too.
        Assert.True(page.Images[9].IsStencilMask);
        Assert.True(page.Images[11].IsInline);
        AssertNear(90, page.Images[10].Rotation);
        foreach (var image in page.Images)
        {
            var data = image.GetData();
            Assert.NotEmpty(data);
            if (image.Format == ExtractedImageFormat.Png)
            {
                Assert.Equal((image.PixelWidth, image.PixelHeight), (Rgba(data).Width, Rgba(data).Height));
            }
        }
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var extractor = new ImageExtractor(PdfReader.Open(OnePage("", "<< >>")));

        Assert.Throws<ArgumentNullException>(() => new ImageExtractor(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractPage(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractPage(-1));
        Assert.Throws<ArgumentNullException>(() => extractor.ExtractPages(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.ExtractPages([3]).ToList());
        Assert.Empty(extractor.ExtractPage(0).Images);
    }
}
