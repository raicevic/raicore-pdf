using System.Text;
using RaiCore.Pdf.Extraction.Fonts;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Tests.Reading;
using static RaiCore.Pdf.Tests.Extraction.Interpret;

namespace RaiCore.Pdf.Tests.Extraction;

public class FontDecodingTests
{
    private static RecordingSink Show(string font, string text, params string[] extra) =>
        Page(OnePage($"BT /F1 10 Tf 100 700 Td {text} Tj ET", Font(font), extra: extra));

    [Theory]
    [InlineData("/WinAnsiEncoding", "(\\200\\226\\237)", "\u20AC\u2013\u0178")]
    [InlineData("/MacRomanEncoding", "(\\216\\245\\333)", "\u00E9\u2022\u00A4")]
    [InlineData("/StandardEncoding", "(\\047\\140\\256\\341)", "\u2019\u2018\uFB01\u00C6")]
    public void Base_encodings_map_codes_to_text(string encoding, string text, string expected)
    {
        var sink = Show($"<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding {encoding} >>", text);

        Assert.Equal(expected, sink.Text);
        Assert.All(sink.Glyphs, g => Assert.False(g.IsGuess));
    }

    [Fact]
    public void A_nonsymbolic_font_without_an_encoding_uses_StandardEncoding()
    {
        Assert.Equal("\u2019s", Show("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", "(\\047s)").Text);
    }

    [Fact]
    public void Differences_name_glyphs_through_the_Adobe_Glyph_List()
    {
        var font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding "
            + "/Differences [65 /Alpha /uni00E9 /f_i /u1F600 /g42 /a.sc 97 /space] >> >>";
        var sink = Show(font, "(ABCDEFab)");

        Assert.Equal(["\u0391", "\u00E9", "fi", "\U0001F600", "E", "a", " ", "b"], sink.Glyphs.Select(g => g.Text));

        // /g42 means nothing: its code is guessed as Latin-1; the rest are mapped.
        Assert.Equal([false, false, false, false, true, false, false, false], sink.Glyphs.Select(g => g.IsGuess));
    }

    [Fact]
    public void ToUnicode_overrides_the_encoding_with_chars_ranges_and_ligatures()
    {
        var cmap = TestPdf.Stream("""
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            /CMapName /Test def /CMapType 2 def
            1 begincodespacerange <00> <FF> endcodespacerange
            2 beginbfchar <41> <0058> <47> <0000> endbfchar
            2 beginbfrange <42> <44> <0061> <45> <46> [<0066006C> <D83DDE00>] endbfrange
            endcmap CMapName currentdict /CMap defineresource pop end end
            """);
        var sink = Show("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 5 0 R >>", "(ABCDEFGH)", cmap);

        // G maps to <0000> (nothing), so the encoding gives it; H is not in the map at all.
        Assert.Equal(["X", "a", "b", "c", "fl", "\U0001F600", "G", "H"], sink.Glyphs.Select(g => g.Text));
    }

    [Fact]
    public void ToUnicode_written_with_two_byte_codes_still_maps_a_simple_font()
    {
        var cmap = TestPdf.Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfchar <0041> <03A9> endbfchar");
        var sink = Show("<< /Type /Font /Subtype /TrueType /BaseFont /Arial /ToUnicode 5 0 R >>", "(A)", cmap);

        Assert.Equal("\u03A9", sink.Text);
    }

    [Fact]
    public void A_symbolic_font_without_any_mapping_guesses_Latin_1()
    {
        var descriptor = "<< /Type /FontDescriptor /FontName /Wingish /Flags 4 /Ascent 900 /Descent -100 /FontBBox [0 -100 1000 900] >>";
        var sink = Show($"<< /Type /Font /Subtype /TrueType /BaseFont /Wingish /FirstChar 65 /LastChar 65 /Widths [700] /FontDescriptor {descriptor} >>", "(A\\001)");

        Assert.Equal("A", sink.Text);
        Assert.True(sink.Glyphs[0].IsGuess);
        Assert.Equal("", sink.Glyphs[1].Text); // A control code is not guessed.
        Assert.Equal(0.9, sink.Glyphs[0].Font.Ascent, 3);
        Assert.Equal(-0.1, sink.Glyphs[0].Font.Descent, 3);
    }

