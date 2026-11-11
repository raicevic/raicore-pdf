using AngleSharp.Dom;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// Named pages (CSS <c>page</c>): the blocks of an element whose page name differs from its parent's go in a container
/// with that <see cref="Block.PageName"/>, which the layout engine starts on a new page with that name. Percentages in
/// it resolve against that page's content width when every ancestor takes the full width.
/// </summary>
internal sealed partial class BoxBuilder
{
    /// <summary>The page name in effect (CSS: the used value of <c>page</c>); <c>null</c> for unnamed pages.</summary>
    private string? _pageName;

    /// <summary>
    /// The width the blocks being built get follows the page's content width: no ancestor has a width or
    /// max-width of its own, and none is a flex item, a table cell or an atomic inline.
    /// </summary>
    private bool _pageWidthFluid = true;

    /// <summary>
    /// The content width of pages with a name (<c>null</c> for unnamed pages), when named pages can have another size or
    /// other margins; <c>null</c> when all pages have the same content width.
    /// </summary>
    public Func<string?, double>? PageContentWidth { get; init; }

    /// <summary>
    /// Builds the blocks of a block-level element in its named page, if it has a page name other than its parent's:
    /// then they are wrapped in a container with that page name, built with the containing width that page gives.
    /// </summary>
    private List<Block> BuildInPage(IElement element, double containingWidth, Func<double, IEnumerable<Block>> build)
    {
        var name = _paged?.PageName(element);
        if (name is null || name == _pageName)
        {
            return [.. build(containingWidth)];
        }

        var outer = _pageName;
        _pageName = name;
        try
        {
            var width = _pageWidthFluid && PageContentWidth is { } widthOf
                ? Math.Max(1, containingWidth + widthOf(name) - widthOf(outer))
                : containingWidth;
            List<Block> blocks = [.. build(width)];
            return blocks.Count == 0 ? blocks : [new ContainerBlock(null, blocks) { PageName = name }];
        }
        finally
        {
            _pageName = outer;
        }
    }

    /// <summary>Builds with <see cref="_pageWidthFluid"/> narrowed for a box (restored afterwards).</summary>
    private void WithPageWidthFluid(bool fluid, Action build)
    {
        var outer = _pageWidthFluid;
        _pageWidthFluid = outer && fluid;
        try
        {
            build();
        }
        finally
        {
            _pageWidthFluid = outer;
        }
    }
}
