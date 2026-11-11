namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Page size and margins.</summary>
/// <param name="Size">The page size.</param>
/// <param name="Margins">Space between the page edge and the content area.</param>
public sealed record PageSetup(PdfPageSize Size, Thickness Margins)
{
    /// <summary>A4 with 2 cm margins.</summary>
    public static PageSetup A4 { get; } = new(PdfPageSize.A4, Thickness.All(56.69));

    /// <summary>The content area's width.</summary>
    public double ContentWidth => Size.Width - Margins.Horizontal;

    /// <summary>The content area's height.</summary>
    public double ContentHeight => Size.Height - Margins.Vertical;
}

/// <summary>Which side of a spread a page is on. In left-to-right documents the first page is a right page.</summary>
public enum PageSide
{
    /// <summary>A right (recto) page: odd page numbers, unless the document starts on a left page.</summary>
    Right,

    /// <summary>A left (verso) page.</summary>
    Left,
}

/// <summary>
/// What a page's setup is chosen by (see <see cref="FlowDocument.PageSetupResolver"/>), like the CSS page selectors
/// <c>@page name:first:left:blank</c>.
/// </summary>
/// <param name="Number">1-based page number.</param>
/// <param name="Name">
/// The page name (CSS <c>page</c>): the <see cref="Block.PageName"/> of the content the page starts with, inherited from
/// the parent blocks; <c>null</c> for unnamed pages.
/// </param>
/// <param name="Side">Whether it is a left or a right page.</param>
/// <param name="IsBlank">
/// The page was inserted empty by a <see cref="PageBreak.Left"/> or <see cref="PageBreak.Right"/> break, to make the
/// content after it start on the requested side.
/// </param>
public readonly record struct PageContext(int Number, string? Name, PageSide Side, bool IsBlank)
{
    /// <summary>The first page of the document (CSS <c>:first</c>).</summary>
    public bool IsFirst => Number == 1;
}

/// <summary>Information passed to a page decorator (header/footer callback).</summary>
/// <param name="Number">1-based page number.</param>
/// <param name="Count">Total number of pages.</param>
/// <param name="Setup">The page's setup (its own size and margins, see <see cref="FlowDocument.PageSetupResolver"/>).</param>
/// <param name="Markers">Markers (<see cref="Block.Marker"/>) of the blocks that start on this page, in document order.</param>
public readonly record struct PageInfo(int Number, int Count, PageSetup Setup, IReadOnlyList<object> Markers)
{
    /// <summary>The page name (see <see cref="PageContext.Name"/>); <c>null</c> for unnamed pages.</summary>
    public string? Name { get; init; }

    /// <summary>Whether it is a left or a right page.</summary>
    public PageSide Side { get; init; }

    /// <summary>The page was inserted empty by a left or right page break (see <see cref="PageContext.IsBlank"/>).</summary>
    public bool IsBlank { get; init; }

    /// <summary>What the page's setup was chosen by.</summary>
    public PageContext Context => new(Number, Name, Side, IsBlank);
}

/// <summary>The pages a <see cref="FlowDocument"/> was laid out on (see <see cref="FlowDocument.MapPages"/>).</summary>
/// <param name="Pages">Each page, in order (<see cref="PageContext.Number"/> is 1-based).</param>
/// <param name="Destinations">The page number (1-based) of each named destination.</param>
public sealed record PageMap(IReadOnlyList<PageContext> Pages, IReadOnlyDictionary<string, int> Destinations);

/// <summary>
/// A sequence of blocks that flows across as many pages as needed.
/// </summary>
/// <example>
/// <code>
/// var flow = new FlowDocument(PageSetup.A4)
///     .Add(new ParagraphBlock(heading, new BoxStyle { BreakAfter = PageBreak.Avoid, Margin = new(0, 0, 8, 0) }))
///     .Add(new ParagraphBlock(body));
/// flow.PageDecorator = (canvas, page) => canvas.DrawText($"Page {page.Number} of {page.Count}", ...);
/// flow.Render().Save("out.pdf");
/// </code>
/// </example>
public sealed class FlowDocument
{
    private readonly List<Block> _blocks = [];

