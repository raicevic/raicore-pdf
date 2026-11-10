namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>A drawing operation on a page: receives the canvas and the page height (to convert from top-down y).</summary>
internal delegate void DrawOperation(PdfCanvas canvas, double pageHeight);

/// <summary>Where a piece of a block ended up (for tests and diagnostics). Y values are from the top of the page.</summary>
/// <param name="Page">0-based page index.</param>
/// <param name="Block">The block.</param>
/// <param name="Top">Top of the placed piece.</param>
/// <param name="Bottom">Bottom of the placed piece.</param>
/// <param name="FirstLine">For paragraphs: first line on this page; -1 for box fragments.</param>
/// <param name="EndLine">For paragraphs: the line after the last one on this page; -1 for box fragments.</param>
/// <param name="Left">Left edge of the placed piece (the border box for boxes, the content box for paragraphs).</param>
/// <param name="Width">Width of the placed piece.</param>
internal sealed record Placement(int Page, Block Block, double Top, double Bottom, int FirstLine, int EndLine, double Left = 0, double Width = 0);

/// <summary>The result of pagination: drawing operations per page, and where everything went.</summary>
internal sealed record PaginationResult(
    IReadOnlyList<IReadOnlyList<DrawOperation>> Pages, IReadOnlyList<Placement> Placements, IReadOnlyList<(int Page, object Marker)> Markers);

/// <summary>
/// Lays blocks out top to bottom and splits them across pages, following CSS block layout and fragmentation:
/// adjacent vertical margins collapse, margins are dropped at the top of a page after an automatic break,
/// paragraphs split between lines while honoring orphans and widows, <see cref="PageBreak.Avoid"/> after a block
/// keeps it with the start of the next one, and boxes split across pages are sliced (top border and padding on
/// the first fragment, bottom ones on the last).
/// </summary>
/// <remarks>All y values here are distances from the top of the page.</remarks>
internal sealed partial class Paginator
{
    private const double Epsilon = 1e-6;

    /// <summary>Content narrower than this is laid out at this width (it overflows, as in CSS) instead of failing.</summary>
    private const double MinimumContentWidth = 1;

    /// <summary>Height of the virtual page used to lay out content without page breaks (e.g. inside table cells).</summary>
    private const double StaticHeight = 1e7;

    private readonly PageSetup _setup;
    private readonly bool _static;
    private readonly List<List<DrawOperation>> _pages = [];
    private readonly List<Placement> _placements = [];
    private readonly List<(int Page, object Marker)> _markers = [];
    private int _pageIndex = -1;

    /// <summary>Where content starts on pages that begin with repeated content (a table's header rows).</summary>
    private readonly Dictionary<int, double> _pageTops = [];

    /// <summary>Called on every page created while they are registered, e.g. to repeat a table's header rows.</summary>
    private readonly List<Action> _pageStartHooks = [];

    private Paginator(PageSetup setup, bool isStatic = false)
    {
        _setup = setup;
        _static = isStatic;
        NewPage(forced: true); // The first page keeps top margins, like the start of a CSS document.
    }

    private double ContentTop => _setup.Margins.Top;

    private double ContentBottom => _setup.Size.Height - _setup.Margins.Bottom;

    /// <summary>The current page. Flex items laid out side by side revisit pages, so this is not always the last one.</summary>
    private int PageIndex => _pageIndex;

    private double Y { get; set; }

    /// <summary>Nothing has been placed on the current page yet.</summary>
    private bool AtPageTop => Y <= PageTop(PageIndex) + Epsilon;

    /// <summary>Where a page's flowing content starts: below repeated table headers, if any.</summary>
    private double PageTop(int page) => _pageTops.TryGetValue(page, out var top) ? top : ContentTop;

    /// <summary>The current page started after an automatic break, so margins at its top are truncated.</summary>
    private bool TruncateMargins { get; set; }

    private double Remaining => ContentBottom - Y;

    public static PaginationResult Paginate(FlowDocument document)
    {
        var paginator = new Paginator(document.Setup);
        var pendingMargin = new CollapsedMargin();
        paginator.LayoutChildren(document.Blocks, document.Setup.Margins.Left, document.Setup.ContentWidth, ref pendingMargin, firstTopMarginApplied: false);
        return new PaginationResult(paginator._pages, paginator._placements, paginator._markers);
    }

