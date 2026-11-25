using System.Globalization;
using System.Text.Json;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;
using static RaiCore.Pdf.Tests.Sheets.BiffBuilder;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// Cell formatting of worksheets beyond <see cref="SheetPrintTests"/>: rich text and conditional formatting of .xls
/// workbooks, comments, rotated text, data bars and icon sets, formula rules and pivot table styles.
/// </summary>
public class SheetFormattingTests
{
    private static readonly Lazy<JsonElement> XlsExpected = new(() => JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Sheets/Xls/expected.json"))).RootElement);

    /// <summary>Reads one sheet of an .xls workbook as the converter does (before it is laid out).</summary>
    internal static XlsxWorksheet ReadXls(byte[] data, int index = 0)
    {
        var limits = ResourceLimits.Default;
        var workbook = XlsConverter.Open(data, limits, CultureInfo.InvariantCulture, null).Workbook;
        return XlsWorksheet.Read(workbook, workbook.SheetOffsets[index], limits.MaxTableCells, null, limits);
    }

    // ---- Rich text of .xls workbooks ----

    public static TheoryData<string> XlsRichCorpus => [.. XlsExpected.Value.EnumerateObject()
        .Where(p => p.Value.TryGetProperty("sheets", out var sheets) && sheets.EnumerateArray().Any(s => s.GetProperty("runs").GetArrayLength() > 0)).Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(XlsRichCorpus))]
    public void Xls_rich_text_runs_match_xlrd(string name)
    {
        var data = Fixtures.Read(Path.Combine("Sheets", "Xls", name));
        var sheets = XlsExpected.Value.GetProperty(name).GetProperty("sheets").EnumerateArray().ToList();
        for (var index = 0; index < sheets.Count; index++)
        {
            var read = ReadXls(data, index);
            foreach (var expected in sheets[index].GetProperty("runs").EnumerateArray())
            {
                var cell = read.Cells[XlsxWorksheet.Key(expected[0].GetInt32(), expected[1].GetInt32())];
                Assert.NotNull(cell.Runs);
                Assert.Equal(cell.Text, string.Concat(cell.Runs.Select(r => r.Text)));
                var starts = cell.Runs.Select((r, i) => cell.Runs.Take(i).Sum(p => p.Text.Length)).ToList();
                foreach (var run in expected[2].EnumerateArray())
                {
                    var first = run[0].GetInt32();
                    if (first >= cell.Text.Length)
                    {
                        continue;
                    }

                    var font = cell.Runs[starts.LastIndexOf(starts.Last(s => s <= first))].Font;
                    Assert.NotNull(font);
                    Assert.Equal((run[1].GetInt32() == 1, run[2].GetInt32() == 1, run[3].GetDouble()), (font.Bold, font.Italic, font.Size));
                    var color = run[4].GetInt32();
                    Assert.Equal(color switch
                    {
                        0x7FFF => null,
                        2 => PdfColor.FromRgb(255, 0, 0),
                        4 => PdfColor.FromRgb(0, 0, 255),
                        _ => font.Color,
                    }, font.Color);
                }
            }
        }
    }

    [Fact]
    public void Xls_rich_text_is_shown_in_its_fonts_and_no_longer_reported()
    {
        var data = Fixtures.Read("Sheets/Xls/print-formatting.xls");
        var cell = ReadXls(data).Cells[XlsxWorksheet.Key(0, 0)];
        Assert.Equal(["Plain ", "bold red", " and ", "italic big"], cell.Runs!.Select(r => r.Text));
        Assert.Null(cell.Runs![0].Font);
        Assert.Equal(16, cell.Runs![3].Font!.Size);

        var diagnostics = new List<SheetDiagnostic>();
        var pdf = XlsToPdf.ConvertToBytes(data, new XlsxConverterOptions { Sheet = new SheetOptions { Diagnostics = diagnostics.Add } });
        Assert.DoesNotContain(diagnostics, d => d.Kind == SheetDiagnosticKind.RichText);
        var text = new RaiCore.Pdf.Extraction.TextExtractor(PdfReader.Open(pdf)).ExtractPages().First().Text;
        Assert.Contains("Plain bold red and italic big", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Xls_rstring_records_carry_their_runs()
    {
        // An RSTRING (BIFF8): the string, then the count of runs and each run's first character and font.
        var xls = SingleSheet(
            s => s.Record(0x00D6, Cell(0, 0), Text("Head tail"), U16(2), U16(0), U16(5), U16(4), U16(0)).Record(0x00D6, Cell(1, 0), Text("Cut"), U16(9)),
            g => g.Font("Arial").Font("Arial").Font("Arial").Font("Arial").Font("Courier New", 14, bold: true));
        var read = ReadXls(xls);
        var runs = read.Cells[XlsxWorksheet.Key(0, 0)].Runs!;
        Assert.Equal(["Head", " tail"], runs.Select(r => r.Text));
        Assert.Equal(("Courier New", 14.0, true), (runs[0].Font!.Name, runs[0].Font!.Size!.Value, runs[0].Font!.Bold));
        Assert.Equal("Arial", runs[1].Font!.Name);

        // Runs cut short by the end of the record are read as far as they go.
        Assert.Equal("Cut", read.Cells[XlsxWorksheet.Key(1, 0)].Text);
    }

    // ---- Conditional formatting of .xls workbooks ----

    public static TheoryData<string> XlsConditionalCorpus => [.. XlsExpected.Value.EnumerateObject()
        .Where(p => p.Value.TryGetProperty("conditional", out var c) && c.EnumerateObject().Any()).Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(XlsConditionalCorpus))]
    public void Xls_conditional_formatting_rules_match_the_independent_walk(string name)
    {
        var data = Fixtures.Read(Path.Combine("Sheets", "Xls", name));
        var sheets = XlsExpected.Value.GetProperty(name).GetProperty("sheets").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        string[] operators = ["", "between", "notBetween", "equal", "notEqual", "greaterThan", "lessThan", "greaterThanOrEqual", "lessThanOrEqual"];
        foreach (var sheet in XlsExpected.Value.GetProperty(name).GetProperty("conditional").EnumerateObject())
        {
            var rules = ReadXls(data, sheets.IndexOf(sheet.Name)).ConditionalRules;
            var expected = sheet.Value.EnumerateArray().ToList();
            Assert.Equal(expected.Count, rules.Count);
            for (var i = 0; i < rules.Count; i++)
            {
                var (record, type, comparison) = (expected[i][0].GetString(), expected[i][1].GetInt32(), expected[i][2].GetInt32());
                var template = expected[i][3].ValueKind == JsonValueKind.Null ? 0 : expected[i][3].GetInt32();
                Assert.Equal((type, template) switch
                {
                    (1, _) => "cellIs",
                    (2, 0x07) => "uniqueValues",
                    (2, 0x1B) => "duplicateValues",
                    (2, 0x19 or 0x1A or 0x1D or 0x1E) => "aboveAverage",
                    (2, >= 0x0F and <= 0x18) => "timePeriod",
                    (2, _) => "expression",
                    (3, _) => "colorScale",
                    (4, _) => "dataBar",
                    (5, _) => "top10",
                    _ => "iconSet",
                }, rules[i].Type);
                if (type == 1)
                {
                    Assert.Equal(operators[comparison], rules[i].Operator);
                }

                if (expected[i][4].ValueKind != JsonValueKind.Null)
                {
                    Assert.Equal(expected[i][4].GetInt32(), rules[i].Priority);
                }

                Assert.Equal(expected[i][5].EnumerateArray().Select(r => new CellRange(r[0].GetInt32(), r[2].GetInt32(), r[1].GetInt32(), r[3].GetInt32())), rules[i].Ranges);
                Assert.True(record == "CF" || rules[i].Type != "cellIs" || rules[i].Formulas.Count > 0);
            }
        }
    }

    /// <summary>The cells of the first sheet of a workbook as converted (with conditional formatting applied), by (row, column).</summary>
    private static Dictionary<(int, int), SheetCell> Cells(Sheet sheet)
    {
        var cells = new Dictionary<(int, int), SheetCell>();
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            for (var c = 0; c < sheet.Rows[r].Length; c++)
            {
                cells[(r, c)] = sheet.Rows[r][c];
            }
        }

        return cells;
    }

