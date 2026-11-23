using System.Globalization;
using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;
using static RaiCore.Pdf.Tests.Sheets.BiffBuilder;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// Converting Excel 97-2003 workbooks (BIFF8, also BIFF5) to PDF: the Apache POI corpus compared with xlrd, and
/// hand-made streams for every record the reader uses, damaged and hostile data.
/// </summary>
public class XlsToPdfTests
{
    private static readonly Lazy<JsonElement> ExpectedJson = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Sheets/Xls/expected.json"))).RootElement);

    public static TheoryData<string> CorpusFiles => [.. ExpectedJson.Value.EnumerateObject().Select(p => p.Name)];

    private static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Sheets", "Xls", name));

    private static string AllText(byte[] pdf) => string.Join("\n", new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text));

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    private static Sheet Single(byte[] xls, XlsxConverterOptions? options = null) => Assert.Single(XlsConverter.Read(xls, options));

    private static string[] Texts(Sheet sheet, int row) => [.. Enumerable.Range(0, sheet.ColumnCount).Select(c => sheet[row, c].Text)];

    /// <summary>The cells of a sheet as stored (the last record of a cell wins), with the worksheet's layout.</summary>
    private static (XlsxWorksheet Sheet, Dictionary<(int, int), (int Xf, XlsValue Value)> Cells) Raw(XlsWorkbook workbook, int index)
    {
        var sheet = new XlsxWorksheet();
        var cells = new Dictionary<(int, int), (int, XlsValue)>();
        XlsWorksheet.Read(workbook, workbook.SheetOffsets[index], sheet, null, (row, column, xf, value) => cells[(row, column)] = (xf, value));
        return (sheet, cells);
    }

    private static XlsWorkbook Open(byte[] data) => XlsConverter.Open(data, ResourceLimits.Default, CultureInfo.InvariantCulture, null).Workbook;

    // ---- The corpus, compared with xlrd ----

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Corpus_files_read_as_xlrd_reads_them(string name)
    {
        var data = Fixture(name);
        var expected = ExpectedJson.Value.GetProperty(name);
        if (expected.TryGetProperty("error", out var error))
        {
            var exception = Record.Exception(() => XlsToPdf.ConvertToBytes(data));
            if (error.GetString()!.Contains("encrypted", StringComparison.Ordinal))
            {
                Assert.Contains("encrypted", Assert.IsType<NotSupportedException>(exception).Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.IsType<SpreadsheetFormatException>(exception);
            }

            return;
        }

        var biff = expected.GetProperty("biff").GetInt32();
        if (biff < 50)
        {
            Assert.Contains("Excel 2.x-4.0", Assert.Throws<NotSupportedException>(() => XlsToPdf.ConvertToBytes(data)).Message, StringComparison.Ordinal);
            return;
        }

        var workbook = Open(data);
        Assert.Equal(biff / 10, workbook.Biff);
        Assert.Equal(expected.GetProperty("datemode").GetInt32() == 1, workbook.Settings.Date1904);
        foreach (var format in expected.GetProperty("formats").EnumerateObject())
        {
            Assert.Equal(format.Value.GetString(), workbook.Styles.NumberFormats[int.Parse(format.Name, CultureInfo.InvariantCulture)]);
        }

        foreach (var sheet in expected.GetProperty("sheets").EnumerateArray())
        {
            var index = workbook.Sheets.FindIndex(s => s.Name == sheet.GetProperty("name").GetString());
            Assert.True(index >= 0, $"{name}: no sheet {sheet.GetProperty("name")}");
            Assert.Equal(sheet.GetProperty("hidden").GetBoolean(), workbook.Sheets[index].Hidden);
            var (layout, cells) = Raw(workbook, index);
            var expectedCells = sheet.GetProperty("cells").EnumerateArray().ToList();
            foreach (var cell in expectedCells)
            {
                var (row, column) = (cell[0].GetInt32(), cell[1].GetInt32());
                Assert.True(cells.TryGetValue((row, column), out var actual), $"{name}: no cell at {row},{column}");
                Assert.Equal(cell[4].GetInt32(), actual.Xf);
                var value = actual.Value;
                switch (cell[2].GetString())
                {
                    case "text":
                        Assert.Equal((XlsValueKind.Text, cell[3].GetString()), (value.Kind, value.Text));
                        break;
                    case "number":
                        Assert.Equal((XlsValueKind.Number, cell[3].GetDouble()), (value.Kind, value.Number));
                        break;
                    case "boolean":
                        Assert.Equal((XlsValueKind.Boolean, cell[3].GetDouble()), (value.Kind, value.Number));
                        break;
                    default:
                        Assert.Equal((XlsValueKind.Error, cell[3].GetString()), (value.Kind, value.Text));
                        break;
                }
            }

            if (expectedCells.Count < 400)
            {
                Assert.Equal(expectedCells.Count, cells.Values.Count(c => c.Value.Kind != XlsValueKind.Blank));
            }

            Assert.Equal(
                sheet.GetProperty("merged").EnumerateArray().Select(m => (m[0].GetInt32(), m[1].GetInt32(), m[2].GetInt32(), m[3].GetInt32())).Order().ToList(),
                layout.Merges.Select(m => (m.Row1, m.Column1, m.Row2, m.Column2)).Order().ToList());
            Assert.Equal(
                sheet.GetProperty("hiddenRows").EnumerateArray().Select(r => r.GetInt32()).ToList(),
                layout.Rows.Where(r => r.Value.Hidden).Select(r => r.Key).Order().ToList());
            var hiddenColumns = sheet.GetProperty("hiddenColumns").EnumerateArray().Select(c => c.GetInt32()).ToHashSet();
            foreach (var width in sheet.GetProperty("columnWidths").EnumerateObject())
            {
                var column = int.Parse(width.Name, CultureInfo.InvariantCulture);
                var info = layout.Columns.Last(c => c.First <= column && column <= c.Last);
                Assert.Equal(width.Value.GetDouble(), info.Width!.Value, 6);
                Assert.Equal(hiddenColumns.Contains(column), info.Hidden);
            }
        }

        var pdf = XlsToPdf.ConvertToBytes(data);
        AssertValid(pdf);
        Assert.True(PdfReader.Open(pdf).Pages.Count >= 1);
    }

    [Fact]
    public void The_corpus_text_is_rendered()
    {
        // The words of the text cells xlrd finds in the printed rows and columns of visible sheets, in the scripts the
        // default font has (Latin, Greek, Cyrillic): long words in narrow columns are broken across lines, so a few are
        // missing from the extracted text.
        long found = 0, total = 0;
        foreach (var file in ExpectedJson.Value.EnumerateObject().Where(f => !f.Value.TryGetProperty("error", out _) && f.Value.GetProperty("biff").GetInt32() >= 50))
        {
            var words = new Dictionary<string, int>();
            foreach (var sheet in file.Value.GetProperty("sheets").EnumerateArray().Where(s => !s.GetProperty("hidden").GetBoolean()))
            {
                var hiddenRows = sheet.GetProperty("hiddenRows").EnumerateArray().Select(r => r.GetInt32()).ToHashSet();
                var hiddenColumns = sheet.GetProperty("hiddenColumns").EnumerateArray().Select(r => r.GetInt32()).ToHashSet();
                foreach (var cell in sheet.GetProperty("cells").EnumerateArray())
                {
                    if (cell[2].GetString() == "text" && !hiddenRows.Contains(cell[0].GetInt32()) && !hiddenColumns.Contains(cell[1].GetInt32()))
                    {
                        foreach (var word in cell[3].GetString()!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(w => w.All(c => c < 0x0530)))
                        {
                            words[word] = words.GetValueOrDefault(word) + 1;
                        }
                    }
                }
            }

            var text = AllText(XlsToPdf.ConvertToBytes(Fixture(file.Name), new XlsxConverterOptions { UsePageSetup = false }));
            var have = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
            found += words.Sum(w => Math.Min(w.Value, have.GetValueOrDefault(w.Key)));
            total += words.Values.Sum();
        }

        Assert.True(total > 1000, $"only {total} words");
        Assert.True(found >= total * 0.97, $"{found} of {total} words");
    }

    [Fact]
    public void Values_are_shown_in_their_number_formats()
    {
        var sheet = XlsConverter.Read(Fixture("Formatting.xls"))[0];
        Assert.Equal(["Dates, all 24th November 2006", string.Empty], Texts(sheet, 0));
        Assert.Equal(["dd/mm/yyyy", "2006-11-24"], Texts(sheet, 1)); // The built-in short date, in the invariant culture.
        Assert.Equal(["yyyy/mm/dd", "2006/11/24"], Texts(sheet, 2));
        Assert.Equal(["dd-mm-yy", "24-11-06"], Texts(sheet, 6));
        Assert.Equal(["nn.nnn", "10.520"], Texts(sheet, 10));
        Assert.Equal(["\u00A3nn.nn", "\u00A310.52"], Texts(sheet, 12));
        Assert.Equal(CellKind.Date, sheet[1, 1].Kind);
        Assert.Equal(CellKind.Number, sheet[10, 1].Kind);
        Assert.True(sheet[0, 0].Format!.Bold);

        var text = AllText(XlsToPdf.ConvertToBytes(Fixture("Formatting.xls")));
        Assert.Contains("24/11/06", text, StringComparison.Ordinal);
        Assert.Contains("\u00A310.52", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbooks_of_the_1904_date_system_show_their_dates()
    {
        var sheets = XlsConverter.Read(Fixture("WrongFormulaRecordType.xls"));
        var sheet = Assert.Single(sheets);

        // 38352 days after 1904-01-01; the formula records of an older type are read too.
        Assert.Equal("Jan 1, 2009", sheet[1, 2].Text);
        Assert.Equal("Jan 2, 2009", sheet[3, 2].Text);
        Assert.Equal(["3", "2"], Texts(sheet, 3)[..2]);
    }

    [Fact]
    public void Excel_95_workbooks_are_read()
    {
        var pdf = XlsToPdf.ConvertToBytes(Fixture("testEXCEL_95.xls"));
        AssertValid(pdf);
        var text = AllText(pdf);
        Assert.Contains("Sample Excel Worksheet - Numbers and their Squares", text, StringComparison.Ordinal);
        Assert.Contains("1,234,500", text, StringComparison.Ordinal);
        Assert.Contains("$1,253.82", text, StringComparison.Ordinal);
        Assert.Contains("155.00%", text, StringComparison.Ordinal);
        var sheet = XlsConverter.Read(Fixture("testEXCEL_95.xls"))[0];
        Assert.True(sheet[0, 0].Format!.Bold);
        Assert.Equal(CellUnderline.Single, sheet[0, 0].Format!.Underline);
    }

    [Fact]
    public void Sheets_and_their_visibility_come_from_the_workbook()
    {
        // Hidden sheets are left out unless they are asked for.
        Assert.Equal("Sheet2A1", Single(Fixture("TwoSheetsOneHidden.xls"))[0, 0].Text);
        Assert.Equal("Sheet1A1", Single(Fixture("TwoSheetsOneHidden.xls"), new XlsxConverterOptions { SheetNames = ["SHEET1"] })[0, 0].Text);
        Assert.Equal(2, XlsConverter.Read(Fixture("TwoSheetsOneHidden.xls"), new XlsxConverterOptions { SheetIndexes = [0, 1] }).Count);
        Assert.Throws<ArgumentException>(() => XlsToPdf.Convert(Fixture("TwoSheetsOneHidden.xls"), new XlsxConverterOptions { SheetNames = ["Nope"] }));

        var multi = XlsToPdf.Convert(Fixture("SampleSS.xls"), new XlsxConverterOptions { ShowSheetNames = true });
        Assert.Equal("Sample Spreadsheet", multi.Info.Title);
        Assert.Equal("RaiCore.Pdf.Sheets", multi.Info.Creator);
        var text = AllText(multi.ToArray());
        Assert.Contains("Sheet Number 2", text, StringComparison.Ordinal);
        Assert.Contains("I'm in bold blue, on a yellow background", text, StringComparison.Ordinal);

        var styled = XlsConverter.Read(Fixture("SampleSS.xls"))[1];
        var format = styled[3, 0].Format!;
        Assert.True(format.Bold);
        Assert.Equal(PdfColor.FromRgb(0, 51, 102), format.Color); // Index 56 of the palette, as xlrd finds too.
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), format.Fill);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), XlsConverter.Read(Fixture("SampleSS.xls"))[0][3, 0].Format!.Color);
    }

    [Fact]
    public void Print_areas_and_titles_are_used()
    {
        var area = XlsConverter.Read(Fixture("SimpleWithPrintArea.xls"))[0];
        Assert.Equal("hi this is some text", area[0, 0].Text);

        // Print titles (rows 2 and 3): the rows repeated at the top of every page, after the first row printed once.
        var sheet = XlsConverter.Read(Fixture("ContinueRecordProblem.xls"))[0];
        Assert.Equal((2, 1), (sheet.HeaderRows, sheet.Formatting!.LeadRows));
        Assert.Equal("data", sheet[0, 0].Text);
        Assert.Equal("HEADER", sheet[2, 0].Text);
        Assert.Equal(0, XlsConverter.Read(Fixture("ContinueRecordProblem.xls"), new XlsxConverterOptions { UsePageSetup = false })[0].HeaderRows);

        // A print area of a sheet: only the visible sheet of this workbook, which has one, is printed.
        var template = Single(Fixture("46250.xls"));
        Assert.Equal(7, template.ColumnCount); // B to H.
        Assert.Equal(17, template.Rows.Count);
    }

    [Fact]
    public void Cyrillic_text_merged_cells_fills_and_hidden_rows_are_shown()
    {
        var pdf = XlsToPdf.ConvertToBytes(Fixture("57925.xls"));
        AssertValid(pdf);
        var text = AllText(pdf);
        Assert.Contains("\u0418\u0441\u0442\u043E\u0447\u043D\u0438\u043A\u0438", text, StringComparison.Ordinal);
        var sheet = XlsConverter.Read(Fixture("57925.xls"))[2];
        Assert.NotEmpty(sheet.Formatting!.Merges);
        Assert.Contains(sheet.Rows.SelectMany(r => r), c => c.Format?.Fill == PdfColor.FromRgb(255, 255, 0));
    }

    [Fact]
    public void Charts_are_left_out()
    {
        var sheet = XlsConverter.Read(Fixture("WithChart.xls"))[0];
        Assert.Equal(["1", "10"], Texts(sheet, 0));
        Assert.Equal(12, sheet.Rows.Sum(r => r.Count(c => c.Kind != CellKind.Empty)));
    }

    [Fact]
    public void Strings_split_across_continue_records_are_read_whole()
    {
        var sheet = XlsConverter.Read(Fixture("46368.xls"))[0];
        var expected = ExpectedJson.Value.GetProperty("46368.xls").GetProperty("sheets")[0].GetProperty("cells");
        foreach (var cell in expected.EnumerateArray().Where(c => c[2].GetString() == "text"))
        {
            Assert.Equal(cell[3].GetString(), sheet[cell[0].GetInt32(), cell[1].GetInt32()].Text);
        }
    }

    [Fact]
    public void XlsxToPdf_converts_Excel_97_2003_workbooks_and_XlsToPdf_converts_xlsx_workbooks()
    {
        Assert.Contains("Start of 2nd sheet", AllText(XlsxToPdf.ConvertToBytes(Fixture("SampleSS.xls"))), StringComparison.Ordinal);
        Assert.NotEmpty(AllText(XlsToPdf.ConvertToBytes(Fixtures.Read("Sheets/minimal.xlsx"))));
        Assert.Throws<NotSupportedException>(() => XlsxToPdf.ConvertToBytes(Fixture("password.xls")));
    }

    [Fact]
    public async Task The_overloads_convert_streams_paths_and_asynchronously()
    {
        var path = Fixtures.Path("Sheets/Xls/SampleSS.xls");
        var bytes = XlsToPdf.ConvertToBytes(File.ReadAllBytes(path));
        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(PdfReader.Open(bytes).Pages.Count, PdfReader.Open(XlsToPdf.ConvertToBytes(stream)).Pages.Count);
        }

        Assert.Equal(PdfReader.Open(bytes).Pages.Count, XlsToPdf.Convert(path).Pages.Count);
        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(PdfReader.Open(bytes).Pages.Count, XlsToPdf.Convert(stream).Pages.Count);
        }

        Assert.Equal(PdfReader.Open(bytes).Pages.Count, (await XlsToPdf.ConvertAsync(File.ReadAllBytes(path), cancellationToken: TestContext.Current.CancellationToken)).Pages.Count);
        await using (var stream = File.OpenRead(path))
        {
            Assert.Equal(PdfReader.Open(bytes).Pages.Count, (await XlsToPdf.ConvertAsync(stream, cancellationToken: TestContext.Current.CancellationToken)).Pages.Count);
        }

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XlsToPdf.ConvertAsync(File.ReadAllBytes(path), cancellationToken: cancel.Token));
        Assert.Throws<ArgumentNullException>(() => XlsToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => XlsToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => XlsToPdf.Convert((string)null!));
    }

    [Fact]
    public void Encrypted_old_and_damaged_files_throw()
    {
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => XlsToPdf.Convert(Fixture("xor-encryption-abc.xls"))).Message, StringComparison.Ordinal);
        Assert.Contains("Excel 2.x-4.0", Assert.Throws<NotSupportedException>(() => XlsToPdf.Convert(Fixture("testEXCEL_4.xls"))).Message, StringComparison.Ordinal);
        Assert.Contains("damaged", Assert.Throws<SpreadsheetFormatException>(() => XlsToPdf.Convert(Fixture("clusterfuzz-testcase-minimized-POIHSSFFuzzer-6537773940867072.xls"))).Message, StringComparison.Ordinal);

        // A compound file that is not a workbook, an encrypted XLSX workbook (a compound file too), and other data.
        Assert.Contains("no Workbook stream", Assert.Throws<SpreadsheetFormatException>(() => XlsToPdf.Convert(Fixtures.Read("Poi/documentProperties.doc"))).Message, StringComparison.Ordinal);
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => XlsToPdf.Convert(Fixtures.Read("Poi/bug53475-password-is-pass.docx"))).Message, StringComparison.Ordinal);
        Assert.Throws<SpreadsheetFormatException>(() => XlsToPdf.Convert("not a workbook"u8.ToArray()));
        Assert.Throws<SpreadsheetFormatException>(() => XlsToPdf.Convert([]));
        Assert.Contains("XlsxToPdf", Assert.Throws<SpreadsheetFormatException>(() => XlsConverter.Read("PK\x03\x04"u8.ToArray())).Message, StringComparison.Ordinal);
    }

    // ---- Hand-made workbook streams ----

    [Fact]
    public void Shared_strings_are_read_across_continue_records()
    {
        var sst = Join(
            U32(5), U32(5),
            U16(5), [0], "Hel"u8.ToArray()); // "Hello": three characters here, the rest in the next record.
        var first = Join(
            [1], Encoding.Unicode.GetBytes("lo"), // The rest, now two bytes a character.
            U16(4), [0x0C], U16(1), U32(4), "Rich"u8.ToArray(), U16(0)); // Rich text with phonetic data: the runs go on.
        var second = Join(U16(1), [1, 2, 3, 4]); // The rest of the run and the phonetic data, with no flags.
        var third = Join(
            U16(4), [0], "Next"u8.ToArray(), // A string that starts a record: its own flags.
            U16(2), [1], Encoding.Unicode.GetBytes("\u0394")); // Delta, and the rest one byte a character.
        var fourth = Join([0], "x"u8.ToArray(), U16(2), [0]); // The header of a string whose characters start the next record.
        var fifth = Join([1], Encoding.Unicode.GetBytes("\u0416b"));
        var data = SingleSheet(
            s => s.Record(0x00FD, Cell(0, 0), U32(0)).Record(0x00FD, Cell(0, 1), U32(1)).Record(0x00FD, Cell(0, 2), U32(2))
                .Record(0x00FD, Cell(0, 3), U32(3)).Record(0x00FD, Cell(0, 4), U32(99)),
            g => g.Record(0x00FC, sst).Record(0x003C, first).Record(0x003C, second).Record(0x003C, third).Record(0x003C, fourth).Record(0x003C, fifth));
        Assert.Equal(["Hello", "Rich", "Next", "\u0394x", "\u0416b"], Open(data).SharedStrings);
        var sheet = Single(data);
        Assert.Equal(["Hello", "Rich", "Next", "\u0394x"], Texts(sheet, 0));
    }

    [Fact]
    public void Cell_records_give_their_values()
    {
        var data = SingleSheet(s => s
            .Record(0x0204, Cell(0, 0), Text("la\0bel")) // LABEL, with a NUL character some writers leave.
            .Record(0x00D6, Cell(0, 1), Text("rich"), U16(1), U16(0), U16(1)) // RSTRING with a formatting run.
            .Record(0x0203, Cell(1, 0), F64(-1.5)) // NUMBER
            .Record(0x027E, Cell(1, 1), U32((123 << 2) | 0x02)) // RK: an integer.
            .Record(0x027E, Cell(1, 2), U32((12345 << 2) | 0x03)) // RK: an integer divided by 100.
            .Record(0x027E, Cell(1, 3), U32((uint)(BitConverter.DoubleToInt64Bits(0.25) >> 32))) // RK: a double.
            .Record(0x027E, Cell(1, 4), U32((uint)(BitConverter.DoubleToInt64Bits(250.0) >> 32) | 0x01)) // RK: a double / 100.
            .Record(0x027E, Cell(1, 5), U32(unchecked((uint)(-7 << 2)) | 0x02)) // RK: a negative integer.
            .Record(0x00BD, U16(2), U16(1), U16(0), U32((7 << 2) | 0x02), U16(0), U32((8 << 2) | 0x02), U16(2)) // MULRK
            .Record(0x0205, Cell(3, 0), [1, 0]) // BOOLERR: TRUE
            .Record(0x0205, Cell(3, 1), [0, 0]) // BOOLERR: FALSE
            .Record(0x0205, Cell(3, 2), [0x07, 1]) // BOOLERR: #DIV/0!
            .Record(0x0205, Cell(3, 3), [0x63, 1]) // BOOLERR: an unknown error
            .Record(0x0006, Cell(4, 0), F64(42), U16(0), U32(0), U16(0)) // FORMULA: a number.
            .Record(0x0006, Cell(4, 1), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0)) // A string, in the STRING record.
            .Record(0x04BC, new byte[8]) // ... after its shared formula.
            .Record(0x0207, Text("formula text"))
            .Record(0x0006, Cell(4, 2), [1, 0, 1, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0)) // TRUE
            .Record(0x0006, Cell(4, 3), [2, 0, 0x2A, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0)) // #N/A
            .Record(0x0006, Cell(4, 4), [3, 0, 0, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0)) // An empty string.
            .Record(0x0006, Cell(4, 5), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0)) // A string without its STRING record.
            .Record(0x0203, Cell(5, 0), F64(double.NaN)) // Not a number.
            .Record(0x0203, Cell(5, 300), F64(1)) // Beyond the last column.
            .Record(0x0006, Cell(6, 0), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0))); // A string at the end of the sheet.
        var sheet = Single(data);
        Assert.Equal(["label", "rich", string.Empty, string.Empty, string.Empty, string.Empty], Texts(sheet, 0));
        Assert.Equal(["-1.5", "123", "123.45", "0.25", "2.5", "-7"], Texts(sheet, 1));
        Assert.Equal(["7", "8"], Texts(sheet, 2)[1..3]);
        Assert.Equal(["TRUE", "FALSE", "#DIV/0!", "#N/A"], Texts(sheet, 3)[..4]);
        Assert.Equal(["42", "formula text", "TRUE", "#N/A", string.Empty, string.Empty], Texts(sheet, 4));
        Assert.Equal("#NUM!", sheet[5, 0].Text);
        Assert.Equal(6, sheet.ColumnCount);
        Assert.Equal(6, sheet.Rows.Count); // The last formula's empty string shows nothing.
        Assert.Equal(CellAlignment.Center, sheet[3, 0].Alignment);
        Assert.Equal(CellAlignment.Center, sheet[3, 2].Alignment);
        Assert.Equal(CellKind.Number, sheet[1, 1].Kind);
        Assert.Equal(CellKind.Text, sheet[0, 0].Kind);
        Assert.True(sheet[1, 1].IsRightAligned);
    }

    [Fact]
    public void Lone_halves_of_surrogate_pairs_are_replaced()
    {
        // Found by fuzzing: text layout cannot shape a lone surrogate, which a damaged or cut string can hold.
        var data = SingleSheet(s => s.Record(0x0204, Cell(0, 0), U16(4), [1], Encoding.Unicode.GetBytes("a"), [0x00, 0xD8], Encoding.Unicode.GetBytes("\U0001F600")));
        Assert.Equal("a\uFFFD\U0001F600", Single(data)[0, 0].Text);
        Assert.Contains("a", AllText(XlsToPdf.ConvertToBytes(Fixtures.Read("Fuzz/Crashes/xls/lone-surrogate.biff"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Columns_fitted_to_text_in_a_larger_font_keep_it_on_one_line()
    {
        // The padding of a cell grows with its font: a column fitted at the default size alone wrapped the text.
        var data = SingleSheet(
            s => s.Record(0x0204, Cell(0, 0, 1), Text("Unbroken")).Record(0x0204, Cell(0, 1), Text("next")),
            g => g.Font("Arial", 10).Font("Arial", 16).Xf().Xf(font: 1));
        Assert.Contains("Unbroken next", AllText(XlsToPdf.ConvertToBytes(data)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x00000002u, 0)]
    [InlineData(0x00000006u, 1)]
    [InlineData(0xFFFFFFFEu, -1)]
    [InlineData(0x00000007u, 0.01)]
    [InlineData(0x3FF00000u, 1)]
    [InlineData(0x3FF00001u, 0.01)]
    [InlineData(0xC0000000u, -2)]
    public void Rk_values_are_decoded(uint rk, double expected) => Assert.Equal(expected, XlsWorksheet.RkValue(rk));

    [Fact]
    public void Formats_give_cells_their_fonts_number_formats_fills_borders_and_alignment()
    {
        var palette = Join([.. Enumerable.Range(0, 56).Select(i => i == 2 ? new byte[] { 0x12, 0x34, 0x56, 0 } : new byte[] { 0, 0, 0, 0 })]);
        var data = SingleSheet(
            s => s.Record(0x0203, Cell(0, 0, 1), F64(0.5)).Record(0x0203, Cell(0, 1, 2), F64(45000)).Record(0x0204, Cell(0, 2, 3), Text("styled"))
                .Record(0x0201, Cell(0, 3, 4)).Record(0x0201, Cell(0, 4, 0)).Record(0x00BE, U16(1), U16(0), U16(4), U16(0), U16(4), U16(2))
                .Record(0x0204, Cell(2, 0, 5), Text("courier")).Record(0x0204, Cell(2, 1, 99), Text("default")),
            g => g
                .Font("Arial", 10).Font("Times New Roman", 14, bold: true, italic: true, underline: 1, strike: true, color: 10)
                .Font("A", 9, underline: 0x22).Font("B").Font("Courier New", 8)
                .Format(164, "0.00%")
                .Record(0x0092, U16(56), palette)
                .Xf()
                .Xf(format: 164)
                .Xf(format: 14)
                .Xf(font: 1, alignment: 0x02 | 0x08, indent: 3) // Centered, wrapped, at the top.
                .Xf(borders: 1 | (2 << 4) | (5 << 8) | (6 << 12) | (10L << 16) | (8L << 23), more: 8 | (8 << 7) | (1L << 26), fill: 10 | (65 << 7))
                .Xf(font: 5, alignment: 0x03 | 0x10)); // Right, in the middle: the fifth font, as font 4 does not exist.
        var sheet = Single(data);
        Assert.Equal("50.00%", sheet[0, 0].Text);
        Assert.Equal(new DateTime(1899, 12, 30).AddDays(45000).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), sheet[0, 1].Text);
        Assert.Equal(CellKind.Date, sheet[0, 1].Kind);

        var font = sheet[0, 2].Format!;
        Assert.Equal(("Times New Roman", 14.0, true, true, CellUnderline.Single, true), (font.FontName, font.FontSize!.Value, font.Bold, font.Italic, font.Underline, font.Strikethrough));
        Assert.Equal(PdfColor.FromRgb(0x12, 0x34, 0x56), font.Color);
        Assert.Equal(CellAlignment.Center, sheet[0, 2].Alignment);
        Assert.Equal((true, 3, VerticalAlignment.Top), (font.Wrap, font.Indent, font.Vertical));

        // A blank cell with a fill and borders is kept; one without is not.
        var boxed = sheet[0, 3].Format!;
        Assert.Equal(PdfColor.FromRgb(0x12, 0x34, 0x56), boxed.Fill);
        Assert.Equal((0.75, PdfColor.FromRgb(0x12, 0x34, 0x56)), (boxed.Left.Width, boxed.Left.Color));
        Assert.Equal((1.5, PdfColor.Black), (boxed.Right.Width, boxed.Right.Color));
        Assert.Equal(2.25, boxed.Top.Width);
        Assert.Equal(BorderStyle.Double, boxed.Bottom.Style);
        Assert.Equal(CellKind.Empty, sheet[0, 3].Kind);
        Assert.Equal(4, sheet.ColumnCount);
        Assert.NotNull(sheet[1, 2].Format?.Fill); // MULBLANK
        Assert.Null(sheet[1, 1].Format?.Fill);

        var courier = sheet[2, 0];
        Assert.Equal(("Courier New", 8.0, CellAlignment.Right, VerticalAlignment.Middle), (courier.Format!.FontName, courier.Format.FontSize!.Value, courier.Alignment, courier.Format.Vertical));
        Assert.Equal("Arial", sheet[2, 1].Format!.FontName); // An XF index out of range: the first.
        Assert.Equal(10, sheet.Formatting!.DefaultFontSize);
        Assert.Equal("Arial", sheet.Formatting.DefaultFontName);
        Assert.Equal(CellUnderline.Double, Open(data).Styles.Fonts[2].Underline);
    }

    [Fact]
    public void Sizes_visibility_merges_and_page_setup_come_from_the_sheet()
    {
        var data = SingleSheet(s => s
            .Record(0x0055, U16(10)) // DEFCOLWIDTH
            .Record(0x007D, U16(0), U16(0), U16(20 * 256), U16(15), U16(0), U16(0)) // COLINFO: 20 characters.
            .Record(0x007D, U16(1), U16(1), U16(5 * 256), U16(15), U16(1), U16(0)) // COLINFO: hidden.
            .Record(0x0208, U16(0), U16(0), U16(3), U16(600), U16(0), U16(0), U16(0x0140), U16(0x000F)) // ROW: 30 pt.
            .Record(0x0208, U16(1), U16(0), U16(3), U16(255), U16(0), U16(0), U16(0x0120), U16(0x000F)) // ROW: hidden.
            .Record(0x0208, U16(2), U16(0), U16(3), U16(400), U16(0), U16(0), U16(0x0100), U16(0x000F)) // ROW: not sized.
            .Record(0x0081, U16(0x0100 | 0x04C1)) // WSBOOL: fit to page.
            .Record(0x00A1, U16(9), U16(75), U16(1), U16(1), U16(0), U16(0), U16(600), U16(600), F64(0.5), F64(0.5), U16(1)) // SETUP
            .Record(0x023E, U16(0x0040 | 0x0696), U16(0), U16(0), U32(64), U16(0), U16(0), U32(0)) // WINDOW2: right to left.
            .Record(0x00E5, U16(1), U16(3), U16(3), U16(0), U16(2)) // MERGEDCELLS
            .Record(0x0203, Cell(0, 0), F64(1)).Record(0x0203, Cell(0, 1), F64(2)).Record(0x0203, Cell(1, 0), F64(3))
            .Record(0x0203, Cell(2, 0), F64(4)).Record(0x0204, Cell(3, 0), Text("merged")).Record(0x0203, Cell(3, 2), F64(5)));
        var sheet = Single(data);
        Assert.Equal(3, sheet.Rows.Count); // Row 1 is hidden.
        Assert.Equal(2, sheet.ColumnCount); // Column B is hidden.
        Assert.Equal(["1", string.Empty], Texts(sheet, 0));
        Assert.Equal("merged", sheet[2, 0].Text);
        var formatting = sheet.Formatting!;
        Assert.Equal([20, 10 + (5 / 7.0)], formatting.ColumnWidths);
        Assert.Equal([30, null, null], formatting.RowHeights);
        Assert.Equal(new CellMerge(2, 0, 1, 2), Assert.Single(formatting.Merges));
        Assert.True(formatting.Landscape);
        Assert.True(formatting.FitToWidth);
        Assert.Equal(1, formatting.Scale);
        Assert.True(formatting.RightToLeft);

        var setupOff = Single(data, new XlsxConverterOptions { UsePageSetup = false, AutoFit = true }).Formatting!;
        Assert.False(setupOff.Landscape);
        Assert.Empty(setupOff.ColumnWidths);

        // Without fitting to the page: the print scale. Without printer settings (fNoPls): neither scale nor orientation.
        var scaled = Single(SingleSheet(s => s
            .Record(0x00A1, U16(9), U16(75), U16(1), U16(1), U16(0), U16(0x02), U16(600), U16(600), F64(0.5), F64(0.5), U16(1))
            .Record(0x023E, U16(0x06B6), U16(0), U16(0), U32(64), U16(0), U16(0), U32(0)) // WINDOW2: left to right.
            .Record(0x0203, Cell(0, 0), F64(1)))).Formatting!;
        Assert.Equal((0.75, false, false), (scaled.Scale, scaled.Landscape, scaled.RightToLeft));
        var noPrinter = Single(SingleSheet(s => s
            .Record(0x00A1, U16(9), U16(75), U16(1), U16(1), U16(0), U16(0x04), U16(600), U16(600), F64(0.5), F64(0.5), U16(1))
            .Record(0x0203, Cell(0, 0), F64(1)))).Formatting!;
        Assert.Equal((1.0, false), (noPrinter.Scale, noPrinter.Landscape));

        // STANDARDWIDTH sizes the columns without their own width; DEFAULTROWHEIGHT can hide the rows without a ROW record.
        var standard = Single(SingleSheet(s => s
            .Record(0x0099, U16(12 * 256)).Record(0x0225, U16(0x02), U16(255))
            .Record(0x0208, U16(1), U16(0), U16(3), U16(255), U16(0), U16(0), U16(0x0100), U16(0x000F))
            .Record(0x0203, Cell(0, 0), F64(1)).Record(0x0203, Cell(1, 0), F64(2))));
        Assert.Equal([12.0], standard.Formatting!.ColumnWidths);
        Assert.Equal(["2"], Texts(standard, 0));
    }

    [Fact]
    public void Hidden_and_chart_sheets_are_left_out_and_bad_offsets_are_found()
    {
        BiffSheet Sheet(string name, string text, int hidden = 0, int type = 0) => new(name, s => s.Record(0x0204, Cell(0, 0), Text(text)), hidden, type);
        var data = Workbook(
            null,
            new BiffSheet("A", s => s.Record(0x0204, Cell(0, 0), Text("first")).Bof(0x20).Eof()), // With an embedded chart.
            Sheet("B", "second", hidden: 1), Sheet("C", "chart", type: 2), Sheet("D", "fourth"), Sheet("E", "very hidden", hidden: 2));
        Assert.Equal(["first", "fourth"], XlsConverter.Read(data).Select(s => s[0, 0].Text));
        Assert.Equal(["very hidden"], XlsConverter.Read(data, new XlsxConverterOptions { SheetIndexes = [4] }).Select(s => s[0, 0].Text));
        Assert.Equal(["second"], XlsConverter.Read(data, new XlsxConverterOptions { SheetNames = ["b"] }).Select(s => s[0, 0].Text));
        Assert.Empty(XlsConverter.Read(data, new XlsxConverterOptions { SheetIndexes = [2] }));
        var workbook = Open(data);
        Assert.Equal(["A", "B", "C", "D", "E"], workbook.Sheets.Select(s => s.Name));
        Assert.Equal([false, true, false, false, true], workbook.Sheets.Select(s => s.Hidden));
        Assert.Equal([true, true, false, true, true], workbook.Sheets.Select(s => s.IsWorksheet));

        // Offsets that do not point at a BOF record: the sheets take the substreams in order.
        var broken = (byte[])data.Clone();
        for (var i = 0; i + 4 < broken.Length; i++)
        {
            if (broken[i] == 0x85 && broken[i + 1] == 0x00 && broken[i + 2] == 9 && broken[i + 3] == 0)
            {
                broken[i + 4] ^= 0x01;
            }
        }

        Assert.Equal(["first", "fourth"], XlsConverter.Read(broken).Select(s => s[0, 0].Text));
        Assert.Equal(XlsConverter.Read(data, new XlsxConverterOptions { SheetIndexes = [1] })[0][0, 0].Text, XlsConverter.Read(broken, new XlsxConverterOptions { SheetIndexes = [1] })[0][0, 0].Text);

        // A sheet whose substream is missing (the stream cut short) is empty.
        var lost = Workbook(null, Sheet("A", "first"), Sheet("B", "second"));
        var cut = lost[..^new BiffBuilder().Bof(0x10).Record(0x0204, Cell(0, 0), Text("second")).Eof().Length];
        var sheets = XlsConverter.Read(cut);
        Assert.Equal("first", sheets[0][0, 0].Text);
        Assert.Empty(sheets[1].Rows);
    }

    [Fact]
    public void Embedded_chart_substreams_are_skipped()
    {
        var data = SingleSheet(s => s
            .Record(0x0203, Cell(0, 0), F64(1))
            .Bof(0x20).Record(0x0203, Cell(5, 5), F64(9)).Record(0x1002, new byte[16]).Eof()
            .Record(0x0203, Cell(1, 0), F64(2)));
        var sheet = Single(data);
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal(1, sheet.ColumnCount);
        Assert.Equal("2", sheet[1, 0].Text);
    }

    [Fact]
    public void Print_areas_and_titles_come_from_built_in_names()
    {
        static byte[] Name(int sheet, char kind, byte[] formula) =>
            Join(U16(0x0020), [0, 1], U16(formula.Length), U16(0), U16(sheet), [0, 0, 0, 0], [0, (byte)kind], formula);
        var area = Join([0x3B], U16(0), U16(1), U16(3), U16(0), U16(1)); // Sheet1!$A$2:$B$4
        var rows = Join([0x3B], U16(0), U16(0), U16(0), U16(0), U16(0xFF)); // Sheet1!$1:$1
        var columns = Join([0x3B], U16(0), U16(0), U16(0xFFFF), U16(0), U16(0)); // Sheet1!$A:$A
        var titles = Join([0x29], U16(rows.Length + columns.Length + 1), columns, rows, [0x10]);
        var data = SingleSheet(
            s =>
            {
                for (var r = 0; r < 6; r++)
                {
                    for (var c = 0; c < 4; c++)
                    {
                        s.Record(0x0203, Cell(r, c), F64((r * 10) + c));
                    }
                }
            },
            g => g.Record(0x0017, U16(1), U16(0), U16(0), U16(0))
                .Record(0x0018, Name(0, '\x06', area))
                .Record(0x0018, Name(1, '\x07', titles))
                .Record(0x0018, Join(U16(0), [0, 4], U16(0), U16(0), U16(1), [0, 0, 0, 0], [0], "Area"u8.ToArray()))); // Not built in.
        var sheet = Single(data);
        Assert.Equal(1, sheet.HeaderRows);
        Assert.Equal(4, sheet.Rows.Count); // The title row, then rows 2 to 4.
        Assert.Equal(["0", "1"], Texts(sheet, 0));
        Assert.Equal(["10", "11"], Texts(sheet, 1));
        Assert.Equal(["30", "31"], Texts(sheet, 3));
        Assert.Equal(6, Single(data, new XlsxConverterOptions { UsePageSetup = false }).Rows.Count);

        // A print area of whole columns (Sheet1!$B:$C), and one of a name local to the first sheet with a plain area.
        void Cells(BiffBuilder s)
        {
            for (var r = 0; r < 6; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    s.Record(0x0203, Cell(r, c), F64((r * 10) + c));
                }
            }
        }

        var columnsArea = Single(SingleSheet(Cells, g => g.Record(0x0017, U16(1), U16(0), U16(0), U16(0))
            .Record(0x0018, Name(0, '\x06', Join([0x3B], U16(0), U16(0), U16(0xFFFF), U16(1), U16(2))))));
        Assert.Equal((6, 2), (columnsArea.Rows.Count, columnsArea.ColumnCount));
        Assert.Equal(["1", "2"], Texts(columnsArea, 0));
        var local = Single(SingleSheet(Cells, g => g.Record(0x0018, Name(1, '\x06', Join([0x25], U16(4), U16(5), U16(3), U16(3))))));
        Assert.Equal((2, 1), (local.Rows.Count, local.ColumnCount));
        Assert.Equal("53", local[1, 0].Text);

        Assert.Equal([(0, new CellRange(4, 2, 4, 2))], XlsWorkbook.Areas(Join([0x3A], U16(0), U16(4), U16(2 | 0xC000))));
        Assert.Equal([(-1, new CellRange(1, 0, 2, 3))], XlsWorkbook.Areas(Join([0x25], U16(2), U16(1), U16(3), U16(0))));
        Assert.Equal([(0, new CellRange(0, 0, 0, 0))], XlsWorkbook.Areas(Join([0x3D], new byte[10], [0x3C], new byte[6], [0x26], new byte[6], [0x3A], U16(0), U16(0), U16(0))));
        Assert.Empty(XlsWorkbook.Areas([0x17, 1, 0]));
        Assert.Empty(XlsWorkbook.Areas([0x3B, 0, 0]));
    }

    [Fact]
    public void Excel_5_workbooks_are_read_in_their_code_page()
    {
        var cyrillic = CodePagesEncodingProvider.Instance.GetEncoding(1251)!;
        var sheet = new BiffBuilder().Bof(0x10, 0x0500)
            .Record(0x0204, Cell(0, 0, 1), Text5("\u041F\u0440\u0438\u0432\u0435\u0442", cyrillic)) // LABEL, in Cyrillic.
            .Record(0x0006, Cell(0, 1), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0))
            .Record(0x0207, Text5("\u043C\u0438\u0440", cyrillic)) // STRING
            .Record(0x0203, Cell(1, 0, 2), F64(0.25))
            .Eof().ToArray();
        byte[] Globals(int offset) => new BiffBuilder().Bof(0x0005, 0x0500)
            .Record(0x0042, U16(1251))
            .Record(0x0031, U16(200), U16(0), U16(0x7FFF), U16(400), U16(0), U8(0), U8(0), U8(0), U8(0), Text5("Arial", cyrillic, 1))
            .Record(0x0031, U16(240), U16(0x02), U16(0x7FFF), U16(700), U16(0), U8(1), U8(0), U8(0), U8(0), Text5("\u0428\u0440\u0438\u0444\u0442", cyrillic, 1))
            .Record(0x041E, U16(164), Text5("0.0%", cyrillic, 1))
            .Record(0x00E0, U16(0), U16(0), U16(1), U8(0x20), U8(0), U32(64 | (65 << 7)), U32(0))
            .Record(0x00E0, U16(1), U16(0), U16(1), U8(0x02), U8(0), U32(10 | (65 << 7) | (1 << 16) | (2 << 22) | (12 << 25)), U32(1 | (2 << 3) | (5 << 6) | (12 << 9)))
            .Record(0x00E0, U16(0), U16(164), U16(1), U8(0x20), U8(0), U32(64 | (65 << 7)), U32(0))
            .Record(0x0085, U32(offset), U8(0), U8(0), Text5("\u041B\u0438\u0441\u0442", cyrillic, 1))
            .Eof().ToArray();
        var data = Join(Globals(Globals(0).Length), sheet);
        var workbook = Open(data);
        Assert.Equal(5, workbook.Biff);
        Assert.Equal("\u041B\u0438\u0441\u0442", workbook.Sheets[0].Name);
        var read = Single(data);
        Assert.Equal(["\u041F\u0440\u0438\u0432\u0435\u0442", "\u043C\u0438\u0440"], Texts(read, 0));
        Assert.Equal("25.0%", read[1, 0].Text);
        var format = read[0, 0].Format!;
        Assert.Equal(("\u0428\u0440\u0438\u0444\u0442", true, true, CellAlignment.Center), (format.FontName, format.Bold, format.Italic, read[0, 0].Alignment));
        Assert.Equal(XlsxColors.Indexed(10), format.Fill);
        Assert.Equal((0.75, 1.5, 2.25, 1.5), (format.Top.Width, format.Left.Width, format.Right.Width, format.Bottom.Width));
        Assert.Equal(XlsxColors.Indexed(12), format.Top.Color);
        Assert.Contains("\u043C\u0438\u0440", AllText(XlsToPdf.ConvertToBytes(data)), StringComparison.Ordinal);
    }

    [Fact]
    public void Encrypted_and_old_streams_are_not_supported_and_others_are_malformed()
    {
        var encrypted = SingleSheet(s => s.Record(0x0203, Cell(0, 0), F64(1)), g => g.Record(0x002F, U16(0), U16(0x0034)));
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => XlsToPdf.Convert(encrypted)).Message, StringComparison.Ordinal);
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => XlsxToPdf.Convert(encrypted)).Message, StringComparison.Ordinal);

        var biff4 = new BiffBuilder().Record(0x0409, U16(0), U16(0x10), U16(0)).Eof().ToArray();
        Assert.Contains("Excel 2.x-4.0", Assert.Throws<NotSupportedException>(() => XlsToPdf.Convert(biff4)).Message, StringComparison.Ordinal);
        var biff2 = new BiffBuilder().Record(0x0009, U16(2), U16(0x10)).Eof().ToArray();
        Assert.Throws<NotSupportedException>(() => XlsToPdf.Convert(biff2));

        // A stream that starts with a worksheet, not the globals.
        var sheetFirst = new BiffBuilder().Bof(0x10).Eof().ToArray();
        Assert.Contains("0x0010", Assert.Throws<SpreadsheetFormatException>(() => XlsToPdf.Convert(sheetFirst)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Damaged_streams_convert_or_throw_the_documented_exceptions()
    {
        var data = SingleSheet(
            s => s.Record(0x0204, Cell(0, 0), Text("text")).Record(0x00FD, Cell(1, 0), U32(0)).Record(0x0006, Cell(2, 0), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], U16(0), U32(0), U16(0))
                .Record(0x0207, Text("result")).Record(0x00E5, U16(1), U16(0), U16(1), U16(0), U16(1)).Record(0x00BD, U16(3), U16(0), U16(0), U32(6), U16(0)),
            g => g.Font("Arial").Format(164, "0.0").Xf().Record(0x00FC, U32(1), U32(1), Text("shared")).Record(0x0018, Join(U16(0x0020), [0, 1], U16(11), U16(0), U16(1), [0, 0, 0, 0], [0, 6], [0x3B], U16(0), U16(0), U16(1), U16(0), U16(1))));
        Assert.Equal("text", Single(data)[0, 0].Text);

        // Every prefix of the stream, and the stream with each byte changed.
        for (var length = 0; length <= data.Length; length++)
        {
            AssertConvertsOrThrowsExpected(data[..length]);
        }

        for (var i = 0; i < data.Length; i++)
        {
            foreach (var value in new byte[] { 0x00, 0xFF, 0x3C, (byte)(data[i] ^ 0x5A) })
            {
                var changed = (byte[])data.Clone();
                changed[i] = value;
                AssertConvertsOrThrowsExpected(changed);
            }
        }

        static void AssertConvertsOrThrowsExpected(byte[] input)
        {
            var exception = Record.Exception(() => XlsToPdf.ConvertToBytes(input, new XlsxConverterOptions
            {
                Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxTableCells = 100_000, MaxPages = 20 } },
            }));
            Assert.True(exception is null or SpreadsheetFormatException or NotSupportedException or ResourceLimitExceededException, exception?.ToString());
        }
    }

    [Fact]
    public void The_limits_bound_cells_and_the_workbook_stream()
    {
        var big = SingleSheet(s => s.Record(0x0203, Cell(0, 0), F64(1)).Record(0x0203, Cell(999, 255), F64(1)));
        var options = new XlsxConverterOptions { Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxTableCells = 1000 } } };
        Assert.Throws<ResourceLimitExceededException>(() => XlsToPdf.Convert(big, options));

        // Cells that show something count against the limit as they are read.
        var many = SingleSheet(s =>
        {
            for (var i = 0; i < 50; i++)
            {
                s.Record(0x0203, Cell(0, i), F64(i));
            }
        });
        var tight = new XlsxConverterOptions { Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxTableCells = 40 } } };
        Assert.Throws<ResourceLimitExceededException>(() => XlsConverter.Read(many, tight));

        // The workbook stream is read within the decompressed size limit.
        var small = new XlsxConverterOptions { Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxDecompressedBytes = 1000 } } };
        Assert.Throws<ResourceLimitExceededException>(() => XlsToPdf.Convert(Fixture("SampleSS.xls"), small));
    }
}
