namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Inline boxes (CSS inline-block, inline-flex, inline-table): blocks laid out as one item in a line of text.</summary>
internal sealed partial class Paginator
{
    /// <summary>
    /// The border-box width an inline box gets: its fixed width, or shrink-to-fit (its preferred width, but no wider
    /// than the space available and no narrower than its minimum width).
    /// </summary>
    public static double InlineBoxWidth(Block block, double availableWidth)
    {
        var style = block.Style;
        if (style.Width is { } width)
        {
            return style.ClampWidth(width);
        }

        var (min, max) = IntrinsicContentBorderBox(block);
        return style.ClampWidth(Math.Min(max, Math.Max(min, availableWidth - style.Margin.Horizontal)));
    }

    /// <summary>The min-content and max-content widths an inline box takes on a line, margins included.</summary>
    public static (double Min, double Max) InlineBoxIntrinsic(Block block) => MeasureIntrinsic(block);

    /// <summary>
    /// Lays out an inline box's block at a border-box width without page breaks, as an independent formatting context.
    /// Returns its border-box height, the distance from its top to the baseline it sits on (CSS: its last line of
    /// text, the first baseline for flex containers, the first row's for tables, the bottom margin edge without
    /// text), and drawing operations
    /// with its top-left corner at x = 0 and y = the top passed to them.
    /// </summary>
    public static (double Height, double Baseline, IReadOnlyList<DrawOperation> Content) LayoutInlineBox(Block block, double borderWidth)
    {
        var setup = new PageSetup(new PdfPageSize(borderWidth + 1, StaticHeight), new Thickness(0, 1, 0, 0));
        var paginator = new Paginator(setup, isStatic: true);
        paginator.LayoutBlock(block, 0, borderWidth, new BoxSizing(Isolated: true));
        var height = paginator.Y;
        var baseline = block switch
        {
            FlexBlock flex => paginator.FirstBaseline(flex, borderWidth),
            TableBlock table => paginator.FirstRowBaseline(table, borderWidth),
            _ => paginator.LastLineBaseline(),
        };

        return (height, baseline ?? height + block.Style.Margin.Bottom, paginator._pages[0]);
    }

    /// <summary>
    /// A table's baseline (CSS: its first row's): the first baseline among the first row's cells, from the table's
    /// border-box top, or <c>null</c> if they have no text.
    /// </summary>
    private double? FirstRowBaseline(TableBlock table, double borderWidth)
    {
        if (table.Rows.Count == 0)
        {
            return null;
        }

        // With collapsed borders the table's own border is part of the grid lines (the cells' insets).
        var style = table.BorderCollapse == BorderCollapse.Collapse ? table.Style with { Border = Borders.None } : table.Style;
        var top = style.Border.Top.Width + style.Padding.Top;
        var contentWidth = Math.Max(MinimumContentWidth, borderWidth - style.Border.Widths.Horizontal - style.Padding.Horizontal);
        var layout = TableLayouter.Layout(table, 0, contentWidth);
        double? best = null;
        foreach (var cell in layout.Cells.Where(c => c.Grid.Row == 0 && c.Grid.Cell.Content.Count > 0))
        {
            var first = cell.Grid.Cell.Content[0];
            if (FirstBaseline(first, BoxWidth(first, cell.InnerWidth)) is { } baseline)
            {
                var candidate = top + layout.SpacingY + cell.InsetTop + first.Style.Margin.Top + baseline;
                best = best is null ? candidate : Math.Max(best.Value, candidate);
            }
        }

        return best;
    }

    /// <summary>The baseline of the last line of text laid out, from the top of the layout, or <c>null</c> if there is none.</summary>
    private double? LastLineBaseline()
    {
        for (var i = _placements.Count - 1; i >= 0; i--)
        {
            if (_placements[i] is { Block: ParagraphBlock paragraph, FirstLine: >= 0 } placement && placement.EndLine > placement.FirstLine)
            {
                var lines = paragraph.Paragraph.Layout(placement.Width).Lines;
                var last = lines[placement.EndLine - 1];
                return placement.Top + (last.Top - lines[placement.FirstLine].Top) + last.Baseline;
            }
        }

        return null;
    }
}
