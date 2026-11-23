using System.Text;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using static RaiCore.Pdf.Tests.WordDocuments.Rtf;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Reading RTF into the word-processing model: the tokenizer and groups, code pages and Unicode, the font, color and
/// style tables, character and paragraph formatting, lists, tables, sections, headers and footers, fields, pictures,
/// bookmarks and notes, and the limits on hostile input.
/// </summary>
public class RtfReaderTests
{
    private static readonly PdfColor Red = PdfColor.FromRgb(255, 0, 0);
    private static readonly PdfColor Blue = PdfColor.FromRgb(0, 0, 255);
    private static readonly PdfColor Yellow = PdfColor.FromRgb(255, 255, 0);

    /// <summary>A Unicode escape (written here so that no tool turns it into the character).</summary>
    private static string U(int value) => "\\" + "u" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Character_formatting_is_scoped_by_groups_and_reset_by_plain()
    {
        var document = ReadBody(@"\f1\fs20 base {\b bold {\i both}} {\ul single}{\uldb double}{\uld dotted}{\uldash dashed}{\ulwave wavy}{\ul\ulnone none}{\ul0 off}"
            + @"{\strike struck}{\striked1 twice}{\cf2 red}{\highlight5 marked}{\chcbpat4 shaded}{\super up}{\sub down}{\super\nosupersub base2}{\caps caps}{\scaps small}"
            + @"{\expnd8 wide}{\expndtw-20 narrow}{\up12 raised}{\dn12 lowered}{\fs48\f2 big}{\b\plain plain}{\b\b0 notbold}\par");
        Assert.Equal("Times New Roman", Run(document, "base").Format.FontFamily);
        Assert.Equal(10, Run(document, "base").Format.FontSize);
        Assert.True(Run(document, "bold").Format.Bold);
        Assert.False(Run(document, "bold").Format.Italic);
        Assert.True(Run(document, "both").Format.Bold && Run(document, "both").Format.Italic);
        Assert.Equal(UnderlineStyle.Single, Run(document, "single").Format.Underline);
        Assert.Equal(UnderlineStyle.Double, Run(document, "double").Format.Underline);
        Assert.Equal(UnderlineStyle.Dotted, Run(document, "dotted").Format.Underline);
        Assert.Equal(UnderlineStyle.Dashed, Run(document, "dashed").Format.Underline);
        Assert.Equal(UnderlineStyle.Wavy, Run(document, "wavy").Format.Underline);
        Assert.Equal(UnderlineStyle.None, Run(document, "none").Format.Underline);
        Assert.Equal(UnderlineStyle.None, Run(document, "off").Format.Underline);
        Assert.True(Run(document, "struck").Format.Strike);
        Assert.True(Run(document, "twice").Format.Strike);
        Assert.Equal(Red, Run(document, "red").Format.Color);
        Assert.Null(Run(document, "base").Format.Color);
        Assert.Equal(Yellow, Run(document, "marked").Format.Background);
        Assert.Equal(Blue, Run(document, "shaded").Format.Background);
        Assert.Equal(VerticalPosition.Superscript, Run(document, "up").Format.Position);
        Assert.Equal(VerticalPosition.Subscript, Run(document, "down").Format.Position);
        Assert.Equal(VerticalPosition.Baseline, Run(document, "base2").Format.Position);
        Assert.True(Run(document, "caps").Format.Caps);
        Assert.True(Run(document, "small").Format.SmallCaps);
        Assert.Equal(2, Run(document, "wide").Format.LetterSpacing); // Quarter points.
        Assert.Equal(-1, Run(document, "narrow").Format.LetterSpacing); // Twips.
        Assert.Equal(6, Run(document, "raised").Format.BaselineShift);
        Assert.Equal(-6, Run(document, "lowered").Format.BaselineShift);
        Assert.Equal(24, Run(document, "big").Format.FontSize);
        Assert.Equal("Courier New", Run(document, "big").Format.FontFamily);
        var plain = Run(document, "plain").Format;
        Assert.False(plain.Bold);
        Assert.Equal(12, plain.FontSize); // \plain: 12 points in the default font.
        Assert.Equal("Arial", plain.FontFamily);
        Assert.False(Run(document, "notbold").Format.Bold);
    }

    [Fact]
    public void Hidden_and_deleted_text_is_left_out_and_inserted_text_kept()
    {
        var paragraph = Paragraphs(ReadBody(@"shown{\v  hidden}{\v\v0  again} {\deleted gone }{\revised added}\par"))[0];
        Assert.Equal("shown again added", Text(paragraph));
    }

    [Fact]
    public void Unicode_escapes_skip_their_fallback_characters()
    {
        var body = "a" + U(233) + "?b " + @"\uc2" + U(239) + @"\'3f\'3fc " + @"\uc0" + U(8364) + "d " + @"\uc1" + U(-3913) + "?e " + U(-10179) + "?" + U(-8704) + "?f"
            + " {" + @"\uc3" + "x} " + U(65) + @"{\b B}\par";
        var text = Text(Paragraphs(ReadBody(body))[0]);
        Assert.Equal("a\u00E9b \u00EFc \u20ACd \uF0B7e \U0001F600f x AB", text);
    }

