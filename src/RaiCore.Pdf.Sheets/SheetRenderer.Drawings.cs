using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Drawings;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// The drawings of formatted sheets (pictures, shapes, charts), drawn over the cells of each table once it is laid
/// out: anchored to the laid-out columns and rows at the sheet's scale. A drawing that spans a page break or a band of
/// columns is split there, each page showing the part over its own rows and columns, as spreadsheets print them; on
/// the pages a table continues on, repeated header rows show no drawings.
/// </summary>
internal static partial class SheetRenderer
{
    /// <summary>
    /// The overlay that draws the sheet's drawings over a table of a band of columns and rows [<paramref name="start"/>,
    /// <paramref name="end"/>), or <c>null</c> when the sheet has none.
    /// </summary>
    private static Func<TablePlacement, IEnumerable<(int Page, Action<PdfCanvas, double> Draw)>>? DrawingOverlay(FormattedContext context, ColumnBand band, int start, int end)
    {
        if (context.Format.Drawings.Count == 0 || end <= start)
        {
            return null;
        }

        return placement => PlaceDrawings(context, band, start, placement);
    }

    private static IEnumerable<(int Page, Action<PdfCanvas, double> Draw)> PlaceDrawings(FormattedContext context, ColumnBand band, int start, TablePlacement placement)
    {
        var sheet = context.Sheet;
        var scale = context.Scale;
        var heights = DrawingLayout.RowHeights(placement.Rows, start, sheet.Rows.Count, context.RowHeight);
        var rows = new SheetAxis(heights);
        var columns = new SheetAxis(context.ColumnSizes);
        var nominalRows = new SheetAxis([.. Enumerable.Range(0, sheet.Rows.Count).Select(r => context.RowHeight(r) / scale)]);
        var nominalColumns = new SheetAxis(context.NaturalWidths);
        var segments = DrawingLayout.Segments(placement.Rows, start, rows);
        var edges = placement.ColumnEdges;
        if (edges.Count < 2 || segments.Count == 0)
        {
            yield break;
        }

        // The band's columns, on the column axis and on the page.
        var bandLeft = columns.Start(band.Start);
        var bandRight = columns.Start(band.Start + band.Count);
        var (pageLeft, pageRight) = (edges[0], edges[^1]);
        var rightToLeft = context.Format.RightToLeft;
        var painter = context.Painter;
        var count = 0;
        foreach (var drawing in context.Format.Drawings)
        {
            if (++count % 1024 == 0)
            {
                ResourceLimits.Checkpoint();
            }

            var (left, top, right, bottom) = DrawingLayout.Box(drawing, columns, rows, scale, nominalColumns, nominalRows);
            if (right < bandLeft || left > bandRight || (right == bandLeft && left < right) || (left == bandRight && left < right))
            {
                continue;
            }

            var (w, h) = (right - left, bottom - top);
            var x = rightToLeft ? pageRight - (right - bandLeft) : pageLeft + (left - bandLeft);
            // The runs of rows follow one another down the row axis: from the first that reaches the drawing's top.
            for (var i = DrawingLayout.FirstReaching(segments, top); i < segments.Count && segments[i].Top <= bottom; i++)
            {
                var segment = segments[i];
                if (bottom < segment.Top || top > segment.Bottom || (bottom == segment.Top && top < bottom) || (top == segment.Bottom && top < bottom))
                {
                    continue;
                }

                var pageTop = segment.PageTop + (top - segment.Top);
                var item = drawing.Item;
                yield return (segment.Page, (canvas, pageHeight) =>
                {
                    canvas.SaveState();
                    canvas.Rectangle(pageLeft, pageHeight - segment.PageTop - segment.Height, pageRight - pageLeft, segment.Height).Clip().EndPath();
                    canvas.Transform(scale, 0, 0, scale, x, pageHeight - pageTop - h);
                    painter.Draw(canvas, item, w / scale, h / scale);
                    canvas.RestoreState();
                });
            }
        }
    }
}
