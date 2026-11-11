using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Fonts;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class CffEmbeddingTests
{
    private static readonly string[] LatinLines =
    [
        "The quick brown fox jumps over the lazy dog.",
        "Zw\u00F6lf Boxk\u00E4mpfer jagen Viktor quer \u00FCber den gro\u00DFen Sylter Deich.",
        "Bl\u00E5b\u00E6rgr\u00F8d med fl\u00F8de \u2013 \u00C5ngstr\u00F6m, \u00E9t\u00E9, \u0141\u00F3d\u017A, fi fl ffi",
    ];

    // The variable font fixture is a smaller subset: it has no \u2013, \u0141 or \u017A.
    private static readonly string[] VariableLines =
    [
        "The quick brown fox jumps over the lazy dog.",
        "Zw\u00F6lf Boxk\u00E4mpfer jagen Viktor quer \u00FCber den gro\u00DFen Sylter Deich.",
        "Bl\u00E5b\u00E6rgr\u00F8d med fl\u00F8de, \u00C5ngstr\u00F6m, \u00E9t\u00E9, fi fl ffi",
    ];

    private static readonly string[] JapaneseLines =
    [
        "\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8\u3092\u57CB\u3081\u8FBC\u307F\u307E\u3059\u3002",
        "\u6771\u4EAC\u90FD\u5343\u4EE3\u7530\u533A \u4E00\u4E8C\u4E09 \u3072\u3089\u304C\u306A",
        "Text 123 \u30AB\u30BF\u30AB\u30CA",
    ];

    private static string[] LinesFor(string file) =>
        file.StartsWith("Noto", StringComparison.Ordinal) ? JapaneseLines : file.Contains("VF", StringComparison.Ordinal) ? VariableLines : LatinLines;

    private static byte[] Create(PdfFont font, string[] lines)
    {
        // Without the XMP packet (a few kilobytes in every PDF), so sizes compare the fonts.
        var doc = new PdfDocument { IncludeXmpMetadata = false };
        doc.Info.CreationDate = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var page = doc.AddPage(PdfPageSize.A4);
        for (var i = 0; i < lines.Length; i++)
        {
            page.Canvas.DrawText(lines[i], font, 20, 40, 780 - (i * 36));
        }

        return doc.ToArray();
    }

    /// <summary>The same font with OS/2 fsType "no subsetting", so it is embedded whole as /OpenType.</summary>
    private static PdfFont WholeFont(string file)
    {
        var data = Fixtures.Read(file);
        FontEmbeddingTests.SetFsType(data, 0x0100);
        return PdfFont.Load(data);
    }

    /// <remarks>
    /// PDFium hints glyphs of SFNT-wrapped fonts (the whole font, /OpenType) but not bare CFF (the subset), so edges
    /// differ by a fraction of a pixel. After a 3x3 box blur the two renders agree to within 64 gray levels (48 at most here),
    /// while a wrong or missing glyph differs by far more.
    /// </remarks>
    [Theory]
    [InlineData("RaiCoreSans3-Subset.otf")]
    [InlineData("NotoSansJP-Subset.otf")]
    [InlineData("RaiCoreSans3VF-Subset.otf")]
    public void Subset_renders_like_the_whole_font(string file)
    {
        var lines = LinesFor(file);
        var subset = Create(PdfFont.Load(Fixtures.Path(file)), lines);
        var whole = Create(WholeFont(file), lines);
        Assert.Contains("/Subtype /CIDFontType0C", Encoding.Latin1.GetString(subset), StringComparison.Ordinal);
        Assert.Contains("/Subtype /OpenType", Encoding.Latin1.GetString(whole), StringComparison.Ordinal);

        var expected = Blurred(whole, out var dark);
        var actual = Blurred(subset, out _);

        var maxDifference = expected.Zip(actual, (e, a) => Math.Abs(e - a)).Max();
        Assert.True(dark > 3000, $"Only {dark} dark pixels: the text did not render");
        Assert.True(maxDifference <= 64, $"The renders differ by up to {maxDifference} gray levels");
    }

    /// <summary>Renders page 1 in gray levels, averaged over 3x3 pixels.</summary>
    private static int[] Blurred(byte[] pdf, out int dark)
    {
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 110));
        int width = bitmap.Width, height = bitmap.Height;
        var gray = new int[width * height];
        dark = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                gray[(y * width) + x] = (c.Red + c.Green + c.Blue) / 3;
                dark += c.Red < 128 ? 1 : 0;
            }
        }

        var blurred = new int[width * height];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var sum = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        sum += gray[((y + dy) * width) + x + dx];
                    }
                }

                blurred[(y * width) + x] = sum / 9;
            }
        }

        return blurred;
    }

    [Theory]
    [InlineData("RaiCoreSans3-Subset.otf")]
    [InlineData("NotoSansJP-Subset.otf")]
    [InlineData("RaiCoreSans3VF-Subset.otf")]
    public void Subset_is_much_smaller_than_the_whole_font(string file)
    {
        var lines = LinesFor(file);
        var subset = Create(PdfFont.Load(Fixtures.Path(file)), lines);
        var whole = Create(WholeFont(file), lines);

        Assert.True(subset.Length < whole.Length / 3, $"{subset.Length} bytes with the subset, {whole.Length} with the whole font");
    }

    [Theory]
    [InlineData("RaiCoreTestCFF-Subrs.otf")]
    [InlineData("RaiCoreTestCFF2-Long.otf")]
    public void Glyphs_are_scaled_by_the_font_matrix(string file)
    {
        // 2048 units per em: "A" of the first font is a box from x = 40 to 560 and 700 high, so at 100 pt it covers
        // x = 2..27 pt and y = 0..34 pt; with the default matrix (1000 units per em) it would be twice as big.
        // The CFF2 font's "A" is a zigzag from x = 100 to 1000 and y = 0 to 400, so 5..49 pt by 20 pt.
        var font = PdfFont.Load(Fixtures.Path(file));
        var doc = new PdfDocument();
        doc.AddPage(PdfPageSize.A4).Canvas.DrawText("A", font, 100, 72, 500);
        var pdf = doc.ToArray();
        Assert.Contains("/Subtype /CIDFontType0C", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        bool Dark(double x, double y) => bitmap.GetPixel(72 + (int)x, bitmap.Height - 500 - (int)y).Red < 128;
        if (file.Contains("CFF2", StringComparison.Ordinal))
        {
            Assert.True(Enumerable.Range(6, 42).Count(x => Dark(x, 5)) > 20, "The CFF2 glyph was not drawn");
            Assert.False(Enumerable.Range(52, 40).Any(x => Dark(x, 5)), "The CFF2 glyph was drawn too wide");
            Assert.False(Enumerable.Range(6, 42).Any(x => Dark(x, 25)), "The CFF2 glyph was drawn too high");
        }
        else
        {
            Assert.True(Dark(15, 17), "The glyph was not drawn");
            Assert.False(Dark(40, 17), "The glyph was drawn too wide");
            Assert.False(Dark(15, 50), "The glyph was drawn too high");
        }
    }

    [Fact]
    public void Fonts_the_subsetter_cannot_handle_are_embedded_whole()
    {
        var font = PdfFont.Load(Fixtures.Path("RaiCoreTestCFF-Seac.otf"));
        var doc = new PdfDocument();
        doc.AddPage(PdfPageSize.A4).Canvas.DrawText("A\u00C1", font, 100, 72, 500);
        var pdf = doc.ToArray();

        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Subtype /OpenType", text, StringComparison.Ordinal);
        Assert.Contains("/BaseFont /RaiCoreTestCFF-Seac", text, StringComparison.Ordinal);

        // The accented glyph (A with the accent box above it) is drawn from the whole font.
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        var accent = bitmap.GetPixel(72 + 60 + 30, bitmap.Height - 500 - 80);
        Assert.True(accent.Red < 50, $"Expected the seac accent to be drawn, got {accent}");
    }

    [Theory]
    [InlineData("RaiCoreSans3-Subset.otf")]
    [InlineData("NotoSansJP-Subset.otf")]
    [InlineData("RaiCoreSans3VF-Subset.otf")]
    public void Text_can_be_extracted_from_cff_subsets(string file)
    {
        var lines = LinesFor(file);
        var text = ExternalTools.ExtractText(Create(PdfFont.Load(Fixtures.Path(file)), lines));

        foreach (var line in lines)
        {
            // pdftotext may separate CJK and Latin runs differently; compare without spaces.
            var expected = line.Replace(" ", string.Empty, StringComparison.Ordinal);
            Assert.Contains(expected, text.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cff_subsets_pass_qpdf_structural_check() =>
        ExternalTools.AssertQpdfClean(Create(PdfFont.Load(Fixtures.Path("NotoSansJP-Subset.otf")), JapaneseLines));
}
