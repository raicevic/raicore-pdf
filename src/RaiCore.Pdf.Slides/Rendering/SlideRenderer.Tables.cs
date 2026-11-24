using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Rendering;

/// <summary>Tables: rows grown to fit their text, merged cells, fills, text, then borders.</summary>
internal sealed partial class SlideRenderer
{
    /// <summary>The heights of a table's rows: at least their own, and enough for the text of their cells.</summary>
    internal List<double> RowHeights(TableShape table)
    {
        var heights = table.Rows.Select(r => r.Height).ToList();
        var columns = table.Columns;
        double Width(int c, int span) => columns.Skip(c).Take(span).Sum();
        double Needed(TableCell cell, int c)
        {
            var frame = cell.Text;
            if (!frame.HasText)
            {
                return 0;
            }

            var inner = Math.Max(Width(c, cell.ColumnSpan) - frame.LeftInset - frame.RightInset, 1);
            return _text.Layout(frame, inner, double.PositiveInfinity).Height + frame.TopInset + frame.BottomInset;
        }

        // Cells in one row first, then cells spanning rows grow their last row if the rows are too short together.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var cells = table.Rows[r].Cells;
                for (var c = 0; c < cells.Count && c < columns.Count; c++)
                {
                    var cell = cells[c];
                    if (cell.Merged || (pass == 0) != (cell.RowSpan == 1))
                    {
                        continue;
                    }

                    _checkpoint?.Invoke();
                    var last = Math.Min(r + cell.RowSpan, heights.Count) - 1;
                    var have = heights.Skip(r).Take(last - r + 1).Sum();
                    var needed = Needed(cell, c);
                    if (needed > have)
                    {
                        heights[last] += needed - have;
                    }
                }
            }
        }

        return heights;
    }

    /// <summary>Draws a table in its frame's coordinates.</summary>
    private void DrawTable(PdfCanvas canvas, TableShape table, Affine toPage, PdfStructureElement? tags)
    {
        var heights = RowHeights(table);
        var columns = table.Columns;
        var xs = new double[columns.Count + 1];
        for (var c = 0; c < columns.Count; c++)
        {
            xs[c + 1] = xs[c] + columns[c];
        }

        var ys = new double[heights.Count + 1];
        for (var r = 0; r < heights.Count; r++)
        {
            ys[r + 1] = ys[r] + heights[r];
        }

        // The cells to draw, with their rectangles.
        var cells = new List<(TableCell Cell, int Row, double X, double Y, double W, double H)>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            for (var c = 0; c < row.Cells.Count && c < columns.Count; c++)
            {
                var cell = row.Cells[c];
                if (cell.Merged)
                {
                    continue;
                }

                var right = Math.Min(c + cell.ColumnSpan, columns.Count);
                var bottom = Math.Min(r + cell.RowSpan, heights.Count);
                cells.Add((cell, r, xs[c], ys[r], xs[right] - xs[c], ys[bottom] - ys[r]));
            }
        }

        foreach (var (cell, _, x, y, w, h) in cells)
        {
            if (cell.Fill is { } fill)
            {
                Paint(canvas, fill, Affine.Translate(x, y).Then(toPage), w, h, b => b.Polygon(0, 0, w, 0, w, h, 0, h));
            }
        }

        var element = tags?.AddChild("Table");
        PdfStructureElement? tableRow = null;
        var currentRow = -1;
        foreach (var (cell, r, x, y, w, h) in cells)
        {
            PdfStructureElement? data = null;
            if (element is not null)
            {
                if (r != currentRow)
                {
                    (tableRow, currentRow) = (element.AddChild("TR"), r);
                }

                data = tableRow!.AddChild("TD");
                data.ColumnSpan = cell.ColumnSpan;
                data.RowSpan = cell.RowSpan;
            }

            _text.Draw(canvas, cell.Text, Affine.Translate(x, y).Then(toPage), (0, 0, w, h), data, heading: false);
        }

        foreach (var (cell, _, x, y, w, h) in cells)
        {
            void Edge(Outline? line, double x0, double y0, double x1, double y1)
            {
                if (line is not null)
                {
                    Stroke(canvas, new PathBuilder().MoveTo(x0, y0).LineTo(x1, y1).ToPath(filled: false), line, toPage);
                }
            }

            Edge(cell.Top, x, y, x + w, y);
            Edge(cell.Bottom, x, y + h, x + w, y + h);
            Edge(cell.Left, x, y, x, y + h);
            Edge(cell.Right, x + w, y, x + w, y + h);
        }
    }
}
