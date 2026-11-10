namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Table rows split across pages (CSS fragmentation of table rows, including rows joined by row spans).</summary>
internal sealed partial class Paginator
{
    /// <summary>A row's piece on one page.</summary>
    private readonly record struct RowFragment(int Row, double Top, double Height, bool First, bool Last);

    /// <summary>A row-spanning cell's piece on one page.</summary>
    private readonly record struct CellFragment(CellLayout Cell, double Top, double Height, bool First, bool Last);

    /// <summary>
    /// Whether rows [start, end) - a single row, or rows that row spans join - may be split across pages: unless one
    /// of them asks to stay whole.
    /// </summary>
    private static bool CanSplit(TableLayout layout, int start, int end, bool ignoreAvoid = false) =>
        ignoreAvoid || Enumerable.Range(start, end - start).All(r => layout.Table.Rows[r].BreakInside != PageBreakInside.Avoid);

    /// <summary>The height a row needs at the bottom of a page before it is worth starting there: the start of every cell beginning in it.</summary>
    private double MinimumRowStart(TableLayout layout, int row)
    {
        var needed = 0.0;
        foreach (var cell in layout.Cells.Where(c => c.Grid.Row == row))
        {
            var content = cell.Grid.Cell.Content;
            var start = content.Count == 0 ? 0 : content[0].Style.Margin.Top + MinimumStartHeight(content[0], cell.InnerWidth);
            needed = Math.Max(needed, cell.InsetTop + start);
        }

        return needed;
    }

    /// <summary>
    /// Splits rows [start, end) across pages. Row by row, the cells beginning in a row are laid out from its top and
    /// continue on the following pages in their own columns (below the repeated header rows); a row ends where the
    /// last of the cells ending in it does, so a cell spanning rows runs alongside them, across pages if need be.
    /// A page break may also fall between the rows. Backgrounds, cell boxes and grid lines are then drawn per page,
    /// sliced like boxes: top edges where a row (or spanning cell) starts, bottom edges where it ends.
    /// </summary>
    private void SplitRows(TableLayout layout, int start, int end, double x, double width)
    {
        var startPage = PageIndex;
        var insertAt = _pages[startPage].Count;
        var count = end - start;
        var tops = new (int Page, double Y)[count];
        var ends = new (int Page, double Y)[count];
        var cellEnds = new Dictionary<CellLayout, (int Page, double Y)>();
        for (var i = 0; i < count; i++)
        {
            var row = start + i;
            if (i > 0)
            {
                Y += layout.SpacingY;

                // A break between the rows (spanning cells carry on across it) when this row's start does not fit.
                if (!AtPageTop && MinimumRowStart(layout, row) > Remaining + Epsilon)
                {
                    NewPage();
                }
            }

            var top = (Page: PageIndex, Y);
            var truncate = TruncateMargins;
            tops[i] = top;
            foreach (var cell in layout.Cells.Where(c => c.Grid.Row == row))
            {
                _pageIndex = top.Page;
                Y = top.Y + cell.InsetTop;
                TruncateMargins = truncate;
                var pending = default(CollapsedMargin);
                LayoutChildren(cell.Grid.Cell.Content, x + cell.X + cell.InsetLeft, cell.InnerWidth, ref pending, firstTopMarginApplied: false);
                cellEnds[cell] = (PageIndex, Y + pending.Value + cell.InsetBottom);
            }

            // The row ends with the last cell ending in it, and is at least as tall as its unbroken height.
            var rowEnd = top;
            foreach (var cell in layout.Cells.Where(c => c.Grid.Row + c.Grid.RowSpan - 1 == row))
            {
                rowEnd = Later(rowEnd, cellEnds[cell]);
            }

            if (rowEnd.Page == top.Page)
            {
                rowEnd.Y = Math.Max(rowEnd.Y, top.Y + layout.RowHeights[row]);
            }

            ends[i] = rowEnd;
            _pageIndex = rowEnd.Page;
            Y = rowEnd.Y;
            TruncateMargins = rowEnd.Page > top.Page || truncate;
        }

        for (var page = startPage; page <= PageIndex; page++)
        {
            var rows = new List<RowFragment>();
            for (var i = 0; i < count; i++)
            {
                if (Fragment(tops[i], ends[i], page) is { } piece)
                {
                    rows.Add(new RowFragment(start + i, piece.Top, piece.Height, piece.First, piece.Last));
                    _placements.Add(new Placement(page, layout.Table, piece.Top, piece.Top + piece.Height, start + i, start + i + 1, x, width));
                }
            }

            // A spanning cell's piece ends with the rows it covers on this page: when the break falls between rows,
            // that is above the page's bottom.
            var spanning = new List<CellFragment>();
            foreach (var cell in layout.Cells.Where(c => c.Grid.Row >= start && c.Grid.Row < end && c.Grid.RowSpan > 1))
            {
                if (Fragment(tops[cell.Grid.Row - start], ends[cell.Grid.Row + cell.Grid.RowSpan - 1 - start], page) is { } piece)
                {
                    var covered = rows.Where(r => r.Row >= cell.Grid.Row && r.Row < cell.Grid.Row + cell.Grid.RowSpan).ToList();
                    var bottom = covered.Count > 0 ? covered.Max(r => r.Top + r.Height) : piece.Top + piece.Height;
                    spanning.Add(new CellFragment(cell, piece.Top, Math.Max(0, bottom - piece.Top), piece.First, piece.Last));
                }
            }

            // Under the cells' content; continuation pages start with the header rows, which this does not overlap.
            _pages[page].Insert(page == startPage ? insertAt : 0, (canvas, pageHeight) => DrawFragments(canvas, pageHeight, layout, x, rows, spanning));
        }

        Y += layout.SpacingY;

        static (int Page, double Y) Later((int Page, double Y) a, (int Page, double Y) b) =>
            b.Page > a.Page || (b.Page == a.Page && b.Y > a.Y) ? b : a;
    }

