using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// The drawings of a worksheet (of either workbook format) on the sheet to print: the range they make the sheet print,
/// and their anchors moved from the worksheet's rows and columns to the printed ones. Hidden rows and columns are as
/// high or wide as nothing, as in Excel: an edge in one moves to the start of the next printed one; an edge before the
/// first printed row or column, or after the last, keeps its distance from it (the part of the drawing there is cut).
/// </summary>
internal static class SheetDrawings
{
    /// <summary>The height of rows without one, in points (Excel's for its default font).</summary>
    private const double RowHeight = 15;

    /// <summary>Points per character of column width (seven-pixel digits at 96 pixels per inch).</summary>
    private const double PointsPerCharacter = 5.25;

    /// <summary>The width of columns without one, in characters (eight characters and the padding).</summary>
    private const double ColumnWidth = 8 + (5 / 7.0);

    /// <summary>The longest run of rows or columns whose sizes are added up one by one; longer runs are estimated.</summary>
    private const int MaxSummed = 4096;

    /// <summary>
    /// The last row and column the worksheet prints without a print area: those of its cells, extended to cover its
    /// drawings, as Excel prints them too (for drawings with a size of their own, by the sizes of the rows and columns
    /// they cover). -1 when there are none.
    /// </summary>
    public static (int LastRow, int LastColumn) UsedRange(XlsxWorksheet worksheet)
    {
        var (lastRow, lastColumn) = (worksheet.LastRow, worksheet.LastColumn);
        var sizes = new Sizes(worksheet, null);
        foreach (var drawing in worksheet.Drawings)
        {
            int endRow, endColumn;
            if (drawing.Kind == AnchorKind.TwoCell)
            {
                (endRow, endColumn) = (End(drawing.Bottom), End(drawing.Right));
            }
            else
            {
                var (top, left) = drawing.Kind == AnchorKind.OneCell ? (drawing.Top, drawing.Left) : (new AnchorEdge(0, drawing.Top.Points), new AnchorEdge(0, drawing.Left.Points));
                endRow = sizes.Reach(top, drawing.Height, rows: true);
                endColumn = sizes.Reach(left, drawing.Width, rows: false);
            }

            lastRow = Math.Max(lastRow, Math.Min(endRow, XlsxWorkbook.MaxRows - 1));
            lastColumn = Math.Max(lastColumn, Math.Min(endColumn, XlsxWorkbook.MaxColumns - 1));
        }

        return (lastRow, lastColumn);

        // The last row or column an edge reaches: its own, unless it is at its very start.
        static int End(AnchorEdge edge) => edge.Points > 0 || edge.Fraction > 0 ? edge.Index : Math.Max(edge.Index - 1, 0);
    }

    /// <summary>
    /// The worksheet's drawings with their anchors on the printed <paramref name="rows"/> and <paramref name="columns"/>
    /// (the worksheet's indexes, ascending), in z-order.
    /// </summary>
    public static List<SheetDrawing> Map(XlsxWorksheet worksheet, int[] rows, int[] columns, double defaultWidth)
    {
        var result = new List<SheetDrawing>(worksheet.Drawings.Count);
        if (worksheet.Drawings.Count == 0 || rows.Length == 0 || columns.Length == 0)
        {
            return result;
        }

        var sizes = new Sizes(worksheet, defaultWidth);
        foreach (var drawing in worksheet.Drawings)
        {
            if (drawing.Kind == AnchorKind.Absolute)
            {
                // Measured from the sheet's first printed row and column.
                result.Add(drawing);
                continue;
            }

            var mapped = drawing with
            {
                Left = Map(drawing.Left, columns, sizes.Column, sizes.DefaultColumn),
                Top = Map(drawing.Top, rows, sizes.Row, sizes.DefaultRow),
            };
            if (drawing.Kind == AnchorKind.TwoCell)
            {
                mapped = mapped with
                {
                    Right = Map(drawing.Right, columns, sizes.Column, sizes.DefaultColumn),
                    Bottom = Map(drawing.Bottom, rows, sizes.Row, sizes.DefaultRow),
                };
            }

            result.Add(mapped);
        }

        return result;
    }

