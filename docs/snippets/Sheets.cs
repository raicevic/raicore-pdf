#pragma warning disable CA1849, CA2007
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

        #region fonts
        var fonts = BundledFonts.CreateCollection().AddSystemFonts();
        var unicode = CsvToPdf.Convert("orders.csv", new CsvConverterOptions
        {
            Sheet = new SheetOptions { Fonts = fonts, FallbackFontFamilies = ["Noto Sans CJK JP", "Noto Sans Arabic"] },
        });
        unicode.Save("orders-unicode.pdf");
        #endregion
    }
}
