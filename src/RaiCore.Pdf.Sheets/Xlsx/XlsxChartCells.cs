using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using RaiCore.Pdf.Charts;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Resolves the cell references of charts without cached data (the newer chart types of Excel refer to their data
/// through hidden names, <c>_xlchart.v1.0</c>, and store no values): a name or a reference such as
/// <c>'Data'!$B$2:$D$9</c> to the values of its cells, read from the worksheet part (numbers, text, shared strings),
/// row by row. Each range is read once; ranges are bounded by <see cref="ResourceLimits.MaxTableCells"/>.
/// </summary>
internal sealed class XlsxChartCells(XlsxPackage package, XlsxWorkbook workbook, ResourceLimits limits)
{
    private readonly Dictionary<string, ChartReference?> _resolved = new(StringComparer.Ordinal);

    /// <summary>The values of a reference's cells, or <c>null</c> when it cannot be resolved.</summary>
    public ChartReference? Resolve(string formula)
    {
        if (_resolved.TryGetValue(formula, out var known))
        {
            return known;
        }

        ChartReference? result = null;
        var text = workbook.ChartName(formula) ?? formula;
        if (XlsxWorkbook.ParseAreas(text) is [var (sheetName, range)]
            && sheetName is not null
            && workbook.Sheets.FirstOrDefault(s => s.IsWorksheet && string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase)) is { Path: { } path }
            && range.Row1 >= 0 && range.Column1 >= 0)
        {
            var rows = (long)range.Row2 - range.Row1 + 1;
            var columns = range.Column2 - range.Column1 + 1;
            if (rows * columns > limits.MaxTableCells)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), limits.MaxTableCells,
                    $"A chart refers to more cells than ResourceLimits.MaxTableCells allows ({limits.MaxTableCells}).");
            }

            try
            {
                var values = package.Parse(path, reader => Read(reader, range), null);
                result = values is null ? null : new ChartReference(values, null, columns);
            }
            catch (SpreadsheetFormatException)
            {
                result = null;
            }
        }

        if (_resolved.Count < 1000)
        {
            _resolved[formula] = result;
        }

        return result;
    }

    /// <summary>The values of a range's cells, row by row (<c>null</c> for empty and error cells).</summary>
    private object?[] Read(XmlReader reader, CellRange range)
    {
        var columns = range.Column2 - range.Column1 + 1;
        var values = new object?[(range.Row2 - range.Row1 + 1) * columns];
        int row = -1, column = -1;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (reader.LocalName == "row")
            {
                row = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r - 1 : row + 1;
                column = -1;
                if (row > range.Row2)
                {
                    break;
                }
            }
            else if (reader.LocalName == "c")
            {
                column++;
                if (reader.GetAttribute("r") is { } reference && XlsxWorkbook.ParseReference(reference) is { Column: >= 0 } at)
                {
                    (row, column) = (at.Row >= 0 ? at.Row : row, at.Column);
                }

                if (row >= range.Row1 && row <= range.Row2 && column >= range.Column1 && column <= range.Column2)
                {
                    values[((row - range.Row1) * columns) + column - range.Column1] = Value((XElement)XNode.ReadFrom(reader));
                    continue;
                }
            }

            reader.Read();
        }

        return values;
    }

    /// <summary>The value of a cell: a number, text, or <c>null</c> (errors, empty).</summary>
    private object? Value(XElement cell)
    {
        var v = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
        switch ((string?)cell.Attribute("t"))
        {
            case "s":
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < workbook.SharedStrings.Count ? workbook.SharedStrings[index] : null;
            case "inlineStr":
                return string.Concat(cell.Elements().Where(e => e.Name.LocalName == "is").Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
            case "str":
                return v;
            case "e":
                return null;
            case "b":
                return v == "1" ? 1.0 : 0.0;
            default:
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        }
    }
}
