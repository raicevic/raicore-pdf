using System.Globalization;
using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// Converting XLSX workbooks to PDF: values in their number formats, styles, merged cells, sizes, page setup, sheet
/// selection, malformed and hostile packages.
/// </summary>
public class XlsxToPdfTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static byte[] Fixture(string name) => Fixtures.Read(Path.Combine("Sheets", name));

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static string AllText(byte[] pdf) => string.Join("\n", Pages(pdf));

    private static IReadOnlyList<TextMatch> Find(byte[] pdf, int page, string text) => new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, text);

    private static void AssertValid(byte[] pdf)
    {
        if (ExternalTools.Find("qpdf") is not null)
        {
            ExternalTools.AssertQpdfClean(pdf);
        }
    }

    /// <summary>A ZIP package of the given parts.</summary>
    internal static byte[] Package(params (string Name, string Content)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A workbook of one sheet (or more) with the given sheet data, and optional styles and shared strings.</summary>
    internal static byte[] Workbook(string sheetData, string? styles = null, string? sharedStrings = null, string sheetExtra = "", string workbookExtra = "", string sheetBefore = "")
    {
        var parts = new List<(string, string)>
        {
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets>{workbookExtra}</workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\">"
                + $"<Relationship Id=\"rId1\" Type=\"{Relationships}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + (styles is null ? string.Empty : $"<Relationship Id=\"rId2\" Type=\"{Relationships}/styles\" Target=\"styles.xml\"/>")
                + (sharedStrings is null ? string.Empty : $"<Relationship Id=\"rId3\" Type=\"{Relationships}/sharedStrings\" Target=\"sharedStrings.xml\"/>")
                + "</Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\">{sheetBefore}<sheetData>{sheetData}</sheetData>{sheetExtra}</worksheet>"),
        };
        if (styles is not null)
        {
            parts.Add(("xl/styles.xml", $"<styleSheet xmlns=\"{Main}\">{styles}</styleSheet>"));
        }

        if (sharedStrings is not null)
        {
            parts.Add(("xl/sharedStrings.xml", $"<sst xmlns=\"{Main}\">{sharedStrings}</sst>"));
        }

        return Package([.. parts]);
    }

    private static Sheet Single(byte[] xlsx, XlsxConverterOptions? options = null) => Assert.Single(XlsxConverter.Read(xlsx, options));

    private static string[] Texts(Sheet sheet, int row) => [.. Enumerable.Range(0, sheet.ColumnCount).Select(c => sheet[row, c].Text)];

    [Fact]
    public void Values_are_shown_in_their_number_formats()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Fixture("styled.xlsx"));
        AssertValid(pdf);
        var text = AllText(pdf);
        foreach (var expected in new[]
        {
            "Quarterly report", "1,234.50", "-99.95", "1,000,000.00", "12.5%", "50.0%", "100.0%", "2026-01-15", "2026-02-28",
            "2026-12-31", "1,134.55", "TRUE", "#DIV/0!", "textresult", "Right", "Indented",
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        // Rich text runs are joined (in the cell's own font).
        Assert.Contains("plain bold and italic", text, StringComparison.Ordinal);
        Assert.Equal("Styled workbook", XlsxToPdf.Convert(Fixture("styled.xlsx")).Info.Title);
        Assert.Equal("Mine", XlsxToPdf.Convert(Fixture("styled.xlsx"), new XlsxConverterOptions { Sheet = new SheetOptions { Title = "Mine" } }).Info.Title);
        Assert.Equal("RaiCore.Pdf.Sheets", XlsxToPdf.Convert(Fixture("styled.xlsx")).Info.Creator);
    }

    [Fact]
    public void Every_format_of_the_formats_fixture_shows_as_a_spreadsheet_does()
    {
        var sheet = Single(Fixture("formats.xlsx"));
        string[] expected =
        [
            "1234.5678", "1235", "1234.57", "1,234,568", "1,234,567.89", "46%", "45.67%", "1.23E+04", "1 1/2", "  5/16",
            "2026-11-19", "19/11/2026", "19 November 2026", "18:00", "36:00:00", "6:00 PM", "(5.00)", "zero", "$1,234.50",
            "\u20AC 1,234.50", "-1,234.50", "123-45-6789", "0.000123", "1.23457E+11", "Thu, Nov 19", "0.0", "5 items", "1.5M",
            "00:00.0", "small",
        ];
        Assert.Equal(expected.Length, sheet.Rows.Count);
        for (var r = 0; r < expected.Length; r++)
        {
            Assert.Equal(expected[r], sheet[r, 1].Text);
        }

        Assert.Equal(CellKind.Date, sheet[10, 1].Kind);
        Assert.Equal(CellKind.Number, sheet[0, 1].Kind);
        Assert.Equal(CellKind.Text, sheet[0, 0].Kind);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), sheet[20, 1].Format!.Color);
    }

    [Fact]
    public void Styles_give_cells_their_fonts_fills_borders_and_alignment()
    {
        var sheet = Single(Fixture("styled.xlsx"));
        var formatting = sheet.Formatting!;

        // The merged title: bold, 16 pt, white on dark blue, centered, with a medium border on all sides of the range.
        var title = sheet[0, 0].Format!;
        Assert.Equal(new CellMerge(0, 0, 1, 5), Assert.Single(formatting.Merges));
        Assert.True(title.Bold);
        Assert.Equal(16, title.FontSize);
        Assert.Equal(PdfColor.FromRgb(255, 255, 255), title.Color);
        Assert.Equal(PdfColor.FromRgb(0x1F, 0x4E, 0x79), title.Fill);
        Assert.Equal(CellAlignment.Center, sheet[0, 0].Alignment);
        Assert.Equal(1.5, title.Left.Width);
        Assert.Equal(1.5, title.Right.Width);
        Assert.Equal(1.5, title.Bottom.Width);

        // The header row: bold with a thick bottom border and a light fill.
        Assert.True(sheet[1, 0].Format!.Bold);
        Assert.Equal(2.25, sheet[1, 0].Format!.Bottom.Width);
        Assert.Equal(PdfColor.FromRgb(0xD9, 0xE1, 0xF2), sheet[1, 0].Format!.Fill);

        Assert.True(sheet[3, 0].Format!.Italic);
        Assert.Equal(CellUnderline.Single, sheet[4, 0].Format!.Underline);
        Assert.True(sheet[2, 4].Format!.Wrap);
        Assert.Equal(VerticalAlignment.Top, sheet[2, 4].Format!.Vertical);
        Assert.Equal(VerticalAlignment.Bottom, sheet[2, 0].Format!.Vertical);
        Assert.True(sheet[5, 0].Format!.Strikethrough);
        Assert.Equal(PdfColor.FromRgb(0xC0, 0x00, 0x00), sheet[5, 0].Format!.Color);
        Assert.Equal(CellAlignment.Center, sheet[5, 0].Alignment);
        Assert.Equal(CellAlignment.Right, sheet[5, 1].Alignment);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), sheet[5, 1].Format!.Fill);
        Assert.Equal(2, sheet[10, 0].Format!.Indent);

        // Dashed, dotted and double borders in green.
        var borders = sheet[6, 0].Format!;
        Assert.Equal(BorderStyle.Dashed, borders.Left.Style);
        Assert.Equal(BorderStyle.Dotted, borders.Top.Style);
        Assert.Equal(BorderStyle.Double, borders.Right.Style);
        Assert.Equal(BorderStyle.Double, borders.Bottom.Style);
        Assert.Equal(PdfColor.FromRgb(0x00, 0xB0, 0x50), borders.Left.Color);

        // Booleans and errors are centered; formulas show their cached results.
        Assert.Equal(CellAlignment.Center, sheet[8, 1].Alignment);
        Assert.Equal(CellAlignment.Center, sheet[8, 2].Alignment);
        Assert.Equal("1,134.55", sheet[8, 0].Text);

        // Column widths in characters and the title row's height in points.
        Assert.Equal([18.71, 12.71, 12.71, 12.71, 12.71], formatting.ColumnWidths.Select(w => w!.Value).Select(w => Math.Round(w, 2)));
        Assert.Equal(30, formatting.RowHeights[0]);
        Assert.Null(formatting.RowHeights[2]);
        Assert.Equal(11, formatting.DefaultFontSize);
        Assert.Equal("Calibri", formatting.DefaultFontName);
    }

    [Fact]
    public void Styled_cells_are_drawn_in_their_fonts_and_colors()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Fixture("styled.xlsx"));
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("LiberationSans-Bold", text, StringComparison.Ordinal);
        Assert.Contains("LiberationSans-Italic", text, StringComparison.Ordinal);
        var content = Encoding.Latin1.GetString(PdfReader.Open(pdf).Pages[0].GetContent());
        Assert.Contains("1 1 0 rg", content, StringComparison.Ordinal); // The yellow fill.

        // The title is centered over the five merged columns, and larger than the other text.
        var reportTitle = Find(pdf, 0, "Quarterly report").Single().Bounds;
        var item = Find(pdf, 0, "Item").Single().Bounds;
        var note = Find(pdf, 0, "Note").Single().Bounds;
        Assert.True(reportTitle.Height > item.Height * 1.3, $"{reportTitle.Height} vs {item.Height}");
        Assert.True(reportTitle.X > item.Right && reportTitle.Right < note.X, $"{reportTitle} between {item} and {note}");
    }

    [Fact]
    public void Text_that_does_not_wrap_runs_on_over_empty_cells()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Fixture("styled.xlsx"));
        var line = Find(pdf, 0, "A long text that runs on over the empty cells to its right, as in a spreadsheet").Single();
        var item = Find(pdf, 0, "Item").Single().Bounds;
        Assert.Single(line.Rectangles.Select(r => Math.Round(r.Y)).Distinct()); // One line.
        Assert.True(line.Bounds.Width > 18 * 6, $"{line.Bounds.Width}");
        Assert.Equal(item.X, line.Bounds.X, 0.5);

        // Text in a cell with borders wraps in its column instead, keeping its borders.
        var bordered = Find(pdf, 0, "Dashed and double borders").Single();
        Assert.True(bordered.Rectangles.Select(r => Math.Round(r.Y)).Distinct().Count() > 1);
    }

    [Fact]
    public void Text_does_not_run_on_over_an_empty_cell_with_a_fill()
    {
        var styles = "<fills><fill/><fill/><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/></patternFill></fill></fills><cellXfs><xf/><xf fillId=\"2\"/></cellXfs>";
        var row = "<row><c r=\"A1\" t=\"inlineStr\"><is><t>A text much longer than its column</t></is></c><c r=\"B1\" s=\"1\"/><c r=\"F1\"><v>1</v></c></row>";
        var cols = "<cols><col min=\"1\" max=\"6\" width=\"8\"/></cols>";
        var blocked = XlsxToPdf.ConvertToBytes(Workbook(row, styles, sheetBefore: cols));
        Assert.True(Find(blocked, 0, "A text much longer than its column").Single().Rectangles.Select(r => Math.Round(r.Y)).Distinct().Count() > 1);
        var free = XlsxToPdf.ConvertToBytes(Workbook(row.Replace(" s=\"1\"", string.Empty, StringComparison.Ordinal), styles, sheetBefore: cols));
        Assert.Single(Find(free, 0, "A text much longer than its column").Single().Rectangles.Select(r => Math.Round(r.Y)).Distinct());
    }

    [Fact]
    public void A_sized_column_is_widened_for_a_number_that_does_not_fit()
    {
        var styles = "<cellXfs><xf/><xf numFmtId=\"4\"/></cellXfs>";
        var pdf = XlsxToPdf.ConvertToBytes(Workbook(
            "<row><c r=\"A1\" s=\"1\"><v>1234567.89</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>Next</t></is></c></row>",
            styles,
            sheetBefore: "<cols><col min=\"1\" max=\"2\" width=\"3\"/></cols>"));
        var number = Find(pdf, 0, "1,234,567.89").Single();
        Assert.Single(number.Rectangles.Select(r => Math.Round(r.Y)).Distinct()); // Whole, on one line.
        Assert.True(number.Bounds.Width > 3 * 7, $"{number.Bounds.Width}");
    }

    [Fact]
    public void Text_runs_on_to_the_left_when_right_aligned_and_to_both_sides_when_centered()
    {
        var format = new CellFormat();
        SheetCell Text(string text, CellAlignment alignment) => new(text, CellKind.Text, alignment, format);
        SheetCell[] Row(params SheetCell[] cells) => cells;
        var empty = SheetCell.Empty;
        var sheet = new Sheet(
            [
                Row(new("HdrA", CellKind.Text), new("HdrB", CellKind.Text), new("HdrC", CellKind.Text), new("HdrD", CellKind.Text), new("HdrE", CellKind.Text)),
                Row(empty, empty, empty, empty, Text("Right aligned text that is long", CellAlignment.Right)),
                Row(empty, empty, Text("Centered text that is long too", CellAlignment.Center), empty, empty),
                Row(Text("Blocked text that cannot run on", CellAlignment.General), new("x", CellKind.Text), empty, empty, empty),
            ],
            0)
        {
            Formatting = new SheetFormatting { ColumnWidths = [8, 8, 8, 8, 8] },
        };
        var pdf = SheetRenderer.Render([sheet], new SheetOptions(), "test", null).ToArray();
        var e = Find(pdf, 0, "HdrE").Single().Bounds;
        var a = Find(pdf, 0, "HdrA").Single().Bounds;
        var c = Find(pdf, 0, "HdrC").Single().Bounds;
        var right = Find(pdf, 0, "Right aligned text that is long").Single();
        Assert.Single(right.Rectangles.Select(r => Math.Round(r.Y)).Distinct());
        Assert.True(right.Bounds.X < e.X - 50, $"{right.Bounds} left of {e}");
        var centered = Find(pdf, 0, "Centered text that is long too").Single();
        Assert.Single(centered.Rectangles.Select(r => Math.Round(r.Y)).Distinct());
        var columnCenter = c.X + (c.Width / 2);
        Assert.InRange(centered.Bounds.X + (centered.Bounds.Width / 2), columnCenter - 30, columnCenter + 30);
        var blocked = Find(pdf, 0, "Blocked text that cannot run on").Single();
        Assert.True(blocked.Rectangles.Select(r => Math.Round(r.Y)).Distinct().Count() > 1);
        Assert.Equal(a.X, blocked.Bounds.X, 0.5);
    }

    [Fact]
    public void Each_sheet_starts_a_page_with_its_print_titles_on_every_page()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Fixture("multi.xlsx"));
        AssertValid(pdf);
        var pages = Pages(pdf);
        var reader = PdfReader.Open(pdf);
        Assert.StartsWith("Summary sheet", pages[0], StringComparison.Ordinal);

        // The data sheet: landscape, its title row once, its print titles (rows 2 and 3) at the top of every page.
        var data = Enumerable.Range(0, pages.Length).Where(i => pages[i].Contains("Name ", StringComparison.Ordinal)).ToList();
        Assert.True(data.Count >= 3, $"{data.Count} pages");
        Assert.All(data, i => Assert.True(reader.Pages[i].Width > reader.Pages[i].Height));
        Assert.Single(data, i => pages[i].Contains("Report title (printed once)", StringComparison.Ordinal));
        var headerTop = Find(pdf, data[1], "(units)").Single().Bounds.Top;
        foreach (var i in data)
        {
            var units = Find(pdf, i, "(units)").Single().Bounds;
            Assert.All(new TextExtractor(reader).FindOnPage(i, new System.Text.RegularExpressions.Regex(@"Name \d+")), m => Assert.True(m.Bounds.Top < units.Y));
            if (i != data[0])
            {
                Assert.Equal(headerTop, units.Top, 0.5);
            }
        }

        var text = string.Join("\n", data.Select(i => pages[i]));
        Assert.DoesNotContain("Name 7\n", text + "\n", StringComparison.Ordinal); // A hidden row.
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal); // A hidden column.
        Assert.Contains("Name 147", text, StringComparison.Ordinal); // The print area ends at row 150.
        Assert.DoesNotContain("Name 148", text, StringComparison.Ordinal);

        // The sheet that fits its width: one page, every column on it. The hidden sheet is left out.
        var fit = Enumerable.Range(0, pages.Length).Single(i => pages[i].Contains("Fit 1", StringComparison.Ordinal));
        Assert.Contains("Fit 15", pages[fit], StringComparison.Ordinal);
        Assert.Equal(fit, pages.Length - 1);
        Assert.DoesNotContain("Hidden sheet", AllText(pdf), StringComparison.Ordinal);

        // A bookmark for each sheet.
        Assert.Equal(["Summary", "Data", "Fit"], XlsxToPdf.Convert(Fixture("multi.xlsx")).Outline.Select(o => o.Title));
    }

    [Fact]
    public void Sheets_can_be_chosen_by_name_and_position()
    {
        var xlsx = Fixture("multi.xlsx");
        var secret = AllText(XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { SheetNames = ["secret"] }));
        Assert.Contains("Hidden sheet", secret, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary", secret, StringComparison.Ordinal);

        var two = XlsxConverter.Read(xlsx, new XlsxConverterOptions { SheetIndexes = [2], SheetNames = ["Summary"] });
        Assert.Equal(2, two.Count);
        Assert.Equal("Summary sheet", two[0][0, 0].Text);
        Assert.Equal("Fit 1", two[1][0, 0].Text);
        Assert.Equal("Summary", two[0].Formatting!.Bookmark);

        Assert.Throws<ArgumentException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { SheetNames = ["Nope"] }));
        Assert.Throws<ArgumentException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { SheetIndexes = [4] }));
        Assert.Throws<ArgumentException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { SheetIndexes = [-1] }));
        Assert.Throws<ArgumentNullException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { SheetNames = [null!] }));
        Assert.Null(Single(xlsx, new XlsxConverterOptions { SheetNames = ["Data"] }).Formatting!.Bookmark);
    }

    [Fact]
    public void Sheet_names_can_be_headings()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Fixture("multi.xlsx"), new XlsxConverterOptions { ShowSheetNames = true, Sheet = new SheetOptions { Tagged = true } });
        AssertValid(pdf);
        var pages = Pages(pdf);
        Assert.StartsWith("Summary\n", pages[0], StringComparison.Ordinal);
        Assert.Contains(pages, p => p.StartsWith("Data\n", StringComparison.Ordinal));
        var heading = Find(pdf, 0, "Summary").First().Bounds;
        var sheetTitle = Find(pdf, 0, "Summary sheet").Single().Bounds;
        Assert.True(heading.Top > sheetTitle.Top);
    }

    [Fact]
    public void The_page_setup_can_be_ignored()
    {
        var sheets = XlsxConverter.Read(Fixture("multi.xlsx"), new XlsxConverterOptions { UsePageSetup = false });
        var data = sheets[1];
        Assert.Equal(0, data.HeaderRows);
        Assert.Equal(0, data.Formatting!.LeadRows);
        Assert.False(data.Formatting.Landscape);
        Assert.Equal("Name 200", data[data.Rows.Count - 1, 1].Text);
        Assert.False(sheets[2].Formatting!.FitToWidth);

        var withSetup = XlsxConverter.Read(Fixture("multi.xlsx"))[1];
        Assert.Equal(2, withSetup.HeaderRows);
        Assert.Equal(1, withSetup.Formatting!.LeadRows);
        Assert.True(withSetup.Formatting.Landscape);
    }

    [Fact]
    public void Auto_fit_ignores_the_sheet_sizes()
    {
        var sheet = Single(Fixture("styled.xlsx"), new XlsxConverterOptions { AutoFit = true });
        Assert.Empty(sheet.Formatting!.ColumnWidths);
        Assert.Empty(sheet.Formatting.RowHeights);
        var pdf = XlsxToPdf.ConvertToBytes(Fixture("styled.xlsx"), new XlsxConverterOptions { AutoFit = true });
        Assert.Contains("Quarterly report", AllText(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Wide_sheets_are_printed_in_bands_or_fitted_to_the_width()
    {
        var xlsx = Fixture("wide.xlsx");
        var pdf = XlsxToPdf.ConvertToBytes(xlsx);
        var pages = Pages(pdf);
        Assert.True(pages.Length >= 6, $"{pages.Length} pages");
        Assert.StartsWith("Column 1 Column 2", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Column 30", pages[0], StringComparison.Ordinal);

        // Every value once, nothing clipped.
        var numbers = new TextExtractor(PdfReader.Open(pdf)).ExtractPages()
            .SelectMany(p => System.Text.RegularExpressions.Regex.Matches(p.Text, @"\b\d{4,6}\b").Select(m => m.Value)).ToList();
        Assert.Equal(120 * 30, numbers.Count);
        Assert.Equal(120 * 30, numbers.Distinct().Count());

        var fitted = Pages(XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { Sheet = new SheetOptions { FitToWidth = true, Landscape = true, MinimumFontSize = 3 } }));
        Assert.Contains("Column 30", fitted[0], StringComparison.Ordinal);
        Assert.True(fitted.Length < pages.Length);
    }

    [Fact]
    public void The_1904_date_system_and_inline_strings_are_read()
    {
        Assert.Equal(["2026-11-19", "1904-01-01"], Single(Fixture("date1904.xlsx")).Rows.Select(r => r[0].Text));
        var inline = Single(Fixture("inline.xlsx"));
        Assert.Equal("Inline text", inline[0, 0].Text);
        Assert.Equal("with \u0007 an escape", inline[0, 1].Text);
        Assert.Equal("42", inline[1, 0].Text);
        Assert.Contains("with an escape", AllText(XlsxToPdf.ConvertToBytes(Fixture("inline.xlsx"))).Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_minimal_strict_package_without_styles_is_read()
    {
        var sheet = Single(Fixture("minimal.xlsx"));
        Assert.Equal(["First", "1.5", "2026-11-19"], Texts(sheet, 0));
        Assert.Equal(["Rich runs", "FALSE", string.Empty], Texts(sheet, 1));
        Assert.Equal(CellKind.Date, sheet[0, 2].Kind);
        Assert.Empty(sheet.Formatting!.ColumnWidths.OfType<double>()); // No sizes: fitted to the content.
    }

    [Fact]
    public async Task Stream_path_and_async_overloads_convert_the_same()
    {
        var xlsx = Fixture("minimal.xlsx");
        var expected = AllText(XlsxToPdf.ConvertToBytes(xlsx));
        Assert.Equal(expected, AllText(XlsxToPdf.ConvertToBytes(new MemoryStream(xlsx))));
        Assert.Equal(expected, AllText(XlsxToPdf.Convert(new MemoryStream(xlsx)).ToArray()));
        Assert.Equal(expected, AllText(XlsxToPdf.Convert(Fixtures.Path(Path.Combine("Sheets", "minimal.xlsx"))).ToArray()));
        Assert.Equal(expected, AllText((await XlsxToPdf.ConvertAsync(xlsx, cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
        Assert.Equal(expected, AllText((await XlsxToPdf.ConvertAsync(new MemoryStream(xlsx), cancellationToken: TestContext.Current.CancellationToken)).ToArray()));
        Assert.Throws<ArgumentNullException>(() => XlsxToPdf.Convert((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => XlsxToPdf.Convert((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => XlsxToPdf.Convert((string)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => XlsxToPdf.ConvertAsync((Stream)null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => XlsxToPdf.ConvertAsync((byte[])null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_and_limits_stop_the_conversion()
    {
        var xlsx = Fixture("wide.xlsx");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XlsxToPdf.ConvertAsync(xlsx, null, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XlsxToPdf.ConvertAsync(new MemoryStream(xlsx), null, canceled.Token));

        var cells = Assert.Throws<ResourceLimitExceededException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions
        {
            Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxTableCells = 1000 } },
        }));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), cells.LimitName);

        var bytes = Assert.Throws<ResourceLimitExceededException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions
        {
            Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxDecompressedBytes = 20_000 } },
        }));
        Assert.Equal(nameof(ResourceLimits.MaxDecompressedBytes), bytes.LimitName);

        var pages = Assert.Throws<ResourceLimitExceededException>(() => XlsxToPdf.Convert(Fixture("multi.xlsx"), new XlsxConverterOptions
        {
            Sheet = new SheetOptions { Limits = ResourceLimits.Default with { MaxPages = 3 } },
        }));
        Assert.Equal(nameof(ResourceLimits.MaxPages), pages.LimitName);

        Assert.Throws<ArgumentException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { Sheet = new SheetOptions { Limits = null! } }));
        Assert.Throws<ArgumentNullException>(() => XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { Sheet = null! }));
    }

    [Fact]
    public void A_sheet_whose_print_range_is_too_large_is_rejected_before_it_takes_memory()
    {
        // One value at the last cell of the sheet: the range from A1 has 17 billion cells.
        var xlsx = Workbook("<row r=\"1048576\"><c r=\"XFD1048576\"><v>1</v></c></row>");
        var error = Assert.Throws<ResourceLimitExceededException>(() => XlsxToPdf.Convert(xlsx));
        Assert.Equal(nameof(ResourceLimits.MaxTableCells), error.LimitName);
    }

    [Fact]
    public void Escaped_halves_of_surrogate_pairs_are_replaced()
    {
        // Found by fuzzing the binary reader: text layout cannot shape a lone surrogate, which an escape can give.
        var sheet = Single(Workbook("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>a_xD800_b_xD83D__xDE00_</t></is></c></row>"));
        Assert.Equal("a\uFFFDb\U0001F600", sheet[0, 0].Text);
        Assert.Equal("\uFFFD\uFFFDx", XlsxWorkbook.WithoutLoneSurrogates("\uDC00\uD800x"));
        AssertValid(XlsxToPdf.ConvertToBytes(Workbook("<row r=\"1\"><c r=\"A1\" t=\"str\"><v>_xDBFF_</v></c></row>")));
    }

    [Fact]
    public void Malformed_packages_throw_documented_exceptions()
    {
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert("not a workbook"u8.ToArray()));
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert("PK\u0003\u0004 broken"u8.ToArray()));
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert([]));
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert(Package(("readme.txt", "no workbook"))));

        // Encrypted workbooks and Excel 97-2003 files are Compound Files: read as Excel 97-2003 workbooks (XlsToPdfTests).
        byte[] compound = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        Assert.Contains("damaged", Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert(compound)).Message, StringComparison.Ordinal);
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => XlsxToPdf.Convert(Fixtures.Read("Poi/bug53475-password-is-pass.docx"))).Message, StringComparison.Ordinal);

        // Binary workbooks.
        var xlsb = Package(("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.bin\"/></Relationships>"), ("xl/workbook.bin", "binary"));
        Assert.Contains(".xlsb", Assert.Throws<NotSupportedException>(() => XlsxToPdf.Convert(xlsb)).Message, StringComparison.Ordinal);

        // Malformed XML, and a DTD (never processed).
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert(Workbook("<row><c><v>1</v></row>")));
        var dtd = Workbook("<row><c t=\"inlineStr\"><is><t>&xxe;</t></is></c></row>")
            .ToArray();
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert(dtd));
        var external = Package(
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<?xml version=\"1.0\"?><!DOCTYPE workbook [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><workbook xmlns=\"{Main}\"><sheets/></workbook>"));
        Assert.Throws<SpreadsheetFormatException>(() => XlsxToPdf.Convert(external));
    }

    [Fact]
    public void Missing_parts_and_out_of_range_indexes_are_tolerated()
    {
        // A shared string index out of range, a style index out of range, a sheet without its part, no styles.
        var sheet = Single(Workbook(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>99</v></c><c r=\"B1\" s=\"42\"><v>7</v></c><c r=\"C1\" t=\"s\"><v>0</v></c></row>",
            sharedStrings: "<si><t>shared</t></si>"));
        Assert.Equal(["", "7", "shared"], Texts(sheet, 0));

        var noPart = Package(
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"Gone\" sheetId=\"1\" r:id=\"rId9\"/></sheets></workbook>"));
        Assert.Single(PdfReader.Open(XlsxToPdf.ConvertToBytes(noPart)).Pages); // No worksheet: one empty page.
    }

    [Fact]
    public void Cells_without_references_follow_the_previous_ones()
    {
        var sheet = Single(Workbook("<row><c><v>1</v></c><c><v>2</v></c></row><row><c r=\"B2\"><v>3</v></c><c><v>4</v></c></row><row r=\"5\"><c r=\"bad\"><v>5</v></c></row>"));
        Assert.Equal(["1", "2", ""], Texts(sheet, 0));
        Assert.Equal(["", "3", "4"], Texts(sheet, 1));
        Assert.Equal(["5", "", ""], Texts(sheet, 4));
    }

    [Fact]
    public void Shared_strings_join_rich_runs_and_leave_out_phonetic_runs()
    {
        var sheet = Single(Workbook(
            "<row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c><c t=\"s\"><v>2</v></c></row>",
            sharedStrings: "<si><r><rPr><b/></rPr><t>Bold </t></r><r><t xml:space=\"preserve\">and plain </t></r></si>"
                + "<si><t>\u6F22\u5B57</t><rPh sb=\"0\" eb=\"2\"><t>\u304B\u3093\u3058</t></rPh><phoneticPr fontId=\"1\"/></si>"
                + "<si><t>a_x005F_b_x000D_c</t></si>"));
        Assert.Equal(["Bold and plain ", "\u6F22\u5B57", "a_b\rc"], Texts(sheet, 0));
    }

    [Fact]
    public void Value_types_are_shown_like_a_spreadsheet()
    {
        var sheet = Single(Workbook(
            "<row><c t=\"b\"><v>1</v></c><c t=\"b\"><v>0</v></c><c t=\"e\"><v>#N/A</v></c><c t=\"str\"><f>A1</f><v>formula</v></c>"
            + "<c><f>1+1</f></c><c><v>not a number</v></c><c><v>NaN</v></c><c t=\"d\"><v>2026-11-19T08:30:00</v></c><c t=\"d\"><v>soon</v></c>"
            + "<c t=\"n\"><v> 2.5 </v></c><c t=\"b\"><v>maybe</v></c></row>"));
        Assert.Equal(["TRUE", "FALSE", "#N/A", "formula", "", "not a number", "NaN", "2026-11-19 08:30:00", "soon", "2.5", "maybe"], Texts(sheet, 0));
        Assert.Equal(CellAlignment.Center, sheet[0, 0].Alignment);
        Assert.Equal(CellAlignment.Center, sheet[0, 2].Alignment);
        Assert.Equal(CellKind.Text, sheet[0, 5].Kind);
        Assert.Equal(CellKind.Date, sheet[0, 7].Kind);
    }

    [Fact]
    public void Dates_in_strict_files_use_the_cell_format_and_the_date_system()
    {
        var styles = "<numFmts><numFmt numFmtId=\"164\" formatCode=\"d mmm yyyy\"/></numFmts><cellXfs><xf/><xf numFmtId=\"164\"/></cellXfs>";
        var sheet = Single(Workbook("<row><c t=\"d\" s=\"1\"><v>1900-02-28</v></c><c t=\"d\" s=\"1\"><v>1900-03-01</v></c><c t=\"d\"><v>2026-11-19</v></c></row>", styles));
        Assert.Equal(["28 Feb 1900", "1 Mar 1900", "2026-11-19"], Texts(sheet, 0));
        var in1904 = Single(Workbook("<row><c t=\"d\" s=\"1\"><v>2026-11-19</v></c></row>", styles, workbookExtra: string.Empty)
            .Replace1904());
        Assert.Equal("19 Nov 2026", in1904[0, 0].Text);
    }

    [Fact]
    public void Theme_and_indexed_colors_and_tints_are_resolved()
    {
        var styles = "<fonts><font><sz val=\"10\"/><name val=\"Arial\"/></font><font><color theme=\"4\" tint=\"0.5\"/></font><font><color indexed=\"10\"/></font><font><color indexed=\"64\"/></font><font><color rgb=\"zz\"/></font></fonts>"
            + "<fills><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
            + "<fill><patternFill patternType=\"solid\"><fgColor theme=\"0\" tint=\"-0.5\"/></patternFill></fill>"
            + "<fill><patternFill patternType=\"mediumGray\"><fgColor rgb=\"FF000000\"/><bgColor rgb=\"FFFFFFFF\"/></patternFill></fill>"
            + "<fill><gradientFill><stop position=\"0\"><color rgb=\"FF00FF00\"/></stop><stop position=\"1\"><color rgb=\"FF0000FF\"/></stop></gradientFill></fill></fills>"
            + "<cellXfs><xf/><xf fontId=\"1\" fillId=\"2\"/><xf fontId=\"2\" fillId=\"3\"/><xf fontId=\"3\" fillId=\"4\"/><xf fontId=\"4\" fillId=\"1\"/></cellXfs>";
        var sheet = Single(Workbook("<row><c s=\"1\"><v>1</v></c><c s=\"2\"><v>2</v></c><c s=\"3\"><v>3</v></c><c s=\"4\"><v>4</v></c></row>", styles));
        Assert.Equal(PdfColor.FromRgb(0xA2, 0xB8, 0xE2), sheet[0, 0].Format!.Color); // Accent 1 (4472C4) half lighter.
        Assert.Equal(PdfColor.FromRgb(0x80, 0x80, 0x80), sheet[0, 0].Format!.Fill); // White half darker.
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), sheet[0, 1].Format!.Color);
        Assert.Equal(PdfColor.FromRgb(128, 128, 128), sheet[0, 1].Format!.Fill);
        Assert.Equal(PdfColor.FromRgb(0, 0, 0), sheet[0, 2].Format!.Color);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), sheet[0, 2].Format!.Fill);
        Assert.Null(sheet[0, 3].Format!.Color);
        Assert.Equal(PdfColor.FromRgb(223, 223, 223), sheet[0, 3].Format!.Fill); // 12.5% black on white.
        Assert.Equal(10, sheet.Formatting!.DefaultFontSize);
        Assert.Equal("Arial", sheet.Formatting.DefaultFontName);
    }

    [Fact]
    public void A_custom_palette_and_theme_replace_the_defaults()
    {
        var styles = "<fonts><font/><font><color indexed=\"8\"/></font></fonts><cellXfs><xf/><xf fontId=\"1\"/></cellXfs>"
            + "<colors><indexedColors>" + string.Concat(Enumerable.Range(0, 9).Select(i => $"<rgbColor rgb=\"FF0000{i:X2}\"/>")) + "</indexedColors></colors>";
        var sheet = Single(Workbook("<row><c s=\"1\"><v>1</v></c></row>", styles));
        Assert.Equal(PdfColor.FromRgb(0, 0, 8), sheet[0, 0].Format!.Color);
    }

    [Fact]
    public void Merged_cells_show_the_first_cell_with_the_borders_of_the_edges()
    {
        var styles = "<borders><border/><border><right style=\"thick\"/><bottom style=\"medium\"><color rgb=\"FFFF0000\"/></bottom></border></borders>"
            + "<cellXfs><xf/><xf borderId=\"1\"/></cellXfs>";
        var sheet = Single(Workbook(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Merged</t></is></c><c r=\"C1\" s=\"1\"/></row>"
            + "<row r=\"2\"><c r=\"A2\" s=\"1\"/><c r=\"D2\"><v>9</v></c></row>",
            styles,
            sheetExtra: "<mergeCells><mergeCell ref=\"A1:C2\"/><mergeCell ref=\"B2:D2\"/><mergeCell ref=\"bad\"/></mergeCells>"));
        var merge = Assert.Single(sheet.Formatting!.Merges); // The overlapping one is left out.
        Assert.Equal(new CellMerge(0, 0, 2, 3), merge);
        Assert.Equal("Merged", sheet[0, 0].Text);
        Assert.Equal(2.25, sheet[0, 0].Format!.Right.Width);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), sheet[0, 0].Format!.Bottom.Color);

        var pdf = XlsxToPdf.ConvertToBytes(Workbook(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Merged</t></is></c></row><row r=\"2\"><c r=\"D2\"><v>9</v></c></row>",
            sheetExtra: "<mergeCells><mergeCell ref=\"A1:C2\"/></mergeCells>"));
        Assert.Single(Find(pdf, 0, "Merged"));
    }

    [Fact]
    public void A_merged_range_whose_first_cell_is_hidden_shows_its_value_in_the_first_shown_cell()
    {
        var sheet = Single(Workbook(
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Value</t></is></c><c r=\"C1\"><v>1</v></c></row>",
            sheetBefore: "<cols><col min=\"1\" max=\"1\" hidden=\"1\"/></cols>",
            sheetExtra: "<mergeCells><mergeCell ref=\"A1:B1\"/></mergeCells>"));
        Assert.Equal(["Value", "1"], Texts(sheet, 0));
        Assert.Empty(sheet.Formatting!.Merges);
    }

    [Fact]
    public void Merged_ranges_are_cut_at_the_edges_of_bands_and_header_rows()
    {
        var cells = new List<SheetCell[]>();
        for (var r = 0; r < 60; r++)
        {
            cells.Add([.. Enumerable.Range(0, 12).Select(c => new SheetCell(r == 0 && c == 0 ? "Wide merged header" : r == 0 ? string.Empty : $"r{r}c{c}", r == 0 && c > 0 ? CellKind.Empty : CellKind.Text))]);
        }

        var sheet = new Sheet(cells, 2)
        {
            Formatting = new SheetFormatting
            {
                ColumnWidths = [.. Enumerable.Repeat<double?>(15, 12)],
                Merges = [new CellMerge(0, 0, 3, 12), new CellMerge(10, 1, 50, 2)],
            },
        };
        var pdf = SheetRenderer.Render([sheet], new SheetOptions(), "test", null).ToArray();
        AssertValid(pdf);
        var pages = Pages(pdf);
        Assert.True(pages.Length > 1);
        Assert.Contains("Wide merged header", pages[0], StringComparison.Ordinal);
        Assert.Contains("r3c0", string.Concat(pages), StringComparison.Ordinal);
        Assert.DoesNotContain("r1c0", string.Concat(pages), StringComparison.Ordinal); // Covered by the merged range.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(string.Join("\n", pages), @"\br10c1\b"));
    }

    [Fact]
    public void Print_areas_and_titles_take_whole_rows_and_columns_and_quoted_names()
    {
        Assert.Equal([("My 'sheet'", new CellRange(0, 0, 9, 3))], XlsxWorkbook.ParseAreas("'My ''sheet'''!$A$1:$D$10"));
        Assert.Equal([("S", new CellRange(1, -1, 2, -1)), ("S", new CellRange(-1, 0, -1, 1))], XlsxWorkbook.ParseAreas("S!$2:$3,S!$A:$B"));
        Assert.Equal([((string?)null, new CellRange(4, 2, 4, 2))], XlsxWorkbook.ParseAreas("C5"));
        Assert.Empty(XlsxWorkbook.ParseAreas("S!#REF!"));
        Assert.Null(XlsxWorkbook.ParseRange("A1:$3"));
        Assert.Null(XlsxWorkbook.ParseReference("A0"));
        Assert.Null(XlsxWorkbook.ParseReference("XFE1"));
        Assert.Null(XlsxWorkbook.ParseReference("A1048577"));
        Assert.Equal((1048575, 16383), XlsxWorkbook.ParseReference("$XFD$1048576"));

        var names = "<definedNames><definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">Sheet1!$B:$C</definedName>"
            + "<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Sheet1!$A:$A,Sheet1!$1:$1</definedName>"
            + "<definedName name=\"_xlnm.Print_Area\" localSheetId=\"5\">Other!$A$1</definedName><definedName name=\"Other\">Sheet1!$A$1</definedName></definedNames>";
        var rows = string.Concat(Enumerable.Range(1, 5).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\"><v>{r}</v></c><c r=\"B{r}\"><v>{r * 10}</v></c><c r=\"C{r}\"><v>{r * 100}</v></c><c r=\"D{r}\"><v>0</v></c></row>"));
        var sheet = Single(Workbook(rows, workbookExtra: names));
        Assert.Equal(2, sheet.ColumnCount);
        Assert.Equal(["10", "100"], Texts(sheet, 0));
        Assert.Equal(1, sheet.HeaderRows);
        Assert.Equal(5, sheet.Rows.Count);
    }

    [Fact]
    public void Print_titles_above_the_print_area_are_repeated_too()
    {
        var names = "<definedNames><definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">Sheet1!$A$4:$A$6</definedName>"
            + "<definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Sheet1!$1:$1</definedName></definedNames>";
        var rows = string.Concat(Enumerable.Range(1, 8).Select(r => $"<row r=\"{r}\"><c r=\"A{r}\"><v>{r}</v></c></row>"));
        var sheet = Single(Workbook(rows, workbookExtra: names));
        Assert.Equal(["1", "4", "5", "6"], sheet.Rows.Select(r => r[0].Text));
        Assert.Equal(1, sheet.HeaderRows);
        Assert.Equal(0, sheet.Formatting!.LeadRows);
    }

    [Fact]
    public void Sizes_hidden_rows_and_page_setup_are_read_from_the_sheet()
    {
        var sheet = Single(Workbook(
            "<row r=\"1\" ht=\"40\" customHeight=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"C1\"><v>3</v></c></row>"
            + "<row r=\"2\" ht=\"20\"><c r=\"A2\"><v>2</v></c></row><row r=\"3\" hidden=\"1\"><c r=\"A3\"><v>3</v></c></row>",
            sheetBefore: "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><sheetViews><sheetView rightToLeft=\"1\"/></sheetViews>"
                + "<sheetFormatPr defaultColWidth=\"10\"/><cols><col min=\"2\" max=\"2\" width=\"0\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"30\"/></cols>",
            sheetExtra: "<pageSetup orientation=\"landscape\" scale=\"50\" fitToWidth=\"1\" fitToHeight=\"0\"/>"));
        var formatting = sheet.Formatting!;
        Assert.Equal(2, sheet.Rows.Count);
        Assert.Equal(2, sheet.ColumnCount); // B has no width: hidden.
        Assert.Equal([10.0, 30.0], formatting.ColumnWidths.Select(w => w!.Value));
        Assert.Equal([40.0, null], formatting.RowHeights);
        Assert.True(formatting.Landscape);
        Assert.True(formatting.FitToWidth);
        Assert.Equal(1, formatting.Scale); // Fitting wins over the scale.
        Assert.True(formatting.RightToLeft);

        var scaled = Single(Workbook("<row><c><v>1</v></c></row>", sheetExtra: "<pageSetup scale=\"5\"/>"));
        Assert.Equal(0.1, scaled.Formatting!.Scale);
        Assert.Null(scaled.Formatting.ColumnWidths.Single()); // No sizes at all: fitted.

        var hiddenRows = Single(Workbook("<row r=\"2\"><c r=\"A2\"><v>2</v></c></row><row r=\"3\" hidden=\"0\"/>", sheetBefore: "<sheetFormatPr zeroHeight=\"1\"/>"));
        Assert.Equal(["2"], hiddenRows.Rows.Select(r => r[0].Text));
    }

    [Fact]
    public void Base_column_width_sets_columns_without_one()
    {
        var sheet = Single(Workbook("<row><c r=\"A1\"><v>1</v></c><c r=\"B1\"><v>2</v></c></row>",
            sheetBefore: "<sheetFormatPr baseColWidth=\"10\"/><cols><col min=\"1\" max=\"1\" width=\"20\"/></cols>"));
        Assert.Equal(20, sheet.Formatting!.ColumnWidths[0]);
        Assert.Equal(10 + (5 / 7.0), sheet.Formatting.ColumnWidths[1]!.Value, 6);
    }

    [Fact]
    public void Scale_and_orientation_change_the_layout()
    {
        var rows = "<row><c t=\"inlineStr\"><is><t>Scaled text</t></is></c></row>";
        var full = XlsxToPdf.ConvertToBytes(Workbook(rows));
        var half = XlsxToPdf.ConvertToBytes(Workbook(rows, sheetExtra: "<pageSetup scale=\"50\" orientation=\"landscape\"/>"));
        var fullBounds = Find(full, 0, "Scaled text").Single().Bounds;
        var halfBounds = Find(half, 0, "Scaled text").Single().Bounds;
        Assert.Equal(fullBounds.Width / 2, halfBounds.Width, 0.5);
        var page = PdfReader.Open(half).Pages[0];
        Assert.True(page.Width > page.Height);
        var ignored = XlsxToPdf.ConvertToBytes(Workbook(rows, sheetExtra: "<pageSetup scale=\"50\"/>"), new XlsxConverterOptions { UsePageSetup = false });
        Assert.Equal(fullBounds.Width, Find(ignored, 0, "Scaled text").Single().Bounds.Width, 0.5);
    }

    [Fact]
    public void Right_to_left_sheets_put_the_first_column_at_the_right()
    {
        var rows = "<row><c t=\"inlineStr\"><is><t>First</t></is></c><c t=\"inlineStr\"><is><t>Second</t></is></c></row>";
        var pdf = XlsxToPdf.ConvertToBytes(Workbook(rows, sheetBefore: "<sheetViews><sheetView rightToLeft=\"1\"/></sheetViews>"));
        Assert.True(Find(pdf, 0, "First").Single().Bounds.X > Find(pdf, 0, "Second").Single().Bounds.X);
    }

    [Fact]
    public void Workbook_fonts_are_used_when_the_collection_has_them()
    {
        Assert.Equal("serif", SheetRenderer.GenericFamily("Times New Roman"));
        Assert.Equal("serif", SheetRenderer.GenericFamily("Cambria"));
        Assert.Equal("monospace", SheetRenderer.GenericFamily("Courier New"));
        Assert.Equal("monospace", SheetRenderer.GenericFamily("Consolas"));
        Assert.Equal("monospace", SheetRenderer.GenericFamily("Liberation Mono"));
        Assert.Null(SheetRenderer.GenericFamily("Calibri"));
        Assert.Null(SheetRenderer.GenericFamily("Microsoft Sans Serif"));

        var styles = "<fonts><font><name val=\"Liberation Serif\"/></font><font><name val=\"Courier New\"/></font><font><name val=\"Georgia\"/></font></fonts><cellXfs><xf/><xf fontId=\"1\"/><xf fontId=\"2\"/></cellXfs>";
        var pdf = XlsxToPdf.ConvertToBytes(Workbook("<row><c t=\"inlineStr\"><is><t>Serif</t></is></c><c s=\"1\" t=\"inlineStr\"><is><t>Mono</t></is></c><c s=\"2\" t=\"inlineStr\"><is><t>Generic serif</t></is></c></row>", styles));
        var text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("LiberationSerif", text, StringComparison.Ordinal);
        Assert.Contains("LiberationMono", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LiberationSans", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_culture_formats_numbers_and_dates()
    {
        var styles = "<cellXfs><xf/><xf numFmtId=\"4\"/><xf numFmtId=\"14\"/></cellXfs>";
        var xlsx = Workbook("<row><c s=\"1\"><v>1234.5</v></c><c s=\"2\"><v>46345</v></c></row>", styles);
        Assert.Equal(["1,234.50", "2026-11-19"], Texts(Single(xlsx), 0));
        Assert.Equal(["1.234,50", "19.11.2026"], Texts(Single(xlsx, new XlsxConverterOptions { Culture = CultureInfo.GetCultureInfo("de-DE") }), 0));
    }

    [Fact]
    public void Number_format_colors_and_cell_styles_combine()
    {
        var styles = "<numFmts><numFmt numFmtId=\"164\" formatCode=\"0;[Blue]-0\"/></numFmts><fonts><font><b/></font></fonts><cellXfs><xf numFmtId=\"164\" fontId=\"0\"/></cellXfs>";
        var sheet = Single(Workbook("<row><c><v>-1</v></c><c><v>-2</v></c><c><v>3</v></c></row>", styles));
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), sheet[0, 0].Format!.Color);
        Assert.True(sheet[0, 0].Format!.Bold);
        Assert.Same(sheet[0, 0].Format, sheet[0, 1].Format); // Shared, not one per cell.
        Assert.Null(sheet[0, 2].Format!.Color);
    }

    [Fact]
    public void Booleans_in_fonts_and_alignment_values_are_read()
    {
        var styles = "<fonts><font><b val=\"0\"/><i val=\"false\"/><u val=\"double\"/><strike val=\"1\"/><sz val=\"999\"/></font><font><u val=\"none\"/><b/></font></fonts>"
            + "<cellXfs><xf><alignment horizontal=\"fill\" vertical=\"center\" wrapText=\"true\" indent=\"999\"/></xf><xf fontId=\"1\"><alignment horizontal=\"centerContinuous\" vertical=\"top\"/></xf>"
            + "<xf><alignment horizontal=\"justify\" vertical=\"distributed\"/></xf></cellXfs>";
        var sheet = Single(Workbook("<row><c><v>1</v></c><c s=\"1\"><v>2</v></c><c s=\"2\"><v>3</v></c></row>", styles));
        var first = sheet[0, 0].Format!;
        Assert.False(first.Bold);
        Assert.False(first.Italic);
        Assert.Equal(CellUnderline.Double, first.Underline);
        Assert.True(first.Strikethrough);
        Assert.Null(first.FontSize);
        Assert.Equal(CellAlignment.Left, sheet[0, 0].Alignment);
        Assert.Equal(VerticalAlignment.Middle, first.Vertical);
        Assert.True(first.Wrap);
        Assert.Equal(250, first.Indent);
        Assert.True(sheet[0, 1].Format!.Bold);
        Assert.Equal(CellUnderline.None, sheet[0, 1].Format!.Underline);
        Assert.Equal(CellAlignment.Center, sheet[0, 1].Alignment);
        Assert.Equal(VerticalAlignment.Top, sheet[0, 1].Format!.Vertical);
        Assert.Equal(CellAlignment.Left, sheet[0, 2].Alignment);
        Assert.Equal(VerticalAlignment.Middle, sheet[0, 2].Format!.Vertical);
    }

    [Fact]
    public void Empty_cells_with_fills_or_borders_are_printed()
    {
        var styles = "<fills><fill/><fill/><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFF0000\"/></patternFill></fill></fills><cellXfs><xf/><xf fillId=\"2\"/></cellXfs>";
        var sheet = Single(Workbook("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"3\"><c r=\"C3\" s=\"1\"/><c r=\"D3\"/></row>", styles));
        Assert.Equal(3, sheet.Rows.Count);
        Assert.Equal(3, sheet.ColumnCount);
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), sheet[2, 2].Format!.Fill);
    }

    [Fact]
    public void An_empty_workbook_is_one_empty_page()
    {
        var pdf = XlsxToPdf.ConvertToBytes(Workbook(string.Empty));
        AssertValid(pdf);
        Assert.Single(PdfReader.Open(pdf).Pages);
        var none = XlsxToPdf.ConvertToBytes(Fixture("multi.xlsx"), new XlsxConverterOptions { SheetNames = [] });
        Assert.Single(PdfReader.Open(none).Pages);
    }

    [Fact]
    public void Parts_in_other_encodings_and_folders_are_read()
    {
        Assert.Equal("xl/worksheets/sheet1.xml", XlsxPackage.ResolveTarget("xl", "worksheets/sheet1.xml"));
        Assert.Equal("xl/sheet.xml", XlsxPackage.ResolveTarget("xl/worksheets", "../sheet.xml"));
        Assert.Equal("xl/a b.xml", XlsxPackage.ResolveTarget("docs", "/xl/a%20b.xml"));
        Assert.Equal("a.xml", XlsxPackage.ResolveTarget(string.Empty, "../../a.xml"));
        Assert.Equal("x/y.xml", XlsxPackage.ResolveTarget("x", "./y.xml"));
        Assert.Equal("xl/_rels/workbook.xml.rels", XlsxPackage.RelationshipsOf("xl/workbook.xml"));
        Assert.Equal("_rels/.rels", XlsxPackage.RelationshipsOf(string.Empty));
        Assert.Equal("_rels/w.xml.rels", XlsxPackage.RelationshipsOf("w.xml"));

        Assert.Equal("<a/>", XlsxPackage.Decode([0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a', (byte)'/', (byte)'>']));
        Assert.Equal("<a/>", XlsxPackage.Decode([.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("<a/>")]));
        Assert.Equal("<a/>", XlsxPackage.Decode([.. new byte[] { 0xFE, 0xFF }, .. Encoding.BigEndianUnicode.GetBytes("<a/>")]));
        Assert.Equal("<a/>", XlsxPackage.Decode(Encoding.Unicode.GetBytes("<a/>")));
        Assert.Equal("<a/>", XlsxPackage.Decode(Encoding.BigEndianUnicode.GetBytes("<a/>")));

        // A UTF-16 worksheet whose declaration claims another encoding, in a folder of its own.
        var parts = new List<(string, string)>
        {
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"/book/main.xml\"/></Relationships>"),
            ("book/main.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("book/_rels/main.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/worksheet\" Target=\"../sheets/one.xml\"/></Relationships>"),
        };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            using var sheetStream = zip.CreateEntry("sheets/one.xml").Open();
            sheetStream.Write([0xFF, 0xFE]);
            sheetStream.Write(Encoding.Unicode.GetBytes($"<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><worksheet xmlns=\"{Main}\"><sheetData><row><c t=\"inlineStr\"><is><t>\u00FCber</t></is></c></row></sheetData></worksheet>"));
        }

        Assert.Equal("\u00FCber", Single(buffer.ToArray())[0, 0].Text);
    }

    [Fact]
    public void Chart_sheets_are_left_out()
    {
        var parts = Package(
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"Chart\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Data\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{PackageRelationships}\"><Relationship Id=\"rId1\" Type=\"{Relationships}/chartsheet\" Target=\"chartsheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{Relationships}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId3\" Type=\"{Relationships}/worksheet\" Target=\"http://example.com/x.xml\" TargetMode=\"External\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row><c><v>1</v></c></row></sheetData></worksheet>"));
        Assert.Equal("1", Single(parts)[0, 0].Text);
        Assert.Empty(XlsxConverter.Read(parts, new XlsxConverterOptions { SheetNames = ["Chart"] }));
    }

    [Fact]
    public void Text_is_never_lost()
    {
        foreach (var name in new[] { "styled.xlsx", "multi.xlsx", "formats.xlsx", "minimal.xlsx" })
        {
            var xlsx = Fixture(name);
            var shown = string.Concat(Pages(XlsxToPdf.ConvertToBytes(xlsx))).Where(c => !char.IsWhiteSpace(c)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
            foreach (var sheet in XlsxConverter.Read(xlsx))
            {
                var expected = sheet.Rows.SelectMany(r => r).SelectMany(c => SheetRenderer.DisplayText(c.Text)).Where(c => !char.IsWhiteSpace(c)).GroupBy(c => c);
                foreach (var group in expected)
                {
                    Assert.True(shown.GetValueOrDefault(group.Key) >= group.Count(), $"{name}: '{group.Key}' shown {shown.GetValueOrDefault(group.Key)} times, not {group.Count()}");
                }
            }
        }
    }
}

/// <summary>Test helpers for packages.</summary>
internal static class XlsxTestPackages
{
    /// <summary>The workbook with <c>date1904="1"</c> set on its workbook part.</summary>
    public static byte[] Replace1904(this byte[] xlsx)
    {
        using var input = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var parts = input.Entries.Select(e =>
        {
            using var reader = new StreamReader(e.Open());
            var text = reader.ReadToEnd();
            return (e.FullName, e.FullName == "xl/workbook.xml" ? text.Replace("<sheets>", "<workbookPr date1904=\"1\"/><sheets>", StringComparison.Ordinal) : text);
        }).ToArray();
        return XlsxToPdfTests.Package(parts);
    }
}
