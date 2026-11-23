using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// The Workbook API reads what openpyxl, an independent reader, reads from the same workbooks
/// (<c>Fixtures/Workbooks/expected.json</c>, from <c>make-workbooks.py</c>): sheets, typed values (dates by their
/// number formats, in either date system), formulas (shared ones copied to their cells), merged ranges, hidden rows
/// and columns, and defined names.
/// </summary>
public class WorkbookOracleTests
{
    private static readonly Lazy<JsonElement> Expected = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path(Path.Combine("Workbooks", "expected.json")))).RootElement);

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var file in Expected.Value.EnumerateObject())
        {
            data.Add(file.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Reads_what_openpyxl_reads(string file)
    {
        var expected = Expected.Value.GetProperty(file);
        Assert.SkipWhen(expected.TryGetProperty("error", out var error), $"openpyxl cannot read it ({error}).");
        var workbook = Workbook.Open(Fixtures.Read(Path.Combine("Workbooks", file)));
        Assert.Equal(expected.GetProperty("date1904").GetBoolean(), workbook.Date1904);
        var sheets = expected.GetProperty("sheets").EnumerateArray().ToList();
        Assert.Equal(sheets.Select(s => s.GetProperty("name").GetString()), workbook.Worksheets.Select(s => s.Name));
        foreach (var expectedSheet in sheets)
        {
            var sheet = workbook.Worksheets[expectedSheet.GetProperty("name").GetString()!];
            Assert.Equal(expectedSheet.GetProperty("hidden").GetBoolean(), sheet.IsHidden);
            var cells = expectedSheet.GetProperty("cells").EnumerateObject().ToList();
            foreach (var cell in cells)
            {
                AssertValue(sheet[cell.Name], cell.Value, $"{file} {sheet.Name}!{cell.Name}");
            }

            if (cells.Count < 300)
            {
                Assert.Equal(cells.Select(c => c.Name).Order(StringComparer.Ordinal), sheet.Cells.Where(c => c.Type != CellValueType.Empty).Select(c => c.Address).Order(StringComparer.Ordinal));
            }

            foreach (var formula in expectedSheet.GetProperty("formulas").EnumerateObject())
            {
                Assert.True(formula.Value.GetString() == sheet[formula.Name].Formula, $"{file} {sheet.Name}!{formula.Name}: {formula.Value.GetString()} != {sheet[formula.Name].Formula}");
            }

            Assert.Equal(expectedSheet.GetProperty("merged").EnumerateArray().Select(m => m.GetString()).Order(StringComparer.Ordinal), sheet.MergedRanges.Order(StringComparer.Ordinal));
            var hiddenRows = expectedSheet.GetProperty("hiddenRows").EnumerateArray().Select(r => r.GetInt32()).ToList();
            var lastRow = Math.Max(hiddenRows.DefaultIfEmpty(0).Max(), sheet.LastRow);
            Assert.Equal(hiddenRows, Enumerable.Range(1, lastRow).Where(r => sheet.Row(r).IsHidden));
            var hiddenColumns = expectedSheet.GetProperty("hiddenColumns").EnumerateArray().Select(c => c.GetInt32()).ToList();
            var lastColumn = Math.Max(hiddenColumns.DefaultIfEmpty(0).Max(), sheet.LastColumn);
            Assert.Equal(hiddenColumns, Enumerable.Range(1, lastColumn).Where(c => sheet.Column(c).IsHidden));
        }

        // Workbook-level defined names, as stored.
        var names = workbook.WorkbookXml.Descendants(workbook.Ns + "definedName").Where(n => n.Attribute("localSheetId") is null)
            .ToDictionary(n => (string)n.Attribute("name")!, n => n.Value);
        foreach (var name in expected.GetProperty("names").EnumerateObject())
        {
            Assert.Equal(name.Value.GetString(), names.GetValueOrDefault(name.Name));
        }
    }

    private static void AssertValue(Cell cell, JsonElement expected, string where)
    {
        var type = expected[0].GetString();
        var value = expected[1];
        switch (type)
        {
            case "s":
                // openpyxl leaves the _xHHHH_ escapes of inline strings as they are; ECMA-376 (and Excel) decode them.
                Assert.True(cell.Type == CellValueType.Text, $"{where}: {cell.Type}");
                Assert.Equal(Regex.Replace(value.GetString()!, "_x([0-9A-Fa-f]{4})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()), cell.TextValue);
                break;
            case "dur":
                // Elapsed time ([h]:mm:ss), which openpyxl reads as a duration: a date and time here, from its serial number.
                Assert.True(cell.Type == CellValueType.DateTime, $"{where}: {cell.Type}");
                Assert.Equal(value.GetDouble(), cell.NumberValue!.Value * 86400, 3);
                break;
            case "e":
                Assert.True(cell.Type == CellValueType.Error, $"{where}: {cell.Type}");
                Assert.Equal(value.GetString(), cell.ErrorValue);
                break;
            case "n":
                Assert.True(cell.Type == CellValueType.Number, $"{where}: {cell.Type} {cell.Value}");
                Assert.Equal(value.GetDouble(), cell.NumberValue!.Value, 9);
                break;
            case "b":
                Assert.True(cell.Type == CellValueType.Boolean, $"{where}: {cell.Type}");
                Assert.Equal(value.GetBoolean(), cell.BooleanValue);
                break;
            case "d":
                Assert.True(cell.Type == CellValueType.DateTime, $"{where}: {cell.Type} {cell.Value}");
                var date = DateTime.ParseExact(value.GetString()!, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                Assert.True(Math.Abs((date - cell.DateTimeValue!.Value).TotalSeconds) < 1, $"{where}: {date:o} != {cell.DateTimeValue:o}");
                break;
            case "t":
                Assert.True(cell.Type == CellValueType.DateTime, $"{where}: {cell.Type} {cell.Value}");
                var time = TimeSpan.ParseExact(value.GetString()!, "hh\\:mm\\:ss", CultureInfo.InvariantCulture);
                Assert.True(Math.Abs((time - cell.DateTimeValue!.Value.TimeOfDay).TotalSeconds) < 1, $"{where}: {time} != {cell.DateTimeValue:o}");
                break;
            default:
                Assert.Fail($"{where}: unknown type {type}");
                break;
        }
    }
}