    /// <summary>
    /// Lays out blocks in one unbroken column (no pages), e.g. the content of a table cell. Returns the height and
    /// drawing operations; they draw with y = 0 at the top when called with a page height of "distance from the top of
    /// the column to the bottom of the page".
    /// </summary>
    public static (double Height, IReadOnlyList<DrawOperation> Content) LayoutStatic(IReadOnlyList<Block> blocks, double x, double width)
    {
        var setup = new PageSetup(new PdfPageSize(x + width + 1, StaticHeight), new Thickness(0, 1, 0, x));
        var paginator = new Paginator(setup, isStatic: true);
        var pending = default(CollapsedMargin);
        paginator.LayoutChildren(blocks, x, width, ref pending, firstTopMarginApplied: false);
        return (paginator.Y + pending.Value, paginator._pages[0]);
    }

    /// <summary>A block's min-content and max-content widths, including its margins (CSS intrinsic sizes).</summary>
    public static (double Min, double Max) MeasureIntrinsic(Block block)
    {
        var style = block.Style;
        var (min, max) = IntrinsicBorderBox(block);
        return (min + style.Margin.Horizontal, max + style.Margin.Horizontal);
    }

    private static (double Min, double Max) IntrinsicBorderBox(Block block) =>
        block.Style.Width is { } fixedWidth ? (fixedWidth, fixedWidth) : IntrinsicContentBorderBox(block);

    /// <summary>The border-box min-content and max-content widths from the content, ignoring a fixed width.</summary>
    private static (double Min, double Max) IntrinsicContentBorderBox(Block block)
    {
        var style = block.Style;
        var decoration = style.Border.Widths.Horizontal + style.Padding.Horizontal;
        var (min, max) = block switch
        {
            ParagraphBlock paragraph => paragraph.Paragraph.MeasureIntrinsicWidths(),
            ImageBlock image => (image.GetSize(double.MaxValue, double.MaxValue).Width, image.GetSize(double.MaxValue, double.MaxValue).Width),
            ContainerBlock container => container.Children.Select(MeasureIntrinsic).Aggregate((Min: 0.0, Max: 0.0), (a, b) => (Math.Max(a.Min, b.Min), Math.Max(a.Max, b.Max))),
            TableBlock table => TableLayouter.MeasureIntrinsic(table),
            FlexBlock flex => FlexIntrinsic(flex),
            _ => (0, 0),
        };
        return (min + decoration, max + decoration);
    }

    /// <summary>
    /// The border-box width a block gets: its fixed width, or the available width; images and tables without a
    /// width shrink to fit their content (CSS shrink-to-fit).
    /// </summary>
    private double BoxWidth(Block block, double width)
    {
        var style = block.Style;
        var available = Math.Max(0, width - style.Margin.Horizontal);
        if (style.Width is { } fixedWidth)
        {
            return fixedWidth;
        }

        switch (block)
        {
            case ImageBlock image:
                var decoration = style.Border.Widths.Horizontal + style.Padding.Horizontal;
                return Math.Min(available, image.GetSize(Math.Max(1e-3, available - decoration), ImageMaxHeight(image)).Width + decoration);
            case TableBlock:
                var (min, max) = IntrinsicBorderBox(block);
                return Math.Max(min, Math.Min(available, max));
            default:
                return available;
        }
    }

    /// <summary>Moves to the next page, creating it unless a side-by-side flex item already did.</summary>
    private void NewPage(bool forced = false)
    {
        _pageIndex++;
        var created = _pageIndex == _pages.Count;
        if (created)
        {
            _pages.Add([]);
        }

        Y = ContentTop;
        TruncateMargins = !forced;
        if (created && _pageStartHooks.Count > 0)
        {
            foreach (var hook in _pageStartHooks.ToList())
            {
                hook();
            }

            _pageTops[_pageIndex] = Y;
        }
        else
        {
            Y = PageTop(_pageIndex);
        }
    }

    /// <summary>
    /// Lays out sibling blocks. <paramref name="pending"/> carries the collapsed margin above the next block in and
    /// the margin below the last block out (so it can collapse through a parent without bottom border or padding).
    /// </summary>
    private void LayoutChildren(IReadOnlyList<Block> blocks, double x, double width, ref CollapsedMargin pending, bool firstTopMarginApplied)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
            var style = block.Style;

