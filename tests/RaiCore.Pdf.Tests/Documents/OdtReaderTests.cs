using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Odt;
using static RaiCore.Pdf.Tests.WordDocuments.Odt;

namespace RaiCore.Pdf.Tests.WordDocuments;

/// <summary>
/// Reading OpenDocument text into the word-processing model: styles and their inheritance, text and paragraph
/// properties, lists and their numbering, tables, page styles and sections, fields, notes, frames and white space.
/// </summary>
public class OdtReaderTests
{
    private const double Cm = 72 / 2.54;

    private static string Text(WordParagraph paragraph) => Docx.Text(paragraph);

    [Fact]
    public void Text_properties_become_run_formats()
    {
        var document = Read(Fixture("styles.odt"));
        Assert.True(Run(document, "bold").Format.Bold);
        Assert.False(Run(document, "Plain text").Format.Bold);
        Assert.True(Run(document, "italic").Format.Italic);
        Assert.Equal(UnderlineStyle.Single, Run(document, "underlined").Format.Underline);
        Assert.Equal(UnderlineStyle.Double, Run(document, "double underlined").Format.Underline);
        Assert.Equal(UnderlineStyle.Dotted, Run(document, "dotted").Format.Underline);
        Assert.Equal(UnderlineStyle.Wavy, Run(document, "wavy").Format.Underline);
        Assert.True(Run(document, "struck").Format.Strike);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), Run(document, "red").Format.Color);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), Run(document, "highlighted").Format.Background);
        var runs = Paragraph(document, "E=mc").Inlines.OfType<TextRun>().ToList();
        Assert.Equal(VerticalPosition.Superscript, runs.First(r => r.Text == "2").Format.Position);
        Assert.Equal(VerticalPosition.Subscript, runs.Last(r => r.Text == "2").Format.Position);
        Assert.True(Run(document, "small caps").Format.SmallCaps);
        Assert.True(Run(document, "all caps").Format.Caps);
        Assert.Equal("lower case", Run(document, "lower case").Text);
        Assert.Equal("Capitalized Words", Run(document, "Capitalized").Text);
        Assert.Equal(2, Run(document, "spaced").Format.LetterSpacing, 3);
        Assert.Equal("Liberation Mono", Run(document, "Liberation Mono").Format.FontFamily);
        Assert.Equal(24, Run(document, "large").Format.FontSize);
        Assert.Equal(6, Run(document, "half size").Format.FontSize);
        var raised = Run(document, "raised").Format;
        Assert.Equal(VerticalPosition.Baseline, raised.Position);
        Assert.Equal(12 * 0.33, raised.BaselineShift, 3);
        Assert.Equal("Liberation Serif", Run(document, "Plain text").Format.FontFamily); // The default style's font face.
        Assert.Equal(12, Run(document, "Plain text").Format.FontSize);
        Assert.Equal("en-GB", Run(document, "Plain text").Format.Language);
        Assert.Equal("en-GB", document.Language);
    }

    [Fact]
    public void Styles_inherit_from_their_parents_and_the_default_style()
    {
        var document = Read(Fixture("styles.odt"));
        var heading = Paragraph(document, "Document styles");
        Assert.Equal(14 * 1.3, heading.MarkFormat.FontSize, 3); // 130% of the parent Heading's 14 pt.
        Assert.True(heading.MarkFormat.Bold);
        Assert.Equal("Liberation Sans", heading.MarkFormat.FontFamily);
        Assert.True(heading.Format.KeepWithNext);
        Assert.Equal(0, heading.Format.OutlineLevel);
        Assert.Equal(1, Paragraph(document, "Paragraph formats").Format.OutlineLevel);
        Assert.True(Paragraph(document, "Paragraph formats").MarkFormat.Italic);
        var strong = Run(document, "inherited big strong").Format;
        Assert.True(strong.Bold); // From the parent text style.
        Assert.Equal(18, strong.FontSize);
        Assert.True(Run(document, "common style").Format.Bold);
        var quote = Paragraph(document, "Quote style");
        Assert.Equal(Cm, quote.Format.IndentStart, 3);
        Assert.Equal(0.25 * Cm, quote.Format.SpaceAfter, 3); // From Text body, the parent.
        Assert.True(quote.MarkFormat.Italic);
        Assert.Equal(1.25 * Cm, document.DefaultTabStop, 3);
        Assert.DoesNotContain("Hidden", Text(Paragraph(document, "Visible text")), StringComparison.Ordinal);
    }

    [Fact]
    public void Paragraph_properties_become_paragraph_formats()
    {
        var document = Read(Fixture("styles.odt"));
        Assert.Equal(WordAlignment.Center, Paragraph(document, "Centered").Format.Alignment);
        Assert.Equal(WordAlignment.End, Paragraph(document, "Right-aligned").Format.Alignment);
        Assert.Equal(WordAlignment.Justify, Paragraph(document, "Justified").Format.Alignment);
        var hanging = Paragraph(document, "hanging indent").Format;
        Assert.Equal(2 * Cm, hanging.IndentStart, 3);
        Assert.Equal(-Cm, hanging.FirstLineIndent, 3);
        Assert.Equal((LineSpacingRule.Multiple, 2.0), (Paragraph(document, "Double spaced").Format.LineRule, Paragraph(document, "Double spaced").Format.LineSpacing));
        Assert.Equal((LineSpacingRule.Exact, 30.0), (Paragraph(document, "Fixed line").Format.LineRule, Paragraph(document, "Fixed line").Format.LineSpacing));
        Assert.Equal((LineSpacingRule.AtLeast, 24.0), (Paragraph(document, "At least").Format.LineRule, Paragraph(document, "At least").Format.LineSpacing));
        var leading = Paragraph(document, "Leading paragraph").Format;
        Assert.Equal(LineSpacingRule.AtLeast, leading.LineRule);
        Assert.Equal((12 * 1.15) + 12, leading.LineSpacing, 3);
        var border = Paragraph(document, "with a border").Format;
        Assert.Equal(new BorderLine(0.5, PdfColor.FromRgb(255, 0, 0)), border.Borders.Top);
        Assert.Equal(border.Borders.Top, border.Borders.Left);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), border.Shading);
        var margins = Paragraph(document, "Margins on all sides").Format;
        Assert.Equal((0.5 * Cm, 0.5 * Cm, 1.5 * Cm, 1.5 * Cm), (margins.SpaceBefore, margins.SpaceAfter, margins.IndentStart, margins.IndentEnd));
        Assert.True(Paragraph(document, "A paragraph with a border").Format.WidowControl);
    }

    [Fact]
    public void Tab_stops_are_relative_to_the_indent_unless_the_settings_say_otherwise()
    {
        var stops = Paragraph(Read(Fixture("styles.odt")), "Name:").Format.TabStops;
        Assert.Equal([5 * Cm, 13 * Cm, 15 * Cm], stops.Select(s => Math.Round(s.Position, 3)).Select(p => p).ToArray(), new ToleranceComparer());
        Assert.Equal([TabAlignment.Left, TabAlignment.Right, TabAlignment.Decimal], stops.Select(s => s.Alignment));
        Assert.Equal([null, '.', null], stops.Select(s => s.Leader));
        var style = Style("P1", "paragraph", "<style:paragraph-properties fo:margin-left=\"1cm\"><style:tab-stops><style:tab-stop style:position=\"2cm\" style:leader-style=\"solid\"/>"
            + "<style:tab-stop style:position=\"3cm\" style:type=\"center\" style:leader-type=\"dash\"/></style:tab-stops></style:paragraph-properties>");
        var settings = "<config:config-item-set config:name=\"ooo:configuration-settings\"><config:config-item config:name=\"TabsRelativeToIndent\" config:type=\"boolean\">false</config:config-item></config:config-item-set>";
        var absolute = Paragraphs(Read(Document(P("x", "P1"), style, settings: settings)))[0].Format.TabStops;
        Assert.Equal([2 * Cm, 3 * Cm], absolute.Select(s => s.Position).ToArray(), new ToleranceComparer());
        Assert.Equal(['_', '-'], absolute.Select(s => s.Leader!.Value));
        var dotted = Style("P2", "paragraph", "<style:paragraph-properties><style:tab-stops><style:tab-stop style:position=\"2cm\" style:leader-style=\"dotted\"/>"
            + "<style:tab-stop style:position=\"3cm\" style:leader-style=\"dotted\" style:leader-text=\" \"/></style:tab-stops></style:paragraph-properties>");
        Assert.Equal(['.', '.'], Paragraphs(Read(Document(P("x", "P2"), dotted)))[0].Format.TabStops.Select(s => s.Leader!.Value));
        Assert.Equal(TabAlignment.Center, absolute[1].Alignment);
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 0.01;

        public int GetHashCode(double obj) => 0;
    }

    [Fact]
    public void Breaks_keeps_widows_and_writing_mode_are_read()
    {
        var automatic = ParagraphStyle("Before", "fo:break-before=\"page\"") + ParagraphStyle("After", "fo:break-after=\"page\"")
            + ParagraphStyle("Keep", "fo:keep-together=\"always\" fo:keep-with-next=\"always\" fo:widows=\"0\" fo:orphans=\"0\" style:writing-mode=\"rl-tb\" style:contextual-spacing=\"true\"")
            + ParagraphStyle("Columns", "fo:break-before=\"column\"");
        var document = Read(Document(P("Before", "Before") + P("After", "After") + P("Next") + P("Keep", "Keep") + P("Column", "Columns"), automatic));
        Assert.True(Paragraph(document, "Before").Format.PageBreakBefore);
        Assert.False(Paragraph(document, "After").Format.PageBreakBefore);
        Assert.True(Paragraph(document, "Next").Format.PageBreakBefore); // After a paragraph with a break after it.
        Assert.False(Paragraph(document, "Keep").Format.PageBreakBefore);
        var keep = Paragraph(document, "Keep").Format;
        Assert.True(keep.KeepLines);
        Assert.True(keep.KeepWithNext);
        Assert.False(keep.WidowControl);
        Assert.True(keep.RightToLeft);
        Assert.True(keep.ContextualSpacing);
        Assert.Equal("Keep", keep.StyleId);
        Assert.True(Paragraph(document, "Column").Format.PageBreakBefore);
        Assert.Single(document.Sections);
        var table = "<table:table><table:table-column/><table:table-row><table:table-cell><text:p>Cell</text:p></table:table-cell></table:table-row></table:table>";
        var beforeTable = Read(Document(P("After", "After") + table + P("End"), automatic));
        Assert.Equal(2, beforeTable.Sections.Count); // The page break after the paragraph starts the table on a new page,
        Assert.False(Paragraph(beforeTable, "Cell").Format.PageBreakBefore); // not its first cell's paragraph.
        Assert.False(Paragraph(beforeTable, "End").Format.PageBreakBefore);
    }

    [Fact]
    public void White_space_collapses_except_spaces_tabs_and_line_breaks()
    {
        var document = Read(Fixture("styles.odt"));
        Assert.Equal("Several    spaces and collapsed white space\nSecond line", Text(Paragraph(document, "Several")));
        Assert.Equal("Name:\tValue\tRight\t3,25", Text(Paragraph(document, "Name:")));
        var leading = Read(Document("<text:p>  \n  Leading <text:span> and </text:span>  trailing <text:s/> kept</text:p>"));
        Assert.Equal("Leading and trailing   kept", Text(Paragraphs(leading)[0])); // One space each: before, of and after the text:s.
    }

    [Fact]
    public void Links_and_bookmarks_are_read()
    {
        var document = Read(Fixture("styles.odt"));
        var links = Paragraph(document, "Visit").Inlines.OfType<HyperlinkInline>().ToList();
        Assert.Equal(new Uri("https://example.com/"), links[0].Uri);
        Assert.Equal(PdfColor.FromRgb(0, 0, 128), Runs(links[0].Inlines).Single().Format.Color); // The link's text style.
        Assert.Equal("target", links[1].Anchor);
        Assert.Null(links[1].Uri);
        Assert.Contains(Paragraph(document, "Targets").Inlines, i => i is BookmarkInline { Name: "target" });
        Assert.Contains(Paragraph(document, "bookmarked target").Inlines, i => i is BookmarkInline { Name: "paragraph" });
        var relative = Read(Document(P("<text:a xlink:href=\"../file.odt\">relative</text:a> <text:a xlink:href=\"#Heading%20One\">escaped</text:a>")));
        var inlines = Paragraphs(relative)[0].Inlines.OfType<HyperlinkInline>().ToList();
        Assert.Null(inlines[0].Uri);
        Assert.Equal("Heading One", inlines[1].Anchor);
    }

    [Fact]
    public void Lists_number_their_levels_continue_and_restart()
    {
        var labels = Paragraphs(Read(Fixture("lists.odt"))).Where(p => p.Label is not null).Select(p => $"{p.Label!.Text} {Text(p)}").ToList();
        Assert.Equal(
        [
            "1. First item", "2. Second item", "a) Nested item", "b) Another nested item", "(i) Roman item", "3. Back to the top level", "4. Continued item",
            "1. Level one", "1.1. Level two", "1.1.1. Level three", "\u2022 Bullet one", "\u25E6 Circle bullet", "\u25AA Square bullet", "C. Letter C item",
            "D. Letter D item", "V. Roman five", "VI. Roman six", "VII. Roman seven", "10. Restarted at ten", "11. Eleven", "1. Item after the header",
            "1. Paragraph style list", "1. Own indent item", "a) Numbered paragraph", "\u2022 Private use bullet",
        ], labels);
    }

    [Fact]
    public void List_levels_set_indents_label_formats_and_suffixes()
    {
        var document = Read(Fixture("lists.odt"));
        var first = Paragraph(document, "First item");
        Assert.Equal(1.27 * Cm, first.Format.IndentStart, 3);
        Assert.Equal(-0.635 * Cm, first.Format.FirstLineIndent, 3);
        Assert.Equal(LabelSuffix.Tab, first.Label!.Suffix);
        Assert.Contains(first.Format.TabStops, s => Math.Abs(s.Position - (1.27 * Cm)) < 0.01);
        var nested = Paragraph(document, "Nested item");
        Assert.Equal(2.54 * Cm, nested.Format.IndentStart, 3);
        var old = Paragraph(document, "Letter C item");
        Assert.Equal(Cm, old.Format.IndentStart, 3); // space-before 0 + min-label-width 1 cm.
        Assert.Equal(-Cm, old.Format.FirstLineIndent, 3);
        var header = Paragraph(document, "A list header");
        Assert.Null(header.Label);
        Assert.Equal(1.27 * Cm, header.Format.IndentStart, 3);
        Assert.Equal(0, header.Format.FirstLineIndent);
        var own = Paragraph(document, "Own indent item");
        Assert.Equal(3 * Cm, own.Format.IndentStart, 3); // The paragraph's own indent wins over the list's.
        Assert.Equal(0, own.Format.FirstLineIndent);
        var bullet = Paragraph(document, "Bullet one").Label!.Format;
        Assert.Equal("Liberation Serif", bullet.FontFamily); // OpenSymbol gives way to the paragraph's font.
        Assert.Equal(12, bullet.FontSize); // And the relative size meant for it is not applied.
        Assert.Null(Paragraph(document, "Numbered paragraph").Inlines.OfType<TextRun>().FirstOrDefault(r => r.Text.Contains("a)", StringComparison.Ordinal)));
    }

    [Fact]
    public void List_labels_have_their_level_formats_spaces_and_relative_sizes()
    {
        var automatic = "<text:list-style style:name=\"L\">"
            + "<text:list-level-style-bullet text:level=\"1\" text:bullet-char=\"-\" text:bullet-relative-size=\"150%\" text:style-name=\"Red\">"
            + "<style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"space\" fo:margin-left=\"1cm\" fo:text-indent=\"-0.5cm\"/></style:list-level-properties></text:list-level-style-bullet>"
            + "<text:list-level-style-number text:level=\"2\" style:num-format=\"A\" style:num-suffix=\"]\">"
            + "<style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"nothing\"/></style:list-level-properties>"
            + "<style:text-properties fo:font-weight=\"bold\"/></text:list-level-style-number>"
            + "<text:list-level-style-number text:level=\"3\" style:num-format=\"\" style:num-prefix=\"#\"/>"
            + "<text:list-level-style-image text:level=\"4\"/></text:list-style>"
            + TextStyle("Red", "fo:color=\"#ff0000\"") + TextStyle("Under", "style:text-underline-style=\"solid\" fo:background-color=\"#00ff00\"");
        var body = "<text:list text:style-name=\"L\"><text:list-item><text:p>Dash <text:span text:style-name=\"Under\">u</text:span></text:p>"
            + "<text:list><text:list-item><text:p>Letter</text:p><text:list><text:list-item><text:p>Prefix only</text:p><text:list><text:list-item><text:p>Image</text:p>"
            + "</text:list-item></text:list></text:list-item></text:list></text:list-item></text:list></text:list-item></text:list>";
        var document = Read(Document(body, automatic));
        var dash = Paragraph(document, "Dash").Label!;
        Assert.Equal(("-", LabelSuffix.Space), (dash.Text, dash.Suffix));
        Assert.Equal(18, dash.Format.FontSize); // 150% of 12 pt.
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), dash.Format.Color);
        var letter = Paragraph(document, "Letter").Label!;
        Assert.Equal(("A]", LabelSuffix.Nothing), (letter.Text, letter.Suffix));
        Assert.True(letter.Format.Bold);
        Assert.Equal("#", Paragraph(document, "Prefix only").Label!.Text);
        Assert.Equal("\u2022", Paragraph(document, "Image").Label!.Text);
        var underlined = Read(Document("<text:list text:style-name=\"L\"><text:list-item><text:p text:style-name=\"U\">x</text:p></text:list-item></text:list>",
            automatic + ParagraphStyle("U", text: "style:text-underline-style=\"solid\" fo:background-color=\"#00ff00\"")));
        var label = Paragraphs(underlined)[0].Label!.Format;
        Assert.Equal(UnderlineStyle.None, label.Underline);
        Assert.Null(label.Background);
    }

    [Fact]
    public void Headings_are_numbered_by_the_outline_style()
    {
        var common = "<text:outline-style style:name=\"Outline\">"
            + "<text:outline-level-style text:level=\"1\" style:num-format=\"1\" style:num-suffix=\".\"/>"
            + "<text:outline-level-style text:level=\"2\" style:num-format=\"1\" text:display-levels=\"2\"/></text:outline-style>";
        var body = "<text:h text:outline-level=\"1\">One</text:h><text:h text:outline-level=\"2\">One one</text:h><text:h text:outline-level=\"2\">One two</text:h>"
            + "<text:h text:outline-level=\"1\">Two</text:h><text:h text:outline-level=\"2\"><text:number>2.1</text:number>Two one</text:h>"
            + "<text:h text:outline-level=\"1\" text:is-list-header=\"true\">Unnumbered</text:h>"
            + "<text:h text:outline-level=\"1\" text:restart-numbering=\"true\" text:start-value=\"7\">Seven</text:h><text:h text:outline-level=\"3\">Level three</text:h>";
        var document = Read(Document(body, common: common));
        var labels = Paragraphs(document).Select(p => (p.Label?.Text, Text(p), p.Format.OutlineLevel)).ToList();
        Assert.Equal(
        [
            ("1.", "One", 0), ("1.1", "One one", 1), ("1.2", "One two", 1), ("2.", "Two", 0), ("2.1", "Two one", 1), (null, "Unnumbered", 0),
            ("7.", "Seven", 0), (null, "Level three", 2),
        ], labels);
        var plain = Read(Document("<text:h text:outline-level=\"2\"><text:number>9.</text:number>Cached</text:h>"));
        Assert.Equal("9.Cached", Text(Paragraphs(plain)[0])); // No outline style: the cached number stays.
    }

    [Fact]
    public void Tables_have_their_grid_spans_rows_and_cell_formats()
    {
        var document = Read(Fixture("tables.odt"));
        var table = document.Sections[0].Blocks.OfType<WordTable>().First();
        Assert.Equal([4 * Cm, 3 * Cm, 3 * Cm, 6 * Cm], table.Grid.ToArray(), new ToleranceComparer());
        Assert.True(table.Rows[0].IsHeader);
        Assert.False(table.Rows[1].IsHeader);
        Assert.Equal([(0, 2), (2, 1), (3, 1)], table.Rows[1].Cells.Select(c => (c.Column, c.ColumnSpan)));
        Assert.Equal(2, table.Rows[2].Cells[0].RowSpan);
        Assert.Equal([1, 2, 3], table.Rows[3].Cells.Select(c => c.Column));
        var header = table.Rows[0].Cells[0];
        Assert.Equal(PdfColor.FromRgb(204, 204, 204), header.Shading);
        Assert.Equal(new CellPadding(0.1 * Cm, 0.1 * Cm, 0.1 * Cm, 0.1 * Cm), header.Padding);
        var tall = table.Rows[4];
        Assert.Equal(2 * Cm, tall.Height, 3);
        Assert.False(tall.ExactHeight);
        Assert.Equal(CellVerticalAlignment.Center, tall.Cells[0].VerticalAlignment);
        Assert.Equal(CellVerticalAlignment.Bottom, tall.Cells[1].VerticalAlignment);
        Assert.Equal(CellVerticalAlignment.Top, tall.Cells[2].VerticalAlignment);
        Assert.Equal(new BorderLine(1.5, PdfColor.FromRgb(0, 0, 255), BorderLineStyle.Double), tall.Cells[0].Borders.Top);
        Assert.Equal(BorderLineStyle.Dotted, tall.Cells[1].Borders.Left!.Value.Style);
        Assert.Equal(BorderLineStyle.Dashed, tall.Cells[2].Borders.Bottom!.Value.Style);
        Assert.Equal(new CellPadding(0.1 * Cm, -1, -1, 0.5 * Cm), tall.Cells[2].Padding);
        Assert.Equal(BorderLine.None, table.Rows[1].Cells[0].Borders.Top); // The column's default cell style: no top border.
        Assert.NotNull(table.Rows[1].Cells[0].Borders.Left);
        var nested = table.Rows[5].Cells[3].Blocks.OfType<WordTable>().Single();
        Assert.Equal(2, nested.Rows.Count);
        Assert.Equal(["Repeated row", "Repeated row", "Repeated row"], table.Rows.Skip(6).Take(3).Select(r => Text((WordParagraph)r.Cells[0].Blocks[0])));
        Assert.Same(table.Rows[6].Cells[0].Blocks[0], table.Rows[8].Cells[0].Blocks[0]); // Repeated rows share their content.
        var repeatedCells = table.Rows[9];
        Assert.Equal([0, 1, 2, 3], repeatedCells.Cells.Select(c => c.Column));
        Assert.All(repeatedCells.Cells, c => Assert.Equal(PdfColor.FromRgb(221, 255, 221), c.Shading)); // The row's background.
        var exact = table.Rows[10];
        Assert.True(exact.ExactHeight);
        Assert.True(exact.CantSplit);
        Assert.Equal(1.5 * Cm, exact.Height, 3);
        Assert.Equal(11, table.Rows.Count);
    }

    [Fact]
    public void Table_widths_come_from_relative_columns_and_the_table_style()
    {
        var document = Read(Fixture("tables.odt"));
        var tables = document.Sections.SelectMany(s => s.Blocks).OfType<WordTable>().ToList();
        var content = (21 - 4) * Cm;
        Assert.Equal([content / 4, content * 3 / 4], tables[1].Grid.ToArray(), new ToleranceComparer()); // Aligned to the margins.
        Assert.Equal([content / 8, content * 3 / 8], tables[2].Grid.ToArray(), new ToleranceComparer()); // 50%.
        Assert.Equal(WordAlignment.Center, tables[2].Alignment);
        Assert.All(tables[2].Rows[0].Cells, c => Assert.Equal(PdfColor.FromRgb(224, 224, 255), c.Shading));
        Assert.Equal(WordAlignment.End, tables[3].Alignment);
        Assert.Equal(2, document.Sections.Count); // The table with a page break before it starts a section.
        Assert.IsType<WordTable>(document.Sections[1].Blocks[0]);
        Assert.Null(document.Sections[1].PageNumberStart);
        Assert.Equal(new CellPadding(0, 0, 0, 0), tables[0].Padding);
        var indented = Read(Document("<table:table table:style-name=\"T\"><table:table-column/><table:table-row><table:table-cell/></table:table-row></table:table>",
            Style("T", "table", "<style:table-properties fo:margin-left=\"1cm\" style:width=\"5cm\"/>")));
        var only = indented.Sections[0].Blocks.OfType<WordTable>().Single();
        Assert.Equal(Cm, only.Indent, 3);
        Assert.Equal([5 * Cm], only.Grid.ToArray(), new ToleranceComparer());
        Assert.IsType<WordParagraph>(only.Rows[0].Cells[0].Blocks.Single()); // An empty cell gets an empty paragraph.
    }

    [Fact]
    public void Master_pages_make_sections_with_their_page_layout_headers_and_footers()
    {
        var document = Read(Fixture("pages.odt"));
        Assert.Equal(6, document.Sections.Count);
        var standard = document.Sections[0];
        Assert.Equal((21 * Cm, 29.7 * Cm), (standard.PageWidth, standard.PageHeight));
        Assert.Equal(2 * Cm, standard.HeaderDistance, 3);
        Assert.Equal(2 * Cm, standard.FooterDistance, 3);
        Assert.Equal(3.5 * Cm, standard.MarginTop, 3); // The margin, the header's 1 cm and its 0.5 cm spacing.
        Assert.Equal(3.5 * Cm, standard.MarginBottom, 3);
        Assert.Equal(2 * Cm, standard.MarginLeft, 3);
        Assert.True(document.EvenAndOddHeaders);
        Assert.Contains("Left header", Text((WordParagraph)standard.Headers[HeaderFooterKind.Even][0]), StringComparison.Ordinal);
        Assert.Contains("Default header", Text((WordParagraph)standard.Headers[HeaderFooterKind.Default][0]), StringComparison.Ordinal);
        Assert.Equal(["Page one text.", "Page two text."], standard.Blocks.OfType<WordParagraph>().Select(Text));
        var page = Docx.Paragraphs(standard.Footers[HeaderFooterKind.Default]).Single().Inlines.OfType<FieldInline>().Select(f => f.Kind);
        Assert.Equal([FieldKind.Page, FieldKind.NumPages], page);

        var landscape = document.Sections[1];
        Assert.Equal((29.7 * Cm, 21 * Cm), (landscape.PageWidth, landscape.PageHeight));
        Assert.Equal(1, landscape.PageNumberStart);
        Assert.Equal(NumberFormat.LowerRoman, landscape.PageNumberFormat);
        Assert.Equal(1.5 * Cm, landscape.MarginLeft, 3);
        Assert.Same(landscape.Headers[HeaderFooterKind.Default], landscape.Headers[HeaderFooterKind.Even]); // Without its own left header.

        var title = document.Sections[2];
        Assert.Equal((612.0, 792.0), (Math.Round(title.PageWidth, 3), Math.Round(title.PageHeight, 3)));
        Assert.True(title.TitlePage);
        Assert.Contains("Title page header", Text((WordParagraph)title.Headers[HeaderFooterKind.First][0]), StringComparison.Ordinal);
        Assert.Contains("Following header", Text((WordParagraph)title.Headers[HeaderFooterKind.Default][0]), StringComparison.Ordinal);
        Assert.Equal(["Title page text.", "After the title page."], title.Blocks.OfType<WordParagraph>().Select(Text));
        Assert.True(((WordParagraph)title.Blocks[1]).Format.PageBreakBefore);

        var odf13 = document.Sections[3];
        Assert.True(odf13.TitlePage);
        Assert.Contains("ODF 1.3 first header", Text((WordParagraph)odf13.Headers[HeaderFooterKind.First][0]), StringComparison.Ordinal);
        Assert.Empty(odf13.Footers); // Hidden.
        Assert.True(((WordParagraph)odf13.Blocks[2]).Format.PageBreakBefore); // After the paragraph with a break after it.
        Assert.False(((WordParagraph)odf13.Blocks[0]).Format.PageBreakBefore); // The section starts the page.

        Assert.IsType<WordTable>(document.Sections[4].Blocks[0]);
        Assert.Equal(7, document.Sections[5].PageNumberStart);
        Assert.Empty(document.Sections[5].Headers);
        Assert.Equal(2 * Cm, document.Sections[5].MarginTop, 3); // No header: the page margin.
    }

    [Fact]
    public void Page_layouts_fit_their_margins_and_default_what_they_lack()
    {
        var layouts = PageLayout("pm1", "fo:page-width=\"5cm\" fo:page-height=\"4cm\" fo:margin=\"2cm\"")
            + "<style:page-layout style:name=\"pm2\"><style:page-layout-properties fo:page-width=\"20cm\" fo:page-height=\"10cm\" style:print-orientation=\"landscape\"/></style:page-layout>";
        var masters = "<style:master-page style:name=\"A\" style:page-layout-name=\"pm1\"/><style:master-page style:name=\"B\" style:page-layout-name=\"pm2\"/>";
        var document = Read(Document(P("Small") + P("Wide", "W"), ParagraphStyle("W", attributes: "style:master-page-name=\"B\""), pageLayouts: layouts, masters: masters));
        var small = document.Sections[0];
        Assert.Equal(5 * Cm, small.PageWidth, 3);
        Assert.Equal(small.PageWidth - 72, small.MarginLeft + small.MarginRight, 3); // At least an inch is left for the text.
        Assert.Equal(small.PageHeight - 72, small.MarginTop + small.MarginBottom, 3);
        var wide = document.Sections[1];
        Assert.Equal((20 * Cm, 10 * Cm), (wide.PageWidth, wide.PageHeight)); // Landscape and already wider than high.
        Assert.Equal(2 * Cm, wide.MarginLeft, 3); // No margins given: 2 cm.
        var none = Read(Document(P("x")));
        Assert.Equal((612.0, 792.0), (none.Sections[0].PageWidth, none.Sections[0].PageHeight)); // No page styles at all.
    }

    [Fact]
    public void The_first_paragraphs_master_page_sets_the_first_section()
    {
        var masters = "<style:master-page style:name=\"Standard\" style:page-layout-name=\"pm1\"/><style:master-page style:name=\"Other\" style:page-layout-name=\"pm2\"/>";
        var layouts = PageLayout("pm1", "fo:page-width=\"21cm\" fo:page-height=\"29.7cm\"") + PageLayout("pm2", "fo:page-width=\"10cm\" fo:page-height=\"10cm\"");
        var document = Read(Document(P("First", "O") + P("Second"), ParagraphStyle("O", "style:page-number=\"5\"", attributes: "style:master-page-name=\"Other\""), pageLayouts: layouts, masters: masters));
        var section = Assert.Single(document.Sections);
        Assert.Equal(10 * Cm, section.PageWidth, 3);
        Assert.Equal(5, section.PageNumberStart);
        var restart = Read(Document(P("One") + P("Two", "R"), ParagraphStyle("R", "fo:break-before=\"page\" style:page-number=\"3\""), pageLayouts: layouts, masters: masters));
        Assert.Equal(2, restart.Sections.Count); // A page break that restarts the page numbers starts a section.
        Assert.Equal(3, restart.Sections[1].PageNumberStart);
        Assert.Equal(21 * Cm, restart.Sections[1].PageWidth, 3);
        var unknown = Read(Document(P("One") + P("Two", "U"), ParagraphStyle("U", attributes: "style:master-page-name=\"Missing\""), pageLayouts: layouts, masters: masters));
        Assert.Equal(21 * Cm, unknown.Sections[1].PageWidth, 3); // An unknown master page: the default one.
    }

    [Fact]
    public void Fields_notes_and_tracked_changes_are_read()
    {
        var document = Read(Fixture("fields.odt"));
        var reference = Paragraph(document, "See page").Inlines.OfType<FieldInline>().Single();
        Assert.Equal((FieldKind.PageRef, "later", "1"), (reference.Kind, reference.Argument, reference.CachedResult));
        var notes = Paragraph(document, "Footnote here").Inlines.OfType<NoteReference>().ToList();
        Assert.Equal([NoteKind.Footnote, NoteKind.Endnote, NoteKind.Footnote], notes.Select(n => n.Kind));
        Assert.Equal([null, null, "*"], notes.Select(n => n.CustomMark));
        Assert.All(notes, n => Assert.Equal(VerticalPosition.Superscript, n.Format.Position));
        var note = Assert.IsType<WordParagraph>(document.Footnotes[notes[0].Id].Single());
        Assert.IsType<NoteMark>(note.Inlines[0]);
        Assert.Equal(" The first footnote text.", Text(note));
        Assert.Equal(10, note.MarkFormat.FontSize);
        Assert.True(document.Endnotes.ContainsKey(notes[1].Id));
        Assert.Equal(NumberFormat.LowerRoman, document.EndnoteFormat);
        Assert.Equal(NumberFormat.Decimal, document.FootnoteFormat);
        Assert.Equal("Inserted new text stays.", Text(Paragraph(document, "Inserted")));
        Assert.Equal("Commented text.", Text(Paragraph(document, "Commented")));
        Assert.Equal("Ruby base text.", Text(Paragraph(document, "Ruby")));
        Assert.Equal("Hidden field and a placeholder.", Text(Paragraph(document, "Hidden field")));
        Assert.DoesNotContain(Paragraphs(document), p => Text(p).Contains("Hidden section", StringComparison.Ordinal));
        Assert.Contains("chapter: Fields and notes, sequence: Table 1.", Text(Paragraph(document, "Title:")), StringComparison.Ordinal);
        var body = Paragraph(document, "in the body").Inlines.OfType<FieldInline>().Select(f => (f.Kind, f.CachedResult));
        Assert.Equal([(FieldKind.Page, "2"), (FieldKind.NumPages, "2")], body);
        Assert.Equal("Fields fixture", document.Title);
        var toc = Paragraph(document, "Later section");
        Assert.Equal("later", toc.Inlines.OfType<HyperlinkInline>().Single().Anchor);
    }

    [Fact]
    public void Notes_take_their_formats_from_the_notes_configuration()
    {
        var common = TextStyle("Anchor", "fo:color=\"#0000ff\"") + TextStyle("Body", "fo:font-weight=\"bold\"")
            + "<text:notes-configuration text:note-class=\"footnote\" style:num-format=\"a\" text:citation-style-name=\"Anchor\" text:citation-body-style-name=\"Body\"/>"
            + "<text:notes-configuration text:note-class=\"endnote\" style:num-format=\"1\"/>";
        var body = P("Text<text:note text:note-class=\"footnote\"><text:note-citation>a</text:note-citation><text:note-body>" + P("Note")
            + "</text:note-body></text:note><text:note text:note-class=\"endnote\"><text:note-citation>1</text:note-citation></text:note>");
        var document = Read(Document(body, common: common));
        Assert.Equal(NumberFormat.LowerLetter, document.FootnoteFormat);
        Assert.Equal(NumberFormat.Decimal, document.EndnoteFormat);
        var references = Paragraphs(document)[0].Inlines.OfType<NoteReference>().ToList();
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), references[0].Format.Color);
        Assert.Equal(VerticalPosition.Baseline, references[0].Format.Position);
        Assert.True(((NoteMark)((WordParagraph)document.Footnotes[references[0].Id][0]).Inlines[0]).Format.Bold);
        Assert.IsType<NoteMark>(((WordParagraph)document.Endnotes[references[1].Id][0]).Inlines[0]); // A note without a body gets a paragraph.
        var plain = Read(Document(P("Text<text:note><text:note-citation>1</text:note-citation><text:note-body>" + P("Note") + "</text:note-body></text:note>")));
        Assert.Equal(VerticalPosition.Superscript, Paragraphs(plain)[0].Inlines.OfType<NoteReference>().Single().Format.Position); // No notes configuration.
        var nested = Read(Document(P("A<text:note><text:note-body><text:p>B<text:note><text:note-body><text:p>C</text:p></text:note-body></text:note></text:p></text:note-body></text:note>")));
        Assert.Single(nested.Footnotes);
        Assert.Equal(" B", Text((WordParagraph)nested.Footnotes.Values.Single()[0]));
    }

    [Fact]
    public void Frames_become_inline_floating_and_block_images()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var document = Read(Fixture("images.odt"), diagnostics: diagnostics);
        ImageInline Image(string text) => Paragraph(document, text).Inlines.OfType<ImageInline>().Single();
        var inline = Image("Inline image");
        Assert.Equal((ImagePlacement.Inline, 2 * Cm, Cm), (inline.Placement, inline.Width, inline.Height));
        Assert.Equal("Pictures/red.png", inline.Name);
        var right = Image("Floating right");
        Assert.Equal((ImagePlacement.Float, WordAlignment.End), (right.Placement, right.Alignment));
        var center = Image("Centered block");
        Assert.Equal((ImagePlacement.Block, WordAlignment.Center), (center.Placement, center.Alignment));
        var left = Image("Floating left");
        Assert.Equal((ImagePlacement.Float, WordAlignment.Start), (left.Placement, left.Alignment));
        Assert.Equal("Pictures/fallback.png", Image("Alternatives").Name);
        Assert.Equal("Pictures/vector.svg", Image("Vector only").Name);
        Assert.Equal("./ObjectReplacements/Object 1", Image("Chart").Name);
        Assert.Equal("A red rectangle", Image("Described").AlternativeText);
        Assert.Empty(Paragraph(document, "Linked image").Inlines.OfType<ImageInline>());
        Assert.Empty(Paragraph(document, "Missing image").Inlines.OfType<ImageInline>());
        Assert.Equal("Pictures/old.wmf", Image("Metafile").Name); // Reported when it is drawn.
        var box = Paragraph(document, "Text box anchor").Inlines.OfType<AnchoredBlocks>().Single();
        Assert.Equal("Text box content.", Text((WordParagraph)box.Blocks.Single()));
        Assert.Single(Paragraph(document, "Shape:").Inlines.OfType<AnchoredBlocks>());
        Assert.Equal(new Uri("https://example.com/"), Paragraph(document, "Linked picture").Inlines.OfType<HyperlinkInline>().Single().Uri);
        var page = Paragraphs(document).First(p => p.Inlines.Count == 1 && p.Inlines[0] is ImageInline);
        Assert.Equal(ImagePlacement.Inline, ((ImageInline)page.Inlines[0]).Placement); // A page anchored frame: in a paragraph of its own,
        Assert.Equal(WordAlignment.Center, page.Format.Alignment); // aligned as the frame is.
        Assert.Contains(diagnostics, d => d.Message.Contains("linked image", StringComparison.Ordinal));
    }

    [Fact]
    public void Frame_offsets_sizes_and_embedded_data_are_read()
    {
        var png = Fixtures.Read(Path.Combine("PngSuite", "basn2c08.png"));
        var automatic = Style("fr", "graphic", "<style:graphic-properties style:wrap=\"right\" style:horizontal-pos=\"from-left\"/>");
        var body = P("<draw:frame draw:style-name=\"fr\" text:anchor-type=\"char\" svg:x=\"3cm\" fo:min-width=\"4cm\" fo:min-height=\"1cm\"><draw:image xlink:href=\"./Pictures/a.png\"/></draw:frame>x")
            + P("<draw:frame text:anchor-type=\"as-char\" svg:width=\"99999cm\"><draw:image><office:binary-data>" + System.Convert.ToBase64String(png) + "</office:binary-data></draw:image></draw:frame>")
            + P("<draw:frame text:anchor-type=\"as-char\"><draw:image><office:binary-data>!!not base64!!</office:binary-data></draw:image></draw:frame>")
            + P("<draw:frame text:anchor-type=\"as-char\"><draw:object xlink:href=\"./Object 1\"/></draw:frame><draw:frame><draw:applet/></draw:frame>")
            + P("<draw:frame text:anchor-type=\"as-char\"><draw:image xlink:href=\"/abs.png\"/><draw:image xlink:href=\"C:\\x.png\"/><draw:image xlink:href=\"Pictures/a.png\"/></draw:frame>");
        var diagnostics = new List<DocumentDiagnostic>();
        var document = Read(Document(body, automatic, parts: [("Pictures/a.png", png)]), diagnostics: diagnostics);
        var paragraphs = Paragraphs(document);
        var floating = paragraphs[0].Inlines.OfType<ImageInline>().Single();
        Assert.Equal((ImagePlacement.Float, 3 * Cm, 4 * Cm, Cm), (floating.Placement, floating.Offset!.Value, floating.Width, floating.Height));
        var embedded = paragraphs[1].Inlines.OfType<ImageInline>().Single();
        Assert.Equal(png, embedded.Data);
        Assert.Equal(0, embedded.Width); // A size beyond reason: the image's own.
        Assert.Empty(paragraphs[2].Inlines.OfType<ImageInline>());
        Assert.Contains(diagnostics, d => d.Message.Contains("not base64", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("without a replacement image", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message.Contains("applet", StringComparison.Ordinal));
        Assert.Equal("Pictures/a.png", paragraphs[4].Inlines.OfType<ImageInline>().Single().Name); // The linked alternatives are left out.
        Assert.True(OdtReader.IsLinked("/abs.png"));
        Assert.True(OdtReader.IsLinked("C:\\x.png"));
        Assert.True(OdtReader.IsLinked("https://example.com/x.png"));
        Assert.True(OdtReader.IsLinked("../x.png"));
        Assert.False(OdtReader.IsLinked("Pictures/x:y.png"));
        Assert.False(OdtReader.IsLinked("./Pictures/x.png"));
    }

    [Fact]
    public void Odf_values_are_parsed()
    {
        Assert.Equal(72, Odf.Length("1in"));
        Assert.Equal(Cm, Odf.Length(" 1cm "));
        Assert.Equal(Cm / 10, Odf.Length("1mm")!.Value, 6);
        Assert.Equal(12, Odf.Length("1pc"));
        Assert.Equal(7.5, Odf.Length("10px"));
        Assert.Equal(12, Odf.Length("12pt"));
        Assert.Equal(5, Odf.Length("5"));
        Assert.Null(Odf.Length("50%"));
        Assert.Null(Odf.Length("1em"));
        Assert.Null(Odf.Length("abc"));
        Assert.Null(Odf.Length(""));
        Assert.Equal(100_000, Odf.Length("1e300pt"));
        Assert.Null(Odf.Length("NaNpt"));
        Assert.Equal(150, Odf.Percent("150%"));
        Assert.Null(Odf.Percent("150"));
        Assert.Equal(PdfColor.FromRgb(0x11, 0x22, 0x33), Odf.Color("#123"));
        Assert.Equal(PdfColor.FromRgb(0xAB, 0xCD, 0xEF), Odf.Color("#abcdef"));
        Assert.Null(Odf.Color("transparent"));
        Assert.Null(Odf.Color("#12345"));
        Assert.Equal(new BorderLine(0.5, PdfColor.FromRgb(255, 0, 0), BorderLineStyle.Dashed), Odf.Border("#ff0000 dashed thin"));
        Assert.Equal(new BorderLine(2, PdfColor.Black, BorderLineStyle.Single), Odf.Border("thick ridge"));
        Assert.Equal(new BorderLine(1, PdfColor.Black, BorderLineStyle.Double), Odf.Border("medium double"));
        Assert.Equal(new BorderLine(0.75, PdfColor.Black), Odf.Border("solid"));
        Assert.Equal(BorderLine.None, Odf.Border("none"));
        Assert.Equal(BorderLine.None, Odf.Border("0pt solid #000000"));
        Assert.Null(Odf.Border(" "));
        Assert.Equal(NumberFormat.None, Odf.NumberFormatOf(""));
        Assert.Equal(NumberFormat.UpperLetter, Odf.NumberFormatOf("A"));
        Assert.Equal(NumberFormat.Decimal, Odf.NumberFormatOf("\u0661"));
        Assert.Equal("Liberation Serif", Odf.FamilyName("'Liberation Serif', serif"));
        Assert.Null(Odf.FamilyName("''"));
    }

    [Fact]
    public void Text_style_values_cover_their_variants()
    {
        var automatic = TextStyle("W", "fo:font-weight=\"700\" fo:font-style=\"oblique\" style:text-underline-type=\"single\"")
            + TextStyle("N", "fo:font-weight=\"400\" style:text-underline-style=\"solid\" style:text-underline-type=\"none\" style:text-line-through-type=\"single\"")
            + TextStyle("C", "style:use-window-font-color=\"true\" fo:background-color=\"transparent\" fo:language=\"zxx\" style:text-line-through-style=\"solid\" style:text-line-through-type=\"none\"")
            + TextStyle("D", "style:text-underline-style=\"long-dash\" style:text-position=\"0% 100%\" fo:text-transform=\"none\" fo:font-variant=\"normal\" fo:letter-spacing=\"normal\" fo:language=\"de\"")
            + TextStyle("S", "style:text-position=\"-20%\" fo:font-size=\"0pt\"")
            + ParagraphStyle("P", text: "fo:color=\"#ff0000\" fo:text-transform=\"uppercase\" fo:background-color=\"#00ff00\" fo:font-variant=\"small-caps\" fo:font-family=\"'My Font', serif\"");
        var body = P("<text:span text:style-name=\"W\">w</text:span><text:span text:style-name=\"N\">n</text:span><text:span text:style-name=\"C\">c</text:span>"
            + "<text:span text:style-name=\"D\">d</text:span><text:span text:style-name=\"S\">s</text:span>", "P");
        var runs = Paragraphs(Read(Document(body, automatic)))[0].Inlines.OfType<TextRun>().ToDictionary(r => r.Text, r => r.Format);
        Assert.True(runs["w"].Bold);
        Assert.True(runs["w"].Italic);
        Assert.Equal(UnderlineStyle.Single, runs["w"].Underline);
        Assert.Equal("My Font", runs["w"].FontFamily);
        Assert.False(runs["n"].Bold);
        Assert.Equal(UnderlineStyle.None, runs["n"].Underline);
        Assert.True(runs["n"].Strike);
        Assert.Null(runs["c"].Color);
        Assert.Null(runs["c"].Background);
        Assert.Null(runs["c"].Language);
        Assert.False(runs["c"].Strike);
        Assert.Equal(UnderlineStyle.Dashed, runs["d"].Underline);
        Assert.Equal((VerticalPosition.Baseline, 0.0), (runs["d"].Position, runs["d"].BaselineShift));
        Assert.False(runs["d"].Caps);
        Assert.False(runs["d"].SmallCaps);
        Assert.Equal("de", runs["d"].Language);
        Assert.Equal(VerticalPosition.Subscript, runs["s"].Position);
        Assert.Equal(12, runs["s"].FontSize); // A zero size is ignored.
        Assert.True(runs["w"].Caps);
        Assert.True(runs["w"].SmallCaps);
    }

    [Fact]
    public void A_section_hides_or_shows_its_content_and_indexes_show_their_body()
    {
        var document = Read(Fixture("fields.odt"));
        var texts = Paragraphs(document).Select(Text).ToList();
        Assert.Contains("Visible section text.", texts);
        Assert.Contains("Two column section text.", texts);
        Assert.Equal(texts.IndexOf("Fields and notes") + 1, texts.IndexOf("Contents"));
        Assert.DoesNotContain("Template title", texts);
    }

    [Fact]
    public void Meta_and_language_come_from_the_metadata()
    {
        var document = Read(Document(P("x"), meta: "<dc:title> Title </dc:title><meta:initial-creator>Creator</meta:initial-creator><dc:language>fr-FR</dc:language>"));
        Assert.Equal(("Title", "Creator", "fr-FR"), (document.Title, document.Author, document.Language));
        var none = Read(Document(P("x"), meta: "<dc:title> </dc:title>"));
        Assert.Null(none.Title);
        Assert.Null(none.Author);
    }

    [Fact]
    public void Odf13_and_libreoffice_header_first_elements_are_read()
    {
        var layouts = PageLayout("pm", "fo:page-width=\"21cm\" fo:page-height=\"29.7cm\"");
        var masters = "<style:master-page style:name=\"Standard\" style:page-layout-name=\"pm\"><style:header><text:p>Header</text:p></style:header>"
            + "<loext:header-first><text:p>First</text:p></loext:header-first><loext:footer-first><text:p>First footer</text:p></loext:footer-first></style:master-page>";
        var section = Read(Document(P("x"), pageLayouts: layouts, masters: masters)).Sections[0];
        Assert.True(section.TitlePage);
        Assert.Equal("First", Text((WordParagraph)section.Headers[HeaderFooterKind.First][0]));
        Assert.Equal("First footer", Text((WordParagraph)section.Footers[HeaderFooterKind.First][0]));
        Assert.False(Read(Document(P("x"), pageLayouts: layouts, masters: masters)).EvenAndOddHeaders);
    }
}
