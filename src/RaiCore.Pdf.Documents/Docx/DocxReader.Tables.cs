using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>Tables: grid, merged cells, borders, shading, padding and table styles with their conditional formats.</summary>
internal sealed partial class DocxReader
{
    /// <summary>Which conditional formats of a table style a table uses (<c>w:tblLook</c>).</summary>
    private readonly record struct TableLook(bool FirstRow, bool LastRow, bool FirstColumn, bool LastColumn, bool RowBands, bool ColumnBands)
    {
        /// <summary>Word's default: first row, first column and row bands.</summary>
        public static TableLook Default => new(true, false, true, false, true, false);

        public static TableLook Read(XElement? look)
        {
            if (look is null)
            {
                return Default;
            }

            if (Ooxml.Attr(look, "val") is { } hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)
                && Ooxml.Attr(look, "firstRow") is null)
            {
                return new((bits & 0x20) != 0, (bits & 0x40) != 0, (bits & 0x80) != 0, (bits & 0x100) != 0, (bits & 0x200) == 0, (bits & 0x400) == 0);
            }

            bool On(string name, bool fallback) => Ooxml.Attr(look, name) is { } value ? Ooxml.IsOn(value) : fallback;
            return new(On("firstRow", true), On("lastRow", false), On("firstColumn", true), On("lastColumn", false), !On("noHBand", false), !On("noVBand", true));
        }
    }

    /// <summary>Reads a table. Its cells start from the document defaults again: formatting does not inherit from an outer table.</summary>
    private WordTable ReadTable(XElement tbl)
    {
        if (_tableDepth + 1 > _context.Limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _context.Limits.MaxNestingDepth,
                $"The document nests tables deeper than ResourceLimits.MaxNestingDepth allows ({_context.Limits.MaxNestingDepth}).");
        }

        _tableDepth++;
        try
        {
            return ReadTableContent(tbl);
        }
        finally
        {
            _tableDepth--;
        }
    }

    private WordTable ReadTableContent(XElement tbl)
    {
        var tblPr = Ooxml.Child(tbl, "tblPr");
        var chain = _styles.Chain(Ooxml.Val(tblPr, "tblStyle") ?? _styles.DefaultTableStyle, "table");
        var table = new WordTable();

        var properties = chain.Select(s => s.TableProperties).Append(tblPr).OfType<XElement>().ToList();
        foreach (var element in properties)
        {
            ApplyTableProperties(table, element, element == tblPr);
        }

        foreach (var column in Ooxml.Children(Ooxml.Child(tbl, "tblGrid"), "gridCol"))
        {
            if (table.Grid.Count >= 1024)
            {
                break;
            }

            table.Grid.Add(Math.Clamp(Ooxml.Twips(column, "w") ?? 0, 0, 14400));
        }

        var look = TableLook.Read(Ooxml.Child(tblPr, "tblLook"));
        var rowBand = Math.Max(1, chain.Select(s => Ooxml.Int(Ooxml.Child(s.TableProperties, "tblStyleRowBandSize"), "val")).LastOrDefault(v => v is not null) ?? 1);
        var columnBand = Math.Max(1, chain.Select(s => Ooxml.Int(Ooxml.Child(s.TableProperties, "tblStyleColBandSize"), "val")).LastOrDefault(v => v is not null) ?? 1);
        var rows = Rows(tbl).ToList();
        var columns = Math.Max(table.Grid.Count, 1);
        var vertical = new Dictionary<int, WordCell>(); // The cell a vertical merge started with, by grid column.
        var headers = true;
        long cells = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            var tr = rows[r];
            var trPr = Ooxml.Child(tr, "trPr");
            var row = new WordRow
            {
                CantSplit = Ooxml.OnOff(trPr, "cantSplit") ?? false,
                GridBefore = Math.Clamp(Ooxml.Int(Ooxml.Child(trPr, "gridBefore"), "val") ?? 0, 0, 1024),
            };
            headers &= Ooxml.OnOff(trPr, "tblHeader") ?? false;
            row.IsHeader = headers;
            if (Ooxml.Child(trPr, "trHeight") is { } height && Ooxml.Twips(height, "val") is { } value && value > 0)
            {
                row.Height = Math.Min(value, 14400);
                row.ExactHeight = Ooxml.Attr(height, "hRule") == "exact";
            }

            var column = row.GridBefore;
            foreach (var tc in Cells(tr))
            {
                var tcPr = Ooxml.Child(tc, "tcPr");
                var span = Math.Clamp(Ooxml.Int(Ooxml.Child(tcPr, "gridSpan"), "val") ?? 1, 1, 1024);
                var merge = Ooxml.Child(tcPr, "vMerge");
                if (merge is not null && Ooxml.Attr(merge, "val") is null or "continue")
                {
                    if (vertical.TryGetValue(column, out var start))
                    {
                        start.RowSpan++;
                        column += span;
                        continue;
                    }
                }

                if (Ooxml.Val(tcPr, "hMerge") == "continue" && row.Cells.Count > 0)
                {
                    row.Cells[^1].ColumnSpan += span; // Legacy horizontal merge: the cell joins the one before it.
                    column += span;
                    continue;
                }

                var cell = new WordCell { Column = column, ColumnSpan = span };
                for (var c = column; c < column + span; c++)
                {
                    vertical.Remove(c);
                }

                if (merge is not null)
                {
                    vertical[column] = cell;
                }

                var conditions = Conditions(chain, look, r, rows.Count, column, span, columns, rowBand, columnBand, row.IsHeader);
                ReadCellFormat(cell, chain, conditions, tcPr);
                var context = new TableContext(
                    [.. chain.Select(s => s.ParagraphProperties).Concat(conditions.Select(c => Ooxml.Child(c, "pPr"))).OfType<XElement>()],
                    [.. chain.Select(s => s.RunProperties).Concat(conditions.Select(c => Ooxml.Child(c, "rPr"))).OfType<XElement>()]);
                ReadBlocks(tc, cell.Blocks, context);
                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new WordParagraph());
                }

                row.Cells.Add(cell);
                column += span;
            }

            cells += Math.Max(column, columns);
            if (cells > _context.Limits.MaxTableCells)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _context.Limits.MaxTableCells,
                    $"A table has more cells than ResourceLimits.MaxTableCells allows ({_context.Limits.MaxTableCells}).");
            }

            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>The rows of a table, inside content controls and tracked insertions; deleted rows are left out.</summary>
    private static IEnumerable<XElement> Rows(XElement tbl)
    {
        foreach (var element in tbl.Elements())
        {
            if (Ooxml.Is(element, "tr"))
            {
                if (Ooxml.Child(Ooxml.Child(element, "trPr"), "del") is null)
                {
                    yield return element;
                }
            }
            else if (Ooxml.Is(element, "sdt") || Ooxml.Is(element, "customXml") || Ooxml.Is(element, "ins") || Ooxml.Is(element, "moveTo"))
            {
                foreach (var row in Rows(Ooxml.Child(element, "sdtContent") ?? element))
                {
                    yield return row;
                }
            }
        }
    }

    /// <summary>The cells of a row, inside content controls and tracked insertions.</summary>
    private static IEnumerable<XElement> Cells(XElement tr)
    {
        foreach (var element in tr.Elements())
        {
            if (Ooxml.Is(element, "tc"))
            {
                yield return element;
            }
            else if (Ooxml.Is(element, "sdt") || Ooxml.Is(element, "customXml") || Ooxml.Is(element, "ins") || Ooxml.Is(element, "moveTo"))
            {
                foreach (var cell in Cells(Ooxml.Child(element, "sdtContent") ?? element))
                {
                    yield return cell;
                }
            }
        }
    }

    private void ApplyTableProperties(WordTable table, XElement tblPr, bool direct)
    {
        if (Ooxml.Child(tblPr, "tblBorders") is { } borders)
        {
            var current = table.Borders;
            table.Borders = new BoxSides(
                Ooxml.Border(Ooxml.Child(borders, "top"), _theme) ?? current.Top,
                Ooxml.Border(Ooxml.Child(borders, "right") ?? Ooxml.Child(borders, "end"), _theme) ?? current.Right,
                Ooxml.Border(Ooxml.Child(borders, "bottom"), _theme) ?? current.Bottom,
                Ooxml.Border(Ooxml.Child(borders, "left") ?? Ooxml.Child(borders, "start"), _theme) ?? current.Left);
            table.InsideHorizontal = Ooxml.Border(Ooxml.Child(borders, "insideH"), _theme) ?? table.InsideHorizontal;
            table.InsideVertical = Ooxml.Border(Ooxml.Child(borders, "insideV"), _theme) ?? table.InsideVertical;
        }

        if (Ooxml.Child(tblPr, "tblCellMar") is { } margins)
        {
            table.Padding = Padding(margins, table.Padding);
        }

        if (Ooxml.Child(tblPr, "tblInd") is { } indent && Ooxml.Attr(indent, "type") is null or "dxa" && Ooxml.Twips(indent, "w") is { } points)
        {
            table.Indent = Math.Clamp(points, -720, 7200);
        }

        if (Ooxml.Val(tblPr, "jc") is { } jc)
        {
            table.Alignment = jc switch
            {
                "center" => WordAlignment.Center,
                "right" or "end" => WordAlignment.End,
                _ => WordAlignment.Start,
            };
        }

        table.RightToLeft = Ooxml.OnOff(tblPr, "bidiVisual") ?? table.RightToLeft;
        if (direct && Ooxml.Child(tblPr, "tblW") is { } width)
        {
            var type = Ooxml.Attr(width, "type");
            var value = Ooxml.Attr(width, "w");
            if (type == "pct" && value is not null)
            {
                var percent = value.EndsWith('%')
                    ? double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p / 100 : 0
                    : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fiftieths) ? fiftieths / 5000 : 0;
                table.WidthPercent = percent is > 0 and <= 10 ? percent : null;
            }
            else if (type is null or "dxa" && Ooxml.Twips(width, "w") is > 0 and var points2)
            {
                table.Width = Math.Min(points2, 14400);
            }
        }
    }

    /// <summary>Cell margins (<c>w:tblCellMar</c>, <c>w:tcMar</c>) over inherited ones.</summary>
    private static CellPadding Padding(XElement margins, CellPadding inherited)
    {
        static double? Side(XElement margins, string name, string alternative)
        {
            var side = Ooxml.Child(margins, name) ?? Ooxml.Child(margins, alternative);
            return side is not null && Ooxml.Attr(side, "type") is null or "dxa" && Ooxml.Twips(side, "w") is { } value ? Math.Clamp(value, 0, 720) : null;
        }

        return new CellPadding(
            Side(margins, "top", "top") ?? inherited.Top,
            Side(margins, "right", "end") ?? inherited.Right,
            Side(margins, "bottom", "bottom") ?? inherited.Bottom,
            Side(margins, "left", "start") ?? inherited.Left);
    }

    /// <summary>
    /// The conditional formats of the table style that apply to a cell, lowest priority first: whole table, column
    /// and row bands, first and last column, first and last row, corner cells.
    /// </summary>
    private static List<XElement> Conditions(
        List<DocxStyle> chain, TableLook look, int row, int rows, int column, int span, int columns, int rowBand, int columnBand, bool header)
    {
        var types = new List<string> { "wholeTable" };
        var firstColumn = look.FirstColumn && column == 0;
        var lastColumn = look.LastColumn && column + span >= columns;
        var firstRow = look.FirstRow && (row == 0 || header);
        var lastRow = look.LastRow && row == rows - 1;
        if (look.ColumnBands && !firstColumn && !lastColumn)
        {
            types.Add(((column - (look.FirstColumn ? 1 : 0)) / columnBand) % 2 == 0 ? "band1Vert" : "band2Vert");
        }

        if (look.RowBands && !firstRow && !lastRow)
        {
            types.Add(((row - (look.FirstRow ? 1 : 0)) / rowBand) % 2 == 0 ? "band1Horz" : "band2Horz");
        }

        if (firstColumn)
        {
            types.Add("firstCol");
        }

        if (lastColumn)
        {
            types.Add("lastCol");
        }

        if (firstRow)
        {
            types.Add("firstRow");
        }

        if (lastRow)
        {
            types.Add("lastRow");
        }

        if (firstRow && firstColumn)
        {
            types.Add("nwCell");
        }

        if (firstRow && lastColumn)
        {
            types.Add("neCell");
        }

        if (lastRow && firstColumn)
        {
            types.Add("swCell");
        }

        if (lastRow && lastColumn)
        {
            types.Add("seCell");
        }

        var result = new List<XElement>();
        foreach (var type in types)
        {
            foreach (var style in chain)
            {
                if (style.Conditions.TryGetValue(type, out var condition))
                {
                    result.Add(condition);
                }
            }
        }

        return result;
    }

    /// <summary>A cell's shading, borders, padding and alignment: from the table style, its conditional formats, then the cell.</summary>
    private void ReadCellFormat(WordCell cell, List<DocxStyle> chain, List<XElement> conditions, XElement? tcPr)
    {
        var sources = chain.Select(s => s.CellProperties).Concat(conditions.Select(c => Ooxml.Child(c, "tcPr"))).Append(tcPr).OfType<XElement>();
        foreach (var properties in sources)
        {
            if (Ooxml.Child(properties, "shd") is { } shd)
            {
                cell.Shading = Ooxml.Shading(shd, _theme);
            }

            if (Ooxml.Child(properties, "tcBorders") is { } borders)
            {
                var current = cell.Borders;
                cell.Borders = new BoxSides(
                    Ooxml.Border(Ooxml.Child(borders, "top"), _theme) ?? current.Top,
                    Ooxml.Border(Ooxml.Child(borders, "right") ?? Ooxml.Child(borders, "end"), _theme) ?? current.Right,
                    Ooxml.Border(Ooxml.Child(borders, "bottom"), _theme) ?? current.Bottom,
                    Ooxml.Border(Ooxml.Child(borders, "left") ?? Ooxml.Child(borders, "start"), _theme) ?? current.Left);
            }

            if (Ooxml.Child(properties, "tcMar") is { } margins)
            {
                cell.Padding = Padding(margins, cell.Padding ?? new CellPadding(-1, -1, -1, -1));
            }

            if (Ooxml.Val(properties, "vAlign") is { } align)
            {
                cell.VerticalAlignment = align switch
                {
                    "center" => CellVerticalAlignment.Center,
                    "bottom" => CellVerticalAlignment.Bottom,
                    _ => CellVerticalAlignment.Top,
                };
            }
        }
    }
}
