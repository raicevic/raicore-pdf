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
public readonly record struct PageInfo(int Number, int Count, PageSetup Setup);

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

    /// <summary>Lays out the blocks and appends the pages to <paramref name="document"/>.</summary>
    public void RenderTo(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pages = Paginator.Paginate(this).Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = document.AddPage(Setup.Size);
            foreach (var draw in pages[i])
            {
                draw(page.Canvas, page.Height);
            }

            PageDecorator?.Invoke(page.Canvas, new PageInfo(i + 1, pages.Count, Setup));
        }
    }
}