    [Fact]
    public void Xls_and_xlsx_of_the_same_workbook_get_the_same_conditional_formats()
    {
        // Apache POI's NewStyleConditionalFormattings in both formats: cell rules, a data bar, color scales, icon sets and a formula rule.
        var options = new XlsxConverterOptions { PrintDate = new DateTime(2026, 3, 4) };
        var xls = Cells(XlsConverter.Read(Fixtures.Read("Sheets/Xls/NewStyleConditionalFormattings.xls"), options)[0]);
        var xlsx = Cells(XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/NewStyleConditionalFormattings.xlsx"), options)[0]);
        var compared = 0;
        foreach (var ((row, column), cell) in xlsx)
        {
            if (row == 0 || column is 16 or 20)
            {
                continue; // The header row; columns Q and U have icon sets .xls workbooks cannot hold (stars, custom icons).
            }

            var other = xls.TryGetValue((row, column), out var c) ? c.Format ?? CellFormat.Default : CellFormat.Default;
            var format = cell.Format ?? CellFormat.Default;
            // .xls rules have the exact colors Excel 2007 keeps after their palette colors. The data bar of the .xlsx
            // workbook is Excel 2010's, from 0 (autoMin), the .xls one's from the least value.
            Assert.Equal((row, column, format.Bold, format.Fill, format.Color, format.Icon, format.DataBar is null), (row, column, other.Bold, other.Fill, other.Color, other.Icon, other.DataBar is null));
            compared += format.Icon is not null || format.DataBar is not null || format.Fill is not null ? 1 : 0;
        }

        Assert.True(compared > 200, $"{compared} cells compared");

        // The .xls rule's solid fill is its background color: Excel's "Green Fill with Dark Green Text" from the extension, not
        // the palette's nearest color (CCFFCC, 008000).
        Assert.Equal((PdfColor.FromRgb(0xC6, 0xEF, 0xCE), PdfColor.FromRgb(0x00, 0x61, 0x00)), (xls[(1, 2)].Format!.Fill, xls[(1, 2)].Format!.Color));
    }

    [Fact]
    public void Xls_conditional_formats_take_their_theme_colors_from_the_extension()
    {
        // Apache POI's WithConditionalFormatting: Excel 2007 kept RGB colors and a theme color with a tint after the rules'
        // formats, in place of the nearest palette colors (008000 for the green).
        var cells = Cells(XlsConverter.Read(Fixtures.Read("Sheets/Xls/WithConditionalFormatting.xls"), new XlsxConverterOptions())[0]);
        var green = PdfColor.FromRgb(0x00, 0xB0, 0x50);
        Assert.Equal((null, green), (cells[(2, 0)].Format!.Fill, cells[(2, 0)].Format!.Color));
        Assert.Equal(PdfColor.FromRgb(0xFF, 0x00, 0x00), cells[(3, 0)].Format!.Color);
        Assert.Equal(green, cells[(7, 0)].Format!.Color);

        // Accent 1 lighter by 80% (theme color 4, tint 0.8), with red text.
        Assert.Equal((PdfColor.FromRgb(0xDA, 0xE3, 0xF3), PdfColor.FromRgb(0xFF, 0x00, 0x00)), (cells[(8, 1)].Format!.Fill, cells[(8, 1)].Format!.Color));
    }

    // ---- Comments ----

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string[] Pages(byte[] pdf) => [.. new RaiCore.Pdf.Extraction.TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    /// <summary>A workbook whose A1 and C2 have comments (C2's with a phonetic run and an author), with more after its cells.</summary>
    private static byte[] Commented(string sheetExtra = "") => SheetDiagnosticsTests.Workbook(sheetExtra,
        SheetDiagnosticsTests.Relationship("rId1", "comments", "../comments1.xml"),
        "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"2\"><c r=\"C2\"><v>2</v></c></row>",
        parts: ("xl/comments1.xml", $"<comments xmlns=\"{Main}\"><authors><author>Ann</author></authors><commentList>"
            + "<comment ref=\"C2\" authorId=\"0\"><text><r><rPr><b/></rPr><t>Ann:</t></r><r><t xml:space=\"preserve\">\nCheck C2</t></r><rPh sb=\"0\" eb=\"1\"><t>x</t></rPh></text></comment>"
            + "<comment ref=\"A1\" authorId=\"9\"><text><t>First</t></text></comment><comment ref=\"ZZZZ1\" authorId=\"0\"><text><t>Bad</t></text></comment>"
            + "<comment ref=\"B1\" authorId=\"0\"><text><t> </t></text></comment></commentList></comments>"));

    [Fact]
    public void Xlsx_comments_are_read_with_their_text_and_author_like_openpyxl()
    {
        // openpyxl reads SimpleWithComments.xlsx: B1 to B3, "Yegor Kozlov:\nfirst cell" and on, by Yegor Kozlov.
        var sheet = XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/SimpleWithComments.xlsx"), new XlsxConverterOptions { Comments = SheetComments.AtEnd })[0];
        Assert.Equal(CommentPrinting.AtEnd, sheet.Formatting!.Comments.Printing);
        Assert.Equal(
            [("B1", "Yegor Kozlov:\nfirst cell", "Yegor Kozlov"), ("B2", "Yegor Kozlov:\nsecond cell", "Yegor Kozlov"), ("B3", "Yegor Kozlov:\nthird cell", "Yegor Kozlov")],
            sheet.Formatting.Comments.List.Select(c => (c.Reference, c.Text, c.Author)));

        // Ordered by cell; without a known author; phonetic runs, blank comments and bad references left out.
        var made = XlsxConverter.Read(Commented(), new XlsxConverterOptions { Comments = SheetComments.Annotations })[0].Formatting!.Comments;
        Assert.Equal(CommentPrinting.Notes, made.Printing);
        Assert.Equal([("A1", 0, 0, "First", (string?)null), ("C2", 1, 2, "Ann:\nCheck C2", "Ann")], made.List.Select(c => (c.Reference, c.Row, c.Column, c.Text, c.Author)));
    }

    public static TheoryData<string> XlsNotesCorpus => [.. XlsExpected.Value.EnumerateObject()
        .Where(p => p.Value.TryGetProperty("sheets", out var sheets) && sheets.EnumerateArray().Any(s => s.GetProperty("notes").GetArrayLength() > 0)).Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(XlsNotesCorpus))]
    public void Xls_comments_match_xlrd(string name)
    {
        var data = Fixtures.Read(Path.Combine("Sheets", "Xls", name));
        var sheets = XlsExpected.Value.GetProperty(name).GetProperty("sheets").EnumerateArray().ToList();
        for (var index = 0; index < sheets.Count; index++)
        {
            var expected = sheets[index].GetProperty("notes").EnumerateArray()
                .Select(n => (n[0].GetInt32(), n[1].GetInt32(), n[3].GetString()!.TrimEnd(), (string?)n[2].GetString())).ToList();
            Assert.Equal(expected, ReadXls(data, index).Comments.OrderBy(c => c.Row).ThenBy(c => c.Column));
        }
    }

    [Fact]
    public void Comments_print_at_the_end_when_the_page_setup_or_the_options_say_so()
    {
        // By default (the page setup's choice): not printed, unless it says "at end".
        Assert.Single(Pages(XlsxToPdf.ConvertToBytes(Commented())));
        var atEnd = Pages(XlsxToPdf.ConvertToBytes(Commented("<pageSetup cellComments=\"atEnd\"/>")));
        Assert.Equal(2, atEnd.Length);
        Assert.Equal("Cell: A1\nComment: First\nCell: C2\nComment: Ann:\nCheck C2", string.Join('\n', atEnd[1].Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)).Replace(":\n", ": ", StringComparison.Ordinal).Replace("Ann: Check", "Ann:\nCheck", StringComparison.Ordinal).Trim());
        Assert.Single(Pages(XlsxToPdf.ConvertToBytes(Commented("<pageSetup cellComments=\"atEnd\"/>"), new XlsxConverterOptions { Comments = SheetComments.None })));
        Assert.Single(Pages(XlsxToPdf.ConvertToBytes(Commented("<pageSetup cellComments=\"atEnd\"/>"), new XlsxConverterOptions { UsePageSetup = false })));
        Assert.Equal(2, Pages(XlsxToPdf.ConvertToBytes(Commented("<pageSetup cellComments=\"asDisplayed\"/>"), new XlsxConverterOptions { Comments = SheetComments.AtEnd })).Length);

        // .xls: the SETUP record's flags print the notes (0x20) at the end (0x200).
        var xls = Fixtures.Read("Sheets/Xls/SimpleWithComments.xls");
        Assert.Contains("Cell: B2", Pages(XlsToPdf.ConvertToBytes(xls, new XlsxConverterOptions { Comments = SheetComments.AtEnd }))[1].Replace(":\n", ": ", StringComparison.Ordinal), StringComparison.Ordinal);
        byte[] Setup(int flags) => SingleSheet(s => s.Record(0x0203, Cell(0, 0), F64(1)).Record(0x00A1, U16(9), U16(100), U16(1), U16(1), U16(1), U16(flags), U16(300), U16(300), F64(0.5), F64(0.5), U16(1))
            .Record(0x005D, U16(0x15), U16(0x12), U16(0x19), U16(7), U16(0), new byte[12])
            .Record(0x01B6, U16(0x12), new byte[8], U16(4), U16(16), U32(0)).Record(0x003C, U8(0), System.Text.Encoding.ASCII.GetBytes("Note"))
            .Record(0x003C, U16(0), U16(0), U32(0), U16(4), U16(0), U32(0))
            .Record(0x005D, U16(0x15), U16(0x12), U16(0x06), U16(7), U16(0), new byte[12])
            .Record(0x01B6, U16(0x12), new byte[8], U16(3), U16(16), U32(0)).Record(0x003C, U8(0), System.Text.Encoding.ASCII.GetBytes("Box"))
            .Record(0x003C, U16(0), U16(0), U32(0), U16(3), U16(0), U32(0))
            .Record(0x001C, Cell(0, 0), U16(7), Text("Bo"), U8(0)));
        Assert.Equal([(0, 0, "Note", (string?)"Bo")], ReadXls(Setup(0)).Comments);
        Assert.Single(Pages(XlsToPdf.ConvertToBytes(Setup(0x20))));
        Assert.Single(Pages(XlsToPdf.ConvertToBytes(Setup(0x200))));
        Assert.Single(Pages(XlsToPdf.ConvertToBytes(Setup(0x220), new XlsxConverterOptions { UsePageSetup = false })));
        Assert.Equal("Cell: A1\nComment: Note", Pages(XlsToPdf.ConvertToBytes(Setup(0x220)))[1]);
    }

