using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;
using static RaiCore.Pdf.Tests.Sheets.BiffBuilder;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// How worksheets print: headers and footers (sections, fields, font codes, first and even pages), page breaks set by
/// hand, rich text, stacked text, shrink to fit and conditional formatting, in .xlsx and .xls.
/// </summary>
public class SheetPrintTests
{
    private static readonly DateTime Date = new(2026, 3, 4, 13, 5, 0);

    private static byte[] Xlsx(string sheetData, string sheetExtra = "", string? styles = null, string? sharedStrings = null) =>
        XlsxToPdfTests.Workbook(sheetData, styles, sharedStrings, sheetExtra);

    private static byte[] Convert(byte[] xlsx, bool usePageSetup = true) =>
        XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { PrintDate = Date, UsePageSetup = usePageSetup });

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static string Rows(int count) => string.Concat(Enumerable.Range(1, count).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\" t=\"inlineStr\"><is><t>Row {r}</t></is></c></row>"));

    private static HeaderFooterContext Context(int page = 3, int pages = 9) => new(page, pages, "Sales", "book.xlsx", Date, CultureInfo.InvariantCulture);

    // ---- Headers and footers ----

    [Fact]
    public void Header_codes_make_sections_fields_and_formatted_runs()
    {
        var sections = HeaderFooterText.Parse("Before&LLeft &P of &N&C&\"Arial,Bold Italic\"&14Mid&B&&&U u&E&S&X&Kff0000x&R&A &F &D &T&P+2&P-1&Z&G&Q", Context());
        Assert.Equal("Left 3 of 9", string.Concat(sections[0].Select(r => r.Text)));
        Assert.Equal("BeforeMid& ux", string.Concat(sections[1].Select(r => r.Text)));
        Assert.Equal("Sales book.xlsx 2026-03-04 13:0552", string.Concat(sections[2].Select(r => r.Text)));
        var mid = sections[1].Single(r => r.Text == "Mid");
        Assert.Equal(("Arial", 14.0, true, true), (mid.Font, mid.Size!.Value, mid.Bold, mid.Italic));
        var amp = sections[1].Single(r => r.Text == "&");
        Assert.False(amp.Bold); // &B turned bold off again.
        var x = sections[1].Single(r => r.Text == "x");
        Assert.Equal((CellUnderline.Double, true, true, PdfColor.FromRgb(255, 0, 0)), (x.Underline, x.Strike, x.Superscript, x.Color));
        Assert.Equal(CellUnderline.Single, sections[1].Single(r => r.Text == " u").Underline);

        // Cut short codes are not trouble.
        string All(string text) => string.Concat(HeaderFooterText.Parse(text, Context()).SelectMany(s => s).Select(r => r.Text));
        Assert.Equal("a&", All("&K12&999a&")); // A color that is not hex is black; a lone & at the end stays.
        Assert.Equal(string.Empty, All("&\"Arial,Bold"));
        Assert.Equal("1", All("&P-2"));
        Assert.Equal(409, HeaderFooterText.Parse("&999z", Context())[1][0].Size);
    }

    [Fact]
    public void First_and_even_pages_take_their_own_texts()
    {
        var headerFooter = new SheetHeaderFooter { OddHeader = "odd", EvenHeader = "even", FirstHeader = "first", OddFooter = "f" };
        Assert.Equal(["odd", "odd", "odd"], new[] { 1, 2, 3 }.Select(p => headerFooter.For(p).Header));
        var both = headerFooter with { DifferentFirst = true, DifferentOddEven = true };
        Assert.Equal(["first", "even", "odd", "even"], new[] { 1, 2, 3, 4 }.Select(p => both.For(p).Header));
        Assert.Null(both.For(1).Footer);
        Assert.True(new SheetHeaderFooter().IsEmpty);
    }

    [Fact]
    public void Headers_and_footers_print_on_every_page_with_its_number()
    {
        var xlsx = Xlsx(Rows(120), "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.5\" footer=\"0.25\"/>"
            + "<pageSetup firstPageNumber=\"5\" useFirstPageNumber=\"1\"/>"
            + "<headerFooter differentFirst=\"1\"><oddHeader>&amp;LReport&amp;R&amp;D</oddHeader><oddFooter>&amp;CPage &amp;P of &amp;N</oddFooter>"
            + "<firstHeader>&amp;CCover</firstHeader></headerFooter>");
        var pdf = Convert(xlsx);
        var pages = Pages(pdf);
        Assert.True(pages.Length >= 3);
        Assert.Contains("Cover", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Report", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Page 5", pages[0], StringComparison.Ordinal); // The first page has no footer of its own.
        for (var i = 1; i < pages.Length; i++)
        {
            Assert.Contains("Report", pages[i], StringComparison.Ordinal);
            Assert.Contains("2026-03-04", pages[i], StringComparison.Ordinal);
            Assert.Contains($"Page {i + 5} of {pages.Length}", pages[i], StringComparison.Ordinal);
        }

        // The header 0.5 inch below the top, the footer ending 0.25 inch above the bottom.
        var reader = PdfReader.Open(pdf);
        var extractor = new TextExtractor(reader);
        var height = reader.Pages[1].Height;
        var report = Assert.Single(extractor.FindOnPage(1, "Report")).Bounds;
        var footer = Assert.Single(extractor.FindOnPage(1, "Page 6")).Bounds;
        Assert.InRange(height - report.Y - report.Height, 34, 40);
        Assert.InRange(footer.Y, 16, 22);

        // Without the page setup: none.
        Assert.DoesNotContain(Pages(Convert(xlsx, usePageSetup: false)), p => p.Contains("Report", StringComparison.Ordinal));
    }

    [Fact]
    public void Xls_headers_footers_and_their_variants_are_read()
    {
        var xls = SingleSheet(s => s
            .Record(0x0204, Cell(0, 0), Text("Cell"))
            .Record(0x0014, Text("&CTop"))
            .Record(0x0015, Text("&RBottom &P"))
            .Record(0x089C, new byte[28], U16(0x0002), U16(0), U16(0), U16(6), U16(0), [0], Encoding.ASCII.GetBytes("&LFirst")[1..], Encoding.ASCII.GetBytes("&LFirst")[..0]));
        var sheet = XlsConverter.Read(xls)[0];
        var headerFooter = sheet.Formatting!.HeaderFooter!;
        Assert.Equal(("&CTop", "&RBottom &P", true), (headerFooter.OddHeader, headerFooter.OddFooter, headerFooter.DifferentFirst));
        Assert.Equal("LFirst", headerFooter.FirstHeader);
        var page = Assert.Single(Pages(XlsToPdf.ConvertToBytes(xls)));
        Assert.DoesNotContain("Top", page, StringComparison.Ordinal);
        Assert.Contains("LFirst", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Bottom", page, StringComparison.Ordinal); // The first page's footer: none.
        var second = XlsToPdf.ConvertToBytes(SingleSheet(s => s.Record(0x0204, Cell(0, 0), Text("Cell")).Record(0x0015, Text("&RBottom &P"))));
        Assert.Contains("Bottom 1", Assert.Single(Pages(second)), StringComparison.Ordinal);
    }

    private static readonly Lazy<JsonElement> XlsExpected = new(() => JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Sheets/Xls/expected.json"))).RootElement);

    public static TheoryData<string> XlsPrintCorpus => [.. XlsExpected.Value.EnumerateObject()
        .Where(p => p.Value.TryGetProperty("print", out var print) && print.EnumerateObject().Any()).Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(XlsPrintCorpus))]
    public void Xls_corpus_headers_footers_and_page_breaks_match_the_independent_walk(string name)
    {
        var data = Fixtures.Read(Path.Combine("Sheets", "Xls", name));
        var limits = ResourceLimits.Default;
        var workbook = XlsConverter.Open(data, limits, CultureInfo.InvariantCulture, null).Workbook;
        foreach (var sheet in XlsExpected.Value.GetProperty(name).GetProperty("print").EnumerateObject())
        {
            var index = workbook.Sheets.FindIndex(s => s.Name == sheet.Name);
            var read = XlsWorksheet.Read(workbook, workbook.SheetOffsets[index], limits.MaxTableCells, null, limits);
            string? Text(string key) => sheet.Value.TryGetProperty(key, out var v) ? v.GetString() : null;
            int[] Breaks(string key) => sheet.Value.TryGetProperty(key, out var v) ? [.. v.EnumerateArray().Select(b => b.GetInt32())] : [];
            Assert.Equal(Text("header"), read.HeaderFooter.OddHeader);
            Assert.Equal(Text("footer"), read.HeaderFooter.OddFooter);
            Assert.Equal(Breaks("rowBreaks"), read.RowBreaks);
            Assert.Equal(Breaks("columnBreaks"), read.ColumnBreaks);
        }

        // Every page prints its footer's page number where it has one.
        var pages = Pages(XlsToPdf.ConvertToBytes(data, new XlsxConverterOptions { PrintDate = Date }));
        if (name == "46368.xls")
        {
            Assert.Contains("Sheet1", pages[0], StringComparison.Ordinal);
            Assert.Contains("Page 1", pages[0], StringComparison.Ordinal);
        }
    }

    // ---- Page breaks set by hand ----

    [Fact]
    public void Page_breaks_set_by_hand_start_pages_and_bands()
    {
        var xlsx = Xlsx(Rows(30) + "<row r=\"31\"><c r=\"D31\"><v>4</v></c></row>",
            "<rowBreaks count=\"3\"><brk id=\"10\" man=\"1\"/><brk id=\"20\" man=\"1\"/><brk id=\"25\"/></rowBreaks><colBreaks count=\"1\"><brk id=\"2\" man=\"1\"/></colBreaks>");
        var pages = Pages(Convert(xlsx));

        // Rows 1-10, 11-20 and 21-31 of columns A-B, then the same of columns C-D.
        Assert.Equal(6, pages.Length);
        Assert.StartsWith("Row 1\n", pages[0], StringComparison.Ordinal);
        Assert.Contains("Row 10", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Row 11", pages[0], StringComparison.Ordinal);
        Assert.StartsWith("Row 11\n", pages[1], StringComparison.Ordinal);
        Assert.StartsWith("Row 21\n", pages[2], StringComparison.Ordinal);
        Assert.Contains("4", pages[5], StringComparison.Ordinal);
        Assert.Single(Pages(Convert(xlsx, usePageSetup: false)), p => p.Contains("Row 11", StringComparison.Ordinal));
    }

    [Fact]
    public void Page_breaks_go_to_the_printed_row_that_starts_the_page()
    {
        Assert.Equal([1, 2], XlsxConverter.Breaks([4, 5, 1, 99, 2], [0, 2, 5, 6], 0));
        Assert.Equal([2, 3], XlsxConverter.Breaks([2, 5, 6], [0, 2, 5, 6], 1)); // Not into the rows before the body.
        Assert.Empty(XlsxConverter.Breaks([], [0], 0));
    }

    [Fact]
    public void Print_titles_repeat_after_a_page_break_set_by_hand()
    {
        var xlsx = Xlsx(Rows(12), "<rowBreaks><brk id=\"6\" man=\"1\"/></rowBreaks>");
        xlsx = XlsxToPdfTests.Workbook(Rows(12), sheetExtra: "<rowBreaks><brk id=\"6\" man=\"1\"/></rowBreaks>",
            workbookExtra: "<definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Sheet1!$1:$1</definedName></definedNames>");
        var pages = Pages(Convert(xlsx));
        Assert.Equal(2, pages.Length);
        Assert.StartsWith("Row 1\nRow 7\n", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Xls_page_breaks_are_read()
    {
        var xls = SingleSheet(s =>
        {
            for (var r = 0; r < 8; r++)
            {
                s.Record(0x0203, Cell(r, 0), F64(r + 1)).Record(0x0203, Cell(r, 1), F64(r + 1));
            }

            s.Record(0x001B, U16(2), U16(3), U16(0), U16(255), U16(6), U16(0), U16(255)) // Before rows 4 and 7.
                .Record(0x001A, U16(1), U16(1), U16(0), U16(65535)); // Before column B.
        });
        var formatting = XlsConverter.Read(xls)[0].Formatting!;
        Assert.Equal([3, 6], formatting.RowBreaks);
        Assert.Equal([1], formatting.ColumnBreaks);
        Assert.Equal(6, Pages(XlsToPdf.ConvertToBytes(xls)).Length);
    }

    // ---- Rich, stacked and shrunk text ----

    [Fact]
    public void Rich_text_runs_keep_their_fonts()
    {
        var xlsx = XlsxToPdfTests.Workbook(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><r><rPr><i/><color rgb=\"FF00FF00\"/></rPr><t>green</t></r><t> tail</t></is></c>"
            + "<c r=\"C1\" t=\"s\"><v>1</v></c></row>",
            sharedStrings: "<si><r><rPr><b/><sz val=\"20\"/><rFont val=\"Courier New\"/><u val=\"double\"/><strike/></rPr><t>big</t></r><r><t> plain</t></r><rPh><t>x</t></rPh></si><si><t>none</t></si>");
        var cells = XlsxConverter.Read(xlsx)[0].Rows[0];
        Assert.Equal("big plain", cells[0].Text);
        Assert.Equal(new CellFont("Courier New", 20, true, false, CellUnderline.Double, true, null), cells[0].Runs![0].Font);
        Assert.Null(cells[0].Runs![1].Font);
        Assert.Equal(new CellFont(null, null, false, true, CellUnderline.None, false, PdfColor.FromRgb(0, 255, 0)), cells[1].Runs![0].Font);
        Assert.Equal(" tail", cells[1].Runs![1].Text);
        Assert.Null(cells[2].Runs);

        var applied = cells[1].Runs![0].Font!.Apply(new CellFormat { Bold = true, FontSize = 9, Color = PdfColor.Black });
        Assert.Equal((false, true, 9.0, PdfColor.FromRgb(0, 255, 0)), (applied.Bold, applied.Italic, applied.FontSize!.Value, applied.Color));
        var text = Pages(Convert(xlsx))[0];
        Assert.Contains("big plain", text, StringComparison.Ordinal);
        Assert.Contains("green tail", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Stacked_text_is_one_character_a_line_and_shrunk_text_fits_its_column()
    {
        const string styles = "<cellXfs count=\"3\"><xf/><xf><alignment textRotation=\"255\"/></xf><xf><alignment shrinkToFit=\"1\"/></xf></cellXfs>";
        var xlsx = XlsxToPdfTests.Workbook(
            "<row r=\"1\"><c r=\"A1\" s=\"1\" t=\"inlineStr\"><is><t>Up</t></is></c><c r=\"B1\" s=\"2\" t=\"inlineStr\"><is><t>A long text set to shrink to fit its column</t></is></c><c r=\"C1\"><v>1</v></c></row>",
            styles, sheetExtra: string.Empty, sheetBefore: "<cols><col min=\"2\" max=\"2\" width=\"10\" customWidth=\"1\"/></cols>");
        var pdf = Convert(xlsx);
        Assert.Contains("U\nP", Pages(pdf)[0].ToUpperInvariant(), StringComparison.Ordinal);
        var sizes = Regex.Matches(Encoding.Latin1.GetString(PdfReader.Open(pdf).Pages[0].GetContent()), @"([\d.]+) Tf").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.Contains(sizes, s => s < 8); // The shrunk text, from 11 points.
        Assert.Contains(sizes, s => s == 11);
    }

    [Fact]
    public void Xls_stacked_and_shrunk_text_are_read_from_the_xf()
    {
        var xls = SingleSheet(
            s => s.Record(0x0204, Cell(0, 0, 1), Text("Up")).Record(0x0204, Cell(0, 1, 2), Text("Shrunk")),
            g => g.Font("Arial").Xf()
                .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(255), U8(0), U8(0), U32(0), U32(0), U16(64 | (65 << 7)))
                .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(0), U8(0x10), U8(0), U32(0), U32(0), U16(64 | (65 << 7))));
        var cells = XlsConverter.Read(xls)[0].Rows[0];
        Assert.Equal((true, false), (cells[0].Format!.Stacked, cells[0].Format!.ShrinkToFit));
        Assert.Equal((false, true), (cells[1].Format!.Stacked, cells[1].Format!.ShrinkToFit));
        Assert.Contains("U\np", Pages(XlsToPdf.ConvertToBytes(xls))[0], StringComparison.Ordinal);
    }

    // ---- Conditional formatting ----

    private const string CfStyles = "<dxfs count=\"2\"><dxf><font><b/><color rgb=\"FFFF0000\"/></font><fill><patternFill><fgColor rgb=\"FF00FFFF\"/><bgColor rgb=\"FFFFFF00\"/></patternFill></fill>"
        + "<border><left style=\"thin\"><color rgb=\"FF0000FF\"/></left></border></dxf><dxf><font><i/></font></dxf></dxfs>";

    private static SheetCell[] Column(string sheetExtra, params string[] values)
    {
        var data = string.Concat(values.Select((v, i) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? $"<row r=\"{i + 1}\"><c r=\"A{i + 1}\"><v>{v}</v></c></row>"
            : $"<row r=\"{i + 1}\"><c r=\"A{i + 1}\" t=\"inlineStr\"><is><t>{v}</t></is></c></row>"));
        var xlsx = XlsxToPdfTests.Workbook(data, $"<cellXfs count=\"1\"><xf/></cellXfs>{CfStyles}", sheetExtra: sheetExtra);
        return [.. XlsxConverter.Read(xlsx)[0].Rows.Select(r => r[0])];
    }

    private static string Rule(string sqref, string rule) => $"<conditionalFormatting sqref=\"{sqref}\">{rule}</conditionalFormatting>";

    private static bool Bold(SheetCell cell) => cell.Format?.Bold ?? false;

    [Fact]
    public void Cell_value_rules_apply_their_format_by_priority_and_stop_if_true()
    {
        var cells = Column(
            Rule("A1:A5", "<cfRule type=\"cellIs\" dxfId=\"0\" priority=\"2\" operator=\"between\"><formula>2</formula><formula>4</formula></cfRule>")
            + Rule("A1:A5", "<cfRule type=\"cellIs\" dxfId=\"1\" priority=\"1\" operator=\"equal\" stopIfTrue=\"1\"><formula>3</formula></cfRule>"),
            "1", "2", "3", "4", "5");
        Assert.Equal([false, true, false, true, false], cells.Select(Bold));
        Assert.True(cells[2].Format!.Italic); // Rule 1 stopped rule 2 for it.
        var format = cells[1].Format!;
        Assert.Equal((PdfColor.FromRgb(255, 0, 0), PdfColor.FromRgb(255, 255, 0), PdfColor.FromRgb(0, 0, 255)), (format.Color, format.Fill, format.Left.Color));

        // Text constants; a formula that is not a constant is not applied.
        var text = Column(Rule("A1:A3", "<cfRule type=\"cellIs\" dxfId=\"0\" priority=\"1\" operator=\"equal\"><formula>\"b\"</formula></cfRule>")
            + Rule("A1:A3", "<cfRule type=\"cellIs\" dxfId=\"1\" priority=\"2\" operator=\"greaterThan\"><formula>$B$1</formula></cfRule>"), "a", "B", "c");
        Assert.Equal([false, true, false], text.Select(Bold));
        Assert.All(text, c => Assert.False(c.Format?.Italic ?? false));
    }

    [Theory]
    [InlineData("<cfRule type=\"top10\" dxfId=\"0\" priority=\"1\" rank=\"2\"/>", "00011")]
    [InlineData("<cfRule type=\"top10\" dxfId=\"0\" priority=\"1\" rank=\"40\" percent=\"1\" bottom=\"1\"/>", "11000")]
    [InlineData("<cfRule type=\"aboveAverage\" dxfId=\"0\" priority=\"1\"/>", "00011")]
    [InlineData("<cfRule type=\"aboveAverage\" dxfId=\"0\" priority=\"1\" aboveAverage=\"0\" equalAverage=\"1\"/>", "11100")]
    [InlineData("<cfRule type=\"duplicateValues\" dxfId=\"0\" priority=\"1\"/>", "11000")]
    [InlineData("<cfRule type=\"uniqueValues\" dxfId=\"0\" priority=\"1\"/>", "00111")]
    [InlineData("<cfRule type=\"containsText\" dxfId=\"0\" priority=\"1\" text=\"AB\"/>", "11000")]
    [InlineData("<cfRule type=\"notContainsText\" dxfId=\"0\" priority=\"1\" text=\"ab\"/>", "00111")]
    [InlineData("<cfRule type=\"beginsWith\" dxfId=\"0\" priority=\"1\" text=\"x\"/>", "00100")]
    [InlineData("<cfRule type=\"endsWith\" dxfId=\"0\" priority=\"1\" text=\"z\"/>", "00010")]
    [InlineData("<cfRule type=\"containsErrors\" dxfId=\"0\" priority=\"1\"/>", "00001")]
    [InlineData("<cfRule type=\"notContainsErrors\" dxfId=\"0\" priority=\"1\"/>", "11110")]
    public void Ranking_average_duplicate_text_and_error_rules(string rule, string expected)
    {
        var cells = Column(Rule("A1:A5", rule), "ab", "Ab", "x3", "4z", "#N/A");
        if (rule.Contains("top10", StringComparison.Ordinal) || rule.Contains("Average", StringComparison.Ordinal))
        {
            cells = Column(Rule("A1:A5", rule), "1", "2", "3", "4", "5");
        }

        Assert.Equal(expected, string.Concat(cells.Select(c => Bold(c) ? '1' : '0')));
    }

    [Fact]
    public void Color_scales_fill_numbers_between_their_colors()
    {
        var cells = Column(Rule("A1:A5", "<cfRule type=\"colorScale\" priority=\"1\"><colorScale><cfvo type=\"min\"/><cfvo type=\"percentile\" val=\"50\"/><cfvo type=\"max\"/>"
            + "<color rgb=\"FFFF0000\"/><color rgb=\"FF000000\"/><color rgb=\"FF0000FF\"/></colorScale></cfRule>"), "0", "5", "10", "15", "20");
        Assert.Equal([PdfColor.FromRgb(255, 0, 0), new PdfColor(0.5, 0, 0), PdfColor.Black, new PdfColor(0, 0, 0.5), PdfColor.FromRgb(0, 0, 255)], cells.Select(c => c.Format!.Fill!.Value));
        var two = Column(Rule("A1:A3", "<cfRule type=\"colorScale\" priority=\"1\"><colorScale><cfvo type=\"num\" val=\"0\"/><cfvo type=\"percent\" val=\"100\"/>"
            + "<color rgb=\"FFFFFFFF\"/><color rgb=\"FF000000\"/></colorScale></cfRule>"), "-5", "2", "text");
        Assert.Equal(PdfColor.White, two[0].Format!.Fill);
        Assert.Equal(PdfColor.Black, two[1].Format!.Fill);
        Assert.Null(two[2].Format?.Fill);
        Assert.Equal(2.5, ConditionalFormatting.Percentile([1, 2, 3, 4], 0.5));
    }

    [Fact]
    public void Rules_that_are_not_applied_are_counted()
    {
        var diagnostics = new List<SheetDiagnostic>();
        var xlsx = XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>", $"<cellXfs count=\"1\"><xf/></cellXfs>{CfStyles}",
            sheetExtra: Rule("A1", "<cfRule type=\"expression\" dxfId=\"0\" priority=\"1\"><formula>A1&gt;0</formula></cfRule><cfRule type=\"dataBar\" priority=\"2\"/>"
                + "<cfRule type=\"iconSet\" priority=\"3\"/><cfRule type=\"timePeriod\" priority=\"4\"/><cfRule type=\"cellIs\" dxfId=\"0\" priority=\"5\" operator=\"equal\"><formula>1</formula></cfRule>"));
        XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { Sheet = new SheetOptions { Diagnostics = diagnostics.Add } });
        Assert.Equal("4 conditional formatting rules are not applied.", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void Constants_and_comparisons_of_rules()
    {
        Assert.Equal((2.5, "2.5"), ConditionalFormatting.Constant(" 2.5 ")!.Value);
        Assert.Equal((null, "a\"b"), ConditionalFormatting.Constant("\"a\"\"b\"")!.Value);
        Assert.Null(ConditionalFormatting.Constant("A1"));
        Assert.True(ConditionalFormatting.Compare("notBetween", 5, "5", (6, "6"), (9, "9")));
        Assert.False(ConditionalFormatting.Compare("lessThanOrEqual", 5, "5", (4, "4"), (4, "4")));
        Assert.True(ConditionalFormatting.Compare("notEqual", null, "x", (4, "4"), (4, "4"))); // Text and a number are not equal.
        Assert.False(ConditionalFormatting.Compare("unknown", 1, "1", (1, "1"), (1, "1")));
    }
}