    /// <summary>The piece of a span from <paramref name="from"/> to <paramref name="to"/> on a page, if it reaches that page.</summary>
    private (double Top, double Height, bool First, bool Last)? Fragment((int Page, double Y) from, (int Page, double Y) to, int page)
    {
        if (page < from.Page || page > to.Page)
        {
            return null;
        }

        var top = page == from.Page ? from.Y : PageTop(page);
        var bottom = page == to.Page ? to.Y : ContentBottom;
        return (top, Math.Max(0, bottom - top), page == from.Page, page == to.Page);
    }

    /// <summary>
    /// Draws one page's pieces of split rows: row backgrounds, then cell boxes (single-row cells with their row,
    /// spanning cells with their own extent), then the collapsed grid lines on top. Not the cells' content.
    /// </summary>
    private static void DrawFragments(PdfCanvas canvas, double pageHeight, TableLayout layout, double x, List<RowFragment> rows, List<CellFragment> spanning)
    {
        var table = layout.Table;
        var collapse = table.BorderCollapse == BorderCollapse.Collapse;
        var left = x + layout.ColumnX[0];
        var right = x + layout.ColumnX[^1] - layout.SpacingX;
        foreach (var row in rows)
        {
            if (table.Rows[row.Row].Background is { } rowBackground)
            {
                canvas.SetFillColor(rowBackground).Rectangle(left, pageHeight - row.Top - row.Height, right - left, row.Height).Fill();
            }
        }

        void Cell(CellLayout cell, double top, double height, bool first, bool last)
        {
            var style = cell.Grid.Cell.Style;
            if (!collapse)
            {
                DrawBox(canvas, pageHeight, style, x + cell.X, top, cell.Width, height, first, last);
            }
            else if (style.Background is { } background)
            {
                canvas.SetFillColor(background).Rectangle(x + cell.X, pageHeight - top - height, cell.Width, height).Fill();
            }
        }

        foreach (var row in rows)
        {
            foreach (var cell in layout.Cells.Where(c => c.Grid.Row == row.Row && c.Grid.RowSpan == 1))
            {
                Cell(cell, row.Top, row.Height, row.First, row.Last);
            }
        }

        foreach (var piece in spanning)
        {
            Cell(piece.Cell, piece.Top, piece.Height, piece.First, piece.Last);
        }

        if (!collapse || layout.Horizontal is not { } horizontal || layout.Vertical is not { } vertical)
        {
            return;
        }

        var columns = layout.ColumnX.Length - 1;
        foreach (var row in rows)
        {
            for (var c = 0; c <= columns; c++)
            {
                var line = vertical[row.Row, c];
                if (line.Width > 0)
                {
                    canvas.SetFillColor(line.Color).Rectangle(x + layout.ColumnX[c] - (line.Width / 2), pageHeight - row.Top - row.Height, line.Width, row.Height).Fill();
                }
            }

            // A row's top line only where the row starts, its bottom line only where it ends. Lines inside a spanning
            // cell have no width, so they are skipped.
            void Horizontal(int gridRow, double lineY)
            {
                for (var c = 0; c < columns; c++)
                {
                    var line = horizontal[gridRow, c];
                    if (line.Width > 0)
                    {
                        var x0 = x + layout.ColumnX[c] - (vertical[row.Row, c].Width / 2);
                        var x1 = x + layout.ColumnX[c + 1] + (vertical[row.Row, c + 1].Width / 2);
                        canvas.SetFillColor(line.Color).Rectangle(x0, pageHeight - lineY - (line.Width / 2), x1 - x0, line.Width).Fill();
                    }
                }
            }

            if (row.First)
            {
                Horizontal(row.Row, row.Top);
            }

            if (row.Last)
            {
                Horizontal(row.Row + 1, row.Top + row.Height);
            }
        }
    }
}
