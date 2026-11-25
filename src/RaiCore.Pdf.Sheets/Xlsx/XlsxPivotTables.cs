using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Applies the style of a worksheet's pivot tables to the cells they are stored in (Excel keeps a pivot table's cells
/// in the sheet; its part says where it is, how it is laid out and how it looks): Excel's built-in pivot styles and the
/// workbook's own (<see cref="PivotStyles"/>), each element over its region (<see cref="PivotStyler"/>). Pivot tables
/// with a style that is neither are reported.
/// </summary>
internal static class XlsxPivotTables
{
    /// <summary>The most pivot tables of a sheet styled.</summary>
    private const int MaxTables = 1000;

    /// <summary>Applies the style of the pivot tables a worksheet refers to; a part that is damaged is left out (and reported).</summary>
    public static void Apply(XlsxPackage package, string sheetPath, XlsxWorksheet sheet, XlsxStyles styles, long maxCells)
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

            if (definition is null || !Apply(definition, sheet, styles, maxCells))
            {
                sheet.Unsupported.Add(SheetDiagnosticKind.PivotTable);
            }
        }
    }

    /// <summary>
    /// A pivot table style by name: the workbook's own (a <c>tableStyle</c> that is not only for tables), else Excel's
    /// built-in one; <c>null</c> for neither.
    /// </summary>
    internal static Dictionary<string, PivotStyleFormat>? Style(string name, XlsxStyles styles)
    {
        if (styles.TableStyles.FirstOrDefault(s => (string?)s.Attribute("name") == name && (string?)s.Attribute("pivot") is not ("0" or "false")) is { } own)
        {
            return PivotStyles.Read(own, styles.DifferentialFormatElements, styles.Colors);
        }

        return PivotStyles.ReadBuiltIn(name, styles.Colors);
    }

    /// <summary>Applies a pivot table's style to its range; <c>false</c> for a style that is not supported.</summary>
    internal static bool Apply(XElement definition, XlsxWorksheet sheet, XlsxStyles styles, long maxCells)
    {
        XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
        int Number(XElement? e, string name, int fallback) =>
            int.TryParse((string?)e?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        bool Flag(XElement? e, string name, bool fallback) => (string?)e?.Attribute(name) is { } v ? v is "1" or "true" : fallback;
        var location = Child(definition, "location");
        var info = Child(definition, "pivotTableStyleInfo");
        if (location is null || XlsxWorkbook.ParseRange((string?)location.Attribute("ref") ?? string.Empty) is not { Row1: >= 0, Column1: >= 0 } range)
        {
            return false;
        }

        if ((string?)info?.Attribute("name") is not { } name)
        {
            return true; // No style: plain cells, as Excel shows them.
        }

        if (Style(name, styles) is not { } style)
        {
            return false;
        }

        int Count(string fields) => Child(definition, fields)?.Elements().Count(e => e.Name.LocalName == "field") ?? 0;
        List<PivotLine> Lines(string items) => Child(definition, items)?.Elements().Where(e => e.Name.LocalName == "i").Take(1_048_576)
            .Select(i => new PivotLine((string?)i.Attribute("t") ?? "data", Math.Clamp(Number(i, "r", 0), 0, 1000), i.Elements().Count(x => x.Name.LocalName == "x")))
            .ToList() ?? [];
        var layout = new PivotLayout(range, Number(location, "firstHeaderRow", 1), Number(location, "firstDataRow", 1), Number(location, "firstDataCol", 1))
        {
            RowFields = Count("rowFields"),
            ColumnFields = Count("colFields"),
            PageFields = Child(definition, "pageFields")?.Elements().Count(e => e.Name.LocalName == "pageField") ?? 0,
            Rows = Lines("rowItems"),
            Columns = Lines("colItems"),
            ShowRowHeaders = Flag(info, "showRowHeaders", true),
            ShowColumnHeaders = Flag(info, "showColHeaders", true),
            ShowRowStripes = Flag(info, "showRowStripes", false),
            ShowColumnStripes = Flag(info, "showColStripes", false),
            ShowLastColumn = Flag(info, "showLastColumn", true),
        };
        return PivotStyler.Apply(layout, style, sheet, maxCells);
    }
}
