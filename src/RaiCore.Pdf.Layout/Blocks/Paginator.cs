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

/// <summary>
/// Where a block that can be navigated to (one with a <see cref="Block.Bookmark"/> or a <see cref="Block.Id"/>) starts:
/// the top of its border box, or of its content when that moved to a later page. Also where a paragraph's anchor
/// (<see cref="Paragraph.AddAnchor"/>) is: the top of its line. Y values are from the top of the page.
/// </summary>
/// <param name="Block">The block (for a paragraph's anchor, the paragraph's block).</param>
/// <param name="Page">0-based page index.</param>
/// <param name="Top">Top of the block, or of the anchor's line.</param>
/// <param name="Left">Left edge of the block's border box, or of the anchor's line.</param>
internal sealed record Anchor(Block Block, int Page, double Top, double Left)
{
    /// <summary>The id of a paragraph's anchor; <c>null</c> for the block's own anchor.</summary>
    public string? Name { get; init; }

    /// <summary>The name of the destination: the paragraph anchor's id or the block's.</summary>
    public string? DestinationName => Name ?? Block.Id;
}

/// <summary>A page's context (number, name, side, blank) and the setup chosen for it.</summary>
/// <param name="Context">What the setup was chosen by.</param>
/// <param name="Setup">The page's size and margins.</param>
internal readonly record struct PageFrame(PageContext Context, PageSetup Setup);

/// <summary>The result of pagination: drawing operations per page, and where everything went.</summary>
/// <param name="Pages">Drawing operations per page.</param>
/// <param name="Placements">
/// Where each piece of each block went. X values are in layout coordinates: from the left margin of the document's
/// <see cref="FlowDocument.Setup"/>, which pages with another left margin are shifted from when rendered.
/// </param>
/// <param name="Markers">The markers of the blocks starting on each page.</param>
/// <param name="Anchors">Where the blocks with bookmarks or ids, and the paragraphs' anchors, are, in document order.</param>
/// <param name="Frames">Each page's context and setup.</param>
internal sealed record PaginationResult(
    IReadOnlyList<IReadOnlyList<DrawOperation>> Pages, IReadOnlyList<Placement> Placements, IReadOnlyList<(int Page, object Marker)> Markers,
    IReadOnlyList<Anchor> Anchors, IReadOnlyList<PageFrame> Frames);

/// <summary>
/// Lays blocks out top to bottom and splits them across pages, following CSS block layout and fragmentation:
/// adjacent vertical margins collapse, margins are dropped at the top of a page after an automatic break,
/// paragraphs split between lines while honoring orphans and widows, <see cref="PageBreak.Avoid"/> after a block
/// keeps it with the start of the next one, and boxes split across pages are sliced (top border and padding on
/// the first fragment, bottom ones on the last).
/// </summary>
/// <remarks>
/// <para>
/// Coordinates. All y values here are distances from the top of the page, in that page's own geometry: content starts
/// at its top margin (<see cref="ContentTop"/>, <see cref="PageTop"/>) and ends above its bottom margin
/// (<see cref="PageBottom"/>), which may differ per page (<see cref="FlowDocument.PageSetupResolver"/>).
/// </para>
/// <para>
/// X values are layout coordinates, not page coordinates: they are measured as if every page had the left margin of the
/// document's <see cref="FlowDocument.Setup"/>. The top-level blocks start at <c>Setup.Margins.Left</c> and the content
/// area of page <c>p</c> spans <c>[Setup.Margins.Left, Setup.Margins.Left + ContentWidthOf(p))</c>.
/// <see cref="FlowDocument.RenderTo"/> shifts each page's drawing operations by
/// <c>page.Margins.Left - Setup.Margins.Left</c> (a canvas transform, which links follow), so a page with another left
/// margin (e.g. mirrored :left/:right margins) moves its content without relaying it out. Placements and anchors keep
/// layout x values. To convert: page x = layout x + <c>_frames[p].Setup.Margins.Left - _setup.Margins.Left</c>.
/// Anything recorded in page coordinates (e.g. areas that later content must avoid on a page) must be converted with
/// that per-page offset; with a single setup (no resolver) the offset is always 0. A static column
/// (<see cref="LayoutStatic"/>) is never shifted.
/// </para>
/// </remarks>
internal sealed partial class Paginator
{
    private const double Epsilon = 1e-6;

    /// <summary>Content narrower than this is laid out at this width (it overflows, as in CSS) instead of failing.</summary>
    private const double MinimumContentWidth = 1;

    /// <summary>Height of the virtual page used to lay out content without page breaks (e.g. inside table cells).</summary>
    private const double StaticHeight = 1e7;

    /// <summary>The document's setup (or the static column's), which pages get unless the document resolves one per page.</summary>
    private readonly PageSetup _setup;

    /// <summary>The document whose <see cref="FlowDocument.PageSetupResolver"/> chooses each page's setup; null for static layout.</summary>
    private readonly FlowDocument? _document;

    private readonly bool _static;

    /// <summary>Each page's context and setup, parallel to <see cref="_pages"/>.</summary>
    private readonly List<PageFrame> _frames = [];

    /// <summary>Setups resolved so far, per context (the resolver is also asked about pages not created yet).</summary>
    private readonly Dictionary<PageContext, PageSetup> _resolved = [];

    /// <summary>The page name of the block being laid out (its <see cref="Block.PageName"/> or its parent's).</summary>
    private string? _pageName;

    /// <summary>
    /// Laying out the block flow of the document (not flex items or table cells): page names and left/right breaks apply.
    /// </summary>
    private bool _blockFlow = true;

