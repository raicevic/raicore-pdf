using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Documents.Rendering;

/// <summary>Tables: the grid as fixed column widths, merged cells, borders resolved per cell, shading and header rows.</summary>
internal sealed partial class DocumentRenderer
{
    /// <summary>
    /// Lays out a table with its grid as fixed column widths (scaled down when they are wider than the column), its
    /// rows repeated at the top of each page they are marked for, and each cell's borders: its own, else the table's
    /// outer border at the table's edge and its inside borders elsewhere.
    /// </summary>
    private TableBlock RenderTable(WordTable table, double width, double top)
    {
        // Collapsed outer borders stick out by half their width on each side.
        var outer = (Side(table.Borders.Left).Width + Side(table.Borders.Right).Width) / 2;
        var available = Math.Max(1, width - Math.Max(0, table.Indent) - outer);
        var grid = Grid(table, available);
        var block = new TableBlock(new BoxStyle { Margin = new Thickness(top, 0, 0, table.Alignment == WordAlignment.Start ? Math.Max(0, table.Indent) : 0) })
        {
            ColumnWidths = [.. grid.Select(w => (Length?)Length.FromPoints(w))],
            LayoutMode = TableLayoutMode.Fixed,
            BorderCollapse = BorderCollapse.Collapse,
            Alignment = table.Alignment switch
            {
                WordAlignment.Center => BlockAlignment.Center,
                WordAlignment.End => BlockAlignment.Right,
                _ => BlockAlignment.Left,
            },
            Direction = table.RightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight,
        };

        var columns = grid.Count;
        var header = true;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            header &= row.IsHeader;
            var layoutRow = new TableRow
            {
                IsHeader = header && r < table.Rows.Count - 1,
                BreakInside = row.CantSplit ? PageBreakInside.Avoid : PageBreakInside.Auto,
                MinHeight = row.Height,
            };

            if (row.GridBefore > 0 && row.Cells.Count > 0)
            {
                layoutRow.Add(new TableCell(new ParagraphBlock(new Paragraph(_styles.Style(RunFormat.Default, ParagraphFormat.Default))))
                {
                    ColumnSpan = Math.Min(row.GridBefore, columns),
                });
            }

            foreach (var cell in row.Cells)
            {
                if (cell.Column >= columns)
                {
                    break;
                }

                var span = Math.Clamp(cell.ColumnSpan, 1, columns - cell.Column);
                var rowSpan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r);
                var padding = cell.Padding is { } own
                    ? new CellPadding(
                        own.Top >= 0 ? own.Top : table.Padding.Top,
                        own.Right >= 0 ? own.Right : table.Padding.Right,
                        own.Bottom >= 0 ? own.Bottom : table.Padding.Bottom,
                        own.Left >= 0 ? own.Left : table.Padding.Left)
                    : table.Padding;
                var cellWidth = grid.Skip(cell.Column).Take(span).Sum();
                var contentWidth = Math.Max(1, cellWidth - padding.Left - padding.Right);
                var borders = cell.Borders;
                var firstColumn = cell.Column == 0;
                var lastColumn = cell.Column + span >= columns;
                var style = new BoxStyle
                {
                    Padding = new Thickness(padding.Top, padding.Right, padding.Bottom, padding.Left),
                    Background = cell.Shading,
                    Border = new Borders(
                        Side(borders.Top ?? (r == 0 ? table.Borders.Top : table.InsideHorizontal)),
                        Side(borders.Right ?? (lastColumn ? table.Borders.Right : table.InsideVertical)),
                        Side(borders.Bottom ?? (r + rowSpan >= table.Rows.Count ? table.Borders.Bottom : table.InsideHorizontal)),
                        Side(borders.Left ?? (firstColumn ? table.Borders.Left : table.InsideVertical))),
                };

                List<Block> content;
                _depth++;
                try
                {
                    content = RenderBlocks(cell.Blocks, contentWidth, inTable: true);
                }
                finally
                {
                    _depth--;
                }

                layoutRow.Add(new TableCell(content)
                {
                    ColumnSpan = span,
                    RowSpan = rowSpan,
                    Style = style,
                    VerticalAlignment = cell.VerticalAlignment switch
                    {
                        CellVerticalAlignment.Center => VerticalAlignment.Middle,
                        CellVerticalAlignment.Bottom => VerticalAlignment.Bottom,
                        _ => VerticalAlignment.Top,
                    },
                    Tag = layoutRow.IsHeader ? new StructureTag("TH") { Scope = PdfTableScope.Column } : null,
                });
            }

            block.Add(layoutRow);
        }

        return block;
    }

    /// <summary>
    /// The widths of the grid columns: the table's grid (or equal columns when it has none), scaled to the table's
    /// preferred width when it is a percentage, and down to the available width when wider.
    /// </summary>
    private static List<double> Grid(WordTable table, double available)
    {
        var columns = Math.Max(1, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.GridBefore + r.Cells.Sum(c => Math.Max(1, c.ColumnSpan))));
        columns = Math.Max(columns, table.Grid.Count);
        var grid = new List<double>(columns);
        var known = table.Grid.Where(w => w > 0).ToList();
        var fallback = known.Count > 0 ? known.Average() : available / columns;
        for (var i = 0; i < columns; i++)
        {
            grid.Add(i < table.Grid.Count && table.Grid[i] > 0 ? table.Grid[i] : fallback);
        }

        var total = grid.Sum();
        var target = table.WidthPercent is { } percent ? available * percent : total;
        if (table.Grid.Count == 0 && table.Width is { } preferred)
        {
            target = preferred;
        }

        target = Math.Min(target, available);
        if (total > 0 && Math.Abs(total - target) > 0.01)
        {
            var scale = target / total;
            for (var i = 0; i < grid.Count; i++)
            {
                grid[i] *= scale;
            }
        }

        return grid;
    }
}
