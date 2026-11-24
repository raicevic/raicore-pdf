using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.Xfa.Template;

/// <summary>Insets (XFA <c>margin</c>), in points.</summary>
internal readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    public double Horizontal => Left + Right;

    public double Vertical => Top + Bottom;
}

/// <summary>One edge of a border, a line or a shape (XFA <c>edge</c>).</summary>
internal sealed record Edge(bool Visible, double Thickness, PdfColor Color, string Stroke, string Cap);

/// <summary>A border (XFA <c>border</c>): four edges (top, right, bottom, left), the corner radius and the fill.</summary>
internal sealed record Border(Edge[] Edges, double Radius, PdfColor? Fill, string Hand);

/// <summary>A font (XFA <c>font</c>).</summary>
internal sealed record FontSpec(
    string Typeface,
    double Size,
    bool Bold,
    bool Italic,
    int Underline,
    bool LineThrough,
    PdfColor Color,
    double BaselineShift,
    double LetterSpacing)
{
    /// <summary>XFA's default font: Courier, 10 points, black.</summary>
    public static FontSpec Default { get; } = new("Courier", 10, false, false, 0, false, PdfColor.Black, 0, 0);
}

/// <summary>Paragraph formatting (XFA <c>para</c>).</summary>
internal sealed record ParaSpec(
    string HAlign,
    string VAlign,
    double SpaceAbove,
    double SpaceBelow,
    double MarginLeft,
    double MarginRight,
    double TextIndent,
    double LineHeight)
{
    public static ParaSpec Default { get; } = new("left", "top", 0, 0, 0, 0, 0, 0);
}

/// <summary>Reads the properties of template elements, with XFA's defaults.</summary>
internal static class Props
{
    /// <summary>The default edge: visible, 0.5 points, black, solid.</summary>
    private static readonly Edge DefaultEdge = new(true, 0.5, PdfColor.Black, "solid", "square");

    /// <summary>An element's margin (its <c>margin</c> child), zero where not set.</summary>
    public static Insets Margin(XElement? owner)
    {
        var margin = owner.Child("margin");
        return margin is null
            ? default
            : new Insets(
                Math.Max(margin.Measure("topInset"), 0),
                Math.Max(margin.Measure("rightInset"), 0),
                Math.Max(margin.Measure("bottomInset"), 0),
                Math.Max(margin.Measure("leftInset"), 0));
    }

