namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Positioning (CSS 2.1 section 9.3 and 10.3.7/10.6.4): relative blocks are laid out in the flow and drawn moved;
/// absolute blocks leave the flow and are placed in their containing block once it is laid out (the padding box of the
/// nearest positioned ancestor, or else the page area of the page they are on); fixed blocks are placed in the page area
/// of every page. Positioned blocks are painted over the flow by z-index (see <see cref="PaintLayer"/>).
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The positioned blocks being laid out, innermost last, each with the absolute blocks waiting for it.</summary>
    private readonly List<List<PendingAbsolute>> _positionedFrames = [];

    /// <summary>The fixed blocks, placed on every page when the document is laid out.</summary>
    private readonly List<PendingAbsolute> _fixed = [];

    /// <summary>The width of the containing block of the block being laid out (for relative offsets in percent).</summary>
    private double _containingWidth;

    /// <summary>An absolute (or fixed) block and where it would have been in the flow (its static position).</summary>
    private readonly record struct PendingAbsolute(Block Block, int Page, double X, double Y);

    /// <summary>
    /// A containing block: its padding box, from <paramref name="Top"/> on page <paramref name="StartPage"/> to
    /// <paramref name="Bottom"/> on page <paramref name="EndPage"/>.
    /// </summary>
    private readonly record struct ContainingBox(int StartPage, double Top, int EndPage, double Bottom, double X, double Width, double Height);

    private static bool IsOutOfFlow(BoxStyle style) => style.Position is Positioning.Absolute or Positioning.Fixed;

    /// <summary>The paint layer of a positioned block's content: by its z-index, or its ancestor's for z-index auto.</summary>
    private static PaintLayer LayerFor(BoxStyle style, PaintLayer outer) => style.ZIndex switch
    {
        { } z => new PaintLayer(z < 0 ? PaintLayer.Below : PaintLayer.Positioned, z),
        null when outer.Group is PaintLayer.Positioned or PaintLayer.Below => outer,
        _ => new PaintLayer(PaintLayer.Positioned, 0),
    };

    /// <summary>
    /// Records an absolute or fixed block met in the flow at its static position: it waits for the nearest positioned
    /// ancestor to be laid out, or is placed now in the page area; fixed blocks wait for the end of the document.
    /// </summary>
    private void AddAbsolute(Block block, double x, double y)
    {
        var pending = new PendingAbsolute(block, PageIndex, x, y);
        if (block.Style.Position == Positioning.Fixed && !_static)
        {
            _fixed.Add(pending);
        }
        else if (_positionedFrames.Count > 0)
        {
            _positionedFrames[^1].Add(pending);
        }
        else
        {
            PlaceAbsolute(pending, PageArea(PageIndex), withAnchors: true);
        }
    }

    /// <summary>The page area of a page (in a static column: the column, down to the current position).</summary>
    private ContainingBox PageArea(int page)
    {
        var setup = _frames[page].Setup;
        var top = setup.Margins.Top;
        var bottom = _static ? Math.Max(top, Y) : setup.Size.Height - setup.Margins.Bottom;
        return new ContainingBox(page, top, page, bottom, _setup.Margins.Left, ContentWidthOf(page), bottom - top);
    }

    /// <summary>
    /// Lays out a positioned block: a relative one is drawn moved by its offsets, and either kind is the containing
    /// block of the absolute blocks inside it, which are placed in its padding box once it is laid out.
    /// </summary>
    private CollapsedMargin LayoutPositioned(Block block, double x, double width, BoxSizing sizing, Func<CollapsedMargin> layout)
    {
        var style = block.Style;
        var (dx, dy) = (0.0, 0.0);
        if (style.Position == Positioning.Relative)
        {
            var insets = style.Insets;
            dx = insets.Left?.Resolve(_containingWidth) ?? -(insets.Right?.Resolve(_containingWidth) ?? 0);
            dy = insets.Top is { IsPercent: false } top ? top.Value : insets.Bottom is { IsPercent: false } bottom ? -bottom.Value : 0;
        }

        var (outerLayer, outerShift) = (_layer, _shift);
        var (startPage, startY) = (PageIndex, Y);
        var pending = new List<PendingAbsolute>();
        _positionedFrames.Add(pending);
        _layer = LayerFor(style, _layer);
        _shift = (_shift.X + dx, _shift.Y + dy);
        CollapsedMargin trailing;
        try
        {
            trailing = layout();
        }
        finally
        {
            _positionedFrames.RemoveAt(_positionedFrames.Count - 1);
            _layer = outerLayer;
            _shift = outerShift;
        }

        if (pending.Count > 0)
        {
            // The padding box, where the block was drawn (moved by its offset).
            var border = style.Border.Widths;
            var (endPage, endY) = sizing.StretchTo is { } line ? (line.Page, line.Y - sizing.StretchInset) : (PageIndex, Y);
            var top = startY + border.Top + dy;
            var bottom = endY - border.Bottom + dy;
            var height = startPage == endPage
                ? bottom - top
                : PageBottom(startPage) - top + Enumerable.Range(startPage + 1, endPage - startPage - 1).Sum(p => PageBottom(p) - PageTop(p)) + (bottom - PageTop(endPage));
            var box = new ContainingBox(startPage, top, endPage, bottom, x + border.Left + dx, Math.Max(0, width - border.Horizontal), Math.Max(0, height));
            // In the block's layer: a z-index auto child paints with its positioned ancestor.
            _layer = LayerFor(style, outerLayer);
            try
            {
                foreach (var absolute in pending)
                {
                    PlaceAbsolute(absolute, box, withAnchors: true);
                }
            }
            finally
            {
                _layer = outerLayer;
            }
        }

        return trailing;
    }

    /// <summary>
    /// Places an absolute block in its containing block (CSS 2.1 sections 10.3.7 and 10.6.4): its width from its own,
    /// from a left and a right inset, or shrink-to-fit; its height from its content, or from a top and a bottom inset;
    /// each auto inset keeping its static position. It is laid out as one piece and drawn on one page.
    /// </summary>
    private void PlaceAbsolute(PendingAbsolute absolute, ContainingBox box, bool withAnchors)
    {
        var block = absolute.Block;
        var style = block.Style;
        var (margin, insets) = (style.Margin, style.Insets);
        var left = insets.Left?.Resolve(box.Width);
        var right = insets.Right?.Resolve(box.Width);
        var top = insets.Top?.Resolve(box.Height);
        var bottom = insets.Bottom?.Resolve(box.Height);

        double boxWidth;
        if (style.Width is { } fixedWidth)
        {
            boxWidth = style.ClampWidth(fixedWidth);
        }
        else if (left is { } l && right is { } r && block is not (ImageBlock or TableBlock))
        {
            boxWidth = style.ClampWidth(Math.Max(0, box.Width - l - r - margin.Horizontal));
        }
        else
        {
            boxWidth = FloatBoxWidth(block, Math.Max(0, box.Width - (left ?? 0) - (right ?? 0)));
        }

        double? height = style.Height is null && block is not (ImageBlock or TableBlock) && top is { } t0 && bottom is { } b0
            ? Math.Max(0, box.Height - t0 - b0 - margin.Vertical)
            : null;
        var (boxHeight, content, anchors) = LayoutAbsoluteBox(block, boxWidth, height);

        var x = left is { } leftInset ? box.X + leftInset + margin.Left
            : right is { } rightInset ? box.X + box.Width - rightInset - margin.Right - boxWidth
            : absolute.X + margin.Left;
        var (page, y) = top is { } topInset ? (box.StartPage, box.Top + topInset + margin.Top)
            : bottom is { } bottomInset ? (box.EndPage, box.Bottom - bottomInset - margin.Bottom - boxHeight)
            : (absolute.Page, absolute.Y + margin.Top);

        var outerLayer = _layer;
        _layer = LayerFor(style, _layer);
        try
        {
            _pages[page].Add((canvas, pageHeight) =>
            {
                canvas.SaveState().Transform(1, 0, 0, 1, x, 0);
                foreach (var draw in content)
                {
                    draw(canvas, pageHeight - y);
                }

                canvas.RestoreState();
            });
        }
        finally
        {
            _layer = outerLayer;
        }

        _placements.Add(new Placement(page, block, y, y + boxHeight, -1, -1, x, boxWidth));
        if (withAnchors)
        {
            foreach (var anchor in anchors)
            {
                _anchors.Add(anchor with { Page = page, Top = y + anchor.Top, Left = x + anchor.Left });
            }
        }
    }

    /// <summary>
    /// Lays out an absolute block at a border-box width (and height, if given) without page breaks, as a new formatting
    /// context. Returns its border-box height, drawing operations with its top-left corner at x = 0 and y = the top
    /// passed to them, and its anchors (relative to that corner).
    /// </summary>
    private static (double Height, IReadOnlyList<DrawOperation> Content, IReadOnlyList<Anchor> Anchors) LayoutAbsoluteBox(Block block, double borderWidth, double? height)
    {
        var setup = new PageSetup(new PdfPageSize(borderWidth + 1, StaticHeight), new Thickness(0, 1, 0, 0));
        var paginator = new Paginator(setup, isStatic: true);
        paginator.InFloatRoot(() => paginator.LayoutBlock(block, 0, borderWidth, new BoxSizing(Height: height, Isolated: true)));
        return (paginator.Y, paginator._pages[0].Grouped(), paginator._anchors);
    }

    /// <summary>
    /// Lays out a block that clips its overflow and has a fixed height without page breaks, and draws it at the current
    /// position, which moves below it.
    /// </summary>
    private void PlaceClipped(Block block, double x, double width, double height)
    {
        var (boxHeight, content, anchors) = LayoutAbsoluteBox(block, width, height);
        var (page, top) = (PageIndex, Y);
        _pages[page].Add((canvas, pageHeight) =>
        {
            canvas.SaveState().Transform(1, 0, 0, 1, x, 0);
            foreach (var draw in content)
            {
                draw(canvas, pageHeight - top);
            }

            canvas.RestoreState();
        });
        _placements.Add(new Placement(page, block, top, top + boxHeight, -1, -1, x, width));
        foreach (var anchor in anchors)
        {
            _anchors.Add(anchor with { Page = page, Top = top + anchor.Top, Left = x + anchor.Left });
        }

        Y = top + boxHeight;
    }

    /// <summary>Places the fixed blocks in the page area of every page, as browsers print them.</summary>
    private void PlaceFixed()
    {
        foreach (var block in _fixed)
        {
            for (var page = 0; page < _pages.Count; page++)
            {
                var onPage = block with { Page = page };
                PlaceAbsolute(onPage, PageArea(page), withAnchors: page == block.Page);
            }
        }
    }
}
