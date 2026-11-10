namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Size overrides for a flex item's border box.</summary>
/// <param name="Height">A fixed border-box height (content that does not fit overflows).</param>
/// <param name="MinHeight">A minimum border-box height.</param>
/// <param name="StretchTo">The line the box stretches to, when the line's end is only known after all its items.</param>
/// <param name="StretchInset">The item's bottom margin, kept free above the line's end.</param>
/// <param name="Isolated">The box is a flex item: its children's margins do not collapse through it.</param>
internal readonly record struct BoxSizing(double? Height = null, double? MinHeight = null, FlexLineEnd? StretchTo = null, double StretchInset = 0, bool Isolated = false);

/// <summary>Where a flex line split across pages ends; stretched items draw their boxes down to it.</summary>
internal sealed class FlexLineEnd
{
    public int Page { get; set; }

    public double Y { get; set; }

    public List<Action> Pending { get; } = [];
}

/// <summary>Flex layout (CSS Flexbox Level 1): the cross axis, pagination and drawing; see <see cref="FlexAlgorithm"/>.</summary>
internal sealed partial class Paginator
{
    /// <summary>A placed row item; X is relative to the container's content box.</summary>
    private sealed record RowItem(FlexItem Item, double X, double Width, double Height, double MarginTop, double MarginBottom, double Baseline);

    /// <summary>A flex line of a row container, with its cross size (height) and the largest item baseline.</summary>
    private sealed record RowLine(List<RowItem> Items, double CrossSize, double MaxBaseline);

    private static AutoMargins Auto(FlexItem item) => item.AutoMargins;

    private static double MarginOrZero(FlexItem item, AutoMargins side, double margin) => (item.AutoMargins & side) != 0 ? 0 : margin;

    private static FlexAlignment AlignmentOf(FlexBlock flex, FlexItem item) => item.AlignSelf ?? flex.AlignItems;

    private void LayoutFlex(FlexBlock flex, double x, double width)
    {
        var items = flex.OrderedItems();
        if (items.Count == 0)
        {
            return;
        }

        if (flex.IsRow)
        {
            LayoutFlexRows(flex, items, x, width);
        }
        else
        {
            LayoutFlexColumn(flex, items, x, width);
        }
    }

    // ---- Rows -------------------------------------------------------------------------------------------------

    /// <summary>The main-axis inputs of a row item: its flex base size and min/max widths, as border-box widths.</summary>
    private static FlexMainInput MainInput(FlexItem item, double width)
    {
        var content = item.Content;
        var margin = content.Style.Margin;
        var margins = MarginOrZero(item, AutoMargins.Left, margin.Left) + MarginOrZero(item, AutoMargins.Right, margin.Right);
        var (minContent, maxContent) = IntrinsicContentBorderBox(content);
        var specified = content.Style.Width;
        var baseSize = item.Basis.Kind switch
        {
            FlexBasisKind.Points => item.Basis.Value,
            FlexBasisKind.Percent => item.Basis.Value / 100 * width,
            FlexBasisKind.Content => maxContent,
            _ => specified ?? maxContent,
        };

        var max = item.MaxWidth ?? double.PositiveInfinity;

        // min-width: auto is the content's min-content width, capped by a specified width and the max width.
        var min = item.MinWidth ?? Math.Min(Math.Min(minContent, specified ?? double.PositiveInfinity), max);
        return new FlexMainInput(baseSize, min, Math.Max(min, max), Math.Max(0, item.Grow), Math.Max(0, item.Shrink), margins);
    }

