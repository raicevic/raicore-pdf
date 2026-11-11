using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Fonts.OpenType.Cff;

namespace RaiCore.Pdf.Tests.Fonts;

public class CffSubsettingTests
{
    internal const string Latin = "Hello, World! Zw\u00F6lf Boxk\u00E4mpfer \u00E9t\u00E9 \u00C5ngstr\u00F6m \u0141\u00F3d\u017A";

    internal const string Japanese =
        "\u65E5\u672C\u8A9E\u306E\u30C6\u30AD\u30B9\u30C8\u3092\u57CB\u3081\u8FBC\u307F\u307E\u3059\u3002"
        + "\u6771\u4EAC\u90FD\u5343\u4EE3\u7530\u533A \u4E00\u4E8C\u4E09 \u3072\u3089\u304C\u306A";

    /// <summary>The fixtures: name-keyed CFF, CID-keyed CFF (FDArray with several Font DICTs) and CFF2, each with and without kept subroutines.</summary>
    public static TheoryData<string, bool> Fonts => new()
    {
        { "RaiCoreSans3-Subset.otf", false },
        { "RaiCoreSans3-Subset.otf", true },
        { "NotoSansJP-Subset.otf", false },
        { "NotoSansJP-Subset.otf", true },
        { "RaiCoreSans3VF-Subset.otf", false },
        { "RaiCoreSans3VF-Subset.otf", true },
        { "RaiCoreTestCFF-Subrs.otf", false },
        { "RaiCoreTestCFF-Subrs.otf", true },
        { "RaiCoreTestCFF2-Long.otf", false },
    };

    internal static int[] GlyphsFor(OpenTypeFont font, string text) =>
        [.. text.EnumerateRunes().Select(r => (int)font.GetGlyphId(r.Value)).Where(g => g != 0).Distinct()];

    private static string TextFor(string file) =>
        file.StartsWith("Noto", StringComparison.Ordinal) ? Japanese + Latin
        : file.Contains("TestCFF", StringComparison.Ordinal) ? "ABCDEFG"
        : Latin;

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Kept_glyphs_have_the_original_outlines_and_widths(string file, bool keepSubroutines)
    {
        var font = OpenTypeFont.Load(Fixtures.Read(file));
        var used = GlyphsFor(font, TextFor(file));
        var original = CffFont.Load(font);

        var subset = CffFont.Parse(CffSubsetter.Subset(font, used, keepSubroutines));

        Assert.True(subset.IsCidKeyed);
        Assert.False(subset.IsCff2);
        Assert.Equal([0, .. used.Order()], subset.Cids!);
        foreach (var gid in used.Prepend(0))
        {
            var (path, width) = CffOutline.Decode(original, gid);
            var (subsetPath, subsetWidth) = CffOutline.Decode(subset, Array.IndexOf(subset.Cids!, gid));
            Assert.Equal(path, subsetPath);
            if (original.IsCff2)
            {
                // CFF2 has no widths in charstrings; the converted ones carry the advance (nominalWidthX is 0).
                var advance = font.GetAdvanceWidth(gid);
                Assert.Equal(advance == 0 ? null : advance, subsetWidth);
            }
            else
            {
                Assert.Equal(width, subsetWidth);
            }
        }

        Assert.Contains(used, g => CffOutline.Decode(subset, Array.IndexOf(subset.Cids!, g)).Path.Count >= 4);
    }

    [Theory]
    [InlineData("RaiCoreSans3-Subset.otf")]
    [InlineData("NotoSansJP-Subset.otf")]
    [InlineData("RaiCoreSans3VF-Subset.otf")]
    public void Subset_is_much_smaller_than_the_font(string file)
    {
        var font = OpenTypeFont.Load(Fixtures.Read(file));
        var table = font.GetTable(font.HasTable("CFF2") ? "CFF2" : "CFF ").Length;

        var subset = CffSubsetter.Subset(font, GlyphsFor(font, "Hello"));

        Assert.True(subset.Length < table / 20, $"Subset is {subset.Length} bytes of {table}");
    }