    /// <summary>
    /// The width blocks are laid out in follows the content width of the page they start on: no ancestor has a width of
    /// its own (CSS <c>width</c> or <c>max-width</c>) and none is a flex item or a table cell.
    /// </summary>
    private bool _fluid = true;
    private readonly List<PageOperations> _pages = [];
    private readonly List<Placement> _placements = [];
    private readonly List<(int Page, object Marker)> _markers = [];
    private readonly List<Anchor> _anchors = [];
    private int _pageIndex = -1;

    /// <summary>Where content starts on pages that begin with repeated content (a table's header rows).</summary>
    private readonly Dictionary<int, double> _pageTops = [];

    /// <summary>Called on every page created while they are registered, e.g. to repeat a table's header rows.</summary>
    private readonly List<Action> _pageStartHooks = [];

    /// <summary>Space kept free at the bottom of pages from a page on, e.g. for a table's repeated footer rows.</summary>
    private readonly List<(int FromPage, double Height)> _bottomReserves = [];

    private Paginator(PageSetup setup, FlowDocument? document = null, bool isStatic = false)
    {
        _setup = setup;
        _document = document;
        _static = isStatic;
        NewPage(forced: true); // The first page keeps top margins, like the start of a CSS document.
    }

    private double ContentTop => _frames[PageIndex].Setup.Margins.Top;

    /// <summary>The content width of a page.</summary>
    private double ContentWidthOf(int page) => _frames[page].Setup.ContentWidth;

    /// <summary>The content height of the current page.</summary>
    private double ContentHeight => _frames[PageIndex].Setup.ContentHeight;

    /// <summary>The content height of the next page (to know whether content would fit at the top of a fresh page).</summary>
    private double NextPageContentHeight
    {
        get
        {
            if (PageIndex + 1 < _frames.Count)
            {
                return _frames[PageIndex + 1].Setup.ContentHeight;
            }

            var current = _frames[PageIndex].Context;
            return Resolve(new PageContext(PageIndex + 2, current.Name, Opposite(current.Side), IsBlank: false)).ContentHeight;
        }
    }

    /// <summary>The setup of a page with this context.</summary>
    private PageSetup Resolve(PageContext context)
    {
        if (_document?.PageSetupResolver is null)
        {
            return _setup;
        }

        if (!_resolved.TryGetValue(context, out var setup))
        {
            _resolved[context] = setup = _document.ResolveSetup(context);
        }

        return setup;
    }

    private static PageSide Opposite(PageSide side) => side == PageSide.Left ? PageSide.Right : PageSide.Left;

    private double ContentBottom => PageBottom(PageIndex);

