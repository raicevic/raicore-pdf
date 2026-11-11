namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>
/// Floats (CSS 2.1 section 9.5): blocks taken out of the flow and moved to a side, with the text after them flowing
/// around them. Each block formatting context (the document, a float, a flex item, a table cell, a block with
/// <see cref="BoxStyle.ContainsFloats"/>) keeps the areas its floats cover, per page, in layout coordinates.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The floats of the current block formatting context.</summary>
    private FloatContext _floatContext = new();

    /// <summary>A float's margin box on one page; y from the top of the page, x in layout coordinates.</summary>
    private readonly record struct FloatArea(int Page, double Top, double Bottom, double Left, double Right, FloatSide Side);

    /// <summary>The floats placed in one block formatting context, and where the last one went.</summary>
    private sealed class FloatContext
    {
        public List<FloatArea> Areas { get; } = [];

        /// <summary>The page and top of the last float placed: a float may not go higher than an earlier one (rule 5).</summary>
        public (int Page, double Top) Last { get; set; } = (-1, 0);
    }

    /// <summary>Whether a block is laid out as the root of a new block formatting context, containing its own floats.</summary>
    private static bool IsFloatRoot(Block block, BoxSizing sizing) =>
        sizing.Isolated || block.Style.ContainsFloats || block.Style.Float != FloatSide.None;

    /// <summary>
    /// Whether a block in the flow keeps clear of floats as a whole (its border box goes beside or below them) instead of
    /// only its lines avoiding them: tables, flex containers, images and new formatting contexts.
    /// </summary>
    private static bool AvoidsFloats(Block block) =>
        block is TableBlock or FlexBlock or ImageBlock || block.Style.ContainsFloats;

    /// <summary>Whether floats of the current context reach below <paramref name="y"/> on the current page, or later pages.</summary>
    private bool HasFloatsBelow(double y)
    {
        foreach (var area in _floatContext.Areas)
        {
            if (area.Page > PageIndex || (area.Page == PageIndex && area.Bottom > y + Epsilon))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The room between <paramref name="left"/> and <paramref name="right"/> that floats leave on a page between two
    /// heights, and the highest bottom of the floats in the way (where the room may grow), if any.
    /// </summary>
    private (double Left, double Right, double? NextBottom) FreeSpace(int page, double top, double bottom, double left, double right)
    {
        double? next = null;
        foreach (var area in _floatContext.Areas)
        {
            if (area.Page != page || area.Top >= bottom - Epsilon || area.Bottom <= top + Epsilon
                || area.Right <= left + Epsilon || area.Left >= right - Epsilon)
            {
                continue;
            }

            if (area.Side == FloatSide.Left)
            {
                left = Math.Max(left, area.Right);
            }
            else
            {
                right = Math.Min(right, area.Left);
            }

            next = next is { } n ? Math.Min(n, area.Bottom) : area.Bottom;
        }

        return (left, right, next);
    }

    /// <summary>
    /// Places a floated block (its margin box) at the current position plus <paramref name="pendingMargin"/>, within
    /// the containing block from <paramref name="x"/> over <paramref name="width"/>: as high as earlier floats allow and as
    /// far to its side as they allow, moving down until it fits beside them. One that does not fit on the rest of the
    /// page (but would on an empty one) moves to the next page, and what follows it with it. The flow position is left
    /// where it was: the float is laid out on its own, then recorded so later lines and boxes avoid it.
    /// </summary>
    private void PlaceFloat(Block block, double x, double width, double pendingMargin)
    {
        var style = block.Style;
        var boxWidth = FloatBoxWidth(block, width);
        var outerWidth = boxWidth + style.Margin.Horizontal;
        var height = MeasureBox(block, boxWidth) + style.Margin.Vertical;

        double top, left;
        while (true)
        {
            top = Y + pendingMargin;
            if (_floatContext.Last.Page == PageIndex)
            {
                top = Math.Max(top, _floatContext.Last.Top);
            }

            (double Left, double Right, double? NextBottom) room;
            while (true)
            {
                room = FreeSpace(PageIndex, top, top + height, x, x + width);
                if (room.Right - room.Left >= outerWidth - Epsilon || room.NextBottom is not { } next)
                {
                    break;
                }

                top = next;
            }

            left = style.Float == FloatSide.Right ? room.Right - outerWidth : room.Left;
            if (_static || top + height <= ContentBottom + Epsilon || AtPageTop || height > NextPageContentHeight + Epsilon)
            {
                break;
            }

            NewPage(); // Does not fit below what is on this page: it starts the next one.
            pendingMargin = 0;
        }

        var (page, y, truncate) = (PageIndex, Y, TruncateMargins);
        Y = top + style.Margin.Top;
        TruncateMargins = false; // Out of the flow: nothing in it is truncated at the top of the page.

        var outerLayer = _layer;
        _layer = _layer.Group == PaintLayer.Flow ? new PaintLayer(PaintLayer.Floats, 0) : _layer; // In a positioned box: its layer.
        try
        {
            LayoutBlock(block, left + style.Margin.Left, boxWidth);
        }
        finally
        {
            _layer = outerLayer;
        }

        var (endPage, endY) = (PageIndex, Y + style.Margin.Bottom);
        for (var p = page; p <= endPage; p++)
        {
            var areaTop = p == page ? top : PageTop(p);
            var areaBottom = p == endPage ? endY : PageBottom(p);
            _floatContext.Areas.Add(new FloatArea(p, areaTop, areaBottom, left, left + outerWidth, style.Float));
        }

        _floatContext.Last = (page, top);
        _pageIndex = page;
        Y = y;
        TruncateMargins = truncate;
    }

    /// <summary>
    /// A float's border-box width: its own width, or shrink-to-fit (CSS 2.1 section 10.3.5): its max-content width,
    /// but no wider than the available width, and no narrower than its min-content width.
    /// </summary>
    private double FloatBoxWidth(Block block, double width)
    {
        var style = block.Style;
        if (style.Width is not null || block is ImageBlock or TableBlock)
        {
            return BoxWidth(block, width);
        }

        var available = Math.Max(0, width - style.Margin.Horizontal);
        var (min, max) = IntrinsicContentBorderBox(block);
        return style.ClampWidth(Math.Min(Math.Max(min, available), max));
    }

    /// <summary>
    /// The page and height below the floats of the current context on the given sides that reach below the current
    /// position (on this page or later ones), or <c>null</c> when there are none.
    /// </summary>
    private (int Page, double Bottom)? FloatsEnd(ClearSide sides)
    {
        (int Page, double Bottom)? end = null;
        foreach (var area in _floatContext.Areas)
        {
            var matches = sides == ClearSide.Both || (sides == ClearSide.Left) == (area.Side == FloatSide.Left);
            if (!matches || area.Page < PageIndex || (area.Page == PageIndex && area.Bottom <= Y + Epsilon))
            {
                continue;
            }

            if (end is not { } e || area.Page > e.Page || (area.Page == e.Page && area.Bottom > e.Bottom))
            {
                end = (area.Page, area.Bottom);
            }
        }

        return end;
    }

    /// <summary>Moves the flow position down to a page and height (below floats), moving to later pages as needed.</summary>
    private void MoveDownTo((int Page, double Bottom) target)
    {
        while (PageIndex < target.Page)
        {
            NewPage();
        }

        Y = Math.Max(Y, target.Bottom);
    }

    /// <summary>
    /// Moves a block with <see cref="BoxStyle.Clear"/> below the floats it clears, if its top border edge (after
    /// <paramref name="pending"/>) would be above them; the clearance then replaces its collapsed top margin.
    /// </summary>
    private void ApplyClearance(ClearSide clear, ref CollapsedMargin pending)
    {
        if (clear == ClearSide.None || FloatsEnd(clear) is not { } end)
        {
            return;
        }

        if (end.Page > PageIndex || Y + pending.Value < end.Bottom - Epsilon)
        {
            MoveDownTo(end);
            pending = default;
        }
    }

    /// <summary>
    /// The room a block that avoids floats (<see cref="AvoidsFloats"/>) gets at the current position plus
    /// <paramref name="pendingMargin"/>, within <paramref name="x"/> over <paramref name="width"/>: beside the floats there,
    /// or, when it does not fit beside them (narrower than its min-content width), below them. Returns the x and width
    /// to lay it out in, and the height it moves down to, if it had to.
    /// </summary>
    private (double X, double Width, double? MoveTo) RoomBeside(Block block, double x, double width, double pendingMargin)
    {
        var top = Y + pendingMargin;
        if (!HasFloatsBelow(top))
        {
            return (x, width, null);
        }

        var minimum = MeasureIntrinsic(block).Min;
        var moved = false;
        while (true)
        {
            var (left, right, next) = FreeSpace(PageIndex, top, top + Math.Max(1, MeasureHeight(block, width)), x, x + width);
            if (next is null || right - left >= minimum - Epsilon)
            {
                return (left, Math.Max(MinimumContentWidth, right - left), moved ? top : null);
            }

            top = next.Value;
            moved = true;
        }
    }

    /// <summary>
    /// The room each line of a paragraph gets beside the floats of the current page, with the paragraph's top at
    /// <paramref name="origin"/> on it, within <paramref name="x"/> over <paramref name="width"/>. The first
    /// <paramref name="keep"/> lines (already placed on earlier pages) get the room they had, which
    /// <paramref name="rooms"/> records for each line. Lines below the page get the full width; the paragraph is laid
    /// out again on the next page.
    /// </summary>
    private LineSpace LineRooms(double x, double width, double origin, int keep, List<(double Top, double Offset, double Width)> rooms)
    {
        var (page, bottom) = (PageIndex, ContentBottom);
        var index = 0;
        return (top, height, minWidth) =>
        {
            var line = index++;
            if (line < keep && line < rooms.Count)
            {
                return rooms[line];
            }

            // Below the page (also after moving down past floats) it gets the full width there, and goes to the next page.
            var y = origin + top;
            (double Top, double Offset, double Width)? found = null;
            while (found is null && y + height <= bottom + Epsilon)
            {
                var (left, right, next) = FreeSpace(page, y, y + height, x, x + width);
                if (next is null || right - left >= minWidth - Epsilon)
                {
                    found = (y - origin, left - x, Math.Max(MinimumContentWidth, right - left));
                }
                else
                {
                    y = next.Value; // Too narrow beside the floats: the line moves down past the first of them.
                }
            }

            var room = found ?? (y - origin, 0, width);

            if (line < rooms.Count)
            {
                rooms[line] = room;
                rooms.RemoveRange(line + 1, rooms.Count - line - 1);
            }
            else
            {
                rooms.Add(room);
            }

            return room;
        };
    }

    /// <summary>
    /// Lays out a new block formatting context's content: its floats are its own, and it grows to contain them (the
    /// flow position ends below them).
    /// </summary>
    private void InFloatRoot(Action layout)
    {
        var outer = _floatContext;
        _floatContext = new FloatContext();
        try
        {
            layout();
            if (FloatsEnd(ClearSide.Both) is { } end)
            {
                MoveDownTo(end);
            }
        }
        finally
        {
            _floatContext = outer;
        }
    }
}
