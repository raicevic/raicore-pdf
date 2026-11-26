namespace RaiCore.Pdf.Layout;

/// <summary>
/// Multi-column layout (<see cref="ContainerBlock.Columns"/>, CSS multi-column layout). The content of a set of columns
/// is laid out by a paginator of its own whose pages are the columns: column <c>k</c> is the <c>k</c>-th page, as tall as
/// the room the column has, so breaking into the next column is a page break there (with its orphans, widows, avoided
/// breaks, repeated table headers and truncated margins). The columns then go side by side into this paginator's pages:
/// with <c>n</c> columns, columns <c>0..n-1</c> on the current page below the current position, the next <c>n</c> at the top
/// of the next page, and so on. Balancing searches the shortest height of the last row of columns that keeps the content
/// in them.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>How close balancing gets to the shortest column height, in points.</summary>
    private const double BalanceTolerance = 0.01;

    /// <summary>
    /// How deep containers with columns are laid out in columns. Balancing lays the content out a number of times, and
    /// each time the columns inside it balance again: deeper containers lay their children out in one column.
    /// </summary>
    private const int MaxColumnNesting = 2;

    /// <summary>How many sets of columns the layout on this thread is inside.</summary>
    [ThreadStatic]
    private static int _columnNesting;

    /// <summary>Whether a container lays its children out in columns here (see <see cref="MaxColumnNesting"/>).</summary>
    private static bool InColumns(ContainerBlock container) => container.Columns is not null && _columnNesting < MaxColumnNesting;

    /// <summary>For a paginator laying out a set of columns: where its pages (the columns) are; <c>null</c> otherwise.</summary>
    private readonly ColumnFlow? _columns;

    /// <summary>
    /// The pages of a paginator that lays out a set of columns: column <c>k</c>'s width and height, and the page of the
    /// paginator the columns go into that it is on (for diagnostics and the page limit). <paramref name="fullHeight"/> is
    /// the room the column has before balancing makes it shorter, which images are scaled down to fit (as on a page).
    /// </summary>
    private sealed class ColumnFlow(double x, Func<int, double> width, Func<int, double> height, Func<int, double> fullHeight, Func<int, int> outerPage, bool uneven, int perRow)
    {
        /// <summary>The number of columns on a page when the columns go on over pages (a page break skips to the next page's first); 0 when they do not.</summary>
        public int PerRow => perRow;

        /// <summary>The columns are not all as wide: paragraphs continuing in another column take its width.</summary>
        public bool Uneven => uneven;

        public double FullHeight(int column) => fullHeight(column);

        /// <summary>
        /// The columns where content was placed although it did not fit or broke where it should not (a line taller than
        /// the column, orphans and widows that could not be kept, a block that avoids breaks taller than the column).
        /// </summary>
        public HashSet<int> Cramped { get; } = [];

        /// <summary>How far down each column its content reaches (from the column's top).</summary>
        public List<double> Ends { get; } = [];

        public int OuterPage(int column) => outerPage(column);

        /// <summary>A column as a page: as wide as the column, starting at the first column's x, its content area at its top.</summary>
        public PageSetup Setup(int column) =>
            new(new PdfPageSize(x + width(column) + 1, Math.Max(height(column), MinimumContentWidth)), new Thickness(0, 1, 0, x));

        public void RecordEnd(int column, double y)
        {
            while (Ends.Count <= column)
            {
                Ends.Add(0);
            }

            Ends[column] = Math.Max(Ends[column], y);
        }
    }

    /// <summary>The geometry of a set of columns, and the layout of its content in them.</summary>
    /// <param name="Content">The paginator the content was laid out by (its pages are the columns).</param>
    /// <param name="PerRow">The number of columns in a row (one row per page).</param>
    /// <param name="Fragmenting">The rows continue on the next pages; otherwise there is one row, as long as the content needs.</param>
    /// <param name="Last">The last column with content.</param>
    private sealed record ColumnSet(Paginator Content, int PerRow, bool Fragmenting, int Last)
    {
        /// <summary>What the layout reported (each try of balancing reports on its own; only the chosen one's count).</summary>
        public DiagnosticSink? Diagnostics { get; init; }

        public int Row(int column) => Fragmenting ? column / PerRow : 0;

        public int Index(int column) => Fragmenting ? column % PerRow : column;

        /// <summary>How far down a column its content reaches.</summary>
        public double End(int column) =>
            column == Content.PageIndex ? Math.Max(Content.Y, Content._columns!.Ends.ElementAtOrDefault(column)) : Content._columns!.Ends.ElementAtOrDefault(column);

        /// <summary>
        /// How many columns of a row the content does not fit in: it reaches below their bottom (a line or an image taller
        /// than the column) or breaks in them where it should not (see <see cref="ColumnFlow.Cramped"/>).
        /// </summary>
        public int Overfilled(int row)
        {
            var count = 0;
            for (var column = 0; column <= Last; column++)
            {
                if (Row(column) == row && (End(column) > Content._frames[column].Setup.ContentHeight + BalanceTolerance || Content._columns!.Cramped.Contains(column)))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>How far down the columns of a row their content reaches (the tallest).</summary>
        public double RowEnd(int row)
        {
            var end = 0.0;
            for (var column = 0; column <= Last; column++)
            {
                if (Row(column) == row)
                {
                    end = Math.Max(end, End(column));
                }
            }

            return end;
        }
    }

    /// <summary>Creates the paginator for a set of columns: no page names or sides, and the width follows each column's.</summary>
    private Paginator(ColumnFlow columns)
        : this(columns.Setup(0), columns: columns)
    {
        _blockFlow = false;
    }

    /// <summary>
    /// The page of the document a page of this paginator is on: itself, the page a static layout (a table cell, a flex
    /// item) is on, or the page the column is on.
    /// </summary>
    private int DocumentPage(int page) => _static ? _diagnostics?.Page ?? 0 : _columns?.OuterPage(page) ?? page;

    /// <summary>Whether a block avoids breaks inside it here: <see cref="PageBreakInside.AvoidColumn"/> only between columns.</summary>
    private bool AvoidsBreakInside(BoxStyle style) =>
        style.BreakInside == PageBreakInside.Avoid || (style.BreakInside == PageBreakInside.AvoidColumn && _columns is not null);

    /// <summary>
    /// Lays out the children of a container with columns in its content box: runs of children in sets of columns, and
    /// children that span the columns (<see cref="BoxStyle.SpanColumns"/>) across them, between the sets.
    /// </summary>
    private void LayoutColumns(ContainerBlock container, IReadOnlyList<Block> children, double x, double width, BoxSizing sizing)
    {
        var columns = container.Columns!;
        var limit = ColumnHeightLimit(container, sizing);
        var pending = default(CollapsedMargin);
        var run = new List<Block>();
        var spans = children.Any(c => c.Style.SpanColumns);

        void Flush(bool beforeSpan)
        {
            if (run.Count == 0)
            {
                return;
            }

            if (!(AtPageTop && TruncateMargins))
            {
                Y += pending.Value;
            }

            pending = default;
            PlaceColumnSet(container, [.. run], x, width, columns, spans ? null : limit, beforeSpan);
            run.Clear();
        }

        foreach (var child in children)
        {
            if (child.Style.SpanColumns && !IsOutOfFlow(child.Style) && child.Style.Float == FloatSide.None)
            {
                Flush(beforeSpan: true);
                LayoutChildren([child], x, width, ref pending, firstTopMarginApplied: false);
            }
            else
            {
                run.Add(child);
            }
        }

        Flush(beforeSpan: false);
        Y = Math.Min(Y + pending.Value, Math.Max(Y, ContentBottom)); // A new formatting context: the last margin stays inside.
    }

    /// <summary>
    /// The most a container's columns may be tall: its content height when its height is fixed or limited (CSS
    /// <c>height</c>, <c>max-height</c>, a flex item's size); <c>null</c> when only the page limits them.
    /// </summary>
    private double? ColumnHeightLimit(Block block, BoxSizing sizing)
    {
        var (fixedHeight, _, maxHeight) = HeightLimits(block, sizing);
        var height = fixedHeight ?? (double.IsPositiveInfinity(maxHeight) ? null : maxHeight);
        var style = StyleOf(block);
        return height is { } h ? Math.Max(0, h - style.Border.Widths.Fixed.Vertical - style.Padding.Fixed.Vertical) : null;
    }

    /// <summary>
    /// Lays out blocks in a set of columns at the current position and places the columns on the pages: the rows of
    /// columns down the pages, the column rules between columns with content. Leaves the position below the last row.
    /// </summary>
    private void PlaceColumnSet(ContainerBlock container, IReadOnlyList<Block> blocks, double x, double width, ColumnLayout columns, double? limit, bool beforeSpan)
    {
        var geometry = columns.Resolve(width);
        var startPage = PageIndex;
        var startY = Y;
        var set = LayoutColumnSet(blocks, x, geometry, columns.Fill, limit, beforeSpan, fragmenting: !_static && limit is null, Remaining);
        var offsets = ColumnOffsets(geometry, set.Fragmenting ? geometry.Count : set.Last + 1);

        // The rows of columns: the first one here, the others at the top of the next pages.
        var tops = new List<double>();
        for (var row = 0; row <= set.Row(set.Last); row++)
        {
            if (row > 0)
            {
                NewPage();
            }

            tops.Add(row == 0 ? startY : PageTop(PageIndex));
            var rowTop = tops[row];
            var lastRow = row == set.Row(set.Last);
            // A full row is as tall as its room; the last as its content, or the fixed height its columns are filled to.
            var rowHeight = lastRow ? set.RowEnd(row) : set.Content._frames[row * set.PerRow].Setup.ContentHeight;
            if (limit is { } fixedHeight && columns.Fill == ColumnFill.Auto && !beforeSpan)
            {
                rowHeight = Math.Max(rowHeight, fixedHeight);
            }

            // The rules go between columns that both have content, under the content.
            if (columns.Rule.Width > 0 && columns.Rule.Style is not BorderStyle.Hidden && rowHeight > 0)
            {
                for (var column = 0; column < set.Last; column++)
                {
                    if (set.Row(column) == row && set.Row(column + 1) == row)
                    {
                        var index = set.Index(column);
                        var gap = geometry[Math.Min(index, geometry.Count - 1)];
                        var ruleX = x + offsets[index] + gap.Width + (gap.Gap / 2);
                        var rule = columns.Rule;
                        _pages[PageIndex].Add((canvas, pageHeight) => LayoutTagging.Decoration(canvas, () => BorderPainter.Paint(canvas,
                            [new BorderPainter.Edge(BorderPainter.Side.Left, rule, ruleX - (rule.Width / 2), pageHeight - rowTop - rowHeight, 0, 1, rowHeight, 0, 0, true, true)])));
                    }
                }
            }

            for (var column = 0; column <= set.Last; column++)
            {
                if (set.Row(column) != row)
                {
                    continue;
                }

                var dx = offsets[set.Index(column)];
                var content = set.Content._pages[column].Grouped();
                if (content.Count > 0)
                {
                    _pages[PageIndex].Add((canvas, pageHeight) =>
                    {
                        canvas.SaveState().Transform(1, 0, 0, 1, dx, 0);
                        foreach (var operation in content)
                        {
                            operation(canvas, pageHeight - rowTop);
                        }

                        canvas.RestoreState();
                    });
                }
            }
        }

        // Where the content went, on this paginator's pages.
        var inner = set.Content;
        (int Page, double Top, double Dx) Where(int column) => (startPage + set.Row(column), tops[set.Row(column)], offsets[set.Index(column)]);
        foreach (var placement in inner._placements)
        {
            var (page, top, dx) = Where(placement.Page);
            _placements.Add(placement with { Page = page, Top = placement.Top + top, Bottom = placement.Bottom + top, Left = placement.Left + dx });
        }

        foreach (var anchor in inner._anchors)
        {
            var (page, top, dx) = Where(anchor.Page);
            _anchors.Add(anchor with { Page = page, Top = anchor.Top + top, Left = anchor.Left + dx });
        }

        if (!_static)
        {
            foreach (var (column, marker) in inner._markers)
            {
                _markers.Add((Where(column).Page, marker));
            }
        }

        Y = tops[^1] + set.RowEnd(set.Row(set.Last)); // A fixed height is the box's (see LayoutBox).
    }

    /// <summary>The x offsets of the first <paramref name="count"/> columns from the first one (columns past the geometry's repeat its last).</summary>
    private static double[] ColumnOffsets(IReadOnlyList<(double Width, double Gap)> geometry, int count)
    {
        var offsets = new double[Math.Max(count, geometry.Count)];
        for (var i = 1; i < offsets.Length; i++)
        {
            var previous = geometry[Math.Min(i - 1, geometry.Count - 1)];
            offsets[i] = offsets[i - 1] + previous.Width + previous.Gap;
        }

        return offsets;
    }

    /// <summary>The room a row of columns after the first has: the content height of its page (as this paginator will create it).</summary>
    private double ColumnHeight(int startPage, int row)
    {
        var page = startPage + row;
        if (page < _frames.Count)
        {
            return PageBottom(page) - PageTop(page);
        }

        // A page not created yet: the next pages keep the name and alternate sides (see NewPage).
        var last = _frames[^1].Context;
        var side = last.Side;
        for (var p = _frames.Count; p <= page; p++)
        {
            side = Opposite(side);
        }

        var setup = Resolve(new PageContext(page + 1, _frames[PageIndex].Context.Name, side, IsBlank: false));
        var bottom = setup.Size.Height - setup.UsedMargins.Bottom;
        foreach (var (fromPage, reserve) in _bottomReserves)
        {
            if (page >= fromPage)
            {
                bottom -= reserve;
            }
        }

        return bottom - setup.UsedMargins.Top;
    }

    /// <summary>
    /// Lays out blocks in columns and balances the last row when it should be: before a spanning block, with
    /// <see cref="ColumnFill.Balance"/>, or when nothing else limits the height (CSS <c>auto</c> in a continuous context).
    /// </summary>
    /// <param name="blocks">The content.</param>
    /// <param name="x">The first column's x.</param>
    /// <param name="geometry">The columns' widths and gaps.</param>
    /// <param name="fill">How the content is spread.</param>
    /// <param name="limit">The most the columns may be tall (a fixed height), or <c>null</c>.</param>
    /// <param name="beforeSpan">The set ends before a spanning block: it is balanced.</param>
    /// <param name="fragmenting">The rows continue on the next pages; otherwise there is one row, and the content that does not fit in it goes into more columns beside it.</param>
    /// <param name="firstHeight">The room the first row has.</param>
    /// <param name="report">Whether what the layout finds is reported (not when it is only measured).</param>
    private ColumnSet LayoutColumnSet(
        IReadOnlyList<Block> blocks, double x, IReadOnlyList<(double Width, double Gap)> geometry, ColumnFill fill, double? limit, bool beforeSpan, bool fragmenting, double firstHeight,
        bool report = true)
    {
        var n = geometry.Count;
        var startPage = PageIndex;
        var outer = this;
        double Width(int column) => geometry[Math.Min(fragmenting ? column % n : column, n - 1)].Width;
        double FullHeight(int column) => fragmenting
            ? (column < n ? firstHeight : outer.ColumnHeight(startPage, column / n))
            : limit ?? StaticHeight;
        int OuterPage(int column) => DocumentPage(startPage + (fragmenting ? column / n : 0));

        ColumnSet Run(Func<int, double> height)
        {
            // Diagnostics go to a sink of their own (static layouts inside report to the thread's sink, too).
            var outerSink = _diagnostics;
            var sink = outerSink is null ? null : new DiagnosticSink { Page = outerSink.Page };
            _diagnostics = sink;
            _columnNesting++;
            try
            {
                var paginator = new Paginator(new ColumnFlow(x, Width, height, FullHeight, OuterPage, geometry.Any(g => g.Width != geometry[0].Width), fragmenting ? n : 0));
                var pending = default(CollapsedMargin);
                paginator.InFloatRoot(() =>
                {
                    paginator.LayoutChildren(blocks, x, Width(0), ref pending, firstTopMarginApplied: false);
                    paginator.Y = Math.Min(paginator.Y + pending.Value, Math.Max(paginator.Y, paginator.ContentBottom));
                });
                return new ColumnSet(paginator, n, fragmenting, paginator.PageIndex) { Diagnostics = sink };
            }
            finally
            {
                _diagnostics = outerSink;
                _columnNesting--;
            }
        }

        var chosen = Choose();
        if (report && chosen.Diagnostics is { } reported && _diagnostics is { } target)
        {
            foreach (var item in reported.Items)
            {
                target.Add(item.Kind, item.Block, item.Page, () => item.Message);
            }
        }

        return chosen;

        ColumnSet Choose()
        {
            var set = Run(FullHeight);
            var balance = beforeSpan || fill == ColumnFill.Balance || (!fragmenting && limit is null);
            var lastRow = set.Row(set.Last);
            if (!balance || (!fragmenting && set.Last >= n))
            {
                return set; // Filled one after the other, or more columns than there are even at the full height.
            }

            // The shortest height of the last row's columns that keeps the content in that row: a binary search between no
            // height and how far the content reaches in the last row at the full height.
            var (low, high) = (0.0, set.RowEnd(lastRow));
            var best = set;
            var rows = lastRow + 1;
            var overfilled = set.Overfilled(lastRow); // What does not fit even at the full height.
            while (high - low > BalanceTolerance)
            {
                var middle = (low + high) / 2;
                var trial = Run(column => (fragmenting ? column / n : 0) == lastRow ? middle : FullHeight(column));
                if (trial.Last < rows * n && trial.Row(trial.Last) == lastRow && trial.Overfilled(lastRow) <= overfilled)
                {
                    (high, best) = (middle, trial);
                }
                else
                {
                    low = middle;
                }
            }

            return best;
        }
    }

    /// <summary>Height of the content of a container with columns, laid out without page breaks (balanced, as CSS does).</summary>
    private double MeasureColumns(ContainerBlock container, IReadOnlyList<Block> children, double width)
    {
        var columns = container.Columns!;
        var geometry = columns.Resolve(width);
        var limit = children.Any(c => c.Style.SpanColumns) ? null : ColumnHeightLimit(container, default);
        var height = 0.0;
        var margin = new CollapsedMargin();
        var run = new List<Block>();

        void Flush(bool beforeSpan)
        {
            if (run.Count > 0)
            {
                var set = LayoutColumnSet([.. run], 0, geometry, columns.Fill, limit, beforeSpan, fragmenting: false, firstHeight: limit ?? StaticHeight, report: false);
                height += margin.Value + set.RowEnd(0);
                margin = default;
                run.Clear();
            }
        }

        foreach (var child in children)
        {
            if (child.Style.SpanColumns && !IsOutOfFlow(child.Style) && child.Style.Float == FloatSide.None)
            {
                Flush(beforeSpan: true);
                var childStyle = ResolveStyle(child, width);
                margin.Add(childStyle.Margin.Fixed.Top);
                height += margin.Value + MeasureHeight(child, width);
                margin = default;
                margin.Add(childStyle.Margin.Fixed.Bottom);
            }
            else
            {
                run.Add(child);
            }
        }

        Flush(beforeSpan: false);
        return height + margin.Value;
    }

    /// <summary>
    /// The min-content and max-content widths of a container's columns from its content's (CSS multi-column layout
    /// section 3.5): the columns side by side at their content's widths; with a column width, the min-content width is
    /// at most one column's.
    /// </summary>
    private static (double Min, double Max) ColumnsIntrinsic(ColumnLayout columns, (double Min, double Max) content)
    {
        if (columns.Widths is { Count: > 0 } widths)
        {
            var total = widths.Sum(w => w.Width) + widths.Take(widths.Count - 1).Sum(w => w.Gap);
            return (total, total);
        }

        var count = Math.Min(columns.Count ?? 1, ColumnLayout.MaxColumns);
        var gaps = (count - 1) * columns.Gap;
        var columnMax = Math.Max(content.Max, columns.Width ?? 0);
        var max = (count * columnMax) + gaps;
        var min = columns.Width is { } width ? Math.Min(content.Min, width) : (count * content.Min) + gaps;
        return (Math.Min(min, max), max);
    }
}
