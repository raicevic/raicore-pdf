using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>
/// Tables: cells (<c>\cell</c>, <c>\nestcell</c>) collected into rows (<c>\row</c>, <c>\nestrow</c>) with the row
/// definition in effect at the row's end, and turned into a grid when the table ends (a paragraph outside it).
/// </summary>
internal sealed partial class RtfReader
{
    /// <summary>The most columns a table's grid has; a table with more cell edges gets equal columns.</summary>
    private const int MaxGridColumns = 1024;

    /// <summary>A table being read at one nesting level.</summary>
    private sealed class TableBuilder
    {
        /// <summary>The blocks of the cell being read.</summary>
        public List<WordBlock> Cell { get; set; } = [];

        /// <summary>The cells of the row being read.</summary>
        public List<List<WordBlock>> Cells { get; set; } = [];

        public List<(RtfRowDefinition? Definition, List<List<WordBlock>> Cells)> Rows { get; } = [];

        public RtfRowDefinition? LastDefinition { get; set; }

        public long CellCount { get; set; }
    }

    /// <summary>Applies a table row or cell definition control word, returning whether it was one.</summary>
    private bool RowWord(Group group, string word, int parameter, bool hasParameter)
    {
        var on = !hasParameter || parameter != 0;
        var twips = Math.Clamp(parameter, -MaxTwips, MaxTwips);
        if (word == "trowd")
        {
            group.Row = new RtfRowDefinition();
            _borderTarget = RtfBorderTarget.None;
            return true;
        }

        if (!(word.StartsWith("tr", StringComparison.Ordinal) || word.StartsWith("cl", StringComparison.Ordinal) || word is "cellx" or "rtlrow" or "ltrrow"))
        {
            return false;
        }

        var row = group.Row ??= new RtfRowDefinition();
        var cell = row.Pending;
        switch (word)
        {
            case "trgaph":
                row.Gap = Math.Clamp(parameter, 0, MaxTwips);
                break;
            case "trleft":
                row.Left = twips;
                break;
            case "trql":
                row.Alignment = WordAlignment.Start;
                break;
            case "trqc":
                row.Alignment = WordAlignment.Center;
                break;
            case "trqr":
                row.Alignment = WordAlignment.End;
                break;
            case "trrh":
                row.Height = twips;
                break;
            case "trhdr":
                row.Header = on;
                break;
            case "trkeep":
                row.Keep = on;
                break;
            case "rtlrow":
                row.RightToLeft = true;
                break;
            case "ltrrow":
                row.RightToLeft = false;
                break;
            case "trpaddl":
                row.PaddingLeft = Math.Max(0, twips);
                break;
            case "trpaddr":
                row.PaddingRight = Math.Max(0, twips);
                break;
            case "trpaddt":
                row.PaddingTop = Math.Max(0, twips);
                break;
            case "trpaddb":
                row.PaddingBottom = Math.Max(0, twips);
                break;
            case "trpaddfl" when parameter != 3:
                row.PaddingLeft = null;
                break;
            case "trpaddfr" when parameter != 3:
                row.PaddingRight = null;
                break;
            case "trpaddft" when parameter != 3:
                row.PaddingTop = null;
                break;
            case "trpaddfb" when parameter != 3:
                row.PaddingBottom = null;
                break;
            case "trbrdrt":
                StartRowBorder(RtfBorderTarget.RowTop);
                break;
            case "trbrdrb":
                StartRowBorder(RtfBorderTarget.RowBottom);
                break;
            case "trbrdrl":
                StartRowBorder(RtfBorderTarget.RowLeft);
                break;
            case "trbrdrr":
                StartRowBorder(RtfBorderTarget.RowRight);
                break;
            case "trbrdrh":
                StartRowBorder(RtfBorderTarget.RowHorizontal);
                break;
            case "trbrdrv":
                StartRowBorder(RtfBorderTarget.RowVertical);
                break;
            case "clbrdrt":
                StartRowBorder(RtfBorderTarget.CellTop);
                break;
            case "clbrdrb":
                StartRowBorder(RtfBorderTarget.CellBottom);
                break;
            case "clbrdrl":
                StartRowBorder(RtfBorderTarget.CellLeft);
                break;
            case "clbrdrr":
                StartRowBorder(RtfBorderTarget.CellRight);
                break;
            case "clmgf":
                cell.MergeFirst = true;
                break;
            case "clmrg":
                cell.Merged = true;
                break;
            case "clvmgf":
                cell.VerticalMergeFirst = true;
                break;
            case "clvmrg":
                cell.VerticalMerged = true;
                break;
            case "clvertalt":
                cell.VerticalAlignment = CellVerticalAlignment.Top;
                break;
            case "clvertalc":
                cell.VerticalAlignment = CellVerticalAlignment.Center;
                break;
            case "clvertalb":
                cell.VerticalAlignment = CellVerticalAlignment.Bottom;
                break;
            case "clcbpat":
                cell.BackgroundColor = parameter;
                break;
            case "clcfpat":
                cell.PatternColor = parameter;
                break;
            case "clshdng":
                cell.Shading = Math.Clamp(parameter, 0, 10000);
                break;
            case "cellx":
                cell.Right = twips;
                if (row.Cells.Count < MaxGridColumns)
                {
                    row.Cells.Add(cell);
                }

                row.Pending = new RtfCellDefinition();
                _borderTarget = RtfBorderTarget.None;
                break;
            default:
                return word.StartsWith("tr", StringComparison.Ordinal) || word.StartsWith("cl", StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>Starts a row or cell border side, with no style until the border words give it one.</summary>
    private void StartRowBorder(RtfBorderTarget target)
    {
        _borderTarget = target;
        var paragraph = default(RtfParagraphState);
        SetBorder(ref paragraph, target, new BorderLine(0.5, PdfColor.Black, BorderLineStyle.None));
    }

    /// <summary>Ends a cell at a nesting level: the paragraph before it is its last.</summary>
    private void EndCell(int level)
    {
        EmitRun();
        var story = CurrentStory;
        CloseTables(story, level);
        OpenTables(story, level);
        var table = story.Tables[level - 1];
        if (story.Paragraph is not null)
        {
            table.Cell.Add(BuildParagraph(story));
        }

        table.Cells.Add(table.Cell);
        table.Cell = [];
        CountCells(table);
    }

    /// <summary>Ends a row at a nesting level, with the row definition in effect.</summary>
    private void EndRow(int level, Group group)
    {
        EmitRun();
        var story = CurrentStory;
        CloseTables(story, level);
        OpenTables(story, level);
        var table = story.Tables[level - 1];
        if (story.Paragraph is not null)
        {
            table.Cell.Add(BuildParagraph(story));
        }

        if (table.Cell.Count > 0)
        {
            table.Cells.Add(table.Cell);
            table.Cell = [];
            CountCells(table);
        }

        table.LastDefinition = group.Row ?? table.LastDefinition;
        table.Rows.Add((table.LastDefinition, table.Cells));
        table.Cells = [];
    }

    private void CountCells(TableBuilder table)
    {
        if (++table.CellCount > _context.Limits.MaxTableCells)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), _context.Limits.MaxTableCells,
                $"A table has more cells than ResourceLimits.MaxTableCells allows ({_context.Limits.MaxTableCells}).");
        }
    }