    [Fact]
    public void Comments_as_annotations_are_notes_at_their_cells()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Commented(), new XlsxConverterOptions { Comments = SheetComments.Annotations });
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "/Subtype /Text").Count);
        Assert.Contains("/T (Ann)", text, StringComparison.Ordinal);
        Assert.Single(Pages(pdf));

        // In a tagged document each is an Annot element.
        var tagged = XlsxToPdf.ConvertToBytes(Commented(), new XlsxConverterOptions { Comments = SheetComments.Annotations, Sheet = new SheetOptions { Tagged = true } });
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(System.Text.Encoding.Latin1.GetString(tagged), "/S /Annot").Count);
        ExternalTools.AssertQpdfClean(tagged);
    }

    // ---- Formulas of rules ----

    /// <summary>Computes a formula written for A1 at a cell, over a few cells: A1 = 4, A2 = "Text", B1 = -2.5, C3 = #N/A, B2 = TRUE.</summary>
    private static FormulaValue? Compute(string formula, int row = 0, int column = 0, long budget = 10_000)
    {
        FormulaValue Cells(int r, int c) => (r, c) switch
        {
            (0, 0) => FormulaValue.Of(4),
            (1, 0) => FormulaValue.Of("Text"),
            (0, 1) => FormulaValue.Of(-2.5),
            (2, 2) => FormulaValue.Error("#N/A"),
            (1, 1) => FormulaValue.Of(true),
            _ => FormulaValue.Blank,
        };
        return ConditionalFormula.Parse(formula, 0, 0)?.Evaluate(Cells, row, column, 46000, ref budget);
    }

    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("=(1+2)*3", 9)]
    [InlineData("-2^2", 4)]
    [InlineData("2^3^2", 64)]
    [InlineData("50%+A1", 4.5)]
    [InlineData("A1*B1", -10)]
    [InlineData("$A$1+A1", 8)]
    [InlineData("MOD(-7,3)", 2)]
    [InlineData("ROW()+COLUMN()*10", 11)]
    [InlineData("ROW(C5)", 5)]
    [InlineData("SUM(A1:B2,1)", 2.5)]
    [InlineData("AVERAGE(A1:B1)", 0.75)]
    [InlineData("COUNT(A1:C3)", 2)]
    [InlineData("COUNTA(A1:C3)", 5)]
    [InlineData("COUNTBLANK(A1:C3)", 4)]
    [InlineData("COUNTIF(A1:B1,\">0\")", 1)]
    [InlineData("COUNTIF(A1:A2,\"text\")", 1)]
    [InlineData("MAX(A1:B1)-MIN(A1:B1)", 6.5)]
    [InlineData("ROUND(2.345,2)", 2.35)]
    [InlineData("INT(-1.5)+ABS(-3)", 1)]
    [InlineData("LEN(A2)", 4)]
    [InlineData("SEARCH(\"x\",A2)", 3)]
    [InlineData("IF(A1>3,1,2)", 1)]
    [InlineData("TODAY()", 46000)]
    [InlineData("\"3\"+1", 4)]
    [InlineData("1E2", 100)]
    public void Formulas_compute_numbers(string formula, double expected) => Assert.Equal(FormulaValue.Of(expected), Compute(formula));

    [Theory]
    [InlineData("A1>3", true)]
    [InlineData("A2=\"TEXT\"", true)]
    [InlineData("A2>A1", true)] // Texts after numbers,
    [InlineData("A2<B2", true)] // and before booleans.
    [InlineData("D9=0", true)] // An empty cell is 0,
    [InlineData("D9=\"\"", true)] // or an empty text.
    [InlineData("AND(A1>3,B1<0,TRUE)", true)]
    [InlineData("OR(A1<0,FALSE())", false)]
    [InlineData("NOT(ISBLANK(A1))", true)]
    [InlineData("ISBLANK(D9)", true)]
    [InlineData("ISNUMBER(A2)", false)]
    [InlineData("ISTEXT(A2)", true)]
    [InlineData("ISERROR(C3)", true)]
    [InlineData("ISNA(1/0)", false)]
    [InlineData("ISEVEN(A1)", true)]
    [InlineData("ISODD(3.7)", true)]
    [InlineData("AND(B2,A1:B1)", true)]
    [InlineData("A1&B1=\"4-2.5\"", true)]
    [InlineData("UPPER(LEFT(A2,2))&LOWER(RIGHT(A2))=\"TEt\"", true)]
    [InlineData("TRIM(\"  a  b \")=\"a b\"", true)]
    [InlineData("ISERROR(FIND(\"t\",\"T\"))", true)]
    [InlineData("UPPER()&LEFT(,1)=\"\"", true)] // Missing arguments are empty texts.
    public void Formulas_compute_conditions(string formula, bool expected) => Assert.Equal(FormulaValue.Of(expected), Compute(formula));

    [Theory]
    [InlineData("1/0", "#DIV/0!")]
    [InlineData("MOD(1,0)", "#DIV/0!")]
    [InlineData("A2*2", "#VALUE!")]
    [InlineData("C3+1", "#N/A")]
    [InlineData("SEARCH(\"q\",A2)", "#VALUE!")]
    [InlineData("SEARCH(\"q\")", "#VALUE!")] // Not in an empty text (fuzzing found it failing).
    [InlineData("#REF!", "#REF!")]
    [InlineData("$A$1:$B$2+1", "#VALUE!")]
    public void Formulas_compute_errors(string formula, string error) => Assert.Equal(FormulaValue.Error(error), Compute(formula));

    [Theory]
    [InlineData("Sheet2!A1>0")]
    [InlineData("INDIRECT(\"A1\")")]
    [InlineData("MyName>0")]
    [InlineData("{1,2}")]
    [InlineData("A:A")]
    [InlineData("(1+2")]
    [InlineData("1+")]
    [InlineData("\"open")]
    [InlineData("1 2")]
    [InlineData("SUM(1,2")]
    [InlineData("#BAD!")]
    [InlineData("")]
    [InlineData("XFE1")]
    public void Formulas_that_are_not_supported_are_not_parsed(string formula) => Assert.Null(ConditionalFormula.Parse(formula, 0, 0));

    [Fact]
    public void Formulas_are_bounded()
    {
        Assert.Null(ConditionalFormula.Parse(new string('(', 70) + "1" + new string(')', 70), 0, 0)); // Too deep.
        Assert.Null(ConditionalFormula.Parse(new string('-', 70) + "1", 0, 0));
        Assert.Null(ConditionalFormula.Parse(string.Join("+", Enumerable.Repeat("1", 3000)), 0, 0)); // Too many nodes.
        Assert.Null(ConditionalFormula.Parse("\"" + new string('a', ConditionalFormula.MaxLength) + "\"", 0, 0)); // Too long.
        Assert.NotNull(ConditionalFormula.Parse(string.Join("+", Enumerable.Repeat("1", 1000)), 0, 0));

        // The work: each node, and each cell of a range.
        Assert.Null(Compute("SUM(A1:B1000)", budget: 1000));
        Assert.Equal(FormulaValue.Of(1.5), Compute("SUM(A1:B1000)", budget: 3000));
        Assert.Equal(FormulaValue.Error("#N/A"), Compute("SUM(A1:C3)")); // An error in a range is the sum's.

        // References move with the cell computed, and those off the sheet are errors.
        Assert.Equal(FormulaValue.Of(-2.5), Compute("A1", 0, 1));
        Assert.Equal(FormulaValue.Error("#REF!"), Compute("B2", 0, XlsxWorkbook.MaxColumns - 1));
    }

    [Fact]
    public void Formula_and_cell_reference_rules_apply_to_empty_cells_too()
    {
        // Every other row of A1:B4 (empty cells too), and cells above B1's value.
        var xlsx = XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\"><v>5</v></c><c r=\"B1\"><v>2</v></c></row><row r=\"4\"><c r=\"B4\"><v>1</v></c></row>",
            $"<cellXfs count=\"1\"><xf/></cellXfs><dxfs count=\"2\"><dxf><fill><patternFill><bgColor rgb=\"FFFFFF00\"/></patternFill></fill></dxf><dxf><font><b/></font></dxf></dxfs>",
            sheetExtra: "<conditionalFormatting sqref=\"A1:B4\"><cfRule type=\"expression\" dxfId=\"0\" priority=\"2\"><formula>MOD(ROW(),2)=0</formula></cfRule></conditionalFormatting>"
                + "<conditionalFormatting sqref=\"A1:A4\"><cfRule type=\"cellIs\" dxfId=\"1\" priority=\"1\" operator=\"greaterThan\"><formula>$B$1</formula></cfRule></conditionalFormatting>"
                + "<conditionalFormatting sqref=\"B3\"><cfRule type=\"cellIs\" dxfId=\"0\" priority=\"3\" operator=\"lessThan\"><formula>3</formula></cfRule></conditionalFormatting>");
        var rows = XlsxConverter.Read(xlsx)[0].Rows;
        Assert.Equal([null, PdfColor.FromRgb(255, 255, 0), null, PdfColor.FromRgb(255, 255, 0)], rows.Select(r => r[0].Format?.Fill));
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), rows[3][1].Format?.Fill);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), rows[2][1].Format?.Fill); // Empty: 0, less than 3.
        Assert.Equal([true, false, false, false], rows.Select(r => r[0].Format?.Bold ?? false));
    }

    [Theory]
    [InlineData("today", "0000100")]
    [InlineData("yesterday", "0001000")]
    [InlineData("tomorrow", "0000010")]
    [InlineData("last7Days", "0111100")]
    [InlineData("thisWeek", "0111111")]
    [InlineData("lastWeek", "1000000")]
    [InlineData("thisMonth", "0111111")]
    [InlineData("lastMonth", "1000000")]
    [InlineData("nextMonth", "0000000")]
    public void Time_period_rules_compare_dates_with_the_print_date(string period, string expected)
    {
        // Wednesday 4 March 2026; the cells are 25 February and 1 to 6 March.
        double[] days = [46078, 46082, 46083, 46084, 46085, 46086, 46087];
        var data = string.Concat(days.Select((d, i) => $"<row r=\"{i + 1}\"><c r=\"A{i + 1}\"><v>{d}</v></c></row>"));
        var xlsx = XlsxToPdfTests.Workbook(data, "<cellXfs count=\"1\"><xf/></cellXfs><dxfs count=\"1\"><dxf><font><b/></font></dxf></dxfs>",
            sheetExtra: $"<conditionalFormatting sqref=\"A1:A7\"><cfRule type=\"timePeriod\" dxfId=\"0\" priority=\"1\" timePeriod=\"{period}\"/></conditionalFormatting>");
        var cells = XlsxConverter.Read(xlsx, new XlsxConverterOptions { PrintDate = new DateTime(2026, 3, 4, 15, 0, 0) })[0].Rows.Select(r => r[0]);
        Assert.Equal(expected, string.Concat(cells.Select(c => c.Format?.Bold ?? false ? '1' : '0')));
    }

    [Fact]
    public void Biff_formulas_are_written_as_text()
    {
        // MOD(ROW($T2),2)=1 as Excel writes it for a rule of T2:T17: PtgRefN (row relative, column absolute), PtgFuncVar, PtgInt, PtgFunc, PtgEq.
        byte[] tokens = [0x2C, 0, 0, 0x13, 0x80, 0x42, 1, 8, 0, 0x1E, 2, 0, 0x41, 0x27, 0, 0x1E, 1, 0, 0x0B];
        Assert.Equal("MOD(ROW($T2),2)=1", BiffFormula.ToText(tokens, 1, 19));
        Assert.Equal("MOD(ROW($T6),2)=1", BiffFormula.ToText(tokens, 5, 19));

        // A string, an area and a reference with both parts relative, a boolean, a number, parentheses and a unary minus.
        byte[] more = [0x17, 2, 0, (byte)'a', (byte)'"', 0x25, 0, 0, 4, 0, 1, 0xC0, 2, 0xC0, 0x42, 1, 4, 0, 0x08, 0x1D, 1, 0x08,
            0x1F, 0, 0, 0, 0, 0, 0, 0xF8, 0x3F, 0x13, 0x15, 0x08, 0x24, 3, 0, 1, 0x80, 0x19, 0x40, 0, 0, 0x08];
        Assert.Equal("\"a\"\"\"&SUM(B1:C5)&TRUE&(-1.5)&$B4", BiffFormula.ToText(more, 0, 0));

        Assert.Null(BiffFormula.ToText([0x23, 1, 0, 0, 0], 0, 0)); // A name.
        Assert.Null(BiffFormula.ToText([0x1E, 1], 0, 0)); // Cut short.
        Assert.Null(BiffFormula.ToText([0x1E, 1, 0, 0x1E, 2, 0], 0, 0)); // Two values left.
        Assert.Null(BiffFormula.ToText([0x03], 0, 0)); // An operator without operands.
        Assert.Null(BiffFormula.ToText([0x42, 1, 0xFF, 0x00], 0, 0)); // A function that is not supported.
        var long1 = Enumerable.Repeat((byte)0x13, 9000).Prepend((byte)0).Prepend((byte)1).Prepend((byte)0x1E).ToArray();
        Assert.Null(BiffFormula.ToText(long1, 0, 0)); // Longer than a formula may be.
    }

    // ---- Rotated text ----

    [Fact]
    public void Rotated_text_is_read_in_degrees_and_drawn_turned()
    {
        const string styles = "<cellXfs count=\"5\"><xf/><xf><alignment textRotation=\"45\"/></xf><xf><alignment textRotation=\"135\"/></xf>"
            + "<xf><alignment textRotation=\"180\"/></xf><xf><alignment textRotation=\"255\"/></xf></cellXfs>";
        var xlsx = XlsxToPdfTests.Workbook(string.Concat(Enumerable.Range(0, 5).Select(i => $"<c r=\"{(char)('A' + i)}1\" s=\"{i}\" t=\"inlineStr\"><is><t>Turn{i}</t></is></c>"))
            .Insert(0, "<row r=\"1\">") + "</row>", styles);
        var row = XlsxConverter.Read(xlsx)[0].Rows[0];
        Assert.Equal([0, 45, -45, -90, 0], row.Select(c => c.Format?.Rotation ?? 0));
        Assert.True(row[4].Format!.Stacked);
        var page = Assert.Single(Pages(XlsxToPdf.ConvertToBytes(xlsx)));
        Assert.All(Enumerable.Range(1, 3), i => Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, $"Turn{i}")));

        // .xls: BIFF8 rotations (1 to 90 up, 91 to 180 down) and BIFF5 orientations.
        var xls = Fixtures.Read("Sheets/Xls/print-formatting.xls");
        var cells = XlsConverter.Read(xls)[1].Rows[0];
        Assert.Equal([0, 45, 90, -45, -90, 0], cells.Select(c => c.Format?.Rotation ?? 0));
        var text = Pages(XlsToPdf.ConvertToBytes(xls))[1];
        Assert.All(["Turned 45", "Turned 90", "Turned -45", "Turned -90"], t => Assert.Contains(t, text, StringComparison.Ordinal));
    }

    [Fact]
    public void Rows_grow_to_fit_rotated_text_unless_their_height_is_set()
    {
        // 30 rows of a long text: on one page, unless turned upright (each row then as high as the text is long).
        static int PageCount(string style, string height) => Pages(XlsxToPdf.ConvertToBytes(XlsxToPdfTests.Workbook(
            string.Concat(Enumerable.Range(1, 30).Select(r => $"<row r=\"{r}\"{height}><c r=\"A{r}\"{style} t=\"inlineStr\"><is><t>A long text turned upright</t></is></c></row>")),
            "<cellXfs count=\"2\"><xf/><xf><alignment textRotation=\"90\"/></xf></cellXfs>"))).Length;
        Assert.Equal(1, PageCount(string.Empty, string.Empty));
        Assert.True(PageCount(" s=\"1\"", string.Empty) >= 4);
        Assert.Equal(1, PageCount(" s=\"1\"", " ht=\"15\" customHeight=\"1\""));
    }

    // ---- Data bars and icon sets ----

    private static SheetCell[] Cf(string rule, params double[] values)
    {
        var data = string.Concat(values.Select((v, i) => $"<row r=\"{i + 1}\"><c r=\"A{i + 1}\"><v>{v.ToString(CultureInfo.InvariantCulture)}</v></c></row>"));
        var xlsx = XlsxToPdfTests.Workbook(data, "<cellXfs count=\"1\"><xf/></cellXfs>", sheetExtra: rule);
        return [.. XlsxConverter.Read(xlsx)[0].Rows.Select(r => r[0])];
    }

    [Fact]
    public void Data_bars_are_as_long_as_their_values_place()
    {
        var cells = Cf($"<conditionalFormatting sqref=\"A1:A4\"><cfRule type=\"dataBar\" priority=\"1\"><dataBar showValue=\"0\"><cfvo type=\"min\"/><cfvo type=\"max\"/>"
            + "<color rgb=\"FF638EC6\"/></dataBar></cfRule></conditionalFormatting>", 0, 5, 10, 2.5);
        Assert.Equal([0.1, 0.5, 0.9, 0.3], cells.Select(c => Math.Round(c.Format!.DataBar!.End, 9)));
        Assert.All(cells, c => Assert.Equal((0.0, PdfColor.FromRgb(0x63, 0x8E, 0xC6), true), (c.Format!.DataBar!.Start, c.Format.DataBar.Color, c.Format.HideText)));

        // Excel 2010's bar (its extension): solid, lengths 0 to 100, from 0 (autoMin), negative values left of the axis in their color.
        var extended = Cf(Extended(), -5, 0, 10);
        Assert.Equal((0.0, 0.333333333, PdfColor.FromRgb(0, 0, 255), false), Bar(extended[0]));
        Assert.Equal((0.333333333, 0.333333333, PdfColor.FromRgb(0, 255, 0), false), Bar(extended[1]));
        Assert.Equal((0.333333333, 1.0, PdfColor.FromRgb(0, 255, 0), false), Bar(extended[2]));
        Assert.Equal(PdfColor.Black, extended[2].Format!.DataBar!.Border);

        // All positive: from 0 (autoMin), not from the least value.
        var positive = Cf(Extended(), 5, 10);
        Assert.Equal([0.5, 1.0], positive.Select(c => Math.Round(c.Format!.DataBar!.End, 9)));

        // As a cell background: the bar's share of the width, placed at its start.
        var layer = Assert.Single(SheetRenderer.DataBarLayers(new CellDataBar(0.25, 0.75, PdfColor.Black, false, null), c => throw new InvalidOperationException()));
        Assert.Equal((50.0, 50.0), (layer.Size.Width!.Value.Percent, layer.PositionX.Offset.Percent));
    }

    [Fact]
    public void Data_bars_have_their_axis_and_borders()
    {
        // Excel 2010's default: the axis where the values change sign, in black; negative bars with the positive border.
        var black = PdfColor.Black;
        var cells = Cf(Extended(), -5, 0, 10);
        Assert.All(cells, c => Assert.Equal((0.333333333, black), (Math.Round(c.Format!.DataBar!.Axis!.Value, 9), c.Format.DataBar.AxisColor)));
        Assert.Equal(black, cells[0].Format!.DataBar!.Border);

        // In the middle: bars on each side as long as their share of the largest absolute value; the negative bars'
        // own border, of their own color (not the positive one's) and the axis' color.
        var red = PdfColor.FromRgb(255, 0, 0);
        var gray = PdfColor.FromRgb(0x80, 0x80, 0x80);
        cells = Cf(Extended("axisPosition=\"middle\" negativeBarBorderColorSameAsPositive=\"0\" negativeBarColorSameAsPositive=\"1\"",
            "<x14:negativeBorderColor rgb=\"FFFF0000\"/><x14:axisColor rgb=\"FF808080\"/>"), -5, 0, 10);
        Assert.Equal((0.25, 0.5, PdfColor.FromRgb(0, 255, 0), false), Bar(cells[0]));
        Assert.Equal((0.5, 1.0, PdfColor.FromRgb(0, 255, 0), false), Bar(cells[2]));
        Assert.Equal((red, black), (cells[0].Format!.DataBar!.Border, cells[2].Format!.DataBar!.Border));
        Assert.All(cells, c => Assert.Equal((0.5, gray), (c.Format!.DataBar!.Axis, c.Format.DataBar.AxisColor)));

        // Without an axis: negative values have bars from the start, as the least values.
        cells = Cf(Extended("axisPosition=\"none\""), -5, 0, 10);
        Assert.All(cells, c => Assert.Equal((0.0, (double?)null), (c.Format!.DataBar!.Start, c.Format.DataBar.Axis)));

        // As layers: the axis on top, then the bar inside its border, which is the bar's place and size.
        var dash = SheetRenderer.AxisDash(gray);
        var layers = SheetRenderer.DataBarLayers(new CellDataBar(0.2, 0.4, black, false, red, 0.3, gray), c => c == gray ? dash : throw new InvalidOperationException());
        Assert.Equal(3, layers.Count);
        Assert.Same(dash, Assert.IsType<ImageBackground>(layers[0].Image).Image);
        Assert.Equal(red, Assert.IsType<LinearGradient>(layers[2].Image).Stops[0].Color);
        foreach (var width in new[] { 100.0, 250 })
        {
            // Where each layer's left edge is in a cell of this width (as Layout places backgrounds), and its width.
            (double Left, double Width) Place(BackgroundLayer l)
            {
                var w = l.Size.Width!.Value.Resolve(width);
                return (Math.Round(l.PositionX.Offset.Resolve(width - w), 9), Math.Round(w, 9));
            }

            var line = SheetRenderer.DataBarLine;
            Assert.Equal((Math.Round((0.3 * width) - (line / 2), 9), line), Place(layers[0]));
            Assert.Equal((Math.Round((0.2 * width) + line, 9), Math.Round((0.2 * width) - (2 * line), 9)), Place(layers[1]));
            Assert.Equal((Math.Round(0.2 * width, 9), Math.Round(0.2 * width, 9)), Place(layers[2]));
        }

        Assert.Equal(new Length(-2 * SheetRenderer.DataBarLine, 76), layers[1].Size.Height);

        // In right-to-left sheets, from the cell's right.
        var mirrored = new CellDataBar(0.25, 0.5, black, false, red, 0.25, gray).Mirrored();
        Assert.Equal((0.5, 0.75, 0.75), (mirrored.Start, mirrored.End, mirrored.Axis));
        Assert.Null(new CellDataBar(0.25, 0.5, black, false, null).Mirrored().Axis);
    }

    /// <summary>A data bar of A1:A3 with Excel 2010's extension: solid, from autoMin to autoMax, a black border and blue negative bars.</summary>
    private static string Extended(string attributes = "", string children = "")
    {
        const string ext = "xmlns:x14=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\" xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\"";
        return $"<conditionalFormatting sqref=\"A1:A3\"><cfRule type=\"dataBar\" priority=\"1\"><dataBar><cfvo type=\"min\"/><cfvo type=\"max\"/><color rgb=\"FF00FF00\"/></dataBar>"
            + $"<extLst><ext uri=\"{{B025F937-C7B1-47D3-B67F-A62EFF666E3E}}\" {ext}><x14:id>{{1}}</x14:id></ext></extLst></cfRule></conditionalFormatting>"
            + $"<extLst><ext uri=\"{{78C0D931-6437-407d-A8EE-F0AAD7539E65}}\" {ext}><x14:conditionalFormattings><x14:conditionalFormatting><x14:cfRule type=\"dataBar\" id=\"{{1}}\">"
            + $"<x14:dataBar gradient=\"0\" border=\"1\" {attributes}><x14:cfvo type=\"autoMin\"/><x14:cfvo type=\"autoMax\"/><x14:borderColor rgb=\"FF000000\"/><x14:negativeFillColor rgb=\"FF0000FF\"/>{children}</x14:dataBar>"
            + "</x14:cfRule><xm:sqref>A1:A3</xm:sqref></x14:conditionalFormatting></x14:conditionalFormattings></ext></extLst>";
    }

    private static (double, double, PdfColor, bool) Bar(SheetCell cell) =>
        (Math.Round(cell.Format!.DataBar!.Start, 9), Math.Round(cell.Format.DataBar.End, 9), cell.Format.DataBar.Color, cell.Format.DataBar.Gradient);

    [Fact]
    public void Icon_sets_give_each_value_the_icon_of_the_last_threshold_it_reaches()
    {
        string Rule(string attributes, string points) => $"<conditionalFormatting sqref=\"A1:A5\"><cfRule type=\"iconSet\" priority=\"1\"><iconSet {attributes}>{points}</iconSet></cfRule></conditionalFormatting>";
        const string percents = "<cfvo type=\"percent\" val=\"0\"/><cfvo type=\"percent\" val=\"33\"/><cfvo type=\"percent\" val=\"67\"/>";
        double[] values = [0, 3, 3.3, 6.7, 10];
        Assert.Equal([0, 0, 1, 2, 2], Cf(Rule("iconSet=\"3Arrows\"", percents), values).Select(c => c.Format!.Icon!.Index));
        Assert.Equal([2, 2, 1, 0, 0], Cf(Rule("iconSet=\"3Arrows\" reverse=\"1\"", percents), values).Select(c => c.Format!.Icon!.Index));
        Assert.Equal([0, 0, 0, 1, 2], Cf(Rule("iconSet=\"3Flags\"", "<cfvo type=\"num\" val=\"0\"/><cfvo type=\"num\" val=\"3.3\" gte=\"0\"/><cfvo type=\"percentile\" val=\"90\"/>"), values)
            .Select(c => c.Format!.Icon!.Index));
        var hidden = Cf(Rule("showValue=\"0\"", percents), values);
        Assert.All(hidden, c => Assert.True(c.Format!.HideText));
        Assert.Equal("3TrafficLights1", hidden[0].Format!.Icon!.Set);

        // Custom icons (Excel 2010): each threshold's own, or none.
        const string x14 = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main";
        var custom = Cf($"<extLst><ext uri=\"x\" xmlns:x14=\"{x14}\" xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\"><x14:conditionalFormattings><x14:conditionalFormatting>"
            + "<x14:cfRule type=\"iconSet\" priority=\"1\" id=\"{2}\"><x14:iconSet iconSet=\"3Stars\" custom=\"1\"><x14:cfvo type=\"percent\"><xm:f>0</xm:f></x14:cfvo><x14:cfvo type=\"num\"><xm:f>3</xm:f></x14:cfvo>"
            + "<x14:cfvo type=\"num\"><xm:f>6</xm:f></x14:cfvo><x14:cfIcon iconSet=\"NoIcons\" iconId=\"0\"/><x14:cfIcon iconSet=\"3Flags\" iconId=\"1\"/><x14:cfIcon iconSet=\"3Stars\" iconId=\"2\"/>"
            + "</x14:iconSet></x14:cfRule><xm:sqref>A1:A5</xm:sqref></x14:conditionalFormatting></x14:conditionalFormattings></ext></extLst>", values);
        Assert.Equal([null, new CellIcon("3Flags", 1), new CellIcon("3Flags", 1), new CellIcon("3Stars", 2), new CellIcon("3Stars", 2)], custom.Select(c => c.Format?.Icon));

        // Drawn at the start of the cell, every icon of every set; the value hidden when the rule says so.
        var all = new[] { "3Arrows", "3ArrowsGray", "3Flags", "3TrafficLights1", "3TrafficLights2", "3Signs", "3Symbols", "3Symbols2", "3Stars", "3Triangles",
            "4Arrows", "4ArrowsGray", "4RedToBlack", "4Rating", "4TrafficLights", "5Arrows", "5ArrowsGray", "5Rating", "5Quarters", "5Boxes" };
        var doc = new PdfDocument();
        var canvas = doc.AddPage().Canvas;
        foreach (var (set, i) in all.Select((s, i) => (s, i)))
        {
            Assert.True(SheetIcons.IsKnown(set));
            for (var index = 0; index < set[0] - '0'; index++)
            {
                SheetIcons.Draw(canvas, new CellIcon(set, index), 20 + (index * 20), 800 - (i * 20), 16);
            }
        }

        Assert.NotEmpty(doc.ToArray());
        Assert.False(SheetIcons.IsKnown("6Hearts"));
        var page = Assert.Single(Pages(XlsxToPdf.ConvertToBytes(XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\"><v>12345</v></c><c r=\"B1\"><v>67890</v></c></row>",
            "<cellXfs count=\"1\"><xf/></cellXfs>", sheetExtra: Rule("showValue=\"0\"", percents).Replace("A1:A5", "A1", StringComparison.Ordinal)))));
        Assert.DoesNotContain("12345", page, StringComparison.Ordinal);
        Assert.Contains("67890", page, StringComparison.Ordinal);
    }

    // ---- Pivot tables ----

    [Fact]
    public void Pivot_tables_get_their_style_over_their_cells()
    {
        // As openpyxl reads ExcelPivotTableSample.xlsx: Tabelle2's pivot table at A3:D7 (two header rows, two items and the
        // grand total; a grand total column), Tabelle3's at A3:B6 (one header row), both PivotStyleLight16.
        var diagnostics = new List<SheetDiagnostic>();
        var options = new XlsxConverterOptions { SheetNames = ["Tabelle2", "Tabelle3"], Sheet = new SheetOptions { Diagnostics = diagnostics.Add } };
        var sheets = XlsxConverter.Read(Fixtures.Read("Sheets/Formatting/ExcelPivotTableSample.xlsx"), options);
        Assert.Empty(diagnostics);
        var light = sheets[0].Rows[2][0].Format!.Fill;
        Assert.NotNull(light);
        string Look(SheetCell cell) => (cell.Format?.Bold ?? false ? "B" : "-") + (cell.Format?.Fill is null ? "-" : "F") + (cell.Format?.Bottom.IsVisible ?? false ? "_" : "-")
            + (cell.Format?.Top.IsVisible ?? false ? "^" : "-");
        Assert.Equal(["BF--", "BF_-", "----", "----", "BF-^"], sheets[0].Rows.Skip(2).Take(5).Select(r => Look(r[0])));
        Assert.Equal(["BF--", "BF_-", "B---", "B---", "BF-^"], sheets[0].Rows.Skip(2).Take(5).Select(r => Look(r[3]))); // The grand total column.
        Assert.Equal(["BF_-", "----", "----", "BF-^"], sheets[1].Rows.Skip(2).Take(4).Select(r => Look(r[1])));
        Assert.Equal(light, sheets[1].Rows[5][0].Format!.Fill);
        Assert.Null(sheets[0].Rows[1][0].Format?.Fill); // Above the table.
    }

    [Theory]
    [InlineData("<pivotTableStyleInfo name=\"PivotStyleLight16\"/>", true, false)]
    [InlineData("<pivotTableStyleInfo name=\"PivotStyleLight21\" showColHeaders=\"0\"/>", false, false)]
    [InlineData("", false, false)]
    [InlineData("<pivotTableStyleInfo name=\"PivotStyleMedium9\"/>", false, true)]
    [InlineData("<pivotTableStyleInfo name=\"PivotStyleLight22\"/>", false, true)]
    [InlineData("<pivotTableStyleInfo name=\"MyStyle\"/>", false, true)]
    public void Pivot_styles_that_are_not_supported_are_reported(string styleInfo, bool styled, bool reported)
    {
        var definition = System.Xml.Linq.XElement.Parse($"<pivotTableDefinition xmlns=\"{Main}\"><location ref=\"A1:B3\" firstHeaderRow=\"0\" firstDataRow=\"1\" firstDataCol=\"1\"/>"
            + $"<rowItems><i/><i t=\"grand\"/></rowItems>{styleInfo}</pivotTableDefinition>");
        var sheet = new XlsxWorksheet();
        sheet.Add(0, 0, new SheetCell("Row Labels", CellKind.Text), 100);
        var colors = new XlsxColors();
        Assert.Equal(!reported, XlsxPivotTables.Apply(definition, sheet, colors, 100));
        if (styled)
        {
            Assert.Equal(colors.Resolve(null, null, "4", null, null), sheet.Cells[XlsxWorksheet.Key(0, 0)].Format!.Bottom.Color); // Accent 1.
        }

        Assert.Equal(styled, sheet.Cells[XlsxWorksheet.Key(0, 0)].Format?.Bold ?? false);
        Assert.False(XlsxPivotTables.Apply(System.Xml.Linq.XElement.Parse($"<pivotTableDefinition xmlns=\"{Main}\"/>"), sheet, new XlsxColors(), 100));
    }

    [Fact]
    public void Xls_rule_formats_take_the_exact_colors_kept_after_them()
    {
        // A CF12 rule "equal to 5" on A1 whose format (DXFN) has a red font, a red solid fill and thin black borders from
        // the palette, and after it (XFExtNoFRT) an RGB text color, a theme fill color (and pattern color, which a solid fill
        // does not show) and an RGB top border color.
        byte[] Range() => Join(U16(0), U16(0), U16(0), U16(0));
        var font = new byte[118];
        font[88] = 0x82; // Italic and strikethrough not set.
        font[96] = 1; // Underline not set.
        font[100] = 1; // Bold not set.
        font[80] = 0x0A; // Red.
        byte[] Ext(int type, int colorType, long value) => Join(U16(type), U16(20), U16(colorType), U16(0), U32(value), new byte[8]);
        var dxf = Join(U32((1L << 26) | (1L << 28) | (1L << 29)), U16(0), font,
            U32(1 | (1 << 4) | (1 << 8) | (1 << 12) | (8 << 16) | (8 << 23)), U32(8 | (8 << 7)),
            U16(1 << 10), U16(0x0A | (0x0A << 7)),
            U16(0), U16(0xFFFF), U16(0), U16(4), Ext(0x0D, 2, 0x563412), Ext(0x05, 3, 4), Ext(0x04, 2, 0x00FF00), Ext(0x07, 2, 0xFF0000));
        var xls = SingleSheet(s => s
            .Record(0x0203, Cell(0, 0), F64(5))
            .Record(0x0879, U16(0x0879), new byte[10], U16(1), U16(2), Range(), U16(1), Range())
            .Record(0x087A, U16(0x087A), new byte[10], U8(1), U8(3), U16(3), U16(0), U32(dxf.Length), dxf, U8(0x1E), U16(5),
                U16(0), U8(0), U16(1), U16(0), U8(16), new byte[16]));
        var format = Assert.Single(ReadXls(xls).ConditionalRules).Format!;
        var black = PdfColor.FromRgb(0, 0, 0);
        Assert.Equal(PdfColor.FromRgb(0x12, 0x34, 0x56), format.Color);
        Assert.Equal(new XlsxColors().Resolve(null, null, "4", null, null), format.Fill); // Accent 1.
        Assert.Equal((PdfColor.FromRgb(0, 0, 0xFF), black, black, black), (format.Top!.Value.Color, format.Bottom!.Value.Color, format.Left!.Value.Color, format.Right!.Value.Color));
        Assert.Equal(format.Fill, XlsConverter.Read(xls)[0].Rows[0][0].Format!.Fill);
    }

    [Fact]
    public void Xls_icon_set_thresholds_keep_whether_equal_values_reach_them()
    {
        // A CFHEADER12 for A1:A2, and a CF12 icon set (3 arrows) of the thresholds 0, 5 (not reached by 5 itself) and 8.
        byte[] Range() => Join(U16(0), U16(1), U16(0), U16(0));
        byte[] Point(double value, bool equal) => Join(U8(1), U16(0), F64(value), U8(equal ? 1 : 0), U32(0));
        var xls = SingleSheet(s => s
            .Record(0x0203, Cell(0, 0), F64(5)).Record(0x0203, Cell(1, 0), F64(8))
            .Record(0x0879, U16(0x0879), new byte[10], U16(1), U16(2), Range(), U16(1), Range())
            .Record(0x087A, U16(0x087A), new byte[10], U8(6), U8(0), U16(0), U16(0), U32(0), U16(0), U16(0), U8(0), U16(1), U16(4), U8(16), new byte[16],
                U16(0), U8(0), U8(3), U8(0), U8(0), Point(0, true), Point(5, false), Point(8, true)));
        var rules = ReadXls(xls).ConditionalRules;
        Assert.Equal([true, false, true], Assert.Single(rules).Values.Select(v => v.GreaterOrEqual));
        var cells = XlsConverter.Read(xls)[0].Rows.Select(r => r[0].Format!.Icon).ToList();
        Assert.Equal([new CellIcon("3Arrows", 0), new CellIcon("3Arrows", 2)], cells);
    }
}
