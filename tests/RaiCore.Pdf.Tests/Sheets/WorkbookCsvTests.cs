using System.Globalization;
using System.Text;
using RaiCore.Pdf.Sheets;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Saving a worksheet as delimited text: values as displayed or as stored, separators, quoting, encodings, line endings, hidden rows and columns.</summary>
public class WorkbookCsvTests
{
    private static byte[] Csv(Workbook workbook, CsvSaveOptions? options = null)
    {
        using var stream = new MemoryStream();
        workbook.SaveAsCsv(stream, options);
        return stream.ToArray();
    }

    private static string[] Lines(Workbook workbook, CsvSaveOptions? options = null) =>
        Encoding.UTF8.GetString(Csv(workbook, options)).Split("\r\n");

    [Fact]
    public void Writes_the_active_sheet_as_displayed()
    {
        var lines = Lines(Workbook.Open(WorkbookTests.Template));
        Assert.Equal("Name,Amount,Date,Flag,Double,Array,Hidden column", lines[0]);
        Assert.Equal("Alpha,10.00,2026-01-01,TRUE,20,100,", lines[1]);
        Assert.Equal(",150.00,,,,,", lines[6]);
        Assert.Equal("Hidden row,,,,,,", lines[7]);
        Assert.Equal("To other,Example,,,,,", lines[12]);
        Assert.Equal(string.Empty, lines[13]);
        Assert.Equal(14, lines.Length);
    }

    [Fact]
    public void Writes_values_as_stored_or_in_a_culture()
    {
        var workbook = Workbook.Open(WorkbookTests.Template);
        var raw = Lines(workbook, new CsvSaveOptions { FormattedValues = false });
        Assert.Equal("Alpha,10,2026-01-01,TRUE,20,100,", raw[1]);
        var german = Lines(workbook, new CsvSaveOptions { Separator = ';', Culture = CultureInfo.GetCultureInfo("de-DE") });
        Assert.Equal("Alpha;10,00;2026-01-01;TRUE;20;100;", german[1].Replace("01.01.2026", "2026-01-01", StringComparison.Ordinal));
        Assert.Equal(";150,00;;;;;", german[6]);
        var created = Workbook.Create();
        created.Worksheets[0]["A1"].SetValue(new DateTime(2026, 11, 23, 8, 30, 15));
        created.Worksheets[0]["B1"].SetValue(1.0 / 3);
        created.Worksheets[0]["C1"].SetValue(123456789012345678.0);
        Assert.Equal("2026-11-23T08:30:15,0.3333333333333333,1.2345678901234568E+17", Lines(created, new CsvSaveOptions { FormattedValues = false })[0]);
        Assert.Equal("2026-11-23 8:30,0.333333333333333,1.23456789012346E+17", Lines(created)[0]);
    }

    [Fact]
    public void Quotes_fields_as_RFC_4180_has_it()
    {
        var workbook = Workbook.Create();
        var sheet = workbook.Worksheets[0];
        sheet["A1"].SetValue("plain");
        sheet["B1"].SetValue("with,comma");
        sheet["C1"].SetValue("with \"quotes\"");
        sheet["D1"].SetValue("two\nlines");
        sheet["E1"].SetValue(" padded ");
        sheet["F1"].SetValue("semi;colon");
        sheet["G1"].SetValue("trailing ");
        Assert.Equal("plain,\"with,comma\",\"with \"\"quotes\"\"\",\"two\nlines\",\" padded \",semi;colon,\"trailing \"\r\n", Encoding.UTF8.GetString(Csv(workbook)));
        sheet["G1"].Clear();
        Assert.Equal("plain;with,comma;\"with \"\"quotes\"\"\";\"two\nlines\";\" padded \";\"semi;colon\"\n",
            Encoding.UTF8.GetString(Csv(workbook, new CsvSaveOptions { Separator = ';', LineEnding = "\n" })));
        Assert.Equal("\"plain\"\t\"with,comma\"\t\"with \"\"quotes\"\"\"\t\"two\nlines\"\t\" padded \"\t\"semi;colon\"\r\n",
            Encoding.UTF8.GetString(Csv(workbook, new CsvSaveOptions { Separator = '\t', Quoting = CsvQuoting.Always })));
    }