    /// <summary>Breaks a row container into lines and resolves every item's width, height and baseline.</summary>
    private List<RowLine> ComputeRows(FlexBlock flex, List<FlexItem> items, double width)
    {
        var inputs = items.Select(item => MainInput(item, width)).ToList();
        var lines = new List<RowLine>();
        var gap = flex.ColumnGap;
        foreach (var (start, end) in FlexAlgorithm.BreakLines(inputs, width, gap, flex.Wrap != FlexWrap.NoWrap))
        {
            var count = end - start;
            var lineInputs = inputs.GetRange(start, count);
            var sizes = FlexAlgorithm.ResolveFlexibleLengths(lineInputs, width - (gap * (count - 1)));
            var outer = new double[count];
            var autoCounts = new int[count];
            for (var i = 0; i < count; i++)
            {
                outer[i] = sizes[i] + lineInputs[i].Margins;
                var auto = Auto(items[start + i]);
                autoCounts[i] = ((auto & AutoMargins.Left) != 0 ? 1 : 0) + ((auto & AutoMargins.Right) != 0 ? 1 : 0);
            }

            var (starts, autoMargin) = FlexAlgorithm.Justify(outer, autoCounts, width, gap, flex.JustifyContent);
            var row = new List<RowItem>(count);
            var crossSize = 0.0;
            var maxBaseline = 0.0;
            var maxBelowBaseline = 0.0;
            for (var i = 0; i < count; i++)
            {
                var item = items[start + i];
                var content = item.Content;
                var margin = content.Style.Margin;
                var leftMargin = (Auto(item) & AutoMargins.Left) != 0 ? autoMargin : margin.Left;
                var x = starts[i] + leftMargin;
                if (flex.Direction == FlexDirection.RowReverse)
                {
                    x = width - x - sizes[i];
                }

                var height = ClampHeight(item, item.Height ?? MeasureBox(content, sizes[i]));
                var marginTop = MarginOrZero(item, AutoMargins.Top, margin.Top);
                var marginBottom = MarginOrZero(item, AutoMargins.Bottom, margin.Bottom);
                var outerHeight = marginTop + height + marginBottom;
                var baseline = 0.0;
                if (AlignmentOf(flex, item) == FlexAlignment.Baseline && (Auto(item) & (AutoMargins.Top | AutoMargins.Bottom)) == 0)
                {
                    // Without text, the baseline is synthesized from the bottom of the border box.
                    baseline = marginTop + (FirstBaseline(content, sizes[i]) ?? height);
                    maxBaseline = Math.Max(maxBaseline, baseline);
                    maxBelowBaseline = Math.Max(maxBelowBaseline, outerHeight - baseline);
                }
                else
                {
                    crossSize = Math.Max(crossSize, outerHeight);
                }

                row.Add(new RowItem(item, x, sizes[i], height, marginTop, marginBottom, baseline));
            }

            lines.Add(new RowLine(row, Math.Max(crossSize, maxBaseline + maxBelowBaseline), maxBaseline));
        }

        if (flex.Wrap == FlexWrap.WrapReverse)
        {
            lines.Reverse();
        }

        return lines;
    }

    private static double ClampHeight(FlexItem item, double height) =>
        Math.Max(Math.Min(height, item.MaxHeight ?? double.PositiveInfinity), item.MinHeight ?? 0);

    /// <summary>An item's offset within its line (from the line's top to its margin box) and its final height.</summary>
    private static (double Offset, double Height, bool Stretched) CrossPlacement(FlexBlock flex, RowLine line, RowItem placed)
    {
        var item = placed.Item;
        var free = line.CrossSize - (placed.MarginTop + placed.Height + placed.MarginBottom);
        var auto = Auto(item) & (AutoMargins.Top | AutoMargins.Bottom);
        if (auto != AutoMargins.None)
        {
            return (auto == (AutoMargins.Top | AutoMargins.Bottom) ? free / 2 : auto == AutoMargins.Top ? free : 0, placed.Height, false);
        }

        var reverse = flex.Wrap == FlexWrap.WrapReverse;
        switch (AlignmentOf(flex, item))
        {
            case FlexAlignment.Stretch when item.Height is null:
                var stretched = line.CrossSize - placed.MarginTop - placed.MarginBottom;
                return (0, Math.Max(placed.Height, ClampHeight(item, stretched)), true);
            case FlexAlignment.Stretch:
            case FlexAlignment.FlexStart:
                return (reverse ? free : 0, placed.Height, false);
            case FlexAlignment.FlexEnd:
                return (reverse ? 0 : free, placed.Height, false);
            case FlexAlignment.Center:
                return (free / 2, placed.Height, false);
            default: // Baseline
                return (line.MaxBaseline - placed.Baseline, placed.Height, false);
        }
    }

