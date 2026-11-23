using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Metafiles;
using RaiCore.Pdf.Reading;
using SkiaSharp;

namespace RaiCore.Pdf.Tests.Images;

/// <summary>
/// WMF, EMF and EMF+ pictures drawn as PDF vectors. The generated pictures (Fixtures/Metafiles/make-metafiles.py) are
/// 200 x 100 points with one logical unit per point, drawn at their natural size and rendered at 72 dpi, so pixel
/// (x, y) is logical (x, y); the real-world pictures come from Apache POI's test data, with POI's own expectations.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class MetafileTests
{
    private static readonly FontCollection Liberation = new FontCollection().AddFile(Fixtures.Path("LiberationSans-Regular.ttf"));

    [Fact]
    public void Wmf_shapes_are_drawn_where_their_records_put_them()
    {
        var (image, reports) = Load("shapes.wmf");
        Assert.Equal(PdfImageFormat.Wmf, image.Format);
        Assert.Equal((200.0, 100.0), (image.NaturalWidth, image.NaturalHeight));
        Assert.Empty(reports);
        using var bitmap = Render(image);
        AssertColor(bitmap, 25, 25, "#ff0000");     // Rectangle.
        AssertColor(bitmap, 8, 25, "#ffffff");
        AssertColor(bitmap, 65, 25, "#0000ff");     // Ellipse ...
        AssertColor(bitmap, 52, 12, "#ffffff");     // ... not its corner.
        AssertColor(bitmap, 105, 35, "#00a000");    // Polygon ...
        AssertColor(bitmap, 93, 12, "#ffffff");     // ... not outside it.
        var hatch = Pixels(bitmap, 131, 11, 159, 39);
        Assert.Contains(hatch, c => c.Red < 80);    // Cross hatch: lines ...
        Assert.Contains(hatch, c => c.Red > 240);   // ... and gaps (a transparent background).
        AssertColor(bitmap, 182, 25, "#000000");    // A pen 6 wide ...
        AssertColor(bitmap, 182, 31, "#ffffff");    // ... from y 22 to 28.
        AssertColor(bitmap, 172, 25, "#000000");    // Flat caps: from x 170 ...
        AssertColor(bitmap, 168, 25, "#ffffff");
        AssertColor(bitmap, 145, 70, "#ffff00");    // Clipped to x 130 to 160 ...
        AssertColor(bitmap, 125, 70, "#ffffff");
        AssertColor(bitmap, 165, 70, "#ffffff");
        AssertColor(bitmap, 185, 70, "#ff8000");    // ... which RestoreDC undoes.
        AssertColor(bitmap, 64, 88, "#ff0000");     // A 2 x 1 bitmap stretched to 20 x 15.
        AssertColor(bitmap, 76, 88, "#0000ff");
    }

    [Fact]
    public void Wmf_text_is_extractable_at_its_reference_point_with_its_advances()
    {
        var (image, _) = Load("shapes.wmf", Fonts);
        var pdf = Pdf(image);
        var metafile = Find(pdf, "Metafile");
        Assert.Equal(10, metafile.Bounds.X, 1.0);
        Assert.InRange(100 - 70, metafile.Bounds.Y, metafile.Bounds.Top);  // The baseline at y = 70, with glyphs above it.
        Assert.InRange(metafile.Bounds.Y, 100 - 70 - 5, 100 - 70);
        Assert.Equal(12, metafile.Bounds.Height, 4.0);                     // A 12-point font.
        Assert.Equal(100, Find(pdf, "B").Bounds.X, 1.0);                   // "A" at x = 60 advances 40.
        Assert.Contains("LiberationSans", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal); // The converter's font.
    }

    [Fact]
    public void Wmf_windows_arcs_alignment_escapement_and_pens_follow_the_window()
    {
        // The window is twice the picture, from (0, 40): logical (x, y) is pixel (x / 2, (y - 40) / 2).
        var (image, reports) = Load("arcs.wmf", Fonts);
        Assert.Empty(reports);
        using var bitmap = Render(image);
        AssertColor(bitmap, 70, 30, "#ff0000");     // A pie from east to north, counterclockwise: the upper right quarter.
        AssertColor(bitmap, 70, 70, "#ffffff");
        AssertColor(bitmap, 30, 30, "#ffffff");
        AssertColor(bitmap, 180, 79, "#000000");    // A pen 8 logical units wide: 4 points, y 78 to 82.
        AssertColor(bitmap, 180, 83, "#ffffff");
        AssertColor(bitmap, 180, 76, "#ffffff");
        AssertColor(bitmap, 125, 20, "#00a000");    // DIBBITBLT without a bitmap: the brush.
        var pdf = Pdf(image);
        var right = Find(pdf, "Right").Bounds;
        Assert.Equal(190, right.X + right.Width, 1.5);                     // TA_RIGHT: it ends at the reference point.
        var up = Find(pdf, "Up").Bounds;
        Assert.True(up.Height > up.Width, $"{up}");                         // Turned 90 degrees: it runs up ...
        Assert.InRange(150, up.X, up.X + up.Width);                        // ... from (150, 90), its glyphs' tops to the left.
        Assert.Equal(100 - 90, up.Y, 1.5);
    }

    [Fact]
    public void Emf_isotropic_mapping_world_multiplication_clip_intersection_and_exclusion()
    {
        var (image, reports) = Load("modes.emf");
        Assert.Empty(reports);
        using var bitmap = Render(image);
        AssertColor(bitmap, 35, 35, "#ff0000");     // MM_ISOTROPIC: 10 x 10 on 60 x 30 keeps its aspect, 30 x 30 ...
        AssertColor(bitmap, 55, 20, "#ffffff");     // ... not 60 wide.
        AssertColor(bitmap, 110, 30, "#0000ff");    // MWT_LEFTMULTIPLY: x -> 2x + 100.
        AssertColor(bitmap, 160, 25, "#00a000");    // Clips intersect: x 150 to 170 ...
        AssertColor(bitmap, 140, 25, "#ffffff");
        AssertColor(bitmap, 180, 25, "#ffffff");
        AssertColor(bitmap, 15, 55, "#ff00ff");     // An excluded rectangle ...
        AssertColor(bitmap, 30, 70, "#ffffff");     // ... is a hole.
        AssertColor(bitmap, 80, 70, "#0000ff");     // 32-bit pixels are opaque whatever their fourth byte.
        AssertColor(bitmap, 138, 60, "#00ffff");    // An excluded rectangle where y is up ...
        AssertColor(bitmap, 114, 80, "#ffffff");    // ... is a hole too.
    }

    [Fact]
    public void A_wmf_draws_the_emf_it_carries_unless_that_has_glyph_numbers()
    {
        var (image, reports) = Load("embedded.wmf", Fonts);
        Assert.Equal(PdfImageFormat.Wmf, image.Format);
        Assert.Empty(reports);
        var text = ExternalText(Pdf(image));
        Assert.Contains("Enhanced", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Wmf text", text, StringComparison.Ordinal);

        // The EMF's text is glyph numbers, which belong to the font it was made with: the WMF's own records are drawn.
        (image, reports) = Load("embedded-glyphs.wmf", Fonts);
        Assert.Empty(reports);
        Assert.Contains("Wmf text", ExternalText(Pdf(image)), StringComparison.Ordinal);
    }

    [Fact]
    public void Emf_shapes_transforms_paths_clips_and_mapping_modes_are_drawn()
    {
        var (image, reports) = Load("shapes.emf");
        Assert.Equal(PdfImageFormat.Emf, image.Format);
        Assert.Equal(200, image.NaturalWidth, 0.1);
        Assert.Equal(100, image.NaturalHeight, 0.1);
        Assert.Empty(reports);
        using var bitmap = Render(image);
        AssertColor(bitmap, 25, 25, "#ff0000");
        AssertColor(bitmap, 75, 25, "#0000ff");     // (0, 0)-(30, 10) turned by the world transform to x 70 to 80, y 10 to 40.
        AssertColor(bitmap, 66, 25, "#ffffff");
        AssertColor(bitmap, 84, 25, "#ffffff");
        AssertColor(bitmap, 105, 15, "#00a000");    // A filled path: a triangle with its point down ...
        AssertColor(bitmap, 93, 35, "#ffffff");
        AssertColor(bitmap, 145, 25, "#ff00ff");    // A square through a circular clip path ...
        AssertColor(bitmap, 132, 12, "#ffffff");    // ... not in its corners ...
        AssertColor(bitmap, 145, 7, "#ffffff");     // ... nor outside the circle.
        AssertColor(bitmap, 180, 25, "#00ffff");    // MM_ANISOTROPIC: (0, 0)-(10, 10) at 3 times, from (165, 10).
        AssertColor(bitmap, 162, 25, "#ffffff");
        AssertColor(bitmap, 179, 80, "#006400");    // MM_LOMETRIC: 10 mm up from (165, 95), to y 66.65.
        AssertColor(bitmap, 179, 63, "#ffffff");
        AssertColor(bitmap, 44, 50, "#000000");     // Dashes of 8 with gaps of 8, from x 40.
        AssertColor(bitmap, 52, 50, "#ffffff");
        AssertColor(bitmap, 60, 50, "#000000");
        AssertColor(bitmap, 155, 61, "#808080");    // ETO_OPAQUE: the text's rectangle in the background color.
        AssertColor(bitmap, 104, 84, "#0000ff");    // A bottom-up 2 x 2 bitmap: its top row is blue, white ...
        AssertColor(bitmap, 116, 84, "#ffffff");
        AssertColor(bitmap, 104, 96, "#ff0000");    // ... and its bottom row red, green.
        AssertColor(bitmap, 116, 96, "#00ff00");
    }

    [Fact]
    public void Emf_text_uses_the_records_advances()
    {
        var (image, _) = Load("shapes.emf", Fonts);
        var pdf = Pdf(image);
        var text = Find(pdf, "Enhanced");
        Assert.Equal(10, text.Bounds.X, 1.0);
        Assert.Equal(8 * 7, text.Bounds.Width, 3.0);                       // Eight characters 7 points apart.
        Assert.InRange(100 - 70, text.Bounds.Y, text.Bounds.Top);
        Assert.Equal(100, Find(pdf, "Box").Bounds.X, 1.0);
    }

    [Fact]
    public void Glyph_numbers_are_drawn_only_in_a_font_of_the_pictures_font_name()
    {
        // ETO_GLYPH_INDEX in Liberation Sans: drawn by glyph number, with the text the glyphs stand for, 10 points apart.
        var (image, reports) = Load("glyphs.emf", Fonts);
        Assert.Empty(reports);
        var pdf = Pdf(image);
        Assert.Contains("Glyphs", ExternalText(pdf).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(10, Find(pdf, "G").Bounds.X, 1.0);
        Assert.Equal(60, Find(pdf, "s").Bounds.X, 1.0);

        // In another font the numbers mean other glyphs: left out, and reported.
        (image, reports) = Load("glyphs.emf", (_, _, _) => [PdfFont.Load(Fixtures.Path("RaiCoreTestTT-Regular.ttf"))]);
        Assert.Contains(reports, r => r.Message.Contains("glyph numbers", StringComparison.Ordinal) && !r.Damaged);
        Assert.DoesNotContain("Glyphs", ExternalText(Pdf(image)), StringComparison.Ordinal);
    }

    [Fact]
    public void EmfPlus_records_are_drawn_instead_of_their_emf_duplicates()
    {
        var (image, reports) = Load("plus.emf");
        Assert.Empty(reports);
        using var bitmap = Render(image);
        AssertColor(bitmap, 20, 20, "#ff0000");     // The EMF+ fill, not the EMF records' magenta.
        var left = bitmap.GetPixel(52, 25);
        var right = bitmap.GetPixel(88, 25);
        Assert.True(left.Red > 200 && left.Blue < 60, $"left {left}");      // A linear gradient from red ...
        Assert.True(right.Blue > 200 && right.Red < 60, $"right {right}");  // ... to blue.
        AssertColor(bitmap, 130, 25, "#0000ff");    // A pen 4 wide.
        AssertColor(bitmap, 130, 29, "#ffffff");
        AssertColor(bitmap, 175, 32, "#00a000");    // A path.
        AssertColor(bitmap, 125, 65, "#80ff80", 12); // A half-transparent green ellipse through a world transform.
        AssertColor(bitmap, 175, 75, "#00ffff");    // EMF records after GetDC are drawn.
        var text = Find(Pdf(image), "Plus text").Bounds;
        Assert.Equal(10 + 2, text.X, 1.0);                     // The layout point and GDI+'s margin of an em / 6 ...
        Assert.InRange(100 - 55, text.Y, text.Top);            // ... with the top of a 12-point line at y = 55.
        Assert.InRange(100 - 55 - 12, text.Y, text.Top);
        Assert.Equal(12, text.Height, 4.0);
        Assert.True(image.HasTransparency);
    }

    [Fact]
    public void A_dual_mode_file_with_unsupported_EmfPlus_records_is_drawn_from_its_emf_records()
    {
        var (image, reports) = Load("plus-fallback.emf");
        var (message, damaged) = Assert.Single(reports);
        Assert.Contains("drawn from its EMF records", message, StringComparison.Ordinal);
        Assert.False(damaged);
        using var bitmap = Render(image);
        AssertColor(bitmap, 20, 20, "#ff00ff");
        AssertColor(bitmap, 70, 25, "#ffffff");     // No EMF+ gradient.
        AssertColor(bitmap, 175, 75, "#00ffff");
    }

    [Fact]
    public void An_EmfPlus_only_file_skips_its_emf_records()
    {
        var (image, _) = Load("plus-only.emf");
        using var bitmap = Render(image);
        AssertColor(bitmap, 20, 20, "#ff0000");
        AssertColor(bitmap, 60, 20, "#ffffff");
    }

    [Fact]
    public void Nested_pictures_are_drawn_to_a_depth_of_four()
    {
        var (image, reports) = Load("nested-2.emf");
        Assert.Empty(reports);
        using (var bitmap = Render(image))
        {
            AssertColor(bitmap, 100, 50, "#ff0000");
        }

        (image, reports) = Load("nested-6.emf");
        Assert.Contains(reports, r => r.Message.Contains("nested more deeply", StringComparison.Ordinal));
        using (var bitmap = Render(image))
        {
            AssertColor(bitmap, 100, 50, "#ffffff");
        }
    }

    [Fact]
    public void Damaged_pictures_are_drawn_up_to_the_damage()
    {
        var emf = Fixtures.Read(Path.Combine("Metafiles", "shapes.emf"));
        var (image, reports) = Load(emf[..900]);
        Assert.Contains(reports, r => r.Damaged && r.Message.Contains("damaged", StringComparison.Ordinal));
        using (var bitmap = Render(image))
        {
            AssertColor(bitmap, 25, 25, "#ff0000");
            AssertColor(bitmap, 104, 84, "#ffffff");    // The bitmap was after the damage.
        }

        var wmf = Fixtures.Read(Path.Combine("Metafiles", "shapes.wmf"));
        (image, reports) = Load(wmf[..300]);
        Assert.Contains(reports, r => r.Damaged);
        using (var bitmap = Render(image))
        {
            AssertColor(bitmap, 25, 25, "#ff0000");
        }

        // A polygon claiming more points than its record holds.
        var polygon = wmf.ToArray();
        var at = IndexOf(polygon, [0x24, 0x03, 0x03, 0x00]);
        polygon[at + 2] = 0xFF;
        polygon[at + 3] = 0x7F;
        (_, reports) = Load(polygon);
        Assert.Contains(reports, r => r.Damaged && r.Message.Contains("more points", StringComparison.Ordinal));
    }

    [Fact]
    public void Headers_that_are_not_pictures_throw_image_format_exceptions()
    {
        Assert.Throws<ImageFormatException>(() => PdfImage.Load([0xD7, 0xCD, 0xC6, 0x9A, 0, 0]));
        var emf = Fixtures.Read(Path.Combine("Metafiles", "shapes.emf"));
        Assert.Throws<ImageFormatException>(() => PdfImage.Load(emf[..60].Concat(new byte[40]).ToArray()));
        var frameless = emf.ToArray();
        Array.Clear(frameless, 8, 32); // No bounds and no frame.
        Assert.Throws<ImageFormatException>(() => PdfImage.Load(frameless));
    }

    [Fact]
    public void Limits_cut_pictures_short_and_leave_out_large_bitmaps()
    {
        var emf = Fixtures.Read(Path.Combine("Metafiles", "shapes.emf"));
        using (new ResourceLimits { MaxElements = 30 }.Enforce(TestContext.Current.CancellationToken))
        {
            var (image, reports) = Load(emf);
            Assert.Contains(reports, r => r.Damaged && r.Message.Contains("MaxElements", StringComparison.Ordinal));
            using var bitmap = Render(image);
            AssertColor(bitmap, 25, 25, "#ff0000");
            AssertColor(bitmap, 104, 84, "#ffffff");
        }

        using (new ResourceLimits { MaxImagePixels = 3 }.Enforce(TestContext.Current.CancellationToken))
        {
            var (image, reports) = Load(emf);
            Assert.Contains(reports, r => r.Damaged && r.Message.Contains("MaxImagePixels", StringComparison.Ordinal));
            using var bitmap = Render(image);
            AssertColor(bitmap, 145, 25, "#ff00ff");
            AssertColor(bitmap, 104, 84, "#ffffff");
        }
    }

    [Fact]
    public void Mutated_pictures_never_throw_anything_but_image_format_exceptions()
    {
        var random = new Random(266);
        string[] files = ["shapes.wmf", "shapes.emf", "plus.emf", "nested-2.emf"];
        foreach (var file in files)
        {
            var original = Fixtures.Read(Path.Combine("Metafiles", file));
            for (var i = 0; i < 150; i++)
            {
                var data = original.ToArray();
                for (var k = random.Next(1, 8); k > 0; k--)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }

                try
                {
                    Pdf(PdfImage.LoadMetafile(data, new MetafileOptions()));
                }
                catch (ImageFormatException)
                {
                }
            }
        }
    }

    [Fact]
    public void Text_in_symbol_and_double_byte_character_sets_is_decoded()
    {
        Assert.Equal("\u03B1\u2264\u00D7", SymbolFont.Map("a\u00A3\u00B4", "Symbol"));
        Assert.Equal("\uF06C", SymbolFont.Map("l", "Wingdings"));
        var font = new GdiFont("MS Gothic", -12, 0, 0, 400, false, false, false, 128);
        Assert.Equal("\u65E5\u672C", font.Encoding.GetString([0x93, 0xFA, 0x96, 0x7B]));
        Assert.Equal([30.0, 7.0], EmfPlayer.PerChar([10, 20, 7], font.Encoding, "\u65E5A"));
    }

    [Fact]
    public void Poi_pictures_have_the_records_and_text_poi_finds()
    {
        // TestHwmfParsing: the record counts (without the end record).
        foreach (var (file, count) in new[] { ("santa.wmf", 581), ("empty-polygon-close.wmf", 272), ("file-45.wmf", 1315) })
        {
            var data = Poi(file);
            Assert.Equal(count, WmfPlayer.CountRecords(data, WmfHeader.Read(data)));
        }

        // TestHemfPicture: the text of the EMF pictures.
        var windows = ExternalText(Pdf(Load(Poi("SimpleEMF_windows.emf"), Fonts).Image));
        Assert.Contains("C:\\Users\\tallison\\\n", windows, StringComparison.Ordinal);
        Assert.Contains("asf2-git-1.x\\tika-", windows, StringComparison.Ordinal); // POI's lines; later ones lie outside the frame.
        var mac = ExternalText(Pdf(Load(Poi("SimpleEMF_mac.emf"), Fonts).Image));
        Assert.Contains("Tika http://incubator.apache.org", mac, StringComparison.Ordinal);
        Assert.Contains("LatestNews", mac.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal); // One record per letter, spaced.
        var header = EmfHeader.Read(Poi("SimpleEMF_windows.emf"));
        Assert.Equal(3, header.Handles);
        Assert.Equal(31, header.Records);

        // 60677.wmf (Shift_JIS text): its embedded EMF stores glyph numbers, so its WMF records are drawn.
        var japanese = new FontCollection().AddFile(Fixtures.Path("NotoSansJP-Subset.otf"));
        var (shiftJis, reports) = Load(Poi("60677.wmf"), (_, _, _) => [japanese.Find("Noto Sans JP")!]);
        Assert.Empty(reports);
        Assert.Contains("\u3078\u306E", ExternalText(Pdf(shiftJis)), StringComparison.Ordinal);
    }

    [Fact]
    public void Poi_pictures_in_pictures_and_damaged_pictures_are_drawn()
    {
        // nested_wmf.emf: an EMF+ image of a WMF (with two bytes of padding after its placeable header).
        var (nested, reports) = Load(Poi("nested_wmf.emf"));
        Assert.DoesNotContain(reports, r => r.Damaged);
        var content = Encoding.Latin1.GetString(Pdf(nested));
        Assert.True(System.Text.RegularExpressions.Regex.Count(content, "/Subtype /Form") >= 2);

        // POI's damaged and fuzzed pictures: drawn up to the damage, or rejected as not pictures.
        string[] damaged = ["61338.wmf", "61294.emf", "clusterfuzz-testcase-minimized-6701721724125184.wmf"];
        foreach (var file in damaged)
        {
            Assert.Contains(Load(Poi(file)).Reports, r => r.Damaged);
        }

        string[] rejected = ["bug69924.wmf", "clusterfuzz-testcase-minimized-POIFileHandlerFuzzer-6060921738035200.wmf",
            "clusterfuzz-testcase-minimized-POIFileHandlerFuzzer-6466833057382400.emf", "clusterfuzz-testcase-minimized-POIFileHandlerFuzzer-5318384433823744.emf"];
        foreach (var file in rejected)
        {
            Assert.Throws<ImageFormatException>(() => PdfImage.Load(Poi(file)));
        }
    }

    [Fact]
    public void Office_blips_are_decompressed_and_given_a_placeable_header()
    {
        var wmf = Fixtures.Read(Path.Combine("Metafiles", "shapes.wmf"))[22..];
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(wmf);
        }

        var header = new byte[34];
        BitConverter.GetBytes(wmf.Length).CopyTo(header, 0);
        BitConverter.GetBytes(2 * 914400).CopyTo(header, 20);  // 2 inches wide.
        BitConverter.GetBytes(914400).CopyTo(header, 24);
        var file = MetafileBlip.Decode([.. header, .. compressed.ToArray()], wmf: true, 1 << 20)!;
        var image = PdfImage.Load(file);
        Assert.Equal((144.0, 72.0), (image.NaturalWidth, image.NaturalHeight)); // The window 200 x 100 in 2 x 1 inches.
        Assert.Throws<ResourceLimitExceededException>(() => MetafileBlip.Decode([.. header, .. compressed.ToArray()], wmf: true, 100));
        Assert.Null(MetafileBlip.Decode([.. header, 1, 2, 3], wmf: true, 1 << 20));
    }

    [Fact]
    public void Pictures_load_through_the_public_api()
    {
        var image = PdfImage.Load(Fixtures.Read(Path.Combine("Metafiles", "shapes.emf")));
        Assert.Equal((267, 133), (image.PixelWidth, image.PixelHeight)); // CSS pixels.
        Assert.True(image.HasTransparency);
        Assert.Equal(1, PdfImage.GetPageCount(Fixtures.Read(Path.Combine("Metafiles", "shapes.wmf"))));
        var pdf = Pdf(image);
        Assert.Contains("Enhanced", ExternalText(pdf), StringComparison.Ordinal); // In a standard font.
        Assert.Contains("/Helvetica", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    // ----- Helpers -----

    private static IReadOnlyList<PdfFont> Fonts(string face, bool bold, bool italic) => [Liberation.Find("Liberation Sans")!];

    private static byte[] Poi(string name) => Fixtures.Read(Path.Combine("Poi", name));

    private static (PdfImage Image, List<(string Message, bool Damaged)> Reports) Load(string name, Func<string, bool, bool, IReadOnlyList<PdfFont>>? fonts = null) =>
        Load(Fixtures.Read(Path.Combine("Metafiles", name)), fonts);

    private static (PdfImage Image, List<(string Message, bool Damaged)> Reports) Load(byte[] data, Func<string, bool, bool, IReadOnlyList<PdfFont>>? fonts = null)
    {
        var reports = new List<(string, bool)>();
        var image = PdfImage.LoadMetafile(data, new MetafileOptions { Fonts = fonts, Report = (message, damaged) => reports.Add((message, damaged)) });
        return (image, reports);
    }

    /// <summary>A PDF with the picture at its natural size on a page of that size.</summary>
    private static byte[] Pdf(PdfImage image)
    {
        var document = new PdfDocument { CompressContent = false };
        document.AddPage(new PdfPageSize(image.NaturalWidth, image.NaturalHeight)).Canvas.DrawImage(image, 0, 0, image.NaturalWidth, image.NaturalHeight);
        return document.ToArray();
    }

    private static SKBitmap Render(PdfImage image) => Conversion.ToImage(Pdf(image), page: 0, options: new RenderOptions(Dpi: 72));

    private static TextMatch Find(byte[] pdf, string text) => new TextExtractor(PdfReader.Open(pdf)).FindOnPage(0, text)[0];

    private static string ExternalText(byte[] pdf) => new TextExtractor(PdfReader.Open(pdf)).ExtractText();

    private static List<SKColor> Pixels(SKBitmap bitmap, int left, int top, int right, int bottom)
    {
        var pixels = new List<SKColor>();
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                pixels.Add(bitmap.GetPixel(x, y));
            }
        }

        return pixels;
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                return i;
            }
        }

        throw new InvalidOperationException("Pattern not found.");
    }

    private static void AssertColor(SKBitmap bitmap, int x, int y, string expected, int tolerance = 8)
    {
        var e = SKColor.Parse(expected);
        var actual = bitmap.GetPixel(x, y);
        Assert.True(
            Math.Abs(e.Red - actual.Red) <= tolerance && Math.Abs(e.Green - actual.Green) <= tolerance && Math.Abs(e.Blue - actual.Blue) <= tolerance,
            $"({x}, {y}): expected {expected}, got {actual}");
    }
}