    /// <summary>
    /// An edge on the printed rows or columns (<paramref name="printed"/>, ascending): in a printed one, at the same
    /// offset; in a hidden one (or one between printed parts), at the start of the next; before the first or after the
    /// last, at its distance from it (row and column sizes summed, or estimated for long runs).
    /// </summary>
    internal static AnchorEdge Map(AnchorEdge edge, int[] printed, Func<int, double> size, double fallback)
    {
        var index = Array.BinarySearch(printed, edge.Index);
        if (index >= 0)
        {
            return edge with { Index = index };
        }

        var next = ~index;
        var offset = (edge.Fraction * size(edge.Index)) + edge.Points;
        if (next == 0)
        {
            return new AnchorEdge(0, offset - Sum(edge.Index, printed[0], size, fallback));
        }

        if (next == printed.Length)
        {
            return new AnchorEdge(printed.Length, Sum(printed[^1] + 1, edge.Index, size, fallback) + offset);
        }

        return new AnchorEdge(next, 0);
    }

    /// <summary>The size of rows or columns [<paramref name="start"/>, <paramref name="end"/>).</summary>
    private static double Sum(int start, int end, Func<int, double> size, double fallback)
    {
        if (end - start > MaxSummed)
        {
            return (end - start) * fallback;
        }

        var sum = 0.0;
        for (var i = start; i < end; i++)
        {
            sum += size(i);
        }

        return sum;
    }

    /// <summary>
    /// The sizes of a worksheet's rows and columns in points at 100% (hidden ones 0), as far as the sheet tells them:
    /// rows of no height of their own at the default height, columns at their width in characters.
    /// </summary>
    private sealed class Sizes
    {
        /// <summary>The most rows or columns walked to find how far drawings reach, for all of a sheet's drawings together.</summary>
        private const int MaxWalked = 1 << 20;

        private readonly XlsxWorksheet _worksheet;
        private readonly Dictionary<int, double> _widths = [];
        private int _walked;

        public Sizes(XlsxWorksheet worksheet, double? defaultWidth)
        {
            _worksheet = worksheet;
            DefaultRow = worksheet.DefaultRowHeight is { } height and > 0 and < 500 ? height : RowHeight;
            var width = defaultWidth ?? worksheet.DefaultColumnWidth ?? (worksheet.BaseColumnWidth is { } b ? b + (5 / 7.0) : ColumnWidth);
            DefaultColumn = width * PointsPerCharacter;
            foreach (var info in worksheet.Columns)
            {
                for (var c = info.First; c <= info.Last && _widths.Count <= XlsxWorkbook.MaxColumns; c++)
                {
                    _widths[c] = info.Hidden ? 0 : (info.Width ?? width) * PointsPerCharacter;
                }
            }
        }

        /// <summary>The height of rows without one.</summary>
        public double DefaultRow { get; }

        /// <summary>The width of columns without one.</summary>
        public double DefaultColumn { get; }

        public double Row(int row) => _worksheet.Rows.TryGetValue(row, out var info)
            ? info.Hidden ? 0 : info.Height ?? DefaultRow
            : _worksheet.ZeroHeight ? 0 : DefaultRow;

        public double Column(int column) => _widths.TryGetValue(column, out var width) ? width : DefaultColumn;

        /// <summary>The last row or column a length reaches from an edge (estimated once too many have been walked).</summary>
        public int Reach(AnchorEdge from, double length, bool rows)
        {
            var max = rows ? XlsxWorkbook.MaxRows : XlsxWorkbook.MaxColumns;
            var remaining = double.IsFinite(length + from.Points) ? Math.Max(0, from.Points + length) : 0;
            var index = Math.Clamp(from.Index, 0, max - 1);
            while (index < max - 1)
            {
                if (_walked++ > MaxWalked)
                {
                    var size = rows ? DefaultRow : DefaultColumn;
                    return (int)Math.Min(max - 1, index + Math.Ceiling(remaining / size));
                }

                var current = rows ? Row(index) : Column(index);
                if (remaining <= current)
                {
                    break;
                }

                remaining -= current;
                index++;
            }

            return index;
        }
    }
}
