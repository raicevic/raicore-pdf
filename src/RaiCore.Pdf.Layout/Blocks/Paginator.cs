namespace RaiCore.Pdf.Layout;

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
/// (<see cref="Paragraph.AddAnchor"/>) is: the top of its line, at its place on the line. Y values are from the top of the page.
/// </summary>
/// <param name="Block">The block (for a paragraph's anchor, the paragraph's block).</param>
/// <param name="Page">0-based page index.</param>
/// <param name="Top">Top of the block, or of the anchor's line.</param>
/// <param name="Left">Left edge of the block's border box, or the anchor's place on its line.</param>
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
    IReadOnlyList<Anchor> Anchors, IReadOnlyList<PageFrame> Frames)
{
    /// <summary>Content that did not fit, collected when the document listens for it (<see cref="FlowDocument.Diagnostics"/>).</summary>
    public IReadOnlyList<LayoutDiagnostic> Diagnostics { get; init; } = [];
}

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

    /// <summary>The most pages the document may have (<see cref="ResourceLimits.MaxPages"/>).</summary>
    private readonly int _maxPages;

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
    /// A forced break was just taken before a container: its first in-flow child is at the same break point, so its own
    /// forced break (already counted, see <see cref="StartBreak"/>) does not break again.
    /// </summary>
    private bool _atForcedBreak;

    /// <summary>
    /// The width blocks are laid out in follows the content width of the page they start on: no ancestor has a width of
    /// its own (CSS <c>width</c> or <c>max-width</c>) and none is a flex item or a table cell.
    /// </summary>
    private bool _fluid = true;

    /// <summary>Whether the parent of the block being laid out follows the page's content width (see <see cref="_fluid"/>).</summary>
    private bool _parentFluid = true;
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

    /// <summary>
    /// The used styles of blocks with percentages (<see cref="BoxStyle.HasPercent"/>): resolved against the containing width they were
    /// last given (see <see cref="ResolveStyle"/>), which is the one they are being laid out or measured in.
    /// </summary>
    private readonly Dictionary<Block, (double ContainingWidth, BoxStyle Style)> _used = new(ReferenceEqualityComparer.Instance);

    private Paginator(PageSetup setup, FlowDocument? document = null, bool isStatic = false, ColumnFlow? columns = null)
    {
        _maxPages = ResourceLimits.Current.MaxPages;
        _setup = setup;
        _document = document;
        _static = isStatic;
        _columns = columns;
        NewPage(forced: true); // The first page keeps top margins, like the start of a CSS document.
    }

    private double ContentTop => _frames[PageIndex].Setup.UsedMargins.Top;

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
        if (_columns is { } columns)
        {
            return columns.Setup(context.Number - 1);
        }

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
        var bottom = setup.Size.Height - setup.UsedMargins.Bottom;
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

    /// <summary>Moves to a page (also telling static layouts, which report diagnostics, what page the document is on).</summary>
    private void SetPage(int page)
    {
        _pageIndex = page;
        if (!_static && _diagnostics is { } sink)
        {
            sink.Page = _columns?.OuterPage(page) ?? page;
        }
    }

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
        BlockDepth.Check(document.Blocks);
        using var cache = LayoutCache.Enter();

        // Static layouts (table cells, inline boxes) report into the same list: it is per thread, for this pagination.
        var outerDiagnostics = _diagnostics;
        _diagnostics = document.Diagnostics is null ? null : new DiagnosticSink();
        try
        {
            var paginator = new Paginator(document.Setup, document);
            var pendingMargin = new CollapsedMargin();
            paginator.LayoutChildren(document.Blocks, document.Setup.UsedMargins.Left, paginator.ContentWidthOf(0), ref pendingMargin, firstTopMarginApplied: false);
            paginator.PlaceFixed();
            return new PaginationResult(paginator.GroupedPages(), paginator._placements, paginator._markers, paginator._anchors, paginator._frames)
            {
                Diagnostics = _diagnostics?.Items ?? [],
            };
        }
        finally
        {
            _diagnostics = outerDiagnostics;
        }
    }

    /// <summary>The diagnostics of the pagination running on this thread, if anyone listens (<see cref="FlowDocument.Diagnostics"/>).</summary>
    [ThreadStatic]
    private static DiagnosticSink? _diagnostics;

    /// <summary>Reports content that did not fit, once per kind and block (a block laid out more than once reports once).</summary>
    private void Report(LayoutDiagnosticKind kind, Block block, Func<string> message)
    {
        if (_diagnostics is { } sink)
        {
            sink.Add(kind, block, _static ? sink.Page : _columns?.OuterPage(PageIndex) ?? PageIndex, message);
        }
    }

    /// <summary>Whether what is drawn now is inside a box that clips its overflow (so overflowing is intended).</summary>
    private bool InClippingBox()
    {
        for (var scope = _scope; scope is not null; scope = scope.Parent)
        {
            if (scope.Clip is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The diagnostics of one pagination, without repeats.</summary>
    private sealed class DiagnosticSink
    {
        private readonly HashSet<(LayoutDiagnosticKind, Block)> _seen = [];

        public List<LayoutDiagnostic> Items { get; } = [];

        /// <summary>The page the document's own pagination is on, for what static layouts (table cells, inline boxes) report.</summary>
        public int Page { get; set; }

        public void Add(LayoutDiagnosticKind kind, Block block, int page, Func<string> message)
        {
            if (_seen.Add((kind, block)))
            {
                Items.Add(new LayoutDiagnostic(kind, block, page, message()));
            }
        }
    }

    /// <summary>
    /// Lays out blocks in one unbroken column (no pages), e.g. the content of a table cell. Returns the height, the
    /// drawing operations, and the anchors (with tops relative to the top of the column); the operations draw with
    /// y = 0 at the top when called with a page height of "distance from the top of the column to the bottom of the page".
    /// </summary>
    public static (double Height, IReadOnlyList<DrawOperation> Content, IReadOnlyList<Anchor> Anchors) LayoutStatic(IReadOnlyList<Block> blocks, double x, double width)
    {
        using var cache = LayoutCache.Enter();
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
        using var cache = LayoutCache.Enter();
        var style = block.Style.Intrinsic();
        var (min, max) = IntrinsicBorderBox(block);
        return (min + style.Margin.Fixed.Horizontal, max + style.Margin.Fixed.Horizontal);
    }

    /// <summary>The border-box min-content and max-content widths: a fixed width or the content's, within the min/max widths.</summary>
    private static (double Min, double Max) IntrinsicBorderBox(Block block)
    {
        var style = block.Style.Intrinsic();
        var (min, max) = style.Width?.Fixed is { } fixedWidth ? (fixedWidth, fixedWidth) : IntrinsicContentBorderBox(block);
        return (style.ClampWidth(min), style.ClampWidth(max));
    }

    /// <summary>
    /// The border-box min-content and max-content widths from the content, ignoring a fixed width. The block's own
    /// padding and limits come from <paramref name="style"/> when its containing width is known (its used style).
    /// </summary>
    private static (double Min, double Max) IntrinsicContentBorderBox(Block block, BoxStyle? style = null)
    {
        using var nesting = ResourceLimits.EnterNesting();
        ResourceLimits.Checkpoint();
        style = BoxOf(block, style ?? block.Style.Intrinsic());
        var decoration = style.Border.Widths.Fixed.Horizontal + style.Padding.Fixed.Horizontal;
        var (min, max) = block switch
        {
            ParagraphBlock paragraph => paragraph.Paragraph.MeasureIntrinsicWidths(),
            ImageBlock image => Compressible(image, image.GetSize(double.MaxValue, double.MaxValue, ImageWidthLimits(style)).Width),
            ContainerBlock multicol when InColumns(multicol) => ColumnsIntrinsic(multicol.Columns!, ChildrenIntrinsic(multicol)),
            ContainerBlock container => ChildrenIntrinsic(container),
            TableBlock table => TableLayouter.MeasureIntrinsic(table),
            FlexBlock flex => FlexIntrinsic(flex),
            _ => (0, 0),
        };
        return (min + decoration, max + decoration);

        static (double Min, double Max) ChildrenIntrinsic(ContainerBlock container) =>
            container.Children.Where(c => !IsOutOfFlow(c.Style)).Select(MeasureIntrinsic).Aggregate((Min: 0.0, Max: 0.0), (a, b) => (Math.Max(a.Min, b.Min), Math.Max(a.Max, b.Max)));

        // An image with a percentage width or max-width has no min-content width of its own: it can shrink with its
        // container (CSS Sizing 3 section 5.2.2, compressible replaced elements).
        static (double, double) Compressible(ImageBlock image, double width) =>
            image.Style.Width is { HasPercent: true } || image.Style.MaxWidth is { HasPercent: true } ? (0, width) : (width, width);
    }

    /// <summary>
    /// The border-box width a block gets: its fixed width, or the available width; images and tables without a
    /// width shrink to fit their content (CSS shrink-to-fit). The min and max widths limit all of these, except that
    /// an image still scales down to fit the available width and a table without a width does not get narrower than its
    /// content allows.
    /// </summary>
    private double BoxWidth(Block block, double width)
    {
        var style = StyleOf(block);
        var available = Math.Max(0, width - style.Margin.Fixed.Horizontal);
        if (style.Width?.Fixed is { } fixedWidth)
        {
            return style.ClampWidth(fixedWidth);
        }

        switch (block)
        {
            case ImageBlock image:
                var decoration = style.Border.Widths.Fixed.Horizontal + style.Padding.Fixed.Horizontal;
                return Math.Min(available, image.GetSize(Math.Max(1e-3, available - decoration), ImageMaxHeight(image), ImageWidthLimits(style)).Width + decoration);
            case TableBlock:
                var (min, max) = IntrinsicContentBorderBox(block, style);
                return Math.Max(min, style.ClampWidth(Math.Min(available, max)));
            default:
                return style.ClampWidth(available);
        }
    }

    /// <summary>An image's minimum and maximum content widths from its box's border-box limits (0 for none).</summary>
    private static (double Min, double Max) ImageWidthLimits(BoxStyle style)
    {
        var decoration = style.Border.Widths.Fixed.Horizontal + style.Padding.Fixed.Horizontal;
        return (Math.Max(0, (style.MinWidth?.Fixed ?? 0) - decoration), style.MaxWidth?.Fixed is { } max ? Math.Max(1e-3, max - decoration) : 0);
    }

    /// <summary>
    /// Resolves a block's percentages (<see cref="BoxStyle.HasPercent"/>) against the width of its containing block (for a block in
    /// the flow, its parent's content width), making that its used style (<see cref="StyleOf"/>) until it is resolved
    /// against another width. Returns the used style.
    /// </summary>
    private BoxStyle ResolveStyle(Block block, double containingWidth)
    {
        if (!block.Style.HasPercent)
        {
            return block.Style;
        }

        if (_used.TryGetValue(block, out var used) && used.ContainingWidth == containingWidth)
        {
            return used.Style;
        }

        var style = block.Style.Resolve(containingWidth);
        _used[block] = (containingWidth, style);
        return style;
    }

    /// <summary>
    /// A block's used style: with its percentages resolved against the containing width it was last given (see
    /// <see cref="ResolveStyle"/>), or its style when it has none (or was not given one yet).
    /// </summary>
    private BoxStyle StyleOf(Block block) =>
        block.Style.HasPercent && _used.TryGetValue(block, out var used) ? used.Style : block.Style;

    /// <summary>
    /// The style a block's box is sized with: a collapsed table that holds half of its outer grid lines
    /// (<see cref="TableBlock.OuterLinesInside"/>) has them as its padding and no border of its own (its border takes
    /// part in the grid lines); other blocks keep their style.
    /// </summary>
    private static BoxStyle BoxOf(Block block, BoxStyle style) =>
        block is TableBlock { BorderCollapse: BorderCollapse.Collapse, OuterLinesInside: true } table
            ? style with { Border = Borders.None, BorderRadius = CornerRadii.None, Padding = TableLayouter.OuterHalves(table) }
            : style;

    /// <summary>Moves to the next page, creating it unless a side-by-side flex item already did. It keeps the page name.</summary>
    private void NewPage(bool forced = false) => NewPage(forced, _frames.Count > 0 ? _frames[PageIndex].Context.Name : null, blank: false);

    /// <summary>
    /// Moves to the next page, creating it (with the page name, on the side after the last page's) unless a side-by-side
    /// flex item already did. A blank page gets no repeated content (table headers).
    /// </summary>
    private void NewPage(bool forced, string? name, bool blank)
    {
        if (_columns is not null && _pageIndex >= 0)
        {
            _columns.RecordEnd(_pageIndex, Y); // How far down the column the content reached.
            if (!forced && _pages[_pageIndex].Count == 0)
            {
                _columns.Cramped.Add(_pageIndex); // Left empty: what was to start in it did not fit.
            }
        }

        SetPage(_pageIndex + 1);
        var created = _pageIndex == _pages.Count;
        if (created)
        {
            if (!_static && (_columns?.OuterPage(_pages.Count) ?? _pages.Count) >= _maxPages)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxPages), _maxPages, $"The document needs more than the limit of {_maxPages} pages.");
            }

            ResourceLimits.Checkpoint();
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
    /// The forced break before a block, combining its own <c>break-before</c> with those of its first in-flow child, that
    /// child's first child and so on, which are at the same break point (CSS Fragmentation section 3.1): a left or right
    /// break wins over a plain page break.
    /// </summary>
    private static PageBreak StartBreak(Block block)
    {
        // A loop, not recursion: the chain of first children is as long as the content is deep. The breaks combine from
        // the innermost block outwards.
        var chain = new List<PageBreak>();
        for (Block? current = block; current is not null; current = FirstInFlowChild(current))
        {
            chain.Add(current.Style.BreakBefore);
        }

        var result = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            result = SideOf(result) is not null || !IsForced(chain[i]) ? result : chain[i];
        }

        return result;
    }

    /// <summary>A container's first child that takes up space, or <c>null</c>.</summary>
    private static Block? FirstInFlowChild(Block block)
    {
        if (block is ContainerBlock container)
        {
            foreach (var child in container.Children)
            {
                if (!TakesNoSpace(child))
                {
                    return child;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The page name a block starts with (CSS start page value): its own or its parent's, or, for a container, its first
    /// child's.
    /// </summary>
    private static string? StartName(Block block, string? inherited)
    {
        var name = inherited;
        for (Block? current = block; current is not null; current = FirstInFlowChild(current))
        {
            name = current.PageName ?? name;
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
            var style = ResolveStyle(block, width);

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
                pending.Add(style.Margin.Fixed.Top);
                pending.Add(style.Margin.Fixed.Bottom);
                if (block.Marker is { } emptyMarker && !_static)
                {
                    _markers.Add((PageIndex, emptyMarker));
                }

                if (block.Bookmark is not null || block.Id is not null)
                {
                    _anchors.Add(new Anchor(block, PageIndex, Y, x + style.Margin.Fixed.Left));
                }

                continue;
            }

            // Forced break before this block, or after the previous one, or between pages of different names. A left or
            // right break before the block wins over one after the previous block (CSS Fragmentation section 3.1).
            // In the block flow, a forced break before a first child is taken before its parent, once.
            var previousBreak = i > 0 ? blocks[i - 1].Style.BreakAfter : PageBreak.Auto;
            var startBreak = _blockFlow ? StartBreak(block) : style.BreakBefore;
            var startName = _blockFlow && !_static ? StartName(block, _pageName) : null;
            var renamed = _blockFlow && !_static && !string.Equals(startName, _frames[PageIndex].Context.Name, StringComparison.Ordinal);
            var atBreak = i == 0 && _atForcedBreak;
            _atForcedBreak = false;
            var columnBreak = _columns is not null && (style.BreakBefore == PageBreak.Column || previousBreak == PageBreak.Column);
            if (!_static && (renamed || (!atBreak && (IsForced(startBreak) || IsForced(previousBreak) || columnBreak))))
            {
                var pageBefore = PageIndex;
                ForcedBreak(startName, SideOf(startBreak) ?? SideOf(previousBreak));
                if (PageIndex != pageBefore)
                {
                    pending = default; // The previous block's bottom margin ends with its page; this block keeps its top margin.
                }

                atBreak = true;
            }

            _atForcedBreak = atBreak && block is ContainerBlock;

            var available = fluid ? Math.Max(MinimumContentWidth, width + ContentWidthOf(PageIndex) - entryWidth) : width;
            style = ResolveStyle(block, available);

            if (!(i == 0 && firstTopMarginApplied))
            {
                pending.Add(style.Margin.Fixed.Top);
                pending.Add(CollapseThroughTop(block, available)); // First descendants' top margins collapse with this one.
            }

            ApplyClearance(style.Clear, ref pending);

            // Move to a new page when the start of the block (and, for break-after: avoid, the start of the
            // next block) would not fit here but would fit at the top of a fresh page.
            var needed = MinimumStartHeight(block, available);
            if (AvoidsBreakInside(style))
            {
                needed = Math.Max(needed, MeasureHeight(block, available));
            }

            if (style.BreakAfter == PageBreak.Avoid && next is not null)
            {
                needed = Math.Max(needed, MeasureHeight(block, available) + Math.Max(style.Margin.Fixed.Bottom, ResolveStyle(next, available).Margin.Fixed.Top) + MinimumStartHeight(next, available));
            }

            if (_columns is not null && needed > (AtPageTop ? Remaining : NextPageContentHeight) + Epsilon)
            {
                _columns.Cramped.Add(PageIndex); // Does not fit in a whole column: it will be split where it should not.
            }

            if (!AtPageTop && needed + pending.Value > Remaining + Epsilon && needed <= NextPageContentHeight + Epsilon)
            {
                NewPage();
                if (fluid)
                {
                    available = Math.Max(MinimumContentWidth, width + ContentWidthOf(PageIndex) - entryWidth);
                    style = ResolveStyle(block, available);
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

            var boxX = areaX + style.Margin.Fixed.Left;
            var boxWidth = BoxWidth(block, areaWidth);
            ReportWideBox(block, boxWidth + style.Margin.Fixed.Horizontal, areaWidth);
            var alignment = block switch
            {
                ImageBlock image when style.Width is null => image.Alignment,
                TableBlock table => table.Alignment,
                _ => BlockAlignment.Left,
            };
            if (alignment != BlockAlignment.Left)
            {
                // Images and tables shrink to their content; the alignment then positions the whole box.
                var free = Math.Max(0, areaWidth - style.Margin.Fixed.Horizontal - boxWidth);
                boxX += alignment switch
                {
                    BlockAlignment.Center => free / 2,
                    BlockAlignment.Right => free,
                    _ => 0,
                };
            }

            if (block.Marker is { } marker && !_static)
            {
                _markers.Add((PageIndex, marker));
            }

            _containingWidth = width;
            if (style.ClipOverflow && !_static && block is ContainerBlock && style.Height is { } clippedHeight && clippedHeight <= Remaining + Epsilon)
            {
                // A clipped box of a fixed height that fits here: laid out in one piece, so what it hides makes no pages.
                PlaceClipped(block, boxX, boxWidth, clippedHeight, width);
                pending = default;
                pending.Add(style.Margin.Fixed.Bottom);
                continue;
            }

            var trailing = LayoutBlock(block, boxX, boxWidth);

            pending = trailing;
            pending.Add(style.Margin.Fixed.Bottom);
        }
    }

    /// <summary>
    /// The top margins of a container's first descendants that collapse with its own (CSS 2.1 section 8.3.1):
    /// only when the container has no top border or padding. <paramref name="width"/> is the width the block is laid
    /// out in (its used style is resolved against it); its children's percentages resolve against its content width.
    /// </summary>
    private double CollapseThroughTop(Block block, double width)
    {
        if (block is not ContainerBlock { Children.Count: > 0, HasLegend: false } container || HasTopEdge(StyleOf(block)) || IsFloatRoot(block, default))
        {
            return 0;
        }

        using var nesting = ResourceLimits.EnterNesting(); // Recursive: as deep as the first children are nested.
        var style = StyleOf(block);
        var contentWidth = Math.Max(MinimumContentWidth, BoxWidth(block, width) - style.Border.Widths.Fixed.Horizontal - style.Padding.Fixed.Horizontal);
        var first = container.Children[0];
        var margin = new CollapsedMargin();
        margin.Add(ResolveStyle(first, contentWidth).Margin.Fixed.Top);
        margin.Add(CollapseThroughTop(first, contentWidth));
        return margin.Value;
    }

    private static bool HasTopEdge(BoxStyle style) => style.Border.Top.Width > 0 || style.Padding.Fixed.Top > 0;

    private static bool HasBottomEdge(BoxStyle style) => style.Border.Bottom.Width > 0 || style.Padding.Fixed.Bottom > 0;

    /// <summary>Lays out one block in its opacity scope (see <see cref="LayoutBlock"/>).</summary>
    private CollapsedMargin LayoutBlockInScope(Block block, double x, double width, BoxSizing sizing)
    {
        // The children of a container in the block flow stay in it; those of a container without a width of its own
        // follow the page's content width.
        var (outerName, outerFlow, outerFluid, outerParentFluid) = (_pageName, _blockFlow, _fluid, _parentFluid);
        var inFlow = block is ContainerBlock && !sizing.Isolated;
        _parentFluid = outerFluid;
        _pageName = block.PageName ?? _pageName;
        _blockFlow = outerFlow && inFlow;
        _fluid = outerFluid && inFlow && StyleOf(block) is { Width: null, MaxWidth: null };
        try
        {
            return block.Style.Position == Positioning.Static
                ? LayoutBlockContent(block, x, width, sizing)
                : LayoutPositioned(block, x, width, sizing, () => LayoutBlockContent(block, x, width, sizing));
        }
        finally
        {
            (_pageName, _blockFlow, _fluid, _parentFluid) = (outerName, outerFlow, outerFluid, outerParentFluid);
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
                var tableStyle = table.BorderCollapse == BorderCollapse.Collapse ? BoxOf(table, StyleOf(table)) with { Border = Borders.None, BorderRadius = CornerRadii.None } : StyleOf(table);
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutTable(table, contentX, contentWidth), sizing, tableStyle);
                break;
            case FlexBlock flex:
                LayoutBox(block, x, width, (contentX, contentWidth) => LayoutFlex(flex, contentX, contentWidth, sizing.DefiniteHeight), sizing);
                break;
            case ContainerBlock container:
                // A new formatting context (a flex item, a float, a flow root) keeps its children's margins and floats
                // inside it.
                var isolated = IsFloatRoot(block, sizing);
                var legend = LegendOf(container, x, width);
                LayoutBox(block, x, width, (contentX, contentWidth) =>
                {
                    var children = container.Children;
                    if (legend is { } placed)
                    {
                        LayoutLegend(container.Children[0], placed, Y - placed.Area - StyleOf(block).Padding.Fixed.Top);
                        children = [.. children.Skip(1)];
                    }

                    void Children()
                    {
                        if (InColumns(container))
                        {
                            LayoutColumns(container, children, contentX, contentWidth, sizing);
                            return;
                        }

                        var pending = default(CollapsedMargin);
                        LayoutChildren(children, contentX, contentWidth, ref pending, firstTopMarginApplied: !HasTopEdge(StyleOf(block)) && !isolated && legend is null);
                        if (HasBottomEdge(StyleOf(block)) || isolated)
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
                }, sizing, legend: legend);
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
    private void LayoutBox(
        Block block, double x, double width, Action<double, double> layoutContent, BoxSizing sizing = default, BoxStyle? styleOverride = null, LegendBox? legend = null)
    {
        var style = styleOverride ?? StyleOf(block);
        var border = style.Border.Widths.Fixed;
        var decorated = style.Background is not null || style.BackgroundLayers.Count > 0 || border.Vertical > 0 || border.Horizontal > 0 || style.Shadows.Count > 0;
        var link = block.Link;

        var startPage = PageIndex;
        var startY = Y;
        var insertAt = _pages[startPage].Count;

        // A box that follows the page's content width is as much wider or narrower on each page as that page.
        var fluid = _fluid && !_static;
        var startWidth = ContentWidthOf(startPage);

        // A box that clips its overflow draws its content in a clip scope; its own background and borders stay outside.
        var outerScope = _scope;
        var clip = style.ClipOverflow ? new ClipRegion(_shift) : null;
        if (clip is not null)
        {
            _scope = new DrawScope(_scope, 1, clip);
        }

        try
        {
            Y += (legend?.Area ?? border.Top) + style.Padding.Fixed.Top;
            layoutContent(x + border.Left + style.Padding.Fixed.Left, Math.Max(MinimumContentWidth, width - border.Horizontal - style.Padding.Fixed.Horizontal));
            Y += style.Padding.Fixed.Bottom + border.Bottom;
        }
        finally
        {
            _scope = outerScope;
        }

        // CSS height, min-height and max-height; a flex item's size from its container overrides height.
        var contentEnd = Y;
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
                Y += target - total;
            }
            else if (clip is not null && target < total - Epsilon)
            {
                // A clipping box ends at its height, not below the content it hides; what follows goes on from there.
                MoveToHeightBelow(startPage, startY, target);
            }
        }

        // A height (beyond the content's) that reaches below the page continues on the next pages: the box is split.
        while (!_static && Y > ContentBottom + Epsilon && Y > contentEnd + Epsilon)
        {
            var below = Y - ContentBottom;
            NewPage();
            Y = PageTop(PageIndex) + below;
            contentEnd = PageTop(PageIndex);
        }

        if (sizing.StretchTo is { } line)
        {
            if (decorated || link is not null)
            {
                line.Pending.Add(InCurrentDrawScope(() => Decorate(line.Page, line.Y - sizing.StretchInset)));
            }

            if (clip is not null)
            {
                line.Pending.Add(() => RecordClip(clip, line.Page, line.Y - sizing.StretchInset));
            }

            return;
        }

        if (decorated || link is not null)
        {
            Decorate(PageIndex, Y);
        }

        if (clip is not null)
        {
            RecordClip(clip, PageIndex, Y);
        }

        // Where the clipped content shows: the box's fragment on each page.
        void RecordClip(ClipRegion region, int endPage, double endY)
        {
            var wholeHeight = 0.0;
            for (var page = startPage; page <= endPage; page++)
            {
                wholeHeight += (page == endPage ? endY : PageBottom(page)) - (page == startPage ? startY : PageTop(page));
            }

            var corners = style.BorderRadius.IsSquare ? default : style.BorderRadius.Resolve(width, wholeHeight);
            for (var page = startPage; page <= endPage; page++)
            {
                var (first, last) = (page == startPage, page == endPage);
                var top = first ? startY : PageTop(page);
                var bottom = last ? endY : PageBottom(page);
                var fragmentWidth = fluid ? Math.Max(0, width + ContentWidthOf(page) - startWidth) : width;
                region.Fragments[page] = new ClipFragment(
                    style, corners.Fragment(first, last, fragmentWidth, bottom - top), x, top, fragmentWidth, bottom - top, first, last);
            }
        }

        // Backgrounds and borders of each page fragment, and the link over it.
        void Decorate(int endPage, double endY)
        {
            // Rounded corners and background layers are sized for the whole box; each fragment keeps the part it has.
            var wholeHeight = 0.0;
            for (var page = startPage; page <= endPage; page++)
            {
                wholeHeight += (page == endPage ? endY : PageBottom(page)) - (page == startPage ? startY : PageTop(page));
            }

            wholeHeight -= legend?.Shift ?? 0;
            var corners = decorated && !style.BorderRadius.IsSquare ? style.BorderRadius.Resolve(width, wholeHeight) : default;
            var offset = 0.0;

            for (var page = startPage; page <= endPage; page++)
            {
                var first = page == startPage;
                var last = page == endPage;
                var top = first ? startY : PageTop(page);
                var bottom = last ? endY : PageBottom(page);
                var fragmentWidth = fluid ? Math.Max(0, width + ContentWidthOf(page) - startWidth) : width;

                // Beside a legend, the border and background start where the border is drawn, centered on the legend.
                var shift = first && legend is { } cut ? Math.Min(cut.Shift, bottom - top) : 0;
                if (decorated)
                {
                    var index = first ? insertAt : 0; // Pages created inside the box: under everything on them.
                    var fragmentOffset = offset;
                    var cutout = first && legend is { } placed ? new LegendCutout(placed.X, placed.Width, top, Math.Max(placed.Height, style.Border.Top.Width)) : (LegendCutout?)null;
                    var fragment = corners.Fragment(first, last, fragmentWidth, bottom - top - shift);
                    _pages[page].Insert(index, (canvas, pageHeight) => LayoutTagging.Decoration(
                        canvas, () => DrawBox(canvas, pageHeight, style, x, top + shift, fragmentWidth, bottom - top - shift, first, last, fragment, (fragmentOffset, wholeHeight), cutout)));
                    _placements.Add(new Placement(page, block, top, bottom, -1, -1, x, fragmentWidth));
                }

                offset += bottom - top - shift;

                if (link is not null)
                {
                    _pages[page].Add((canvas, pageHeight) => link.AddTo(canvas, x, pageHeight - bottom, fragmentWidth, bottom - top));
                }
            }
        }
    }

    /// <summary>Moves the position to <paramref name="height"/> below a point, across the pages after it.</summary>
    private void MoveToHeightBelow(int page, double y, double height)
    {
        while (y + height > PageBottom(page) + Epsilon && page + 1 < _pages.Count)
        {
            height -= PageBottom(page) - y;
            page++;
            y = PageTop(page);
        }

        SetPage(page);
        Y = y + height;
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
    /// fragment; <c>null</c> sizes the style's radii to the fragment. <paramref name="whole"/> places a fragment in its
    /// whole box (how far below the box's top it starts, and the box's height) for the background layers; <c>null</c> is
    /// a whole box.
    /// </summary>
    private static void DrawBox(
        PdfCanvas canvas, double pageHeight, BoxStyle style, double x, double top, double width, double height, bool first, bool last,
        RoundedCorners? corners = null, (double Offset, double Height)? whole = null, LegendCutout? cutout = null)
    {
        var y = pageHeight - top - height; // PDF y of the bottom edge.
        var rounded = corners ?? (style.BorderRadius.IsSquare ? default : style.BorderRadius.Resolve(width, height).Fragment(first, last, width, height));
        if (style.Shadows.Count > 0)
        {
            ShadowPainter.PaintOuter(canvas, style.Shadows, x, y, width, height, rounded);
        }
        if (style.Background is { } background)
        {
            canvas.SetFillColor(background);
            if (AddBackgroundArea(canvas, style, rounded, x, y, width, height, first, last))
            {
                canvas.Fill();
            }
        }

        if (style.BackgroundLayers.Count > 0)
        {
            var (offset, wholeHeight) = whole ?? (0, height);
            PaintBackgroundLayers(canvas, style, rounded, x, y, width, height, first, last, offset, wholeHeight);
        }

        if (style.Shadows.Count > 0)
        {
            // Inset shadows: inside the padding box, over the background.
            var border = style.Border.Widths.Fixed;
            var (insetTop, insetBottom) = (first ? border.Top : 0, last ? border.Bottom : 0);
            ShadowPainter.PaintInset(canvas, style.Shadows, x + border.Left, y + insetBottom, width - border.Horizontal, height - insetTop - insetBottom,
                rounded.Inset(insetTop, border.Right, insetBottom, border.Left));
        }

        if (cutout is { } legend)
        {
            // The border is cut where the legend is: everything but the legend's column of the top border band.
            canvas.SaveState();
            canvas.Rectangle(x - 1, y - 1, width + 2, height + 2)
                .Rectangle(legend.X, pageHeight - legend.Top - legend.Height, legend.Width, legend.Height)
                .Clip(evenOdd: true)
                .EndPath();
        }

        BorderPainter.PaintBox(canvas, style.Border, x, y, width, height, top: first, bottom: last, corners: rounded);
        if (cutout is not null)
        {
            canvas.RestoreState();
        }
    }

    /// <summary>
    /// Adds the area a box's background covers (CSS <c>background-clip</c>) as a path: the border box, or the padding or
    /// content box (inset on the edges the fragment has), with the corners reduced by the inset. False when it is empty.
    /// </summary>
    private static bool AddBackgroundArea(PdfCanvas canvas, BoxStyle style, RoundedCorners corners, double x, double y, double width, double height, bool first, bool last)
    {
        var inset = style.BackgroundClip switch
        {
            BackgroundClip.PaddingBox => style.Border.Widths.Fixed,
            BackgroundClip.ContentBox => new Sides(
                style.Border.Top.Width + style.Padding.Fixed.Top, style.Border.Right.Width + style.Padding.Fixed.Right,
                style.Border.Bottom.Width + style.Padding.Fixed.Bottom, style.Border.Left.Width + style.Padding.Fixed.Left),
            _ => default(Sides),
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
        var (w, h) = block.GetSize(width, ImageMaxHeight(block), ImageWidthLimits(StyleOf(block)));
        ReportScaledImage(block, w, h);
        if (h > Remaining + Epsilon && !AtPageTop)
        {
            NewPage(); // Normally avoided by MinimumStartHeight; happens only inside a tall box.
        }

        var left = x + block.Alignment switch
        {
            BlockAlignment.Center => Math.Max(0, (width - w) / 2),
            BlockAlignment.Right => Math.Max(0, width - w),
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
            var (border, padding) = (block.Style.Border.Widths.Fixed, StyleOf(block).Padding.Fixed);
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

    /// <summary>
    /// Reports an image scaled down to fit on one page: its height is the page's limit, and its natural (or given) size
    /// is taller. Its box may already have been narrowed for that (shrink-to-fit), so the size is taken without width.
    /// </summary>
    private void ReportScaledImage(ImageBlock block, double w, double h)
    {
        var max = ImageMaxHeight(block);
        if (_diagnostics is null || _static || h < max - OverflowTolerance
            || block.GetSize(double.PositiveInfinity, double.PositiveInfinity, ImageWidthLimits(StyleOf(block))).Height is var full && full <= h + OverflowTolerance)
        {
            return;
        }

        Report(LayoutDiagnosticKind.ScaledToFit, block, () => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The image is {full:0.#} pt tall, more than a page ({max:0.#} pt): it is scaled down to {w:0.#} x {h:0.#} pt to fit on one (a browser would cut it at the page's bottom)."));
    }

    /// <summary>An image is scaled down to fit on one page together with its own border and padding.</summary>
    private double ImageMaxHeight(Block block) =>
        Math.Max(1, (_columns?.FullHeight(PageIndex) ?? ContentHeight) - block.Style.Border.Widths.Fixed.Vertical - StyleOf(block).Padding.Fixed.Vertical);

    private void LayoutParagraph(ParagraphBlock block, double x, double width)
    {
        // Lines beside floats get the room the floats leave where the lines are. So after each page break the lines that
        // are left are laid out again from where they now start: the lines already placed keep the room they had (and so
        // stay the same), the others get the room on the new page.
        // In columns of different widths, the lines in each column get its width (when the paragraph's width follows them).
        var rooms = new List<(double Top, double Offset, double Width)>();
        var from = 0.0; // Where the current page's lines start, from the paragraph's top.
        var (startWidth, reflow) = (ContentWidthOf(PageIndex), _columns is { Uneven: true } && _parentFluid);
        double Room() => reflow ? Math.Max(MinimumContentWidth, width + ContentWidthOf(PageIndex) - startWidth) : width;
        ParagraphLayout LayoutFrom(int keep) => reflow || rooms.Count > 0 || HasFloatsBelow(Y)
            ? block.Paragraph.Layout(width, LineRooms(x, Room(), Y - from, keep, rooms))
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
            ResourceLimits.Checkpoint();
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
                _columns?.Cramped.Add(PageIndex);
                if (fit == 0)
                {
                    var tall = lines[i].Height;
                    Report(LayoutDiagnosticKind.Clipped, block, () => string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"A line is {tall:0.#} pt tall, more than a whole page ({Remaining:0.#} pt): it is cut at the bottom of the page."));
                }
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
        ReportOverflowingLines(block, layout, first, end);
        _pages[PageIndex].Add((canvas, pageHeight) => layout.Draw(canvas, x, pageHeight - top, first, end));
        Y += layout.Lines[end - 1].Top + layout.Lines[end - 1].Height - layout.Lines[first].Top;
        _placements.Add(new Placement(PageIndex, block, top, Y, first, end, x, layout.AvailableWidth));
        AddParagraphAnchors(block, layout, x, top, first, end);
    }

    /// <summary>
    /// Reports lines [first, end) that are wider than the paragraph's box (an unbreakable word, image or inline box), unless
    /// a box around them clips its overflow on purpose.
    /// </summary>
    private void ReportOverflowingLines(ParagraphBlock block, ParagraphLayout layout, int first, int end)
    {
        if (_diagnostics is null || InClippingBox())
        {
            return;
        }

        var widest = 0.0;
        for (var i = first; i < end; i++)
        {
            widest = Math.Max(widest, layout.Lines[i].X + layout.Lines[i].Width - layout.Lines[i].RightOverhang);
        }

        var excess = widest - layout.AvailableWidth;
        if (excess > OverflowTolerance)
        {
            Report(LayoutDiagnosticKind.Overflow, block, () => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A line is {excess:0.#} pt wider than its box ({layout.AvailableWidth:0.#} pt): a word, image or inline box that cannot be broken runs past the box's edge."));
        }
    }

    /// <summary>
    /// Reports a block whose border box (with its margins) is wider than the room it has: a table whose columns cannot be
    /// narrower than their content, or a box, image or table with a fixed width.
    /// </summary>
    private void ReportWideBox(Block block, double outerWidth, double available)
    {
        var excess = outerWidth - available;
        if (_diagnostics is null || excess <= OverflowTolerance || available <= MinimumContentWidth || InClippingBox())
        {
            return;
        }

        var what = block switch
        {
            TableBlock when StyleOf(block).Width is null => "The table's columns cannot be narrower than their content: it",
            TableBlock => "The table",
            ImageBlock => "The image",
            _ => "The box",
        };
        Report(LayoutDiagnosticKind.Overflow, block, () => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{what} is {excess:0.#} pt wider than the {available:0.#} pt it has, and runs past its container's edge."));
    }

    /// <summary>How far content may run past its box before it counts as overflowing (rounding, hanging punctuation).</summary>
    private const double OverflowTolerance = 0.5;

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
                    _anchors.Add(new Anchor(block, PageIndex, top + line.Top - lines[first].Top, x + line.X + AnchorX(line, anchor.Index)) { Name = anchor.Id });
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Where a position of a paragraph's text is on its line, from the start of the line's content: the start of the
    /// text or image that begins there (the right edge of right-to-left text), or the end of the line. Anchors are
    /// between spans, which are never shaped together, so a position is where a fragment starts.
    /// </summary>
    private static double AnchorX(LineLayout line, int index)
    {
        double? next = null;
        var nextStart = int.MaxValue;
        foreach (var fragment in line.Fragments)
        {
            if (fragment.Range.Start >= index && fragment.Range.Start < nextStart)
            {
                next = fragment.Run.Direction == TextDirection.RightToLeft ? fragment.X + fragment.Width : fragment.X;
                nextStart = fragment.Range.Start;
            }
        }

        foreach (var image in line.Images)
        {
            if (image.Start >= index && image.Start < nextStart)
            {
                (next, nextStart) = (image.X, image.Start);
            }
        }

        return next ?? line.Width;
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
        if (_diagnostics is not null && layout.ColumnX[^1] - width is var columnsExcess && columnsExcess > OverflowTolerance && !InClippingBox())
        {
            // A table with a fixed width keeps it; its columns, which cannot be narrower than their content, overflow it.
            Report(LayoutDiagnosticKind.Overflow, table, () => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The table's columns cannot be narrower than their content: they need {columnsExcess:0.#} pt more than its {width:0.#} pt width, and run past its edge."));
        }

        var headerEnd = layout.HeaderRows;
        var footerStart = table.Rows.Count - layout.FooterRows;
        var anchoredCells = new HashSet<CellLayout>(ReferenceEqualityComparer.Instance);
        List<PlacedRow>? placed = table.Overlay is null ? null : [];

        // Header rows are content where the table starts, and artifacts where they repeat.
        var headerPlaced = false;
        void Place(int start, int end)
        {
            var top = Y;
            var repeated = start == 0 && end == headerEnd && headerPlaced;
            headerPlaced |= start == 0 && end == headerEnd;
            _pages[PageIndex].Add((canvas, pageHeight) => DrawRows(canvas, pageHeight, layout, x, top, start, end, repeated));
            AddCellAnchors(layout, top, start, end, anchoredCells);
            for (var row = start; row < end && placed is not null; row++)
            {
                var rowTop = top + layout.Height(start, row) + (row > start ? layout.SpacingY : 0);
                placed.Add(new PlacedRow(row, PageIndex, rowTop, layout.RowHeights[row], repeated));
            }

            Y += layout.Height(start, end);
            _placements.Add(new Placement(PageIndex, table, top, Y, start, end, x, width));
            Y += layout.SpacingY;
        }

        // The table's spacing and header rows start it, and every page it continues on (also pages that a split row's
        // cells create). A header repeats only where it leaves room for the start of the body: one taller than that (as
        // spreadsheet print titles can be) would fill every page without the table getting any further (#249). Like
        // browsers, the table then shows it once, where it starts.
        var headerHeight = headerEnd > 0 ? layout.Height(0, headerEnd) : 0;
        var bodyStart = headerEnd < footerStart ? MinimumRowStart(layout, headerEnd) : 0;
        var repeatHeader = true;
        void StartPage()
        {
            Y += layout.SpacingY;
            if (headerEnd == 0)
            {
                return;
            }

            if (headerPlaced)
            {
                repeatHeader &= Y + headerHeight + layout.SpacingY + bodyStart <= ContentBottom + Epsilon;
                if (!repeatHeader)
                {
                    return;
                }
            }

            Place(0, headerEnd);
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
                ResourceLimits.Checkpoint();
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
                    SplitRows(layout, groupStart, end, x, width, placed);
                    continue;
                }

                if (!AtPageTop)
                {
                    NewPage(); // Repeats the header rows.
                }

                // Taller than a whole page: split even a row (or rows joined by row spans) that asked to stay whole.
                if (Y + height <= ContentBottom + Epsilon || _static || !CanSplit(layout, groupStart, end, ignoreAvoid: true))
                {
                    Place(groupStart, end);
                }
                else
                {
                    SplitRows(layout, groupStart, end, x, width, placed);
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
            DrawOverlay(table, layout, x, placed);
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
                _pages[page].Add((canvas, pageHeight) => DrawRows(canvas, pageHeight, layout, x, top, footerStart, table.Rows.Count, repeated: true));
                _placements.Add(new Placement(page, table, top, top + footerHeight, footerStart, table.Rows.Count, x, width));
                for (var row = footerStart; row < table.Rows.Count && placed is not null; row++)
                {
                    placed.Add(new PlacedRow(row, page, top + layout.Height(footerStart, row) + (row > footerStart ? layout.SpacingY : 0), layout.RowHeights[row], true));
                }
            }
        }

        Place(footerStart, table.Rows.Count);
        DrawOverlay(table, layout, x, placed);
    }

    /// <summary>
    /// Adds what a table's overlay (if it has one) draws to its pages, given where its rows went, over the flow content,
    /// as an artifact.
    /// </summary>
    private void DrawOverlay(TableBlock table, TableLayout layout, double x, List<PlacedRow>? placed)
    {
        if (table.Overlay is not { } overlay || placed is null)
        {
            return;
        }

        var placement = new TablePlacement([.. layout.ColumnX.Select(c => x + c)], placed);
        var outer = _layer;
        _layer = new PaintLayer(PaintLayer.Positioned, 0);
        try
        {
            foreach (var (page, draw) in overlay(placement))
            {
                if (page >= 0 && page < _pages.Count)
                {
                    _pages[page].Add((canvas, pageHeight) => LayoutTagging.Decoration(canvas, () => draw(canvas, pageHeight)));
                }
            }
        }
        finally
        {
            _layer = outer;
        }
    }

    /// <summary>
    /// Draws rows [start, end) of a table with their cells' content. In a tagged document the cells' content belongs to
    /// their elements and the backgrounds and borders are artifacts; <paramref name="repeated"/> rows (header rows on
    /// later pages, footer rows before the last) are artifacts as a whole.
    /// </summary>
    private static void DrawRows(PdfCanvas canvas, double pageHeight, TableLayout layout, double x, double top, int start, int end, bool repeated = false)
    {
        if (repeated)
        {
            LayoutTagging.BeginArtifact(canvas, PdfArtifactKind.Pagination);
        }

        DrawRowsTagged(canvas, pageHeight, layout, x, top, start, end);
        if (repeated)
        {
            LayoutTagging.End(canvas);
        }
    }

    private static void DrawRowsTagged(PdfCanvas canvas, double pageHeight, TableLayout layout, double x, double top, int start, int end)
    {
        if (end <= start)
        {
            return; // No rows (fuzzing found a header whose rows span into the footer, leaving none to repeat).
        }

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

        LayoutTagging.BeginArtifact(canvas);
        for (var r = start; r < end; r++)
        {
            if (table.Rows[r].Background is { } rowBackground)
            {
                canvas.SetFillColor(rowBackground).Rectangle(left, pageHeight - TopOf(r) - layout.RowHeights[r], right - left, layout.RowHeights[r]).Fill();
            }
        }

        LayoutTagging.End(canvas);
        foreach (var cell in layout.CellsStartingIn(start, end))
        {
            LayoutTagging.BeginArtifact(canvas);
            var cellTop = TopOf(cell.Grid.Row);
            var cellHeight = layout.Height(cell.Grid.Row, cell.Grid.Row + cell.Grid.RowSpan);
            var style = cell.Grid.Cell.Style;
            if (collapse)
            {
                if (style.Background is { } background)
                {
                    canvas.SetFillColor(background).Rectangle(x + cell.X, pageHeight - cellTop - cellHeight, cell.Width, cellHeight).Fill();
                }

                if (style.BackgroundLayers.Count > 0)
                {
                    // Images and gradients over the color (a spreadsheet's data bars, CSS backgrounds of collapsed cells).
                    PaintBackgroundLayers(canvas, style, default, x + cell.X, pageHeight - cellTop - cellHeight, cell.Width, cellHeight, true, true, 0, cellHeight);
                }
            }
            else
            {
                DrawBox(canvas, pageHeight, style, x + cell.X, cellTop, cell.Width, cellHeight, first: true, last: true);
            }

            LayoutTagging.End(canvas);
            var contentTop = cellTop + cell.InsetTop + ContentOffset(cell, cellHeight);
            LayoutTagging.Begin(canvas, cell.Grid.Cell);
            foreach (var draw in cell.Content)
            {
                draw(canvas, pageHeight - contentTop);
            }

            LayoutTagging.End(canvas);
        }

        if (collapse && layout.Horizontal is { } horizontal && layout.Vertical is { } vertical)
        {
            LayoutTagging.Decoration(canvas, () => DrawCollapsedLines(canvas, pageHeight, layout, horizontal, vertical, x, start, end, TopOf));
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
        foreach (var cell in layout.AnchoredCells) // Not every cell: this runs for every page a table is on.
        {
            if (cell.Grid.Row < start || cell.Grid.Row >= end || !anchored.Add(cell))
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
    private double MeasureHeight(Block block, double width)
    {
        ResolveStyle(block, width);
        return MeasureBox(block, BoxWidth(block, width));
    }

    /// <summary>Height of a block's border box at a given border-box width, if laid out without page breaks.</summary>
    private double MeasureBox(Block block, double boxWidth) => ApplyHeights(block, MeasureContentBox(block, boxWidth));

    /// <summary>Height of a block's border box from its content alone, ignoring its height properties.</summary>
    private double MeasureContentBox(Block block, double boxWidth)
    {
        using var nesting = ResourceLimits.EnterNesting();
        ResourceLimits.Checkpoint();
        var style = BoxOf(block, StyleOf(block));
        var decoration = style.Border.Widths.Fixed.Vertical + style.Padding.Fixed.Vertical;
        var contentWidth = Math.Max(MinimumContentWidth, boxWidth - style.Border.Widths.Fixed.Horizontal - style.Padding.Fixed.Horizontal);
        switch (block)
        {
            case SpacerBlock spacer:
                return spacer.Height;
            case ParagraphBlock paragraph:
                return decoration + paragraph.Paragraph.Layout(contentWidth).Height;
            case ImageBlock image:
                return decoration + image.GetSize(contentWidth, ImageMaxHeight(image), ImageWidthLimits(style)).Height;
            case TableBlock table:
                var tableLayout = TableLayouter.Layout(table, 0, contentWidth);
                return decoration + tableLayout.Height(0, table.Rows.Count) + (2 * tableLayout.SpacingY);
            case FlexBlock flex:
                return decoration + MeasureFlex(flex, contentWidth);
            case ContainerBlock multicol when InColumns(multicol):
                return decoration + LegendExtra(block, boxWidth) + MeasureColumns(multicol, multicol.HasLegend ? [.. multicol.Children.Skip(1)] : multicol.Children, contentWidth);
            case ContainerBlock container:
                var height = LegendExtra(block, boxWidth);
                var margin = new CollapsedMargin();
                foreach (var child in container.HasLegend ? container.Children.Skip(1) : container.Children)
                {
                    var childStyle = ResolveStyle(child, contentWidth);
                    margin.Add(childStyle.Margin.Fixed.Top);
                    height += margin.Value + MeasureHeight(child, contentWidth);
                    margin = new CollapsedMargin();
                    margin.Add(childStyle.Margin.Fixed.Bottom);
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
    private double MinimumStartHeight(Block block, double width)
    {
        ResolveStyle(block, width);
        return MinimumStartBox(block, BoxWidth(block, width));
    }

    /// <summary>The smallest height a block of a given border-box width needs at the bottom of a page.</summary>
    private double MinimumStartBox(Block block, double boxWidth)
    {
        using var nesting = ResourceLimits.EnterNesting();
        ResourceLimits.Checkpoint();
        var style = BoxOf(block, StyleOf(block));
        if (style.Height is not null && block is not TableBlock)
        {
            return MeasureBox(block, boxWidth); // A fixed-height box is kept on one page when it fits on one.
        }

        var top = style.Border.Top.Width + style.Padding.Fixed.Top + LegendExtra(block, boxWidth);
        var contentWidth = Math.Max(MinimumContentWidth, boxWidth - style.Border.Widths.Fixed.Horizontal - style.Padding.Fixed.Horizontal);
        switch (block)
        {
            case ParagraphBlock paragraph:
                // The first lines it may not be split from (orphans); all of them if orphans and widows leave no break.
                var lines = paragraph.Paragraph.Layout(contentWidth).Lines;
                var orphans = Math.Max(1, style.Orphans);
                var count = lines.Count < orphans + Math.Max(1, style.Widows) ? lines.Count : orphans;
                var linesHeight = lines.Take(count).Sum(l => l.Height);
                return top + (count == lines.Count ? linesHeight + style.Padding.Fixed.Bottom + style.Border.Bottom.Width : linesHeight);
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

                return top + tableLayout.SpacingY + tableLayout.Height(0, firstEnd) + footer + (firstEnd == table.Rows.Count ? tableLayout.SpacingY + style.Padding.Fixed.Bottom + style.Border.Bottom.Width : 0);
            case ContainerBlock container when container.Children.Count > (container.HasLegend ? 1 : 0):
                var firstChild = container.Children[container.HasLegend ? 1 : 0];
                return top + ResolveStyle(firstChild, contentWidth).Margin.Fixed.Top + MinimumStartHeight(firstChild, contentWidth);
            case SpacerBlock:
                return 0;
            default:
                return top + style.Padding.Fixed.Bottom + style.Border.Bottom.Width;
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
