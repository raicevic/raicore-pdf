#pragma warning disable CA1849, CA2007
using System.Globalization;
using System.IO.Compression;
using RaiCore.Pdf;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets;

namespace Snippets;

internal static class Sheets
{
    public static async Task RunAsync()
    {
        // Data to convert: a header row, numbers, a quoted field with a comma and a line break.
        await File.WriteAllTextAsync("orders.csv", """
            Order,Customer,Date,Quantity,Total,Note
            1001,Alice Example,2026-11-19,3,"1,234.50","Leave at the door, please"
            1002,Bob Example,2026-11-20,12,99.00,"Two lines:
            second line"
            """);
        await File.WriteAllTextAsync("prices.csv", "Artikel;Menge;Preis\nKaffee;2;1.234,56\nTee;10;0,99\n");

        #region convert
        var pdf = CsvToPdf.Convert("orders.csv");
        pdf.Save("orders.pdf");
        #endregion

        #region options
        var options = new CsvConverterOptions
        {
            Delimiter = ';',
            DecimalSeparator = ',',
            Sheet = new SheetOptions
            {
                Page = new PageSetup(PdfPageSize.Letter, Thickness.All(36)),
                Landscape = true,
                FitToWidth = true,
                FontSize = 10,
                Title = "Prices",
                Tagged = true,
            },
        };
        await using (var csv = File.OpenRead("prices.csv"))
        {
            var document = await CsvToPdf.ConvertAsync(csv, options);
            document.Save("prices.pdf");
        }
        #endregion

        // A small workbook to convert: a sheet with a bold header row, amounts in a number format and dates.
        WriteWorkbook("summary.xlsx");

        #region xlsx
        var workbook = XlsxToPdf.Convert("summary.xlsx", new XlsxConverterOptions
        {
            SheetNames = ["Summary"],    // The sheets to convert; all visible ones by default.
            ShowSheetNames = true,       // Each sheet's name as a heading.
            Culture = CultureInfo.GetCultureInfo("en-US"), // Separators, month names, short dates.
            PrintDate = new DateTime(2026, 11, 24), // The date headers and footers print (&D); now by default.
            Sheet = new SheetOptions { Page = new PageSetup(PdfPageSize.Letter, Thickness.All(36)), FitToWidth = true },
        });
        workbook.Save("summary.pdf");
        #endregion

        #region diagnostics
        var diagnostics = new List<SheetDiagnostic>();
        var checkedWorkbook = XlsxToPdf.Convert("summary.xlsx", new XlsxConverterOptions
        {
            Sheet = new SheetOptions { Diagnostics = diagnostics.Add },
        });
        foreach (var diagnostic in diagnostics)
        {
            Console.WriteLine($"{diagnostic.SheetName}: {diagnostic.Kind}: {diagnostic.Message}"); // Summary: Comment: 1 comment is left out.
        }

        checkedWorkbook.Save("summary-checked.pdf");
        #endregion

        #region fonts
        var fonts = BundledFonts.CreateCollection().AddSystemFonts();
        var unicode = CsvToPdf.Convert("orders.csv", new CsvConverterOptions
        {
            Sheet = new SheetOptions { Fonts = fonts, FallbackFontFamilies = ["Noto Sans CJK JP", "Noto Sans Arabic"] },
        });
        unicode.Save("orders-unicode.pdf");
        #endregion
    }

    /// <summary>
    /// Writes a minimal workbook with one styled sheet and a comment (a spreadsheet program would write the same parts).
    /// </summary>
    private static void WriteWorkbook(string path)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string package = "http://schemas.openxmlformats.org/package/2006/relationships";
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{relationships}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{main}\" xmlns:r=\"{relationships}\"><sheets><sheet name=\"Summary\" sheetId=\"1\" r:id=\"r1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{relationships}/worksheet\" Target=\"sheet1.xml\"/>"
                + $"<Relationship Id=\"r2\" Type=\"{relationships}/styles\" Target=\"styles.xml\"/></Relationships>",
            ["xl/_rels/sheet1.xml.rels"] = $"<Relationships xmlns=\"{package}\"><Relationship Id=\"r1\" Type=\"{relationships}/comments\" Target=\"comments1.xml\"/></Relationships>",
            ["xl/comments1.xml"] = $"<comments xmlns=\"{main}\"><authors><author>Alice</author></authors><commentList>"
                + "<comment ref=\"B2\" authorId=\"0\"><text><t>Includes the delivery.</t></text></comment></commentList></comments>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{main}\"><fonts><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/></font></fonts>"
                + "<cellXfs><xf/><xf fontId=\"1\"/><xf numFmtId=\"4\"/><xf numFmtId=\"15\"/></cellXfs></styleSheet>",
            ["xl/sheet1.xml"] = $"<worksheet xmlns=\"{main}\"><sheetData>"
                + "<row><c s=\"1\" t=\"inlineStr\"><is><t>Item</t></is></c><c s=\"1\" t=\"inlineStr\"><is><t>Amount</t></is></c><c s=\"1\" t=\"inlineStr\"><is><t>Due</t></is></c></row>"
                + "<row><c t=\"inlineStr\"><is><t>Coffee</t></is></c><c s=\"2\"><v>1234.5</v></c><c s=\"3\"><v>46345</v></c></row>"
                + "</sheetData></worksheet>",
        };
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(xml);
        }
    }
}
