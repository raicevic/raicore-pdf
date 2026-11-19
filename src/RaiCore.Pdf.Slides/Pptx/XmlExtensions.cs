using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>
/// Element and attribute lookups by local name, so that transitional and strict Office Open XML (which have different
/// namespaces) and the SmartArt drawing's shapes (another namespace for the same elements) read alike.
/// </summary>
internal static class XmlExtensions
{
    /// <summary>EMUs (English Metric Units) per point.</summary>
    public const double EmuPerPoint = 12700;

    /// <summary>The first child element with a local name, or <c>null</c>.</summary>
    public static XElement? El(this XElement? element, string localName)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == localName)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>The element at a path of local names (<c>"spPr", "xfrm"</c>), or <c>null</c>.</summary>
    public static XElement? Path(this XElement? element, params ReadOnlySpan<string> localNames)
    {
        foreach (var name in localNames)
        {
            element = element.El(name);
        }

        return element;
    }

    /// <summary>The child elements with a local name.</summary>
    public static IEnumerable<XElement> Els(this XElement? element, string localName) =>
        element is null ? [] : element.Elements().Where(e => e.Name.LocalName == localName);

    /// <summary>An attribute's value by local name in no namespace, or <c>null</c>.</summary>
    public static string? Attr(this XElement? element, string localName) => (string?)element?.Attribute(localName);

    /// <summary>A relationship id attribute (in the relationships namespace, transitional or strict) by local name.</summary>
    public static string? RelId(this XElement? element, string localName)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName == localName && attribute.Name.NamespaceName.EndsWith("relationships", StringComparison.Ordinal))
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>An integer attribute (EMUs, 1000ths of percent, ...), or <c>null</c> if missing or not a finite number.</summary>
    public static double? Number(this XElement? element, string localName)
    {
        if (element.Attr(localName) is not { } text)
        {
            return null;
        }

        // Strict files may write percentages with a % sign, and lengths with units are not expected here.
        text = text.Trim();
        var percent = text.EndsWith('%');
        if (percent)
        {
            text = text[..^1];
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return null;
        }

        return percent ? value * 1000 : value;
    }

    /// <summary>A length attribute in EMUs as points, or <c>null</c>.</summary>
    public static double? Points(this XElement? element, string localName) =>
        element.Number(localName) is { } emu ? Math.Clamp(emu, -1e12, 1e12) / EmuPerPoint : null;

    /// <summary>A boolean attribute (<c>1</c>, <c>true</c>, <c>on</c>), or <c>null</c>.</summary>
    public static bool? Bool(this XElement? element, string localName) => element.Attr(localName) switch
    {
        "1" or "true" or "on" => true,
        "0" or "false" or "off" => false,
        _ => null,
    };
}
