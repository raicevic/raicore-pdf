using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// Tables ([MS-DOC] 2.4.3): paragraphs in a table (<c>sprmPFInTable</c>, at a depth <c>sprmPItap</c>) collected into
/// cells at their cell marks and into rows at their row marks (<c>sprmPFTtp</c>, or <c>sprmPFInnerTtp</c> in a
/// nested table), whose modifiers define the row (<c>sprmTDefTable</c>); a table ends at a paragraph outside it.
/// </summary>
internal sealed partial class DocReader
{
    /// <summary>The most columns a table's grid has; a table with more cell edges gets equal columns.</summary>
    private const int MaxGridColumns = 1024;

    /// <summary>A table being read at one nesting level.</summary>
    internal sealed class TableBuilder
    {
        public List<WordBlock> Cell { get; set; } = [];

        public List<List<WordBlock>> Cells { get; set; } = [];

        public List<(DocRowDefinition? Definition, List<List<WordBlock>> Cells)> Rows { get; } = [];

        public DocRowDefinition? LastDefinition { get; set; }

        public long CellCount { get; set; }
    }

    /// <summary>Puts a paragraph where it belongs: in the story, or in a table cell (ending the cell or the row).</summary>
    private void Place(Story story, WordParagraph paragraph, DocPap pap, char mark, byte[] grpprl)
    {
        var level = pap.TableLevel;
        if (level > _context.Limits.MaxNestingDepth)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), _context.Limits.MaxNestingDepth,
                $"The document nests tables deeper than ResourceLimits.MaxNestingDepth allows ({_context.Limits.MaxNestingDepth}).");
        }

        CloseTables(story, level);
        if (level == 0)
        {
            story.Blocks.Add(paragraph);
            return;
        }

        while (story.Tables.Count < level)
        {
            story.Tables.Add(new TableBuilder());
        }

        var table = story.Tables[level - 1];
        var rowEnd = level == 1 ? pap.TableRowEnd : pap.InnerRowEnd;
        var cellEnd = level == 1 ? mark == '\a' : pap.InnerCellEnd;
        if (rowEnd)
        {
            if (table.Cell.Count > 0)
            {
                EndCell(table);
            }

            table.LastDefinition = DocProperties.Row(grpprl) ?? table.LastDefinition;
            table.Rows.Add((table.LastDefinition, table.Cells));
            table.Cells = [];
            return;
        }

        table.Cell.Add(paragraph);
        if (cellEnd)
        {
            EndCell(table);
        }
    }

    private void EndCell(TableBuilder table)
    {
        table.Cells.Add(table.Cell);
        table.Cell = [];
        if (++table.CellCount > _context.Limits.MaxTableCells)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _context.Limits.MaxTableCells,
                $"A table has more cells than ResourceLimits.MaxTableCells allows ({_context.Limits.MaxTableCells}).");
        }
    }

    /// <summary>
    /// Ends the tables nested deeper than a level, innermost first: each goes into the cell of the table around it (or
    /// the story). Cells of a row that was not ended make a last row; table paragraphs that never made a cell stay
    /// paragraphs.
    /// </summary>
    private static void CloseTables(Story story, int level)
    {
        while (story.Tables.Count > level)
        {
            var builder = story.Tables[^1];
            story.Tables.RemoveAt(story.Tables.Count - 1);
            var target = story.Tables.Count == 0 ? story.Blocks : story.Tables[^1].Cell;
            if (builder.Rows.Count == 0 && builder.Cells.Count == 0)
            {
                target.AddRange(builder.Cell);
                continue;
            }

            if (builder.Cell.Count > 0)
            {
                builder.Cells.Add(builder.Cell);
            }

            if (builder.Cells.Count > 0)
            {
                builder.Rows.Add((builder.LastDefinition, builder.Cells));
            }

            target.Add(BuildTable(builder));
        }
    }

    /// <summary>
    /// Makes a table from its rows: the grid from the cell edges of all rows, cells spanning the grid columns between
    /// their edges, merged cells joined, borders, shading, padding and alignment from the row definitions.
    /// </summary>
    internal static WordTable BuildTable(TableBuilder builder)
    {
        var table = new WordTable();
        var rows = builder.Rows.Where(r => r.Cells.Count > 0).ToList();
        var edges = new List<int[]>(rows.Count);
        var positions = new SortedSet<int>();
        foreach (var (definition, cells) in rows)
        {
            var defined = definition?.Cells ?? [];
            var left = definition?.Left ?? 0;
            var width = defined.Count > 0 ? Math.Max(20, (defined[^1].Right - left) / defined.Count) : 1440;
            var row = new int[cells.Count + 1];
            row[0] = left;
            for (var i = 0; i < cells.Count; i++)
            {
                var right = i < defined.Count ? defined[i].Right : row[i] + width;
                row[i + 1] = Math.Max(right, row[i] + 1);
            }

            edges.Add(row);
            if (positions.Count <= MaxGridColumns + 1)
            {
                positions.UnionWith(row);
            }
        }

        var uniform = positions.Count > MaxGridColumns + 1;
        var index = new Dictionary<int, int>();
        if (!uniform)
        {
            var previous = 0;
            foreach (var position in positions)
            {
                if (index.Count > 0)
                {
                    table.Grid.Add((position - previous) / 20.0);
                }

                index[position] = index.Count;
                previous = position;
            }
        }

        if (rows.Count > 0 && rows[0].Definition is { } first)
        {
            table.Alignment = first.Justification switch
            {
                1 => WordAlignment.Center,
                2 => WordAlignment.End,
                _ => WordAlignment.Start,
            };
            table.Borders = new BoxSides(first.Top, first.RightBorder, first.Bottom, first.LeftBorder);
            table.InsideHorizontal = first.InsideHorizontal;
            table.InsideVertical = first.InsideVertical;
            table.Padding = new CellPadding(
                (first.PaddingTop ?? 0) / 20.0,
                (first.PaddingRight ?? first.GapHalf) / 20.0,
                (first.PaddingBottom ?? 0) / 20.0,
                (first.PaddingLeft ?? first.GapHalf) / 20.0);
        }

        table.Indent = uniform || positions.Count == 0 ? 0 : positions.Min / 20.0;
        var vertical = new Dictionary<int, WordCell>();
        for (var r = 0; r < rows.Count; r++)
        {
            var (definition, cells) = rows[r];
            var row = edges[r];
            var wordRow = new WordRow
            {
                IsHeader = definition?.Header ?? false,
                CantSplit = definition?.CantSplit ?? false,
                GridBefore = uniform ? 0 : index[row[0]],
            };
            if (definition is { Height: not 0 } sized)
            {
                wordRow.Height = Math.Abs(sized.Height) / 20.0;
                wordRow.ExactHeight = sized.Height < 0;
            }

            for (var i = 0; i < cells.Count; i++)
            {
                var cellDefinition = definition is not null && i < definition.Cells.Count ? definition.Cells[i] : null;
                var column = uniform ? i : index[row[i]];
                var end = uniform ? i + 1 : index[row[i + 1]];
                if (cellDefinition is { HorizontalMerge: 2 } && wordRow.Cells.Count > 0)
                {
                    var previous = wordRow.Cells[^1];
                    previous.ColumnSpan = Math.Max(previous.ColumnSpan, end - previous.Column);
                    continue;
                }

                if (cellDefinition is { VerticalMerge: 1 } && vertical.TryGetValue(column, out var above))
                {
                    above.RowSpan++;
                    continue;
                }

                var cell = new WordCell
                {
                    Column = column,
                    ColumnSpan = Math.Max(1, end - column),
                    Borders = cellDefinition is null ? default : new BoxSides(cellDefinition.Top, cellDefinition.Right2, cellDefinition.Bottom, cellDefinition.Left),
                    Shading = cellDefinition?.Shading,
                    VerticalAlignment = cellDefinition?.VerticalAlignment ?? CellVerticalAlignment.Top,
                };
                for (var c = column; c < column + cell.ColumnSpan; c++)
                {
                    vertical.Remove(c);
                }

                if (cellDefinition is { VerticalMerge: >= 2 })
                {
                    vertical[column] = cell;
                }

                cell.Blocks.AddRange(cells[i]);
                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new WordParagraph());
                }

                wordRow.Cells.Add(cell);
            }

            table.Rows.Add(wordRow);
        }

        return table;
    }
}
