using System.Text;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Extraction.Fonts;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

public class GlyphNameAndCMapTests
{
    [Theory]
    [InlineData("A", "A")]
    [InlineData("Delta", "\u2206")]
    [InlineData("eacute", "\u00E9")]
    [InlineData("a.sc", "a")]
    [InlineData("f_f_i", "ffi")]
    [InlineData("f_i.liga", "fi")]
    [InlineData("uni00410042", "AB")]
    [InlineData("uni00e9", "\u00E9")]
    [InlineData("u1F600", "\U0001F600")]
    [InlineData("u00E9", "\u00E9")]
    [InlineData("dalethatafpatah", "\u05D3\u05B2")]
    [InlineData("T_uni0301_g42", "T\u0301")]
    public void Glyph_names_map_to_text(string name, string expected) => Assert.Equal(expected, GlyphNames.ToUnicode(name));

    [Theory]
    [InlineData(".notdef")]
    [InlineData("g42")]
    [InlineData(".sc")]
    [InlineData("uniD800")]
    [InlineData("uni004")]
    [InlineData("u110000")]
    [InlineData("u12")]
    [InlineData("uni00GG")]
    [InlineData("")]
    public void Glyph_names_without_text_map_to_null(string name) => Assert.Null(GlyphNames.ToUnicode(name));

    [Fact]
    public void Codespace_ranges_decide_code_lengths_byte_by_byte()
    {
        var cmap = Parse("3 begincodespacerange <00> <80> <8140> <9FFC> <A0> <DF> endcodespacerange");

        Assert.Equal(1, cmap.CodeLength([0x41, 0x81], 2));
        Assert.Equal(2, cmap.CodeLength([0x81, 0x40], 1));
        Assert.Equal(1, cmap.CodeLength([0xA5], 2));

        // 0x81 0x30 is in no range (0x30 < 0x40): the length of a range its first byte fits.
        Assert.Equal(2, cmap.CodeLength([0x81, 0x30], 1));

        // 0xF0 fits no range at all: the caller's default, but never past the end of the data.
        Assert.Equal(2, cmap.CodeLength([0xF0, 0x00], 2));
        Assert.Equal(1, cmap.CodeLength([0xF0], 2));
    }

    [Fact]
    public void Text_and_CID_mappings_keep_codes_of_different_lengths_apart()
    {
        var cmap = Parse("""
            2 beginbfchar <41> <0058> <0041> <0059> endbfchar
            1 begincidrange <0000> <00FF> 100 endcidrange
            1 begincidchar <41> 7 endcidchar
            """);

        Assert.Equal("X", cmap.Text(0x41, 1));
        Assert.Equal("Y", cmap.Text(0x41, 2));
        Assert.Equal(7, cmap.Cid(0x41, 1));
        Assert.Equal(165, cmap.Cid(0x41, 2));
        Assert.Null(cmap.Cid(0x100, 2));
    }

    [Fact]
    public void Incrementing_bfranges_count_up_the_last_UTF16_unit()
    {
        var cmap = Parse("1 beginbfrange <10> <12> <00660066> endbfrange 1 beginbfrange <20> <21> <FFFE> endbfrange");

        Assert.Equal(["ff", "fg", "fh"], new uint[] { 0x10, 0x11, 0x12 }.Select(c => cmap.Text(c, 1)));
        Assert.Equal("\uFFFE", cmap.Text(0x20, 1));
        Assert.Equal("\uFFFF", cmap.Text(0x21, 1));
    }

    [Fact]
    public void Overlapping_ranges_resolve_to_one_of_them()
    {
        var cmap = Parse("2 beginbfrange <00> <FF> <0041> <10> <1F> <0061> endbfrange");

        Assert.Equal("a", cmap.Text(0x10, 1));
        Assert.Equal("A", cmap.Text(0x00, 1));
        Assert.Equal("B", cmap.Text(0x01, 1));
        Assert.Equal("f", cmap.Text(0x25, 1)); // After the inner range: found by looking back.
    }