    /// <summary>Where a page's flowing content ends: above space reserved for repeated table footers, if any.</summary>
    private double PageBottom(int page)
    {
        var setup = _frames[page].Setup;
        var bottom = setup.Size.Height - setup.Margins.Bottom;
        foreach (var (fromPage, height) in _bottomReserves)
        {
            if (page >= fromPage)
            {
                bottom -= height;
            }
        }

        return bottom;
    }

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
        var paginator = new Paginator(document.Setup, document);
        var pendingMargin = new CollapsedMargin();
        paginator.LayoutChildren(document.Blocks, document.Setup.Margins.Left, paginator.ContentWidthOf(0), ref pendingMargin, firstTopMarginApplied: false);
        paginator.PlaceFixed();
        return new PaginationResult(paginator.GroupedPages(), paginator._placements, paginator._markers, paginator._anchors, paginator._frames);
    }

    /// <summary>
    /// Lays out blocks in one unbroken column (no pages), e.g. the content of a table cell. Returns the height, the
    /// drawing operations, and the anchors (with tops relative to the top of the column); the operations draw with
    /// y = 0 at the top when called with a page height of "distance from the top of the column to the bottom of the page".
    /// </summary>
    public static (double Height, IReadOnlyList<DrawOperation> Content, IReadOnlyList<Anchor> Anchors) LayoutStatic(IReadOnlyList<Block> blocks, double x, double width)
    {
        var setup = new PageSetup(new PdfPageSize(x + width + 1, StaticHeight), new Thickness(0, 1, 0, x));
        var paginator = new Paginator(setup, isStatic: true);
        var pending = default(CollapsedMargin);
        paginator.InFloatRoot(() =>
        {
            paginator.LayoutChildren(blocks, x, width, ref pending, firstTopMarginApplied: false);
            paginator.Y += pending.Value;
        });
        return (paginator.Y, paginator._pages[0].Grouped(), paginator._anchors);
    }

    /// <summary>A block's min-content and max-content widths, including its margins (CSS intrinsic sizes).</summary>
    public static (double Min, double Max) MeasureIntrinsic(Block block)
    {
        var style = block.Style;
        var (min, max) = IntrinsicBorderBox(block);
        return (min + style.Margin.Horizontal, max + style.Margin.Horizontal);
    }

    /// <summary>The border-box min-content and max-content widths: a fixed width or the content's, within the min/max widths.</summary>
    private static (double Min, double Max) IntrinsicBorderBox(Block block)
    {
        var style = block.Style;
        var (min, max) = style.Width is { } fixedWidth ? (fixedWidth, fixedWidth) : IntrinsicContentBorderBox(block);
        return (style.ClampWidth(min), style.ClampWidth(max));
    }

    /// <summary>The border-box min-content and max-content widths from the content, ignoring a fixed width.</summary>
    private static (double Min, double Max) IntrinsicContentBorderBox(Block block)
    {
        var style = block.Style;
        var decoration = style.Border.Widths.Horizontal + style.Padding.Horizontal;
        var (min, max) = block switch
        {
            ParagraphBlock paragraph => paragraph.Paragraph.MeasureIntrinsicWidths(),
            ImageBlock image => Both(image.GetSize(double.MaxValue, double.MaxValue, ImageWidthLimits(image)).Width),
            ContainerBlock container => container.Children.Where(c => !IsOutOfFlow(c.Style)).Select(MeasureIntrinsic).Aggregate((Min: 0.0, Max: 0.0), (a, b) => (Math.Max(a.Min, b.Min), Math.Max(a.Max, b.Max))),
            TableBlock table => TableLayouter.MeasureIntrinsic(table),
            FlexBlock flex => FlexIntrinsic(flex),
            _ => (0, 0),
        };
        return (min + decoration, max + decoration);

        static (double, double) Both(double width) => (width, width);
    }

    /// <summary>
    /// The border-box width a block gets: its fixed width, or the available width; images and tables without a
    /// width shrink to fit their content (CSS shrink-to-fit). The min and max widths limit all of these, except that
    /// an image still scales down to fit the available width and a table without a width does not get narrower than its
    /// content allows.
    /// </summary>
    private double BoxWidth(Block block, double width)
    {
        var style = block.Style;
        var available = Math.Max(0, width - style.Margin.Horizontal);
        if (style.Width is { } fixedWidth)
        {
            return style.ClampWidth(fixedWidth);
        }

        switch (block)
        {
            case ImageBlock image:
                var decoration = style.Border.Widths.Horizontal + style.Padding.Horizontal;
                return Math.Min(available, image.GetSize(Math.Max(1e-3, available - decoration), ImageMaxHeight(image), ImageWidthLimits(image)).Width + decoration);
            case TableBlock:
                var (min, max) = IntrinsicContentBorderBox(block);
                return Math.Max(min, style.ClampWidth(Math.Min(available, max)));
            default:
                return style.ClampWidth(available);
        }
    }

    /// <summary>An image's minimum and maximum content widths from its box's border-box limits (0 for none).</summary>
    private static (double Min, double Max) ImageWidthLimits(Block block)
    {
        var style = block.Style;
        var decoration = style.Border.Widths.Horizontal + style.Padding.Horizontal;
        return (Math.Max(0, (style.MinWidth ?? 0) - decoration), style.MaxWidth is { } max ? Math.Max(1e-3, max - decoration) : 0);
    }

    /// <summary>Moves to the next page, creating it unless a side-by-side flex item already did. It keeps the page name.</summary>
    private void NewPage(bool forced = false) => NewPage(forced, _frames.Count > 0 ? _frames[PageIndex].Context.Name : null, blank: false);

    /// <summary>
    /// Moves to the next page, creating it (with the page name, on the side after the last page's) unless a side-by-side
    /// flex item already did. A blank page gets no repeated content (table headers).
    /// </summary>
    private void NewPage(bool forced, string? name, bool blank)
    {
        _pageIndex++;
        var created = _pageIndex == _pages.Count;
        if (created)
        {
            var side = _frames.Count > 0 ? Opposite(_frames[^1].Context.Side) : PageSide.Right; // Left-to-right: starts on a right page.
            var context = new PageContext(_pageIndex + 1, name, side, blank);
            _frames.Add(new PageFrame(context, Resolve(context)));
            _pages.Add(new PageOperations(this));
        }

        Y = ContentTop;
        TruncateMargins = !forced;
        if (created && !blank && _pageStartHooks.Count > 0)
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

    /// <summary>Nothing has been placed on the current page.</summary>
    private bool PageIsEmpty => AtPageTop && _pages[PageIndex].Count == 0;

    /// <summary>
    /// A forced break before content with page name <paramref name="name"/>, which must start on a page of
    /// <paramref name="side"/> (if given): a new page, after a blank one if the next page is on the other side. An empty
    /// page is not left empty: it takes the name (and, at the start of the document, the side), or becomes the blank page.
    /// Outside the block flow, names and sides do not apply.
    /// </summary>
    private void ForcedBreak(string? name, PageSide? side)
    {
        var current = _frames[PageIndex].Context;
        if (!_blockFlow)
        {
            if (!PageIsEmpty)
            {
                NewPage(forced: true);
            }

            return;
        }

        if (PageIsEmpty)
        {
            if (_pages.Count == 1)
            {
                // The start of the document: the first page takes the content's name and side.
                if (name != current.Name || (side is { } first && first != current.Side))
                {
                    Reframe(name, side ?? current.Side, blank: false);
                }
            }
            else if (side is { } wanted && wanted != current.Side)
            {
                Reframe(name, current.Side, blank: true);
                NewPage(forced: true, name, blank: false);
            }
            else if (name != current.Name)
            {
                Reframe(name, current.Side, blank: false);
            }

            return;
        }

        if (side is { } required && Opposite(current.Side) != required)
        {
            NewPage(forced: true, name, blank: true);
        }

        NewPage(forced: true, name, blank: false);
    }

    /// <summary>Gives the current (empty) page another name, side or blankness, and so maybe another setup.</summary>
    private void Reframe(string? name, PageSide side, bool blank)
    {
        var context = new PageContext(PageIndex + 1, name, side, blank);
        _frames[PageIndex] = new PageFrame(context, Resolve(context));
        Y = PageTop(PageIndex);
    }

    /// <summary>Whether a break value forces a page break.</summary>
    private static bool IsForced(PageBreak value) => value is PageBreak.Page or PageBreak.Left or PageBreak.Right;

    /// <summary>The side a forced break requires the next page to be on, if any.</summary>
    private static PageSide? SideOf(PageBreak value) => value switch
    {
        PageBreak.Left => PageSide.Left,
        PageBreak.Right => PageSide.Right,
        _ => null,
    };

    /// <summary>
    /// An empty block without border or padding takes no space: its margins collapse through it (CSS 2.1 section 8.3.1),
    /// so it only records its marker where the next content will start.
    /// </summary>
    private static bool TakesNoSpace(Block block)
    {
        var style = block.Style;
        return block is ContainerBlock { Children.Count: 0 } && !HasTopEdge(style) && !HasBottomEdge(style) && style.Background is null
            && style.Height is not > 0 && style.MinHeight is not > 0 && !IsForced(style.BreakBefore) && !IsForced(style.BreakAfter);
    }

    /// <summary>
    /// The page name a block starts with (CSS start page value): its own or its parent's, or, for a container, its first
    /// child's.
    /// </summary>
    private static string? StartName(Block block, string? inherited)
    {
        var name = block.PageName ?? inherited;
        if (block is ContainerBlock container)
        {
            foreach (var child in container.Children)
            {
                if (!TakesNoSpace(child))
                {
                    return StartName(child, name);
                }
            }
        }

        return name;
    }

    /// <summary>
    /// Lays out sibling blocks. <paramref name="pending"/> carries the collapsed margin above the next block in and
    /// the margin below the last block out (so it can collapse through a parent without bottom border or padding).
    /// </summary>
    private void LayoutChildren(IReadOnlyList<Block> blocks, double x, double width, ref CollapsedMargin pending, bool firstTopMarginApplied)
    {
        // Blocks follow the content width of the page they start on: the width is given for the current page.
        var fluid = _fluid && !_static;
        var entryWidth = ContentWidthOf(PageIndex);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
            var style = block.Style;

            if (IsOutOfFlow(style))
            {
                // Out of the flow: placed in its containing block, where it would have been in the flow if insets are auto.
                AddAbsolute(block, x, Y + (AtPageTop && TruncateMargins ? 0 : pending.Value));
                continue;
            }

            if (style.Float != FloatSide.None)
            {
                // Out of the flow: placed beside it, at the current position after the pending margin.
                PlaceFloat(block, x, width, AtPageTop && TruncateMargins ? 0 : pending.Value);
                continue;
            }

            if (TakesNoSpace(block))
            {
                ApplyClearance(style.Clear, ref pending); // E.g. an empty clearing block after floats ("clearfix").
                pending.Add(style.Margin.Top);
                pending.Add(style.Margin.Bottom);
                if (block.Marker is { } emptyMarker && !_static)
                {
                    _markers.Add((PageIndex, emptyMarker));
                }

                if (block.Bookmark is not null || block.Id is not null)
                {
                    _anchors.Add(new Anchor(block, PageIndex, Y, x + style.Margin.Left));
                }

                continue;
            }

            // Forced break before this block, or after the previous one, or between pages of different names. A left or
            // right break before the block wins over one after the previous block (CSS Fragmentation section 3.1).
            var previousBreak = i > 0 ? blocks[i - 1].Style.BreakAfter : PageBreak.Auto;
            var startName = _blockFlow && !_static ? StartName(block, _pageName) : null;
            var renamed = _blockFlow && !_static && !string.Equals(startName, _frames[PageIndex].Context.Name, StringComparison.Ordinal);
            if (!_static && (renamed || IsForced(style.BreakBefore) || IsForced(previousBreak)))
            {
                var pageBefore = PageIndex;
                ForcedBreak(startName, SideOf(style.BreakBefore) ?? SideOf(previousBreak));
                if (PageIndex != pageBefore)
                {
                    pending = default; // The previous block's bottom margin ends with its page; this block keeps its top margin.
                }
            }

            var available = fluid ? Math.Max(MinimumContentWidth, width + ContentWidthOf(PageIndex) - entryWidth) : width;

            if (!(i == 0 && firstTopMarginApplied))
            {
                pending.Add(style.Margin.Top);
                pending.Add(CollapseThroughTop(block)); // First descendants' top margins collapse with this one.
            }

            ApplyClearance(style.Clear, ref pending);

            // Move to a new page when the start of the block (and, for break-after: avoid, the start of the
            // next block) would not fit here but would fit at the top of a fresh page.
            var needed = MinimumStartHeight(block, available);
            if (style.BreakInside == PageBreakInside.Avoid)
            {
                needed = Math.Max(needed, MeasureHeight(block, available));
            }

            if (style.BreakAfter == PageBreak.Avoid && next is not null)
            {
                needed = Math.Max(needed, MeasureHeight(block, available) + Math.Max(style.Margin.Bottom, next.Style.Margin.Top) + MinimumStartHeight(next, available));
            }

            if (!AtPageTop && needed + pending.Value > Remaining + Epsilon && needed <= NextPageContentHeight + Epsilon)
            {
                NewPage();
                if (fluid)
                {
                    available = Math.Max(MinimumContentWidth, width + ContentWidthOf(PageIndex) - entryWidth);
                }
            }

            if (!(AtPageTop && TruncateMargins))
            {
                Y += pending.Value;
            }

            pending = default;

            // Tables, flex containers, images and new formatting contexts go beside floats, or below them.
            var (areaX, areaWidth) = (x, available);
            if (AvoidsFloats(block))
            {
                (areaX, areaWidth, var moveTo) = RoomBeside(block, x, available, 0);
                if (moveTo is { } below)
                {
                    Y = below;
                }
            }

            var boxX = areaX + style.Margin.Left;
            var boxWidth = BoxWidth(block, areaWidth);
            var alignment = block switch
            {
                ImageBlock { Style.Width: null } image => image.Alignment,
                TableBlock table => table.Alignment,
                _ => TextAlignment.Left,
            };
            if (alignment != TextAlignment.Left)
            {
                // Images and tables shrink to their content; the alignment then positions the whole box.
                var free = Math.Max(0, areaWidth - style.Margin.Horizontal - boxWidth);
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

            _containingWidth = width;
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
        if (block is not ContainerBlock { Children.Count: > 0 } container || HasTopEdge(block.Style) || IsFloatRoot(block, default))
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

    /// <summary>Lays out one block in its opacity scope (see <see cref="LayoutBlock"/>).</summary>
    private CollapsedMargin LayoutBlockInScope(Block block, double x, double width, BoxSizing sizing)
    {
        // The children of a container in the block flow stay in it; those of a container without a width of its own
        // follow the page's content width.
        var (outerName, outerFlow, outerFluid) = (_pageName, _blockFlow, _fluid);
        var inFlow = block is ContainerBlock && !sizing.Isolated;
        _pageName = block.PageName ?? _pageName;
        _blockFlow = outerFlow && inFlow;
        _fluid = outerFluid && inFlow && block.Style is { Width: null, MaxWidth: null };
        try
        {
            return block.Style.Position == Positioning.Static
                ? LayoutBlockContent(block, x, width, sizing)
                : LayoutPositioned(block, x, width, sizing, () => LayoutBlockContent(block, x, width, sizing));
        }
        finally
        {
            (_pageName, _blockFlow, _fluid) = (outerName, outerFlow, outerFluid);
        }
    }

    private CollapsedMargin LayoutBlockContent(Block block, double x, double width, BoxSizing sizing)
    {
        var trailing = default(CollapsedMargin);
        var anchor = BeginAnchor(block, x);
        var placementsBefore = _placements.Count;
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
                var tableStyle = table.BorderCollapse == BorderCollapse.Collapse ? table.Style with { Border = Borders.None, BorderRadius = CornerRadii.None } : table.Style;
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutTable(table, contentX, contentWidth), sizing, tableStyle);
                break;
            case FlexBlock flex:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutFlex(flex, contentX, contentWidth, sizing.DefiniteHeight), sizing);
                break;
            case ContainerBlock container:
                // A new formatting context (a flex item, a float, a flow root) keeps its children's margins and floats
                // inside it.
                var isolated = IsFloatRoot(block, sizing);
                LayoutBox(block, x, width, (contentX, contentWidth) =>
                {
                    void Children()
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
                    }

                    if (isolated)
                    {
                        InFloatRoot(Children);
                    }
                    else
                    {
                        Children();
                    }
                }, sizing);
                break;
            default:
                throw new NotSupportedException($"Unknown block type {block.GetType().Name}.");
        }

        EndAnchor(anchor, placementsBefore);
        return trailing;
    }

    /// <summary>
    /// Records where a block with a bookmark or an id starts (its border box at the current position), before its
    /// content is laid out so that anchors stay in document order. Returns the anchor's index, or -1 when it has none.
    /// </summary>
    private int BeginAnchor(Block block, double x)
    {
        if (block.Bookmark is null && block.Id is null)
        {
            return -1;
        }

        _anchors.Add(new Anchor(block, PageIndex, Y, x));
        return _anchors.Count - 1;
    }

    /// <summary>
    /// Moves an anchor to where the block's content starts if that is on a later page than its box (e.g. a paragraph
    /// whose first lines did not fit below its top border), so that the destination shows the content.
    /// </summary>
    private void EndAnchor(int index, int placementsBefore)
    {
        if (index < 0)
        {
            return;
        }

        var anchor = _anchors[index];
        Placement? first = null;
        for (var i = placementsBefore; i < _placements.Count; i++)
        {
            // Lines of text, table rows and images (box fragments have no lines, and start where the box does).
            var placement = _placements[i];
            if ((placement.FirstLine >= 0 || placement.Block is ImageBlock)
                && (first is null || placement.Page < first.Page || (placement.Page == first.Page && placement.Top < first.Top)))
            {
                first = placement;
            }
        }

        if (first is not null && first.Page > anchor.Page)
        {
            _anchors[index] = anchor with { Page = first.Page, Top = first.Top };
        }
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
        var link = block.Link;

        var startPage = PageIndex;
        var startY = Y;
        var insertAt = _pages[startPage].Count;

        // A box that follows the page's content width is as much wider or narrower on each page as that page.
        var fluid = _fluid && !_static;
        var startWidth = ContentWidthOf(startPage);

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
            var total = ContentBottom - startY + (Y - PageTop(PageIndex));
            for (var page = startPage + 1; page < PageIndex; page++)
            {
                total += _frames[page].Setup.ContentHeight;
            }

            var target = Math.Max(Math.Min(fixedHeight ?? total, maxHeight), minHeight);
            if (target > total)
            {
                Y = Math.Min(ContentBottom, Y + target - total);
            }
        }

        if (sizing.StretchTo is { } line)
        {
            if (decorated || link is not null)
            {
                line.Pending.Add(InCurrentOpacityScope(() => Decorate(line.Page, line.Y - sizing.StretchInset)));
            }

            return;
        }

        if (decorated || link is not null)
        {
            Decorate(PageIndex, Y);
        }

        // Backgrounds and borders of each page fragment, and the link over it.
        void Decorate(int endPage, double endY)
        {
            // Rounded corners are sized for the whole box; each fragment keeps the corners it has.
            var corners = default(RoundedCorners);
            if (decorated && !style.BorderRadius.IsSquare)
            {
                var height = 0.0;
                for (var page = startPage; page <= endPage; page++)
                {
                    height += (page == endPage ? endY : PageBottom(page)) - (page == startPage ? startY : PageTop(page));
                }

                corners = style.BorderRadius.Resolve(width, height);
            }

            for (var page = startPage; page <= endPage; page++)
            {
                var first = page == startPage;
                var last = page == endPage;
                var top = first ? startY : PageTop(page);
                var bottom = last ? endY : PageBottom(page);
                var fragmentWidth = fluid ? Math.Max(0, width + ContentWidthOf(page) - startWidth) : width;
                if (decorated)
                {
                    var index = first ? insertAt : 0; // Pages created inside the box: under everything on them.
                    var fragment = corners.Fragment(first, last, fragmentWidth, bottom - top);
                    _pages[page].Insert(index, (canvas, pageHeight) => DrawBox(canvas, pageHeight, style, x, top, fragmentWidth, bottom - top, first, last, fragment));
                    _placements.Add(new Placement(page, block, top, bottom, -1, -1, x, fragmentWidth));
                }

                if (link is not null)
                {
                    _pages[page].Add((canvas, pageHeight) => link.AddTo(canvas, x, pageHeight - bottom, fragmentWidth, bottom - top));
                }
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

    /// <summary>
    /// Draws a box's (or a fragment's) background and borders. Rounded <paramref name="corners"/> are those of the
    /// fragment; <c>null</c> sizes the style's radii to the fragment.
    /// </summary>
    private static void DrawBox(
        PdfCanvas canvas, double pageHeight, BoxStyle style, double x, double top, double width, double height, bool first, bool last, RoundedCorners? corners = null)
    {
        var y = pageHeight - top - height; // PDF y of the bottom edge.
        var rounded = corners ?? (style.BorderRadius.IsSquare ? default : style.BorderRadius.Resolve(width, height).Fragment(first, last, width, height));
        if (style.Background is { } background)
        {
            canvas.SetFillColor(background);
            if (AddBackgroundArea(canvas, style, rounded, x, y, width, height, first, last))
            {
                canvas.Fill();
            }
        }

        BorderPainter.PaintBox(canvas, style.Border, x, y, width, height, top: first, bottom: last, corners: rounded);
    }

    /// <summary>
    /// Adds the area a box's background covers (CSS <c>background-clip</c>) as a path: the border box, or the padding or
    /// content box (inset on the edges the fragment has), with the corners reduced by the inset. False when it is empty.
    /// </summary>
    private static bool AddBackgroundArea(PdfCanvas canvas, BoxStyle style, RoundedCorners corners, double x, double y, double width, double height, bool first, bool last)
    {
        var inset = style.BackgroundClip switch
        {
            BackgroundClip.PaddingBox => style.Border.Widths,
            BackgroundClip.ContentBox => new Thickness(
                style.Border.Top.Width + style.Padding.Top, style.Border.Right.Width + style.Padding.Right,
                style.Border.Bottom.Width + style.Padding.Bottom, style.Border.Left.Width + style.Padding.Left),
            _ => Thickness.Zero,
        };
        var (top, bottom) = (first ? inset.Top : 0, last ? inset.Bottom : 0);
        var (areaWidth, areaHeight) = (width - inset.Horizontal, height - top - bottom);
        if (areaWidth <= 0 || areaHeight <= 0)
        {
            return false;
        }

        if (corners.IsSquare)
        {
            canvas.Rectangle(x + inset.Left, y + bottom, areaWidth, areaHeight);
        }
        else
        {
            corners.Inset(top, inset.Right, bottom, inset.Left).AddPath(canvas, x + inset.Left, y + bottom, areaWidth, areaHeight);
        }

        return true;
    }

    private void LayoutImage(ImageBlock block, double x, double width)
    {
        var (w, h) = block.GetSize(width, ImageMaxHeight(block), ImageWidthLimits(block));
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
        if (block.Style.BorderRadius.IsSquare)
        {
            _pages[PageIndex].Add((canvas, pageHeight) => canvas.DrawImage(block.Image, left, pageHeight - top - h, w, h));
        }
        else
        {
            // Clipped to the content box's curves: the border box's radii, reduced by the border and padding.
            var (border, padding) = (block.Style.Border.Widths, block.Style.Padding);
            var corners = block.Style.BorderRadius.Resolve(w + border.Horizontal + padding.Horizontal, h + border.Vertical + padding.Vertical)
                .Inset(border.Top + padding.Top, border.Right + padding.Right, border.Bottom + padding.Bottom, border.Left + padding.Left);
            _pages[PageIndex].Add((canvas, pageHeight) =>
            {
                canvas.SaveState();
                corners.Clip(canvas, left, pageHeight - top - h, w, h);
                canvas.DrawImage(block.Image, left, pageHeight - top - h, w, h);
                canvas.RestoreState();
            });
        }

        Y += h;
        _placements.Add(new Placement(PageIndex, block, top, Y, -1, -1, left, w));
    }

    /// <summary>An image is scaled down to fit on one page together with its own border and padding.</summary>
    private double ImageMaxHeight(Block block) =>
        Math.Max(1, ContentHeight - block.Style.Border.Widths.Vertical - block.Style.Padding.Vertical);

    private void LayoutParagraph(ParagraphBlock block, double x, double width)
    {
        // Lines beside floats get the room the floats leave where the lines are. So after each page break the lines that
        // are left are laid out again from where they now start: the lines already placed keep the room they had (and so
        // stay the same), the others get the room on the new page.
        var rooms = new List<(double Top, double Offset, double Width)>();
        var from = 0.0; // Where the current page's lines start, from the paragraph's top.
        ParagraphLayout LayoutFrom(int keep) => rooms.Count > 0 || HasFloatsBelow(Y)
            ? block.Paragraph.Layout(width, LineRooms(x, width, Y - from, keep, rooms))
            : block.Paragraph.Layout(width);

        var layout = LayoutFrom(0);
        var lines = layout.Lines;
        var orphans = Math.Max(1, block.Style.Orphans);
        var widows = Math.Max(1, block.Style.Widows);
        var i = 0;
        void NextPage()
        {
            NewPage();
            from = i == 0 ? 0 : lines[i - 1].Top + lines[i - 1].Height; // Without a gap line i had above it on the last page.
            layout = LayoutFrom(i);
            lines = layout.Lines;
        }

        while (i < lines.Count)
        {
            var fit = CountFittingLines(lines, i, from, Remaining);
            var rest = lines.Count - i;
            if (fit >= rest)
            {
                Place(block, layout, x, i, lines.Count, Y - from);
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
                    NextPage();
                    continue;
                }

                // Even an empty page cannot satisfy the constraints: place what fits (at least one line).
                take = Math.Max(1, Math.Min(fit, rest));
            }

            Place(block, layout, x, i, i + take, Y - from);
            i += take;
            if (i < lines.Count)
            {
                NextPage();
            }
        }
    }

    /// <summary>
    /// Places lines [first, end) on the current page, with the paragraph's top at <paramref name="origin"/> (lines that
    /// moved down past floats keep their gap).
    /// </summary>
    private void Place(ParagraphBlock block, ParagraphLayout layout, double x, int first, int end, double origin)
    {
        var top = origin + layout.Lines[first].Top;
        _pages[PageIndex].Add((canvas, pageHeight) => layout.Draw(canvas, x, pageHeight - top, first, end));
        Y += layout.Lines[end - 1].Top + layout.Lines[end - 1].Height - layout.Lines[first].Top;
        _placements.Add(new Placement(PageIndex, block, top, Y, first, end, x, layout.AvailableWidth));
        AddParagraphAnchors(block, layout, x, top, first, end);
    }

    /// <summary>
    /// Records the paragraph's anchors (<see cref="Paragraph.AddAnchor"/>) on lines [first, end), placed from
    /// <paramref name="top"/> on the current page: each at the top of the line containing it (an anchor at the very end
    /// of the text belongs to the last line).
    /// </summary>
    private void AddParagraphAnchors(ParagraphBlock block, ParagraphLayout layout, double x, double top, int first, int end)
    {
        var lines = layout.Lines;
        foreach (var anchor in block.Paragraph.Anchors)
        {
            for (var i = first; i < end; i++)
            {
                var line = lines[i];
                if (anchor.Index >= line.Start && (anchor.Index < line.End || i == lines.Count - 1))
                {
                    _anchors.Add(new Anchor(block, PageIndex, top + line.Top - lines[first].Top, x + line.X) { Name = anchor.Id });
                    break;
                }
            }
        }
    }

    /// <summary>
    /// How many lines from <paramref name="start"/> fit in <paramref name="available"/> height below
    /// <paramref name="from"/> (from the paragraph's top): lines that moved down past floats leave gaps, so heights are
    /// measured from the lines' tops.
    /// </summary>
    private static int CountFittingLines(IReadOnlyList<LineLayout> lines, int start, double from, double available)
    {
        var count = 0;
        for (var i = start; i < lines.Count; i++)
        {
            var used = lines[i].Top + lines[i].Height - from;
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
        var footerStart = table.Rows.Count - layout.FooterRows;
        var anchoredCells = new HashSet<CellLayout>(ReferenceEqualityComparer.Instance);

        void Place(int start, int end)
        {
            var top = Y;
            _pages[PageIndex].Add((canvas, pageHeight) => DrawRows(canvas, pageHeight, layout, x, top, start, end));
            AddCellAnchors(layout, top, start, end, anchoredCells);
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

        // Footer rows repeat below the body on every page the table continues from: their space is kept free at the
        // bottom of each of its pages, and they are drawn there once the body is placed.
        var startPage = PageIndex;
        var placementsBefore = _placements.Count;
        var footerHeight = layout.FooterRows > 0 ? layout.Height(footerStart, table.Rows.Count) : 0;
        var repeatFooter = !_static && layout.FooterRows > 0 && footerHeight <= ContentHeight / 4;
        var reserve = (startPage, footerHeight + (2 * layout.SpacingY));

        StartPage();
        _pageStartHooks.Add(StartPage);
        if (repeatFooter)
        {
            _bottomReserves.Add(reserve);
        }

        try
        {
            foreach (var (start, groupEnd) in layout.Groups.Where(g => g.End > headerEnd && g.Start < footerStart))
            {
                var groupStart = Math.Max(start, headerEnd);
                var end = Math.Min(groupEnd, footerStart);
                var height = layout.Height(groupStart, end);
                if (Y + height <= ContentBottom + Epsilon)
                {
                    Place(groupStart, end);
                    continue;
                }

                // A row that does not fit is split here if the start of its cells fits, as in browsers.
                if (!_static && CanSplit(layout, groupStart, end) && MinimumRowStart(layout, groupStart) <= Remaining + Epsilon)
                {
                    SplitRows(layout, groupStart, end, x, width);
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
                    SplitRows(layout, groupStart, end, x, width);
                }
            }
        }
        finally
        {
            _pageStartHooks.Remove(StartPage);
            _bottomReserves.Remove(reserve);
        }

        if (layout.FooterRows == 0)
        {
            return;
        }

        // On the pages the table continues from, the footer goes right below the last body row (or row piece).
        if (repeatFooter)
        {
            var bodyEnds = _placements.Skip(placementsBefore)
                .Where(p => p.Block == table && p.FirstLine >= headerEnd && p.FirstLine < footerStart && p.Page < PageIndex)
                .GroupBy(p => p.Page)
                .Select(g => (Page: g.Key, Bottom: g.Max(p => p.Bottom)));
            foreach (var (page, bottom) in bodyEnds)
            {
                var top = bottom + layout.SpacingY;
                _pages[page].Add((canvas, pageHeight) => DrawRows(canvas, pageHeight, layout, x, top, footerStart, table.Rows.Count));
                _placements.Add(new Placement(page, table, top, top + footerHeight, footerStart, table.Rows.Count, x, width));
            }
        }

        Place(footerStart, table.Rows.Count);
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

            var contentTop = cellTop + cell.InsetTop + ContentOffset(cell, cellHeight);
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

    /// <summary>How far a cell's content sits below the top of its content box (CSS <c>vertical-align</c>).</summary>
    private static double ContentOffset(CellLayout cell, double cellHeight)
    {
        var free = cellHeight - cell.InsetTop - cell.InsetBottom - cell.ContentHeight;
        return cell.Grid.Cell.VerticalAlignment switch
        {
            VerticalAlignment.Middle => Math.Max(0, free / 2),
            VerticalAlignment.Bottom => Math.Max(0, free),
            _ => 0,
        };
    }

    /// <summary>
    /// Records the anchors in the cells beginning in rows [start, end), placed from <paramref name="top"/> on the current
    /// page. Cell content is laid out without pages, so its anchors are offsets from the cell's content box. Rows placed
    /// more than once (header rows repeated on every page) keep the anchors of their first placement.
    /// </summary>
    private void AddCellAnchors(TableLayout layout, double top, int start, int end, HashSet<CellLayout> anchored)
    {
        foreach (var cell in layout.Cells)
        {
            if (cell.Anchors.Count == 0 || cell.Grid.Row < start || cell.Grid.Row >= end || !anchored.Add(cell))
            {
                continue;
            }

            var cellTop = top + layout.Height(start, cell.Grid.Row) + (cell.Grid.Row > start ? layout.SpacingY : 0);
            var contentTop = cellTop + cell.InsetTop + ContentOffset(cell, layout.Height(cell.Grid.Row, cell.Grid.Row + cell.Grid.RowSpan));
            foreach (var anchor in cell.Anchors)
            {
                _anchors.Add(anchor with { Page = PageIndex, Top = contentTop + anchor.Top });
            }
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

        var verticals = new List<BorderPainter.Edge>();
        for (var r = start; r < end; r++)
        {
            for (var c = 0; c <= columns; c++)
            {
                var line = vertical[r, c];
                if (line.Width > 0)
                {
                    var lineBottom = pageHeight - topOf(r) - layout.RowHeights[r];
                    verticals.Add(BorderPainter.VerticalLine(line, x + layout.ColumnX[c], lineBottom, lineBottom + layout.RowHeights[r]));
                }
            }
        }

        BorderPainter.Paint(canvas, verticals);

        // Horizontal lines on top, so they run through the crossings.
        var horizontals = new List<BorderPainter.Edge>();
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
                horizontals.Add(BorderPainter.HorizontalLine(line, x0, x1, pageHeight - y));
            }
        }

        BorderPainter.Paint(canvas, horizontals);
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
                return decoration + image.GetSize(contentWidth, ImageMaxHeight(image), ImageWidthLimits(image)).Height;
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
                // The header rows and the first group of rows that must stay together (or the start of a splittable row),
                // plus the footer rows that will repeat below them.
                var tableLayout = TableLayouter.Layout(table, 0, contentWidth);
                var footerStart = table.Rows.Count - tableLayout.FooterRows;
                var footerHeight = tableLayout.FooterRows > 0 ? tableLayout.Height(footerStart, table.Rows.Count) : 0;
                var footer = footerHeight > 0 && footerHeight <= ContentHeight / 4 ? footerHeight + tableLayout.SpacingY : 0;
                var firstGroup = tableLayout.Groups.FirstOrDefault(g => g.End > tableLayout.HeaderRows, tableLayout.Groups[^1]);
                var firstEnd = Math.Max(Math.Min(firstGroup.End, footerStart), tableLayout.HeaderRows);
                var firstBody = Math.Max(firstGroup.Start, tableLayout.HeaderRows);
                if (firstBody < firstEnd && CanSplit(tableLayout, firstBody, firstEnd))
                {
                    var header = tableLayout.HeaderRows > 0 ? tableLayout.Height(0, tableLayout.HeaderRows) + tableLayout.SpacingY : 0;
                    var splitStart = top + tableLayout.SpacingY + header + MinimumRowStart(tableLayout, firstBody) + footer;
                    return Math.Min(splitStart, top + tableLayout.SpacingY + tableLayout.Height(0, firstEnd) + footer);
                }

                if (firstEnd >= footerStart)
                {
                    footer = 0; // The whole table is its first group: its footer is part of it.
                    firstEnd = table.Rows.Count;
                }

                return top + tableLayout.SpacingY + tableLayout.Height(0, firstEnd) + footer + (firstEnd == table.Rows.Count ? tableLayout.SpacingY + style.Padding.Bottom + style.Border.Bottom.Width : 0);
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
