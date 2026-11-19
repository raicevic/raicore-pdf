using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>
/// Tables: columns with their widths, header rows, cells spanning columns and rows, borders, padding, backgrounds and
/// vertical alignment, nested tables; repeated rows, columns and cells counted against the limits.
/// </summary>
internal sealed partial class OdtReader
{
    /// <summary>The most grid columns a table has; columns and cells beyond are left out.</summary>
    private const int MaxColumns = 1024;

    private int _tableDepth;

    /// <summary>The rows and cells added for repetitions (<c>number-rows-repeated</c>, <c>number-columns-repeated</c>).</summary>
    private long _repeated;

    /// <summary>A grid column: its width (absolute or relative) and the style of its cells that have none.</summary>
    private readonly record struct Column(double? Width, double? Relative, string? CellStyle);

    /// <summary>Reads a table in a block container, with the page break or master page its style asks for.</summary>
    private void ReadTableBlock(XElement element, List<WordBlock> output, bool body)
    {
        var name = Odf.Attr(element, Odf.Table, "style-name");
        var style = _styles.Find("table", name, _scope);
        var properties = _styles.Properties("table", name, _scope, "table-properties");
        var master = Odf.Attr(style, Odf.Style, "master-page-name") is { Length: > 0 } m ? m : null;
        var before = OdtStyles.Property(properties, Odf.Fo, "break-before") is "page" or "column" or "even-page" or "odd-page";
        var after = OdtStyles.Property(properties, Odf.Fo, "break-after") is "page" or "column" or "even-page" or "odd-page";
        var pageNumber = int.TryParse(OdtStyles.Property(properties, Odf.Style, "page-number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? Math.Clamp(number, 0, 1_000_000)
            : (int?)null;
        if (body && master is not null)
        {
            _width = ContentWidth(_styles.MasterPage(master));
        }

        var table = ReadTable(element);
        AddWithBreak(table, output, body, master, pageNumber, before, after);
    }

    /// <summary>Reads a table. Tables nest no deeper than <see cref="ResourceLimits.MaxNestingDepth"/>.</summary>
    private WordTable ReadTable(XElement element)
    {
        if (_tableDepth + 1 > _context.Limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _context.Limits.MaxNestingDepth,
                $"The document nests tables deeper than ResourceLimits.MaxNestingDepth allows ({_context.Limits.MaxNestingDepth}).");
        }

        var width = _width;
        _tableDepth++;
        try
        {
            return ReadTableContent(element);
        }
        finally
        {
            _tableDepth--;
            _width = width;
        }
    }

    private WordTable ReadTableContent(XElement element)
    {
        var properties = _styles.Properties("table", Odf.Attr(element, Odf.Table, "style-name"), _scope, "table-properties");
        var table = new WordTable { Padding = new CellPadding(0, 0, 0, 0) };
        var available = _width;
        var align = OdtStyles.Property(properties, Odf.Table, "align");
        table.Alignment = align switch
        {
            "center" => WordAlignment.Center,
            "right" => WordAlignment.End,
            _ => WordAlignment.Start,
        };
        var marginLeft = Odf.Length(OdtStyles.Property(properties, Odf.Fo, "margin-left")) ?? 0;
        var marginRight = Odf.Length(OdtStyles.Property(properties, Odf.Fo, "margin-right")) ?? 0;
        if (align is null or "left" or "margins")
        {
            table.Indent = Math.Clamp(marginLeft, -720, 7200);
        }

        var tableWidth = align == "margins" ? available - Math.Max(0, marginLeft) - Math.Max(0, marginRight)
            : Odf.Percent(OdtStyles.Property(properties, Odf.Style, "rel-width")) is { } percent and > 0 ? available * Math.Min(percent, 100) / 100
            : Odf.Length(OdtStyles.Property(properties, Odf.Style, "width")) ?? available;
        tableWidth = Math.Clamp(tableWidth, 1, 14400);
        var background = Odf.Color(OdtStyles.Property(properties, Odf.Fo, "background-color"));

        var columns = new List<Column>();
        CollectColumns(element, columns);
        Grid(table, columns, tableWidth);

        var rows = new List<(XElement Row, bool Header)>();
        CollectRows(element, rows, header: false);
        var gridColumns = Math.Max(1, table.Grid.Count);
        long cells = 0;
        foreach (var (rowElement, header) in rows)
        {
            var repeat = Math.Max(1, Odf.Int(rowElement, Odf.Table, "number-rows-repeated") ?? 1);
            var row = ReadRow(rowElement, header, table, columns, background);
            cells += Math.Max(row.Cells.Count == 0 ? 0 : row.Cells[^1].Column + row.Cells[^1].ColumnSpan, gridColumns) * (long)repeat;
            if (cells > _context.Limits.MaxTableCells)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _context.Limits.MaxTableCells,
                    $"A table has more cells than ResourceLimits.MaxTableCells allows ({_context.Limits.MaxTableCells}).");
            }

            CountRepeated(repeat - 1);
            table.Rows.Add(row);
            for (var i = 1; i < repeat; i++)
            {
                table.Rows.Add(Copy(row));
                if (i % CheckpointInterval == 0)
                {
                    _context.Checkpoint();
                }
            }
        }

