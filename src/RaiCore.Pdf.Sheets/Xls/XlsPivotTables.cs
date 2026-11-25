using System.Text;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// Reads the pivot tables of a BIFF8 worksheet, for their style ([MS-XLS] 2.2.5): each SxView record (its location, the
/// first rows and column of its parts, the counts of its fields and lines), the two SXLI records after it (the lines
/// of its row area, then of its column area: SXLIItem 2.5.259) and the SXAddl record of its table style
/// (SXAddl_SXCView_SXDTableStyleClient 2.4.273.107: which elements show, and the style's name). Pivot tables without a
/// table style (Excel 97's AutoFormats, whose formatting is in their cells) are left as they are.
/// </summary>
internal sealed class XlsPivotTables
{
    /// <summary>The most pivot tables of a sheet read.</summary>
    private const int MaxTables = 1000;

    /// <summary>The most lines of an area read.</summary>
    private const int MaxLines = 1_048_576;

    /// <summary>The functions of subtotal lines by their itmType (1 to 12).</summary>
    private static readonly string[] Subtotals = ["default", "sum", "countA", "count", "avg", "max", "min", "product", "stdDev", "stdDevP", "var", "varP"];

    private sealed class PivotView
    {
        public required PivotLayout Layout;
        public int Lines; // The SXLI records read for the view so far.
        public string? Style;
    }

    private readonly List<PivotView> _views = [];

    /// <summary>The pivot tables with a table style: their layout and the style's name.</summary>
    public IEnumerable<(PivotLayout Layout, string Style)> Styled => _views.Where(v => v.Style is not null).Select(v => (v.Layout, v.Style!));

    /// <summary>An SxView record: a pivot table's location and parts.</summary>
    public void View(ReadOnlySpan<byte> data)
    {
        if (data.Length < 36 || _views.Count >= MaxTables)
        {
            return;
        }

        var fields = new ushort[18];
        for (var i = 0; i < fields.Length; i++)
        {
            fields[i] = (ushort)BiffReader.U16(data, 2 * i);
        }

        int U(int at) => fields[at / 2];
        var (rwFirst, rwLast, colFirst, colLast) = (U(0), U(2), U(4), U(6));
        if (rwLast < rwFirst || colLast < colFirst)
        {
            return;
        }

        var layout = new PivotLayout(new CellRange(rwFirst, colFirst, rwLast, colLast), U(8) - rwFirst, U(10) - rwFirst, U(12) - colFirst)
        {
            RowFields = U(24),
            ColumnFields = U(26),
            PageFields = U(28),
        };
        _views.Add(new PivotView { Layout = layout, Lines = U(32) > 0 || U(34) > 0 ? 0 : 2 });
    }

    /// <summary>An SXLI record: the lines of the last pivot table's row area (the first after it) or column area (the second).</summary>
    public void Lines(ReadOnlySpan<byte> data)
    {
        if (_views.Count == 0 || _views[^1].Lines >= 2)
        {
            return;
        }

        var view = _views[^1];
        var lines = new List<PivotLine>();
        for (var position = 0; position + 8 <= data.Length && lines.Count < MaxLines;)
        {
            var repeated = (short)BiffReader.U16(data, position);
            var type = BiffReader.U16(data, position + 2) & 0x7FFF;
            var items = (short)BiffReader.U16(data, position + 4);
            var grand = (BiffReader.U16(data, position + 6) & 0x0800) != 0;
            if (repeated < 0 || items < 0)
            {
                break;
            }

            var kind = grand || type == 0x0D ? "grand" : type == 0x0E ? "blank" : type is >= 1 and <= 12 ? Subtotals[type - 1] : "data";
            lines.Add(new PivotLine(kind, repeated, items));
            position += 8 + (2 * items);
        }

        view.Layout = view.Lines++ == 0 ? view.Layout with { Rows = lines } : view.Layout with { Columns = lines };
    }

    /// <summary>
    /// An SXAddl record: of the last pivot table's view (class SxcView, 0x00), its table style (SXDTableStyleClient,
    /// 0x1E): which elements show (bits from the least: fLastColumn 1, fRowStrips 2, fColumnStrips 3, fRowHeaders 4,
    /// fColumnHeaders 5, fDefaultStyle 6) and the style's name (an LPWideString). The default style is Excel's,
    /// <c>PivotStyleLight16</c>.
    /// </summary>
    public void Addition(ReadOnlySpan<byte> data)
    {
        if (_views.Count == 0 || data.Length < 16 || data[4] != 0x00 || data[5] != 0x1E)
        {
            return;
        }

        var flags = BiffReader.U16(data, 12);
        var length = Math.Min(BiffReader.U16(data, 14), 255);
        var name = 16 + (2 * length) <= data.Length ? Encoding.Unicode.GetString(data.Slice(16, 2 * length)) : string.Empty;
        var view = _views[^1];
        view.Layout = view.Layout with
        {
            ShowLastColumn = (flags & 0x02) != 0,
            ShowRowStripes = (flags & 0x04) != 0,
            ShowColumnStripes = (flags & 0x08) != 0,
            ShowRowHeaders = (flags & 0x10) != 0,
            ShowColumnHeaders = (flags & 0x20) != 0,
        };
        view.Style = name.Length > 0 ? name : (flags & 0x40) != 0 ? "PivotStyleLight16" : null;
    }

    /// <summary>Applies the pivot tables' styles to the sheet's cells; a style that is not known is reported.</summary>
    public void Apply(XlsxWorksheet sheet, XlsxColors colors, long maxCells)
    {
        foreach (var (layout, name) in Styled)
        {
            if (PivotStyles.ReadBuiltIn(name, colors) is not { } style || !PivotStyler.Apply(layout, style, sheet, maxCells))
            {
                sheet.Unsupported.Add(SheetDiagnosticKind.PivotTable);
            }
        }
    }
}