    [Fact]
    public void WMode_and_the_CMap_name_are_read()
    {
        var cmap = Parse("/CMapName /Test-V def /WMode 1 def /Identity-V usecmap");

        Assert.True(cmap.IsVertical);
        Assert.Equal("Test-V", cmap.Name);
        Assert.Equal("Identity-V", cmap.UseCMap);
    }

    [Fact]
    public void Mappings_beyond_the_limit_are_ignored_with_a_diagnostic()
    {
        var entries = new StringBuilder();
        var pairs = CMap.MaxSectionOperands / 2;
        for (var i = 0; i < pairs + 5; i++)
        {
            entries.Append(System.Globalization.CultureInfo.InvariantCulture, $"<{i:X4}> <0041> ");
        }

        var diagnostics = new ExtractionDiagnostics();
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes($"1 beginbfchar {entries} endbfchar"), diagnostics);

        Assert.Equal("A", cmap.Text(0, 2));
        Assert.Equal("A", cmap.Text((uint)pairs - 1, 2));
        Assert.Null(cmap.Text((uint)pairs, 2));
        Assert.Contains(diagnostics.Messages, m => m.Contains("operands", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagnostics_are_kept_once_and_up_to_a_limit()
    {
        var diagnostics = new ExtractionDiagnostics();
        for (var i = 0; i < ExtractionDiagnostics.MaxMessages * 2; i++)
        {
            diagnostics.Report("same");
            diagnostics.Report($"message {i}");
        }

        Assert.Equal(ExtractionDiagnostics.MaxMessages, diagnostics.Messages.Count);
        Assert.Single(diagnostics.Messages, m => m == "same");
    }

    [Theory]
    [InlineData("/Helvetica", 2.78)]
    [InlineData("/Helvetica-Bold", 2.78)]
    [InlineData("/Times-Roman", 2.5)]
    [InlineData("/Courier", 6)]
    [InlineData("/ArialMT", 2.78)]
    public void Standard_fonts_without_widths_use_Adobe_widths_where_Liberation_differs(string baseFont, double middleDotAdvance)
    {
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td (\\267A) Tj ET", Font($"<< /Type /Font /Subtype /Type1 /BaseFont {baseFont} /Encoding /WinAnsiEncoding >>")));

        Assert.Equal("\u00B7A", sink.Text);
        Assert.Equal(100 + middleDotAdvance, sink.Glyphs[1].Origin.X, 3);
    }

    [Theory]
    [InlineData("ABCDEF+Helvetica", "Helvetica")]
    [InlineData("Helvetica", "Helvetica")]
    [InlineData("abcdef+Helvetica", "abcdef+Helvetica")]
    [InlineData("ABCDE+Helvetica", "ABCDE+Helvetica")]
    public void Subset_tags_are_six_capitals_and_a_plus(string name, string expected) =>
        Assert.Equal(expected, StandardFontNames.WithoutSubsetTag(name));

    [Theory]
    [InlineData("Arial,BoldItalic", RaiCore.Pdf.Fonts.StandardFont.HelveticaBoldOblique)]
    [InlineData("TimesNewRomanPS-ItalicMT", RaiCore.Pdf.Fonts.StandardFont.TimesItalic)]
    [InlineData("CourierNewPS-BoldMT", RaiCore.Pdf.Fonts.StandardFont.CourierBold)]
    [InlineData("Times-Roman", RaiCore.Pdf.Fonts.StandardFont.TimesRoman)]
    [InlineData("SymbolMT", RaiCore.Pdf.Fonts.StandardFont.Symbol)]
    [InlineData("ZapfDingbats", RaiCore.Pdf.Fonts.StandardFont.ZapfDingbats)]
    [InlineData("ABCDEF+Helvetica-Oblique", RaiCore.Pdf.Fonts.StandardFont.HelveticaOblique)]
    public void Standard_font_names_and_their_equivalents_are_recognized(string name, RaiCore.Pdf.Fonts.StandardFont expected) =>
        Assert.Equal(expected, StandardFontNames.Find(name));

    [Fact]
    public void Other_font_names_are_not_standard_fonts() => Assert.Null(StandardFontNames.Find("Calibri"));

    private static CMap Parse(string text) => CMap.Parse(Encoding.ASCII.GetBytes(text), new ExtractionDiagnostics());
}
