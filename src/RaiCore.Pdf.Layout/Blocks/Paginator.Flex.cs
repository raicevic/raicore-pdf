namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>Size overrides for a flex item's border box.</summary>
/// <param name="Height">A fixed border-box height (content that does not fit overflows).</param>
/// <param name="MinHeight">A minimum border-box height.</param>
/// <param name="StretchTo">The line the box stretches to, when the line's end is only known after all its items.</param>
/// <param name="StretchInset">The item's bottom margin, kept free above the line's end.</param>
/// <param name="Isolated">The box is a flex item: its children's margins do not collapse through it.</param>
/// <param name="DefiniteHeight">
/// A flex item's resolved border-box height (flexed, stretched or fixed), which CSS treats as definite: a nested flex
/// container lays its content out in it.
/// </param>
internal readonly record struct BoxSizing(
    double? Height = null, double? MinHeight = null, FlexLineEnd? StretchTo = null, double StretchInset = 0, bool Isolated = false, double? DefiniteHeight = null);

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

    /// <summary>A placed column item: Top is its margin-box top within the line, X its border-box left in the container.</summary>
    private sealed record ColumnItem(FlexItem Item, double Top, double Height, double X, double Width, double MarginTop, double MarginBottom);

    /// <summary>A line of a column container: its items in top-to-bottom order and its position across the container.</summary>
    private sealed record ColumnLine(List<ColumnItem> Items, double X, double Width);

    private static AutoMargins Auto(FlexItem item) => item.AutoMargins;

    private static double MarginOrZero(FlexItem item, AutoMargins side, double margin) => (item.AutoMargins & side) != 0 ? 0 : margin;

    private static FlexAlignment AlignmentOf(FlexBlock flex, FlexItem item) => item.AlignSelf ?? flex.AlignItems;

    /// <summary>
    /// The container's inner (content-box) height limits: a definite height if it has one, and the min and max that
    /// an auto height is clamped to.
    /// </summary>
    private static (double? Definite, double Min, double Max) InnerHeights(FlexBlock flex, double? definiteBorderBox = null)
    {
        var style = flex.Style;
        var decoration = style.Border.Widths.Vertical + style.Padding.Vertical;
        var min = Math.Max(0, (style.MinHeight ?? 0) - decoration);
        var max = style.MaxHeight is { } maxHeight ? Math.Max(0, maxHeight - decoration) : double.PositiveInfinity;
        double? definite = (definiteBorderBox ?? style.Height) is { } height ? Math.Max(Math.Min(Math.Max(0, height - decoration), max), min) : null;
        return (definite, min, Math.Max(min, max));
    }

    /// <summary>The size the container's lines are fitted into: definite, or the content size clamped by min/max.</summary>
    private static double InnerSize(double? definite, double content, double min, double max) => definite ?? Math.Max(Math.Min(content, max), min);

    /// <summary>Positions the lines of a container on the cross axis (CSS align-content) and stretches them.</summary>
    private static (double[] Starts, double Extra) AlignLines(List<double> sizes, double available, double gap, AlignContent align, bool reverse)
    {
        var free = available - sizes.Sum() - (gap * Math.Max(0, sizes.Count - 1));
        if (align == AlignContent.Stretch && free > 0)
        {
            var extra = free / sizes.Count;
            var (stretchedStarts, _) = FlexAlgorithm.Justify([.. sizes.Select(s => s + extra)], new int[sizes.Count], available, gap, JustifyContent.FlexStart);
            return (stretchedStarts, extra);
        }

        // wrap-reverse puts the cross start at the other edge, so start and end packing swap.
        var justify = align switch
        {
            AlignContent.FlexEnd => reverse ? JustifyContent.FlexStart : JustifyContent.FlexEnd,
            AlignContent.Center => JustifyContent.Center,
            AlignContent.SpaceBetween => JustifyContent.SpaceBetween,
            AlignContent.SpaceAround => JustifyContent.SpaceAround,
            AlignContent.SpaceEvenly => JustifyContent.SpaceEvenly,
            _ => reverse ? JustifyContent.FlexEnd : JustifyContent.FlexStart,
        };
        var (starts, _) = FlexAlgorithm.Justify(sizes, new int[sizes.Count], available, gap, justify);
        return (starts, 0);
    }

    private void LayoutFlex(FlexBlock flex, double x, double width, double? definiteHeight)
    {
        var items = flex.OrderedItems();
        if (items.Count == 0)
        {
            return;
        }

        if (flex.IsRow)
        {
            LayoutFlexRows(flex, items, x, width, definiteHeight);
        }
        else
        {
            LayoutFlexColumns(flex, items, x, width, definiteHeight);
        }
    }

    // ---- Rows -------------------------------------------------------------------------------------------------

    /// <summary>The main-axis inputs of a row item: its flex base size and min/max widths, as border-box widths.</summary>
    private static FlexMainInput RowInput(FlexItem item, double width)
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

    /// <summary>
    /// Breaks a row container into lines, resolves every item's width, height and baseline, and sizes the lines to the
    /// container's height (a single line fills it; several lines share the free space by align-content).
    /// </summary>
    private (List<RowLine> Lines, double[] Starts) ComputeRows(FlexBlock flex, List<FlexItem> items, double width, double? definiteHeight = null)
    {
        var inputs = items.Select(item => RowInput(item, width)).ToList();
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

                var height = MeasureBox(content, sizes[i]);
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

        var reverse = flex.Wrap == FlexWrap.WrapReverse;
        if (reverse)
        {
            lines.Reverse();
        }

        // Fit the lines into the container's height: definite, or auto clamped by min-height/max-height.
        var (definite, min, max) = InnerHeights(flex, definiteHeight);
        var natural = lines.Sum(l => l.CrossSize) + (flex.RowGap * (lines.Count - 1));
        var inner = InnerSize(definite, natural, min, max);
        if (flex.Wrap == FlexWrap.NoWrap)
        {
            // A single-line container's line is exactly as tall as the container.
            lines[0] = lines[0] with { CrossSize = inner };
            return (lines, [0]);
        }

        var (lineStarts, extra) = AlignLines([.. lines.Select(l => l.CrossSize)], inner, flex.RowGap, flex.AlignContent, reverse);
        if (extra > 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i] = lines[i] with { CrossSize = lines[i].CrossSize + extra };
            }
        }

        return (lines, lineStarts);
    }

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
            case FlexAlignment.Stretch when item.Content.Style.Height is null:
                var stretched = ApplyHeights(item.Content, line.CrossSize - placed.MarginTop - placed.MarginBottom);
                return (0, Math.Max(placed.Height, stretched), true);
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

    private void LayoutFlexRows(FlexBlock flex, List<FlexItem> items, double x, double width, double? definiteHeight)
    {
        var (lines, starts) = ComputeRows(flex, items, width, definiteHeight);
        var cursor = 0.0; // Where the previous line ended, in unbroken coordinates.
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            if (!(AtPageTop && TruncateMargins && l > 0))
            {
                Y += starts[l] - cursor; // The row gap and align-content spacing.
            }

            cursor = starts[l] + line.CrossSize;

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
            var (offset, height, stretched) = CrossPlacement(flex, line, placed);
            Y = lineTop + offset + placed.MarginTop;
            AddMarker(placed.Item.Content);

            // An explicit height is exact (content may overflow); a measured or stretched one only a minimum.
            var definite = stretched || placed.Item.Content.Style.Height is not null ? height : (double?)null;
            var sizing = placed.Item.Content.Style.Height is null
                ? new BoxSizing(MinHeight: height, Isolated: true, DefiniteHeight: definite)
                : new BoxSizing(Height: height, Isolated: true, DefiniteHeight: definite);
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
        var lineTop = Y;
        LayoutSideBySide(line.Items, (placed, end) =>
        {
            Y = lineTop + placed.MarginTop;
            var (_, _, stretched) = CrossPlacement(flex, line, placed);
            var sizing = stretched
                ? new BoxSizing(StretchTo: end, StretchInset: placed.MarginBottom, Isolated: true)
                : new BoxSizing(Isolated: true);
            AddMarker(placed.Item.Content);
            LayoutBlock(placed.Item.Content, x + placed.X, placed.Width, sizing);
            Y += placed.MarginBottom;
        });
    }

    /// <summary>
    /// Lays out parts that sit side by side (the items of a row, the lines of a column), each starting at the current
    /// position and free to continue on later pages. Afterwards the position is where the longest part ended.
    /// </summary>
    private void LayoutSideBySide<T>(IEnumerable<T> parts, Action<T, FlexLineEnd> layout)
    {
        var startPage = PageIndex;
        var startY = Y;
        var truncate = TruncateMargins;
        var end = new FlexLineEnd { Page = startPage, Y = startY };
        foreach (var part in parts)
        {
            _pageIndex = startPage;
            Y = startY;
            TruncateMargins = truncate;
            layout(part, end);
            if (PageIndex > end.Page || (PageIndex == end.Page && Y > end.Y))
            {
                (end.Page, end.Y) = (PageIndex, Y);
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

    /// <summary>
    /// A column item's border-box width and its x offset (to the border box) within a line of the given width.
    /// <paramref name="stretch"/> is false when sizing the item's hypothetical width (to find the width of a wrapped line).
    /// </summary>
    private static (double X, double Width) ColumnCross(FlexBlock flex, FlexItem item, double width, bool stretch = true)
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
        else if (stretch && alignment == FlexAlignment.Stretch && auto == AutoMargins.None)
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
        var reverse = flex.Wrap == FlexWrap.WrapReverse;
        var offset = auto switch
        {
            AutoMargins.Left | AutoMargins.Right => free / 2,
            AutoMargins.Left => free,
            AutoMargins.Right => 0,
            _ => alignment switch
            {
                FlexAlignment.FlexEnd => reverse ? 0 : free,
                FlexAlignment.Center => free / 2,
                _ => reverse ? free : 0,
            },
        };
        return (marginLeft + offset, boxWidth);
    }

    /// <summary>The main-axis inputs of a column item: base, min and max heights of its border box at a width.</summary>
    private FlexMainInput ColumnInput(FlexItem item, double boxWidth, double? definiteHeight)
    {
        var content = item.Content;
        var style = content.Style;
        var margins = MarginOrZero(item, AutoMargins.Top, style.Margin.Top) + MarginOrZero(item, AutoMargins.Bottom, style.Margin.Bottom);
        var natural = MeasureContentBox(content, boxWidth);
        var specified = content is ImageBlock ? null : style.Height;
        var baseSize = item.Basis.Kind switch
        {
            FlexBasisKind.Points => item.Basis.Value,
            FlexBasisKind.Percent when definiteHeight is { } h => item.Basis.Value / 100 * h,
            FlexBasisKind.Content or FlexBasisKind.Percent => natural,
            _ => specified ?? natural,
        };

        var max = style.MaxHeight ?? double.PositiveInfinity;

        // min-height: auto keeps a column item at least as tall as its content (capped by a specified height).
        var min = style.MinHeight ?? Math.Min(Math.Min(natural, specified ?? double.PositiveInfinity), max);
        return new FlexMainInput(baseSize, min, Math.Max(min, max), Math.Max(0, item.Grow), Math.Max(0, item.Shrink), margins);
    }

    /// <summary>
    /// Sizes and positions a column container's items: lines (more than one only when wrapping into a limited height),
    /// item heights flexed into the container's height, justify-content and auto margins vertically, and the lines
    /// placed across the width by align-content.
    /// </summary>
    private List<ColumnLine> ComputeColumns(FlexBlock flex, List<FlexItem> items, double width, double? definiteHeight = null)
    {
        var (definite, minInner, maxInner) = InnerHeights(flex, definiteHeight);
        var wrap = flex.Wrap != FlexWrap.NoWrap && (definite is not null || !double.IsPositiveInfinity(maxInner));

        // Hypothetical widths: a single line is as wide as the container; wrapped lines are as wide as their widest item.
        var widths = items.Select(item => wrap ? ColumnCross(flex, item, width, stretch: false).Width : ColumnCross(flex, item, width).Width).ToList();
        var inputs = items.Select((item, i) => ColumnInput(item, widths[i], definite)).ToList();
        var gap = flex.RowGap;
        var breakAt = definite ?? maxInner;
        var ranges = FlexAlgorithm.BreakLines(inputs, breakAt, gap, wrap);

        var lineWidths = ranges.Select(r =>
        {
            if (!wrap)
            {
                return width;
            }

            var widest = 0.0;
            for (var i = r.Start; i < r.End; i++)
            {
                var margin = items[i].Content.Style.Margin;
                widest = Math.Max(widest, widths[i] + MarginOrZero(items[i], AutoMargins.Left, margin.Left) + MarginOrZero(items[i], AutoMargins.Right, margin.Right));
            }

            return widest;
        }).ToList();

        var reverseLines = flex.Wrap == FlexWrap.WrapReverse;
        var (lineStarts, extra) = wrap
            ? AlignLines(lineWidths, width, flex.ColumnGap, flex.AlignContent, reverse: false)
            : ([0.0], 0.0);

        var lines = new List<ColumnLine>();
        for (var l = 0; l < ranges.Count; l++)
        {
            var (start, end) = ranges[l];
            var count = end - start;
            var lineInputs = inputs.GetRange(start, count);
            var natural = lineInputs.Sum(i => i.Hypothetical + i.Margins) + (gap * (count - 1));
            var inner = InnerSize(definite, natural, minInner, maxInner);
            var sizes = FlexAlgorithm.ResolveFlexibleLengths(lineInputs, inner - (gap * (count - 1)));

            var outer = new double[count];
            var autoCounts = new int[count];
            for (var i = 0; i < count; i++)
            {
                outer[i] = sizes[i] + lineInputs[i].Margins;
                var auto = Auto(items[start + i]);
                autoCounts[i] = ((auto & AutoMargins.Top) != 0 ? 1 : 0) + ((auto & AutoMargins.Bottom) != 0 ? 1 : 0);
            }

            var (starts, autoMargin) = FlexAlgorithm.Justify(outer, autoCounts, inner, gap, flex.JustifyContent);
            var lineWidth = lineWidths[l] + extra;
            var placed = new List<ColumnItem>(count);
            for (var i = 0; i < count; i++)
            {
                var item = items[start + i];
                var margin = item.Content.Style.Margin;
                var marginTop = (Auto(item) & AutoMargins.Top) != 0 ? autoMargin : margin.Top;
                var marginBottom = (Auto(item) & AutoMargins.Bottom) != 0 ? autoMargin : MarginOrZero(item, AutoMargins.Bottom, margin.Bottom);
                var top = starts[i];
                if (flex.Direction == FlexDirection.ColumnReverse)
                {
                    top = inner - top - (marginTop + sizes[i] + marginBottom);
                }

                var (x, boxWidth) = ColumnCross(flex, item, lineWidth);
                placed.Add(new ColumnItem(item, top, sizes[i], x, boxWidth, marginTop, marginBottom));
            }

            var lineX = reverseLines ? width - lineStarts[l] - lineWidth : lineStarts[l];
            lines.Add(new ColumnLine([.. placed.OrderBy(p => p.Top)], lineX, lineWidth));
        }

        return lines;
    }

    /// <summary>
    /// A column container: each line stacks its items top to bottom (they may continue on the next pages); wrapped
    /// lines sit side by side.
    /// </summary>
    private void LayoutFlexColumns(FlexBlock flex, List<FlexItem> items, double x, double width, double? definiteHeight)
    {
        var lines = ComputeColumns(flex, items, width, definiteHeight);
        if (lines.Count == 1)
        {
            LayoutColumnLine(lines[0], x);
        }
        else
        {
            LayoutSideBySide(lines, (line, _) => LayoutColumnLine(line, x));
        }
    }

    private void LayoutColumnLine(ColumnLine line, double x)
    {
        var cursor = 0.0; // Where the previous item's margin box ended, in unbroken coordinates.
        Block? previous = null;
        foreach (var placed in line.Items)
        {
            var content = placed.Item.Content;
            var style = content.Style;
            var forced = style.BreakBefore == PageBreak.Page || previous?.Style.BreakAfter == PageBreak.Page;
            if (forced && !_static && !(AtPageTop && _pages[PageIndex].Count == 0))
            {
                NewPage(forced: true);
            }
            else if (!(AtPageTop && TruncateMargins))
            {
                Y += placed.Top - cursor + placed.MarginTop; // Gap, justify-content spacing and the top margin.
            }

            cursor = placed.Top + placed.MarginTop + placed.Height + placed.MarginBottom;
            var needed = style.BreakInside == PageBreakInside.Avoid || style.Height is not null ? placed.Height : MinimumStartBox(content, placed.Width);
            if (!_static && !AtPageTop && needed > Remaining + Epsilon && needed <= _setup.ContentHeight + Epsilon)
            {
                NewPage();
            }

            AddMarker(content);
            LayoutBlock(content, x + line.X + placed.X, placed.Width, new BoxSizing(Height: placed.Height, Isolated: true, DefiniteHeight: placed.Height));
            Y += placed.MarginBottom;
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
            var (lines, starts) = ComputeRows(flex, items, width);
            return starts[^1] + lines[^1].CrossSize;
        }

        return ComputeColumns(flex, items, width).Max(l => l.Items.Count == 0 ? 0 : l.Items.Max(p => p.Top + p.MarginTop + p.Height + p.MarginBottom));
    }

    /// <summary>The height a flex container's content needs at the bottom of a page before it is worth starting there.</summary>
    private double MinimumFlexStart(FlexBlock flex, double width)
    {
        var items = flex.OrderedItems();
        if (flex.IsRow)
        {
            var line = ComputeRows(flex, items, width).Lines[0];
            return line.Items.Max(p => p.MarginTop + MinimumStartBox(p.Item.Content, p.Width));
        }

        return ComputeColumns(flex, items, width).Max(l =>
        {
            var first = l.Items[0];
            return first.Top + first.MarginTop + (first.Item.Content.Style.Height is null ? MinimumStartBox(first.Item.Content, first.Width) : first.Height);
        });
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
                var (rows, starts) = ComputeRows(flex, flex.OrderedItems(), contentWidth);
                var placed = rows[0].Items[0];
                var (offset, _, _) = CrossPlacement(flex, rows[0], placed);
                return FirstBaseline(placed.Item.Content, placed.Width) is { } itemBaseline ? top + starts[0] + offset + placed.MarginTop + itemBaseline : null;
            default:
                return null;
        }
    }
}
