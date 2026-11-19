using System.Buffers.Binary;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Images;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>
/// TIFF structure: directories, field types, layouts and the checks on JPEG strips, on files built byte by byte or
/// fixtures with one field or byte changed.
/// </summary>
public partial class ImageTests
{
    [Fact]
    public void A_bigtiff_header_must_say_8_byte_offsets()
    {
        var data = TiffFixture("rgb8-bigtiff-lzw.tif");
        data[4] = 4;
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("Invalid BigTIFF header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_first_directory_inside_the_header_is_rejected()
    {
        var data = TiffFixture("rgb8-lzw-predictor.tif");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 4);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.GetPageCount(data));
        Assert.Contains("first image file directory is outside the file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_link_to_the_next_page_ends_the_pages()
    {
        var data = TiffFixture("multipage.tif");
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ifd));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(ifd + 2 + (count * 12)), 0xFFFFFFF0);
        Assert.Equal(1, PdfImage.GetPageCount(data));
        Assert.Equal(40, PdfImage.Load(data, 0).PixelWidth);
    }

    [Fact]
    public void Fields_of_unknown_types_are_ignored()
    {
        var data = TiffFixture("rgb8-lzw-predictor.tif");
        SetTiffTagType(data, 256, 99);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("The TIFF has no tag 256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_of_duplicate_fields_counts()
    {
        var data = TiffBuilder.Gray8(2, 1).Strips([10, 20]).Add(256, 4, 5).Build();
        var image = PdfImage.Load(data);
        Assert.Equal(2, image.PixelWidth);
        Assert.Equal([10, 20], image.Image.Data);
    }

    [Theory]
    [InlineData(256, 8)] // SSHORT
    [InlineData(257, 6)] // SBYTE
    [InlineData(256, 9)] // SLONG
    public void Signed_field_values_are_read_as_signed(ushort tag, ushort type)
    {
        var data = TiffBuilder.Gray8(2, 1).Strips([10, 20]).Tag(tag, type, -1).Build();
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("TIFF size must be positive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bits_per_sample_beyond_the_samples_are_ignored()
    {
        var data = TiffBuilder.Gray8(1, 1).Tag(277, 3, 3).Tag(262, 3, 2).Tag(258, 3, 8, 8, 8, 16).Strips([1, 2, 3]).Build();
        Assert.Equal([1, 2, 3], PdfImage.Load(data).Image.Data);
    }

    [Fact]
    public void Samples_of_different_depths_are_rejected()
    {
        var data = TiffBuilder.Gray8(1, 1).Tag(277, 3, 3).Tag(262, 3, 2).Tag(258, 3, 8, 8, 16).Strips([1, 2, 3, 4]).Build();
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("different bit depths", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, new long[] { 300, 2 }, new long[] { 300, 2 }, 150, 150)] // RATIONAL with a denominator.
    [InlineData(3, new long[] { 100 }, new long[] { 200 }, 100, 200)] // SHORT.
    [InlineData(5, new long[0], new long[] { 200, 1 }, 200, 200)] // Only a vertical resolution: used for both.
    [InlineData(5, new long[] { 200, 1 }, new long[0], 200, 200)] // Only a horizontal one.
    [InlineData(5, new long[] { 1, 2 }, new long[] { 1, 2 }, 96, 96)] // Half a pixel per inch: ignored.
    [InlineData(5, new long[] { 1, 2 }, new long[] { 200, 1 }, 96, 96)] // Ignored if either is out of range.
    [InlineData(5, new long[] { 200, 1 }, new long[] { 1, 2 }, 96, 96)]
    public void Resolution_fields_of_every_kind_set_the_natural_size(ushort type, long[] x, long[] y, double dpiX, double dpiY)
    {
        var builder = TiffBuilder.Gray8(4, 2).Strips(new byte[8]);
        if (x.Length > 0)
        {
            builder.Tag(282, type, x);
        }

        if (y.Length > 0)
        {
            builder.Tag(283, type, y);
        }

        var image = PdfImage.Load(builder.Build());
        Assert.Equal(4 * 72 / dpiX, image.NaturalWidth, 6);
        Assert.Equal(2 * 72 / dpiY, image.NaturalHeight, 6);
    }

    [Fact]
    public void Jpeg_tables_may_be_bytes_or_undefined()
    {
        var data = TiffFixture("rgb8-jpeg.tif");
        SetTiffTagType(data, 347, 1);
        Assert.Equal(PdfImage.Load(TiffFixture("rgb8-jpeg.tif")).Image.Data, PdfImage.Load(data).Image.Data);
    }

    [Fact]
    public void A_fax_without_a_photometric_interpretation_is_white_is_zero()
    {
        var data = TiffFixture("fax-g4.tif");
        ReplaceTiffTag(data, 262, 65000, 0);
        Assert.False(PdfImage.Load(data).Image.Ccitt!.BlackIs1);
    }

    [Fact]
    public void An_invalid_orientation_is_upright()
    {
        var data = TiffFixture("gray8-150x75dpi.tif");
        ReplaceTiffTag(data, 305, 274, 9);
        var image = PdfImage.Load(data);
        Assert.Equal((1, 40), (image.Image.Orientation, image.PixelWidth));
    }

    [Fact]
    public void A_tile_length_alone_makes_a_tiled_image()
    {
        var data = TiffFixture("rgb8-deflate-tiled.tif");
        ReplaceTiffTag(data, 322, 65000, 0);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("The TIFF has no tag 322", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_per_strip_beyond_the_image_mean_one_strip()
    {
        var data = TiffFixture("gray8-150x75dpi.tif");
        PatchTiffTag(data, 278, 2_000_000_000);
        Assert.Equal(PdfImage.Load(TiffFixture("gray8-150x75dpi.tif")).Image.Data, PdfImage.Load(data).Image.Data);
    }

    [Fact]
    public void Empty_strips_are_left_black_wherever_their_offset_points()
    {
        var data = TiffBuilder.Gray8(2, 4).Tag(278, 4, 2).Tag(273, 4, 8, 999_999).Tag(279, 4, 4, 0).Strips([1, 2, 3, 4]).Build();
        Assert.Equal([1, 2, 3, 4, 0, 0, 0, 0], PdfImage.Load(data).Image.Data);
    }

    [Fact]
    public void A_strip_starting_at_the_end_of_the_file_is_outside_it()
    {
        var data = TiffFixture("rgb8-lzw-predictor.tif");
        PatchTiffTag(data, 273, (uint)data.Length);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("strip 0 is outside the file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tiles_must_start_on_a_byte()
    {
        var data = TiffBuilder.Gray8(20, 8).Tag(258, 3, 1).Tag(322, 4, 12).Tag(323, 4, 8).Strips(new byte[16], new byte[16]).Build();
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("do not end on a byte boundary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_too_large_for_an_array_is_rejected()
    {
        var data = TiffFixture("rgb8-lzw-predictor.tif");
        PatchTiffTag(data, 256, 40_000);
        PatchTiffTag(data, 257, 25_000);
        PatchTiffTag(data, 278, 25_000);
        using ((ResourceLimits.Default with { MaxImagePixels = 1_000_000_000 }).Enforce(TestContext.Current.CancellationToken))
        {
            var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
            Assert.Contains("too large to decode", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Too_little_data_for_the_size_is_rejected_before_decoding()
    {
        var data = TiffFixture("gray8-150x75dpi.tif"); // Uncompressed.
        PatchTiffTag(data, 256, 1000);
        PatchTiffTag(data, 257, 1000);
        PatchTiffTag(data, 278, 1000);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("is too short for its 1000000 bytes of samples", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_order_does_not_apply_to_lzw()
    {
        var data = TiffFixture("rgb8-lzw-predictor.tif");
        ReplaceTiffTag(data, 305, 266, 2);
        Assert.Equal(PdfImage.Load(TiffFixture("rgb8-lzw-predictor.tif")).Image.Data, PdfImage.Load(data).Image.Data);
    }

    [Fact]
    public void A_palette_image_with_associated_alpha_keeps_its_indices()
    {
        var map = new long[3 * 256];
        map[5] = map[256 + 5] = map[512 + 5] = 65535;
        var data = TiffBuilder.Gray8(1, 1).Tag(262, 3, 3).Tag(277, 3, 2).Tag(258, 3, 8, 8).Tag(338, 3, 1).Tag(320, 3, map).Strips([5, 128]).Build();
        var image = PdfImage.Load(data).Image;
        Assert.Equal([5], image.Data);
        Assert.Equal([128], image.Alpha);
    }

    [Fact]
    public void Extra_samples_without_a_kind_are_not_alpha()
    {
        var data = TiffFixture("rgb8-extra-unspecified.tif");
        ReplaceTiffTag(data, 338, 65000, 0);
        Assert.False(PdfImage.Load(data).HasTransparency);
    }

    [Fact]
    public void Color_map_values_are_clamped_to_16_bits()
    {
        var data = TiffBuilder.Gray8(2, 1).Tag(258, 3, 1).Tag(262, 3, 3).Tag(320, 4, 70_000, 256, 70_000, 256, 70_000, 256).Strips([0x40]).Build();
        Assert.Equal([255, 255, 255, 1, 1, 1], PdfImage.Load(data).Image.Palette);
    }

    [Fact]
    public void A_short_color_map_is_rejected()
    {
        var data = TiffBuilder.Gray8(2, 1).Tag(258, 3, 1).Tag(262, 3, 3).Tag(320, 3, 1, 2, 3, 4, 5).Strips([0x40]).Build();
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("no complete color map", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackBits_skips_its_no_op_code()
    {
        var data = TiffBuilder.Gray8(7, 1).Tag(259, 3, 32773).Strips([0x80, 0x03, 1, 2, 3, 4, 0x80, 0xFE, 9]).Build();
        Assert.Equal([1, 2, 3, 4, 9, 9, 9], PdfImage.Load(data).Image.Data);
    }

    [Fact]
    public void Old_style_lzw_is_told_apart_from_lzw_starting_with_a_zero_byte()
    {
        // 9-bit codes 0 (a literal zero) and 257 (end): 00000000 0|1000000 01...
        var lzw = TiffBuilder.Gray8(1, 1).Tag(259, 3, 5).Strips([0x00, 0x40, 0x40]).Build();
        Assert.Equal([0], PdfImage.Load(lzw).Image.Data);

        var old = TiffBuilder.Gray8(1, 1).Tag(259, 3, 5).Strips([0x00, 0x01, 0x00]).Build();
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(old));
        Assert.Contains("Old-style TIFF LZW", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lzw_decoding_stops_at_the_length_needed()
    {
        var data = TiffFixture("rgb8-lzw-predictor.tif");
        var strip = data.AsSpan(240, 1737).ToArray();
        Assert.Equal(40 * 28 * 3, StreamDecoder.Lzw(strip, 1).Length);

        // Stopping early also keeps the work within the decompression limit.
        using ((ResourceLimits.Default with { MaxDecompressedBytes = 1000 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(100, StreamDecoder.Lzw(strip, 1, maxLength: 100).Length);
        }
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(128, false)]
    public void A_fax_may_be_one_tile_as_wide_as_the_image(int tileWidth, bool supported)
    {
        var fax = TiffFixture("fax-g4.tif");
        var data = new TiffBuilder().Tag(256, 4, 120).Tag(257, 4, 64).Tag(258, 3, 1).Tag(259, 3, 4).Tag(262, 3, 0)
            .Tag(322, 4, tileWidth).Tag(323, 4, 64).Strips(fax.AsSpan(8, (int)TiffValues(fax, 279)[0]).ToArray()).Build();
        if (supported)
        {
            Assert.Equal(ImageEncoding.CcittFax, PdfImage.Load(data).Image.Encoding);
        }
        else
        {
            var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
            Assert.Contains("one tile as wide as the image", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_fax_without_data_is_rejected()
    {
        var data = TiffFixture("fax-g4.tif");
        PatchTiffTag(data, 279, 0);
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains("fax image has no data", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rgb-progressive.jpg", false, "progressive or lossless")]
    [InlineData("rgb.jpg", true, "restart intervals")]
    public void Jpeg_strips_that_cannot_be_joined_are_rejected(string file, bool restartInterval, string message)
    {
        var jpeg = File.ReadAllBytes(Fixtures.Path(Path.Combine("Images", file)));
        if (restartInterval)
        {
            jpeg = [0xFF, 0xD8, 0xFF, 0xDD, 0x00, 0x04, 0x00, 0x10, .. jpeg.AsSpan(2)];
        }

        var data = new TiffBuilder().Tag(256, 4, 64).Tag(257, 4, 96).Tag(258, 3, 8, 8, 8).Tag(259, 3, 7).Tag(262, 3, 6).Tag(277, 3, 3)
            .Tag(278, 4, 48).Strips(jpeg, jpeg).Build();
        var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Changes to rgb8-jpeg-strips.tif (Pillow: 3 strips, each SOI, SOF0 at 2, SOS at 21, data from 35).</summary>
    [Theory]
    [InlineData(2, 7, new byte[] { 0x00, 0x20 }, "does not match the strip's size")] // The last strip 32 rows high.
    [InlineData(1, 27, new byte[] { 0x11 }, "different JPEG headers")] // Another Huffman table in the scan header.
    [InlineData(1, 6, new byte[] { 0x0C }, "different JPEG headers")] // 12-bit samples in the frame header.
    [InlineData(1, 40, new byte[] { 0xFF, 0xC4 }, "several scans")] // A marker inside the data.
    [InlineData(0, 3, new byte[] { 0xD9 }, "has no image")] // End of image where the frame header was.
    [InlineData(0, 3, new byte[] { 0xE0 }, "has no frame header")] // The frame header turned into an APP0 segment.
    [InlineData(0, 4, new byte[] { 0x00, 0x08 }, "frame header of a TIFF strip is damaged")] // Too short for its components.
    [InlineData(1, 40, new byte[] { 0xFF, 0xD0 }, null)] // A restart marker inside the data does not end it.
    public void Jpeg_strips_are_checked_before_they_are_joined(int strip, int at, byte[] bytes, string? message)
    {
        var data = TiffFixture("rgb8-jpeg-strips.tif");
        bytes.CopyTo(data.AsSpan((int)TiffValues(data, 273)[strip] + at));
        if (message is null)
        {
            Assert.Equal(ImageEncoding.Jpeg, PdfImage.Load(data).Image.Encoding);
        }
        else
        {
            var ex = Assert.Throws<ImageFormatException>(() => PdfImage.Load(data));
            Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_single_component_is_joined_in_8_row_blocks_whatever_its_sampling()
    {
        // Sampling factors 2 x 2 on the only component: its blocks are still 8 x 8, so 8-row strips can be joined.
        var data = TiffFixture("gray8-jpeg-strips.tif");
        foreach (var offset in TiffValues(data, 273))
        {
            data[offset + 13] = 0x22;
        }

        var jpeg = JpegReader.Read(PdfImage.Load(data).Image.Data);
        Assert.Equal((48, 40), (jpeg.Width, jpeg.Height));
    }

    [Theory]
    [InlineData("fax-g4.tif")]
    [InlineData("fax-g4-miniswhite.tif")]
    [InlineData("fax-g3-1d.tif")]
    public void Fax_images_come_back_from_image_extraction_unchanged(string file)
    {
        // RaiCore.Pdf.Extraction writes /CCITTFaxDecode images as TIFF files with its own reading of /BlackIs1.
        var image = PdfImage.Load(Fixtures.Path(Path.Combine(TiffFolder, file)));
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawImage(image, 10, 10, 120, 64);
        var extracted = new ImageExtractor(PdfReader.Open(doc.ToArray())).ExtractPage(0).Images[0];
        Assert.Equal(ExtractedImageFormat.Tiff, extracted.Format);

        var again = PdfImage.Load(extracted.GetData()).Image;
        Assert.Equal(image.Image.Data, again.Data);
        Assert.Equal(image.Image.Ccitt!.K < 0, again.Ccitt!.K < 0);
        Assert.Equal(image.Image.Ccitt.BlackIs1, again.Ccitt.BlackIs1);
    }

    private static byte[] TiffFixture(string file) => File.ReadAllBytes(Fixtures.Path(Path.Combine(TiffFolder, file)));

    /// <summary>The first directory's entry for <paramref name="tag"/> in a little-endian classic TIFF.</summary>
    private static int TiffEntry(byte[] data, ushort tag)
    {
        Assert.Equal((byte)'I', data[0]);
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ifd));
        var entry = Enumerable.Range(0, count).Select(i => ifd + 2 + (i * 12)).FirstOrDefault(e => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(e)) == tag, -1);
        Assert.True(entry >= 0, $"The fixture has no tag {tag}.");
        return entry;
    }

    /// <summary>The SHORT or LONG values of a tag (little-endian classic TIFF).</summary>
    private static int[] TiffValues(byte[] data, ushort tag)
    {
        var entry = TiffEntry(data, tag);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry + 2));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 4));
        var size = type == 3 ? 2 : 4;
        var at = count * size <= 4 ? entry + 8 : (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8));
        return [.. Enumerable.Range(0, count).Select(i => size == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + (i * 2)))
            : (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + (i * 4))))];
    }

    private static void SetTiffTagType(byte[] data, ushort tag, ushort type) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(TiffEntry(data, tag) + 2), type);

    /// <summary>Turns the entry of <paramref name="victim"/> into a SHORT <paramref name="tag"/> with one value.</summary>
    private static void ReplaceTiffTag(byte[] data, ushort victim, ushort tag, ushort value)
    {
        var entry = TiffEntry(data, victim);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry + 2), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 8), value);
    }

    /// <summary>
    /// Writes a little-endian classic TIFF with one directory: the given fields, and the strips (or tiles, with a
    /// TileWidth) after the header, whose offsets and byte counts are added unless given.
    /// </summary>
    private sealed class TiffBuilder
    {
        private readonly List<(ushort Tag, ushort Type, long[] Values)> _fields = [];
        private byte[][] _strips = [];

        /// <summary>An 8-bit grayscale image in one uncompressed strip.</summary>
        public static TiffBuilder Gray8(int width, int height) => new TiffBuilder()
            .Tag(256, 4, width).Tag(257, 4, height).Tag(258, 3, 8).Tag(259, 3, 1).Tag(262, 3, 1).Tag(277, 3, 1).Tag(278, 4, height);

        /// <summary>Sets a field (RATIONAL values are numerator and denominator pairs).</summary>
        public TiffBuilder Tag(ushort tag, ushort type, params long[] values)
        {
            _fields.RemoveAll(f => f.Tag == tag);
            return Add(tag, type, values);
        }

        /// <summary>Adds a field after any with the same tag.</summary>
        public TiffBuilder Add(ushort tag, ushort type, params long[] values)
        {
            _fields.Add((tag, type, values));
            return this;
        }

        public TiffBuilder Strips(params byte[][] strips)
        {
            _strips = strips;
            return this;
        }

        public byte[] Build()
        {
            var file = new List<byte> { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0 };
            var offsets = new List<long>();
            foreach (var strip in _strips)
            {
                offsets.Add(file.Count);
                file.AddRange(strip);
            }

            var tiled = _fields.Any(f => f.Tag == 322);
            if (!_fields.Any(f => f.Tag is 273 or 324))
            {
                Add(tiled ? (ushort)324 : (ushort)273, 4, [.. offsets]);
            }

            if (!_fields.Any(f => f.Tag is 279 or 325))
            {
                Add(tiled ? (ushort)325 : (ushort)279, 4, [.. _strips.Select(s => (long)s.Length)]);
            }

            if (file.Count % 2 != 0)
            {
                file.Add(0);
            }

            var ifd = file.Count;
            BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(file).Slice(4), (uint)ifd);
            var fields = _fields.OrderBy(f => f.Tag).ToList(); // Stable: duplicates keep their order.
            var external = new List<byte>();
            var directory = new List<byte>();
            directory.AddRange(BitConverter.GetBytes((ushort)fields.Count));
            foreach (var (tag, type, values) in fields)
            {
                var size = type switch { 1 or 6 => 1, 3 or 8 => 2, 5 => 4, _ => 4 };
                var bytes = values.SelectMany(v => size switch
                {
                    1 => [(byte)v],
                    2 => BitConverter.GetBytes((ushort)v),
                    _ => BitConverter.GetBytes((uint)v),
                }).ToArray();
                var count = type == 5 ? values.Length / 2 : values.Length;
                directory.AddRange(BitConverter.GetBytes(tag));
                directory.AddRange(BitConverter.GetBytes(type));
                directory.AddRange(BitConverter.GetBytes((uint)count));
                if (bytes.Length <= 4)
                {
                    directory.AddRange([.. bytes, .. new byte[4 - bytes.Length]]);
                }
                else
                {
                    directory.AddRange(BitConverter.GetBytes((uint)(ifd + 2 + (fields.Count * 12) + 4 + external.Count)));
                    external.AddRange(bytes);
                }
            }

            directory.AddRange(new byte[4]); // No next directory.
            file.AddRange(directory);
            file.AddRange(external);
            return [.. file];
        }
    }
}
