namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>A cell placed on the table grid.</summary>
internal sealed record GridCell(TableCell Cell, int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>A grid line segment in the collapsing border model.</summary>
internal readonly record struct GridLine(double Width, PdfColor Color);

/// <summary>A laid-out cell: where its box is and how to draw its content.</summary>
internal sealed record CellLayout(GridCell Grid, double X, double Width, double ContentHeight, double InsetTop, double InsetBottom, IReadOnlyList<DrawOperation> Content);

/// <summary>A table laid out at a width, independent of pages: column positions, row heights and cell content.</summary>
internal sealed class TableLayout
{
    public required TableBlock Table { get; init; }

    /// <summary>Left edge of each column, plus the right edge of the last (offsets from the table's content x).</summary>
    public required double[] ColumnX { get; init; }

    public required double[] RowHeights { get; init; }

    public required IReadOnlyList<CellLayout> Cells { get; init; }

    /// <summary>Collapsed horizontal lines: [r, c] is the line above row r (r = Rows is the bottom edge) in column c.</summary>
    public GridLine[,]? Horizontal { get; init; }

    /// <summary>Collapsed vertical lines: [r, c] is the line left of column c (c = Columns is the right edge) in row r.</summary>
    public GridLine[,]? Vertical { get; init; }

    public required GridCell?[,] Slots { get; init; }

    public required int HeaderRows { get; init; }

    /// <summary>Row ranges that must stay on one page because rowspans join them.</summary>
    public required IReadOnlyList<(int Start, int End)> Groups { get; init; }

    public double SpacingX => Table.BorderCollapse == BorderCollapse.Separate ? Table.HorizontalSpacing : 0;

    public double SpacingY => Table.BorderCollapse == BorderCollapse.Separate ? Table.VerticalSpacing : 0;

    /// <summary>Height of rows [start, end), including the spacing between them (not around them).</summary>
    public double Height(int start, int end) =>
        end <= start ? 0 : RowHeights[start..end].Sum() + (SpacingY * (end - start - 1));
}

/// <summary>
/// Table layout following CSS 2.1 chapter 17: grid construction with row and column spans, the automatic column width
/// algorithm (columns get between their min-content and max-content widths), fixed layout, and border collapsing.
/// </summary>
internal static class TableLayouter
{
    /// <summary>Places cells on the grid, skipping slots covered by row spans from above.</summary>
    public static (GridCell?[,] Slots, List<GridCell> Cells, int Columns) BuildGrid(TableBlock table)
    {
        var rows = table.Rows.Count;
        var occupied = new List<bool[]>();
        var cells = new List<GridCell>();
        var columns = 0;
        for (var r = 0; r < rows; r++)
        {
            var c = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (IsOccupied(occupied, r, c))
                {
                    c++;
                }

                var rowSpan = Math.Clamp(cell.RowSpan, 1, rows - r);
                var columnSpan = Math.Max(1, cell.ColumnSpan);
                for (var dr = 0; dr < rowSpan; dr++)
                {
                    for (var dc = 0; dc < columnSpan; dc++)
                    {
                        Occupy(occupied, r + dr, c + dc);
                    }
                }

                cells.Add(new GridCell(cell, r, c, rowSpan, columnSpan));
                c += columnSpan;
                columns = Math.Max(columns, c);
            }
        }

        columns = Math.Max(columns, table.ColumnWidths.Count);
        var slots = new GridCell?[rows, Math.Max(1, columns)];
        foreach (var cell in cells)
        {
            for (var dr = 0; dr < cell.RowSpan; dr++)
            {
                for (var dc = 0; dc < cell.ColumnSpan; dc++)
                {
                    slots[cell.Row + dr, cell.Column + dc] = cell;
                }
            }
        }

        return (slots, cells, columns);
    }

    /// <summary>The table's content-box min-content and max-content widths.</summary>
    public static (double Min, double Max) MeasureIntrinsic(TableBlock table)
    {
        var (_, cells, columns) = BuildGrid(table);
        if (columns == 0)
        {
            return (0, 0);
        }

        var (min, max, _, _) = ColumnIntrinsics(table, cells, columns, null);
        var spacing = table.BorderCollapse == BorderCollapse.Separate ? table.HorizontalSpacing * (columns + 1) : 0;
        return (min.Sum() + spacing, Math.Max(min.Sum(), max.Sum()) + spacing);
    }

    /// <summary>Lays the table out at a content width: columns, rows and each cell's content.</summary>
    public static TableLayout Layout(TableBlock table, double x, double width)
    {
        var (slots, cells, columns) = BuildGrid(table);
        var rows = table.Rows.Count;
        var horizontal = table.BorderCollapse == BorderCollapse.Collapse ? CollapseHorizontal(table, slots, rows, columns) : null;
        var vertical = table.BorderCollapse == BorderCollapse.Collapse ? CollapseVertical(table, slots, rows, columns) : null;

        var spacingX = table.BorderCollapse == BorderCollapse.Separate ? table.HorizontalSpacing : 0;
        var widths = table.LayoutMode == TableLayoutMode.Fixed
            ? FixedWidths(table, cells, columns, width - (spacingX * (columns + 1)))
            : AutoWidths(table, cells, columns, width - (spacingX * (columns + 1)), horizontal, vertical);

        var columnX = new double[columns + 1];
        columnX[0] = spacingX;
        for (var c = 0; c < columns; c++)
        {
            columnX[c + 1] = columnX[c] + widths[c] + spacingX;
        }

        // Lay out each cell's content at its width.
        var layouts = new List<CellLayout>(cells.Count);
        foreach (var cell in cells)
        {
            var cellX = columnX[cell.Column];
            var cellWidth = columnX[cell.Column + cell.ColumnSpan] - cellX - spacingX;
            var (left, right, top, bottom) = Insets(cell, horizontal, vertical);
            var innerX = x + cellX + left;
            var innerWidth = Math.Max(1, cellWidth - left - right);
            var (height, content) = Paginator.LayoutStatic(cell.Cell.Content, innerX, innerWidth);
            layouts.Add(new CellLayout(cell, cellX, cellWidth, height, top, bottom, content));
        }

        // Row heights: the tallest single-row cell; cells spanning rows stretch their last row if needed.
        var spacingY = table.BorderCollapse == BorderCollapse.Separate ? table.VerticalSpacing : 0;
        var heights = table.Rows.Select(r => Math.Max(0, r.MinHeight)).ToArray();
        foreach (var cell in layouts.Where(c => c.Grid.RowSpan == 1))
        {
            heights[cell.Grid.Row] = Math.Max(heights[cell.Grid.Row], cell.InsetTop + cell.ContentHeight + cell.InsetBottom);
        }

        foreach (var cell in layouts.Where(c => c.Grid.RowSpan > 1).OrderBy(c => c.Grid.RowSpan))
        {
            var spanned = heights[cell.Grid.Row..(cell.Grid.Row + cell.Grid.RowSpan)].Sum() + (spacingY * (cell.Grid.RowSpan - 1));
            var needed = cell.InsetTop + cell.ContentHeight + cell.InsetBottom;
            if (needed > spanned)
            {
                heights[cell.Grid.Row + cell.Grid.RowSpan - 1] += needed - spanned;
            }
        }

        return new TableLayout
        {
            Table = table,
            ColumnX = columnX,
            RowHeights = heights,
            Cells = layouts,
            Horizontal = horizontal,
            Vertical = vertical,
            Slots = slots,
            HeaderRows = table.Rows.TakeWhile(r => r.IsHeader).Count(),
            Groups = RowGroups(cells, rows),
        };
    }

    /// <summary>Space between a cell's edge and its content: padding plus its border (half of a collapsed line).</summary>
    private static (double Left, double Right, double Top, double Bottom) Insets(GridCell cell, GridLine[,]? horizontal, GridLine[,]? vertical)
    {
        var padding = cell.Cell.Style.Padding;
        if (horizontal is null || vertical is null)
        {
            var border = cell.Cell.Style.Border.Widths;
            return (padding.Left + border.Left, padding.Right + border.Right, padding.Top + border.Top, padding.Bottom + border.Bottom);
        }

        double MaxH(int row) => Enumerable.Range(cell.Column, cell.ColumnSpan).Max(c => horizontal[row, c].Width);
        double MaxV(int column) => Enumerable.Range(cell.Row, cell.RowSpan).Max(r => vertical[r, column].Width);
        return (
            padding.Left + (MaxV(cell.Column) / 2),
            padding.Right + (MaxV(cell.Column + cell.ColumnSpan) / 2),
            padding.Top + (MaxH(cell.Row) / 2),
            padding.Bottom + (MaxH(cell.Row + cell.RowSpan) / 2));
    }

    /// <summary>Per-column min-content and max-content widths (cell borders and padding included).</summary>
    private static (double[] Min, double[] Max, double?[] Percent, bool[] Fixed) ColumnIntrinsics(
        TableBlock table, List<GridCell> cells, int columns, (GridLine[,] H, GridLine[,] V)? collapsed)
    {
        var min = new double[columns];
        var max = new double[columns];
        var percent = new double?[columns];
        var isFixed = new bool[columns];
        for (var c = 0; c < Math.Min(columns, table.ColumnWidths.Count); c++)
        {
            switch (table.ColumnWidths[c])
            {
                case { IsPercent: true } p:
                    percent[c] = p.Value;
                    break;
                case { } w:
                    min[c] = max[c] = w.Value;
                    isFixed[c] = true;
                    break;
            }
        }

        var spacing = table.BorderCollapse == BorderCollapse.Separate ? table.HorizontalSpacing : 0;
        var measured = cells.Select(cell => (Cell: cell, Width: CellIntrinsic(cell, collapsed))).ToList();
        foreach (var (cell, (cellMin, cellMax)) in measured.Where(m => m.Cell.ColumnSpan == 1))
        {
            var c = cell.Column;
            min[c] = Math.Max(min[c], cellMin);
            max[c] = isFixed[c] ? Math.Max(max[c], cellMin) : Math.Max(max[c], cellMax);
            if (cell.Cell.Width is { IsPercent: true } p)
            {
                percent[c] = Math.Max(percent[c] ?? 0, p.Value);
            }
        }

        // Spanning cells: share any extra width over the spanned columns, in proportion to their max-content widths.
        foreach (var (cell, (cellMin, cellMax)) in measured.Where(m => m.Cell.ColumnSpan > 1).OrderBy(m => m.Cell.ColumnSpan))
        {
            var span = Enumerable.Range(cell.Column, cell.ColumnSpan).ToArray();
            Distribute(min, max, span, cellMin - (spacing * (cell.ColumnSpan - 1)), useMin: true);
            Distribute(min, max, span, cellMax - (spacing * (cell.ColumnSpan - 1)), useMin: false);
        }

        for (var c = 0; c < columns; c++)
        {
            max[c] = Math.Max(max[c], min[c]);
        }

        return (min, max, percent, isFixed);
    }

    private static void Distribute(double[] min, double[] max, int[] span, double needed, bool useMin)
    {
        var target = useMin ? min : max;
        var current = span.Sum(c => target[c]);
        if (needed <= current)
        {
            return;
        }

        var weights = span.Select(c => max[c]).ToArray();
        var total = weights.Sum();
        for (var i = 0; i < span.Length; i++)
        {
            target[span[i]] += (needed - current) * (total > 0 ? weights[i] / total : 1.0 / span.Length);
        }
    }

    private static (double Min, double Max) CellIntrinsic(GridCell cell, (GridLine[,] H, GridLine[,] V)? collapsed)
    {
        var style = cell.Cell.Style;
        var decoration = style.Padding.Horizontal + (collapsed is null ? style.Border.Widths.Horizontal : (style.Border.Left.Width + style.Border.Right.Width) / 2);
        double min = 0, max = 0;
        foreach (var block in cell.Cell.Content)
        {
            var (blockMin, blockMax) = Paginator.MeasureIntrinsic(block);
            min = Math.Max(min, blockMin);
            max = Math.Max(max, blockMax);
        }

        if (cell.Cell.Width is { IsPercent: false } fixedWidth)
        {
            min = Math.Max(min, fixedWidth.Value);
            max = Math.Max(min, fixedWidth.Value);
        }

        return (min + decoration, max + decoration);
    }

    /// <summary>CSS 2.1 automatic table layout: percentage columns first, then the rest between min and max content.</summary>
    private static double[] AutoWidths(TableBlock table, List<GridCell> cells, int columns, double available, GridLine[,]? h, GridLine[,]? v)
    {
        var (min, max, percent, isFixed) = ColumnIntrinsics(table, cells, columns, h is null || v is null ? null : (h, v));
        var widths = new double[columns];
        var remaining = available;
        for (var c = 0; c < columns; c++)
        {
            if (percent[c] is { } p)
            {
                widths[c] = Math.Max(min[c], p / 100 * available);
                remaining -= widths[c];
            }
        }

        var others = Enumerable.Range(0, columns).Where(c => percent[c] is null).ToArray();
        var sumMin = others.Sum(c => min[c]);
        var sumMax = others.Sum(c => max[c]);
        if (sumMax <= remaining)
        {
            // Everything fits at its preferred width; share the extra space (a table with a set width), favoring
            // columns without a fixed width, in proportion to their max-content widths.
            var extra = remaining - sumMax;
            var flexible = others.Where(c => !isFixed[c]).ToArray();
            var targets = flexible.Length > 0 ? flexible : others;
            var weight = targets.Sum(c => max[c]);
            foreach (var c in others)
            {
                widths[c] = max[c];
            }

            foreach (var c in targets)
            {
                widths[c] += extra * (weight > 0 ? max[c] / weight : 1.0 / targets.Length);
            }
        }
        else if (sumMin >= remaining)
        {
            foreach (var c in others)
            {
                widths[c] = min[c]; // Overflows, as in CSS.
            }
        }
        else
        {
            var ratio = (remaining - sumMin) / (sumMax - sumMin);
            foreach (var c in others)
            {
                widths[c] = min[c] + ((max[c] - min[c]) * ratio);
            }
        }

        return widths;
    }

    /// <summary>CSS fixed table layout: column specs and first-row cell widths; the rest share what is left.</summary>
    private static double[] FixedWidths(TableBlock table, List<GridCell> cells, int columns, double available)
    {
        var widths = new double?[columns];
        for (var c = 0; c < Math.Min(columns, table.ColumnWidths.Count); c++)
        {
            widths[c] = table.ColumnWidths[c]?.Resolve(available);
        }

        foreach (var cell in cells.Where(c => c.Row == 0 && c.Cell.Width is not null))
        {
            var total = cell.Cell.Width!.Value.Resolve(available);
            var unset = Enumerable.Range(cell.Column, cell.ColumnSpan).Where(c => widths[c] is null).ToList();
            foreach (var c in unset)
            {
                widths[c] = total / cell.ColumnSpan;
            }
        }

        var used = widths.Where(w => w is not null).Sum(w => w!.Value);
        var free = Math.Max(0, available - used);
        var auto = widths.Count(w => w is null);
        return [.. widths.Select(w => w ?? (auto > 0 ? free / auto : 0))];
    }

    /// <summary>Rows joined by row spans form groups that are kept on one page.</summary>
    private static List<(int, int)> RowGroups(List<GridCell> cells, int rows)
    {
        var reach = Enumerable.Range(0, rows).Select(r => r + 1).ToArray();
        foreach (var cell in cells)
        {
            reach[cell.Row] = Math.Max(reach[cell.Row], cell.Row + cell.RowSpan);
        }

        var groups = new List<(int, int)>();
        for (var start = 0; start < rows;)
        {
            var end = reach[start];
            for (var r = start; r < end; r++)
            {
                end = Math.Max(end, reach[r]);
            }

            groups.Add((start, end));
            start = end;
        }

        return groups;
    }

    /// <summary>Collapsed horizontal borders: for each line, the widest of the borders that meet there (CSS 2.1 section 17.6.2.1).</summary>
    private static GridLine[,] CollapseHorizontal(TableBlock table, GridCell?[,] slots, int rows, int columns)
    {
        var lines = new GridLine[rows + 1, columns];
        for (var r = 0; r <= rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var above = r > 0 ? slots[r - 1, c] : null;
                var below = r < rows ? slots[r, c] : null;
                if (above is not null && ReferenceEquals(above, below))
                {
                    continue; // Inside a cell spanning rows.
                }

                var candidates = new List<BorderSide>();
                if (above is not null)
                {
                    candidates.Add(above.Cell.Style.Border.Bottom);
                }

                if (below is not null)
                {
                    candidates.Add(below.Cell.Style.Border.Top);
                }

                if (r == 0)
                {
                    candidates.Add(table.Style.Border.Top);
                }

                if (r == rows)
                {
                    candidates.Add(table.Style.Border.Bottom);
                }

                lines[r, c] = Widest(candidates);
            }
        }

        return lines;
    }

    private static GridLine[,] CollapseVertical(TableBlock table, GridCell?[,] slots, int rows, int columns)
    {
        var lines = new GridLine[rows, columns + 1];
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c <= columns; c++)
            {
                var left = c > 0 ? slots[r, c - 1] : null;
                var right = c < columns ? slots[r, c] : null;
                if (left is not null && ReferenceEquals(left, right))
                {
                    continue; // Inside a cell spanning columns.
                }

                var candidates = new List<BorderSide>();
                if (left is not null)
                {
                    candidates.Add(left.Cell.Style.Border.Right);
                }

                if (right is not null)
                {
                    candidates.Add(right.Cell.Style.Border.Left);
                }

                if (c == 0)
                {
                    candidates.Add(table.Style.Border.Left);
                }

                if (c == columns)
                {
                    candidates.Add(table.Style.Border.Right);
                }

                lines[r, c] = Widest(candidates);
            }
        }

        return lines;
    }

    private static GridLine Widest(List<BorderSide> sides)
    {
        var best = sides.Where(s => s.Width > 0).OrderByDescending(s => s.Width).FirstOrDefault();
        return new GridLine(best.Width, best.Color);
    }

    private static bool IsOccupied(List<bool[]> occupied, int row, int column) =>
        row < occupied.Count && column < occupied[row].Length && occupied[row][column];

    private static void Occupy(List<bool[]> occupied, int row, int column)
    {
        while (occupied.Count <= row)
        {
            occupied.Add([]);
        }

        if (occupied[row].Length <= column)
        {
            var grown = new bool[Math.Max(column + 1, occupied[row].Length * 2)];
            occupied[row].CopyTo(grown, 0);
            occupied[row] = grown;
        }

        occupied[row][column] = true;
    }
}
