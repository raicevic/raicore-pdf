using System.Xml.Linq;
using RaiCore.Pdf.DrawingML;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>
/// The scheme colors a slide sees: its theme's colors through the color map of its master (and the overrides of its
/// layout or slide), which maps <c>bg1</c>, <c>tx1</c>, ... to <c>lt1</c>, <c>dk1</c>, ....
/// </summary>
/// <param name="theme">The theme.</param>
/// <param name="map">The color map (<c>bg1</c> to <c>lt1</c> and so on).</param>
internal sealed class ColorScheme(Theme theme, IReadOnlyDictionary<string, string> map)
{
    /// <summary>The theme.</summary>
    public Theme Theme => theme;

    /// <summary>The color map.</summary>
    public IReadOnlyDictionary<string, string> Map => map;

    /// <summary>A scheme color by name (<c>tx1</c>, <c>accent2</c>, <c>dk1</c>), or <c>null</c>.</summary>
    public PdfColor? Color(string name)
    {
        if (map.TryGetValue(name, out var mapped))
        {
            name = mapped;
        }
        else
        {
            // Without a map entry, the usual mapping of the aliases.
            name = name switch
            {
                "bg1" => "lt1",
                "tx1" => "dk1",
                "bg2" => "lt2",
                "tx2" => "dk2",
                _ => name,
            };
        }

        return theme.Colors.TryGetValue(name, out var color) ? color : null;
    }

    /// <summary>Reads a color map element (<c>p:clrMap</c>, or an override's <c>a:overrideClrMapping</c>).</summary>
    public static Dictionary<string, string> ReadMap(XElement? element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element is not null)
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.NamespaceName.Length == 0)
                {
                    map[attribute.Name.LocalName] = attribute.Value;
                }
            }
        }

        return map;
    }
}

/// <summary>
/// DrawingML colors with a slide's scheme colors: the color logic itself is shared with the chart engine
/// (<see cref="DrawingMLColors"/>); this adds the slide's <see cref="ColorScheme"/> and the presentation's default theme.
/// </summary>
internal static class DrawingColors
{
    /// <summary>
    /// The color of a color element (<c>a:srgbClr</c>, <c>a:schemeClr</c>, ...) with its transforms applied, or
    /// <c>null</c> if it is not a color element.
    /// </summary>
    /// <param name="color">The color element.</param>
    /// <param name="scheme">The scheme colors, or <c>null</c> where scheme colors cannot be used (in a theme).</param>
    /// <param name="placeholder">The color <c>phClr</c> stands for (a style reference's color), or <c>null</c>.</param>
    public static PdfColor? Resolve(XElement color, ColorScheme? scheme, PdfColor? placeholder) =>
        DrawingMLColors.Resolve(color, Resolver(scheme), placeholder);

    /// <summary>The first color of an element that has a color child (such as <c>a:solidFill</c>), or <c>null</c>.</summary>
    public static PdfColor? Child(XElement? element, ColorScheme? scheme, PdfColor? placeholder) =>
        DrawingMLColors.Child(element, Resolver(scheme), placeholder);

    /// <summary>The color of a color element before its transforms.</summary>
    internal static PdfColor? Base(XElement color, ColorScheme? scheme, PdfColor? placeholder) =>
        DrawingMLColors.Base(color, Resolver(scheme), placeholder);

    /// <summary>The scheme color resolver of a slide (the default theme's colors without one).</summary>
    internal static Func<string, PdfColor?> Resolver(ColorScheme? scheme) =>
        scheme is null ? name => Theme.Default.Colors.TryGetValue(name, out var color) ? color : null : scheme.Color;

    /// <summary>An RGB color in hex (<c>4472C4</c>), or <c>null</c>.</summary>
    internal static PdfColor? Hex(string? value) => DrawingMLColors.Hex(value);

    /// <summary>sRGB to linear light.</summary>
    internal static double ToLinear(double c) => DrawingMLColors.ToLinear(c);

    /// <summary>Linear light to sRGB.</summary>
    internal static double ToSrgb(double c) => DrawingMLColors.ToSrgb(c);

    /// <summary>RGB (0..1) to hue, saturation and luminance (0..1).</summary>
    internal static (double H, double S, double L) ToHsl(double r, double g, double b) => DrawingMLColors.ToHsl(r, g, b);

    /// <summary>Hue, saturation and luminance (0..1) to a color.</summary>
    internal static PdfColor FromHsl(double h, double s, double l, double alpha) => DrawingMLColors.FromHsl(h, s, l, alpha);
}
