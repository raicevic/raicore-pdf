namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Table rows split across pages (CSS fragmentation of table rows).</summary>
internal sealed partial class Paginator
{
    /// <summary>
    /// Whether rows [start, end) can be split across pages: a single row that row spans do not join to others, unless
    /// it asks to stay whole.
    /// </summary>
    private static bool CanSplit(TableLayout layout, int start, int end, bool ignoreAvoid = false) =>
        end - start == 1
        && (ignoreAvoid || layout.Table.Rows[start].BreakInside != PageBreakInside.Avoid)
        && layout.Cells.Where(c => c.Grid.Row == start).All(c => c.Grid.RowSpan == 1);

    /// <summary>The height a row needs at the bottom of a page before it is worth starting there: every cell's start.</summary>
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
    /// Splits a row across pages: each cell's content is laid out from the row's top and continues on the following
    /// pages in its own column (below the repeated header rows); the cells' backgrounds and borders are sliced like
    /// boxes, with the top edge on the first page and the bottom edge on the last.
    /// </summary>
    private void SplitRow(TableLayout layout, int row, double x, double width)
    {
        var startPage = PageIndex;
        var rowTop = Y;
        var insertAt = _pages[startPage].Count;
        LayoutSideBySide(layout.Cells.Where(c => c.Grid.Row == row), (cell, _) =>
        {
            Y = rowTop + cell.InsetTop;
            var pending = default(CollapsedMargin);
            LayoutChildren(cell.Grid.Cell.Content, x + cell.X + cell.InsetLeft, cell.InnerWidth, ref pending, firstTopMarginApplied: false);
            Y += pending.Value + cell.InsetBottom;
        });

        var endPage = PageIndex;
        if (endPage == startPage)
        {
            Y = Math.Max(Y, rowTop + layout.RowHeights[row]); // Its content fitted after all (e.g. a narrower layout).
        }

        var endY = Y;
        for (var page = startPage; page <= endPage; page++)
        {
            var first = page == startPage;
            var last = page == endPage;
            var top = first ? rowTop : PageTop(page);
            var bottom = last ? endY : ContentBottom;

            // Under the cells' content; continuation pages start with the header rows, which this does not overlap.
            _pages[page].Insert(first ? insertAt : 0, (canvas, pageHeight) => DrawRowFragment(canvas, pageHeight, layout, x, row, top, bottom - top, first, last));
            _placements.Add(new Placement(page, layout.Table, top, bottom, row, row + 1, x, width));
        }

        Y += layout.SpacingY;
    }

    /// <summary>Draws the background, cell boxes and grid lines of one page's piece of a split row (not the content).</summary>
    private static void DrawRowFragment(PdfCanvas canvas, double pageHeight, TableLayout layout, double x, int row, double top, double height, bool first, bool last)
    {
        var table = layout.Table;
        var y = pageHeight - top - height; // PDF y of the fragment's bottom.
        var left = x + layout.ColumnX[0];
        var right = x + layout.ColumnX[^1] - layout.SpacingX;
        if (table.Rows[row].Background is { } rowBackground)
        {
            canvas.SetFillColor(rowBackground).Rectangle(left, y, right - left, height).Fill();
        }

        var collapse = table.BorderCollapse == BorderCollapse.Collapse;
        foreach (var cell in layout.Cells.Where(c => c.Grid.Row == row))
        {
            var style = cell.Grid.Cell.Style;
            if (!collapse)
            {
                DrawBox(canvas, pageHeight, style, x + cell.X, top, cell.Width, height, first, last);
            }
            else if (style.Background is { } background)
            {
                canvas.SetFillColor(background).Rectangle(x + cell.X, y, cell.Width, height).Fill();
            }
        }

        if (!collapse || layout.Horizontal is not { } horizontal || layout.Vertical is not { } vertical)
        {
            return;
        }

        var columns = layout.ColumnX.Length - 1;
        for (var c = 0; c <= columns; c++)
        {
            var line = vertical[row, c];
            if (line.Width > 0)
            {
                canvas.SetFillColor(line.Color).Rectangle(x + layout.ColumnX[c] - (line.Width / 2), y, line.Width, height).Fill();
            }
        }

        // The row's top line only where the row starts, its bottom line only where it ends.
        void Horizontal(int gridRow, double lineY)
        {
            for (var c = 0; c < columns; c++)
            {
                var line = horizontal[gridRow, c];
                if (line.Width > 0)
                {
                    var x0 = x + layout.ColumnX[c] - (vertical[row, c].Width / 2);
                    var x1 = x + layout.ColumnX[c + 1] + (vertical[row, c + 1].Width / 2);
                    canvas.SetFillColor(line.Color).Rectangle(x0, pageHeight - lineY - (line.Width / 2), x1 - x0, line.Width).Fill();
                }
            }
        }

        if (first)
        {
            Horizontal(row, top);
        }

        if (last)
        {
            Horizontal(row + 1, top + height);
        }
    }
}