    private void OpenTables(Story story, int level)
    {
        _ = NestingLevel(new RtfParagraphState { InTable = true, NestingLevel = level });
        while (story.Tables.Count < level)
        {
            story.Tables.Add(new TableBuilder());
        }
    }

    /// <summary>
    /// Ends the tables nested deeper than a level, innermost first: each goes into the cell of the table around it (or
    /// the story). A row that was not ended is ended; table paragraphs that never made a row stay paragraphs.
    /// </summary>
    private void CloseTables(Story story, int level)
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
    /// Makes a table from its rows: the grid from the cell edges of all rows (<c>\trleft</c>, <c>\cellx</c>), cells
    /// spanning the grid columns between their edges, merged cells (<c>\clmrg</c>, <c>\clvmrg</c>) joined, borders,
    /// shading, padding and alignment from the definitions.
    /// </summary>
    private WordTable BuildTable(TableBuilder builder)
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

        var first = rows.Count > 0 ? rows[0].Definition : null;
        if (first is not null)
        {
            table.Alignment = first.Alignment;
            table.RightToLeft = first.RightToLeft;
            table.Borders = first.Borders;
            table.InsideHorizontal = first.InsideHorizontal;
            table.InsideVertical = first.InsideVertical;
            table.Padding = new CellPadding(
                (first.PaddingTop ?? 0) / 20.0,
                (first.PaddingRight ?? first.Gap) / 20.0,
                (first.PaddingBottom ?? 0) / 20.0,
                (first.PaddingLeft ?? first.Gap) / 20.0);
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
                CantSplit = definition?.Keep ?? false,
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
                if (cellDefinition is { Merged: true } && wordRow.Cells.Count > 0)
                {
                    var previous = wordRow.Cells[^1];
                    previous.ColumnSpan = Math.Max(previous.ColumnSpan, end - previous.Column);
                    continue;
                }

                if (cellDefinition is { VerticalMerged: true } && vertical.TryGetValue(column, out var above))
                {
                    above.RowSpan++;
                    continue;
                }

                var cell = new WordCell
                {
                    Column = column,
                    ColumnSpan = Math.Max(1, end - column),
                    Borders = cellDefinition?.Borders ?? default,
                    Shading = cellDefinition is null ? null : Shading(cellDefinition.BackgroundColor, cellDefinition.PatternColor, cellDefinition.Shading),
                    VerticalAlignment = cellDefinition?.VerticalAlignment ?? CellVerticalAlignment.Top,
                };
                for (var c = column; c < column + cell.ColumnSpan; c++)
                {
                    vertical.Remove(c);
                }

                if (cellDefinition is { VerticalMergeFirst: true })
                {
                    vertical[column] = cell;
                }

                cell.Blocks.AddRange(cells[i]);
                if (cell.Blocks.Count == 0)
                {
                    cell.Blocks.Add(new WordParagraph { MarkFormat = Format(_defaultChars) });
                }

                wordRow.Cells.Add(cell);
            }

            table.Rows.Add(wordRow);
        }

        return table;
    }
}
