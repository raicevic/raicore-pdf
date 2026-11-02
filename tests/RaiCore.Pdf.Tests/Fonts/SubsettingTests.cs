using RaiCore.Pdf.Fonts.OpenType;

namespace RaiCore.Pdf.Tests.Fonts;

public class SubsettingTests
{
    [Fact]
    public void Subset_keeps_used_glyphs_and_is_much_smaller()
    {
        var data = Fixtures.Read("LiberationSans-Regular.ttf");
        var font = OpenTypeFont.Load(data);
        int[] used = [.. "Hello, World".Select(c => (int)font.GetGlyphId(c))];

        var subset = OpenTypeFont.Load(TrueTypeSubsetter.Subset(font, used));

        Assert.True(subset.Data.Length < data.Length / 20, $"Subset is {subset.Data.Length} bytes");
        Assert.Equal(used.Max() + 1, subset.NumGlyphs);
        foreach (var gid in used)
        {
            Assert.Equal(font.GetAdvanceWidth(gid), subset.GetAdvanceWidth(gid));
            Assert.Equal(font.GetLeftSideBearing(gid), subset.GetLeftSideBearing(gid));
            Assert.Equal(GlyphBytes(font, gid), GlyphBytes(subset, gid));
        }

        // Glyphs that were not requested are emptied.
        var unused = Enumerable.Range(1, used.Max()).First(g => !used.Contains(g) && GlyphBytes(font, g).Length > 0);
        Assert.Empty(GlyphBytes(subset, unused));
    }

    [Fact]
    public void Subset_includes_the_components_of_composite_glyphs()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreTestTT-Regular.ttf"));
        int agrave = font.GetGlyphId('À');
        int a = font.GetGlyphId('A');
        int grave = font.GetGlyphId('`');

        var subset = OpenTypeFont.Load(TrueTypeSubsetter.Subset(font, [agrave]));

        Assert.NotEmpty(GlyphBytes(subset, agrave));
        Assert.NotEmpty(GlyphBytes(subset, a));
        Assert.NotEmpty(GlyphBytes(subset, grave));
        Assert.NotEmpty(GlyphBytes(subset, 0)); // .notdef is always kept.
    }

    [Fact]
    public void Subset_has_valid_checksums()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("LiberationSerif-Bold.ttf"));
        var subset = TrueTypeSubsetter.Subset(font, [font.GetGlyphId('R')]);

        // The checksum of a whole font file, including head.checkSumAdjustment, is always 0xB1B0AFBA.
        Assert.Equal(0xB1B0AFBAu, SfntBuilder.Checksum(subset));
    }

    /// <summary>A glyph's bytes without trailing alignment padding, which differs between fonts.</summary>
    private static byte[] GlyphBytes(OpenTypeFont font, int gid)
    {
        var loca = TrueTypeSubsetter.ReadLoca(font);
        var glyph = font.GetTable("glyf")[loca[gid]..loca[gid + 1]];
        var end = glyph.Length;
        while (end > 0 && glyph[end - 1] == 0)
        {
            end--;
        }

        return glyph[..end].ToArray();
    }
}