            // An empty block without border or padding takes no space: its margins collapse through it (CSS 2.1
            // section 8.3.1), so it only records its marker where the next content will start.
            if (block is ContainerBlock { Children.Count: 0 } && !HasTopEdge(style) && !HasBottomEdge(style) && style.Background is null
                && style.Height is not > 0 && style.MinHeight is not > 0
                && style.BreakBefore != PageBreak.Page && style.BreakAfter != PageBreak.Page)
            {
                pending.Add(style.Margin.Top);
                pending.Add(style.Margin.Bottom);
                if (block.Marker is { } emptyMarker && !_static)
                {
                    _markers.Add((PageIndex, emptyMarker));
                }

                continue;
            }

            // Forced break before this block, or after the previous one.
            var forced = style.BreakBefore == PageBreak.Page || (i > 0 && blocks[i - 1].Style.BreakAfter == PageBreak.Page);
            if (forced && !_static && !(AtPageTop && _pages[PageIndex].Count == 0))
            {
                NewPage(forced: true);
                pending = default; // The previous block's bottom margin ends with its page; this block keeps its top margin.
            }

            if (!(i == 0 && firstTopMarginApplied))
            {
                pending.Add(style.Margin.Top);
                pending.Add(CollapseThroughTop(block)); // First descendants' top margins collapse with this one.
            }

            // Move to a new page when the start of the block (and, for break-after: avoid, the start of the
            // next block) would not fit here but would fit at the top of a fresh page.
            var needed = MinimumStartHeight(block, width);
            if (style.BreakInside == PageBreakInside.Avoid)
            {
                needed = Math.Max(needed, MeasureHeight(block, width));
            }

            if (style.BreakAfter == PageBreak.Avoid && next is not null)
            {
                needed = Math.Max(needed, MeasureHeight(block, width) + Math.Max(style.Margin.Bottom, next.Style.Margin.Top) + MinimumStartHeight(next, width));
            }

            if (!AtPageTop && needed + pending.Value > Remaining + Epsilon && needed <= _setup.ContentHeight + Epsilon)
            {
                NewPage();
            }

            if (!(AtPageTop && TruncateMargins))
            {
                Y += pending.Value;
            }

            pending = default;

            var boxX = x + style.Margin.Left;
            var boxWidth = BoxWidth(block, width);
            var alignment = block switch
            {
                ImageBlock { Style.Width: null } image => image.Alignment,
                TableBlock table => table.Alignment,
                _ => TextAlignment.Left,
            };
            if (alignment != TextAlignment.Left)
            {
                // Images and tables shrink to their content; the alignment then positions the whole box.
                var free = Math.Max(0, width - style.Margin.Horizontal - boxWidth);
                boxX += alignment switch
                {
                    TextAlignment.Center => free / 2,
                    TextAlignment.Right => free,
                    _ => 0,
                };
            }

            if (block.Marker is { } marker && !_static)
            {
                _markers.Add((PageIndex, marker));
            }

            var trailing = LayoutBlock(block, boxX, boxWidth);