    /// <summary>Creates a document with a page setup.</summary>
    public FlowDocument(PageSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (setup.ContentWidth <= 0 || setup.ContentHeight <= 0)
        {
            throw new ArgumentException("The page margins leave no room for content.", nameof(setup));
        }

        Setup = setup;
    }

    /// <summary>The page setup: of every page, unless <see cref="PageSetupResolver"/> chooses one per page.</summary>
    public PageSetup Setup { get; }

    /// <summary>
    /// Chooses each page's size and margins (CSS named pages and <c>@page :first</c>/<c>:left</c>/<c>:right</c>/<c>:blank</c>),
    /// e.g. landscape pages for wide tables or mirrored margins; <c>null</c> (the default) gives every page
    /// <see cref="Setup"/>. Called once per distinct <see cref="PageContext"/> while laying out, also to look ahead at the
    /// next page. Content on a page with a different content width than <see cref="Setup"/> is laid out at that page's
    /// width when it starts there; a block continuing from a page of another width keeps the width it started with.
    /// </summary>
    /// <example>
    /// <code>
    /// flow.PageSetupResolver = page => page.Name == "wide"
    ///     ? new PageSetup(PdfPageSize.A4.Landscape, Thickness.All(36))
    ///     : page.Side == PageSide.Left ? leftSetup : rightSetup;
    /// flow.Add(new ContainerBlock { PageName = "wide" }.Add(table)); // Starts on a new, landscape page.
    /// </code>
    /// </example>
    public Func<PageContext, PageSetup>? PageSetupResolver { get; set; }

    /// <summary>The top-level blocks.</summary>
    public IReadOnlyList<Block> Blocks => _blocks;

    /// <summary>
    /// Called for every page after the content has been laid out, e.g. to draw headers, footers and
    /// "page X of Y". It draws on top of the page content.
    /// </summary>
    public Action<PdfCanvas, PageInfo>? PageDecorator { get; set; }

    /// <summary>Appends a block.</summary>
    public FlowDocument Add(Block block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _blocks.Add(block);
        return this;
    }

    /// <summary>Lays out the blocks and returns the number of pages they need.</summary>
    public int CountPages() => Paginator.Paginate(this).Pages.Count;

    /// <summary>
    /// Lays out the blocks and returns the pages they make (with their names, sides and blank pages) and the page of
    /// each named destination: the blocks' ids (<see cref="Block.Id"/>) and the paragraphs' anchors
    /// (<see cref="Paragraph.AddAnchor"/>), the first of each name winning as in <see cref="RenderTo"/>. Useful for
    /// cross-references such as the page numbers of a table of contents.
    /// </summary>
    public PageMap MapPages()
    {
        var result = Paginator.Paginate(this);
        var destinations = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var anchor in result.Anchors)
        {
            if (anchor.DestinationName is { } name)
            {
                destinations.TryAdd(name, anchor.Page + 1);
            }
        }

