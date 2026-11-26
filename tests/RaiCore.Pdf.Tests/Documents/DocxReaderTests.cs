using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;
using static RaiCore.Pdf.Tests.WordDocuments.Docx;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Reading WordprocessingML into the word-processing model: styles and their inheritance, numbering, fields, tracked
/// changes, content controls, tables, sections, headers and footers, images and notes.
/// </summary>
public class DocxReaderTests
{
    private const string Styles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Arial"/><w:sz w:val="22"/></w:rPr></w:rPrDefault>
          <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Base"><w:basedOn w:val="Normal"/><w:pPr><w:jc w:val="center"/><w:ind w:left="720"/></w:pPr><w:rPr><w:b/><w:color w:val="FF0000"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Derived"><w:basedOn w:val="Base"/><w:pPr><w:spacing w:before="240"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:sz w:val="40"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Loop1"><w:basedOn w:val="Loop2"/><w:rPr><w:i/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Loop2"><w:basedOn w:val="Loop1"/><w:rPr><w:b/></w:rPr></w:style>
        <w:style w:type="character" w:styleId="Strong"><w:rPr><w:u w:val="double"/><w:sz w:val="16"/></w:rPr></w:style>
        """;

    [Fact]
    public void Paragraph_and_run_formats_resolve_through_the_style_hierarchy()
    {
        var document = Read(Document(
            P("defaults") + P("derived", "<w:pStyle w:val=\"Derived\"/>")
            + "<w:p><w:pPr><w:pStyle w:val=\"Derived\"/><w:jc w:val=\"right\"/></w:pPr><w:r><w:rPr><w:rStyle w:val=\"Strong\"/><w:b w:val=\"0\"/></w:rPr><w:t>direct</w:t></w:r></w:p>",
            Styles));
        var paragraphs = Paragraphs(document);

        var plain = paragraphs[0];
        Assert.Equal(8, plain.Format.SpaceAfter);
        Assert.Equal(1.15, plain.Format.LineSpacing, 3);
        Assert.Equal(LineSpacingRule.Multiple, plain.Format.LineRule);
        var plainRun = Assert.IsType<TextRun>(plain.Inlines[0]);
        Assert.Equal("Arial", plainRun.Format.FontFamily);
        Assert.Equal(11, plainRun.Format.FontSize);
        Assert.False(plainRun.Format.Bold);

        var derived = paragraphs[1];
        Assert.Equal(WordAlignment.Center, derived.Format.Alignment); // From Base.
        Assert.Equal(36, derived.Format.IndentStart);
        Assert.Equal(12, derived.Format.SpaceBefore);
        Assert.Equal(8, derived.Format.SpaceAfter); // From the defaults.
        Assert.Equal(1, derived.Format.OutlineLevel);
        Assert.Equal("Derived", derived.Format.StyleId);
        var derivedRun = Assert.IsType<TextRun>(derived.Inlines[0]);
        Assert.True(derivedRun.Format.Bold);
        Assert.Equal(20, derivedRun.Format.FontSize);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), derivedRun.Format.Color);

        var direct = paragraphs[2];
        Assert.Equal(WordAlignment.End, direct.Format.Alignment);
        var directRun = Assert.IsType<TextRun>(direct.Inlines[0]);
        Assert.False(directRun.Format.Bold); // Direct formatting wins over the paragraph style.
        Assert.Equal(UnderlineStyle.Double, directRun.Format.Underline); // From the character style.
        Assert.Equal(8, directRun.Format.FontSize); // The character style wins over the paragraph style.
    }

    [Fact]
    public void Style_loops_and_unknown_styles_do_not_hang()
    {
        var document = Read(Document(P("loop", "<w:pStyle w:val=\"Loop1\"/>") + P("unknown", "<w:pStyle w:val=\"Nope\"/>"), Styles));
        var paragraphs = Paragraphs(document);
        var loop = Assert.IsType<TextRun>(paragraphs[0].Inlines[0]);
        Assert.True(loop.Format.Italic);
        Assert.True(loop.Format.Bold);
        Assert.Equal("Normal", paragraphs[1].Format.StyleId is "Nope" ? "Normal" : paragraphs[1].Format.StyleId);
    }

    [Fact]
    public void Without_styles_text_is_ten_point_times()
    {
        var run = Assert.IsType<TextRun>(Paragraphs(Read(Document(P("x"))))[0].Inlines[0]);
        Assert.Equal(10, run.Format.FontSize);
        Assert.Equal("Times New Roman", run.Format.FontFamily);
    }

    [Fact]
    public void Run_properties_are_read()
    {
        const string rPr = "<w:i/><w:caps/><w:smallCaps/><w:strike/><w:vertAlign w:val=\"superscript\"/><w:highlight w:val=\"yellow\"/>"
            + "<w:spacing w:val=\"40\"/><w:position w:val=\"6\"/><w:u w:val=\"wave\"/><w:rtl/><w:lang w:val=\"da-DK\"/>";
        var run = Assert.IsType<TextRun>(Paragraphs(Read(Document(P("x", rPr: rPr))))[0].Inlines[0]);
        Assert.True(run.Format.Italic);
        Assert.True(run.Format.Caps);
        Assert.True(run.Format.SmallCaps);
        Assert.True(run.Format.Strike);
        Assert.Equal(VerticalPosition.Superscript, run.Format.Position);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), run.Format.Background);
        Assert.Equal(2, run.Format.LetterSpacing);
        Assert.Equal(3, run.Format.BaselineShift);
        Assert.Equal(UnderlineStyle.Wavy, run.Format.Underline);
        Assert.True(run.Format.RightToLeft);
        Assert.Equal("da-DK", run.Format.Language);
    }

    [Fact]
    public void Theme_fonts_and_colors_are_resolved()
    {
        const string theme = $"""
            <a:theme xmlns:a="{A}"><a:themeElements><a:clrScheme name="c"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
            <a:accent1><a:srgbClr val="4472C4"/></a:accent1></a:clrScheme><a:fontScheme name="f"><a:majorFont><a:latin typeface="Cambria"/></a:majorFont>
            <a:minorFont><a:latin typeface="Calibri"/></a:minorFont></a:fontScheme></a:themeElements></a:theme>
            """;
        var document = Read(Document(
            P("minor", rPr: "<w:rFonts w:asciiTheme=\"minorHAnsi\"/><w:color w:val=\"auto\" w:themeColor=\"accent1\"/>")
            + P("major", rPr: "<w:rFonts w:asciiTheme=\"majorHAnsi\" w:ascii=\"Arial\"/>"),
            parts: [("word/theme/theme1.xml", theme)], relationships: [("rTheme", "theme", "theme/theme1.xml", false)]));
        var paragraphs = Paragraphs(document);
        var minor = Assert.IsType<TextRun>(paragraphs[0].Inlines[0]);
        Assert.Equal("Calibri", minor.Format.FontFamily);
        Assert.Equal(PdfColor.FromRgb(0x44, 0x72, 0xC4), minor.Format.Color);
        Assert.Equal("Cambria", Assert.IsType<TextRun>(paragraphs[1].Inlines[0]).Format.FontFamily); // The theme wins over ascii.
    }

    [Fact]
    public void Paragraph_properties_are_read()
    {
        const string pPr = "<w:keepNext/><w:keepLines/><w:pageBreakBefore/><w:widowControl w:val=\"0\"/><w:contextualSpacing/><w:bidi/>"
            + "<w:ind w:start=\"1440\" w:end=\"720\" w:hanging=\"360\"/><w:spacing w:before=\"120\" w:after=\"0\" w:line=\"300\" w:lineRule=\"exact\"/>"
            + "<w:shd w:val=\"clear\" w:fill=\"DDEEFF\"/><w:pBdr><w:top w:val=\"single\" w:sz=\"8\" w:color=\"00FF00\"/><w:between w:val=\"double\" w:sz=\"4\"/></w:pBdr>"
            + "<w:tabs><w:tab w:val=\"right\" w:leader=\"dot\" w:pos=\"9000\"/><w:tab w:val=\"center\" w:pos=\"4000\"/></w:tabs>";
        var format = Paragraphs(Read(Document(P("x", pPr))))[0].Format;
        Assert.True(format.KeepWithNext);
        Assert.True(format.KeepLines);
        Assert.True(format.PageBreakBefore);
        Assert.False(format.WidowControl);
        Assert.True(format.ContextualSpacing);
        Assert.True(format.RightToLeft);
        Assert.Equal(72, format.IndentStart);
        Assert.Equal(36, format.IndentEnd);
        Assert.Equal(-18, format.FirstLineIndent);
        Assert.Equal(6, format.SpaceBefore);
        Assert.Equal(15, format.LineSpacing);
        Assert.Equal(LineSpacingRule.Exact, format.LineRule);
        Assert.Equal(PdfColor.FromRgb(0xDD, 0xEE, 0xFF), format.Shading);
        Assert.Equal(1, format.Borders.Top!.Value.Width);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), format.Borders.Top.Value.Color);
        Assert.Null(format.Borders.Bottom);
        Assert.Equal(BorderLineStyle.Double, format.BetweenBorder!.Value.Style);
        Assert.Equal([new TabStop(200, TabAlignment.Center), new TabStop(450, TabAlignment.Right, '.')], format.TabStops);
    }

    [Fact]
    public void Tab_stops_inherited_from_a_style_can_be_cleared()
    {
        const string styles = "<w:style w:type=\"paragraph\" w:styleId=\"T\"><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"1000\"/><w:tab w:val=\"left\" w:pos=\"2000\"/></w:tabs></w:pPr></w:style>";
        var format = Paragraphs(Read(Document(P("x", "<w:pStyle w:val=\"T\"/><w:tabs><w:tab w:val=\"clear\" w:pos=\"1000\"/></w:tabs>"), styles)))[0].Format;
        Assert.Equal([new TabStop(100, TabAlignment.Left)], format.TabStops);
    }

    [Theory]
    [InlineData("1440", 72)]
    [InlineData("2.54cm", 72)]
    [InlineData("1in", 72)]
    [InlineData("36pt", 36)]
    [InlineData("25.4mm", 72)]
    [InlineData("3pc", 36)]
    public void Measures_are_twips_or_have_units(string value, double points) =>
        Assert.Equal(points, Ooxml.Measure(value, 1 / 20.0)!.Value, 6);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e400")]
    [InlineData("12qq")]
    public void Unreadable_measures_are_ignored(string value) => Assert.Null(Ooxml.Measure(value, 1));

    private const string Numbering = """
        <w:abstractNum w:abstractNumId="1">
          <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
          <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1.%2)"/><w:suff w:val="space"/></w:lvl>
          <w:lvl w:ilvl="2"><w:start w:val="4"/><w:numFmt w:val="upperRoman"/><w:lvlText w:val="%3"/><w:isLgl/><w:suff w:val="nothing"/></w:lvl>
          <w:lvl w:ilvl="3"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1.%2.%3.%4"/><w:isLgl/><w:lvlRestart w:val="0"/></w:lvl>
        </w:abstractNum>
        <w:abstractNum w:abstractNumId="2">
          <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/><w:rPr><w:rFonts w:ascii="Symbol"/><w:color w:val="0000FF"/></w:rPr></w:lvl>
          <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0A7;"/><w:rPr><w:rFonts w:ascii="Wingdings"/></w:rPr></w:lvl>
        </w:abstractNum>
        <w:abstractNum w:abstractNumId="3"><w:numStyleLink w:val="LinkedList"/></w:abstractNum>
        <w:abstractNum w:abstractNumId="4"><w:lvl w:ilvl="0"><w:numFmt w:val="ordinal"/><w:lvlText w:val="%1"/></w:lvl></w:abstractNum>
        <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
        <w:num w:numId="2"><w:abstractNumId w:val="2"/></w:num>
        <w:num w:numId="3"><w:abstractNumId w:val="1"/><w:lvlOverride w:ilvl="0"><w:startOverride w:val="10"/></w:lvlOverride></w:num>
        <w:num w:numId="4"><w:abstractNumId w:val="3"/></w:num>
        <w:num w:numId="5"><w:abstractNumId w:val="4"/></w:num>
        <w:num w:numId="6"><w:abstractNumId w:val="1"/><w:lvlOverride w:ilvl="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="upperLetter"/><w:lvlText w:val="(%1)"/></w:lvl></w:lvlOverride></w:num>
        """;

    private static string Item(string text, int numId, int level) =>
        P(text, $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/></w:numPr>");

    private static List<string?> Labels(string body, string? styles = null) =>
        [.. Paragraphs(Read(Document(body, styles, Numbering))).Select(p => p.Label?.Text)];

    [Fact]
    public void Multi_level_numbering_builds_labels_from_the_level_texts()
    {
        var labels = Labels(Item("a", 1, 0) + Item("b", 1, 1) + Item("c", 1, 1) + Item("d", 1, 2) + Item("e", 1, 3) + Item("f", 1, 0) + Item("g", 1, 1) + P("plain")
            + Item("h", 1, 2));
        Assert.Equal(["1.", "1.a)", "1.b)", "IV", "1.2.4.1", "2.", "2.a)", null, "IV"], labels);
    }

    [Fact]
    public void A_level_with_restart_zero_keeps_counting()
    {
        // Levels not used yet show their start values; the first use of level 0 starts it at 1.
        var labels = Labels(Item("a", 1, 3) + Item("b", 1, 0) + Item("c", 1, 3));
        Assert.Equal(["1.1.4.1", "1.", "1.1.4.2"], labels);
    }

    [Fact]
    public void Numbering_formats_starts_overrides_and_shared_counters()
    {
        var labels = Labels(Item("a", 1, 0) + Item("b", 3, 0) + Item("c", 3, 0) + Item("d", 1, 0) + Item("e", 5, 0) + Item("f", 5, 0) + Item("g", 6, 0));
        // Instance 3 shares instance 1's counters and restarts them at 10 when first used; 6 overrides the level's format.
        Assert.Equal(["1.", "10.", "11.", "12.", "0th", "1st", "(M)"], labels);
    }

    [Fact]
    public void Bullets_of_symbol_fonts_become_unicode_bullets_in_the_paragraph_font()
    {
        var paragraphs = Paragraphs(Read(Document(Item("a", 2, 0) + Item("b", 2, 1), Styles, Numbering)));
        var label = paragraphs[0].Label!;
        Assert.Equal("\u2022", label.Text);
        Assert.Equal("Arial", label.Format.FontFamily); // Not Symbol, which has no Unicode bullet.
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), label.Format.Color);
        Assert.Equal("\u25AA", paragraphs[1].Label!.Text);
    }

    [Fact]
    public void Numbering_level_indents_and_suffixes_apply()
    {
        var paragraphs = Paragraphs(Read(Document(Item("a", 1, 0) + Item("b", 1, 1) + Item("c", 1, 2), null, Numbering)));
        Assert.Equal(36, paragraphs[0].Format.IndentStart);
        Assert.Equal(-18, paragraphs[0].Format.FirstLineIndent);
        Assert.Equal(LabelSuffix.Tab, paragraphs[0].Label!.Suffix);
        Assert.Equal(LabelSuffix.Space, paragraphs[1].Label!.Suffix);
        Assert.Equal(LabelSuffix.Nothing, paragraphs[2].Label!.Suffix);
    }

    [Fact]
    public void Numbering_from_a_style_and_a_numbering_style_link()
    {
        const string styles = """
            <w:style w:type="paragraph" w:styleId="ListItem"><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr></w:style>
            <w:style w:type="numbering" w:styleId="LinkedList"><w:pPr><w:numPr><w:numId w:val="2"/></w:numPr></w:pPr></w:style>
            """;
        var labels = Labels(P("a", "<w:pStyle w:val=\"ListItem\"/>") + P("b", "<w:pStyle w:val=\"ListItem\"/><w:numPr><w:numId w:val=\"0\"/></w:numPr>") + Item("c", 4, 0), styles);
        Assert.Equal(["1.", null, "\u2022"], labels);
    }

    [Fact]
    public void Unknown_lists_and_levels_have_no_label()
    {
        Assert.Equal([null, null, null], Labels(Item("a", 99, 0) + Item("b", 1, 7) + Item("c", 1, -1)));
    }

    [Theory]
    [InlineData(1, "Decimal", "1")]
    [InlineData(7, "DecimalZero", "07")]
    [InlineData(12, "DecimalZero", "12")]
    [InlineData(28, "LowerLetter", "bb")]
    [InlineData(26, "UpperLetter", "Z")]
    [InlineData(1994, "UpperRoman", "MCMXCIV")]
    [InlineData(4, "LowerRoman", "iv")]
    [InlineData(4000, "UpperRoman", "4000")]
    [InlineData(0, "LowerLetter", "0")]
    [InlineData(2, "Ordinal", "2nd")]
    [InlineData(3, "Ordinal", "3rd")]
    [InlineData(11, "Ordinal", "11th")]
    [InlineData(21, "Ordinal", "21st")]
    [InlineData(5, "None", "")]
    public void Numbers_are_written_in_their_formats(int number, string format, string expected) =>
        Assert.Equal(expected, RaiCore.Pdf.Documents.Model.Numbering.Format(number, Enum.Parse<NumberFormat>(format)));

    [Fact]
    public void Tracked_changes_are_accepted_and_content_controls_unwrapped()
    {
        var document = Read(Document(
            "<w:p><w:r><w:t xml:space=\"preserve\">a </w:t></w:r><w:ins><w:r><w:t>inserted</w:t></w:r></w:ins><w:del><w:r><w:delText>deleted</w:delText></w:r></w:del>"
            + "<w:moveFrom><w:r><w:t>from</w:t></w:r></w:moveFrom><w:moveTo><w:r><w:t xml:space=\"preserve\"> to</w:t></w:r></w:moveTo>"
            + "<w:sdt><w:sdtContent><w:r><w:t xml:space=\"preserve\"> control</w:t></w:r></w:sdtContent></w:sdt>"
            + "<w:smartTag><w:r><w:t xml:space=\"preserve\"> tag</w:t></w:r></w:smartTag><w:r><w:rPr><w:vanish/></w:rPr><w:t>hidden</w:t><w:tab/></w:r>"
            + "<w:r><w:delText>stray deleted text</w:delText></w:r></w:p>"
            + "<w:sdt><w:sdtContent>" + P("block control") + "</w:sdtContent></w:sdt><w:del>" + P("deleted paragraph") + "</w:del><w:customXml>" + P("custom") + "</w:customXml>"));
        Assert.Equal(["a inserted to control tag", "block control", "custom"], Paragraphs(document).Select(Text));
    }

    [Fact]
    public void Run_content_tabs_breaks_symbols_and_special_hyphens()
    {
        var paragraph = Paragraphs(Read(Document(
            "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t><w:cr/><w:noBreakHyphen/><w:softHyphen/><w:sym w:font=\"Wingdings\" w:char=\"F0FC\"/>"
            + "<w:sym w:font=\"Symbol\" w:char=\"F061\"/><w:br w:type=\"page\"/><w:br w:type=\"column\"/><w:t>x\ty</w:t><w:ptab w:alignment=\"right\"/></w:r></w:p>")))[0];
        Assert.Equal("a\tb\nc\n\u2011\u00AD\u2713\u03B1\n\nx\ty\t", Text(paragraph));
        var breaks = paragraph.Inlines.OfType<BreakInline>().Select(b => b.Kind).ToList();
        Assert.Equal([BreakKind.Line, BreakKind.Line, BreakKind.Page, BreakKind.Column], breaks);
        Assert.Equal(WordAlignment.End, paragraph.Inlines.OfType<TabInline>().Last().PositionalAlignment);
    }

    [Fact]
    public void Complex_fields_keep_their_results_and_compute_page_fields()
    {
        var paragraph = Paragraphs(Read(Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> AUTHOR </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>Jane</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
            + "<w:r><w:t xml:space=\"preserve\"> page </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>PAGE \\* ROMAN</w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>IV</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>NUMPAGES</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
            + "<w:fldSimple w:instr=\" PAGEREF mark \\h \"><w:r><w:t>7</w:t></w:r></w:fldSimple></w:p>")))[0];
        Assert.Equal("Jane page IV7", Text(paragraph)); // NUMPAGES without a result has no fallback text.
        var fields = paragraph.Inlines.OfType<FieldInline>().ToList();
        Assert.Equal([FieldKind.Page, FieldKind.NumPages, FieldKind.PageRef], fields.Select(f => f.Kind));
        Assert.Equal(NumberFormat.UpperRoman, fields[0].NumberFormat);
        Assert.True(fields[0].Format.Bold); // The result's format.
        Assert.Equal("mark", fields[2].Argument);
        Assert.Equal("7", fields[2].CachedResult);
    }

    [Fact]
    public void Field_instructions_never_show_and_nested_fields_inside_them_are_dropped()
    {
        var paragraph = Paragraphs(Read(Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>IF </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
            + "<w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:instrText> = 1 \"one\"</w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>one</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>")))[0];
        Assert.Equal("one", Text(paragraph));
        Assert.Empty(paragraph.Inlines.OfType<FieldInline>());
    }

    [Fact]
    public void An_unterminated_field_does_not_swallow_the_following_paragraphs()
    {
        var paragraphs = Paragraphs(Read(Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:t>3</w:t></w:r></w:p>" + P("next") + "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>X</w:instrText></w:r></w:p>" + P("last")
            + "<w:p><w:r><w:fldChar w:fldCharType=\"end\"/><w:fldChar w:fldCharType=\"separate\"/></w:r></w:p>")));
        Assert.Equal(["", "next", "", "last", ""], paragraphs.Select(Text));
    }

    [Fact]
    public void Hyperlinks_by_relationship_anchor_and_field()
    {
        var paragraph = Paragraphs(Read(Document(
            "<w:p><w:hyperlink r:id=\"rLink\"><w:r><w:t>web</w:t></w:r></w:hyperlink><w:hyperlink w:anchor=\"there\"><w:r><w:t>inside</w:t></w:r></w:hyperlink>"
            + "<w:hyperlink r:id=\"rBad\"><w:r><w:t>script</w:t></w:r></w:hyperlink>"
            + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>HYPERLINK \"mailto:a@example.com\" \\o \"tip\"</w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>mail</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>HYPERLINK \\l \"top\"</w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>up</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>",
            relationships: [("rLink", "hyperlink", "https://example.com/a?b=1", true), ("rBad", "hyperlink", "javascript:alert(1)", true)])))[0];
        var links = paragraph.Inlines.OfType<HyperlinkInline>().ToList();
        Assert.Equal(4, links.Count);
        Assert.Equal(new Uri("https://example.com/a?b=1"), links[0].Uri);
        Assert.Equal("there", links[1].Anchor);
        Assert.Equal(new Uri("mailto:a@example.com"), links[2].Uri);
        Assert.Equal("top", links[3].Anchor);
        Assert.Equal("webinsidescriptmailup", Text(paragraph)); // The script link keeps its text, without the link.
    }

    [Fact]
    public void A_hyperlink_field_spanning_paragraphs_links_in_each()
    {
        var paragraphs = Paragraphs(Read(Document(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>HYPERLINK \"https://example.com\"</w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>one</w:t></w:r></w:p><w:p><w:r><w:t>two</w:t></w:r>"
            + "<w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:t>three</w:t></w:r></w:p>")));
        Assert.IsType<HyperlinkInline>(Assert.Single(paragraphs[0].Inlines));
        Assert.IsType<HyperlinkInline>(paragraphs[1].Inlines[0]);
        Assert.IsType<TextRun>(paragraphs[1].Inlines[1]);
    }

    [Fact]
    public void Bookmarks_skip_the_cursor_mark_and_body_level_ones_move_into_the_next_paragraph()
    {
        var paragraphs = Paragraphs(Read(Document(
            "<w:bookmarkStart w:id=\"1\" w:name=\"before\"/>" + "<w:p><w:bookmarkStart w:id=\"2\" w:name=\"_GoBack\"/><w:bookmarkStart w:id=\"3\" w:name=\"inside\"/><w:r><w:t>x</w:t></w:r></w:p>")));
        Assert.Equal(["before", "inside"], paragraphs[0].Inlines.OfType<BookmarkInline>().Select(b => b.Name));
    }

    [Fact]
    public void Tables_read_their_grid_merges_rows_and_cells()
    {
        var table = Assert.IsType<WordTable>(Read(Document(
            """
            <w:tbl><w:tblPr><w:tblW w:w="2500" w:type="pct"/><w:jc w:val="center"/><w:tblInd w:w="144" w:type="dxa"/>
              <w:tblBorders><w:top w:val="single" w:sz="16"/><w:insideH w:val="dashed" w:sz="4"/><w:insideV w:val="nil"/></w:tblBorders>
              <w:tblCellMar><w:left w:w="0" w:type="dxa"/></w:tblCellMar></w:tblPr>
              <w:tblGrid><w:gridCol w:w="1000"/><w:gridCol w:w="2000"/><w:gridCol w:w="3000"/></w:tblGrid>
              <w:tr><w:trPr><w:tblHeader/><w:trHeight w:val="400" w:hRule="exact"/><w:cantSplit/></w:trPr>
                <w:tc><w:tcPr><w:gridSpan w:val="2"/><w:shd w:val="clear" w:fill="FF0000"/><w:vAlign w:val="center"/><w:tcMar><w:top w:w="100" w:type="dxa"/></w:tcMar></w:tcPr><w:p/></w:tc>
                <w:tc><w:tcPr><w:vMerge w:val="restart"/><w:tcBorders><w:left w:val="nil"/><w:bottom w:val="double" w:sz="4"/></w:tcBorders></w:tcPr><w:p/></w:tc></w:tr>
              <w:tr><w:tc><w:p/></w:tc><w:sdt><w:sdtContent><w:tc><w:p/></w:tc></w:sdtContent></w:sdt><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc></w:tr>
              <w:tr><w:trPr><w:tblHeader/><w:gridBefore w:val="1"/></w:trPr><w:tc><w:tcPr><w:hMerge w:val="restart"/></w:tcPr><w:p/></w:tc>
                <w:tc><w:tcPr><w:hMerge w:val="continue"/></w:tcPr><w:p/></w:tc></w:tr>
              <w:tr><w:trPr><w:del/></w:trPr><w:tc><w:p/></w:tc></w:tr>
            </w:tbl>
            """)).Sections[0].Blocks[0]);
        Assert.Equal([50, 100, 150], table.Grid);
        Assert.Equal(0.5, table.WidthPercent);
        Assert.Equal(WordAlignment.Center, table.Alignment);
        Assert.Equal(7.2, table.Indent, 6);
        Assert.Equal(2, table.Borders.Top!.Value.Width);
        Assert.Equal(BorderLineStyle.Dashed, table.InsideHorizontal!.Value.Style);
        Assert.False(table.InsideVertical!.Value.IsVisible);
        Assert.Equal(new CellPadding(0, 5.4, 0, 0), table.Padding);
        Assert.Equal(3, table.Rows.Count); // The deleted row is left out.

        var header = table.Rows[0];
        Assert.True(header.IsHeader);
        Assert.True(header.CantSplit);
        Assert.True(header.ExactHeight);
        Assert.Equal(20, header.Height);
        Assert.Equal(2, header.Cells[0].ColumnSpan);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), header.Cells[0].Shading);
        Assert.Equal(CellVerticalAlignment.Center, header.Cells[0].VerticalAlignment);
        Assert.Equal(5, header.Cells[0].Padding!.Value.Top);
        Assert.Equal(-1, header.Cells[0].Padding!.Value.Left); // Unset: the table's.
        Assert.Equal(2, header.Cells[1].RowSpan); // Merged down into the second row.
        Assert.Equal(2, header.Cells[1].Column);
        Assert.False(header.Cells[1].Borders.Left!.Value.IsVisible);
        Assert.Equal(BorderLineStyle.Double, header.Cells[1].Borders.Bottom!.Value.Style);

        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal(2, table.Rows[1].Cells.Count); // The content control's cell, without the merged one.
        Assert.False(table.Rows[2].IsHeader); // Header rows only count from the top.
        Assert.Equal(1, table.Rows[2].GridBefore);
        Assert.Equal(2, Assert.Single(table.Rows[2].Cells).ColumnSpan);
        Assert.Equal(1, table.Rows[2].Cells[0].Column);
    }

    [Fact]
    public void Table_styles_apply_with_their_conditional_formats()
    {
        const string styles = """
            <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:pPr><w:spacing w:after="200"/></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="Other"><w:pPr><w:spacing w:after="300"/></w:pPr></w:style>
            <w:style w:type="table" w:styleId="Grid"><w:pPr><w:spacing w:after="0"/></w:pPr><w:rPr><w:sz w:val="16"/></w:rPr>
              <w:tblPr><w:tblStyleRowBandSize w:val="1"/><w:tblBorders><w:insideV w:val="single" w:sz="4"/></w:tblBorders></w:tblPr>
              <w:tblStylePr w:type="firstRow"><w:rPr><w:b/></w:rPr><w:tcPr><w:shd w:val="clear" w:fill="0000FF"/><w:tcBorders><w:bottom w:val="single" w:sz="12"/></w:tcBorders></w:tcPr></w:tblStylePr>
              <w:tblStylePr w:type="band1Horz"><w:tcPr><w:shd w:val="clear" w:fill="EEEEEE"/></w:tcPr></w:tblStylePr>
              <w:tblStylePr w:type="lastCol"><w:rPr><w:i/></w:rPr></w:tblStylePr></w:style>
            """;
        string Row(params string[] cells) => "<w:tr>" + string.Concat(cells.Select(c => $"<w:tc><w:p><w:r><w:t>{c}</w:t></w:r></w:p></w:tc>")) + "</w:tr>";
        var document = Read(Document(
            "<w:tbl><w:tblPr><w:tblStyle w:val=\"Grid\"/><w:tblLook w:firstRow=\"1\" w:lastColumn=\"1\" w:noHBand=\"0\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"1000\"/><w:gridCol w:w=\"1000\"/></w:tblGrid>"
            + Row("h1", "h2") + Row("a", "b") + Row("c", "d")
            + "<w:tr><w:tc><w:p><w:pPr><w:pStyle w:val=\"Other\"/></w:pPr><w:r><w:t>e</w:t></w:r></w:p></w:tc></w:tr></w:tbl>", styles));
        var table = Assert.IsType<WordTable>(document.Sections[0].Blocks[0]);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), table.Rows[0].Cells[0].Shading);
        Assert.Equal(1.5, table.Rows[0].Cells[0].Borders.Bottom!.Value.Width);
        Assert.Equal(PdfColor.FromRgb(0xEE, 0xEE, 0xEE), table.Rows[1].Cells[0].Shading); // The first band after the header.
        Assert.Null(table.Rows[2].Cells[0].Shading); // The second band has no format.
        Assert.True(table.InsideVertical!.Value.IsVisible);

        var paragraphs = Paragraphs(document);
        var h1 = Assert.IsType<TextRun>(paragraphs[0].Inlines[0]);
        Assert.True(h1.Format.Bold);
        Assert.Equal(8, h1.Format.FontSize);
        Assert.False(h1.Format.Italic);
        Assert.True(Assert.IsType<TextRun>(paragraphs[1].Inlines[0]).Format.Italic); // Last column.
        Assert.Equal(0, paragraphs[2].Format.SpaceAfter); // The table style over the default paragraph style...
        Assert.Equal(15, paragraphs[6].Format.SpaceAfter); // ...but not over another style.
    }

    [Theory]
    [InlineData("04A0", true, false, true, false, true, false)]
    [InlineData("0000", false, false, false, false, true, true)]
    [InlineData("0660", true, true, false, false, false, false)]
    public void Table_look_bits(string value, bool firstRow, bool lastRow, bool firstColumn, bool lastColumn, bool rowBands, bool columnBands)
    {
        const string styles = """
            <w:style w:type="table" w:styleId="S">
              <w:tblStylePr w:type="firstRow"><w:rPr><w:b/></w:rPr></w:tblStylePr><w:tblStylePr w:type="lastRow"><w:rPr><w:i/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="firstCol"><w:rPr><w:caps/></w:rPr></w:tblStylePr><w:tblStylePr w:type="lastCol"><w:rPr><w:strike/></w:rPr></w:tblStylePr>
              <w:tblStylePr w:type="band1Horz"><w:rPr><w:smallCaps/></w:rPr></w:tblStylePr><w:tblStylePr w:type="band1Vert"><w:rPr><w:vanish w:val="0"/><w:u/></w:rPr></w:tblStylePr></w:style>
            """;
        string Cell(string text) => $"<w:tc><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc>";
        var document = Read(Document(
            $"<w:tbl><w:tblPr><w:tblStyle w:val=\"S\"/><w:tblLook w:val=\"{value}\"/></w:tblPr><w:tblGrid><w:gridCol/><w:gridCol/><w:gridCol/></w:tblGrid>"
            + "<w:tr>" + Cell("a") + Cell("b") + Cell("c") + "</w:tr><w:tr>" + Cell("d") + Cell("e") + Cell("f") + "</w:tr><w:tr>" + Cell("g") + Cell("h") + Cell("i") + "</w:tr></w:tbl>", styles));
        var runs = Paragraphs(document).Select(p => Assert.IsType<TextRun>(p.Inlines[0]).Format).ToList();
        Assert.Equal(firstRow, runs[1].Bold);
        Assert.Equal(lastRow, runs[7].Italic);
        Assert.Equal(firstColumn, runs[3].Caps);
        Assert.Equal(lastColumn, runs[5].Strike);
        var bandRow = firstRow ? 1 : 0; // Bands count after the first row and column when those have their own format.
        var bandColumn = firstColumn ? 1 : 0;
        Assert.Equal(rowBands, runs[(bandRow * 3) + 1].SmallCaps);
        Assert.Equal(columnBands, runs[3 + bandColumn].Underline != UnderlineStyle.None);
    }

    [Fact]
    public void Sections_read_page_setup_start_numbering_and_inherit_headers()
    {
        var header = Part("hdr", P("Header"));
        var first = Part("hdr", P("First"));
        var footer = Part("ftr", P("Footer"));
        var document = Read(Document(
            P("one") + """
            <w:p><w:pPr><w:sectPr><w:headerReference w:type="default" r:id="rH"/><w:headerReference w:type="first" r:id="rF"/><w:footerReference w:type="even" r:id="rE"/>
              <w:footerReference w:type="default" r:id="rMissing"/><w:titlePg/><w:pgSz w:w="11906" w:h="16838"/>
              <w:pgMar w:top="-1440" w:right="1000" w:bottom="1000" w:left="1000" w:header="500" w:footer="400" w:gutter="400"/>
              <w:pgNumType w:start="5" w:fmt="lowerRoman"/><w:cols w:num="2"/></w:sectPr></w:pPr></w:p>
            """ + P("two") + """
            <w:sectPr><w:type w:val="continuous"/><w:pgSz w:w="16838" w:h="11906" w:orient="landscape"/><w:headerReference w:type="default" r:id="rF"/></w:sectPr>
            """,
            parts: [("word/header1.xml", header), ("word/header2.xml", first), ("word/footer1.xml", footer)],
            relationships: [("rH", "header", "header1.xml", false), ("rF", "header", "header2.xml", false), ("rE", "footer", "footer1.xml", false),
                ("rMissing", "footer", "nowhere.xml", false)]));
        Assert.Equal(2, document.Sections.Count);
        var a = document.Sections[0];
        Assert.Equal(["one", ""], Paragraphs(a.Blocks).Select(Text));
        Assert.Equal(595.3, a.PageWidth, 1);
        Assert.Equal(841.9, a.PageHeight, 1);
        Assert.Equal(72, a.MarginTop); // A negative top margin is its size.
        Assert.Equal(70, a.MarginLeft); // With the gutter.
        Assert.Equal(25, a.HeaderDistance);
        Assert.Equal(20, a.FooterDistance);
        Assert.True(a.TitlePage);
        Assert.Equal(5, a.PageNumberStart);
        Assert.Equal(NumberFormat.LowerRoman, a.PageNumberFormat);
        Assert.Equal(2, a.Columns.Count);
        Assert.Equal("Header", Text(Paragraphs(a.Headers[HeaderFooterKind.Default]).Single()));
        Assert.Equal("First", Text(Paragraphs(a.Headers[HeaderFooterKind.First]).Single()));
        Assert.Equal("Footer", Text(Paragraphs(a.Footers[HeaderFooterKind.Even]).Single()));
        Assert.False(a.Footers.ContainsKey(HeaderFooterKind.Default)); // Its part is missing.

        var b = document.Sections[1];
        Assert.Equal(SectionStart.Continuous, b.Start);
        Assert.Equal(841.9, b.PageWidth, 1);
        Assert.Equal("First", Text(Paragraphs(b.Headers[HeaderFooterKind.Default]).Single()));
        Assert.Same(a.Headers[HeaderFooterKind.First], b.Headers[HeaderFooterKind.First]); // Inherited.
        Assert.Same(a.Footers[HeaderFooterKind.Even], b.Footers[HeaderFooterKind.Even]);
    }

    [Fact]
    public void Absurd_page_sizes_and_margins_leave_room_for_text()
    {
        var section = Read(Document(P("x") + "<w:sectPr><w:pgSz w:w=\"10\" w:h=\"99999999\"/><w:pgMar w:top=\"200000\" w:bottom=\"200000\" w:left=\"5000\" w:right=\"5000\"/></w:sectPr>")).Sections[0];
        Assert.Equal(72, section.PageWidth);
        Assert.Equal(14400, section.PageHeight);
        Assert.Equal(72, section.PageWidth - section.MarginLeft - section.MarginRight, 6);
        Assert.True(section.PageHeight - section.MarginTop - section.MarginBottom >= 71.9);
    }

    [Fact]
    public void Settings_and_title_are_read()
    {
        var document = Read(Package(
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"a\" Type=\"{R}/officeDocument\" Target=\"/word/main.xml\"/>"
                + "<Relationship Id=\"b\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>"),
            ("word/main.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{P("x")}</w:body></w:document>"),
            ("word/_rels/main.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"s\" Type=\"{R}/settings\" Target=\"settings.xml\"/></Relationships>"),
            ("word/settings.xml", $"<w:settings xmlns:w=\"{W}\"><w:evenAndOddHeaders/><w:defaultTabStop w:val=\"360\"/><w:footnotePr><w:numFmt w:val=\"upperLetter\"/></w:footnotePr><w:endnotePr><w:numFmt w:val=\"decimal\"/></w:endnotePr></w:settings>"),
            ("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title> My title </dc:title></cp:coreProperties>")));
        Assert.True(document.EvenAndOddHeaders);
        Assert.Equal(18, document.DefaultTabStop);
        Assert.Equal(NumberFormat.UpperLetter, document.FootnoteFormat);
        Assert.Equal(NumberFormat.Decimal, document.EndnoteFormat);
        Assert.Equal("My title", document.Title);
    }

    private static byte[] Png() => System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAADElEQVR4nGP8zwACAAYIAQFazwZIAAAAAElFTkSuQmCC");

    private static string Drawing(string id, string container = "inline", string extra = "", string uri = "http://schemas.openxmlformats.org/drawingml/2006/picture") =>
        $"<w:r><w:drawing><wp:{container}>{extra}<wp:extent cx=\"914400\" cy=\"457200\"/><wp:docPr id=\"1\" name=\"p\" descr=\"A picture\"/>"
        + $"<a:graphic><a:graphicData uri=\"{uri}\"><pic:pic><pic:blipFill><a:blip r:embed=\"{id}\"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:{container}></w:drawing></w:r>";

    [Fact]
    public void Images_inline_and_anchored()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var document = Read(Document(
            "<w:p>" + Drawing("rImg") + Drawing("rImg", "anchor", "<wp:positionH relativeFrom=\"margin\"><wp:align>right</wp:align></wp:positionH><wp:wrapSquare/>")
            + Drawing("rImg", "anchor", "<wp:positionH relativeFrom=\"column\"><wp:posOffset>1270000</wp:posOffset></wp:positionH><wp:wrapTopAndBottom/>")
            + Drawing("rMissing") + Drawing("rExternal")
            + Drawing("rImg", uri: "http://schemas.openxmlformats.org/drawingml/2006/chart") + Drawing("rImg", uri: "http://schemas.openxmlformats.org/drawingml/2006/diagram")
            + "<w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\" style=\"width:72pt;height:48px\"><v:imagedata r:id=\"rImg\" o:title=\"\" xmlns:o=\"urn:schemas-microsoft-com:office:office\"/></v:shape></w:pict></w:r></w:p>",
            parts: [("word/media/a.png", Png())],
            relationships: [("rImg", "image", "media/a.png", false), ("rMissing", "image", "media/none.png", false), ("rExternal", "image", "https://example.com/a.png", true)]),
            diagnostics: diagnostics);
        var images = Paragraphs(document)[0].Inlines.OfType<ImageInline>().ToList();
        Assert.Equal(4, images.Count);
        Assert.Equal((72, 36), (images[0].Width, images[0].Height));
        Assert.Equal(ImagePlacement.Inline, images[0].Placement);
        Assert.Equal("A picture", images[0].AlternativeText);
        Assert.Equal("word/media/a.png", images[0].Name);
        Assert.Equal(ImagePlacement.Float, images[1].Placement);
        Assert.Equal(WordAlignment.End, images[1].Alignment);
        Assert.Equal(ImagePlacement.Block, images[2].Placement);
        Assert.Equal(100, images[2].Offset);
        Assert.Equal((72, 36), (images[3].Width, images[3].Height)); // VML: 48 pixels are 36 points.
        Assert.Same(images[0].Data, images[1].Data); // Read once.
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("media/none.png", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("linked", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message == "A chart without its chart part is left out.");
        Assert.Contains(diagnostics, d => d.Message == "A SmartArt diagram is left out.");
    }

    [Fact]
    public void Text_boxes_are_anchored_blocks_and_equations_plain_text()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var document = Read(Document(
            "<w:p><w:r><w:t>Body</w:t></w:r><mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"wps\">"
            + "<w:r><w:drawing><wp:anchor><wp:extent cx=\"1\" cy=\"1\"/><wp:wrapNone/><a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">"
            + "<wps:wsp xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:txbx><w:txbxContent>" + P("In the box") + "</w:txbxContent></wps:txbx></wps:wsp>"
            + "</a:graphicData></a:graphic></wp:anchor></w:drawing></w:r></mc:Choice><mc:Fallback><w:r><w:t>fallback</w:t></w:r></mc:Fallback></mc:AlternateContent>"
            + "<m:oMath xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><m:r><m:t>a</m:t></m:r><m:r><m:t>+b</m:t></m:r></m:oMath></w:p>"), diagnostics: diagnostics);
        var paragraph = Assert.Single(Paragraphs(document));
        Assert.Equal("Bodya+b", Text(paragraph));
        var box = Assert.Single(paragraph.Inlines.OfType<AnchoredBlocks>());
        Assert.Equal("In the box", Text(Assert.IsType<WordParagraph>(Assert.Single(box.Blocks))));
        Assert.Contains(diagnostics, d => d.Message.Contains("text box", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("equation", StringComparison.Ordinal));
    }

    [Fact]
    public void Notes_are_read_without_their_separators()
    {
        var notes = Part("footnotes", "<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>"
            + "<w:footnote w:id=\"1\"><w:p><w:r><w:footnoteRef/></w:r><w:r><w:t xml:space=\"preserve\"> Note one.</w:t></w:r></w:p></w:footnote>");
        var document = Read(Document(
            "<w:p><w:r><w:t>x</w:t></w:r><w:r><w:footnoteReference w:id=\"1\"/></w:r><w:r><w:footnoteReference w:customMarkFollows=\"1\" w:id=\"1\"/><w:t>*</w:t></w:r></w:p>",
            parts: [("word/footnotes.xml", notes)], relationships: [("rN", "footnotes", "footnotes.xml", false)]));
        Assert.Equal(["1"], document.Footnotes.Keys);
        var note = Assert.IsType<WordParagraph>(Assert.Single(document.Footnotes["1"]));
        Assert.IsType<NoteMark>(note.Inlines[0]);
        var references = Paragraphs(document)[0].Inlines.OfType<NoteReference>().ToList();
        Assert.Equal(2, references.Count);
        Assert.Null(references[0].CustomMark);
        Assert.Equal(string.Empty, references[1].CustomMark);
    }

    [Fact]
    public void Strict_documents_are_read()
    {
        var document = Read(Fixtures.Read(Path.Combine("Documents", "strict.docx")));
        Assert.Equal(["Hello, strict document.", "Italic"], Paragraphs(document).Select(Text));
        Assert.True(Assert.IsType<TextRun>(Paragraphs(document)[1].Inlines[0]).Format.Italic);
    }

    [Theory]
    [InlineData("word/document.xml", "<w:wrong")]
    [InlineData("word/styles.xml", "<oops")]
    public void Malformed_xml_throws_a_format_exception(string part, string xml)
    {
        var docx = Package(
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"a\" Type=\"{R}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/_rels/document.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"s\" Type=\"{R}/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("word/document.xml", part == "word/document.xml" ? xml : $"<w:document xmlns:w=\"{W}\"/>"),
            ("word/styles.xml", part == "word/styles.xml" ? xml : $"<w:styles xmlns:w=\"{W}\"/>"));
        var exception = Assert.Throws<DocumentFormatException>(() => Read(docx));
        Assert.Contains(part, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parts_are_decoded_by_their_byte_order_mark_not_their_declaration()
    {
        var utf16 = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><w:document xmlns:w=\"{W}\"><w:body>{P("\u00E6\u00F8\u00E5")}</w:body></w:document>")).ToArray();
        var docx = Package(("word/document.xml", utf16));
        Assert.Equal("\u00E6\u00F8\u00E5", Text(Paragraphs(Read(docx)).Single()));
        Assert.Equal("<a/>", DocxPackage.Decode([.. "\uFEFF"u8.ToArray(), .. "<a/>"u8.ToArray()]).TrimStart('\uFEFF'));
        Assert.Equal("<a/>", DocxPackage.Decode([0, (byte)'<', 0, (byte)'a', 0, (byte)'/', 0, (byte)'>']));
        Assert.Equal("<a/>", DocxPackage.Decode([(byte)'<', 0, (byte)'a', 0, (byte)'/', 0, (byte)'>', 0]));
        Assert.Equal("<a/>", DocxPackage.Decode([0xFE, 0xFF, 0, (byte)'<', 0, (byte)'a', 0, (byte)'/', 0, (byte)'>']));
    }

    [Theory]
    [InlineData("word", "media/a.png", "word/media/a.png")]
    [InlineData("word", "../media/a.png", "media/a.png")]
    [InlineData("word", "/word/media/a%20b.png", "word/media/a b.png")]
    [InlineData("word", "./x/../a.png", "word/a.png")]
    [InlineData("", "../../a.png", "a.png")]
    [InlineData("word", "media\\a.png", "word/media/a.png")]
    public void Relationship_targets_resolve_against_their_part(string folder, string target, string expected) =>
        Assert.Equal(expected, DocxPackage.ResolveTarget(folder, target));

    [Fact]
    public void Field_instructions_split_into_words_and_quoted_strings()
    {
        Assert.Equal(["HYPERLINK", "https://a b", "\\l", "x"], DocxReader.Tokenize(" HYPERLINK  \"https://a b\" \\l \"x"));
        Assert.Equal(["a", "b"], DocxReader.Tokenize("a\"b\""));
    }

    [Theory]
    [InlineData("width:72pt;height:1in", 72, 72)]
    [InlineData("height:96px; WIDTH: 2cm", 56.69, 72)]
    [InlineData("position:absolute", 0, 0)]
    [InlineData("width:-5pt;height:abc", 0, 0)]
    public void Vml_shape_sizes(string style, double width, double height)
    {
        var (w, h) = DocxReader.VmlSize(style);
        Assert.Equal(width, w, 2);
        Assert.Equal(height, h, 2);
    }
}