    [Fact]
    public void Writes_in_the_encoding_with_or_without_a_byte_order_mark()
    {
        var workbook = Workbook.Create();
        workbook.Worksheets[0]["A1"].SetValue("caf\u00E9 \u20AC5 \u6F22");
        Assert.Equal(Encoding.UTF8.GetBytes("caf\u00E9 \u20AC5 \u6F22\r\n"), Csv(workbook));
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("caf\u00E9 \u20AC5 \u6F22\r\n")], Csv(workbook, new CsvSaveOptions { ByteOrderMark = true }));
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("caf\u00E9 \u20AC5 \u6F22\r\n")], Csv(workbook, new CsvSaveOptions { Encoding = Encoding.Unicode, ByteOrderMark = true }));
        Assert.Equal(Encoding.Unicode.GetBytes("caf\u00E9 \u20AC5 \u6F22\r\n"), Csv(workbook, new CsvSaveOptions { Encoding = new UnicodeEncoding(false, false) }));
        var windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        Assert.Equal([(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)' ', 0x80, (byte)'5', (byte)' ', (byte)'?', 13, 10], Csv(workbook, new CsvSaveOptions { Encoding = windows1252 }));
        Assert.Equal(Csv(workbook, new CsvSaveOptions { Encoding = windows1252 }), Csv(workbook, new CsvSaveOptions { Encoding = windows1252, ByteOrderMark = true }));
    }

    [Fact]
    public void Leaves_out_hidden_rows_and_columns_when_asked()
    {
        var workbook = Workbook.Open(WorkbookTests.Template);
        var lines = Lines(workbook, new CsvSaveOptions { IncludeHiddenRows = false, IncludeHiddenColumns = false });
        Assert.Equal("Name,Amount,Date,Flag,Double,Array", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Hidden row", StringComparison.Ordinal));
        Assert.Equal("Merged,,,,,", lines[7]);
    }

    [Fact]
    public void Writes_the_sheet_the_options_name()
    {
        var workbook = Workbook.Open(WorkbookTests.Template);
        Assert.Equal("Item,Amount,Note", Lines(workbook, new CsvSaveOptions { SheetName = "tab" })[0]);
        Assert.Equal("Secret", Lines(workbook, new CsvSaveOptions { SheetName = "Secret" })[0]);
        Assert.Throws<ArgumentException>(() => Csv(workbook, new CsvSaveOptions { SheetName = "Missing" }));
        Assert.Throws<ArgumentException>(() => Csv(workbook, new CsvSaveOptions { Separator = '"' }));
        Assert.Throws<ArgumentException>(() => Csv(workbook, new CsvSaveOptions { Separator = '\n' }));
        Assert.Equal(string.Empty, Encoding.UTF8.GetString(Csv(Workbook.Create())));
    }

    [Fact]
    public void Delimited_text_round_trips_with_another_separator()
    {
        var source = "a,\"b,c\",d\r\n1,\"two\r\nlines\",\"say \"\"hi\"\"\"\r\n,,x\r\n";
        var workbook = Workbook.Open(Encoding.UTF8.GetBytes(source));
        Assert.Equal(source, Encoding.UTF8.GetString(Csv(workbook)).Replace("\"two\nlines\"", "\"two\r\nlines\"", StringComparison.Ordinal));
        Assert.Equal("a\tb,c\td\r\n1\t\"two\r\nlines\"\t\"say \"\"hi\"\"\"\r\n\t\tx\r\n",
            Encoding.UTF8.GetString(Csv(workbook, new CsvSaveOptions { Separator = '\t' })));
        var path = Path.Combine(Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.csv");
        try
        {
            workbook.SaveAsCsv(path, new CsvSaveOptions { Separator = ';' });
            Assert.Equal("a;b,c;d", File.ReadAllText(path).Split("\r\n")[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Excel_97_2003_workbooks_are_saved_as_delimited_text()
    {
        var workbook = Workbook.Open(Fixtures.Read(Path.Combine("Sheets", "Xls", "SimpleWithPrintArea.xls")));
        var text = Encoding.UTF8.GetString(Csv(workbook));
        Assert.NotEmpty(text);
        Assert.Equal(workbook.Worksheets[0].LastRow, text.Split("\r\n", StringSplitOptions.None).Length - 1);
    }
}