    /// <summary>A color (<c>color value="r,g,b"</c>), or <paramref name="fallback"/>.</summary>
    public static PdfColor Color(XElement? color, PdfColor fallback)
    {
        var value = color.Attr("value");
        if (value is null)
        {
            return fallback;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return fallback;
        }

        var rgb = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var component))
            {
                return fallback;
            }

            rgb[i] = (byte)Math.Clamp(component, 0, 255);
        }

        return PdfColor.FromRgb(rgb[0], rgb[1], rgb[2]);
    }

    /// <summary>
    /// A fill's color (<c>fill</c>: its color, white by default; a gradient or pattern as the average of its two colors),
    /// or <c>null</c> when there is no fill or it is hidden.
    /// </summary>
    public static PdfColor? Fill(XElement? fill)
    {
        if (fill is null || fill.Attr("presence") is "hidden" or "invisible" or "inactive")
        {
            return null;
        }

        var color = Color(fill.Child("color"), PdfColor.White);
        var second = fill.Elements().FirstOrDefault(e => e.Name.LocalName is "linear" or "radial" or "pattern" or "stipple");
        if (second is not null && second.Child("color") is { } other)
        {
            var end = Color(other, PdfColor.Black);
            var weight = second.Name.LocalName == "stipple" ? Math.Clamp(second.Int("rate", 50), 0, 100) / 100.0 : 0.5;
            color = new PdfColor(
                (color.Red * (1 - weight)) + (end.Red * weight),
                (color.Green * (1 - weight)) + (end.Green * weight),
                (color.Blue * (1 - weight)) + (end.Blue * weight));
        }

        return color;
    }

    /// <summary>An edge (<c>edge</c>, or the default edge for <c>null</c>).</summary>
    public static Edge ReadEdge(XElement? edge)
    {
        if (edge is null)
        {
            return DefaultEdge;
        }

        var visible = edge.Attr("presence") is not ("hidden" or "invisible" or "inactive");
        var thickness = Math.Clamp(edge.Measure("thickness", 0.5), 0, 1000);
        return new Edge(visible, thickness, Color(edge.Child("color"), PdfColor.Black), edge.Attr("stroke") ?? "solid", edge.Attr("cap") ?? "square");
    }

    /// <summary>
    /// A border (<c>border</c>), or <c>null</c> when there is none or it is hidden. Fewer than four edges repeat the last
    /// one; none is one default edge.
    /// </summary>
    public static Border? ReadBorder(XElement? border)
    {
        if (border is null || border.Attr("presence") is "hidden" or "invisible" or "inactive")
        {
            return null;
        }

        var edges = border.Children("edge").Take(4).Select(ReadEdge).ToList();
        if (edges.Count == 0)
        {
            edges.Add(DefaultEdge);
        }

        while (edges.Count < 4)
        {
            edges.Add(edges[^1]);
        }

        var radius = border.Children("corner").Select(c => c.Measure("radius")).DefaultIfEmpty(0).Max();
        return new Border([.. edges], Math.Clamp(radius, 0, 1000), Fill(border.Child("fill")), border.Attr("hand") ?? "even");
    }

    /// <summary>A font (<c>font</c>), with what it does not set taken from <paramref name="inherited"/> (or XFA's defaults).</summary>
    public static FontSpec ReadFont(XElement? font, FontSpec? inherited = null)
    {
        var basis = inherited ?? FontSpec.Default;
        if (font is null)
        {
            return basis;
        }

        var size = font.MeasureOrNull("size", "pt") is { } s and > 0 ? Math.Min(s, 1000) : basis.Size;
        return new FontSpec(
            font.Attr("typeface") is { Length: > 0 } typeface ? typeface : basis.Typeface,
            size,
            font.Attr("weight") is { } weight ? weight == "bold" : basis.Bold,
            font.Attr("posture") is { } posture ? posture == "italic" : basis.Italic,
            font.Attr("underline") is { } underline ? Math.Clamp(font.Int("underline", 0), 0, 2) : basis.Underline,
            font.Attr("lineThrough") is { } through ? through != "0" : basis.LineThrough,
            font.Child("fill") is { } fill ? Color(fill.Child("color"), PdfColor.Black) : basis.Color,
            font.MeasureOrNull("baselineShift", "pt") ?? basis.BaselineShift,
            font.MeasureOrNull("letterSpacing", "pt") ?? basis.LetterSpacing);
    }

    /// <summary>Paragraph formatting (<c>para</c>), with what it does not set taken from <paramref name="inherited"/>.</summary>
    public static ParaSpec ReadPara(XElement? para, ParaSpec? inherited = null)
    {
        var basis = inherited ?? ParaSpec.Default;
        if (para is null)
        {
            return basis;
        }

        return new ParaSpec(
            para.Attr("hAlign") ?? basis.HAlign,
            para.Attr("vAlign") ?? basis.VAlign,
            para.MeasureOrNull("spaceAbove") ?? basis.SpaceAbove,
            para.MeasureOrNull("spaceBelow") ?? basis.SpaceBelow,
            para.MeasureOrNull("marginLeft") ?? basis.MarginLeft,
            para.MeasureOrNull("marginRight") ?? basis.MarginRight,
            para.MeasureOrNull("textIndent") ?? basis.TextIndent,
            Math.Max(para.MeasureOrNull("lineHeight", "pt") ?? basis.LineHeight, 0));
    }
}