        return table;
    }

    /// <summary>The grid column widths: absolute widths, else relative ones shared out of the table's width, else equal columns.</summary>
    private static void Grid(WordTable table, List<Column> columns, double tableWidth)
    {
        if (columns.Count == 0)
        {
            return;
        }

        var absolute = columns.Where(c => c.Width is > 0).Select(c => c.Width!.Value).ToList();
        if (absolute.Count > 0)
        {
            var fallback = absolute.Average();
            table.Grid.AddRange(columns.Select(c => Math.Min(c.Width is > 0 ? c.Width.Value : fallback, 14400)));
            return;
        }

        var relative = columns.Sum(c => c.Relative is > 0 ? c.Relative.Value : 0);
        foreach (var column in columns)
        {
            table.Grid.Add(relative > 0 && column.Relative is > 0 ? tableWidth * column.Relative.Value / relative : tableWidth / columns.Count);
        }
    }

    /// <summary>Collects a table's columns (also in column groups and header columns), repeated ones up to <see cref="MaxColumns"/>.</summary>
    private void CollectColumns(XElement container, List<Column> columns)
    {
        foreach (var element in container.Elements())
        {
            if (columns.Count >= MaxColumns)
            {
                return;
            }

            if (Odf.Is(element, Odf.Table, "table-column"))
            {
                var properties = _styles.Properties("table-column", Odf.Attr(element, Odf.Table, "style-name"), _scope, "table-column-properties");
                var width = Odf.Length(OdtStyles.Property(properties, Odf.Style, "column-width"));
                var relativeText = OdtStyles.Property(properties, Odf.Style, "rel-column-width")?.TrimEnd('*');
                double? relative = double.TryParse(relativeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) && r > 0 ? r : null;
                var column = new Column(width is > 0 ? Math.Min(width.Value, 14400) : null, relative, Odf.Attr(element, Odf.Table, "default-cell-style-name"));
                var repeat = Math.Clamp(Odf.Int(element, Odf.Table, "number-columns-repeated") ?? 1, 1, MaxColumns - columns.Count);
                for (var i = 0; i < repeat; i++)
                {
                    columns.Add(column);
                }
            }
            else if (element.Name.Namespace == Odf.Table && element.Name.LocalName is "table-columns" or "table-header-columns" or "table-column-group")
            {
                CollectColumns(element, columns);
            }
        }
    }

    /// <summary>Collects a table's rows, in header rows, row groups and row lists too.</summary>
    private static void CollectRows(XElement container, List<(XElement, bool)> rows, bool header)
    {
        foreach (var element in container.Elements())
        {
            if (Odf.Is(element, Odf.Table, "table-row"))
            {
                rows.Add((element, header));
            }
            else if (Odf.Is(element, Odf.Table, "table-header-rows"))
            {
                CollectRows(element, rows, header: true);
            }
            else if (element.Name.Namespace == Odf.Table && element.Name.LocalName is "table-rows" or "table-row-group")
            {
                CollectRows(element, rows, header);
            }
        }
    }

    /// <summary>Reads a row: its height and cells (covered cells, under a cell spanning them, are left out).</summary>
    private WordRow ReadRow(XElement element, bool header, WordTable table, List<Column> columns, PdfColor? tableBackground)
    {
        var properties = _styles.Properties("table-row", Odf.Attr(element, Odf.Table, "style-name"), _scope, "table-row-properties");
        var row = new WordRow { IsHeader = header, CantSplit = OdtStyles.Property(properties, Odf.Fo, "keep-together") == "always" };
        if (Odf.Length(OdtStyles.Property(properties, Odf.Style, "row-height")) is { } height && height > 0)
        {
            (row.Height, row.ExactHeight) = (Math.Min(height, 14400), true);
        }
        else if (Odf.Length(OdtStyles.Property(properties, Odf.Style, "min-row-height")) is { } least && least > 0)
        {
            row.Height = Math.Min(least, 14400);
        }

        var rowBackground = OdtStyles.Property(properties, Odf.Fo, "background-color") is { } color ? Odf.Color(color) : tableBackground;
        var rowCellStyle = Odf.Attr(element, Odf.Table, "default-cell-style-name");
        var column = 0;
        foreach (var cellElement in element.Elements())
        {
            if (column >= MaxColumns)
            {
                break;
            }

            var covered = Odf.Is(cellElement, Odf.Table, "covered-table-cell");
            if (!covered && !Odf.Is(cellElement, Odf.Table, "table-cell"))
            {
                continue;
            }

            var repeat = Math.Clamp(Odf.Int(cellElement, Odf.Table, "number-columns-repeated") ?? 1, 1, MaxColumns - column);
            if (covered)
            {
                column += repeat;
                continue;
            }

            var span = Math.Clamp(Odf.Int(cellElement, Odf.Table, "number-columns-spanned") ?? 1, 1, MaxColumns);
            var cell = new WordCell
            {
                Column = column,
                ColumnSpan = span,
                RowSpan = Math.Clamp(Odf.Int(cellElement, Odf.Table, "number-rows-spanned") ?? 1, 1, 65535),
            };
            var styleName = Odf.Attr(cellElement, Odf.Table, "style-name") ?? (column < columns.Count ? columns[column].CellStyle : null) ?? rowCellStyle;
            ReadCellFormat(cell, styleName, rowBackground);
            var padding = cell.Padding ?? table.Padding;
            var gridWidth = table.Grid.Skip(column).Take(span).Sum();
            _width = Math.Max(36, (gridWidth > 0 ? gridWidth : _width) - Math.Max(0, padding.Left) - Math.Max(0, padding.Right));
            ReadBlocks(cellElement, cell.Blocks, body: false);
            if (cell.Blocks.Count == 0)
            {
                cell.Blocks.Add(new WordParagraph { Format = _styles.Paragraph(null, _scope).Format });
            }

            CountRepeated(repeat - 1);
            for (var i = 0; i < repeat && column < MaxColumns; i++)
            {
                var copy = i == 0 ? cell : CopyCell(cell);
                copy.Column = column;
                row.Cells.Add(copy);
                column++; // A spanning cell takes one grid column; the covered cells after it take the others.
            }
        }

        return row;
    }

    /// <summary>
    /// Counts rows or cells added for repetitions against <see cref="ResourceLimits.MaxElements"/>, as the elements
    /// they stand for.
    /// </summary>
    private void CountRepeated(long count)
    {
        _repeated += count;
        if (_repeated > _context.Limits.MaxElements)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), _context.Limits.MaxElements,
                $"The document's repeated table rows and cells are more than ResourceLimits.MaxElements allows ({_context.Limits.MaxElements}).");
        }
    }

    /// <summary>A cell's borders, padding, background and vertical alignment from its style.</summary>
    private void ReadCellFormat(WordCell cell, string? styleName, PdfColor? rowBackground)
    {
        var properties = _styles.Properties("table-cell", styleName, _scope, "table-cell-properties");
        string? Get(string name) => OdtStyles.Property(properties, Odf.Fo, name);
        var all = Odf.Border(Get("border"));
        cell.Borders = new BoxSides(Odf.Border(Get("border-top")) ?? all, Odf.Border(Get("border-right")) ?? all, Odf.Border(Get("border-bottom")) ?? all, Odf.Border(Get("border-left")) ?? all);
        var padding = Odf.Length(Get("padding"));
        double Side(string name) => Math.Clamp(Odf.Length(Get(name)) ?? padding ?? -1, -1, 720);
        if (padding is not null || Get("padding-top") is not null || Get("padding-right") is not null || Get("padding-bottom") is not null || Get("padding-left") is not null)
        {
            cell.Padding = new CellPadding(Side("padding-top"), Side("padding-right"), Side("padding-bottom"), Side("padding-left"));
        }

        cell.Shading = Get("background-color") is { } background ? Odf.Color(background) : rowBackground;
        cell.VerticalAlignment = OdtStyles.Property(properties, Odf.Style, "vertical-align") switch
        {
            "middle" => CellVerticalAlignment.Center,
            "bottom" => CellVerticalAlignment.Bottom,
            _ => CellVerticalAlignment.Top,
        };
    }

    /// <summary>A copy of a repeated row, its cells sharing their content.</summary>
    private static WordRow Copy(WordRow row)
    {
        var copy = new WordRow { IsHeader = row.IsHeader, CantSplit = row.CantSplit, Height = row.Height, ExactHeight = row.ExactHeight, GridBefore = row.GridBefore };
        copy.Cells.AddRange(row.Cells.Select(CopyCell));
        return copy;
    }

    private static WordCell CopyCell(WordCell cell)
    {
        var copy = new WordCell
        {
            Column = cell.Column,
            ColumnSpan = cell.ColumnSpan,
            RowSpan = cell.RowSpan,
            Borders = cell.Borders,
            Shading = cell.Shading,
            Padding = cell.Padding,
            VerticalAlignment = cell.VerticalAlignment,
        };
        copy.Blocks.AddRange(cell.Blocks);
        return copy;
    }
}
