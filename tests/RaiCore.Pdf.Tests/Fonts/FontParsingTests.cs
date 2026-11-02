using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;

namespace RaiCore.Pdf.Tests.Fonts;

public class FontParsingTests
{
    [Fact]
    public void Reads_names_and_metrics_from_truetype()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("LiberationSans-Regular.ttf"));

        Assert.Equal("Liberation Sans", font.FamilyName);
        Assert.Equal("LiberationSans", font.PostScriptName);
        Assert.Equal(OutlineFormat.TrueType, font.Outlines);
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.Equal(2620, font.NumGlyphs);
        Assert.Equal(400, font.WeightClass);
        Assert.False(font.IsItalic);
        Assert.False(font.IsFixedPitch);
        Assert.NotEqual(0, font.GetGlyphId('A'));
        Assert.Equal(0, font.GetGlyphId(0x1F600)); // No emoji in Liberation.
    }

    [Theory]
    [InlineData("LiberationSans-Bold.ttf", 700, false)]
    [InlineData("LiberationSans-Italic.ttf", 400, true)]
    [InlineData("LiberationMono-Regular.ttf", 400, false)]
    public void Reads_style_flags(string file, int weight, bool italic)
    {
        var font = PdfFont.Load(Fixtures.Path(file));

        Assert.Equal(weight, font.Weight);
        Assert.Equal(italic, font.IsItalic);
        Assert.Equal(file.StartsWith("LiberationMono", StringComparison.Ordinal), font.IsMonospaced);
    }

    [Theory]
    [InlineData("LiberationSans-Regular.woff")]
    [InlineData("LiberationSans-Regular.woff2")]
    public void Web_fonts_decode_to_the_same_font_as_the_truetype_original(string file)
    {
        var original = OpenTypeFont.Load(Fixtures.Read("LiberationSans-Regular.ttf"));
        var web = OpenTypeFont.Load(Fixtures.Read(file));

        Assert.Equal(original.FamilyName, web.FamilyName);
        Assert.Equal(original.NumGlyphs, web.NumGlyphs);
        Assert.Equal(original.Ascender, web.Ascender);
        Assert.Equal(original.Descender, web.Descender);
        Assert.Equal(original.GetMappedCodePoints().Order(), web.GetMappedCodePoints().Order());
        for (var gid = 0; gid < original.NumGlyphs; gid++)
        {
            Assert.Equal(original.GetAdvanceWidth(gid), web.GetAdvanceWidth(gid));
            Assert.Equal(original.GetLeftSideBearing(gid), web.GetLeftSideBearing(gid));
        }

        // Every reconstructed glyph has the same contour count and bounding box. (WOFF2 re-encodes the
        // point data, so bytes differ; tools/README.md describes the full outline comparison against fontTools.)
        var originalLoca = TrueTypeSubsetter.ReadLoca(original);
        var webLoca = TrueTypeSubsetter.ReadLoca(web);
        for (var gid = 0; gid < original.NumGlyphs; gid++)
        {
            var a = original.GetTable("glyf")[originalLoca[gid]..originalLoca[gid + 1]];
            var b = web.GetTable("glyf")[webLoca[gid]..webLoca[gid + 1]];
            Assert.Equal(a.IsEmpty, b.IsEmpty);
            if (!a.IsEmpty)
            {
                Assert.True(a[..10].SequenceEqual(b[..10]), $"Glyph {gid} header differs");
            }
        }
    }

    [Fact]
    public void Selects_faces_in_a_collection()
    {
        var data = Fixtures.Read("RaiCoreTestTT.ttc");

        Assert.Equal(2, PdfFont.GetFaceCount(data));
        var regular = PdfFont.Load(data, faceIndex: 0);
        var bold = PdfFont.Load(data, faceIndex: 1);
        Assert.Equal("RaiCoreTestTT-Regular", regular.PostScriptName);
        Assert.Equal("RaiCoreTestTT-Bold", bold.PostScriptName);
        Assert.Equal(700, bold.Weight);
        Assert.Equal(0.68, bold.MeasureText("A", 1), 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfFont.Load(data, faceIndex: 2));
    }

    [Fact]
    public void Reads_cff_based_opentype()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreTestCFF-Regular.otf"));

        Assert.Equal(OutlineFormat.Cff, font.Outlines);
        Assert.Equal("RaiCore Test CFF", font.FamilyName);
        Assert.Equal(600, font.GetAdvanceWidth(font.GetGlyphId('A')));
        Assert.Equal(700, font.CapHeight);
    }

    [Fact]
    public void Exposes_metrics_as_fractions_of_the_font_size()
    {
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

        Assert.Equal(0.905, font.Ascent, 3);
        Assert.Equal(-0.212, font.Descent, 3);
        Assert.Equal(0.688, font.CapHeight, 3);
        Assert.Equal(11.5, font.GetLineHeight(10), 1);
        Assert.True(font.HasGlyphs("Za\u017C\u00F3\u0142\u0107 g\u0119\u015Bl\u0105 ja\u017A\u0144 \u03A9\u03BC\u03AD\u03B3\u03B1 \u041F\u0440\u0438\u0432\u0435\u0442"));
        Assert.False(font.HasGlyphs("\u65E5\u672C"));
    }

    [Fact]
    public void Measures_text_like_helvetica()
    {
        // Liberation Sans is metric-compatible with Helvetica: H e l l o = 722 + 556 + 222 + 222 + 556 = 2278 / 1000 em.
        // Liberation uses 2048 units per em, so widths agree to within rounding.
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));

        Assert.InRange(font.MeasureText("Hello", 10), 22.77, 22.79);
    }

    [Fact]
    public void Rejects_data_that_is_not_a_font()
    {
        Assert.Throws<FontFormatException>(() => PdfFont.Load("%PDF-1.7 this is not a font"u8.ToArray()));
        Assert.Throws<FontFormatException>(() => PdfFont.Load(new byte[4]));
    }

    [Theory]
    [InlineData("RaiCoreTestTT-Regular.ttf")]
    [InlineData("RaiCoreTestCFF-Regular.otf")]
    [InlineData("RaiCoreTestTT.ttc")]
    [InlineData("LiberationSans-Regular.woff2")]
    public void Truncated_fonts_fail_with_a_font_format_error(string file)
    {
        var data = Fixtures.Read(file);
        for (var length = 0; length < data.Length; length += Math.Max(1, data.Length / 200))
        {
            var truncated = data[..length];
            var ex = Record.Exception(() =>
            {
                var font = OpenTypeFont.Load(truncated);
                _ = font.GetGlyphId('A');
            });

            Assert.True(ex is null or FontFormatException, $"Length {length}: {ex?.GetType().Name}: {ex?.Message}");
        }
    }
}