    [Fact]
    public void Subsets_are_desubroutinized_by_default()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreSans3-Subset.otf"));
        Assert.True(CffFont.Load(font).GlobalSubrs.Count > 0);

        var subset = CffFont.Parse(CffSubsetter.Subset(font, GlyphsFor(font, Latin)));

        Assert.Equal(0, subset.GlobalSubrs.Count);
        Assert.All(subset.FontDicts, fd => Assert.Equal(0, fd.LocalSubrs.Count));
        Assert.All(subset.FontDicts, fd => Assert.False(fd.Private.Has(CffDict.Subrs)));
    }

    [Theory]
    [InlineData("RaiCoreSans3-Subset.otf")]
    [InlineData("NotoSansJP-Subset.otf")]
    public void Kept_subroutines_are_only_those_called(string file)
    {
        var font = OpenTypeFont.Load(Fixtures.Read(file));
        var original = CffFont.Load(font);

        var subset = CffFont.Parse(CffSubsetter.Subset(font, GlyphsFor(font, "Hello"), keepSubroutines: true));

        Assert.InRange(subset.GlobalSubrs.Count, 1, (original.GlobalSubrs.Count / 4) - 1);
        var localBefore = original.FontDicts.Sum(fd => fd.LocalSubrs.Count);
        var localAfter = subset.FontDicts.Sum(fd => fd.LocalSubrs.Count);
        Assert.InRange(localAfter, 1, (localBefore / 4) - 1);
    }

    [Fact]
    public void Kept_subroutines_are_renumbered_with_the_new_bias()
    {
        // 1300 global and local subroutines (bias 1131): "A" calls global 1250, which calls global 1260; "B" calls local 1270.
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreTestCFF-Subrs.otf"));
        var original = CffFont.Load(font);
        Assert.Equal(1300, original.GlobalSubrs.Count);

        var a = CffFont.Parse(CffSubsetter.Subset(font, [font.GetGlyphId('A')], keepSubroutines: true));
        var b = CffFont.Parse(CffSubsetter.Subset(font, [font.GetGlyphId('B')], keepSubroutines: true));

        // Two global subroutines (bias 107): "A" calls 0 - 107, which calls 1 - 107.
        Assert.Equal(2, a.GlobalSubrs.Count);
        Assert.Equal(0, a.FontDicts[0].LocalSubrs.Count);
        Assert.Equal([248, 236, 32, 29, 14], a.CharStrings[1].ToArray()); // 600 -107 callgsubr endchar
        Assert.Equal([179, 139, 21, 33, 29, 11], a.GlobalSubrs[0].ToArray()); // 40 0 rmoveto -106 callgsubr return
        Assert.Equal(original.GlobalSubrs[1260].ToArray(), a.GlobalSubrs[1].ToArray());
        Assert.Equal(0, b.GlobalSubrs.Count);
        Assert.Equal(1, b.FontDicts[0].LocalSubrs.Count);
        Assert.Equal(original.FontDicts[0].LocalSubrs[1270].ToArray(), b.FontDicts[0].LocalSubrs[0].ToArray());
    }

    [Fact]
    public void Cid_keyed_fonts_keep_the_used_font_dicts_with_their_private_dicts()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("NotoSansJP-Subset.otf"));
        var original = CffFont.Load(font);
        var used = GlyphsFor(font, Japanese + Latin);
        var usedFontDicts = used.Select(original.FontDictIndex).Distinct().Count();
        Assert.True(usedFontDicts > 1);
        Assert.True(usedFontDicts <= original.FontDicts.Count);

        var subset = CffFont.Parse(CffSubsetter.Subset(font, GlyphsFor(font, Japanese + Latin)));

        Assert.Equal(used.Append(0).Select(original.FontDictIndex).Distinct().Count(), subset.FontDicts.Count);
        foreach (var gid in used)
        {
            var newGid = Array.IndexOf(subset.Cids!, gid);
            Assert.Equal(
                PrivateEntries(original.FontDicts[original.FontDictIndex(gid)].Private),
                PrivateEntries(subset.FontDicts[subset.FontDictIndex(newGid)].Private));
        }
    }

    [Fact]
    public void Cff2_is_converted_to_cff_at_the_default_instance()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreSans3VF-Subset.otf"));
        var original = CffFont.Load(font);
        Assert.True(original.IsCff2);
        Assert.NotEmpty(original.RegionCounts);

        var data = CffSubsetter.Subset(font, GlyphsFor(font, Latin));
        var subset = CffFont.Parse(data);

        Assert.Equal(1, data[0]); // CFF version 1
        Assert.False(subset.IsCff2);
        Assert.All(subset.FontDicts, fd => Assert.DoesNotContain(fd.Private.Entries, e => e.Op is CffDict.Blend or CffDict.VsIndexOp));
    }

    [Fact]
    public void Cff2_private_dict_blends_are_resolved_to_the_default_values()
    {
        // BlueValues -12 0 1400 1412 (stored as deltas) and StdVW 80, each with a delta for the weight axis.
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreTestCFF2-Long.otf"));

        var subset = CffFont.Parse(CffSubsetter.Subset(font, [font.GetGlyphId('A')]));

        var privateDict = subset.FontDicts[0].Private;
        Assert.Equal([-12, 12, 1400, 12], privateDict.Get(6)!.Select(o => o.Value));
        Assert.Equal([80], privateDict.Get(11)!.Select(o => o.Value));
        Assert.Equal([0.0625], privateDict.Get(0x0C09)!.Select(o => o.Value));
    }

    [Fact]
    public void Cff2_argument_lists_are_split_to_the_cff_stack_limit()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreTestCFF2-Long.otf"));
        var original = CffFont.Load(font);
        var all = Enumerable.Range(1, original.NumGlyphs - 1).ToArray();
        Assert.True(all.Max(g => MaxArguments(original, g)) > 48);

        var subset = CffFont.Parse(CffSubsetter.Subset(font, all));

        Assert.InRange(Enumerable.Range(0, subset.NumGlyphs).Max(g => MaxArguments(subset, g)), 1, 48);
    }

    [Theory]
    [InlineData("RaiCoreTestCFF-Subrs.otf")]
    [InlineData("RaiCoreTestCFF2-Long.otf")]
    public void The_font_matrix_of_name_keyed_and_cff2_fonts_moves_to_the_font_dict(string file)
    {
        // Both fonts have 2048 units per em.
        var font = OpenTypeFont.Load(Fixtures.Read(file));

        var subset = CffFont.Parse(CffSubsetter.Subset(font, [font.GetGlyphId('A')]));

        Assert.False(subset.TopDict.Has(CffDict.FontMatrix));
        var matrix = subset.FontDicts[0].FontDict!.Get(CffDict.FontMatrix)!.Select(o => o.Value);
        Assert.Equal([1 / 2048.0, 0, 0, 1 / 2048.0, 0, 0], matrix);
    }

    [Fact]
    public void Glyphs_built_with_seac_are_not_supported()
    {
        var font = OpenTypeFont.Load(Fixtures.Read("RaiCoreTestCFF-Seac.otf"));

        Assert.NotNull(CffSubsetter.Subset(font, [font.GetGlyphId('A')]));
        Assert.Throws<NotSupportedException>(() => CffSubsetter.Subset(font, [font.GetGlyphId('\u00C1')]));
    }

    private static int MaxArguments(CffFont font, int glyphId) =>
        CharStringFlattener.Flatten(new CharStringInterpreter(font), glyphId).Select(c => c.Args.Length).DefaultIfEmpty(0).Max();

    private static List<string> PrivateEntries(CffDict dict) =>
        [.. dict.Entries
            .Where(e => e.Op != CffDict.Subrs)
            .Select(e => $"{e.Op}: {string.Join(' ', e.Operands.Select(o => Convert.ToHexString(o.Encoded)))}")];
}
