using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Tests.Reading;

namespace RaiCore.Pdf.Tests;

/// <summary>Resource limits outside HTML conversion: decoders, font parsing, the PDF reader, layout and the scopes.</summary>
public class ResourceLimitsTests
{
    private static readonly TextStyle Text = new(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);

    [Fact]
    public void The_defaults_are_generous_but_finite()
    {
        var limits = ResourceLimits.Default;

        Assert.Equal(256, limits.MaxNestingDepth);
        Assert.Equal(1_000_000, limits.MaxElements);
        Assert.Equal(2_000_000, limits.MaxTableCells);
        Assert.Equal(100_000_000, limits.MaxImagePixels);
        Assert.Equal(64L * 1024 * 1024, limits.MaxResourceBytes);
        Assert.Equal(64L * 1024 * 1024, limits.MaxFontBytes);
        Assert.Equal(256L * 1024 * 1024, limits.MaxDecompressedBytes);
        Assert.Equal(10_000, limits.MaxPages);
        Assert.Equal(TimeSpan.FromMinutes(5), limits.Timeout);
    }

    [Fact]
    public void Scopes_nest_and_restore_the_limits_before_them()
    {
        var outer = ResourceLimits.Default with { MaxPages = 7 };
        var inner = ResourceLimits.Default with { MaxPages = 3 };

        Assert.Same(ResourceLimits.Default, ResourceLimits.Current);
        using (outer.Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Same(outer, ResourceLimits.Current);
            using (inner.Enforce(TestContext.Current.CancellationToken))
            {
                Assert.Same(inner, ResourceLimits.Current);
            }

            Assert.Same(outer, ResourceLimits.Current);
        }

        Assert.Same(ResourceLimits.Default, ResourceLimits.Current);
    }

    [Fact]
    public void Disposing_scopes_out_of_order_does_not_resurrect_them()
    {
        var outer = (ResourceLimits.Default with { MaxPages = 7 }).Enforce(TestContext.Current.CancellationToken);
        var inner = (ResourceLimits.Default with { MaxPages = 3 }).Enforce(TestContext.Current.CancellationToken);
        outer.Dispose(); // Out of order: the inner scope stays current until it is disposed too.
        Assert.Equal(3, ResourceLimits.Current.MaxPages);
        inner.Dispose();
        Assert.Equal(7, ResourceLimits.Current.MaxPages); // It restores what was current when it began.
    }

    [Theory]
    [InlineData(nameof(ResourceLimits.MaxNestingDepth))]
    [InlineData(nameof(ResourceLimits.MaxPages))]
    [InlineData(nameof(ResourceLimits.MaxImagePixels))]
    [InlineData(nameof(ResourceLimits.Timeout))]
    public void Rejects_limits_that_are_not_positive(string name)
    {
        var limits = name switch
        {
            nameof(ResourceLimits.MaxNestingDepth) => ResourceLimits.Default with { MaxNestingDepth = 0 },
            nameof(ResourceLimits.MaxPages) => ResourceLimits.Default with { MaxPages = -1 },
            nameof(ResourceLimits.MaxImagePixels) => ResourceLimits.Default with { MaxImagePixels = 0 },
            _ => ResourceLimits.Default with { Timeout = TimeSpan.Zero },
        };

        Assert.Throws<ArgumentException>(() => limits.Enforce(TestContext.Current.CancellationToken));
        using ((ResourceLimits.Default with { Timeout = Timeout.InfiniteTimeSpan }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, ResourceLimits.Current.Timeout);
        }
    }

