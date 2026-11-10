using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>Flex containers (CSS Flexbox): every child element becomes a flex item, and so does each run of loose text.</summary>
internal sealed partial class BoxBuilder
{
    private FlexBlock BuildFlex(IElement element, ComputedStyle style, double containingWidth, bool flexItem)
    {
        var (box, contentWidth) = BoxStyleFor(style, containingWidth, flexItem, _containingHeight);
        var outerHeight = EnterBox(box);
        var items = new List<FlexItem>();
        var collector = new InlineCollector(this, style, contentWidth);
        var listIndex = ListStart(element);

        void FlushText()
        {
            // Contiguous text directly inside the container is wrapped in an anonymous item; whitespace is dropped.
            if (collector.Flush() is { } paragraph)
            {
                items.Add(new FlexItem(paragraph));
            }
        }

        foreach (var node in element.ChildNodes)
        {
            switch (node)
            {
                case IText text:
                    collector.AddText(text.Data, style);
                    break;
                case IElement child:
                    var childStyle = Style(child);
                    if (childStyle.Display == CssDisplay.None || child.LocalName == "br")
                    {
                        break;
                    }

                    FlushText();
                    if (_paged?.RunningName(child) is { } runningName)
                    {
                        // A running element leaves the flow; an empty item keeps its place for element().
                        var content = BuildRunning(child, childStyle);
                        items.Add(new FlexItem(new ContainerBlock { Marker = new Paged.RunningMarker(runningName, content is null ? [] : [content]) }));
                        break;
                    }

                    if (BuildFlexItem(element, child, childStyle, contentWidth, ref listIndex) is { } item)
                    {
                        items.Add(item);
                    }

                    break;
            }
        }

        FlushText();
        var innerHeight = _containingHeight;
        _containingHeight = outerHeight;
        return new FlexBlock(box, items)
        {
            Direction = style.FlexDirection,
            Wrap = style.FlexWrap,
            JustifyContent = style.JustifyContent,
            AlignItems = style.AlignItems,
            AlignContent = style.AlignContent,
            RowGap = Math.Max(0, style.RowGap.Kind == CssLengthKind.Percent ? (innerHeight is { } h ? style.RowGap.Resolve(h) : 0) : style.RowGap.Resolve(0)),
            ColumnGap = Math.Max(0, style.ColumnGap.Resolve(contentWidth)),
            Marker = _paged?.MarkerFor(element),
            Bookmark = BookmarkFor(element),
        };
    }

    /// <summary>Flex items are block-level whatever their display (CSS: blockification); flex stays flex.</summary>
    private static ComputedStyle BlockLevel(ComputedStyle style) =>
        style.Display switch
        {
            CssDisplay.Flex or CssDisplay.Table or CssDisplay.ListItem => style,
            CssDisplay.InlineFlex => style with { Display = CssDisplay.Flex },
            CssDisplay.InlineTable => style with { Display = CssDisplay.Table },
            _ => style with { Display = CssDisplay.Block },
        };

    private FlexItem? BuildFlexItem(IElement container, IElement child, ComputedStyle childStyle, double containerWidth, ref int listIndex)
    {
        var style = BlockLevel(childStyle);
        Block? block;
        if (child.LocalName is "img" or "svg")
        {
            var blocks = new List<Block>();
            var collector = new InlineCollector(this, style, containerWidth);
            AddImage(child, style, containerWidth, blocks, collector);
            Flush(collector, blocks); // Alt text when the image cannot be loaded.
            block = blocks.Count == 1 ? blocks[0] : null;
        }
        else if (style.Display == CssDisplay.Table)
        {
            var blocks = BuildTable(child, style, containerWidth).ToList();
            block = blocks.Count == 1 ? blocks[0] : new ContainerBlock(null, blocks);
        }
        else
        {
            string? marker = null;
            if (style.Display == CssDisplay.ListItem)
            {
                marker = NextListMarker(container, child, style, ref listIndex);
            }

            block = BuildBlock(child, style, containerWidth, marker, flexItem: true);
        }

        if (block is null)
        {
            return null;
        }

        var box = block.Style;
        var horizontal = box.Padding.Horizontal + box.Border.Widths.Horizontal;
        var vertical = box.Padding.Vertical + box.Border.Widths.Vertical;

        // CSS sizes are content-box unless box-sizing: border-box; the layout engine works with border boxes.
        double? Width(CssLength length) => length.IsAuto ? null : Math.Max(0, length.Resolve(containerWidth) + (style.BorderBox ? 0 : horizontal));

        // In a column the main size is a height: a percentage refers to the container's height (resolved in layout).
        var column = Style(container).FlexDirection is FlexDirection.Column or FlexDirection.ColumnReverse;
        var basis = style.FlexBasisContent ? FlexBasis.Content
            : style.FlexBasis.IsAuto ? FlexBasis.Auto
            : column && style.FlexBasis.Kind == CssLengthKind.Percent ? FlexBasis.Percent(style.FlexBasis.Value)
            : column ? FlexBasis.Points(Math.Max(0, style.FlexBasis.Resolve(0) + (style.BorderBox ? 0 : vertical)))
            : FlexBasis.Points(Width(style.FlexBasis) ?? 0);

        var auto = AutoMargins.None;
        if (style.Margin[0].IsAuto)
        {
            auto |= AutoMargins.Top;
        }

        if (style.Margin[1].IsAuto)
        {
            auto |= AutoMargins.Right;
        }

        if (style.Margin[2].IsAuto)
        {
            auto |= AutoMargins.Bottom;
        }

        if (style.Margin[3].IsAuto)
        {
            auto |= AutoMargins.Left;
        }

        return new FlexItem(block)
        {
            Grow = style.FlexGrow,
            Shrink = style.FlexShrink,
            Basis = basis,
            AlignSelf = style.AlignSelf,
            Order = style.Order,
            MinWidth = Width(style.MinWidth),
            MaxWidth = Width(style.MaxWidth),
            AutoMargins = auto,
        };
    }
}
