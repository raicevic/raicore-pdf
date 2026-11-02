using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using PDFtoImage;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Tests.Fonts;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public partial class FontEmbeddingTests
{
    private static readonly string[] Lines =
    [
        "Blåbærgrød med fløde – 25 €",
        "Zażółć gęślą jaźń",
        "Καλημέρα κόσμε",
        "Съешь же ещё этих мягких французских булок",
    ];

    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

    private static byte[] CreateMultilingual(PdfFont font)
    {
        var doc = new PdfDocument();
        doc.Info.CreationDate = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var page = doc.AddPage(PdfPageSize.A4);
        for (var i = 0; i < Lines.Length; i++)
        {
            page.Canvas.DrawText(Lines[i], font, 18, 72, 760 - (i * 40));
        }

        return doc.ToArray();
    }

    [Fact]
    public void Embeds_a_subset_as_a_cid_font_with_tounicode()
    {
        var pdf = Encoding.Latin1.GetString(CreateMultilingual(Sans));

        Assert.Contains("/Subtype /Type0", pdf, StringComparison.Ordinal);
        Assert.Contains("/Encoding /Identity-H", pdf, StringComparison.Ordinal);
        Assert.Contains("/Subtype /CIDFontType2", pdf, StringComparison.Ordinal);
        Assert.Contains("/CIDToGIDMap /Identity", pdf, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", pdf, StringComparison.Ordinal);
        Assert.Contains("/ToUnicode", pdf, StringComparison.Ordinal);
        Assert.Matches(SubsetNameRegex(), pdf);
    }

    [Fact]
    public void Subsetting_keeps_files_small()
    {
        var pdf = CreateMultilingual(Sans);

        // The full font is 410 KB; the subset for four lines of text is a few KB.
        Assert.True(pdf.Length < 30_000, $"PDF is {pdf.Length} bytes");
    }

    [Fact]
    public void Output_is_deterministic() => Assert.Equal(CreateMultilingual(Sans), CreateMultilingual(Sans));

    [Fact]
    public void Passes_qpdf_structural_check() => ExternalTools.AssertQpdfClean(CreateMultilingual(Sans));

    [Fact]
    public void Text_can_be_extracted_in_every_script()
    {
        var text = ExternalTools.ExtractText(CreateMultilingual(Sans));

        foreach (var line in Lines)
        {
            Assert.Contains(line, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("LiberationSans-Regular.ttf")]
    [InlineData("LiberationSans-Regular.woff2")]
    [InlineData("LiberationSerif-BoldItalic.ttf")]
    public void Pdfium_renders_every_line(string file)
    {
        var pdf = CreateMultilingual(PdfFont.Load(Fixtures.Path(file)));

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        for (var i = 0; i < Lines.Length; i++)
        {
            var baseline = bitmap.Height - (760 - (i * 40));
            Assert.True(CountDarkPixels(bitmap, 72, baseline - 16, 300, 20) > 150, $"Line {i} ('{Lines[i]}') did not render");
        }
    }

    [Fact]
    public void Embeds_cff_fonts_as_opentype()
    {
        var font = PdfFont.Load(Fixtures.Path("RaiCoreTestCFF-Regular.otf"));
        var doc = new PdfDocument();
        doc.AddPage(PdfPageSize.A4).Canvas.DrawText("ABAB", font, 100, 72, 500);
        var pdf = doc.ToArray();

        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Subtype /CIDFontType0", text, StringComparison.Ordinal);
        Assert.Contains("/FontFile3", text, StringComparison.Ordinal);
        Assert.Contains("/Subtype /OpenType", text, StringComparison.Ordinal);

        // "A" is a filled box from x = 4 to 56 at 100 pt (width 600, inset 40), 70 pt tall.
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        var inside = bitmap.GetPixel(72 + 30, bitmap.Height - 535);
        Assert.True(inside.Red < 50, $"Expected the CFF glyph to be drawn, got {inside}");
    }

    [Fact]
    public void A_font_used_on_several_pages_is_embedded_once_with_all_glyphs()
    {
        var doc = new PdfDocument();
        doc.AddPage().Canvas.DrawText("abc", Sans, 12, 72, 700);
        doc.AddPage().Canvas.DrawText("xyz", Sans, 12, 72, 700);
        var pdf = doc.ToArray();

        Assert.Single(Regex.Matches(Encoding.Latin1.GetString(pdf), "/FontFile2"));
        var text = ExternalTools.ExtractText(pdf);
        Assert.Contains("abc", text, StringComparison.Ordinal);
        Assert.Contains("xyz", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_and_embedded_fonts_mix_on_one_page()
    {
        var doc = new PdfDocument();
        doc.AddPage().Canvas
            .DrawText("Standard", StandardFont.Helvetica, 12, 72, 700)
            .DrawText("Embedded ż", Sans, 12, 72, 680);

        ExternalTools.AssertQpdfClean(doc.ToArray());
    }

    [Fact]
    public void Fonts_whose_license_forbids_embedding_are_rejected()
    {
        var data = Fixtures.Read("RaiCoreTestTT-Regular.ttf");
        SetFsType(data, 0x0002); // Restricted License embedding.
        var font = PdfFont.Load(data);

        var ex = Assert.Throws<InvalidOperationException>(() => new PdfDocument().AddPage().Canvas.DrawText("A", font, 12, 0, 0));
        Assert.Contains("does not permit embedding", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0.6, font.MeasureText("A", 1), 3); // Measuring is still allowed.
    }

    private static void SetFsType(byte[] font, ushort fsType)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + (i * 16);
            if (Encoding.ASCII.GetString(font, record, 4) == "OS/2")
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
                BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(offset + 8), fsType);
                return;
            }
        }

        throw new InvalidOperationException("No OS/2 table.");
    }

    private static int CountDarkPixels(SkiaSharp.SKBitmap bitmap, int x, int y, int width, int height)
    {
        var count = 0;
        for (var px = x; px < x + width; px++)
        {
            for (var py = y; py < y + height; py++)
            {
                var c = bitmap.GetPixel(px, py);
                if (c.Red < 128)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [GeneratedRegex(@"/BaseFont /[A-Z]{6}\+LiberationSans\b")]
    private static partial Regex SubsetNameRegex();
}
