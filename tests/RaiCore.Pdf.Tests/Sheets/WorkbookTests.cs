using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Sheets;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// The Workbook API: opening, reading typed values, writing values, finding cells, rows and columns, saving with the
/// template's parts unchanged, read-only formats, new workbooks, converting, and hostile input.
/// </summary>
public class WorkbookTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal static byte[] Template => Fixtures.Read(Path.Combine("Workbooks", "template.xlsx"));

    /// <summary>The decompressed parts of a package, by name.</summary>
    internal static Dictionary<string, byte[]> Parts(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            parts[entry.FullName] = buffer.ToArray();
        }

        return parts;
    }

    /// <summary>A part of a package as XML.</summary>
    internal static XDocument Xml(byte[] package, string part) => XDocument.Parse(Encoding.UTF8.GetString(Parts(package)[part]));

    /// <summary>The corpus: the generated fixtures and the Apache POI workbooks.</summary>
    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Fixtures.Path("Workbooks"), "*.xls?", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Fixtures.Path("Sheets"), "*.xlsx")).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(Fixtures.Path(string.Empty), file).Replace('\\', '/'));
        }

        return data;
    }

    [Fact]
    public void Opens_worksheets_by_position_and_name()
    {
        var workbook = Workbook.Open(Template);
        Assert.Equal(WorkbookFormat.Xlsx, workbook.Format);
        Assert.False(workbook.IsReadOnly);
        Assert.False(workbook.Date1904);
        Assert.Equal(["Data", "Other Sheet", "Tab", "Secret"], workbook.Worksheets.Select(s => s.Name));
        Assert.Same(workbook.Worksheets[1], workbook.Worksheets["other sheet"]);
        Assert.True(workbook.Worksheets["Secret"].IsHidden);
        Assert.False(workbook.Worksheets["Data"].IsHidden);
        Assert.Equal(2, workbook.Worksheets["Tab"].Index);
        Assert.True(workbook.Worksheets.Contains("TAB"));
        Assert.Throws<KeyNotFoundException>(() => workbook.Worksheets["Missing"]);
        Assert.Same(workbook.Worksheets[0], workbook.ActiveWorksheet);
    }

    [Fact]
    public void Reads_typed_values()
    {
        var sheet = Workbook.Open(Template).Worksheets["Data"];
        Assert.Equal(CellValueType.Text, sheet["A1"].Type);
        Assert.Equal("Name", sheet["A1"].Value);
        Assert.Equal("Name", sheet["A1"].TextValue);
        Assert.Equal(CellValueType.Number, sheet["B2"].Type);
        Assert.Equal(10.0, sheet["B2"].NumberValue);
        Assert.Equal("10.00", sheet["B2"].GetDisplayText());
        Assert.Equal("#,##0.00", sheet["B2"].NumberFormat);
        Assert.Equal(CellValueType.DateTime, sheet["C2"].Type);
        Assert.Equal(new DateTime(2026, 1, 1), sheet["C2"].DateTimeValue);
        Assert.Equal("2026-01-01", sheet["C2"].GetDisplayText());
        Assert.Equal(CellValueType.Boolean, sheet["D2"].Type);
        Assert.True(sheet["D2"].BooleanValue);
        Assert.Equal("TRUE", sheet["D2"].GetDisplayText());
        Assert.Equal(CellValueType.Empty, sheet["Z99"].Type);
        Assert.Null(sheet["Z99"].Value);
        Assert.Equal(string.Empty, sheet["Z99"].GetDisplayText());
        Assert.Null(sheet["A1"].NumberValue);
        Assert.Null(sheet["B2"].TextValue);

        // Formulas with their cached values: a shared formula's cells have the first cell's formula copied to them.
        Assert.Equal("B2*2", sheet["E2"].Formula);
        Assert.Equal("B5*2", sheet["E5"].Formula);
        Assert.Equal(80.0, sheet["E5"].Value);
        Assert.Equal("SUM(B2:B6)", sheet["B7"].Formula);
        Assert.Equal(150.0, sheet["B7"].NumberValue);
        Assert.True(sheet["F2"].HasFormula);
        Assert.False(sheet["F3"].HasFormula);
        Assert.False(sheet["A1"].HasFormula);
        Assert.Equal(sheet["B3"].Address, sheet.Cell(3, 2).Address);
        Assert.Equal("Data!B3", sheet.Cell(3, 2).ToString());
    }

    [Fact]
    public void Reads_errors_inline_strings_and_iso_dates()
    {
        var bytes = XlsxToPdfTests.Workbook(
            "<row r=\"1\"><c r=\"A1\" t=\"e\"><f>1/0</f><v>#DIV/0!</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>Inline</t></is></c>"
            + "<c r=\"C1\" t=\"d\"><v>2026-11-23T08:30:00</v></c><c r=\"D1\" t=\"str\"><f>\"a\"&amp;\"b\"</f><v>ab</v></c>"
            + "<c r=\"E1\"><f>NOW()</f></c><c r=\"F1\" t=\"s\"><v>0</v></c><c r=\"G1\" t=\"s\"><v>7</v></c></row>",
            sharedStrings: "<si><r><t>Rich </t></r><r><t>text</t></r><rPh><t>phonetic</t></rPh></si>");
        var sheet = Workbook.Open(bytes).Worksheets[0];
        Assert.Equal(CellValueType.Error, sheet["A1"].Type);
        Assert.Equal("#DIV/0!", sheet["A1"].ErrorValue);
        Assert.Equal("Inline", sheet["B1"].Value);
        Assert.Equal(new DateTime(2026, 11, 23, 8, 30, 0), sheet["C1"].DateTimeValue);
        Assert.Equal("ab", sheet["D1"].Value);
        Assert.Equal("\"a\"&\"b\"", sheet["D1"].Formula);
        Assert.Equal(CellValueType.Empty, sheet["E1"].Type);
        Assert.Equal("NOW()", sheet["E1"].Formula);
        Assert.Equal("Rich text", sheet["F1"].Value);
        Assert.Equal(string.Empty, sheet["G1"].Value);
    }

    [Fact]
    public void Reads_dates_in_the_1904_date_system()
    {
        var workbook = Workbook.Open(Fixtures.Read(Path.Combine("Sheets", "date1904.xlsx")));
        Assert.True(workbook.Date1904);
        Assert.Equal(new DateTime(2026, 11, 19), workbook.Worksheets[0]["A1"].DateTimeValue);
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Saving_without_changes_writes_the_same_bytes(string file)
    {
        var bytes = Fixtures.Read(file);
        var workbook = Workbook.Open(bytes);
        Assert.Equal(bytes, workbook.ToArray());
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Editing_a_cell_keeps_every_other_part_byte_for_byte(string file)
    {
        var bytes = Fixtures.Read(file);
        var workbook = Workbook.Open(bytes);
        Assert.SkipWhen(workbook.Worksheets.Count == 0, "The workbook has no worksheets.");
        var sheet = workbook.Worksheets[0];
        sheet["A1"].SetValue(42);
        var saved = workbook.ToArray();
        var before = Parts(bytes);
        var after = Parts(saved);
        var changed = after.Where(p => !before.TryGetValue(p.Key, out var old) || !old.AsSpan().SequenceEqual(p.Value)).Select(p => p.Key).ToHashSet();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        var allowed = new HashSet<string> { "[Content_Types].xml", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", sheet.Part };
        Assert.DoesNotContain(changed, p => !allowed.Contains(p));
        Assert.All(removed, r => Assert.EndsWith("calcChain.xml", r, StringComparison.Ordinal));
        Assert.Equal(before.Keys.Where(k => !removed.Contains(k)), after.Keys.Take(before.Count - removed.Count));

        // The saved workbook opens again with the value, and Excel recalculates when it opens it.
        var reopened = Workbook.Open(saved);
        Assert.Equal(42.0, reopened.Worksheets[0]["A1"].NumberValue);
        Assert.Equal("1", (string?)Xml(saved, "xl/workbook.xml").Root!.Element(XName.Get("calcPr", Xml(saved, "xl/workbook.xml").Root!.Name.NamespaceName))?.Attribute("fullCalcOnLoad"));
    }

    [Fact]
    public void Writes_values_keeping_the_style()
    {
        var workbook = Workbook.Open(Template);
        var sheet = workbook.Worksheets["Data"];
        sheet["A2"].SetValue("Zulu");
        sheet["B2"].SetValue(12.5);
        sheet["B3"].SetValue(7);
        sheet["B4"].SetValue(1234567890123L);
        sheet["B5"].SetValue(0.1m);
        sheet["C2"].SetValue(new DateTime(2027, 2, 3));
        sheet["D2"].SetValue(false);
        sheet["H5"].SetValue(new DateTime(2026, 11, 23, 14, 15, 16));
        sheet["H6"].SetFormula("=SUM(B2:B6)");
        sheet["H7"].SetValue("=not a formula");
        sheet["H8"].SetValue("Name");
        sheet["A9"].Clear();
        sheet["H9"].SetValue("  spaced\ttext\nwith _x0041_ and \u0001  ");
        var saved = workbook.ToArray();

        var reopened = Workbook.Open(saved).Worksheets["Data"];
        Assert.Equal("Zulu", reopened["A2"].Value);
        Assert.Equal(12.5, reopened["B2"].Value);
        Assert.Equal("12.50", reopened["B2"].GetDisplayText());
        Assert.Equal(7.0, reopened["B3"].Value);
        Assert.Equal(1234567890123.0, reopened["B4"].Value);
        Assert.Equal(0.1, reopened["B5"].Value);
        Assert.Equal(new DateTime(2027, 2, 3), reopened["C2"].Value);
        Assert.Equal("yyyy-mm-dd", reopened["C2"].NumberFormat);
        Assert.Equal(false, reopened["D2"].Value);
        Assert.Equal(new DateTime(2026, 11, 23, 14, 15, 16), reopened["H5"].Value);
        Assert.Equal("yyyy-mm-dd h:mm", reopened["H5"].NumberFormat);
        Assert.Equal("SUM(B2:B6)", reopened["H6"].Formula);
        Assert.Equal(CellValueType.Empty, reopened["H6"].Type);
        Assert.Equal("=not a formula", reopened["H7"].Value);
        Assert.False(reopened["H7"].HasFormula);
        Assert.Equal(CellValueType.Empty, reopened["A9"].Type);
        Assert.Equal("  spaced\ttext\nwith _x0041_ and \u0001  ", reopened["H9"].Value);

        // The shared string "Name" is reused; new strings are appended.
        var strings = Xml(saved, "xl/sharedStrings.xml").Root!;
        Assert.Single(strings.Elements(), e => e.Value == "Name");
        Assert.Equal(strings.Elements().Count().ToString(CultureInfo.InvariantCulture), (string?)strings.Attribute("uniqueCount"));

        // Written cells keep their style: B2 its number format and so its money format.
        var cells = Xml(saved, "xl/worksheets/sheet1.xml").Descendants(XName.Get("c", Main)).ToDictionary(c => (string)c.Attribute("r")!);
        Assert.Equal((string?)Xml(Template, "xl/worksheets/sheet1.xml").Descendants(XName.Get("c", Main)).First(c => (string?)c.Attribute("r") == "B2").Attribute("s"), (string?)cells["B2"].Attribute("s"));
    }

    [Fact]
    public void Writing_a_date_adds_a_date_style_once()
    {
        var workbook = Workbook.Create();
        var sheet = workbook.Worksheets[0];
        sheet["A1"].SetValue(new DateTime(2026, 11, 23));
        sheet["A2"].SetValue(new DateTime(2026, 11, 24));
        sheet["A3"].SetValue(new DateTime(1900, 1, 1));
        sheet["A4"].SetValue(new DateTime(1900, 3, 1));
        Assert.Equal(1.0, sheet["A3"].NumberValue);
        Assert.Equal(61.0, sheet["A4"].NumberValue);
        Assert.Equal(new DateTime(1900, 3, 1), sheet["A4"].DateTimeValue);
        Assert.Equal(46349.0, sheet["A1"].NumberValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet["A5"].SetValue(new DateTime(1899, 12, 1)));
        var styles = Xml(workbook.ToArray(), "xl/styles.xml").Root!;
        Assert.Equal(2, styles.Element(XName.Get("cellXfs", Main))!.Elements().Count());

        // Around Excel's 29 February 1900, which does not exist: serial 60.
        sheet["A6"].SetValue(new DateTime(1900, 2, 28));
        Assert.Equal(59.0, sheet["A6"].NumberValue);
        Assert.Equal(new DateTime(1900, 2, 28), Cell.Date(59, false));
        Assert.Equal(new DateTime(1900, 2, 28), Cell.Date(60, false));
        Assert.Equal(new DateTime(1900, 3, 1), Cell.Date(61, false));
        Assert.Equal(new DateTime(1904, 1, 2), Cell.Date(1, true));
    }

    [Fact]
    public void Number_formats_can_be_set()
    {
        var workbook = Workbook.Create();
        var cell = workbook.Worksheets[0]["B2"];
        cell.SetValue(1234.5);
        cell.NumberFormat = "0.00%";
        Assert.Equal("123450.00%", cell.GetDisplayText());
        cell.NumberFormat = "#,##0.000 \"kg\"";
        Assert.Equal("1,234.500 kg", cell.GetDisplayText());
        Assert.Equal("1.234,500 kg", cell.GetDisplayText(CultureInfo.GetCultureInfo("de-DE")));
        workbook.Worksheets[0]["B3"].NumberFormat = "#,##0.000 \"kg\"";
        var styles = Xml(workbook.ToArray(), "xl/styles.xml").Root!;
        Assert.Single(styles.Element(XName.Get("numFmts", Main))!.Elements(), e => (string?)e.Attribute("formatCode") == "#,##0.000 \"kg\"");
        Assert.Equal("1", (string?)styles.Element(XName.Get("numFmts", Main))!.Attribute("count"));
    }

    [Fact]
    public void Writing_rejects_what_a_cell_cannot_hold()
    {
        var sheet = Workbook.Create().Worksheets[0];
        Assert.Throws<ArgumentException>(() => sheet["A1"].SetValue(double.NaN));
        Assert.Throws<ArgumentException>(() => sheet["A1"].SetValue(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => sheet["A1"].SetValue(new string('x', 32768)));
        Assert.Throws<ArgumentException>(() => sheet["A1"].SetFormula("="));
        Assert.Throws<ArgumentException>(() => sheet["A1"].SetFormula("A1\u0001"));
        Assert.Throws<ArgumentException>(() => sheet["A0"]);
        Assert.Throws<ArgumentException>(() => sheet["A1:B2"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Cell(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Cell(1, 16385));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Row(1048577));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Column(0));
    }

    [Fact]
    public void Array_formulas_are_changed_only_as_a_whole()
    {
        var workbook = Workbook.Open(Template);
        var sheet = workbook.Worksheets["Data"];
        Assert.Throws<InvalidOperationException>(() => sheet["F3"].SetValue(1));
        Assert.Throws<InvalidOperationException>(() => sheet["F2"].SetValue(1));
        Assert.Equal(100.0, sheet["F2"].Value);
    }

    [Fact]
    public void Writing_to_the_first_cell_of_a_shared_formula_keeps_the_others()
    {
        var workbook = Workbook.Open(Template);
        var sheet = workbook.Worksheets["Data"];
        sheet["E2"].SetValue(1);
        Assert.Null(sheet["E2"].Formula);
        Assert.Equal("B3*2", sheet["E3"].Formula);
        Assert.Equal("B6*2", sheet["E6"].Formula);
        var reopened = Workbook.Open(workbook.ToArray()).Worksheets["Data"];
        Assert.Equal("B4*2", reopened["E4"].Formula);
        Assert.DoesNotContain(Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Descendants(XName.Get("f", Main)), f => (string?)f.Attribute("t") == "shared");
    }

    [Fact]
    public void New_cells_take_their_row_or_column_style()
    {
        var bytes = XlsxToPdfTests.Workbook("<row r=\"2\" s=\"1\" customFormat=\"1\"/>",
            styles: "<cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"10\" applyNumberFormat=\"1\"/><xf numFmtId=\"2\" applyNumberFormat=\"1\"/></cellXfs>",
            sheetBefore: "<cols><col min=\"3\" max=\"4\" width=\"12\" style=\"2\" customWidth=\"1\"/></cols>");
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        sheet["A2"].SetValue(0.5);
        sheet["C5"].SetValue(0.5);
        sheet["A5"].SetValue(0.5);
        Assert.Equal("50.00%", sheet["A2"].GetDisplayText());
        Assert.Equal("0.50", sheet["C5"].GetDisplayText());
        Assert.Equal("0.5", sheet["A5"].GetDisplayText());
    }

    [Fact]
    public void Cells_are_listed_row_by_row()
    {
        var sheet = Workbook.Open(Template).Worksheets["Other Sheet"];
        Assert.Equal(["A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9"], sheet.Cells.Select(c => c.Address));
        Assert.Equal(9, sheet.LastRow);
        Assert.Equal(1, sheet.LastColumn);
        Assert.Equal(["A9:C9"], Workbook.Open(Template).Worksheets["Data"].MergedRanges);
        var empty = Workbook.Create().Worksheets[0];
        Assert.Equal(0, empty.LastRow);
        Assert.Empty(empty.Cells);
    }

    [Fact]
    public void Finds_cells_by_their_text()
    {
        var workbook = Workbook.Open(Template);
        var data = workbook.Worksheets["Data"];
        Assert.Equal(["A3"], data.Find("bravo").Select(c => c.Address));
        Assert.Empty(data.Find("bravo", new FindOptions { MatchCase = true }));
        Assert.Equal(["A2", "A4", "A6", "A9"], data.Find("^(Alpha|Charlie|Echo|Merged)$", new FindOptions { Match = FindMatch.Regex }).Select(c => c.Address));
        Assert.Empty(data.Find("alph", new FindOptions { Match = FindMatch.WholeCell }));
        Assert.Equal(["A2"], data.Find("ALPHA", new FindOptions { Match = FindMatch.WholeCell }).Select(c => c.Address));
        Assert.Equal(["B4"], data.Find("30.00", new FindOptions { Match = FindMatch.WholeCell }).Select(c => c.Address));
        Assert.Equal(["C3"], data.Find("2026-01-02").Select(c => c.Address));
        Assert.Equal(["B4"], data.Find("30,00", new FindOptions { Culture = CultureInfo.GetCultureInfo("de-DE") }).Select(c => c.Address));
        Assert.Equal(["B7"], data.Find("=SUM(", new FindOptions { LookIn = FindLookIn.Formulas }).Select(c => c.Address));
        Assert.Equal(["B4"], data.Find("30", new FindOptions { LookIn = FindLookIn.Formulas, Match = FindMatch.WholeCell }).Select(c => c.Address));
        Assert.Equal(["Data!A1", "Other Sheet!A1", "Other Sheet!A4"], workbook.Find("^(Name|Other)$", new FindOptions { Match = FindMatch.Regex }).Select(c => c.ToString()));
        Assert.Equal(["Secret!A1"], workbook.Find("secret").Select(c => c.ToString()));
        Assert.Throws<ArgumentException>(() => workbook.Find(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => workbook.Find("(", new FindOptions { Match = FindMatch.Regex }));
    }

    [Fact]
    public void Rows_and_columns_tell_whether_they_are_hidden_and_grouped()
    {
        var workbook = Workbook.Open(Template);
        var sheet = workbook.Worksheets["Data"];
        Assert.True(sheet.Row(8).IsHidden);
        Assert.False(sheet.Row(7).IsHidden);
        Assert.False(sheet.Row(500).IsHidden);
        Assert.Equal(1, sheet.Row(10).OutlineLevel);
        Assert.Equal(0, sheet.Row(9).OutlineLevel);
        Assert.True(sheet.Column(7).IsHidden);
        Assert.False(sheet.Column(6).IsHidden);
        Assert.Equal("G", sheet.Column(7).Name);

        sheet.Row(8).IsHidden = false;
        sheet.Row(3).IsHidden = true;
        sheet.Row(600).IsHidden = true;
        sheet.Row(4).OutlineLevel = 2;
        sheet.Row(5).IsCollapsed = true;
        sheet.Row(6).Height = 30;
        sheet.Column(7).IsHidden = false;
        sheet.Column(2).IsHidden = true;
        sheet.Column(3).OutlineLevel = 1;
        sheet.Column(4).Width = 20;
        sheet.Column(5).IsCollapsed = true;
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Row(1).OutlineLevel = 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Row(1).Height = 410);
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Column(1).Width = -1);

        var reopened = Workbook.Open(workbook.ToArray()).Worksheets["Data"];
        Assert.False(reopened.Row(8).IsHidden);
        Assert.True(reopened.Row(3).IsHidden);
        Assert.True(reopened.Row(600).IsHidden);
        Assert.Equal(2, reopened.Row(4).OutlineLevel);
        Assert.True(reopened.Row(5).IsCollapsed);
        Assert.Equal(30, reopened.Row(6).Height);
        Assert.Null(reopened.Row(7).Height);
        Assert.False(reopened.Column(7).IsHidden);
        Assert.True(reopened.Column(2).IsHidden);
        Assert.False(reopened.Column(1).IsHidden);
        Assert.Equal(1, reopened.Column(3).OutlineLevel);
        Assert.Equal(20, reopened.Column(4).Width);
        Assert.True(reopened.Column(5).IsCollapsed);
        var format = Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Root!.Element(XName.Get("sheetFormatPr", Main))!;
        Assert.Equal("2", (string?)format.Attribute("outlineLevelRow"));
        Assert.Equal("1", (string?)format.Attribute("outlineLevelCol"));
    }

    [Fact]
    public void Column_ranges_are_split_to_change_one_column()
    {
        var bytes = XlsxToPdfTests.Workbook(string.Empty, sheetBefore: "<cols><col min=\"2\" max=\"6\" width=\"15\" customWidth=\"1\"/></cols>");
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        sheet.Column(4).IsHidden = true;
        Assert.Equal([15.0, 15.0, 15.0, 15.0, 15.0], Enumerable.Range(2, 5).Select(c => sheet.Column(c).Width ?? 0));
        Assert.Equal([false, false, true, false, false], Enumerable.Range(2, 5).Select(c => sheet.Column(c).IsHidden));
        var cols = Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Descendants(XName.Get("col", Main)).Select(c => $"{c.Attribute("min")!.Value}-{c.Attribute("max")!.Value}");
        Assert.Equal(["2-3", "4-4", "5-6"], cols);
        sheet.Column(5).IsHidden = true;
        cols = Xml(workbook.ToArray(), "xl/worksheets/sheet1.xml").Descendants(XName.Get("col", Main)).Select(c => $"{c.Attribute("min")!.Value}-{c.Attribute("max")!.Value}");
        Assert.Equal(["2-3", "4-4", "5-5", "6-6"], cols);
    }

    [Fact]
    public void Rows_hidden_by_default_are_hidden_unless_listed()
    {
        var bytes = XlsxToPdfTests.Workbook("<row r=\"2\"><c r=\"A2\"><v>1</v></c></row>", sheetBefore: "<sheetFormatPr defaultRowHeight=\"15\" zeroHeight=\"1\"/>");
        var workbook = Workbook.Open(bytes);
        var sheet = workbook.Worksheets[0];
        Assert.True(sheet.Row(1).IsHidden);
        Assert.False(sheet.Row(2).IsHidden);
        sheet.Row(1).IsHidden = false;
        Assert.False(Workbook.Open(workbook.ToArray()).Worksheets[0].Row(1).IsHidden);
    }

    [Fact]
    public void Sheets_can_be_hidden_but_not_all_of_them()
    {
        var workbook = Workbook.Open(Template);
        workbook.Worksheets["Data"].IsHidden = true;
        workbook.Worksheets["Secret"].IsHidden = false;
        var reopened = Workbook.Open(workbook.ToArray());
        Assert.True(reopened.Worksheets["Data"].IsHidden);
        Assert.False(reopened.Worksheets["Secret"].IsHidden);
        Assert.Equal("Other Sheet", reopened.ActiveWorksheet!.Name);

        var single = Workbook.Create();
        Assert.Throws<InvalidOperationException>(() => single.Worksheets[0].IsHidden = true);
    }

    [Fact]
    public void Creates_a_new_workbook_with_worksheets()
    {
        var workbook = Workbook.Create();
        Assert.Equal(["Sheet1"], workbook.Worksheets.Select(s => s.Name));
        workbook.Worksheets[0]["A1"].SetValue("Hello");
        var second = workbook.Worksheets.Add("Second sheet");
        second["B2"].SetValue(2.5);
        second["B3"].SetFormula("B2*2");
        Assert.Throws<ArgumentException>(() => workbook.Worksheets.Add("sheet1"));
        Assert.Throws<ArgumentException>(() => workbook.Worksheets.Add("Bad/name"));
        Assert.Throws<ArgumentException>(() => workbook.Worksheets.Add(new string('x', 32)));
        Assert.Throws<ArgumentException>(() => workbook.Worksheets.Add(string.Empty));
        var saved = workbook.ToArray();
        var reopened = Workbook.Open(saved);
        Assert.Equal(["Sheet1", "Second sheet"], reopened.Worksheets.Select(s => s.Name));
        Assert.Equal("Hello", reopened.Worksheets[0]["A1"].Value);
        Assert.Equal(2.5, reopened.Worksheets[1]["B2"].Value);
        Assert.Equal("B2*2", reopened.Worksheets[1]["B3"].Formula);
        Assert.Contains("worksheet+xml", Encoding.UTF8.GetString(Parts(saved)["[Content_Types].xml"]), StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_workbooks_are_written_in_their_namespace()
    {
        var workbook = Workbook.Open(Fixtures.Read(Path.Combine("Workbooks", "Poi", "SampleSS.strict.xlsx")));
        var sheet = workbook.Worksheets[0];
        sheet["A1"].SetValue("Strict text");
        sheet["A2"].SetValue(new DateTime(2026, 11, 23));
        var saved = workbook.ToArray();
        var reopened = Workbook.Open(saved).Worksheets[0];
        Assert.Equal("Strict text", reopened["A1"].Value);
        Assert.Equal(new DateTime(2026, 11, 23), reopened["A2"].Value);
        Assert.All(Xml(saved, "xl/worksheets/sheet1.xml").Descendants().Where(e => e.Name.LocalName == "c"),
            c => Assert.Equal("http://purl.oclc.org/ooxml/spreadsheetml/main", c.Name.NamespaceName));
    }

    [Fact]
    public void Macro_workbooks_keep_their_macros()
    {
        var bytes = Fixtures.Read(Path.Combine("Workbooks", "Poi", "SimpleMacro.xlsm"));
        var workbook = Workbook.Open(bytes);
        workbook.Worksheets[0]["C3"].SetValue("Edited");
        var saved = workbook.ToArray();
        var vba = Parts(bytes).Keys.Single(k => k.EndsWith("vbaProject.bin", StringComparison.Ordinal));
        Assert.Equal(Parts(bytes)[vba], Parts(saved)[vba]);
        Assert.Contains("macroEnabled", Encoding.UTF8.GetString(Parts(saved)["[Content_Types].xml"]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_calculation_chain_goes_when_cells_change()
    {
        var bytes = XlsxToPdfTests.Package(
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/calcChain.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml\"/></Types>"),
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr calcId=\"191029\"/></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain\" Target=\"calcChain.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\"><f>1+1</f><v>2</v></c></row></sheetData></worksheet>"),
            ("xl/calcChain.xml", $"<calcChain xmlns=\"{Main}\"><c r=\"A1\" i=\"1\"/></calcChain>"));
        var workbook = Workbook.Open(bytes);
        workbook.Worksheets[0].Row(1).IsHidden = true;
        Assert.Contains("xl/calcChain.xml", Parts(workbook.ToArray()).Keys);

        workbook.Worksheets[0]["B1"].SetValue(1);
        var saved = workbook.ToArray();
        var parts = Parts(saved);
        Assert.DoesNotContain("xl/calcChain.xml", parts.Keys);
        Assert.DoesNotContain("calcChain", Encoding.UTF8.GetString(parts["xl/_rels/workbook.xml.rels"]), StringComparison.Ordinal);
        Assert.DoesNotContain("calcChain", Encoding.UTF8.GetString(parts["[Content_Types].xml"]), StringComparison.Ordinal);
        var calc = Xml(saved, "xl/workbook.xml").Descendants(XName.Get("calcPr", Main)).Single();
        Assert.Equal("1", (string?)calc.Attribute("fullCalcOnLoad"));
        Assert.Equal("191029", (string?)calc.Attribute("calcId"));
    }

    [Fact]
    public void Saving_is_deterministic()
    {
        byte[] Edit()
        {
            var workbook = Workbook.Open(Template);
            workbook.Worksheets["Data"]["A2"].SetValue("Same");
            workbook.Worksheets["Data"].InsertRows(2);
            workbook.Worksheets.Add("Added");
            return workbook.ToArray();
        }

        Assert.Equal(Edit(), Edit());
    }

    [Fact]
    public void Saves_to_files_and_streams()
    {
        var workbook = Workbook.Open(Template);
        workbook.Worksheets[0]["A1"].SetValue("File");
        var path = Path.Combine(Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.xlsx");
        try
        {
            workbook.Save(path);
            Assert.Equal("File", Workbook.Open(path).Worksheets[0]["A1"].Value);
            using var stream = new MemoryStream();
            workbook.Save(stream);
            stream.Position = 0;
            Assert.Equal("File", Workbook.Open(stream).Worksheets[0]["A1"].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Converts_a_changed_workbook_to_pdf()
    {
        var workbook = Workbook.Open(Template);
        workbook.Worksheets["Data"]["A2"].SetValue("Converted value");
        var pdf = XlsxToPdf.ConvertToBytes(workbook, new XlsxConverterOptions { SheetNames = ["Data"] });
        var text = string.Concat(new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)).Replace("\n", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("Convertedvalue", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel_97_2003_workbooks_are_read_only()
    {
        var bytes = Fixtures.Read(Path.Combine("Sheets", "Xls", "SampleSS.xls"));
        var workbook = Workbook.Open(bytes);
        Assert.Equal(WorkbookFormat.Xls, workbook.Format);
        Assert.True(workbook.IsReadOnly);
        Assert.NotEmpty(workbook.Worksheets);
        var sheet = workbook.Worksheets[0];
        Assert.NotEmpty(sheet.Cells);
        Assert.Throws<NotSupportedException>(() => sheet["A1"].SetValue("x"));
        Assert.Throws<NotSupportedException>(() => sheet.InsertRows(1));
        Assert.Throws<NotSupportedException>(() => sheet.Row(1).IsHidden = true);
        Assert.Throws<NotSupportedException>(() => workbook.ToArray());
        Assert.Throws<NotSupportedException>(() => workbook.Worksheets.Add("New"));
        using var csv = new MemoryStream();
        workbook.SaveAsCsv(csv);
        Assert.NotEmpty(csv.ToArray());
        var pdf = XlsxToPdf.ConvertToBytes(workbook);
        Assert.Equal(XlsToPdf.ConvertToBytes(bytes).Length > 0, pdf.Length > 0);
    }

    [Fact]
    public void Excel_97_2003_values_and_formats_are_read()
    {
        var workbook = Workbook.Open(Fixtures.Read(Path.Combine("Sheets", "Xls", "DateFormats.xls")));
        var dates = workbook.Worksheets.SelectMany(s => s.Cells).Where(c => c.Type == CellValueType.DateTime).ToList();
        Assert.NotEmpty(dates);
        var hidden = Workbook.Open(Fixtures.Read(Path.Combine("Sheets", "Xls", "TwoSheetsOneHidden.xls")));
        Assert.Contains(hidden.Worksheets, s => s.IsHidden);
    }

    [Fact]
    public void Delimited_text_is_read_only_text()
    {
        var bytes = Encoding.UTF8.GetBytes("Name;Amount\r\nAlpha;1,5\r\n\"Quoted; field\";007\r\n");
        var workbook = Workbook.Open(bytes);
        Assert.Equal(WorkbookFormat.Csv, workbook.Format);
        var sheet = workbook.Worksheets[0];
        Assert.Equal("Amount", sheet["B1"].Value);
        Assert.Equal("1,5", sheet["B2"].Value);
        Assert.Equal("007", sheet["B3"].Value);
        Assert.Equal("Quoted; field", sheet["A3"].Value);
        Assert.Equal(["A3"], workbook.Find("quoted").Select(c => c.Address));
        Assert.Throws<NotSupportedException>(() => workbook.Save(new MemoryStream()));
        var pdf = XlsxToPdf.ConvertToBytes(workbook);
        Assert.Contains("Alpha", string.Join("\n", new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)), StringComparison.Ordinal);

        var tabs = Workbook.Open(Encoding.UTF8.GetBytes("a|b\n1|2"), new WorkbookOptions { CsvDelimiter = '|' });
        Assert.Equal("b", tabs.Worksheets[0]["B1"].Value);
        Assert.Throws<ArgumentException>(() => Workbook.Open(bytes, new WorkbookOptions { CsvDelimiter = '"' }));
    }

    [Fact]
    public void Malformed_packages_throw_the_format_exception()
    {
        Assert.Throws<SpreadsheetFormatException>(() => Workbook.Open("PK\u0003\u0004 broken"u8.ToArray()));
        Assert.Throws<SpreadsheetFormatException>(() => Workbook.Open(XlsxToPdfTests.Package(("a.txt", "x"))));
        Assert.Throws<SpreadsheetFormatException>(() => Workbook.Open(XlsxToPdfTests.Workbook("<row><c>")));
        var dtd = XlsxToPdfTests.Workbook("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&xxe;</t></is></c></row>")
            .AsSpan().ToArray();
        Assert.Throws<SpreadsheetFormatException>(() => Workbook.Open(XlsxToPdfTests.Package(
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", "<!DOCTYPE workbook [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><workbook xmlns=\"" + Main + "\"/>"))));
        Assert.Throws<SpreadsheetFormatException>(() => Workbook.Open(dtd));
        Assert.Throws<NotSupportedException>(() => Workbook.Open(XlsxToPdfTests.Package(
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.bin\"/></Relationships>"))));
        Assert.Throws<NotSupportedException>(() => Workbook.Open(Fixtures.Read(Path.Combine("Sheets", "Xls", "password.xls"))));
    }

    [Fact]
    public void Limits_bound_what_a_workbook_may_take()
    {
        var limits = ResourceLimits.Default;
        var cells = string.Concat(Enumerable.Range(1, 50).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\"><v>{r}</v></c><c r=\"B{r}\"><v>{r}</v></c></row>"));
        var bytes = XlsxToPdfTests.Workbook(cells);
        Assert.Equal("MaxTableCells", Assert.Throws<ResourceLimitExceededException>(() => Workbook.Open(bytes, new WorkbookOptions { Limits = limits with { MaxTableCells = 99 } })).LimitName);
        Assert.Equal("MaxElements", Assert.Throws<ResourceLimitExceededException>(() => Workbook.Open(bytes, new WorkbookOptions { Limits = limits with { MaxElements = 100 } })).LimitName);
        Assert.Equal("MaxDecompressedBytes", Assert.Throws<ResourceLimitExceededException>(() => Workbook.Open(bytes, new WorkbookOptions { Limits = limits with { MaxDecompressedBytes = 1000 } })).LimitName);
        var deep = XlsxToPdfTests.Workbook(string.Empty, sheetExtra: "<extLst>" + string.Concat(Enumerable.Repeat("<a>", 300)) + string.Concat(Enumerable.Repeat("</a>", 300)) + "</extLst>");
        Assert.Equal("MaxNestingDepth", Assert.Throws<ResourceLimitExceededException>(() => Workbook.Open(deep)).LimitName);
        Assert.Equal("MaxTableCells", Assert.Throws<ResourceLimitExceededException>(() => Workbook.Open(Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n"), new WorkbookOptions { Limits = limits with { MaxTableCells = 4 } })).LimitName);
        Assert.Equal("Timeout", Assert.Throws<ResourceLimitExceededException>(() => Workbook.Open(bytes, new WorkbookOptions { Limits = limits with { Timeout = TimeSpan.FromTicks(1) } })).LimitName);
        Assert.Throws<ArgumentException>(() => Workbook.Open(bytes, new WorkbookOptions { Limits = null! }));
    }
}