    private void LayoutFlexRows(FlexBlock flex, List<FlexItem> items, double x, double width)
    {
        var lines = ComputeRows(flex, items, width);
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            if (l > 0 && !(AtPageTop && TruncateMargins))
            {
                Y += flex.RowGap;
            }

            // Start the line on a new page if not even the start of its items fits here.
            var needed = 0.0;
            foreach (var placed in line.Items)
            {
                var start = placed.Item.Content.Style.BreakInside == PageBreakInside.Avoid ? placed.Height : MinimumStartBox(placed.Item.Content, placed.Width);
                needed = Math.Max(needed, placed.MarginTop + start);
            }

            if (!_static && !AtPageTop && needed > Remaining + Epsilon && needed <= _setup.ContentHeight + Epsilon)
            {
                NewPage();
            }

            if (_static || Y + line.CrossSize <= ContentBottom + Epsilon)
            {
                LayoutRowOnPage(flex, line, x);
            }
            else
            {
                LayoutRowAcrossPages(flex, line, x);
            }
        }
    }

    /// <summary>A line that fits on the current page: items are aligned within it and stretched to its height.</summary>
    private void LayoutRowOnPage(FlexBlock flex, RowLine line, double x)
    {
        var lineTop = Y;
        var lineBottom = lineTop + line.CrossSize;
        foreach (var placed in line.Items)
        {
            var (offset, height, _) = CrossPlacement(flex, line, placed);
            Y = lineTop + offset + placed.MarginTop;
            AddMarker(placed.Item.Content);

            // An explicit height is exact (content may overflow); a measured or stretched one only a minimum.
            var sizing = placed.Item.Height is null ? new BoxSizing(MinHeight: height, Isolated: true) : new BoxSizing(Height: height, Isolated: true);
            LayoutBlock(placed.Item.Content, x + placed.X, placed.Width, sizing);
            lineBottom = Math.Max(lineBottom, Y + placed.MarginBottom);
        }

        Y = lineBottom;
    }

    /// <summary>
    /// A line taller than the space left: every item starts at the line's top and continues on the following pages in
    /// its own column, as browsers fragment flex rows. Stretched items' boxes run down to where the tallest item ends.
    /// </summary>
    private void LayoutRowAcrossPages(FlexBlock flex, RowLine line, double x)
    {
        var startPage = PageIndex;
        var lineTop = Y;
        var truncate = TruncateMargins;
        var end = new FlexLineEnd { Page = startPage, Y = lineTop };
        foreach (var placed in line.Items)
        {
            _pageIndex = startPage;
            Y = lineTop + placed.MarginTop;
            TruncateMargins = truncate;
            var (_, _, stretched) = CrossPlacement(flex, line, placed);
            var sizing = stretched
                ? new BoxSizing(StretchTo: end, StretchInset: placed.MarginBottom, Isolated: true)
                : new BoxSizing(MinHeight: placed.Item.Height, Isolated: true);
            AddMarker(placed.Item.Content);
            LayoutBlock(placed.Item.Content, x + placed.X, placed.Width, sizing);

            var itemEnd = Y + placed.MarginBottom;
            if (PageIndex > end.Page || (PageIndex == end.Page && itemEnd > end.Y))
            {
                (end.Page, end.Y) = (PageIndex, itemEnd);
            }
        }

        foreach (var decorate in end.Pending)
        {
            decorate();
        }

        _pageIndex = end.Page;
        Y = end.Y;
        TruncateMargins = end.Page > startPage || truncate;
    }

    private void AddMarker(Block block)
    {
        if (block.Marker is { } marker && !_static)
        {
            _markers.Add((PageIndex, marker));
        }
    }

    // ---- Columns ----------------------------------------------------------------------------------------------

    /// <summary>A column item's border-box width and its x offset (to the border box) within the container.</summary>
    private static (double X, double Width) ColumnCross(FlexBlock flex, FlexItem item, double width)
    {
        var style = item.Content.Style;
        var auto = Auto(item) & (AutoMargins.Left | AutoMargins.Right);
        var marginLeft = MarginOrZero(item, AutoMargins.Left, style.Margin.Left);
        var marginRight = MarginOrZero(item, AutoMargins.Right, style.Margin.Right);
        var available = Math.Max(0, width - marginLeft - marginRight);
        var alignment = AlignmentOf(flex, item);

        double boxWidth;
        if (style.Width is { } fixedWidth)
        {
            boxWidth = fixedWidth;
        }
        else if (alignment == FlexAlignment.Stretch && auto == AutoMargins.None)
        {
            boxWidth = available;
        }
        else
        {
            var (min, max) = IntrinsicContentBorderBox(item.Content);
            boxWidth = Math.Min(max, Math.Max(min, available)); // Shrink to fit.
        }

        boxWidth = Math.Max(Math.Min(boxWidth, item.MaxWidth ?? double.PositiveInfinity), item.MinWidth ?? 0);
        var free = available - boxWidth;
        var offset = auto switch
        {
            AutoMargins.Left | AutoMargins.Right => free / 2,
            AutoMargins.Left => free,
            AutoMargins.Right => 0,
            _ => alignment switch
            {
                FlexAlignment.FlexEnd => free,
                FlexAlignment.Center => free / 2,
                _ => 0,
            },
        };
        return (marginLeft + offset, boxWidth);
    }

    private static IEnumerable<FlexItem> ColumnOrder(FlexBlock flex, List<FlexItem> items) =>
        flex.Direction == FlexDirection.ColumnReverse ? Enumerable.Reverse(items) : items;

    /// <summary>
    /// A column container stacks its items like blocks, without margin collapsing and with row gaps between them.
    /// Its height is its content's, so there is no free space to grow into or justify.
    /// </summary>
    private void LayoutFlexColumn(FlexBlock flex, List<FlexItem> items, double x, double width)
    {
        var first = true;
        Block? previous = null;
        foreach (var item in ColumnOrder(flex, items))
        {
            var content = item.Content;
            var style = content.Style;
            var forced = style.BreakBefore == PageBreak.Page || previous?.Style.BreakAfter == PageBreak.Page;
            if (forced && !_static && !(AtPageTop && _pages[PageIndex].Count == 0))
            {
                NewPage(forced: true);
            }
            else if (!first && !(AtPageTop && TruncateMargins))
            {
                Y += flex.RowGap;
            }

            if (!(AtPageTop && TruncateMargins))
            {
                Y += MarginOrZero(item, AutoMargins.Top, style.Margin.Top);
            }

            var (offset, boxWidth) = ColumnCross(flex, item, width);
            var needed = style.BreakInside == PageBreakInside.Avoid ? MeasureBox(content, boxWidth) : MinimumStartBox(content, boxWidth);
            if (!_static && !AtPageTop && needed > Remaining + Epsilon && needed <= _setup.ContentHeight + Epsilon)
            {
                NewPage();
            }

            var height = item.Height is { } fixedHeight ? ClampHeight(item, fixedHeight) : (double?)null;
            AddMarker(content);
            LayoutBlock(content, x + offset, boxWidth, new BoxSizing(Height: height, MinHeight: item.MinHeight, Isolated: true));
            Y += MarginOrZero(item, AutoMargins.Bottom, style.Margin.Bottom);
            first = false;
            previous = content;
        }
    }

    // ---- Measuring --------------------------------------------------------------------------------------------

    /// <summary>The height of a flex container's content at a content width, without page breaks.</summary>
    private double MeasureFlex(FlexBlock flex, double width)
    {
        var items = flex.OrderedItems();
        if (items.Count == 0)
        {
            return 0;
        }

        if (flex.IsRow)
        {
            var lines = ComputeRows(flex, items, width);
            return lines.Sum(l => l.CrossSize) + (flex.RowGap * (lines.Count - 1));
        }

        var height = flex.RowGap * (items.Count - 1);
        foreach (var item in items)
        {
            var (_, boxWidth) = ColumnCross(flex, item, width);
            var style = item.Content.Style;
            var box = item.Height is { } fixedHeight ? ClampHeight(item, fixedHeight) : Math.Max(MeasureBox(item.Content, boxWidth), item.MinHeight ?? 0);
            height += MarginOrZero(item, AutoMargins.Top, style.Margin.Top) + box + MarginOrZero(item, AutoMargins.Bottom, style.Margin.Bottom);
        }

        return height;
    }

    /// <summary>The height a flex container's content needs at the bottom of a page before it is worth starting there.</summary>
    private double MinimumFlexStart(FlexBlock flex, double width)
    {
        var items = flex.OrderedItems();
        if (flex.IsRow)
        {
            var line = ComputeRows(flex, items, width)[0];
            return line.Items.Max(p => p.MarginTop + MinimumStartBox(p.Item.Content, p.Width));
        }

        var first = ColumnOrder(flex, items).First();
        var (_, boxWidth) = ColumnCross(flex, first, width);
        return MarginOrZero(first, AutoMargins.Top, first.Content.Style.Margin.Top) + MinimumStartBox(first.Content, boxWidth);
    }

    /// <summary>A flex container's min-content and max-content content widths (CSS Flexbox section 9.9.1).</summary>
    private static (double Min, double Max) FlexIntrinsic(FlexBlock flex)
    {
        var contributions = flex.Items.Select(item =>
        {
            var margin = item.Content.Style.Margin;
            var margins = MarginOrZero(item, AutoMargins.Left, margin.Left) + MarginOrZero(item, AutoMargins.Right, margin.Right);
            var (min, max) = IntrinsicBorderBox(item.Content);
            double Clamp(double w) => Math.Max(Math.Min(w, item.MaxWidth ?? double.PositiveInfinity), item.MinWidth ?? 0);
            return (Min: Clamp(min) + margins, Max: Clamp(max) + margins);
        }).ToList();

        if (contributions.Count == 0)
        {
            return (0, 0);
        }

        if (!flex.IsRow)
        {
            return (contributions.Max(c => c.Min), contributions.Max(c => c.Max));
        }

        var gaps = flex.ColumnGap * (contributions.Count - 1);
        var sumMax = contributions.Sum(c => c.Max) + gaps;
        return flex.Wrap == FlexWrap.NoWrap
            ? (contributions.Sum(c => c.Min) + gaps, sumMax)
            : (contributions.Max(c => c.Min), sumMax);
    }

    /// <summary>
    /// The distance from a block's border-box top to its first text baseline, or <c>null</c> if it has none
    /// (images, empty boxes).
    /// </summary>
    private double? FirstBaseline(Block block, double boxWidth)
    {
        var style = block.Style;
        var top = style.Border.Top.Width + style.Padding.Top;
        var contentWidth = Math.Max(MinimumContentWidth, boxWidth - style.Border.Widths.Horizontal - style.Padding.Horizontal);
        switch (block)
        {
            case ParagraphBlock paragraph:
                var lines = paragraph.Paragraph.Layout(contentWidth).Lines;
                return lines.Count > 0 ? top + lines[0].Baseline : null;
            case ContainerBlock { Children.Count: > 0 } container:
                var child = container.Children[0];
                return FirstBaseline(child, BoxWidth(child, contentWidth)) is { } inner ? top + child.Style.Margin.Top + inner : null;
            case FlexBlock { IsRow: true } flex when flex.Items.Count > 0:
                var line = ComputeRows(flex, flex.OrderedItems(), contentWidth)[0];
                var placed = line.Items[0];
                var (offset, _, _) = CrossPlacement(flex, line, placed);
                return FirstBaseline(placed.Item.Content, placed.Width) is { } itemBaseline ? top + offset + placed.MarginTop + itemBaseline : null;
            default:
                return null;
        }
    }
}
