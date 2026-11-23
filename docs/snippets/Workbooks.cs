#pragma warning disable CA1849, CA2007
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Sheets;

namespace Snippets;

internal static class Workbooks
{
    public static Task RunAsync()
    {
        // A template to fill: a sheet with a title, labels, a table of lines and a total.
        var template = Workbook.Create();
        var setup = template.Worksheets[0];
        setup["A1"].SetValue("Invoice");
        setup["A3"].SetValue("Customer:");
        setup["A4"].SetValue("Date:");
        setup["A6"].SetValue("Item");
        setup["B6"].SetValue("Amount");
        setup["A7"].SetValue("Item 1");
        setup["B7"].SetValue(0);
        setup["B7"].NumberFormat = "#,##0.00";
        setup["A8"].SetValue("Total");
        setup["B8"].SetFormula("SUM(B7:B7)");
        setup.Row(9).IsHidden = true;
        template.Save("template.xlsx");

        #region open
        var workbook = Workbook.Open("template.xlsx");
        var sheet = workbook.Worksheets[0];          // Or by name: workbook.Worksheets["Invoice"].
        sheet["B3"].SetValue("Alice Example");      // Text.
        sheet["B4"].SetValue(new DateTime(2026, 11, 23)); // A date: a number in a date format.
        sheet.Cell(7, 2).SetValue(1234.5);          // Row 7, column 2: B7.
        sheet["C8"].SetFormula("B8*1.25");          // Calculated by Excel when it opens the file.

        var total = sheet["B8"];
        Console.WriteLine($"{total.Formula} = {total.Value} ({total.Type})"); // SUM(B7:B7) =  (Empty): calculated when Excel opens it.
        workbook.Save("invoice.xlsx");
        #endregion

        #region find
        var found = workbook.Find("total", new FindOptions { Match = FindMatch.WholeCell });
        foreach (var cell in found)
        {
            Console.WriteLine($"{cell.Worksheet.Name}!{cell.Address}");
        }

        var dates = sheet.Find(@"^\d{4}-\d{2}-\d{2}$", new FindOptions { Match = FindMatch.Regex });
        #endregion

        #region insert
        // Two more lines before the total: the total's formula becomes SUM(B7:B9), and everything below moves down.
        sheet.InsertRows(8, 2);
        sheet["A8"].SetValue("Item 2");
        sheet["B8"].SetValue(99.5);
        sheet.DeleteColumns(3);                     // References to deleted cells become #REF!.
        #endregion

        #region hidden
        var row = sheet.Row(11);
        Console.WriteLine($"Row 11 hidden: {row.IsHidden}, outline level {row.OutlineLevel}");
        sheet.Column(4).IsHidden = true;
        sheet.Row(2).Height = 6;
        #endregion

        #region csv
        workbook.SaveAsCsv("invoice.csv", new CsvSaveOptions
        {
            Separator = ';',
            Encoding = Encoding.UTF8,
            ByteOrderMark = true,                   // So that Excel reads it as UTF-8.
            Culture = CultureInfo.GetCultureInfo("de-DE"), // 1.234,50
            IncludeHiddenRows = false,
        });
        #endregion

        #region convert
        var pdf = XlsxToPdf.Convert(workbook);       // With the changes, without saving first.
        pdf.Save("invoice.pdf");
        #endregion

        #region readonly
        var archive = Workbook.Open("invoice.csv"); // Also .xls: read, find and save as CSV.
        Console.WriteLine($"{archive.Format}, read only: {archive.IsReadOnly}, B3 = {archive.Worksheets[0]["B3"].Value}");
        #endregion

        _ = dates;
        return Task.CompletedTask;
    }
}
