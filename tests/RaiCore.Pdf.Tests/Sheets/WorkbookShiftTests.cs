using System.Xml.Linq;
using RaiCore.Pdf.Sheets;
using static RaiCore.Pdf.Tests.Sheets.WorkbookTests;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// Inserting and deleting rows and columns with the Workbook API: everything that refers to the cells moves, as Excel
/// moves it (the expectations follow Excel's documented behavior: references at or after an insertion move, deleted
/// single references become #REF!, ranges grow and shrink).
/// </summary>
public class WorkbookShiftTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace S = Main;
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    private static Dictionary<string, string> Names(byte[] saved) =>
        Xml(saved, "xl/workbook.xml").Descendants(S + "definedName").ToDictionary(n => (string)n.Attribute("name")! + ((string?)n.Attribute("localSheetId") is { } id ? "@" + id : string.Empty), n => n.Value);

    private static string Part(byte[] saved, string suffix) => Parts(saved).Keys.Single(k => k.EndsWith(suffix, StringComparison.Ordinal));

    [Fact]
    public void Inserting_rows_moves_every_reference()
    {
        var workbook = Workbook.Open(Template);
        var data = workbook.Worksheets["Data"];
        data.InsertRows(2);
        var saved = workbook.ToArray();
        var reopened = Workbook.Open(saved);
        var moved = reopened.Worksheets["Data"];
        var other = reopened.Worksheets["Other Sheet"];

        // Cells and their formulas, the shared formula's cells each with their own.
        Assert.Equal("Name", moved["A1"].Value);
        Assert.Equal(CellValueType.Empty, moved["A2"].Type);
        Assert.Equal("Alpha", moved["A3"].Value);
        Assert.Equal(10.0, moved["B3"].Value);
        Assert.Equal("B3*2", moved["E3"].Formula);
        Assert.Equal("B7*2", moved["E7"].Formula);
        Assert.Equal("SUM(B3:B7)", moved["B8"].Formula);
        Assert.Equal("B3:B4*10", moved["F3"].Formula);

        // Formulas on other sheets, by sheet name; whole columns, 3-D references, names and tables need no change.
        Assert.Equal("Data!B3", other["A2"].Formula);
        Assert.Equal("SUM(Data!B3:B7)", other["A3"].Formula);
        Assert.Equal("'Other Sheet'!A1", other["A4"].Formula);
        Assert.Equal("SUM(Data!B:B)", other["A5"].Formula);
        Assert.Equal("SUM(Data!3:4)", other["A6"].Formula);
        Assert.Equal("SUM(Data:Tab!A1)", other["A7"].Formula);
        Assert.Equal("Total", other["A8"].Formula);
        Assert.Equal("SUM(Sales[Amount])", other["A9"].Formula);

        // Defined names, the print area and print titles.
        var names = Names(saved);
        Assert.Equal("Data!$B$8", names["Total"]);
        Assert.Equal("Data!$B$3:$B$7", names["Amounts"]);
        Assert.Equal("Data!$A$1:$F$14", names["_xlnm.Print_Area@0"]);
        Assert.Equal("Data!$1:$1", names["_xlnm.Print_Titles@0"]);
        Assert.Equal("Data!$A$1:$F$7", names["_xlnm._FilterDatabase@0"]);
        Assert.Equal("'Other Sheet'!$A$1", names["Local@1"]);

        // The worksheet's ranges.
        var sheet = Xml(saved, "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal(["A10:C10"], moved.MergedRanges);
        Assert.Equal(["A3:A7", "B3:B7"], sheet.Elements(S + "conditionalFormatting").Select(c => (string)c.Attribute("sqref")!).Order());
        Assert.Contains(sheet.Descendants(S + "formula"), f => f.Value == "$D3");
        Assert.Equal(["B3:B7", "D3:D7"], sheet.Descendants(S + "dataValidation").Select(c => (string)c.Attribute("sqref")!).Order());
        Assert.Equal("A1:F7", (string?)sheet.Element(S + "autoFilter")!.Attribute("ref"));
        Assert.Equal(["A14", "B14"], sheet.Descendants(S + "hyperlink").Select(h => (string)h.Attribute("ref")!));
        Assert.Equal("8", (string?)sheet.Element(S + "rowBreaks")!.Element(S + "brk")!.Attribute("id"));
        Assert.True(moved.Row(9).IsHidden);
        Assert.False(moved.Row(8).IsHidden);
        Assert.Equal([0, 1, 1, 1, 0], Enumerable.Range(10, 5).Select(r => moved.Row(r).OutlineLevel));
        Assert.True(moved.Column(7).IsHidden);
        Assert.Equal("A1:G14", (string?)sheet.Element(S + "dimension")!.Attribute("ref"));

        // The comment, the chart's series and the anchors of the chart and the picture.
        var comments = Xml(saved, Part(saved, "comments1.xml"));
        Assert.Equal("C4", (string?)comments.Descendants(S + "comment").Single().Attribute("ref"));
        var vml = System.Text.Encoding.UTF8.GetString(Parts(saved)[Part(saved, "vmlDrawing1.vml")]);
        Assert.Contains("<x:Row>3</x:Row>", vml, StringComparison.Ordinal);
        var chart = Xml(saved, Part(saved, "chart1.xml"));
        Assert.Equal(["Data!$B$1", "Data!$A$3:$A$7", "Data!$B$3:$B$7"], chart.Descendants().Where(e => e.Name.LocalName == "f").Select(f => f.Value));
        var anchors = Xml(saved, Part(saved, "drawing1.xml")).Root!.Elements().Select(a => (int)a.Element(Xdr + "from")!.Element(Xdr + "row")!).Order().ToList();
        Assert.Equal([2, 20], anchors);
    }

    [Fact]
    public void Inserting_rows_inside_an_array_formula_throws_and_changes_nothing()
    {
        var workbook = Workbook.Open(Template);
        var data = workbook.Worksheets["Data"];
        Assert.Throws<InvalidOperationException>(() => data.InsertRows(3));
        Assert.Throws<InvalidOperationException>(() => data.DeleteRows(3));
        Assert.Throws<InvalidOperationException>(() => data.DeleteRows(2));
        Assert.Equal(Template, workbook.ToArray());
    }

    [Fact]
    public void Deleting_rows_shrinks_ranges_and_makes_deleted_references_errors()
    {
        var workbook = Workbook.Open(Template);
        workbook.Worksheets["Data"].DeleteRows(4, 2);
        var saved = workbook.ToArray();
        var reopened = Workbook.Open(saved);
        var data = reopened.Worksheets["Data"];
        var other = reopened.Worksheets["Other Sheet"];
        Assert.Equal("Bravo", data["A3"].Value);
        Assert.Equal("Echo", data["A4"].Value);
        Assert.Equal("SUM(B2:B4)", data["B5"].Formula);
        Assert.Equal("B4*2", data["E4"].Formula);
        Assert.Equal("SUM(Data!B2:B4)", other["A3"].Formula);
        Assert.Equal("SUM(Data!2:3)", other["A6"].Formula);
        var names = Names(saved);
        Assert.Equal("Data!$B$5", names["Total"]);
        Assert.Equal("Data!$B$2:$B$4", names["Amounts"]);
        Assert.Equal("Data!$A$1:$F$11", names["_xlnm.Print_Area@0"]);
        Assert.Equal(["A7:C7"], data.MergedRanges);
        Assert.True(data.Row(6).IsHidden);
        var sheet = Xml(saved, "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal("A1:F4", (string?)sheet.Element(S + "autoFilter")!.Attribute("ref"));
        Assert.Equal("5", (string?)sheet.Element(S + "rowBreaks")!.Element(S + "brk")!.Attribute("id"));

        // Deleting the rows a formula refers to.
        var again = Workbook.Open(saved);
        again.Worksheets["Data"].DeleteColumns(6);
        again.Worksheets["Data"].DeleteRows(2, 1);
        var other2 = Workbook.Open(again.ToArray()).Worksheets["Other Sheet"];
        Assert.Equal("Data!#REF!", other2["A2"].Formula);
        Assert.Equal("SUM(Data!B2:B3)", other2["A3"].Formula);
    }

    [Fact]
    public void Deleting_everything_a_range_covers_removes_it()
    {
        var bytes = XlsxToPdfTests.Workbook(
            "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"2\"><c r=\"A2\"><v>2</v></c></row><row r=\"3\"><c r=\"A3\"><v>3</v></c></row>"
            + "<row r=\"5\"><c r=\"B5\"><f>A2</f></c><c r=\"C5\"><f>SUM(A1:A3)</f></c><c r=\"D5\"><f>A2*2+$A$3</f></c><c r=\"E5\"><f>Sheet1!A2</f></c></row>",
            sheetExtra: "<mergeCells count=\"2\"><mergeCell ref=\"F2:G2\"/><mergeCell ref=\"F1:F2\"/></mergeCells>"
                + "<conditionalFormatting sqref=\"H2\"><cfRule type=\"expression\" priority=\"1\"><formula>A2</formula></cfRule></conditionalFormatting>"
                + "<dataValidations count=\"2\"><dataValidation type=\"whole\" sqref=\"H2 H3\"><formula1>0</formula1></dataValidation><dataValidation type=\"whole\" sqref=\"I2\"><formula1>0</formula1></dataValidation></dataValidations>"
                + "<hyperlinks><hyperlink ref=\"J2\" location=\"Sheet1!A3\"/></hyperlinks>");
        var workbook = Workbook.Open(bytes);
        workbook.Worksheets[0].DeleteRows(2);
        var saved = workbook.ToArray();
        var sheet = Workbook.Open(saved).Worksheets[0];
        Assert.Equal("#REF!", sheet["B4"].Formula);
        Assert.Equal("SUM(A1:A2)", sheet["C4"].Formula);
        Assert.Equal("#REF!*2+$A$2", sheet["D4"].Formula);
        Assert.Equal("Sheet1!#REF!", sheet["E4"].Formula);
        Assert.Empty(sheet.MergedRanges);
        var root = Xml(saved, "xl/worksheets/sheet1.xml").Root!;
        Assert.Null(root.Element(S + "mergeCells"));
        Assert.Null(root.Element(S + "conditionalFormatting"));
        Assert.Equal("1", (string?)root.Element(S + "dataValidations")!.Attribute("count"));
        Assert.Equal("H2", (string?)root.Descendants(S + "dataValidation").Single().Attribute("sqref"));
        Assert.Null(root.Element(S + "hyperlinks"));
    }

    [Fact]
    public void Inserting_columns_moves_every_reference()
    {
        var workbook = Workbook.Open(Template);
        workbook.Worksheets["Data"].InsertColumns(2);
        var saved = workbook.ToArray();
        var reopened = Workbook.Open(saved);
        var data = reopened.Worksheets["Data"];
        var other = reopened.Worksheets["Other Sheet"];
        Assert.Equal("Alpha", data["A2"].Value);
        Assert.Equal(10.0, data["C2"].Value);
        Assert.Equal("SUM(C2:C6)", data["C7"].Formula);
        Assert.Equal("C4*2", data["F4"].Formula);
        Assert.Equal("Data!C2", other["A2"].Formula);
        Assert.Equal("SUM(Data!C:C)", other["A5"].Formula);
        Assert.Equal("SUM(Data!2:3)", other["A6"].Formula);
        Assert.True(data.Column(8).IsHidden);
        Assert.False(data.Column(7).IsHidden);
        Assert.Equal(["A9:D9"], data.MergedRanges);
        var names = Names(saved);
        Assert.Equal("Data!$C$7", names["Total"]);
        Assert.Equal("Data!$A$1:$G$13", names["_xlnm.Print_Area@0"]);
        var sheet = Xml(saved, "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal("A1:G6", (string?)sheet.Element(S + "autoFilter")!.Attribute("ref"));
        Assert.Equal("D3", (string?)Xml(saved, Part(saved, "comments1.xml")).Descendants(S + "comment").Single().Attribute("ref"));
        var anchors = Xml(saved, Part(saved, "drawing1.xml")).Root!.Elements().Select(a => (int)a.Element(Xdr + "from")!.Element(Xdr + "col")!).ToList();
        Assert.Equal([10, 10], anchors);
    }

    [Fact]
    public void Deleting_columns_moves_filters_and_column_ranges()
    {
        var bytes = XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"D1\"><v>4</v></c></row>",
            sheetBefore: "<cols><col min=\"1\" max=\"1\" width=\"5\" customWidth=\"1\"/><col min=\"2\" max=\"3\" width=\"7\" customWidth=\"1\"/><col min=\"4\" max=\"6\" width=\"9\" hidden=\"1\" customWidth=\"1\"/></cols>",
            sheetExtra: "<autoFilter ref=\"A1:E9\"><filterColumn colId=\"1\"><filters><filter val=\"x\"/></filters></filterColumn><filterColumn colId=\"3\"><filters><filter val=\"y\"/></filters></filterColumn></autoFilter>"
                + "<colBreaks count=\"2\" manualBreakCount=\"2\"><brk id=\"2\" max=\"1048575\" man=\"1\"/><brk id=\"5\" max=\"1048575\" man=\"1\"/></colBreaks>");
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        sheet.DeleteColumns(2, 2);
        Assert.Equal(4.0, sheet["B1"].Value);
        var root = Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal(["1-1", "2-4"], root.Descendants(S + "col").Select(c => $"{c.Attribute("min")!.Value}-{c.Attribute("max")!.Value}"));
        Assert.Equal("A1:C9", (string?)root.Element(S + "autoFilter")!.Attribute("ref"));
        Assert.Equal(["1"], root.Descendants(S + "filterColumn").Select(f => (string)f.Attribute("colId")!));
        Assert.Equal(["3"], root.Descendants(S + "brk").Select(b => (string)b.Attribute("id")!));
        Assert.Equal("1", (string?)root.Element(S + "colBreaks")!.Attribute("count"));
        Assert.True(sheet.Column(3).IsHidden);
        Assert.False(sheet.Column(1).IsHidden);
    }

    [Fact]
    public void Inserting_columns_inside_a_table_adds_table_columns()
    {
        var workbook = Workbook.Open(Template);
        var tab = workbook.Worksheets["Tab"];
        tab.InsertColumns(2);
        var saved = workbook.ToArray();
        var table = Xml(saved, Part(saved, "table1.xml")).Root!;
        Assert.Equal("A1:D5", (string?)table.Attribute("ref"));
        Assert.Equal(["Item", "Column4", "Amount", "Note"], table.Descendants(S + "tableColumn").Select(c => (string)c.Attribute("name")!));
        Assert.Equal("4", (string?)table.Element(S + "tableColumns")!.Attribute("count"));
        Assert.Equal(4, table.Descendants(S + "tableColumn").Select(c => (string)c.Attribute("id")!).Distinct().Count());
        Assert.Equal("A1:D5", (string?)table.Element(S + "autoFilter")!.Attribute("ref"));
        Assert.Equal("Column4", Workbook.Open(saved).Worksheets["Tab"]["B1"].Value);

        // Deleting it again removes the table column.
        var again = Workbook.Open(saved);
        again.Worksheets["Tab"].DeleteColumns(2);
        var table2 = Xml(again.ToArray(), Part(saved, "table1.xml")).Root!;
        Assert.Equal(["Item", "Amount", "Note"], table2.Descendants(S + "tableColumn").Select(c => (string)c.Attribute("name")!));
    }

    [Fact]
    public void Deleting_a_whole_table_removes_it_and_part_of_its_header_throws()
    {
        var workbook = Workbook.Open(Template);
        var tab = workbook.Worksheets["Tab"];
        Assert.Throws<InvalidOperationException>(() => tab.DeleteRows(1, 2));
        Assert.Throws<InvalidOperationException>(() => tab.DeleteRows(2, 4));
        tab.InsertRows(3);
        Assert.Equal("A1:C6", (string?)Xml(workbook.ToArray(), Part(workbook.ToArray(), "table1.xml")).Root!.Attribute("ref"));
        tab.DeleteRows(1, 6);
        var saved = workbook.ToArray();
        var parts = Parts(saved);
        Assert.DoesNotContain(parts.Keys, k => k.Contains("tables/", StringComparison.Ordinal));
        Assert.DoesNotContain("table+xml", System.Text.Encoding.UTF8.GetString(parts["[Content_Types].xml"]), StringComparison.Ordinal);
        var sheet = Xml(saved, "xl/worksheets/sheet3.xml").Root!;
        Assert.Null(sheet.Element(S + "tableParts"));
        Assert.DoesNotContain("table", System.Text.Encoding.UTF8.GetString(parts["xl/worksheets/_rels/sheet3.xml.rels"]), StringComparison.Ordinal);
        Assert.Equal(4, Workbook.Open(saved).Worksheets.Count);
    }

    [Fact]
    public void Rows_inserted_inside_an_outline_group_join_it()
    {
        var workbook = Workbook.Open(Template);
        var data = workbook.Worksheets["Data"];
        data.InsertRows(11, 2);
        Assert.Equal([0, 1, 1, 1, 1, 1, 0], Enumerable.Range(9, 7).Select(r => data.Row(r).OutlineLevel));
        data.InsertRows(10);
        Assert.Equal(0, data.Row(10).OutlineLevel);
    }

    [Fact]
    public void Inserted_rows_and_columns_take_the_format_of_their_neighbors()
    {
        var bytes = XlsxToPdfTests.Workbook(
            "<row r=\"1\" ht=\"30\" customHeight=\"1\"><c r=\"A1\" s=\"1\"><v>1</v></c><c r=\"B1\" s=\"2\"/><c r=\"C1\"><v>3</v></c></row>"
            + "<row r=\"2\"><c r=\"A2\" s=\"2\"><v>2</v></c></row>",
            styles: "<cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"10\" applyNumberFormat=\"1\"/><xf numFmtId=\"2\" applyNumberFormat=\"1\"/></cellXfs>",
            sheetBefore: "<cols><col min=\"1\" max=\"1\" width=\"20\" customWidth=\"1\"/></cols>");
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        sheet.InsertRows(2, 2);
        var root = Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Root!;
        var cells = root.Descendants(S + "c").ToDictionary(c => (string)c.Attribute("r")!, c => (string?)c.Attribute("s"));
        Assert.Equal("1", cells["A2"]);
        Assert.Equal("2", cells["B3"]);
        Assert.False(cells.ContainsKey("C2"));
        Assert.Equal("2", cells["A4"]);
        Assert.Equal(CellValueType.Empty, sheet["A2"].Type);
        Assert.Equal(30, sheet.Row(3).Height);
        Assert.Null(sheet.Row(4).Height);

        sheet.InsertColumns(2);
        Assert.Equal(20, sheet.Column(2).Width);
        Assert.Equal(1.0, sheet["A1"].Value);
        Assert.Equal(CellValueType.Empty, sheet["B1"].Type);
        Assert.Equal("0.00%", sheet["B1"].NumberFormat);
        Assert.Equal("0.00", sheet["B4"].NumberFormat);
    }

    [Fact]
    public void Shared_formulas_on_the_changed_sheet_get_formulas_of_their_own()
    {
        var bytes = XlsxToPdfTests.Workbook(
            "<row r=\"1\"><c r=\"B1\"><f t=\"shared\" ref=\"B1:B3\" si=\"0\">Other!A1</f><v>1</v></c></row>"
            + "<row r=\"2\"><c r=\"B2\"><f t=\"shared\" si=\"0\"/><v>2</v></c></row><row r=\"3\"><c r=\"B3\"><f t=\"shared\" si=\"0\"/><v>3</v></c></row>");
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        sheet.InsertRows(2);
        Assert.Equal(["Other!A1", "Other!A2", "Other!A3"], new[] { "B1", "B3", "B4" }.Select(a => sheet[a].Formula));
    }

    [Fact]
    public void Local_names_refer_to_their_own_sheet_without_a_sheet_name()
    {
        var bytes = XlsxToPdfTests.Workbook(string.Empty,
            workbookExtra: "<definedNames><definedName name=\"Local\" localSheetId=\"0\">$A$5</definedName><definedName name=\"Global\">$A$5</definedName></definedNames>");
        var workbook = Workbook.Open(bytes);
        workbook.Worksheets[0].InsertRows(2);
        var names = Names(workbook.ToArray());
        Assert.Equal("$A$6", names["Local@0"]);
        Assert.Equal("$A$5", names["Global"]);
    }

    [Fact]
    public void Filters_keep_their_columns_when_the_filter_moves()
    {
        var bytes = XlsxToPdfTests.Workbook(string.Empty,
            sheetExtra: "<autoFilter ref=\"C1:F9\"><filterColumn colId=\"1\"><filters><filter val=\"x\"/></filters></filterColumn></autoFilter>");
        var workbook = Workbook.Open(bytes);
        workbook.Worksheets[0].DeleteColumns(1);
        var filter = Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Root!.Element(S + "autoFilter")!;
        Assert.Equal("B1:E9", (string?)filter.Attribute("ref"));
        Assert.Equal("1", (string?)filter.Element(S + "filterColumn")!.Attribute("colId"));
    }

    [Fact]
    public void Inserting_columns_before_a_table_moves_it()
    {
        var workbook = Workbook.Open(Template);
        workbook.Worksheets["Tab"].InsertColumns(1);
        var saved = workbook.ToArray();
        var table = Xml(saved, Part(saved, "table1.xml")).Root!;
        Assert.Equal("B1:D5", (string?)table.Attribute("ref"));
        Assert.Equal(["Item", "Amount", "Note"], table.Descendants(S + "tableColumn").Select(c => (string)c.Attribute("name")!));
    }

    [Fact]
    public void Deleting_a_commented_cell_removes_the_comment_and_its_note()
    {
        var bytes = Fixtures.Read(Path.Combine("Workbooks", "Poi", "comments.xlsx"));
        var refs = Xml(bytes, Part(bytes, "comments1.xml")).Descendants(S + "comment").Select(c => (string)c.Attribute("ref")!).ToList();
        var row = int.Parse(refs[0].TrimStart('A', 'B', 'C', 'D', 'E', 'F', 'G', 'H'), System.Globalization.CultureInfo.InvariantCulture);
        static int Notes(byte[] package) => System.Text.RegularExpressions.Regex.Count(
            System.Text.Encoding.UTF8.GetString(Parts(package)[Part(package, ".vml")]), "ObjectType=\"Note\"");
        var workbook = Workbook.Open(bytes);
        workbook.Worksheets[0].DeleteRows(row);
        var saved = workbook.ToArray();
        var remaining = Xml(saved, Part(saved, "comments1.xml")).Descendants(S + "comment").Count();
        Assert.True(remaining < refs.Count);
        Assert.Equal(Notes(bytes) - (refs.Count - remaining), Notes(saved));
    }

    [Fact]
    public void Insertions_that_push_values_off_the_sheet_throw()
    {
        var bytes = XlsxToPdfTests.Workbook("<row r=\"1048576\"><c r=\"A1048576\"><v>1</v></c></row>");
        var workbook = Workbook.Open(bytes);
        Assert.Throws<InvalidOperationException>(() => workbook.Worksheets[0].InsertRows(1));
        workbook.Worksheets[0].InsertColumns(1);
        Assert.Equal(1.0, workbook.Worksheets[0]["B1048576"].Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => workbook.Worksheets[0].DeleteRows(1048576, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => workbook.Worksheets[0].InsertRows(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => workbook.Worksheets[0].InsertColumns(1, 0));
    }

    [Fact]
    public void Comments_and_their_shapes_move_and_go_with_their_cells()
    {
        var bytes = Fixtures.Read(Path.Combine("Workbooks", "Poi", "comments.xlsx"));
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        var before = Xml(bytes, Part(bytes, "comments1.xml")).Descendants(S + "comment").Select(c => (string)c.Attribute("ref")!).ToList();
        sheet.InsertRows(1, 3);
        var saved = workbook.ToArray();
        var after = Xml(saved, Part(saved, "comments1.xml")).Descendants(S + "comment").Select(c => (string)c.Attribute("ref")!).ToList();
        Assert.Equal(before.Select(r => Shifted(r, 3)), after);

        static string Shifted(string address, int rows)
        {
            var letters = new string(address.TakeWhile(char.IsLetter).ToArray());
            return letters + (int.Parse(address[letters.Length..], System.Globalization.CultureInfo.InvariantCulture) + rows).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    [Theory]
    [MemberData(nameof(WorkbookTests.Corpus), MemberType = typeof(WorkbookTests))]
    public void Corpus_workbooks_take_insertions_and_deletions(string file)
    {
        var workbook = Workbook.Open(Fixtures.Read(file));
        foreach (var sheet in workbook.Worksheets)
        {
            foreach (var action in new Action[] { () => sheet.InsertRows(2, 2), () => sheet.DeleteRows(3), () => sheet.InsertColumns(1), () => sheet.DeleteColumns(2) })
            {
                try
                {
                    action();
                }
                catch (InvalidOperationException)
                {
                    // An array formula or a table header in the way, as in Excel.
                }
            }
        }

        var saved = workbook.ToArray();
        var reopened = Workbook.Open(saved);
        Assert.Equal(workbook.Worksheets.Select(s => s.Name), reopened.Worksheets.Select(s => s.Name));
        foreach (var part in Parts(saved).Where(p => p.Key.EndsWith(".xml", StringComparison.Ordinal) || p.Key.EndsWith(".rels", StringComparison.Ordinal)))
        {
            XDocument.Parse(System.Text.Encoding.UTF8.GetString(part.Value));
        }
    }

    /// <summary>
    /// Writes edited workbooks for the independent check with openpyxl (<c>Fixtures/Workbooks/check-saved.py</c>) to
    /// the folder <c>RAICORE_WORKBOOK_OUT</c> names; skipped without it.
    /// </summary>
    [Fact]
    public void Writes_edited_workbooks_for_the_openpyxl_check()
    {
        var folder = Environment.GetEnvironmentVariable("RAICORE_WORKBOOK_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "RAICORE_WORKBOOK_OUT is not set (the openpyxl check runs by hand).");
        Directory.CreateDirectory(folder!);

        void Save(string name, Action<Workbook> edit, byte[]? source = null)
        {
            var workbook = source is null ? Workbook.Create() : Workbook.Open(source);
            edit(workbook);
            workbook.Save(Path.Combine(folder!, name));
        }

        Save("insert-rows.xlsx", w => w.Worksheets["Data"].InsertRows(2), Template);
        Save("delete-rows.xlsx", w => w.Worksheets["Data"].DeleteRows(4, 2), Template);
        Save("insert-columns.xlsx", w =>
        {
            w.Worksheets["Data"].InsertColumns(2);
            w.Worksheets["Tab"].InsertColumns(2);
        }, Template);
        Save("delete-columns.xlsx", w => w.Worksheets["Data"].DeleteColumns(1, 2), Template);
        Save("values.xlsx", w =>
        {
            var data = w.Worksheets["Data"];
            data["A2"].SetValue("Zulu");
            data["B2"].SetValue(12.5);
            data["C2"].SetValue(new DateTime(2027, 2, 3));
            data["D2"].SetValue(false);
            data["H2"].SetValue(new DateTime(2026, 11, 23, 14, 15, 16));
            data["H3"].SetFormula("SUM(B2:B6)");
            data["H4"].SetValue("  spaced & <escaped> _x0041_ ");
            data.Row(3).IsHidden = true;
            data.Row(8).IsHidden = false;
            data.Column(2).IsHidden = true;
            data.Column(4).Width = 20;
            w.Worksheets["Secret"].IsHidden = false;
            w.Worksheets.Add("Added")["A1"].SetValue("New sheet");
        }, Template);
        Save("created.xlsx", w =>
        {
            var sheet = w.Worksheets[0];
            sheet["A1"].SetValue("Text");
            sheet["B1"].SetValue(1.5);
            sheet["C1"].SetValue(true);
            sheet["D1"].SetValue(new DateTime(2026, 11, 23));
            sheet["E1"].SetFormula("B1*2");
            sheet.Row(2).IsHidden = true;
            w.Worksheets.Add("Second")["A1"].SetValue("Second");
        });
        foreach (var file in WorkbookTests.Corpus())
        {
            var path = file.Data;
            Save("corpus-" + Path.GetFileName(path), workbook =>
            {
                foreach (var sheet in workbook.Worksheets)
                {
                    foreach (var action in new Action[] { () => sheet.InsertRows(2, 2), () => sheet.DeleteRows(3), () => sheet.InsertColumns(1), () => sheet.DeleteColumns(2), () => sheet["B2"].SetValue("Edited") })
                    {
                        try
                        {
                            action();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }
            }, Fixtures.Read(path));
        }
    }
}
