namespace RaiCore.Pdf.Layout;

/// <summary>
/// Where a container's legend (<see cref="ContainerBlock.HasLegend"/>) goes: its border box at <paramref name="X"/>,
/// <paramref name="Width"/> wide and <paramref name="Height"/> high, <paramref name="Top"/> below the container's top
/// edge. The container's border and background start <paramref name="Shift"/> lower (centered on the legend), and its
/// padding starts <paramref name="Area"/> below its top edge (below the legend's margin box or the top border, whichever
/// is lower). <paramref name="ContainingWidth"/> is the container's content width.
/// </summary>
internal readonly record struct LegendBox(double X, double Width, double Height, double Top, double Shift, double Area, double ContainingWidth);

/// <summary>The part of a container's top border a legend cuts out: its column from the container's top edge down.</summary>
internal readonly record struct LegendCutout(double X, double Width, double Top, double Height);

/// <summary>
/// Containers with a legend, as HTML <c>fieldset</c> and <c>legend</c> (the HTML standard's rendering section 15.3.13,
/// with Chrome's geometry): the legend shrinks to fit, its border box is centered on the top border (its top margin is
/// ignored), and the content starts below the legend's bottom margin.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>
    /// The legend's place in a container of border-box width <paramref name="width"/> at <paramref name="x"/>, or
    /// <c>null</c> when it has none. Its used style is resolved against the container's content width.
    /// </summary>
    private LegendBox? LegendOf(ContainerBlock container, double x, double width)
    {
        if (!container.HasLegend || container.Children.Count == 0)
        {
            return null;
        }

        var style = StyleOf(container);
        var contentWidth = Math.Max(MinimumContentWidth, width - style.Border.Widths.Fixed.Horizontal - style.Padding.Fixed.Horizontal);
        var legend = container.Children[0];
        var margins = ResolveStyle(legend, contentWidth).Margin.Fixed;
        var legendWidth = FloatBoxWidth(legend, contentWidth);
        var height = MeasureBox(legend, legendWidth);

        // A legend lower than the border is centered in it; a higher one moves the border down to its middle.
        var border = style.Border.Top.Width;
        var top = Math.Max(0, (border - height) / 2);
        var shift = Math.Max(0, (height - border) / 2);
        var area = Math.Max(border, top + height + margins.Bottom);

        var free = Math.Max(0, contentWidth - legendWidth - margins.Horizontal);
        var offset = container.LegendAlignment switch
        {
            BlockAlignment.Center => free / 2,
            BlockAlignment.Right => free,
            _ => 0,
        };
        var legendX = x + style.Border.Widths.Fixed.Left + style.Padding.Fixed.Left + margins.Left + offset;
        return new LegendBox(legendX, legendWidth, height, top, shift, area, contentWidth);
    }

    /// <summary>
    /// Lays out a legend at its place below the container's top edge <paramref name="boxTop"/>, then returns to the
    /// current position (where the container's content starts) unless the legend went on to another page.
    /// </summary>
    private void LayoutLegend(Block legend, LegendBox placed, double boxTop)
    {
        var (page, resume) = (PageIndex, Y);
        ResolveStyle(legend, placed.ContainingWidth);
        Y = boxTop + placed.Top;
        LayoutBlock(legend, placed.X, placed.Width, new BoxSizing(Isolated: true)); // A legend is a formatting context of its own.
        if (PageIndex == page)
        {
            Y = resume;
        }
    }

    /// <summary>
    /// How much more (or less) room a container's top edge takes for its legend than its top border alone, in a
    /// container of border-box width <paramref name="boxWidth"/>; 0 without a legend.
    /// </summary>
    private double LegendExtra(Block block, double boxWidth) =>
        block is ContainerBlock container && LegendOf(container, 0, boxWidth) is { } legend ? legend.Area - StyleOf(block).Border.Top.Width : 0;
}
