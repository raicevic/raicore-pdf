using System.Text;
using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Layout;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class TextShapingTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly PdfFont Arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));

    [Fact]
    public void Applies_kerning()
    {
        var kerned = TextShaper.Shape("AV", Sans);
        var unkerned = TextShaper.Shape("AV", Sans, new ShapingOptions { Features = ["-kern"] });

        Assert.True(kerned.Advance < unkerned.Advance, $"{kerned.Advance} should be less than {unkerned.Advance}");
        Assert.Equal(Sans.MeasureText("AV", 1), unkerned.Advance, 6);
    }

    [Fact]
    public void Applies_requested_ligatures_and_keeps_their_text()
    {
        // Liberation's only discretionary ligature is Hebrew alef + lamed.
        const string alefLamed = "\u05D0\u05DC";
        var plain = TextShaper.Shape(alefLamed, Sans);
        var ligature = TextShaper.Shape(alefLamed, Sans, new ShapingOptions { Features = ["dlig"] });

        Assert.Equal(2, plain.Runs[0].Glyphs.Count);
        var glyph = Assert.Single(ligature.Runs[0].Glyphs);
        Assert.Equal(alefLamed, glyph.Text);
    }

    [Fact]
    public void Combining_marks_take_the_width_of_the_precomposed_letter()
    {
        var decomposed = TextShaper.Shape("é", Sans);
        var precomposed = TextShaper.Shape("é", Sans);

        Assert.Equal(precomposed.Advance, decomposed.Advance, 3);
        Assert.Single(decomposed.Runs);
    }

    [Fact]
    public void Joins_arabic_letters()
    {
        var shaped = TextShaper.Shape("ببب", Arabic);
        var run = Assert.Single(shaped.Runs);
        var isolated = ((OpenTypePdfFont)Arabic).Font.GetGlyphId('ب');

        Assert.Equal("Arab", run.Script);
        Assert.Equal(TextDirection.RightToLeft, run.Direction);

        // Noto draws the dot as a separate mark (zero advance), so look at the letter bodies only:
        // final, medial and initial forms (visual order), none of them the isolated form.
        var letters = run.Glyphs.Where(g => g.Advance > 0).ToList();
        Assert.Equal(3, letters.Count);
        Assert.All(letters, g => Assert.NotEqual(isolated, g.GlyphId));
        Assert.Equal(3, letters.Select(g => g.GlyphId).Distinct().Count());
        Assert.True(run.NeedsActualText);
    }

    [Fact]
    public void Right_to_left_glyphs_come_in_visual_order()
    {
        var run = Assert.Single(TextShaper.Shape("שלום", Sans).Runs); // Hebrew "shalom"

        Assert.Equal("Hebr", run.Script);
        Assert.Equal(TextDirection.RightToLeft, run.Direction);
        Assert.Equal([3, 2, 1, 0], run.Clusters);
    }

    [Fact]
    public void Falls_back_per_character_and_keeps_spaces_with_the_current_font()
    {
        var shaped = TextShaper.Shape("Hello سلام 1", [Sans, Arabic]);

        Assert.Collection(
            shaped.Runs,
            r =>
            {
                Assert.Equal("Hello ", r.Text);
                Assert.Same(Sans, r.Font);
                Assert.Equal("Latn", r.Script);
            },
            r =>
            {
                Assert.Equal("سلام 1", r.Text);
                Assert.Same(Arabic, r.Font);
                Assert.Equal(TextDirection.RightToLeft, r.Direction);
            });
    }

    [Fact]
    public void Characters_no_font_has_use_the_missing_glyph_of_the_first_font()
    {
        var run = Assert.Single(TextShaper.Shape("日本", [Sans, Arabic]).Runs);

        Assert.Same(Sans, run.Font);
        Assert.All(run.Glyphs, g => Assert.Equal(0, g.GlyphId));
    }

    [Fact]
    public void Standard_fonts_cannot_be_shaped() =>
        Assert.Throws<ArgumentException>(() => TextShaper.Shape("x", PdfFont.Standard(StandardFont.Helvetica)));

    [Fact]
    public void Shaping_is_thread_safe()
    {
        const string text = "AVATAR Waterway ببب";
        var expected = Signature(TextShaper.Shape(text, [Sans, Arabic]));

        var results = Enumerable.Range(0, 64).AsParallel().Select(_ => Signature(TextShaper.Shape(text, [Sans, Arabic]))).ToList();

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Fact]
    public void Shaping_survives_garbage_collection_moving_memory()
    {
        // Regression test: HarfBuzz reads font data in place, so it must not live in movable GC memory.
        var font = PdfFont.Load(Fixtures.Read("NotoSansArabic-Subset.ttf"));
        const string text = "\u0633\u0644\u0627\u0645 \u0628\u0628\u0628";
        var expected = Signature(TextShaper.Shape(text, font));

        for (var i = 0; i < 5; i++)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            // Fill the freed memory with junk so a dangling pointer would read garbage.
            var junk = new List<byte[]>();
            for (var n = 0; n < 20_000; n++)
            {
                var block = new byte[64 + (n % 64)];
                Array.Fill(block, (byte)0xA5);
                junk.Add(block);
            }

            Assert.Equal(expected, Signature(TextShaper.Shape(text, font)));
            GC.KeepAlive(junk);
        }
    }

    [Fact]
    public void Kerning_is_written_as_tj_adjustments()
    {
        var doc = new PdfDocument { CompressContent = false };
        doc.AddPage().Canvas.DrawText(TextShaper.Shape("AV", Sans), 20, 72, 700);
        var pdf = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Matches(@"\[<[0-9A-F]{4}> -?\d+(\.\d+)? <[0-9A-F]{4}>\] TJ", pdf);
    }

    [Fact]
    public void Shaped_pdf_is_valid_extractable_and_renders()
    {
        var doc = new PdfDocument();
        var canvas = doc.AddPage(PdfPageSize.A4).Canvas;
        canvas.DrawText(TextShaper.Shape("AVATAR WAVE Tokyo", Sans), 24, 72, 760);
        canvas.DrawText(TextShaper.Shape("سلام عليكم", Arabic), 24, 72, 700);
        canvas.DrawText(TextShaper.Shape("שלום", Sans), 24, 72, 640);
        canvas.DrawText(TextShaper.Shape("Café crème", Sans), 24, 72, 580);
        var pdf = doc.ToArray();

        ExternalTools.AssertQpdfClean(pdf);

        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));
        foreach (var baseline in new[] { 760, 700, 640, 580 })
        {
            Assert.True(CountDark(bitmap, 72, bitmap.Height - baseline - 20, 250, 26) > 100, $"Line at {baseline} did not render");
        }

        var text = ExternalTools.ExtractText(pdf);
        Assert.Contains("AVATAR WAVE Tokyo", text, StringComparison.Ordinal);
        Assert.Contains("سلام", text, StringComparison.Ordinal);
        Assert.Contains("שלום", text, StringComparison.Ordinal);
        Assert.Contains("Café", text.Normalize(NormalizationForm.FormD), StringComparison.Ordinal);
    }

    [Fact]
    public void Drawing_glyphs_requires_an_embedded_font() =>
        Assert.Throws<NotSupportedException>(() =>
            new PdfDocument().AddPage().Canvas.DrawGlyphs([new PositionedGlyph(1, 0.5)], PdfFont.Standard(StandardFont.Courier), 12, 0, 0));

    private static string Signature(ShapedText shaped) =>
        string.Join("|", shaped.Runs.SelectMany(r => r.Glyphs).Select(g => $"{g.GlyphId}:{g.Advance:F5}:{g.OffsetX:F5}:{g.OffsetY:F5}"));

    private static int CountDark(SkiaSharp.SKBitmap bitmap, int x, int y, int width, int height)
    {
        var count = 0;
        for (var px = x; px < x + width; px++)
        {
            for (var py = y; py < y + height; py++)
            {
                if (bitmap.GetPixel(px, py).Red < 128)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