    [Fact]
    public void Standard_fonts_without_widths_use_the_metrics_of_the_standard_font()
    {
        var sink = Show("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", "(iW)");

        Assert.Equal(106, sink.Glyphs[1].Origin.X, 3); // Courier is monospaced: 600 units.
    }

    [Fact]
    public void Missing_widths_fall_back_to_MissingWidth()
    {
        var sink = Show(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Unknown /FirstChar 65 /LastChar 65 /Widths [500] /Encoding /WinAnsiEncoding "
            + "/FontDescriptor << /Type /FontDescriptor /Flags 32 /MissingWidth 300 >> >>",
            "(BAB)");

        Assert.Equal([100, 103, 108], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
    }

    [Fact]
    public void Type3_widths_and_bounds_go_through_the_font_matrix()
    {
        var font = "<< /Type /Font /Subtype /Type3 /FontBBox [0 -20 100 80] /FontMatrix [0.01 0 0 0.01 0 0] "
            + "/CharProcs << /square 5 0 R >> /Encoding << /Type /Encoding /Differences [65 /square /B] >> "
            + "/FirstChar 65 /LastChar 66 /Widths [50 120] /Resources << >> >>";
        var sink = Show(font, "(ABA)", TestPdf.Stream("50 0 0 0 50 50 d1 0 0 50 50 re f"));

        // Widths 50 and 120 glyph units are 0.5 and 1.2 em at a font matrix of 0.01.
        Assert.Equal([100, 105, 117], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal(100 - 8, sink.Glyphs[0].Bounds.Top, 3);
        Assert.Equal(100 + 2, sink.Glyphs[0].Bounds.Bottom, 3);
        Assert.Equal("B", sink.Glyphs[1].Text);
        Assert.Equal("A", sink.Glyphs[0].Text); // /square is no glyph name: guessed.
        Assert.True(sink.Glyphs[0].IsGuess);
    }

    [Fact]
    public void A_Type1_program_built_in_encoding_is_read_from_its_clear_text()
    {
        var program = "%!PS-AdobeFont-1.0: Test 001\n/FontName /Test def\n/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\n"
            + "dup 65 /Lslash put\ndup 66 /germandbls put\nreadonly def\ncurrentfile eexec\n\u00D9\u00D6\u0000";
        var fontFile = TestPdf.Stream(program, $"/Length1 {program.IndexOf("eexec", StringComparison.Ordinal) + 6} /Length2 3 /Length3 0");
        var sink = Show(
            "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+Test /FontDescriptor << /Type /FontDescriptor /FontName /ABCDEF+Test /Flags 4 /FontFile 5 0 R >> >>",
            "(AB)",
            fontFile);

        Assert.Equal("\u0141\u00DF", sink.Text);
        Assert.Equal("Test", sink.Glyphs[0].Font.Name);
        Assert.True(sink.Glyphs[0].Font.IsEmbedded);
    }

    [Fact]
    public void Composite_fonts_with_Identity_H_map_through_ToUnicode_and_W()
    {
        var cmap = TestPdf.Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <0001> <0048> <0002> <0069> endbfchar");
        var sink = Show(CidFont("/Identity-H", "/W [1 [500 600]] /DW 1000", "/ToUnicode 5 0 R"), "<000100020003>", cmap);

        Assert.Equal("Hi", sink.Text);
        Assert.Equal([100, 105, 111], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal([1u, 2u, 3u], sink.Glyphs.Select(g => g.Code));
        Assert.All(sink.Glyphs, g => Assert.Equal(2, g.CodeLength));
    }

    [Fact]
    public void An_embedded_CMap_splits_mixed_length_codes_and_maps_them_to_CIDs()
    {
        var encoding = TestPdf.Stream("""
            /CMapName /Mixed def
            2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange
            2 begincidrange <00> <7F> 0 <8000> <80FF> 1000 endcidrange
            """, "/Type /CMap /CMapName /Mixed");
        var toUnicode = TestPdf.Stream("2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange 3 beginbfchar <41> <0041> <8001> <00E9> <20> <0020> endbfchar");
        var font = CidFont("5 0 R", "/W [65 [100] 1001 [200] 32 [250] 66 66 300]", "/ToUnicode 6 0 R");
        var sink = Page(OnePage("BT /F1 10 Tf 3 Tw 100 700 Td <41800120 42> Tj ET", Font(font), extra: [encoding, toUnicode]));

        Assert.Equal(["A", "\u00E9", " ", ""], sink.Glyphs.Select(g => g.Text));
        Assert.Equal([1, 2, 1, 1], sink.Glyphs.Select(g => g.CodeLength));

        // The one-byte code 32 takes word spacing (2.5 + 3).
        Assert.Equal([100, 101, 103, 108.5], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.True(sink.Glyphs[2].IsWordSpace);
    }

    [Fact]
    public void A_CMap_can_build_on_Identity_with_usecmap()
    {
        var encoding = TestPdf.Stream("/Identity-H usecmap 1 begincidchar <0005> 7 endcidchar 1 begincidrange <0100> <01FF> 0 endcidrange", "/Type /CMap /CMapName /Derived");
        var sink = Page(OnePage("BT /F1 10 Tf 100 700 Td <000500060101> Tj ET", Font(CidFont("5 0 R", "/W [0 [400 300] 6 [100 200]]", "")), extra: [encoding]));

        // Code 5 is CID 7 (200 wide), code 6 stays CID 6 (100 wide), and the CMap's own range beats Identity: code 0x101 is CID 1.
        Assert.Equal([100, 102, 103], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal(106, sink.Glyphs[2].End.X, 3);
    }

    [Fact]
    public void Vertical_fonts_advance_downwards()
    {
        var sink = Show(CidFont("/Identity-V", "/DW 1000 /W2 [2 [-500 250 800]]", ""), "<000100020001>");

        // Default vertical metrics: advance -1 em, origin at (w0 / 2, 0.88 em); CID 2 advances half an em.
        Assert.Equal([95, 97.5, 95], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal([108.8, 118, 123.8], sink.Glyphs.Select(g => Math.Round(g.Origin.Y, 3)));
        Assert.True(sink.Glyphs[0].Font.IsVertical);
    }

    [Fact]
    public void DW2_sets_the_default_vertical_origin_and_advance()
    {
        var sink = Show(CidFont("/Identity-V", "/DW 600 /DW2 [900 -800]", ""), "<00010001>");

        Assert.Equal([97, 97], sink.Glyphs.Select(g => Math.Round(g.Origin.X, 3)));
        Assert.Equal([109, 117], sink.Glyphs.Select(g => Math.Round(g.Origin.Y, 3)));
    }

    [Fact]
    public void Unicode_CMaps_give_the_text_directly()
    {
        var sink = Show(CidFont("/UniGB-UCS2-H", "", ""), "<4E2D6587>");

        Assert.Equal("\u4E2D\u6587", sink.Text);
    }

    [Fact]
    public void UTF16_CMaps_read_surrogate_pairs_as_one_code()
    {
        var sink = Show(CidFont("/UniJIS-UTF16-H", "", ""), "<0041D83DDE000042>");

        Assert.Equal(["A", "\U0001F600", "B"], sink.Glyphs.Select(g => g.Text));
        Assert.Equal([2, 4, 2], sink.Glyphs.Select(g => g.CodeLength));
    }

    [Fact]
    public void An_unsupported_predefined_CMap_is_reported_and_read_as_two_byte_codes()
    {
        var sink = Show(CidFont("/90ms-RKSJ-H", "", ""), "<8140>");

        Assert.Single(sink.Glyphs);
        Assert.Contains(sink.Diagnostics.Messages, m => m.Contains("90ms-RKSJ-H", StringComparison.Ordinal));
    }

    [Fact]
    public void Huge_width_ranges_and_ToUnicode_ranges_cost_nothing_until_used()
    {
        var cmap = TestPdf.Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfrange <0000> <FFFF> <4E00> endbfrange");
        var sink = Show(CidFont("/Identity-H", "/W [0 2147483647 250]", "/ToUnicode 5 0 R"), "<00020003>", cmap);

        Assert.Equal("\u4E02\u4E03", sink.Text);
        Assert.Equal(102.5, sink.Glyphs[1].Origin.X, 3);
    }

    [Fact]
    public void Text_drawn_by_RaiCore_with_a_standard_font_round_trips()
    {
        var document = new PdfDocument();
        var page = document.AddPage(PdfPageSize.A4);
        page.Canvas.DrawText("Hello, Gr\u00FC\u00DFe \u2013 \u20AC5", StandardFont.Helvetica, 12, 72, 700);
        var sink = Page(document.ToArray());

        Assert.Equal("Hello, Gr\u00FC\u00DFe \u2013 \u20AC5", sink.Text);
        Assert.Equal(72, sink.Glyphs[0].Origin.X, 3);
        Assert.Equal(PdfPageSize.A4.Height - 700, sink.Glyphs[0].Origin.Y, 3);

        // Advances match RaiCore's own measurement of the text.
        var width = PdfFont.Standard(StandardFont.Helvetica).MeasureText("Hello, Gr\u00FC\u00DFe \u2013 \u20AC5", 12);
        Assert.Equal(72 + width, sink.Glyphs[^1].End.X, 2);
        Assert.Empty(sink.Diagnostics.Messages);
    }

    [Fact]
    public void Text_drawn_by_RaiCore_with_an_embedded_font_round_trips()
    {
        var text = "Gr\u00FC\u00DFe \u03A9mega \u201Cquoted\u201D";
        var font = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
        var document = new PdfDocument();
        document.AddPage(PdfPageSize.A4).Canvas.DrawText(text, font, 14, 72, 700);
        var sink = Page(document.ToArray());

        Assert.Equal(text, sink.Text);
        Assert.Equal(72 + font.MeasureText(text, 14), sink.Glyphs[^1].End.X, 2);
        Assert.Equal(FontKind.Type0, sink.Glyphs[0].Font.Kind);
        Assert.True(sink.Glyphs[0].Font.IsEmbedded);
    }

    [Fact]
    public void Without_ToUnicode_the_embedded_TrueType_cmap_gives_the_text()
    {
        // fpdf2 keeps a cmap in its TrueType subsets (RaiCore leaves it out, as composite fonts allow).
        var pdf = Fixtures.Read("Extraction/fpdf2.pdf");
        var withoutToUnicode = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(pdf).Replace("/ToUnicode", "/NoUnicode", StringComparison.Ordinal));
        Assert.NotEqual(pdf, withoutToUnicode);

        var expected = Page(pdf).Text;
        Assert.Contains("Gr\u00FC\u00DFe", expected, StringComparison.Ordinal);
        Assert.Equal(expected, Page(withoutToUnicode).Text);
    }

    [Fact]
    public void Arabic_drawn_by_the_HTML_converter_carries_its_actual_text()
    {
        var html = "<p style=\"font-family: 'Noto Sans Arabic'; direction: rtl\">\u0627\u0644\u0633\u0644\u0627\u0645 \u0639\u0644\u064A\u0643\u0645</p>";
        var fonts = RaiCore.Pdf.Fonts.BundledFonts.CreateCollection().AddFile(Fixtures.Path("NotoSansArabic-Subset.ttf"));
        var sink = Page(RaiCore.Pdf.Html.HtmlToPdf.ConvertToBytes(html, new RaiCore.Pdf.Html.HtmlConverterOptions { Fonts = fonts }));

        var actualText = string.Concat(sink.Events.Where(e => e.StartsWith("begin Span ActualText=", StringComparison.Ordinal)).Select(e => e[22..]));
        Assert.Contains("\u0627\u0644\u0633\u0644\u0627\u0645", actualText, StringComparison.Ordinal);
        Assert.NotEmpty(sink.Glyphs);
    }

    /// <summary>A Type 0 font with a CIDFontType2 descendant (not embedded).</summary>
    private static string CidFont(string encoding, string widths, string fontEntries) =>
        $"<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding {encoding} {fontEntries} /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 "
        + $"/BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> {widths} "
        + "/FontDescriptor << /Type /FontDescriptor /FontName /Test /Flags 4 /Ascent 880 /Descent -120 >> >>] >>";
}
