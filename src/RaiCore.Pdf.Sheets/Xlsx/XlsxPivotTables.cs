using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Applies the style of a worksheet's pivot tables to the cells they are stored in (Excel keeps a pivot table's cells
/// in the sheet; its part says where it is and how it looks): the built-in styles <c>PivotStyleLight15</c> to
/// <c>PivotStyleLight21</c> (<c>PivotStyleLight16</c> is Excel's default; the others are its pattern in the other theme
/// colors): header rows bold on a light fill with a line under them, subtotal rows and the grand total column bold, and
/// the grand total row bold on the light fill with a line over it. Pivot tables with other styles are reported.
/// </summary>
internal static class XlsxPivotTables
{
    /// <summary>The most pivot tables of a sheet styled.</summary>
    private const int MaxTables = 1000;

    /// <summary>Applies the style of the pivot tables a worksheet refers to; a part that is damaged is left out (and reported).</summary>
    public static void Apply(XlsxPackage package, string sheetPath, XlsxWorksheet sheet, XlsxColors colors, long maxCells)
    {
        List<(string Id, string Type, string Target)> relationships;
        try
        {
            relationships = package.Relationships(sheetPath);
        }
        catch (SpreadsheetFormatException)
        {
            return;
        }

        foreach (var (_, type, target) in relationships.Where(r => r.Type == "pivotTable").Take(MaxTables))
        {
            XElement? definition;
            try
            {
                definition = package.Parse(target, reader => XElement.Load(reader, LoadOptions.None), null);
            }
            catch (Exception exception) when (exception is SpreadsheetFormatException or XmlException)
            {
                definition = null;
            }

            if (definition is null || !Apply(definition, sheet, colors, maxCells))
            {
                sheet.Unsupported.Add(SheetDiagnosticKind.PivotTable);
            }
        }
    }

    /// <summary>Applies a pivot table's style to its range; <c>false</c> for a style that is not supported.</summary>
    internal static bool Apply(XElement definition, XlsxWorksheet sheet, XlsxColors colors, long maxCells)
    {
        XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
        int Number(XElement? e, string name, int fallback) =>
            int.TryParse((string?)e?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        var location = Child(definition, "location");
        var info = Child(definition, "pivotTableStyleInfo");
        if (location is null || XlsxWorkbook.ParseRange((string?)location.Attribute("ref") ?? string.Empty) is not { Row1: >= 0, Column1: >= 0 } range)
        {
            return false;
        }

        var name = (string?)info?.Attribute("name");
        if (name is null)
        {
            return true; // No style: plain cells, as Excel shows them.
        }

        if (!name.StartsWith("PivotStyleLight", StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan("PivotStyleLight".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 15 or > 21)
        {
            return false;
        }

        // The theme color of the style: Text 1 for Light15, then the accents 1 to 6.
        var theme = number == 15 ? "1" : (number - 12).ToString(CultureInfo.InvariantCulture);
        var accent = colors.Resolve(null, null, theme, null, null) ?? PdfColor.Black;
        var light = colors.Resolve(null, null, theme, "0.79998168889431442", null) ?? PdfColor.FromRgb(0xD9, 0xE1, 0xF2);
        var line = new CellBorder(0.5, accent);

        // The header rows, then a row for each row item (subtotals and the grand total marked by their type).
        var headers = Math.Clamp(Number(location, "firstDataRow", 1), 1, range.Row2 - range.Row1 + 1);
        var items = Child(definition, "rowItems")?.Elements().Where(e => e.Name.LocalName == "i").Take(range.Row2 - range.Row1 + 1)
            .Select(i => (string?)i.Attribute("t")).ToList() ?? [];
        var grandColumn = Child(definition, "colItems")?.Elements().LastOrDefault(e => e.Name.LocalName == "i") is { } last
            && (string?)last.Attribute("t") == "grand" && Child(definition, "colFields") is not null && info!.Attribute("showLastColumn") is not { Value: "0" };
        var showHeaders = (string?)info!.Attribute("showColHeaders") is not "0";
        if ((long)(range.Row2 - range.Row1 + 1) * (range.Column2 - range.Column1 + 1) > maxCells)
        {
            return false;
        }

        for (var row = range.Row1; row <= range.Row2; row++)
        {
            var offset = row - range.Row1;
            var item = offset - headers;
            var kind = item >= 0 && item < items.Count ? items[item] : null;
            for (var column = range.Column1; column <= range.Column2; column++)
            {
                var key = XlsxWorksheet.Key(row, column);
                var cell = sheet.Cells.TryGetValue(key, out var found) ? found : SheetCell.Empty;
                var format = cell.Format ?? CellFormat.Default;
                if (offset < headers && showHeaders)
                {
                    format = format with { Bold = true, Fill = light, Bottom = offset == headers - 1 ? line : format.Bottom };
                }
                else if (kind == "grand")
                {
                    format = format with { Bold = true, Fill = light, Top = line };
                }
                else if (kind is not null || (grandColumn && column == range.Column2 && offset >= headers))
                {
                    format = kind is "default" or "sum" or "countA" or "avg" or "max" or "min" or "product" or "count" or "stdDev" or "stdDevP" or "var" or "varP"
                        || (grandColumn && column == range.Column2) ? format with { Bold = true } : format;
                }
                else
                {
                    continue;
                }

                sheet.Add(row, column, cell with { Format = format }, maxCells);
            }
        }

        return true;
    }
}
