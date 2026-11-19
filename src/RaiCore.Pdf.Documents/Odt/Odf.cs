using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>OpenDocument namespaces and value parsing: lengths, percentages, colors, borders and number formats.</summary>
internal static class Odf
{
    public static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    public static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    public static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    public static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    public static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    public static readonly XNamespace Config = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    public static readonly XNamespace Xml = XNamespace.Xml;

    /// <summary>LibreOffice's extensions (ODF 1.2 extended), which hold some ODF 1.3 features in older files.</summary>
    public static readonly XNamespace LibreOffice = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    /// <summary>Whether an element has a name in a namespace.</summary>
    public static bool Is(XElement? element, XNamespace ns, string name) =>
        element is not null && element.Name.LocalName == name && element.Name.Namespace == ns;

    /// <summary>The first child element with a name, or <c>null</c>.</summary>
    public static XElement? Child(XElement? element, XNamespace ns, string name) => element?.Element(ns + name);

    /// <summary>The child elements with a name.</summary>
    public static IEnumerable<XElement> Children(XElement? element, XNamespace ns, string name) => element?.Elements(ns + name) ?? [];

    /// <summary>An attribute's value, or <c>null</c>.</summary>
    public static string? Attr(XElement? element, XNamespace ns, string name) => element?.Attribute(ns + name)?.Value;

    /// <summary>An integer attribute, or <c>null</c> when absent or not a number.</summary>
    public static int? Int(XElement? element, XNamespace ns, string name) =>
        int.TryParse(Attr(element, ns, name)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// A length (<c>2cm</c>, <c>0.5in</c>, <c>12pt</c>, <c>10mm</c>, <c>1pc</c>, <c>3px</c>) in points, finite and
    /// bounded, or <c>null</c> for anything else (percentages included).
    /// </summary>
    public static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        var end = text.Length;
        while (end > 0 && char.IsAsciiLetter(text[end - 1]))
        {
            end--;
        }

        var unit = text[end..].ToLowerInvariant() switch
        {
            "pt" => 1.0,
            "in" or "inch" => 72,
            "cm" => 72 / 2.54,
            "mm" => 72 / 25.4,
            "pc" or "pi" => 12,
            "px" => 0.75,
            "" => 1, // A bare number: treated as points.
            _ => double.NaN,
        };
        if (double.IsNaN(unit) || !double.TryParse(text.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var points = value * unit;
        return double.IsFinite(points) ? Math.Clamp(points, -100_000, 100_000) : null;
    }

    /// <summary>A length attribute in points, or <c>null</c>.</summary>
    public static double? Length(XElement? element, XNamespace ns, string name) => Length(Attr(element, ns, name));

    /// <summary>A percentage (<c>150%</c>) as a number (150), finite and bounded, or <c>null</c>.</summary>
    public static double? Percent(string? text)
    {
        if (text is null)
        {
            return null;
        }

        text = text.Trim();
        return text.EndsWith('%') && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            ? Math.Clamp(value, -100_000, 100_000)
            : null;
    }

    /// <summary>A color (<c>#RRGGBB</c>, also <c>#RGB</c>), or <c>null</c> for <c>transparent</c> or unreadable values.</summary>
    public static PdfColor? Color(string? text)
    {
        if (text is null)
        {
            return null;
        }

        text = text.Trim();
        if (text.Length == 4 && text[0] == '#')
        {
            text = string.Concat("#", text[1], text[1], text[2], text[2], text[3], text[3]);
        }

        return text.Length == 7 && text[0] == '#' && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
    }

    /// <summary>
    /// A border (<c>0.06pt solid #000000</c>, its parts in any order, or <c>none</c>), or <c>null</c> when absent or
    /// unreadable.
    /// </summary>
    public static BorderLine? Border(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        double width = 0.75;
        var style = BorderLineStyle.Single;
        var color = PdfColor.Black;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "none":
                case "hidden":
                    return BorderLine.None;
                case "solid":
                case "groove":
                case "ridge":
                case "inset":
                case "outset":
                    style = BorderLineStyle.Single;
                    break;
                case "double":
                case "double-thin":
                    style = BorderLineStyle.Double;
                    break;
                case "dotted":
                    style = BorderLineStyle.Dotted;
                    break;
                case "dashed":
                case "fine-dashed":
                case "dash-dot":
                case "dash-dot-dot":
                    style = BorderLineStyle.Dashed;
                    break;
                case "thin":
                    width = 0.5;
                    break;
                case "medium":
                    width = 1;
                    break;
                case "thick":
                    width = 2;
                    break;
                default:
                    if (Color(part) is { } c)
                    {
                        color = c;
                    }
                    else if (Length(part) is { } w)
                    {
                        width = Math.Clamp(w, 0, 72);
                    }

                    break;
            }
        }

        return width > 0 ? new BorderLine(width, color, style) : BorderLine.None;
    }

    /// <summary>The number format of a <c>style:num-format</c> (<c>1</c>, <c>a</c>, <c>A</c>, <c>i</c>, <c>I</c>, empty for none).</summary>
    public static NumberFormat NumberFormatOf(string? format) => format switch
    {
        null => NumberFormat.Decimal,
        "" => NumberFormat.None,
        "a" => NumberFormat.LowerLetter,
        "A" => NumberFormat.UpperLetter,
        "i" => NumberFormat.LowerRoman,
        "I" => NumberFormat.UpperRoman,
        _ => NumberFormat.Decimal,
    };

    /// <summary>A font family name without the quotes and further families of a list (<c>'Liberation Serif', serif</c>).</summary>
    public static string? FamilyName(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        var first = family.Split(',')[0].Trim().Trim('\'', '"').Trim();
        return first.Length > 0 ? first : null;
    }

    /// <summary>The plain text of an element (all its text, as written).</summary>
    public static string TextOf(XElement element) => string.Concat(element.DescendantNodes().OfType<XText>().Select(t => t.Value));
}
