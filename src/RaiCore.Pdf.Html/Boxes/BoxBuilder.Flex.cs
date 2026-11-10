using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>Flex containers (CSS Flexbox): every child element becomes a flex item, and so does each run of loose text.</summary>
internal sealed partial class BoxBuilder
{
    private FlexBlock BuildFlex(IElement element, ComputedStyle style, double containingWidth, bool flexItem)
    {
        var (box, contentWidth) = BoxStyleFor(style, containingWidth, flexItem);
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
                        var content = BuildBlock(child, RunningStyle(childStyle), _pageContentWidth, marker: null);
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
        return new FlexBlock(box, items)
        {
            Direction = style.FlexDirection,
            Wrap = style.FlexWrap,
            JustifyContent = style.JustifyContent,
            AlignItems = style.AlignItems,
            RowGap = style.RowGap.Kind == CssLengthKind.Points ? Math.Max(0, style.RowGap.Value) : 0, // % of an auto height: 0.
            ColumnGap = Math.Max(0, style.ColumnGap.Resolve(contentWidth)),
            Marker = _paged?.MarkerFor(element),
        };
    }

    /// <summary>Flex items are block-level whatever their display (CSS: blockification); flex stays flex.</summary>
    private static ComputedStyle BlockLevel(ComputedStyle style) =>
        style.Display is CssDisplay.Flex or CssDisplay.Table or CssDisplay.ListItem ? style : style with { Display = CssDisplay.Block };

    private FlexItem? BuildFlexItem(IElement container, IElement child, ComputedStyle childStyle, double containerWidth, ref int listIndex)
    {
        var style = BlockLevel(childStyle);
        Block? block;
        if (child.LocalName == "img")
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

        // Percentage heights refer to the container's height, which is its content's (auto), so they are ignored.
        double? Height(CssLength length) => length.Kind == CssLengthKind.Points ? Math.Max(0, length.Value + (style.BorderBox ? 0 : vertical)) : null;

        // In a column the main size is a height: a length flex-basis wins over height, as in CSS.
        var column = Style(container).FlexDirection is FlexDirection.Column or FlexDirection.ColumnReverse;
        var basis = style.FlexBasisContent ? FlexBasis.Content
            : style.FlexBasis.IsAuto || column ? FlexBasis.Auto
            : FlexBasis.Points(Width(style.FlexBasis) ?? 0);
        var height = column && Height(style.FlexBasis) is { } basisHeight ? basisHeight : Height(style.Height);

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
            Height = height,
            MinHeight = Height(style.MinHeight),
            MaxHeight = Height(style.MaxHeight),
            AutoMargins = auto,
        };
    }
}