    [Fact]
    public void Unpaired_surrogates_and_escapes_out_of_range_become_replacement_characters()
    {
        // Found by fuzzing: \u-664401 made a lone surrogate, which text layout cannot itemize.
        var document = ReadBody(U(-664401) + "?a" + U(-10179) + "?b" + U(-8704) + "?c" + U(70000) + "?d" + U(-10179) + @"?{\b " + U(-8704) + "?}"
            + @"{\*\bkmkstart m" + U(-10179) + "?}" + @"{\listtext " + U(-8704) + @"?\tab}x\par", @"{\info{\title t" + U(-10179) + "?}}");
        var paragraph = Paragraphs(document)[0];
        Assert.Equal("\uFFFDa\uFFFDb\uFFFDc\uFFFDd\uFFFD\uFFFDx", Text(paragraph));
        Assert.Equal("m\uFFFD", Assert.Single(paragraph.Inlines.OfType<BookmarkInline>()).Name);
        Assert.Equal("\uFFFD", paragraph.Label!.Text);
        Assert.Equal("t\uFFFD", document.Title);
        Assert.Contains("x", Docx.AllText(RtfToPdf.ConvertToBytes(Document(U(-664401) + "?x"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_decoded_with_the_code_pages_of_the_document_and_its_fonts()
    {
        var document = ReadBody(@"caf\'e9 {\f4 \'cf\'f0\'e8} {\f5 \'82\'b1\'82\'f1} {\f3 \'61\'b7} {\f2 \'e9}\par");
        Assert.Equal("caf\u00E9 \u041F\u0440\u0438 \u3053\u3093 \u03B1\u2022 \u00E9", Text(Paragraphs(document)[0]));
        Assert.Null(Run(document, "\u03B1").Format.FontFamily); // Symbol text is mapped to Unicode in the default font.

        var cyrillic = Read(Encoding.Latin1.GetBytes(@"{\rtf1\ansi\ansicpg1251{\fonttbl{\f0 Arial;}{\f1\cpg1253 Greek;}}\f0 \'cf\'f0 {\f1 \'e1}}"));
        Assert.Equal("\u041F\u0440 \u03B1", Text(Paragraphs(cyrillic)[0]));
        var mac = Read(Encoding.Latin1.GetBytes(@"{\rtf1\mac \'8e}"));
        Assert.Equal("\u00E9", Text(Paragraphs(mac)[0]));
        var unknown = Read(Encoding.Latin1.GetBytes(@"{\rtf1\ansi\ansicpg99999 \'e9}"));
        Assert.Equal("\u00E9", Text(Paragraphs(unknown)[0])); // An unknown code page falls back to Windows-1252.
    }

    [Fact]
    public void Unknown_destinations_are_skipped_and_unknown_words_ignored()
    {
        var document = ReadBody(@"a{\*\unknowndest skipped {\nested too}}b\unknownword c{\*\generator x}{\nonesttables gone}{\*\bkmkend m}d\bin3 {}}e\par");
        Assert.Equal("abcde", Text(Paragraphs(document)[0]));
    }

    [Fact]
    public void The_unicode_version_of_upr_is_read_and_its_ansi_version_left_out()
    {
        var document = ReadBody(@"{\upr{\b Ansi fallback}{\*\ud{\b Unicode version}}} after\par{\upr{only ansi}}x\par");
        Assert.Equal("Unicode version after", Text(Paragraphs(document)[0]));
        Assert.True(Run(document, "Unicode").Format.Bold);
        Assert.Equal("x", Text(Paragraphs(document)[1]));
    }

    [Fact]
    public void Malformed_stories_and_fields_in_one_group_do_not_take_over_the_document()
    {
        var document = ReadBody(@"a{\footnote\header note}b{\field\footnote x}c{\footnote\field{\*\fldinst PAGE}}d{\field\field{\*\fldinst HYPERLINK ""https://x.org""}{\fldrslt e}}f\par g\par");
        var paragraphs = Paragraphs(document);
        Assert.Equal(2, paragraphs.Count);
        Assert.StartsWith("a", Text(paragraphs[0]), StringComparison.Ordinal);
        Assert.EndsWith("f", Text(paragraphs[0]), StringComparison.Ordinal);
        Assert.Equal("g", Text(paragraphs[1]));
        Assert.DoesNotContain(paragraphs[1].Inlines, i => i is HyperlinkInline);

        // A second field or story in the same group is left out; the first still ends with the group.
        var twice = ReadBody(@"{\field{\*\fldinst HYPERLINK ""https://x.org""}{\fldrslt e}\field}f\par{\footnote\footnote y}g\par h\par");
        Assert.Equal(["ef", "g", "h"], Paragraphs(twice).Select(Text));
        Assert.All(Paragraphs(twice).Skip(1), p => Assert.DoesNotContain(p.Inlines, i => i is HyperlinkInline));
        Assert.IsType<HyperlinkInline>(Paragraphs(twice)[0].Inlines[0]);
    }

    [Fact]
    public void Escaped_characters_and_special_words_become_text()
    {
        var text = Text(Paragraphs(ReadBody(@"\{x\}\\ a\~b c\_d e\-f\emdash\endash\lquote\rquote\ldblquote\rdblquote\bullet\emspace\enspace\qmspace\zwj\zwnj\zwbo\ltrmark\rtlmark\par"))[0]);
        Assert.Equal("{x}\\ a\u00A0b c\u2011d ef\u2014\u2013\u2018\u2019\u201C\u201D\u2022\u2003\u2002\u2005\u200D\u200C\u200B\u200E\u200F", text);
    }

    [Fact]
    public void Tabs_breaks_and_line_ends()
    {
        var paragraph = Paragraphs(ReadBody("a\\tab b\tc\\line d\\page e\\column f\r\ng\\\r\nh\\par"))[0];
        Assert.Collection(paragraph.Inlines,
            i => Assert.Equal("a", Assert.IsType<TextRun>(i).Text),
            i => Assert.IsType<TabInline>(i),
            i => Assert.Equal("b", Assert.IsType<TextRun>(i).Text),
            i => Assert.IsType<TabInline>(i),
            i => Assert.Equal("c", Assert.IsType<TextRun>(i).Text),
            i => Assert.Equal(BreakKind.Line, Assert.IsType<BreakInline>(i).Kind),
            i => Assert.Equal("d", Assert.IsType<TextRun>(i).Text),
            i => Assert.Equal(BreakKind.Page, Assert.IsType<BreakInline>(i).Kind),
            i => Assert.Equal("e", Assert.IsType<TextRun>(i).Text),
            i => Assert.Equal(BreakKind.Column, Assert.IsType<BreakInline>(i).Kind),
            i => Assert.Equal("fg", Assert.IsType<TextRun>(i).Text));
        Assert.Equal("h", Text(Paragraphs(ReadBody("a\\tab b\tc\\line d\\page e\\column f\r\ng\\\r\nh\\par"))[1])); // \ before a line end: \par.
    }

    [Fact]
    public void Font_and_color_tables_in_both_forms()
    {
        var document = Read(Encoding.Latin1.GetBytes(@"{\rtf1\ansi{\fonttbl\f0\fswiss Helvetica;\f1\fmodern Courier;}{\colortbl;\red255\green0\blue0;;\red0\green0\blue255;}"
            + @"\f1\cf1 one {\cf2 two} {\cf3\f0 three} {\cf9\f9 four}\par}"));
        Assert.Equal("Courier", Run(document, "one").Format.FontFamily);
        Assert.Equal(Red, Run(document, "one").Format.Color);
        Assert.Null(Run(document, "two").Format.Color); // An empty entry is automatic.
        Assert.Equal(Blue, Run(document, "three").Format.Color);
        Assert.Equal("Helvetica", Run(document, "three").Format.FontFamily);
        Assert.Null(Run(document, "four").Format.Color);
        Assert.Equal("Helvetica", Run(document, "four").Format.FontFamily); // An unknown font is the default one (\deff, else font 0).
    }

    [Fact]
    public void Styles_apply_through_their_base_styles()
    {
        const string styles = @"{\stylesheet{\ql\sa200\fs22 Normal;}{\s1\sbasedon0\snext0\qc\b\fs32\cf2 heading 1;}{\s2\sbasedon1\i\li720 Derived;}"
            + @"{\s5\sbasedon6\b Loop A;}{\s6\sbasedon5\i Loop B;}{\*\cs10\additive\ul\cf4 Link;}{\*\ts11\tsrowd\b Table Style;}}";
        var document = ReadBody(@"\pard\s1 Title\par\pard\s2 Derived text {\cs10 styled}\par\pard\s5 loop\par\pard\s0 normal\par\pard\s9 unknown\par", styles);
        var title = Paragraph(document, "Title");
        Assert.Equal(WordAlignment.Center, title.Format.Alignment);
        Assert.Equal(10, title.Format.SpaceAfter); // From Normal.
        Assert.Equal(0, title.Format.OutlineLevel); // A heading by its name.
        Assert.Equal("s1", title.Format.StyleId);
        Assert.True(Run(document, "Title").Format.Bold);
        Assert.Equal(16, Run(document, "Title").Format.FontSize);
        Assert.Equal(Red, Run(document, "Title").Format.Color);
        var derived = Paragraph(document, "Derived");
        Assert.Equal(WordAlignment.Center, derived.Format.Alignment);
        Assert.Equal(36, derived.Format.IndentStart);
        Assert.Equal(0, derived.Format.OutlineLevel); // Inherited from heading 1.
        Assert.True(Run(document, "Derived").Format.Italic && Run(document, "Derived").Format.Bold);
        Assert.Equal(UnderlineStyle.Single, Run(document, "styled").Format.Underline);
        Assert.Equal(Blue, Run(document, "styled").Format.Color);
        Assert.True(Run(document, "styled").Format.Italic); // A character style adds to the paragraph's formatting.
        Assert.True(Run(document, "loop").Format.Bold && Run(document, "loop").Format.Italic);
        Assert.Null(Paragraph(document, "normal").Format.OutlineLevel);
        Assert.Equal(11, Run(document, "normal").Format.FontSize);
        Assert.Equal(12, Run(document, "unknown").Format.FontSize);
    }

    [Fact]
    public void Paragraph_formatting_is_read_and_reset_by_pard()
    {
        var document = ReadBody(@"\qc\li1440\ri720\fi-360\sb240\sa120\keep\keepn\pagebb\widctlpar\contextualspace one\par"
            + @"\pard\qr two\par\pard\qj\sl360\slmult1 three\par\pard\sl-300 four\par\pard\sl300 five\par\pard\sl0 six\par\pard\qd\rtlpar\outlinelevel2 seven\par"
            + @"\pard\li-99999 eight\par");
        var one = Paragraph(document, "one").Format;
        Assert.Equal(WordAlignment.Center, one.Alignment);
        Assert.Equal(72, one.IndentStart);
        Assert.Equal(36, one.IndentEnd);
        Assert.Equal(-18, one.FirstLineIndent);
        Assert.Equal(12, one.SpaceBefore);
        Assert.Equal(6, one.SpaceAfter);
        Assert.True(one.KeepLines && one.KeepWithNext && one.PageBreakBefore && one.WidowControl && one.ContextualSpacing);
        var two = Paragraph(document, "two").Format;
        Assert.Equal(WordAlignment.End, two.Alignment);
        Assert.Equal(0, two.IndentStart);
        Assert.False(two.KeepLines || two.KeepWithNext || two.PageBreakBefore || two.WidowControl);
        var three = Paragraph(document, "three").Format;
        Assert.Equal(WordAlignment.Justify, three.Alignment);
        Assert.Equal((1.5, LineSpacingRule.Multiple), (three.LineSpacing, three.LineRule));
        Assert.Equal((15.0, LineSpacingRule.Exact), (Paragraph(document, "four").Format.LineSpacing, Paragraph(document, "four").Format.LineRule));
        Assert.Equal((15.0, LineSpacingRule.AtLeast), (Paragraph(document, "five").Format.LineSpacing, Paragraph(document, "five").Format.LineRule));
        Assert.Equal((1.0, LineSpacingRule.Multiple), (Paragraph(document, "six").Format.LineSpacing, Paragraph(document, "six").Format.LineRule));
        var seven = Paragraph(document, "seven").Format;
        Assert.Equal(WordAlignment.Justify, seven.Alignment);
        Assert.True(seven.RightToLeft);
        Assert.Equal(2, seven.OutlineLevel);
        Assert.Equal(-1584, Paragraph(document, "eight").Format.IndentStart); // Clamped to 22 inches.
    }

    [Fact]
    public void Document_defaults_apply_to_plain_and_pard()
    {
        var document = ReadBody(@"\pard\plain one\par", @"{\*\defchp\fs20\b}{\*\defpap\sa160\qc}");
        Assert.Equal(10, Run(document, "one").Format.FontSize);
        Assert.True(Run(document, "one").Format.Bold);
        Assert.Equal(8, Paragraph(document, "one").Format.SpaceAfter);
        Assert.Equal(WordAlignment.Center, Paragraph(document, "one").Format.Alignment);
    }

    [Fact]
    public void Paragraph_borders_and_shading()
    {
        var document = ReadBody(@"\brdrt\brdrs\brdrw30\brdrcf2\brdrb\brdrdb\brdrw10\brdrl\brdrdot\brdrr\brdrdash\brdrw20\brdrbtw\brdrs\cbpat5 boxed\par"
            + @"\pard\box\brdrw10\brdrth\shading5000\cfpat4 shaded\par\pard\brdrt\brdrnone nothing\par\pard\brdrt\brdrw40\brdrhair\shading2500 hair\par");
        var boxed = Paragraph(document, "boxed").Format;
        Assert.Equal(new BorderLine(1.5, Red, BorderLineStyle.Single), boxed.Borders.Top);
        Assert.Equal(new BorderLine(0.5, PdfColor.Black, BorderLineStyle.Double), boxed.Borders.Bottom);
        Assert.Equal(BorderLineStyle.Dotted, boxed.Borders.Left!.Value.Style);
        Assert.Equal(new BorderLine(1, PdfColor.Black, BorderLineStyle.Dashed), boxed.Borders.Right);
        Assert.Equal(BorderLineStyle.Single, boxed.BetweenBorder!.Value.Style);
        Assert.Equal(Yellow, boxed.Shading);
        var shaded = Paragraph(document, "shaded").Format;
        Assert.All(new[] { shaded.Borders.Top, shaded.Borders.Right, shaded.Borders.Bottom, shaded.Borders.Left }, b => Assert.Equal(new BorderLine(1, PdfColor.Black, BorderLineStyle.Single), b));
        Assert.Equal(new PdfColor(0.5, 0.5, 1), shaded.Shading); // Half blue over white.
        Assert.False(Paragraph(document, "nothing").Format.Borders.Top!.Value.IsVisible);
        Assert.Null(Paragraph(document, "nothing").Format.Shading);
        var hair = Paragraph(document, "hair").Format;
        Assert.Equal(0.5, hair.Borders.Top!.Value.Width);
        Assert.Equal(new PdfColor(0.75, 0.75, 0.75), hair.Shading); // A quarter of black.
    }

    [Fact]
    public void Tab_stops_with_alignment_and_leaders()
    {
        var format = Paragraphs(ReadBody(@"\tqr\tldot\tx8640\tx1440\tqc\tlhyph\tx4320\tqdec\tlul\tx5760\tleq\tx7200\tb3000\tx6000\tx1440 a\par"))[0].Format;
        Assert.Equal(
            [new TabStop(72, TabAlignment.Left), new TabStop(216, TabAlignment.Center, '-'), new TabStop(288, TabAlignment.Decimal, '_'), new TabStop(300, TabAlignment.Left),
             new TabStop(360, TabAlignment.Left, '='), new TabStop(432, TabAlignment.Right, '.')],
            format.TabStops);
    }

    [Fact]
    public void Document_settings()
    {
        var document = Read(Encoding.Latin1.GetBytes(@"{\rtf1\ansi\deftab360\facingp\ftnnruc\aftnnalc{\info{\title  The title }{\author Someone}{\creatim\yr2001}}x}"));
        Assert.Equal(18, document.DefaultTabStop);
        Assert.True(document.EvenAndOddHeaders);
        Assert.Equal(NumberFormat.UpperRoman, document.FootnoteFormat);
        Assert.Equal(NumberFormat.LowerLetter, document.EndnoteFormat);
        Assert.Equal("The title", document.Title);
        Assert.Equal("Someone", document.Author);
        Assert.Equal("x", Text(Paragraphs(document)[0])); // Nothing of the info group is text.
        Assert.Null(Read(Encoding.Latin1.GetBytes(@"{\rtf1 x}")).Title);
        Assert.Null(Read(Encoding.Latin1.GetBytes(@"{\rtf1 x}")).Author);
    }

    private const string ListTable = @"{\*\listtable"
        + @"{\list\listtemplateid1{\listlevel\levelnfc0\levelstartat1\levelfollow0{\leveltext\'02\'00.;}{\levelnumbers\'01;}\fi-360\li720}"
        + @"{\listlevel\levelnfc2\levelstartat1\levelfollow1{\leveltext\'03(\'01);}{\levelnumbers\'02;}\b\fi-360\li1440}"
        + @"{\listlevel\levelnfc3\levelstartat2\levelfollow2{\leveltext\'04\'00.\'02.;}\fi-360\li2160}"
        + @"{\listlevel\levelnfc4\levellegal\levelnorestart{\leveltext\'03\'00-\'03;}\li2880}{\listname ;}\listid10}"
        + @"{\list\listsimple{\listlevel\levelnfc23{\leveltext\'01" + "\\u-3913 ?" + @";}\f3\ul\fi-360\li720}\listid20}"
        + @"{\list{\listlevel\levelnfc22\levelstartat9{\leveltext\'02\'00.;}}{\listlevel\levelnfc255{\leveltext\'00;}}{\listlevel\levelnfc5{\leveltext\'01\'02;}}\listid30}}"
        + @"{\*\listoverridetable{\listoverride\listid10\listoverridecount0\ls1}{\listoverride\listid20\listoverridecount0\ls2}"
        + @"{\listoverride\listid10\listoverridecount1{\lfolevel\listoverridestartat\levelstartat7}\ls3}{\listoverride\listid30\ls4}"
        + @"{\listoverride\listid99\ls5}{\listoverride\listid10\listoverridecount1{\lfolevel\listoverrideformat{\listlevel\levelnfc1{\leveltext\'02\'00:;}}}\ls6}}";

    private static string Item(string text, int list, int level, string extra = "") => $@"\pard\ls{list}\ilvl{level}{extra} {text}\par";

    [Fact]
    public void List_labels_are_counted_per_list_and_level()
    {
        var document = ReadBody(
            Item("one", 1, 0) + Item("two", 1, 0) + Item("nested", 1, 1) + Item("nested2", 1, 1) + Item("deep", 1, 2) + Item("legal", 1, 3) + Item("three", 1, 0)
            + Item("again", 1, 1) + Item("legal2", 1, 3) + Item("bullet", 2, 0, @"\fs20") + Item("bullet2", 2, 4) + Item("seven", 3, 0) + Item("eight", 3, 0)
            + Item("zero", 4, 0) + Item("blank", 4, 1) + Item("ordinal", 4, 2) + Item("missing", 5, 0) + Item("format", 6, 0) + Item("own", 1, 0, @"\li100\fi0"),
            ListTable);
        string Label(string text) => Paragraph(document, text).Label?.Text ?? "-";
        Assert.Equal("1.", Label("one"));
        Assert.Equal("2.", Label("two"));
        Assert.Equal("(i)", Label("nested"));
        Assert.Equal("(ii)", Label("nested2"));
        Assert.Equal("2.B.", Label("deep")); // Starts at 2, letters.
        Assert.Equal("2-a", Label("legal"));
        Assert.Equal("3.", Label("three"));
        Assert.Equal("(i)", Label("again")); // Restarted by its parent level.
        Assert.Equal("3-b", Label("legal2")); // \levelnorestart.
        Assert.Equal("\u2022", Label("bullet")); // The Symbol bullet as Unicode.
        Assert.Equal("\u2022", Label("bullet2")); // A simple list's one level for all levels.
        Assert.Equal("7.", Label("seven")); // The override starts at 7, the first time it is used.
        Assert.Equal("8.", Label("eight"));
        Assert.Equal("09.", Label("zero"));
        Assert.Equal("-", Label("blank"));
        Assert.Equal("1st", Label("ordinal"));
        Assert.Equal("-", Label("missing")); // A list that is not defined.
        Assert.Equal("IX:", Label("format")); // The override's own level definition, counting on with the list's other overrides.

        var one = Paragraph(document, "one");
        Assert.Equal(LabelSuffix.Tab, one.Label!.Suffix);
        Assert.Equal(36, one.Format.IndentStart); // The level's indents, when the paragraph has none.
        Assert.Equal(-18, one.Format.FirstLineIndent);
        Assert.Equal(5, Paragraph(document, "own").Format.IndentStart); // The paragraph's own indents win.
        Assert.Equal(LabelSuffix.Space, Paragraph(document, "nested").Label!.Suffix);
        Assert.True(Paragraph(document, "nested").Label!.Format.Bold);
        Assert.False(Run(document, "nested").Format.Bold);
        Assert.Equal(LabelSuffix.Nothing, Paragraph(document, "deep").Label!.Suffix);
        var bullet = Paragraph(document, "bullet").Label!.Format;
        Assert.Equal("Arial", bullet.FontFamily); // The paragraph's font, not Symbol.
        Assert.Equal(10, bullet.FontSize);
        Assert.Equal(UnderlineStyle.Single, bullet.Underline); // The level's own underline.
    }

    [Fact]
    public void Labels_come_from_old_numbering_or_the_writers_text()
    {
        var document = ReadBody(
            @"\pard{\*\pn\pnlvlbody\pnucrm\pnstart3{\pntxtb (}{\pntxta )}} roman\par\pard{\*\pn\pnlvlbody\pnucrm\pnstart3{\pntxta )}} roman2\par"
            + @"\pard plain\par\pard{\*\pn\pnlvlbody\pnlcltr\pnstart1{\pntxta .}} letter\par"
            + @"\pard{\*\pn\pnlvlblt\pnf3{\pntxtb \'b7}} bullet\par\pard{\*\pn\pnlvlcont} continued\par"
            + @"{\pntext\f0 9.\tab}\pard{\*\pn\pnlvlbody\pndec} captured\par"
            + @"{\listtext\pard\plain\f3\'a7\tab}\pard\ls42 written\par{\listtext\b 4)\tab}\pard text\par"
            + @"\pard{\*\pn\pnlvl2} section\par\pard{\*\pn\pnlvl3\pndec\pnstart5} own\par",
            @"{\*\pnseclvl2\pnucltr\pnstart2{\pntxta .}}");
        string Label(string text) => Paragraph(document, text).Label?.Text ?? "-";
        Assert.Equal("(III)", Label("roman"));
        Assert.Equal("IV)", Label("roman2"));
        Assert.Equal("a.", Label("letter")); // Restarted after a paragraph without numbering.
        Assert.Equal("\u2022", Label("bullet"));
        Assert.Equal("-", Label("continued"));
        Assert.Equal("9.", Label("captured"));
        Assert.Equal("\u2022", Label("written")); // The writer's text, its Symbol character as Unicode.
        Assert.Equal("4)", Label("text"));
        Assert.True(Paragraph(document, "text").Label!.Format.Bold);
        Assert.Equal("B.", Label("section")); // The section-level definition of level 2.
        Assert.Equal("5", Label("own"));
        Assert.Null(Paragraph(document, "plain").Label);
    }

    private static string Cell(string text) => $@"\pard\intbl {text}\cell ";

    [Fact]
    public void Tables_have_a_grid_from_the_cell_edges_and_merged_cells()
    {
        var document = ReadBody(
            @"before\par\trowd\trgaph100\trleft-100\trhdr\trqc\trrh-400\trkeep\clcbpat5\clvertalc\cellx2000\clshdng5000\clcfpat2\cellx4000\cellx6000"
            + Cell("h1") + Cell("h2") + Cell("h3") + @"\row"
            + @"\trowd\trgaph100\trleft-100\trrh300\clmgf\cellx2000\clmrg\cellx4000\clvmgf\cellx6000" + Cell("across") + Cell(string.Empty) + Cell("down") + @"\row"
            + @"\trowd\trgaph100\trleft-100\cellx2000\cellx4000\clvmrg\cellx6000" + Cell("a") + Cell("b") + Cell(string.Empty) + @"\row"
            + @"\trowd\trgaph100\trleft900\cellx3000\cellx6000" + Cell("late") + Cell("x") + @"\row"
            + @"\pard after\par");
        var table = Assert.Single(Tables(document));
        Assert.Equal([50.0, 55, 50, 50, 100], table.Grid); // Edges -100, 900, 2000, 3000, 4000, 6000 twips.
        Assert.Equal(-5, table.Indent);
        Assert.Equal(WordAlignment.Center, table.Alignment);
        Assert.Equal(5, table.Padding.Left);
        Assert.Equal(5, table.Padding.Right);
        Assert.Equal(4, table.Rows.Count);
        var header = table.Rows[0];
        Assert.True(header.IsHeader && header.CantSplit && header.ExactHeight);
        Assert.Equal(20, header.Height);
        Assert.Equal([(0, 2), (2, 2), (4, 1)], header.Cells.Select(c => (c.Column, c.ColumnSpan)));
        Assert.Equal(Yellow, header.Cells[0].Shading);
        Assert.Equal(CellVerticalAlignment.Center, header.Cells[0].VerticalAlignment);
        Assert.Equal(new PdfColor(1, 0.5, 0.5), header.Cells[1].Shading);
        Assert.Null(header.Cells[2].Shading);
        var merged = table.Rows[1];
        Assert.False(merged.IsHeader || merged.ExactHeight);
        Assert.Equal(15, merged.Height);
        Assert.Equal(2, merged.Cells.Count);
        Assert.Equal((0, 4), (merged.Cells[0].Column, merged.Cells[0].ColumnSpan));
        Assert.Equal(2, merged.Cells[1].RowSpan);
        Assert.Equal(["a", "b"], table.Rows[2].Cells.Select(c => Text((WordParagraph)c.Blocks[0])));
        Assert.Equal(1, table.Rows[3].GridBefore);
        Assert.Equal((1, 2), (table.Rows[3].Cells[0].Column, table.Rows[3].Cells[0].ColumnSpan));
        Assert.Equal(["before", "after"], document.Sections[0].Blocks.OfType<WordParagraph>().Select(Text));
    }

    [Fact]
    public void Table_and_cell_borders()
    {
        var document = ReadBody(@"\trowd\trbrdrt\brdrs\brdrw20\trbrdrb\brdrdb\trbrdrl\brdrs\trbrdrr\brdrs\trbrdrh\brdrdot\trbrdrv\brdrdash\trpaddl50\trpaddr60\trpaddt10\trpaddb20\trpaddfl3\trpaddfr3\trpaddft3\trpaddfb0"
            + @"\clbrdrt\brdrs\brdrcf2\clbrdrb\brdrnone\clbrdrl\brdrs\clbrdrr\brdrs\brdrw40\cellx3000\cellx6000" + Cell("one") + Cell("two") + @"\row");
        var table = Assert.Single(Tables(document));
        Assert.Equal(new BorderLine(1, PdfColor.Black), table.Borders.Top);
        Assert.Equal(BorderLineStyle.Double, table.Borders.Bottom!.Value.Style);
        Assert.Equal(BorderLineStyle.Dotted, table.InsideHorizontal!.Value.Style);
        Assert.Equal(BorderLineStyle.Dashed, table.InsideVertical!.Value.Style);
        Assert.Equal(new CellPadding(0.5, 3, 0, 2.5), table.Padding); // The bottom padding's unit is not twips.
        var cell = table.Rows[0].Cells[0];
        Assert.Equal(Red, cell.Borders.Top!.Value.Color);
        Assert.False(cell.Borders.Bottom!.Value.IsVisible);
        Assert.Equal(2, cell.Borders.Right!.Value.Width);
        Assert.False(table.Rows[0].Cells[1].Borders.Any);
    }

    [Fact]
    public void Nested_tables_go_into_their_cells()
    {
        var document = ReadBody(@"\trowd\cellx4000\cellx8000\pard\intbl\itap1 outer\par"
            + @"\pard\intbl\itap2 inner1\nestcell\pard\intbl\itap2 inner2\nestcell\pard\intbl\itap2{\*\nesttableprops\trowd\cellx1000\cellx2000\nestrow}{\nonesttables\par}"
            + @"\pard\intbl\itap3 deep\nestcell{\*\nesttableprops\trowd\cellx500\nestrow}"
            + @"\pard\intbl\itap2 last\nestcell{\*\nesttableprops\trowd\cellx1000\nestrow}"
            + @"\pard\intbl\itap1 tail\cell\pard\intbl second\cell{\trowd\cellx4000\cellx8000\row}\pard after\par");
        var table = Assert.Single(Tables(document));
        var first = table.Rows[0].Cells[0];
        Assert.Collection(first.Blocks,
            b => Assert.Equal("outer", Text(Assert.IsType<WordParagraph>(b))),
            b =>
            {
                var nested = Assert.IsType<WordTable>(b);
                Assert.Equal(2, nested.Rows.Count);
                Assert.Equal(["inner1", "inner2"], nested.Rows[0].Cells.Select(c => Text((WordParagraph)c.Blocks[0])));
                Assert.Equal([50.0, 50], nested.Grid);
                var last = nested.Rows[1].Cells[0];
                Assert.IsType<WordTable>(last.Blocks[0]); // The third level, before "last" in the same cell.
                Assert.Equal("last", Text((WordParagraph)last.Blocks[1]));
            },
            b => Assert.Equal("tail", Text(Assert.IsType<WordParagraph>(b))));
        Assert.Equal("second", Text((WordParagraph)table.Rows[0].Cells[1].Blocks[0]));
        Assert.Equal("after", Text((WordParagraph)document.Sections[0].Blocks[^1]));
    }

    [Fact]
    public void Unfinished_tables_are_ended_and_table_paragraphs_without_rows_stay_paragraphs()
    {
        var unfinished = ReadBody(@"\trowd\cellx2000\cellx4000\pard\intbl one\cell\pard\intbl two");
        var table = Assert.Single(Tables(unfinished));
        Assert.Equal(["one", "two"], table.Rows[0].Cells.Select(c => Text((WordParagraph)c.Blocks[0])));
        var stray = ReadBody(@"\pard\intbl lonely\par\pard back\par");
        Assert.Empty(Tables(stray));
        Assert.Equal(["lonely", "back"], stray.Sections[0].Blocks.OfType<WordParagraph>().Select(Text));
        var extraCells = ReadBody(@"\trowd\cellx2000\pard\intbl a\cell b\cell c\cell\row");
        var wide = Assert.Single(Tables(extraCells));
        Assert.Equal(3, wide.Rows[0].Cells.Count); // Cells beyond the definition get the definition's average width.
        Assert.Equal([100.0, 100, 100], wide.Grid);
        var noDefinition = Assert.Single(Tables(ReadBody(@"\pard\intbl a\cell b\cell\row\pard x\par")));
        Assert.Equal([72.0, 72], noDefinition.Grid);
        var rowText = Assert.Single(Tables(ReadBody(@"\trowd\cellx2000\cellx4000\pard\intbl a\cell b\row")));
        Assert.Equal(2, rowText.Rows[0].Cells.Count); // Text before \row is the last cell.
    }

    [Fact]
    public void Sections_have_their_page_setup_and_headers()
    {
        var document = ReadBody(
            @"\paperw11906\paperh16838\margl1000\margr1000\margt800\margb800\gutter200\sectd\titlepg\headery500\footery400"
            + @"{\header\pard all\par}{\headerl\pard left\par}{\headerf\pard first\par}{\footer\pard foot\par}one\par"
            + @"\sect\sectd\lndscpsxn\sbkodd\pgnstarts5\pgnrestart\pgnucrm\pgwsxn11906\pghsxn16838{\headerr\pard right\par}two\par"
            + @"\sect\sectd\sbknone\cols3 three\par\sect\sectd\sbkeven{\footerl\pard evenfoot\par} four\par\sect\sectd\sbkpage\sbkcol five\par\sect");
        var sections = document.Sections;
        Assert.Equal(5, sections.Count); // The trailing \sect leaves no empty section.
        var first = sections[0];
        Assert.Equal((595.3, 841.9), (Math.Round(first.PageWidth, 1), Math.Round(first.PageHeight, 1)));
        Assert.Equal((60, 50, 40, 40), (first.MarginLeft, first.MarginRight, first.MarginTop, first.MarginBottom)); // The gutter adds to the left margin.
        Assert.Equal((25, 20), (first.HeaderDistance, first.FooterDistance));
        Assert.True(first.TitlePage);
        Assert.Equal("all", Text((WordParagraph)first.Headers[HeaderFooterKind.Default][0]));
        Assert.Equal("left", Text((WordParagraph)first.Headers[HeaderFooterKind.Even][0])); // \headerl wins over \header for even pages.
        Assert.Equal("first", Text((WordParagraph)first.Headers[HeaderFooterKind.First][0]));
        Assert.Equal("foot", Text((WordParagraph)first.Footers[HeaderFooterKind.Even][0])); // \footer is for all pages.
        Assert.Null(first.PageNumberStart);
        var second = sections[1];
        Assert.True(second.PageWidth > second.PageHeight);
        Assert.Equal(SectionStart.OddPage, second.Start);
        Assert.False(second.TitlePage); // \sectd resets the section's formatting.
        Assert.Equal(5, second.PageNumberStart);
        Assert.Equal(NumberFormat.UpperRoman, second.PageNumberFormat);
        Assert.Equal(60, second.MarginLeft); // \sectd resets to the document's margins (and gutter).
        Assert.Equal("right", Text((WordParagraph)second.Headers[HeaderFooterKind.Default][0]));
        Assert.Equal("left", Text((WordParagraph)second.Headers[HeaderFooterKind.Even][0])); // Inherited.
        Assert.Equal(SectionStart.Continuous, sections[2].Start);
        Assert.Equal(3, sections[2].Columns);
        Assert.Equal(SectionStart.EvenPage, sections[3].Start);
        Assert.Equal("evenfoot", Text((WordParagraph)sections[3].Footers[HeaderFooterKind.Even][0]));
        Assert.Equal(SectionStart.Continuous, sections[4].Start); // \sbkcol: a new column, which is the same page here.
        Assert.Equal("five", Text((WordParagraph)sections[4].Blocks[0]));
    }

    [Fact]
    public void Page_setup_is_kept_within_bounds()
    {
        var huge = ReadBody(@"\paperw999999999\paperh10\margl999999\margr999999 x\par").Sections[0];
        Assert.Equal(14400, huge.PageWidth);
        Assert.Equal(72, huge.PageHeight);
        Assert.Equal(14400 - 72, huge.MarginLeft + huge.MarginRight, 3);
        var defaults = ReadBody("x").Sections[0];
        Assert.Equal((612, 792, 90, 90, 72, 72), (defaults.PageWidth, defaults.PageHeight, defaults.MarginLeft, defaults.MarginRight, defaults.MarginTop, defaults.MarginBottom));
        Assert.Equal(SectionStart.NextPage, defaults.Start);
    }

    [Fact]
    public void Fields_become_links_computed_fields_or_their_result()
    {
        var document = ReadBody(
            @"{\field{\*\fldinst {HYPERLINK ""https://example.com/a"" \\o ""tip""}}{\fldrslt {\ul link}}} "
            + @"{\field{\*\fldinst HYPERLINK \\l ""mark""}{\fldrslt inner}} {\field{\*\fldinst HYPERLINK ""file.doc""}{\fldrslt file}} "
            + @"{\field{\*\fldinst PAGE \\* roman}{\fldrslt {\b iv}}} of {\field{\*\fldinst NUMPAGES}{\fldrslt 9}} {\field{\*\fldinst SECTIONPAGES \\* ALPHABETIC}{\fldrslt 2}} "
            + @"{\field{\*\fldinst PAGEREF mark \\h}{\fldrslt 7}} {\field{\*\fldinst PAGEREF}{\fldrslt 8}} {\field{\*\fldinst DATE}{\fldrslt 2026}} {\field{\*\fldinst PAGE}} \chpgn"
            + @"{\field{\*\fldinst {IF {\field{\*\fldinst PAGE}{\fldrslt 1}} = 1 x}}{\fldrslt cond}}\par"
            + @"{\field{\*\fldinst HYPERLINK ""https://example.com/b""}{\fldrslt first\par second}}\par");
        var paragraph = Paragraphs(document)[0];
        var links = paragraph.Inlines.OfType<HyperlinkInline>().ToList();
        Assert.Equal(new Uri("https://example.com/a"), links[0].Uri);
        Assert.Equal("link", Assert.IsType<TextRun>(Assert.Single(links[0].Inlines)).Text);
        Assert.Equal(UnderlineStyle.Single, ((TextRun)links[0].Inlines[0]).Format.Underline);
        Assert.Equal("mark", links[1].Anchor);
        Assert.Equal(2, links.Count); // A file link is shown as text.
        var fields = paragraph.Inlines.OfType<FieldInline>().ToList();
        Assert.Equal(6, fields.Count);
        Assert.Equal((FieldKind.Page, "iv", NumberFormat.LowerRoman), (fields[0].Kind, fields[0].CachedResult, fields[0].NumberFormat));
        Assert.True(fields[0].Format.Bold); // The result's format.
        Assert.Equal((FieldKind.NumPages, "9", NumberFormat.Decimal), (fields[1].Kind, fields[1].CachedResult, fields[1].NumberFormat));
        Assert.Equal((FieldKind.SectionPages, NumberFormat.UpperLetter), (fields[2].Kind, fields[2].NumberFormat));
        Assert.Equal((FieldKind.PageRef, "mark", "7"), (fields[3].Kind, fields[3].Argument, fields[3].CachedResult));
        Assert.Equal((FieldKind.Page, null), (fields[4].Kind, fields[4].CachedResult)); // No result.
        Assert.Equal(FieldKind.Page, fields[5].Kind); // \chpgn.
        Assert.Equal("link inner file iv of 9 2 7 8 2026  cond", Text(paragraph));
        var spanning = Paragraphs(document).Skip(1).Take(2).ToList();
        Assert.All(spanning, p => Assert.Equal(new Uri("https://example.com/b"), Assert.IsType<HyperlinkInline>(p.Inlines[0]).Uri)); // The link continues.
        Assert.Equal(["first", "second"], spanning.Select(Text));
    }

    [Fact]
    public void Footnotes_and_endnotes_are_numbered_from_their_marks()
    {
        var document = ReadBody(@"text{\super\chftn}{\footnote\pard\plain{\super\chftn} The note.}more*{\footnote\pard custom}end{\super\chftn}{\footnote\ftnalt\pard\plain{\super\chftn} Ending.}\par");
        var references = Paragraphs(document)[0].Inlines.OfType<NoteReference>().ToList();
        Assert.Equal(3, references.Count);
        Assert.Equal(NoteKind.Footnote, references[0].Kind);
        Assert.Null(references[0].CustomMark);
        Assert.Equal(VerticalPosition.Superscript, references[0].Format.Position);
        Assert.Equal(string.Empty, references[1].CustomMark); // Its mark is the text before it.
        Assert.Equal(NoteKind.Endnote, references[2].Kind);
        Assert.Equal(2, document.Footnotes.Count);
        Assert.Single(document.Endnotes);
        var note = Assert.IsType<WordParagraph>(Assert.Single(document.Footnotes[references[0].Id]));
        Assert.IsType<NoteMark>(note.Inlines[0]);
        Assert.Equal(" The note.", Text(note));
        Assert.Equal("textmore*end", Text(Paragraphs(document)[0]));
    }

    [Fact]
    public void Pictures_become_images_at_their_size()
    {
        var png = Fixtures.Read(Path.Combine("PngSuite", "basn2c08.png"));
        var hex = System.Convert.ToHexString(png);
        var diagnostics = new List<DocumentDiagnostic>();
        var document = ReadBody(
            $@"{{\pict\pngblip\picw100\pich50\picwgoal2000\pichgoal1000\picscalex50\picscaley200 {hex}}}"
            + $@"{{\pict\jpegblip\picw40\pich20 {hex}}}"
            + $@"{{\pict\pngblip\bin{png.Length} " + Encoding.Latin1.GetString(png) + "}"
            + $@"{{\*\shppict{{\pict\pngblip\picwgoal100 {hex}}}}}{{\nonshppict{{\pict\pngblip\picwgoal900 {hex}}}}}"
            + @"{\pict\emfblip 0100}{\pict\wmetafile8 01}{\pict\macpict 01}{\pict 01}"
            + $@"{{\shp{{\*\shpinst{{\sp{{\sn pib}}{{\sv{{\pict\pngblip\picwgoal200 {hex}}}}}}}{{\sp{{\sn fillBlip}}{{\sv{{\pict\pngblip {hex}}}}}}}{{\shptxt\pard boxed\par}}}}{{\shprslt fallback}}}}"
            + $@"{{\object\objemb{{\*\objdata 0102}}{{\result {{\pict\pngblip\picwgoal300 {hex}}}}}}}"
            + $@"{{\listtext{{\pict\pngblip {hex}}}}}{{\field{{\*\fldinst PAGE}}{{\fldrslt {{\pict\pngblip {hex}}}}}}}x\par",
            diagnostics: diagnostics);
        var images = Paragraphs(document)[0].Inlines.OfType<ImageInline>().ToList();
        Assert.Equal(6, images.Count);
        Assert.Equal((50.0, 100.0), (images[0].Width, images[0].Height)); // The goal size, scaled.
        Assert.Equal(png, images[0].Data);
        Assert.Equal((30.0, 15.0), (images[1].Width, images[1].Height)); // Pixels at 96 dpi.
        Assert.Equal(png, images[2].Data); // \bin data.
        Assert.Equal((0.0, 0.0), (images[2].Width, images[2].Height));
        Assert.Equal(5, images[3].Width); // The shape picture, not its fallback.
        Assert.Equal(10, images[4].Width); // A shape's picture (pib), not its fill.
        Assert.Equal(15, images[5].Width); // An object's result.
        Assert.Contains(Paragraphs(document)[0].Inlines, i => i is AnchoredBlocks anchored && Text((WordParagraph)anchored.Blocks[0]) == "boxed");
        Assert.DoesNotContain("fallback", Text(Paragraphs(document)[0]), StringComparison.Ordinal);
        Assert.Contains(diagnostics, d => d.Kind == DocumentDiagnosticKind.UnsupportedContent && d.Message.Contains("EMF", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("WMF format", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("PICT", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown format", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("embedded object", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Shapes", StringComparison.Ordinal));
    }

    [Fact]
    public void Device_independent_bitmaps_become_bmp_files_and_large_pictures_are_left_out()
    {
        // A 1x1 24-bit DIB: a 40-byte header and one padded pixel.
        var dib = new byte[44];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(1).CopyTo(dib, 4);
        BitConverter.GetBytes(1).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        dib[40] = 0xFF;
        var document = ReadBody($@"{{\pict\dibitmap0\picwgoal200 {System.Convert.ToHexString(dib)}}}{{\pict\dibitmap0 0102}}x\par");
        var image = Assert.Single(Paragraphs(document)[0].Inlines.OfType<ImageInline>());
        Assert.Equal("BM"u8.ToArray(), image.Data[..2]);
        Assert.Equal(54, BitConverter.ToInt32(image.Data, 10)); // Pixels after the file and info headers.
        Assert.NotNull(PdfImage.Load(image.Data));

        var diagnostics = new List<DocumentDiagnostic>();
        var limited = Read(Document($@"{{\pict\pngblip {new string('a', 400)}}}{{\pict\pngblip\bin300 {new string('b', 300)}}}x\par"),
            ResourceLimits.Default with { MaxResourceBytes = 100 }, diagnostics);
        Assert.Empty(Paragraphs(limited)[0].Inlines.OfType<ImageInline>());
        Assert.Equal(2, diagnostics.Count(d => d.Kind == DocumentDiagnosticKind.ImageNotLoaded && d.Message.Contains("MaxResourceBytes", StringComparison.Ordinal)));
    }

    [Fact]
    public void Bookmarks_comments_and_drawing_objects()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var document = ReadBody(@"{\*\bkmkstart  place }here{\*\bkmkend place}{\*\bkmkstart _GoBack}{\*\atnid A}\chatn{\*\annotation\pard comment}{\*\do\dprect x} done\par", diagnostics: diagnostics);
        var paragraph = Paragraphs(document)[0];
        Assert.Equal("place", Assert.Single(paragraph.Inlines.OfType<BookmarkInline>()).Name);
        Assert.Equal("here done", Text(paragraph));
        Assert.Contains(diagnostics, d => d.Message.Contains("annotations", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("Drawing objects", StringComparison.Ordinal));
    }

    [Fact]
    public void The_last_paragraph_needs_no_mark_and_content_after_the_document_is_ignored()
    {
        var document = Read(Encoding.Latin1.GetBytes(@"{\rtf1 one\par two} three"));
        Assert.Equal(["one", "two"], Paragraphs(document).Select(Text));
        var unclosed = Read(Encoding.Latin1.GetBytes(@"{\rtf1 one{\b two"));
        Assert.Equal("onetwo", Text(Paragraphs(unclosed)[0]));
        var extraBraces = Read([0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes("  {\\rtf1 a}}}")]);
        Assert.Equal("a", Text(Paragraphs(extraBraces)[0]));
    }

    [Fact]
    public void Data_that_is_not_rtf_throws_a_format_exception()
    {
        Assert.Contains("not an RTF document", Assert.Throws<DocumentFormatException>(() => Read("hello"u8.ToArray())).Message, StringComparison.Ordinal);
        Assert.Contains("ZIP package", Assert.Throws<DocumentFormatException>(() => Read("PK\u0003\u0004xx"u8.ToArray())).Message, StringComparison.Ordinal);
        Assert.Throws<DocumentFormatException>(() => Read([]));
        Assert.Throws<DocumentFormatException>(() => Read(Encoding.Latin1.GetBytes(new string(' ', 100) + @"{\rtf1 x}")));
    }

    [Fact]
    public void Limits_bound_groups_tokens_tables_and_cells()
    {
        string Limit(string rtf, ResourceLimits limits) =>
            Assert.Throws<ResourceLimitExceededException>(() => Read(Encoding.Latin1.GetBytes(rtf), limits)).LimitName;
        var deep = @"{\rtf1 " + new string('{', 60) + "x" + new string('}', 61);
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), Limit(deep, ResourceLimits.Default with { MaxNestingDepth = 50 }));
        Assert.Equal("x", Text(Paragraphs(Read(Encoding.Latin1.GetBytes(deep)))[0]));
        Assert.Equal(nameof(ResourceLimits.MaxElements), Limit(@"{\rtf1 " + string.Concat(Enumerable.Repeat(@"\b x", 600)) + "}", ResourceLimits.Default with { MaxElements = 1000 }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), Limit(@"{\rtf1 \pard\intbl\itap100 x\par}", ResourceLimits.Default with { MaxNestingDepth = 50 }));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), Limit(@"{\rtf1 \pard\intbl\itap2 x\nestcell}", ResourceLimits.Default with { MaxNestingDepth = 1 }));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), Limit(@"{\rtf1 " + string.Concat(Enumerable.Repeat(@"\pard\intbl x\cell", 200)) + "}", ResourceLimits.Default with { MaxTableCells = 100 }));
        Assert.Equal(nameof(ResourceLimits.Timeout), Limit(@"{\rtf1 " + string.Concat(Enumerable.Repeat(@"\b x", 10000)) + "}", ResourceLimits.Default with { Timeout = TimeSpan.FromTicks(1) }));
    }

    [Fact]
    public void Long_control_words_and_parameters_are_clamped()
    {
        var document = ReadBody(@"\" + new string('a', 100) + @"99999999999999999 x\fs99999999999 y\li-99999999999999 z\par");
        var paragraph = Paragraphs(document)[0];
        Assert.Equal("xyz", Text(paragraph)); // The space after a control word is part of it.
        Assert.Equal(1638, Run(document, "y").Format.FontSize);
        Assert.Equal(-1584, paragraph.Format.IndentStart);
    }
}