            pending = trailing;
            pending.Add(style.Margin.Bottom);
        }
    }

    /// <summary>
    /// The top margins of a container's first descendants that collapse with its own (CSS 2.1 section 8.3.1):
    /// only when the container has no top border or padding.
    /// </summary>
    private static double CollapseThroughTop(Block block)
    {
        if (block is not ContainerBlock { Children.Count: > 0 } container || HasTopEdge(block.Style))
        {
            return 0;
        }

        var first = container.Children[0];
        var margin = new CollapsedMargin();
        margin.Add(first.Style.Margin.Top);
        margin.Add(CollapseThroughTop(first));
        return margin.Value;
    }

    private static bool HasTopEdge(BoxStyle style) => style.Border.Top.Width > 0 || style.Padding.Top > 0;

    private static bool HasBottomEdge(BoxStyle style) => style.Border.Bottom.Width > 0 || style.Padding.Bottom > 0;

    /// <summary>Lays out one block; returns the bottom margin that collapses out of it (from its last child).</summary>
    private CollapsedMargin LayoutBlock(Block block, double x, double width, BoxSizing sizing = default)
    {
        var trailing = default(CollapsedMargin);
        switch (block)
        {
            case SpacerBlock spacer:
                if (!(AtPageTop && TruncateMargins))
                {
                    Y = Math.Min(Y + spacer.Height, ContentBottom);
                }

                break;
            case ParagraphBlock paragraph:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutParagraph(paragraph, contentX, contentWidth), sizing);
                break;
            case ImageBlock image:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutImage(image, contentX, contentWidth), sizing);
                break;
            case TableBlock table:
                // With collapsed borders, the table's border takes part in the grid lines instead of framing the table.
                var tableStyle = table.BorderCollapse == BorderCollapse.Collapse ? table.Style with { Border = Borders.None } : table.Style;
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutTable(table, contentX, contentWidth), sizing, tableStyle);
                break;
            case FlexBlock flex:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutFlex(flex, contentX, contentWidth, sizing.DefiniteHeight), sizing);
                break;
            case ContainerBlock container:
                // A flex item is an independent formatting context: its children's margins stay inside it.
                var isolated = sizing.Isolated;
                LayoutBox(block, x, width, (contentX, contentWidth) =>
                {
                    var pending = default(CollapsedMargin);
                    LayoutChildren(container.Children, contentX, contentWidth, ref pending, firstTopMarginApplied: !HasTopEdge(block.Style) && !isolated);
                    if (HasBottomEdge(block.Style) || isolated)
                    {
                        Y = Math.Min(Y + pending.Value, Math.Max(Y, ContentBottom)); // Stays inside the box.
                    }
                    else
                    {
                        trailing = pending; // Collapses through the container's bottom.
                    }
                }, sizing);
                break;
            default:
                throw new NotSupportedException($"Unknown block type {block.GetType().Name}.");
        }

        return trailing;
    }

    /// <summary>
    /// Lays out a box's content between its top and bottom border and padding, then draws the background and
    /// borders of each page fragment underneath the content.
    /// </summary>
    private void LayoutBox(Block block, double x, double width, Action<double, double> layoutContent, BoxSizing sizing = default, BoxStyle? styleOverride = null)
    {
        var style = styleOverride ?? block.Style;
        var border = style.Border.Widths;
        var decorated = style.Background is not null || border.Vertical > 0 || border.Horizontal > 0;

        var startPage = PageIndex;
        var startY = Y;
        var insertAt = _pages[startPage].Count;

        Y += border.Top + style.Padding.Top;
        layoutContent(x + border.Left + style.Padding.Left, Math.Max(MinimumContentWidth, width - border.Horizontal - style.Padding.Horizontal));
        Y += style.Padding.Bottom + border.Bottom;

        // CSS height, min-height and max-height; a flex item's size from its container overrides height.
        var (fixedHeight, minHeight, maxHeight) = HeightLimits(block, sizing);
        if (PageIndex == startPage)
        {
            Y = startY + Math.Max(Math.Min(fixedHeight ?? (Y - startY), maxHeight), minHeight);
        }
        else
        {
            // A box split across pages: its fragments add up to the height; only growing it is possible here.
            var total = ContentBottom - startY + ((PageIndex - startPage - 1) * _setup.ContentHeight) + (Y - PageTop(PageIndex));
            var target = Math.Max(Math.Min(fixedHeight ?? total, maxHeight), minHeight);
            if (target > total)
            {
                Y = Math.Min(ContentBottom, Y + target - total);
            }
        }

        if (sizing.StretchTo is { } line)
        {
            if (decorated)
            {
                line.Pending.Add(() => Decorate(line.Page, line.Y - sizing.StretchInset));
            }

            return;
        }

        if (decorated)
        {
            Decorate(PageIndex, Y);
        }

        void Decorate(int endPage, double endY)
        {
            for (var page = startPage; page <= endPage; page++)
            {
                var first = page == startPage;
                var last = page == endPage;
                var top = first ? startY : PageTop(page);
                var bottom = last ? endY : ContentBottom;
                var index = first ? insertAt : 0; // Pages created inside the box: under everything on them.
                _pages[page].Insert(index, (canvas, pageHeight) => DrawBox(canvas, pageHeight, style, x, top, width, bottom - top, first, last));
                _placements.Add(new Placement(page, block, top, bottom, -1, -1, x, width));
            }
        }
    }

    /// <summary>
    /// A block's height constraints: a fixed height (from a flex container, or CSS height), a minimum and a maximum.
    /// Images ignore them (they size themselves); for tables a height is a minimum, as in CSS.
    /// </summary>
    private static (double? Fixed, double Min, double Max) HeightLimits(Block block, BoxSizing sizing = default)
    {
        var style = block.Style;
        if (block is ImageBlock or SpacerBlock)
        {
            return (sizing.Height, sizing.MinHeight ?? 0, double.PositiveInfinity);
        }

        var isTable = block is TableBlock;
        var fixedHeight = sizing.Height ?? (isTable ? null : style.Height);
        var min = Math.Max(sizing.MinHeight ?? 0, Math.Max(style.MinHeight ?? 0, isTable ? style.Height ?? 0 : 0));
        var max = sizing.Height is null ? style.MaxHeight ?? double.PositiveInfinity : double.PositiveInfinity;
        return (fixedHeight, min, max);
    }

    /// <summary>A block's border-box height from its natural (content) height and its height constraints; min wins over max.</summary>
    private static double ApplyHeights(Block block, double natural)
    {
        var (fixedHeight, min, max) = HeightLimits(block);
        return Math.Max(Math.Min(fixedHeight ?? natural, max), min);
    }

    private static void DrawBox(PdfCanvas canvas, double pageHeight, BoxStyle style, double x, double top, double width, double height, bool first, bool last)
    {
        var y = pageHeight - top - height; // PDF y of the bottom edge.
        if (style.Background is { } background)
        {
            canvas.SetFillColor(background).Rectangle(x, y, width, height).Fill();
        }

        var b = style.Border;
        void Side(BorderSide side, double sx, double sy, double sw, double sh)
        {
            if (side.Width > 0)
            {
                canvas.SetFillColor(side.Color).Rectangle(sx, sy, sw, sh).Fill();
            }
        }

        if (first)
        {
            Side(b.Top, x, y + height - b.Top.Width, width, b.Top.Width);
        }

        if (last)
        {
            Side(b.Bottom, x, y, width, b.Bottom.Width);
        }

        Side(b.Left, x, y, b.Left.Width, height);
        Side(b.Right, x + width - b.Right.Width, y, b.Right.Width, height);
    }

    private void LayoutImage(ImageBlock block, double x, double width)
    {
        var (w, h) = block.GetSize(width, ImageMaxHeight(block));
        if (h > Remaining + Epsilon && !AtPageTop)
        {
            NewPage(); // Normally avoided by MinimumStartHeight; happens only inside a tall box.
        }

        var left = x + block.Alignment switch
        {
            TextAlignment.Center => Math.Max(0, (width - w) / 2),
            TextAlignment.Right => Math.Max(0, width - w),
            _ => 0,
        };
        var top = Y;
        _pages[PageIndex].Add((canvas, pageHeight) => canvas.DrawImage(block.Image, left, pageHeight - top - h, w, h));
        Y += h;
        _placements.Add(new Placement(PageIndex, block, top, Y, -1, -1, left, w));
    }

    /// <summary>An image is scaled down to fit on one page together with its own border and padding.</summary>
    private double ImageMaxHeight(Block block) =>
        Math.Max(1, _setup.ContentHeight - block.Style.Border.Widths.Vertical - block.Style.Padding.Vertical);

    private void LayoutParagraph(ParagraphBlock block, double x, double width)
    {
        var layout = block.Paragraph.Layout(width);
        var lines = layout.Lines;
        var orphans = Math.Max(1, block.Style.Orphans);
        var widows = Math.Max(1, block.Style.Widows);
        var i = 0;
        while (i < lines.Count)
        {
            var fit = CountFittingLines(lines, i, Remaining);
            var rest = lines.Count - i;
            if (fit >= rest)
            {
                Place(block, layout, x, i, lines.Count);
                return;
            }

            // Respect orphans (lines left before the break) and widows (lines carried after it).
            var take = fit;
            if (rest - take < widows)
            {
                take = rest - widows;
            }

            var minimum = i == 0 ? Math.Min(orphans, rest) : 1;
            if (take < minimum || take <= 0)
            {
                if (!AtPageTop)
                {
                    NewPage();
                    continue;
                }

                // Even an empty page cannot satisfy the constraints: place what fits (at least one line).
                take = Math.Max(1, Math.Min(fit, rest));
            }

            Place(block, layout, x, i, i + take);
            i += take;
            if (i < lines.Count)
            {
                NewPage();
            }
        }
    }

    private void Place(ParagraphBlock block, ParagraphLayout layout, double x, int first, int end)
    {
        var top = Y;
        _pages[PageIndex].Add((canvas, pageHeight) => layout.Draw(canvas, x, pageHeight - top, first, end));
        Y += layout.Lines[end - 1].Top + layout.Lines[end - 1].Height - layout.Lines[first].Top;
        _placements.Add(new Placement(PageIndex, block, top, Y, first, end, x, layout.AvailableWidth));
    }

    private static int CountFittingLines(IReadOnlyList<LineLayout> lines, int start, double available)
    {
        var used = 0.0;
        var count = 0;
        for (var i = start; i < lines.Count; i++)
        {
            used += lines[i].Height;
            if (used > available + Epsilon)
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// Places a table's rows: rows joined by row spans stay together, a group that does not fit moves to the next page,
    /// and header rows are repeated at the top of every page the table continues on.
    /// </summary>
    private void LayoutTable(TableBlock table, double x, double width)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        var layout = TableLayouter.Layout(table, x, width);
        var headerEnd = layout.HeaderRows;

        void Place(int start, int end)
        {
            var top = Y;
            _pages[PageIndex].Add((canvas, pageHeight) => DrawRows(canvas, pageHeight, layout, x, top, start, end));
            Y += layout.Height(start, end);
            _placements.Add(new Placement(PageIndex, table, top, Y, start, end, x, width));
            Y += layout.SpacingY;
        }

        // The table's spacing and header rows start it, and every page it continues on (also pages that a split row's
        // cells create).
        void StartPage()
        {
            Y += layout.SpacingY;
            if (headerEnd > 0)
            {
                Place(0, headerEnd);
            }
        }

        StartPage();
        _pageStartHooks.Add(StartPage);
        try
        {
            foreach (var (start, end) in layout.Groups.Where(g => g.End > headerEnd))
            {
                var groupStart = Math.Max(start, headerEnd);
                var height = layout.Height(groupStart, end);
                if (Y + height <= ContentBottom + Epsilon)
                {
                    Place(groupStart, end);
                    continue;
                }

                // A row that does not fit is split here if the start of its cells fits, as in browsers.
                if (!_static && CanSplit(layout, groupStart, end) && MinimumRowStart(layout, groupStart) <= Remaining + Epsilon)
                {
                    SplitRow(layout, groupStart, x, width);
                    continue;
                }

                if (!AtPageTop)
                {
                    NewPage(); // Repeats the header rows.
                }

                // Taller than a whole page: split even a row that asked to stay whole. Rows joined by row spans overflow.
                if (Y + height <= ContentBottom + Epsilon || _static || !CanSplit(layout, groupStart, end, ignoreAvoid: true))
                {
                    Place(groupStart, end);
                }
                else
                {
                    SplitRow(layout, groupStart, x, width);
                }
            }
        }
        finally
        {
            _pageStartHooks.Remove(StartPage);
        }
    }

    private static void DrawRows(PdfCanvas canvas, double pageHeight, TableLayout layout, double x, double top, int start, int end)
    {
        var table = layout.Table;
        var collapse = table.BorderCollapse == BorderCollapse.Collapse;
        var rowTop = new double[end - start + 1];
        rowTop[0] = top;
        for (var r = start; r < end; r++)
        {
            rowTop[r - start + 1] = rowTop[r - start] + layout.RowHeights[r] + layout.SpacingY;
        }

        double TopOf(int row) => rowTop[row - start];
        var left = x + layout.ColumnX[0];
        var right = x + layout.ColumnX[^1] - layout.SpacingX;

        for (var r = start; r < end; r++)
        {
            if (table.Rows[r].Background is { } rowBackground)
            {
                canvas.SetFillColor(rowBackground).Rectangle(left, pageHeight - TopOf(r) - layout.RowHeights[r], right - left, layout.RowHeights[r]).Fill();
            }
        }

        foreach (var cell in layout.Cells.Where(c => c.Grid.Row >= start && c.Grid.Row < end))
        {
            var cellTop = TopOf(cell.Grid.Row);
            var cellHeight = layout.Height(cell.Grid.Row, cell.Grid.Row + cell.Grid.RowSpan);
            var style = cell.Grid.Cell.Style;
            if (collapse)
            {
                if (style.Background is { } background)
                {
                    canvas.SetFillColor(background).Rectangle(x + cell.X, pageHeight - cellTop - cellHeight, cell.Width, cellHeight).Fill();
                }
            }
            else
            {
                DrawBox(canvas, pageHeight, style, x + cell.X, cellTop, cell.Width, cellHeight, first: true, last: true);
            }

            var free = cellHeight - cell.InsetTop - cell.InsetBottom - cell.ContentHeight;
            var offset = cell.Grid.Cell.VerticalAlignment switch
            {
                VerticalAlignment.Middle => Math.Max(0, free / 2),
                VerticalAlignment.Bottom => Math.Max(0, free),
                _ => 0,
            };
            var contentTop = cellTop + cell.InsetTop + offset;
            foreach (var draw in cell.Content)
            {
                draw(canvas, pageHeight - contentTop);
            }
        }

        if (collapse && layout.Horizontal is { } horizontal && layout.Vertical is { } vertical)
        {
            DrawCollapsedLines(canvas, pageHeight, layout, horizontal, vertical, x, start, end, TopOf);
        }
    }

    /// <summary>Draws collapsed grid lines centered on the cell boundaries of rows [start, end).</summary>
    private static void DrawCollapsedLines(
        PdfCanvas canvas, double pageHeight, TableLayout layout, GridLine[,] horizontal, GridLine[,] vertical,
        double x, int start, int end, Func<int, double> topOf)
    {
        var columns = layout.ColumnX.Length - 1;
        var bottom = topOf(end - 1) + layout.RowHeights[end - 1];
        double MaxVertical(int row, int column) => vertical[row, column].Width;

        for (var r = start; r < end; r++)
        {
            for (var c = 0; c <= columns; c++)
            {
                var line = vertical[r, c];
                if (line.Width > 0)
                {
                    var lineTop = topOf(r);
                    canvas.SetFillColor(line.Color)
                        .Rectangle(x + layout.ColumnX[c] - (line.Width / 2), pageHeight - lineTop - layout.RowHeights[r], line.Width, layout.RowHeights[r]).Fill();
                }
            }
        }

        for (var r = start; r <= end; r++)
        {
            var y = r == end ? bottom : topOf(r);
            for (var c = 0; c < columns; c++)
            {
                var line = horizontal[r, c];
                if (line.Width <= 0)
                {
                    continue;
                }

                // Extend over half of the vertical lines at both ends so corners are closed.
                var row = Math.Min(r, end - 1);
                var x0 = x + layout.ColumnX[c] - (MaxVertical(row, c) / 2);
                var x1 = x + layout.ColumnX[c + 1] + (MaxVertical(row, c + 1) / 2);
                canvas.SetFillColor(line.Color).Rectangle(x0, pageHeight - y - (line.Width / 2), x1 - x0, line.Width).Fill();
            }
        }
    }

    /// <summary>Height of a block's border box if it were laid out without page breaks.</summary>
    private double MeasureHeight(Block block, double width) => MeasureBox(block, BoxWidth(block, width));

    /// <summary>Height of a block's border box at a given border-box width, if laid out without page breaks.</summary>
    private double MeasureBox(Block block, double boxWidth) => ApplyHeights(block, MeasureContentBox(block, boxWidth));

    /// <summary>Height of a block's border box from its content alone, ignoring its height properties.</summary>
    private double MeasureContentBox(Block block, double boxWidth)
    {
        var style = block.Style;
        var decoration = style.Border.Widths.Vertical + style.Padding.Vertical;
        var contentWidth = Math.Max(MinimumContentWidth, boxWidth - style.Border.Widths.Horizontal - style.Padding.Horizontal);
        switch (block)
        {
            case SpacerBlock spacer:
                return spacer.Height;
            case ParagraphBlock paragraph:
                return decoration + paragraph.Paragraph.Layout(contentWidth).Height;
            case ImageBlock image:
                return decoration + image.GetSize(contentWidth, ImageMaxHeight(image)).Height;
            case TableBlock table:
                var tableLayout = TableLayouter.Layout(table, 0, contentWidth);
                return decoration + tableLayout.Height(0, table.Rows.Count) + (2 * tableLayout.SpacingY);
            case FlexBlock flex:
                return decoration + MeasureFlex(flex, contentWidth);
            case ContainerBlock container:
                var height = 0.0;
                var margin = new CollapsedMargin();
                foreach (var child in container.Children)
                {
                    margin.Add(child.Style.Margin.Top);
                    height += margin.Value + MeasureHeight(child, contentWidth);
                    margin = new CollapsedMargin();
                    margin.Add(child.Style.Margin.Bottom);
                }

                return decoration + height + margin.Value;
            default:
                return 0;
        }
    }

    /// <summary>
    /// The smallest height a block needs at the bottom of a page before it is worth starting there:
    /// its top border and padding plus the first lines a paragraph may not be split from (orphans).
    /// </summary>
    private double MinimumStartHeight(Block block, double width) => MinimumStartBox(block, BoxWidth(block, width));

    /// <summary>The smallest height a block of a given border-box width needs at the bottom of a page.</summary>
    private double MinimumStartBox(Block block, double boxWidth)
    {
        var style = block.Style;
        if (style.Height is not null && block is not TableBlock)
        {
            return MeasureBox(block, boxWidth); // A fixed-height box is kept on one page when it fits on one.
        }

        var top = style.Border.Top.Width + style.Padding.Top;
        var contentWidth = Math.Max(MinimumContentWidth, boxWidth - style.Border.Widths.Horizontal - style.Padding.Horizontal);
        switch (block)
        {
            case ParagraphBlock paragraph:
                // The first lines it may not be split from (orphans); all of them if orphans and widows leave no break.
                var lines = paragraph.Paragraph.Layout(contentWidth).Lines;
                var orphans = Math.Max(1, style.Orphans);
                var count = lines.Count < orphans + Math.Max(1, style.Widows) ? lines.Count : orphans;
                var linesHeight = lines.Take(count).Sum(l => l.Height);
                return top + (count == lines.Count ? linesHeight + style.Padding.Bottom + style.Border.Bottom.Width : linesHeight);
            case ImageBlock image:
                return MeasureBox(image, boxWidth); // Images are never split.
            case FlexBlock flex when flex.Items.Count > 0:
                return top + MinimumFlexStart(flex, contentWidth);
            case TableBlock table when table.Rows.Count > 0:
                // The header rows and the first group of rows that must stay together (or the start of a splittable row).
                var tableLayout = TableLayouter.Layout(table, 0, contentWidth);
                var firstGroup = tableLayout.Groups.FirstOrDefault(g => g.End > tableLayout.HeaderRows, tableLayout.Groups[^1]);
                var firstEnd = firstGroup.End;
                var firstBody = Math.Max(firstGroup.Start, tableLayout.HeaderRows);
                if (firstBody < firstEnd && CanSplit(tableLayout, firstBody, firstEnd))
                {
                    var header = tableLayout.HeaderRows > 0 ? tableLayout.Height(0, tableLayout.HeaderRows) + tableLayout.SpacingY : 0;
                    var splitStart = top + tableLayout.SpacingY + header + MinimumRowStart(tableLayout, firstBody);
                    return Math.Min(splitStart, top + tableLayout.SpacingY + tableLayout.Height(0, firstEnd));
                }

                return top + tableLayout.SpacingY + tableLayout.Height(0, firstEnd) + (firstEnd == table.Rows.Count ? tableLayout.SpacingY + style.Padding.Bottom + style.Border.Bottom.Width : 0);
            case ContainerBlock container when container.Children.Count > 0:
                var firstChild = container.Children[0];
                return top + firstChild.Style.Margin.Top + MinimumStartHeight(firstChild, contentWidth);
            case SpacerBlock:
                return 0;
            default:
                return top + style.Padding.Bottom + style.Border.Bottom.Width;
        }
    }

    /// <summary>CSS margin collapsing: the largest positive margin plus the most negative one.</summary>
    private struct CollapsedMargin
    {
        private double _positive;
        private double _negative;

        public readonly double Value => _positive + _negative;

        public void Add(double margin)
        {
            _positive = Math.Max(_positive, margin);
            _negative = Math.Min(_negative, margin);
        }
    }
}