    [Fact]
    public void A_png_with_too_many_pixels_is_rejected_before_decoding()
    {
        var png = Png(100_000, 100_000, Zlib(new byte[16]));

        var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfImage.Load(png));
        Assert.Equal(nameof(ResourceLimits.MaxImagePixels), ex.LimitName);
        Assert.Equal(100_000_000, ex.Limit);
    }

    [Fact]
    public void The_pixel_limit_comes_from_the_scope()
    {
        var png = Png(20, 20, Zlib(new byte[20 * 21]));
        Assert.Equal(20, PdfImage.Load(png).PixelWidth);

        using ((ResourceLimits.Default with { MaxImagePixels = 399 }).Enforce(TestContext.Current.CancellationToken))
        {
            var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfImage.Load(png));
            Assert.Equal(399, ex.Limit);
        }

        using ((ResourceLimits.Default with { MaxImagePixels = 400 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(20, PdfImage.Load(png).PixelHeight);
        }
    }

    [Fact]
    public void A_png_whose_data_inflates_far_beyond_its_size_is_not_inflated_in_full()
    {
        // A 1x1 gray image needs 2 bytes (filter + sample); the data inflates to 64 MB of zeros.
        var bomb = Zlib(new byte[64 * 1024 * 1024]);
        var png = Png(1, 1, bomb, colorType: 0);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var image = PdfImage.Load(png);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, image.PixelWidth);
        Assert.True(allocated < 8 * 1024 * 1024, $"Decoding allocated {allocated} bytes.");
    }

    [Fact]
    public void A_gif_with_a_huge_logical_screen_is_rejected()
    {
        // 65535 x 65535 overflowed the screen buffer's size before.
        var gif = new List<byte>("GIF89a"u8.ToArray());
        gif.AddRange([0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0, 0]); // Screen size, global palette of 2 colors.
        gif.AddRange([0, 0, 0, 255, 255, 255]);
        gif.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]); // A 1x1 frame.
        gif.AddRange([2, 2, 0x44, 0x01, 0, 0x3B]);

        var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfImage.Load([.. gif]));
        Assert.Equal(nameof(ResourceLimits.MaxImagePixels), ex.LimitName);
    }

    [Fact]
    public void Bmp_jpeg_and_webp_headers_with_too_many_pixels_are_rejected()
    {
        var bmp = new byte[64];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 20_000);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 20_000);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0xFF, 0xFF, 0xFF, 0xFF, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xFF, 0xD9];

        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(4), 22);
        "WEBPVP8X"u8.CopyTo(webp.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16), 10);
        webp[24] = webp[25] = 0xFF; webp[26] = 0x3F; // Canvas width 16384.
        webp[27] = webp[28] = 0xFF; webp[29] = 0x3F; // Canvas height 16384.

        foreach (var data in new[] { bmp, jpeg, webp })
        {
            var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfImage.Load(data));
            Assert.Equal(nameof(ResourceLimits.MaxImagePixels), ex.LimitName);
        }
    }

    [Fact]
    public void A_woff_declaring_gigabytes_of_tables_is_rejected_before_inflating()
    {
        var woff = new byte[44 + 20 + 8];
        "wOFF"u8.CopyTo(woff);
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(8), (uint)woff.Length);
        BinaryPrimitives.WriteUInt16BigEndian(woff.AsSpan(12), 1);
        "glyf"u8.CopyTo(woff.AsSpan(44));
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(48), 64); // offset
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(52), 8); // compLength
        BinaryPrimitives.WriteUInt32BigEndian(woff.AsSpan(56), int.MaxValue); // origLength

        var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfFont.Load(woff));
        Assert.Equal(nameof(ResourceLimits.MaxFontBytes), ex.LimitName);
    }

    [Fact]
    public void A_woff2_declaring_gigabytes_of_tables_is_rejected_before_decompressing()
    {
        var woff2 = new List<byte>(new byte[48]);
        "wOF2"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(woff2));
        var header = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(woff2);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header[12..], 1); // numTables
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], 4); // totalCompressedSize
        woff2.AddRange([0x00, 0x84, 0x80, 0x80, 0x80, 0x00]); // cmap, untransformed, origLength 1 GB (UIntBase128).
        woff2.AddRange([0, 0, 0, 0]);

        var ex = Assert.Throws<ResourceLimitExceededException>(() => PdfFont.Load([.. woff2]));
        Assert.Equal(nameof(ResourceLimits.MaxFontBytes), ex.LimitName);
    }

    [Fact]
    public void The_real_woff_fixtures_load_within_the_font_limit()
    {
        Assert.NotNull(PdfFont.Load(Fixtures.Read("LiberationSans-Regular.woff")));
        Assert.NotNull(PdfFont.Load(Fixtures.Read("LiberationSans-Regular.woff2")));
        using ((ResourceLimits.Default with { MaxFontBytes = 1000 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Throws<ResourceLimitExceededException>(() => PdfFont.Load(Fixtures.Read("LiberationSans-Regular.woff")));
            Assert.Throws<ResourceLimitExceededException>(() => PdfFont.Load(Fixtures.Read("LiberationSans-Regular.woff2")));
        }
    }

    [Fact]
    public void A_pdf_stream_that_decompresses_beyond_the_limit_is_rejected()
    {
        var content = new string(' ', 2 * 1024 * 1024) + "0 0 m";
        var compressed = Encoding.Latin1.GetString(Zlib(Encoding.Latin1.GetBytes(content)));
        var pdf = TestPdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            TestPdf.Stream(compressed, "/Filter /FlateDecode"),
        ]);

        var page = PdfReader.Open(pdf).Pages[0];
        Assert.Equal(content.Length, page.GetContent().Length);
        using ((ResourceLimits.Default with { MaxDecompressedBytes = 1024 * 1024 }).Enforce(TestContext.Current.CancellationToken))
        {
            var ex = Assert.Throws<ResourceLimitExceededException>(() => page.GetContent());
            Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), ex.LimitName);
            Assert.Equal(1024 * 1024, ex.Limit);
        }
    }

    [Fact]
    public void Page_content_split_into_streams_counts_them_together()
    {
        var part = new string(' ', 600 * 1024) + "0 0 m";
        var pdf = TestPdf.Build(
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents [4 0 R 5 0 R] >>",
            TestPdf.Stream(part),
            TestPdf.Stream(part),
        ]);

        var page = PdfReader.Open(pdf).Pages[0];
        using ((ResourceLimits.Default with { MaxDecompressedBytes = 1024 * 1024 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Throws<ResourceLimitExceededException>(() => page.GetContent());
        }

        using ((ResourceLimits.Default with { MaxDecompressedBytes = 2 * 1024 * 1024 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal((2 * part.Length) + 2, page.GetContent().Length);
        }
    }

    [Fact]
    public void Pagination_stops_at_the_page_limit()
    {
        var flow = Pages(20);
        Assert.Equal(20, flow.CountPages());

        using ((ResourceLimits.Default with { MaxPages = 19 }).Enforce(TestContext.Current.CancellationToken))
        {
            var ex = Assert.Throws<ResourceLimitExceededException>(() => flow.Render());
            Assert.Equal(nameof(ResourceLimits.MaxPages), ex.LimitName);
            Assert.Equal(19, ex.Limit);
        }

        using ((ResourceLimits.Default with { MaxPages = 20 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(20, flow.Render().Pages.Count);
        }
    }

    [Fact]
    public void Pagination_is_canceled_between_pages()
    {
        using var cancel = new CancellationTokenSource();
        var flow = Pages(100);
        var resolved = 0;
        flow.PageSetupResolver = page =>
        {
            if (++resolved == 3)
            {
                cancel.Cancel(); // While the third page is being laid out.
            }

            return flow.Setup;
        };

        using (ResourceLimits.Default.Enforce(cancel.Token))
        {
            Assert.Throws<OperationCanceledException>(() => flow.Render());
        }

        Assert.InRange(resolved, 3, 5);
    }

    [Fact]
    public void Rendering_is_canceled_between_pages()
    {
        // Laid out first, then drawn page by page: cancellation is checked while drawing too.
        using var cancel = new CancellationTokenSource();
        var flow = Pages(100);
        var drawn = 0;
        flow.PageDecorator = (_, page) =>
        {
            if (++drawn == 3)
            {
                cancel.Cancel();
            }
        };

        using (ResourceLimits.Default.Enforce(cancel.Token))
        {
            Assert.Throws<OperationCanceledException>(() => flow.Render());
        }

        Assert.Equal(3, drawn);
    }

    [Fact]
    public void A_long_paragraph_is_stopped_by_the_time_limit_while_it_is_broken_into_lines()
    {
        // One paragraph laid out in one call: only the checks between its lines can stop it.
        var paragraph = new Paragraph(Text).Add(string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 20_000)));

        var started = System.Diagnostics.Stopwatch.StartNew();
        using ((ResourceLimits.Default with { Timeout = TimeSpan.FromMilliseconds(1) }).Enforce(TestContext.Current.CancellationToken))
        {
            Thread.Sleep(5);
            var ex = Assert.Throws<ResourceLimitExceededException>(() => paragraph.Layout(300));
            Assert.Equal(nameof(ResourceLimits.Timeout), ex.LimitName);
            Assert.Equal(1, ex.Limit);
        }

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"Took {started.Elapsed}.");
    }

    [Fact]
    public void Blocks_nested_deeper_than_twice_the_depth_limit_are_rejected_before_layout()
    {
        var flow = new FlowDocument(PageSetup.A4).Add(Nested(5000));

        var ex = Assert.Throws<ResourceLimitExceededException>(() => flow.Render());
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), ex.LimitName);
        Assert.Throws<ResourceLimitExceededException>(() => FlowDocument.MeasureBlocks([Nested(5000)], 100));
        using ((ResourceLimits.Default with { MaxNestingDepth = 2 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, new FlowDocument(PageSetup.A4).Add(Nested(3)).CountPages()); // Four levels: twice the limit.
            Assert.Throws<ResourceLimitExceededException>(() => new FlowDocument(PageSetup.A4).Add(Nested(4)).CountPages());
        }
    }

    [Fact]
    public void Deep_nesting_on_a_small_stack_stops_with_an_exception_instead_of_a_stack_overflow()
    {
        var flow = new FlowDocument(PageSetup.A4).Add(Nested(450));
        Exception? error = null;
        var thread = new Thread(
            () =>
            {
                using ((ResourceLimits.Default with { MaxNestingDepth = 1000 }).Enforce(TestContext.Current.CancellationToken))
                {
                    error = Record.Exception(() => flow.Render());
                }
            },
            256 * 1024);
        thread.Start();
        thread.Join();

        var ex = Assert.IsType<ResourceLimitExceededException>(error);
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), ex.LimitName);
        Assert.Contains("stack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_grid_larger_than_the_cell_limit_is_rejected()
    {
        var table = new TableBlock();
        for (var r = 0; r < 10; r++)
        {
            table.Add(new TableRow(new TableCell(new Paragraph(Text).Add("x")) { ColumnSpan = 10 }));
        }

        var flow = new FlowDocument(PageSetup.A4).Add(table);
        Assert.Equal(1, flow.CountPages());
        using ((ResourceLimits.Default with { MaxTableCells = 99 }).Enforce(TestContext.Current.CancellationToken))
        {
            var ex = Assert.Throws<ResourceLimitExceededException>(() => flow.CountPages());
            Assert.Equal(nameof(ResourceLimits.MaxTableCells), ex.LimitName);
        }

        using ((ResourceLimits.Default with { MaxTableCells = 100 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, flow.CountPages());
        }
    }

    [Fact]
    public void Overlapping_spans_count_every_slot_they_cover()
    {
        // A row span running down column 1 and column spans across it in every later row: the cells overlap, so the
        // slots they cover (1 + 10 + 9 x 10 = 101) add up beyond the 10 x 10 grid.
        var table = new TableBlock();
        table.Add(new TableRow(new TableCell(), new TableCell() { RowSpan = 10 }));
        for (var r = 1; r < 10; r++)
        {
            table.Add(new TableRow(new TableCell() { ColumnSpan = 10 }));
        }

        using ((ResourceLimits.Default with { MaxTableCells = 101 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, new FlowDocument(PageSetup.A4).Add(table).CountPages());
        }

        using ((ResourceLimits.Default with { MaxTableCells = 100 }).Enforce(TestContext.Current.CancellationToken))
        {
            Assert.Throws<ResourceLimitExceededException>(() => new FlowDocument(PageSetup.A4).Add(table).CountPages());
        }
    }

    /// <summary>A paragraph inside <paramref name="levels"/> nested containers.</summary>
    private static Block Nested(int levels)
    {
        Block block = new ParagraphBlock(new Paragraph(Text).Add("deep"));
        for (var i = 0; i < levels; i++)
        {
            block = new ContainerBlock(new BoxStyle { Padding = Thickness.All(0.01) }, block);
        }

        return block;
    }

    private static FlowDocument Pages(int count)
    {
        var flow = new FlowDocument(PageSetup.A4);
        for (var i = 0; i < count; i++)
        {
            flow.Add(new ParagraphBlock(new Paragraph(Text).Add($"Page {i + 1}"), new BoxStyle { BreakAfter = PageBreak.Page }));
        }

        return flow;
    }

    internal static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>A PNG with an IHDR of the given size (8-bit, gray or RGB) and one IDAT chunk.</summary>
    internal static byte[] Png(int width, int height, byte[] idat, int colorType = 0)
    {
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = (byte)colorType;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        png.Write(length);
        var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray();
        png.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
