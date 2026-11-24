using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets.Drawings;

/// <summary>
/// The rows (or columns) of a laid-out sheet along one axis: where each starts and how big it is, in points from the
/// start of the first. Anchor edges resolve to positions on it.
/// </summary>
internal sealed class SheetAxis
{
    private readonly double[] _starts;

    /// <summary>Creates an axis of rows or columns of the given sizes (negative or not finite sizes count as 0).</summary>
    public SheetAxis(IReadOnlyList<double> sizes)
    {
        _starts = new double[sizes.Count + 1];
        for (var i = 0; i < sizes.Count; i++)
        {
            _starts[i + 1] = _starts[i] + (double.IsFinite(sizes[i]) ? Math.Max(0, sizes[i]) : 0);
        }
    }

    /// <summary>The number of rows or columns.</summary>
    public int Count => _starts.Length - 1;

    /// <summary>Where a row or column starts (<see cref="Count"/> for the end of the last).</summary>
    public double Start(int index) => _starts[Math.Clamp(index, 0, Count)];

    /// <summary>The size of a row or column (0 outside the axis).</summary>
    public double Size(int index) => index >= 0 && index < Count ? _starts[index + 1] - _starts[index] : 0;

    /// <summary>
    /// Where an anchor edge is: its row or column's start plus its offset at the scale, the offset no larger than the
    /// row or column (which may be smaller here than in the spreadsheet). An edge at <see cref="Count"/> runs on past
    /// the end; a negative offset (an edge before the first printed row or column) is kept.
    /// </summary>
    public double Position(AnchorEdge edge, double scale)
    {
        var index = Math.Clamp(edge.Index, 0, Count);
        var size = Size(index);
        var offset = (Clamp(edge.Fraction, 0, 1) * size) + (Clamp(edge.Points, -1e7, 1e7) * scale);
        if (index < Count)
        {
            offset = Math.Min(offset, size);
        }

        return Start(index) + offset;
    }

    /// <summary>The edge at a distance from the start (for absolute anchors): its row or column and the offset in it.</summary>
    public AnchorEdge Locate(double distance)
    {
        distance = Clamp(distance, 0, 1e7);
        var index = Array.BinarySearch(_starts, distance);
        index = index >= 0 ? index : ~index - 1;

        // Of rows or columns starting at the same place (empty ones), the last.
        while (index + 1 < _starts.Length && _starts[index + 1] <= distance)
        {
            index++;
        }

        index = Math.Clamp(index, 0, Count);
        return new AnchorEdge(index, distance - _starts[index]);
    }

    private static double Clamp(double value, double min, double max) => double.IsFinite(value) ? Math.Clamp(value, min, max) : 0;
}

/// <summary>
/// A run of rows of a table drawn one below the other on a page: where it is on the page (<see cref="PageTop"/>) and
/// in the table's own coordinates (from <see cref="Top"/> to <see cref="Bottom"/> on the row axis).
/// </summary>
internal readonly record struct RowSegment(int Page, double PageTop, double Top, double Bottom)
{
    public double Height => Bottom - Top;
}

/// <summary>
/// Where a sheet's drawings go once its table is laid out: the rows' actual heights, the runs of rows on each page,
/// and each drawing's box on the axes.
/// </summary>
internal static class DrawingLayout
{
    private const double Epsilon = 0.01;

    /// <summary>
    /// The heights of a sheet's rows as laid out: the rows of the table (<paramref name="first"/> onwards) from their
    /// pieces on the pages (header rows repeated on later pages left out), the others at their nominal heights.
    /// </summary>
    public static double[] RowHeights(IReadOnlyList<PlacedRow> placed, int first, int rowCount, Func<int, double> nominal)
    {
        var heights = new double[rowCount];
        var seen = new bool[rowCount];
        foreach (var piece in placed)
        {
            var row = first + piece.Row;
            if (!piece.Repeated && row >= 0 && row < rowCount)
            {
                heights[row] += piece.Height;
                seen[row] = true;
            }
        }

        for (var row = 0; row < rowCount; row++)
        {
            if (!seen[row])
            {
                heights[row] = nominal(row);
            }
        }

        return heights;
    }

    /// <summary>
    /// The runs of rows on each page (repeated header rows left out): pieces that follow one another both on the page
    /// and on the row axis are joined.
    /// </summary>
    public static List<RowSegment> Segments(IReadOnlyList<PlacedRow> placed, int first, SheetAxis rows)
    {
        var used = new Dictionary<int, double>();
        var segments = new List<RowSegment>();
        foreach (var piece in placed.Where(p => !p.Repeated).OrderBy(p => p.Page).ThenBy(p => p.Top))
        {
            var row = first + piece.Row;
            used.TryGetValue(row, out var before);
            used[row] = before + piece.Height;
            var top = rows.Start(row) + before;
            if (segments.Count > 0 && segments[^1] is var last && last.Page == piece.Page
                && Math.Abs(last.PageTop + last.Height - piece.Top) < Epsilon && Math.Abs(last.Bottom - top) < Epsilon)
            {
                segments[^1] = last with { Bottom = top + piece.Height };
            }
            else
            {
                segments.Add(new RowSegment(piece.Page, piece.Top, top, top + piece.Height));
            }
        }

        return segments;
    }

    /// <summary>The first of the runs of rows (in order down the row axis) whose bottom is at or below a position.</summary>
    public static int FirstReaching(List<RowSegment> segments, double position)
    {
        int low = 0, high = segments.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (segments[middle].Bottom < position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// A drawing's box on the axes at a scale: left, top, right and bottom. Absolute anchors are located on the nominal
    /// axes (the sizes at 100% the sheet asks for), then placed like one-cell anchors.
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) Box(SheetDrawing drawing, SheetAxis columns, SheetAxis rows, double scale,
        SheetAxis nominalColumns, SheetAxis nominalRows)
    {
        var (leftEdge, topEdge) = drawing.Kind == AnchorKind.Absolute
            ? (nominalColumns.Locate(drawing.Left.Points), nominalRows.Locate(drawing.Top.Points))
            : (drawing.Left, drawing.Top);
        var left = columns.Position(leftEdge, scale);
        var top = rows.Position(topEdge, scale);
        if (drawing.Kind == AnchorKind.TwoCell)
        {
            // An anchor whose end comes before its start is drawn as nothing wide or high, as Excel does.
            return (left, top, Math.Max(left, columns.Position(drawing.Right, scale)), Math.Max(top, rows.Position(drawing.Bottom, scale)));
        }

        return (left, top, left + (Size(drawing.Width) * scale), top + (Size(drawing.Height) * scale));
    }

    private static double Size(double points) => double.IsFinite(points) ? Math.Clamp(points, 0, 1e6) : 0;
}
