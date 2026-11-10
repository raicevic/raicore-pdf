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

/// <summary>Information passed to a page decorator (header/footer callback).</summary>
/// <param name="Number">1-based page number.</param>
/// <param name="Count">Total number of pages.</param>
/// <param name="Setup">The page setup.</param>
/// <param name="Markers">Markers (<see cref="Block.Marker"/>) of the blocks that start on this page, in document order.</param>
public readonly record struct PageInfo(int Number, int Count, PageSetup Setup, IReadOnlyList<object> Markers);

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

    /// <summary>The page setup.</summary>
    public PageSetup Setup { get; }

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
    /// <see cref="Block.Bookmark"/> are appended to its <see cref="PdfDocument.Outline"/>, nested by level.
    /// </summary>
    public void RenderTo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = Paginator.Paginate(this);
        var pages = result.Pages;
        var rendered = new List<PdfPage>(pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            var page = document.AddPage(Setup.Size);
            rendered.Add(page);
            foreach (var draw in pages[i])
            {
                draw(page.Canvas, page.Height);
            }

            var markers = result.Markers.Where(m => m.Page == i).Select(m => m.Marker).ToList();
            PageDecorator?.Invoke(page.Canvas, new PageInfo(i + 1, pages.Count, Setup, markers));
        }

        AddOutline(document.Outline, result.Anchors, rendered);
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
            if (anchor.Block.Bookmark is not { } bookmark)
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
