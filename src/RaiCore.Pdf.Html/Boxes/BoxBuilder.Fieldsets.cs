using AngleSharp.Dom;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// <c>fieldset</c> and its rendered <c>legend</c> (the HTML standard's rendering section 15.3.13): the legend becomes
/// the container's legend (<see cref="ContainerBlock.HasLegend"/>), drawn over the top border.
/// </summary>
internal sealed partial class BoxBuilder
{
    /// <summary>The legends drawn over their fieldset's border, which the fieldset's other children leave out.</summary>
    private readonly HashSet<IElement> _renderedLegends = [];

    /// <summary>
    /// A fieldset's rendered legend: its first <c>legend</c> child that is displayed, not floated and not absolutely
    /// positioned; <c>null</c> for other elements.
    /// </summary>
    private IElement? RenderedLegend(IElement element)
    {
        if (element.LocalName != "fieldset" || element.NamespaceUri != NamespaceNames.HtmlUri)
        {
            return null;
        }

        foreach (var child in element.Children)
        {
            if (child.LocalName == "legend" && child.NamespaceUri == NamespaceNames.HtmlUri
                && Style(child) is { Display: not CssDisplay.None, Float: FloatSide.None, Position: not (Positioning.Absolute or Positioning.Fixed) })
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a fieldset's rendered legend as a block, which the fieldset's children then leave out. The legend is
    /// blockified, and its margins are resolved as if its fieldset were left to right: the layout places it from the
    /// left padding edge (see <see cref="LegendAlignment"/>).
    /// </summary>
    private Block? BuildLegend(IElement legend, double contentWidth)
    {
        _renderedLegends.Add(legend);
        var style = LegendStyle(legend);
        var display = style.Display is CssDisplay.Flex or CssDisplay.InlineFlex ? CssDisplay.Flex : CssDisplay.Block;
        return BuildBlock(legend, style with { Display = display, ContainerRtl = false }, contentWidth, null);
    }

    /// <summary>
    /// A legend's style, with its legacy <c>align</c> attribute as its text-align (a presentational hint: it beats an
    /// inherited text-align, as in Chrome, though here also the legend's own).
    /// </summary>
    private ComputedStyle LegendStyle(IElement legend)
    {
        var style = Style(legend);
        return legend.GetAttribute("align")?.Trim().ToUpperInvariant() switch
        {
            "CENTER" => style with { TextAlign = TextAlignment.Center },
            "RIGHT" => style with { TextAlign = TextAlignment.Right },
            "LEFT" => style with { TextAlign = TextAlignment.Left },
            _ => style,
        };
    }

    /// <summary>
    /// Where a rendered legend sits along its fieldset's top border, as in Chrome: auto margins share the free space;
    /// otherwise the legend's text-align places it (start and justify on the start side).
    /// </summary>
    private static BlockAlignment LegendAlignment(ComputedStyle legend) => (legend.Margin[3].IsAuto, legend.Margin[1].IsAuto) switch
    {
        (true, true) => BlockAlignment.Center,
        (true, false) => BlockAlignment.Right,
        (false, true) => BlockAlignment.Left,
        _ => legend.TextAlign switch
        {
            TextAlignment.Center => BlockAlignment.Center,
            TextAlignment.Left => BlockAlignment.Left,
            TextAlignment.Right => BlockAlignment.Right,
            TextAlignment.End => legend.Rtl ? BlockAlignment.Left : BlockAlignment.Right,
            _ => legend.Rtl ? BlockAlignment.Right : BlockAlignment.Left,
        },
    };
}