        return new PageMap([.. result.Frames.Select(f => f.Context)], destinations);
    }

    /// <summary>Pagination details, for tests and diagnostics.</summary>
    internal PaginationResult Paginate() => Paginator.Paginate(this);

    /// <summary>Lays out the blocks into a new <see cref="PdfDocument"/>.</summary>
    public PdfDocument Render()
    {
        var document = new PdfDocument();
        RenderTo(document);
        return document;
    }

    /// <summary>
    /// Lays out the blocks and appends the pages to <paramref name="document"/>. Blocks with a
    /// <see cref="Block.Bookmark"/> are appended to its <see cref="PdfDocument.Outline"/>, nested by level; blocks with
    /// an <see cref="Block.Id"/> and paragraph anchors (<see cref="Paragraph.AddAnchor"/>) become its
    /// <see cref="PdfDocument.NamedDestinations"/>, which links (<see cref="LinkTarget.ToDestination"/>) jump to.
    /// </summary>
    public void RenderTo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = Paginator.Paginate(this);
        if (document.Tagged)
        {
            StructureBuilder.Build(Blocks, document.StructureRoot, document.LayoutStructure);
        }

        var pages = result.Pages;
        var rendered = new List<PdfPage>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            var (context, setup) = result.Frames[i];
            var page = document.AddPage(setup.Size);
            rendered.Add(page);

            // Content is laid out from the left margin of the document's setup; pages with another left margin shift it.
            var shift = setup.Margins.Left - Setup.Margins.Left;
            if (shift != 0)
            {
                page.Canvas.SaveState().Transform(1, 0, 0, 1, shift, 0);
            }

            foreach (var draw in pages[i])
            {
                draw(page.Canvas, page.Height);
            }

            if (shift != 0)
            {
                page.Canvas.RestoreState();
            }

            // Headers, footers and page numbers repeat on every page: artifacts in a tagged document.
            var markers = result.Markers.Where(m => m.Page == i).Select(m => m.Marker).ToList();
            LayoutTagging.BeginArtifact(page.Canvas, PdfArtifactKind.Pagination);
            PageDecorator?.Invoke(page.Canvas, new PageInfo(i + 1, pages.Count, setup, markers) { Name = context.Name, Side = context.Side, IsBlank = context.IsBlank });
            LayoutTagging.End(page.Canvas);
        }

        AddOutline(document.Outline, result.Anchors, rendered);
        AddDestinations(document.NamedDestinations, result.Anchors, rendered);
    }

    /// <summary>
    /// Turns the blocks' ids and the paragraphs' anchors into named destinations, which links to them refer to. The
    /// first one with a name wins, also over destinations the document already has.
    /// </summary>
    private static void AddDestinations(IDictionary<string, PdfDestination> destinations, IReadOnlyList<Anchor> anchors, List<PdfPage> pages)
    {
        foreach (var anchor in anchors)
        {
            if (anchor.DestinationName is { } name && !destinations.ContainsKey(name))
            {
                var page = pages[anchor.Page];
                destinations[name] = new PdfDestination(page, page.Height - anchor.Top);
            }
        }
    }

    /// <summary>
    /// Turns the bookmarks into outline items in document order. Each item nests under the nearest preceding item with
    /// a lower level (so an h3 after an h1 is the h1's child), or goes at the top when there is none.
    /// </summary>
    private static void AddOutline(IList<PdfOutlineItem> outline, IReadOnlyList<Anchor> anchors, List<PdfPage> pages)
    {
        var open = new List<(int Level, PdfOutlineItem Item)>(); // The current item at each nesting depth.
        foreach (var anchor in anchors)
        {
            if (anchor.Name is not null || anchor.Block.Bookmark is not { } bookmark)
            {
                continue;
            }

            var page = pages[anchor.Page];
            var item = new PdfOutlineItem(bookmark.Title, page, page.Height - anchor.Top) { IsOpen = bookmark.IsOpen };
            while (open.Count > 0 && open[^1].Level >= bookmark.Level)
            {
                open.RemoveAt(open.Count - 1);
            }

            (open.Count > 0 ? open[^1].Item.Children : outline).Add(item);
            open.Add((bookmark.Level, item));
        }
    }

    /// <summary>The setup of a page: from <see cref="PageSetupResolver"/>, or <see cref="Setup"/>.</summary>
    internal PageSetup ResolveSetup(PageContext context)
    {
        if (PageSetupResolver is not { } resolve)
        {
            return Setup;
        }

        var setup = resolve(context) ?? throw new InvalidOperationException($"The page setup resolver returned no setup for page {context.Number}.");
        if (setup.ContentWidth <= 0 || setup.ContentHeight <= 0)
        {
            throw new InvalidOperationException($"The margins of page {context.Number} leave no room for content.");
        }

        return setup;
    }

    /// <summary>The height of blocks laid out in one unbroken column of <paramref name="width"/> points.</summary>
    public static double MeasureBlocks(IReadOnlyList<Block> blocks, double width)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return Paginator.LayoutStatic(blocks, 0, Math.Max(1, width)).Height;
    }

    /// <summary>
    /// Draws blocks in one unbroken column (no page breaks) with its top-left corner at (<paramref name="x"/>,
    /// <paramref name="top"/>), where <paramref name="top"/> is measured from the top of the page. Useful in page
    /// decorators, e.g. for headers and footers. Returns the height used.
    /// </summary>
    public static double DrawBlocks(PdfCanvas canvas, double pageHeight, IReadOnlyList<Block> blocks, double x, double top, double width)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(blocks);
        var (height, content, _) = Paginator.LayoutStatic(blocks, x, Math.Max(1, width));
        foreach (var draw in content)
        {
            draw(canvas, pageHeight - top);
        }

        return height;
    }
}
