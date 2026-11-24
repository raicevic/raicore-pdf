using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.Xfa.Template;

/// <summary>
/// Reading XFA XML by local name (the template's namespace changes with its version), with measurements, numbers and
/// enumerations parsed leniently: a value that cannot be read is the default.
/// </summary>
internal static class Xml
{
    /// <summary>The first child element with a local name.</summary>
    public static XElement? Child(this XElement? element, string name)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == name)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>The child elements with a local name.</summary>
    public static IEnumerable<XElement> Children(this XElement? element, string name) =>
        element is null ? [] : element.Elements().Where(e => e.Name.LocalName == name);

    /// <summary>An attribute's value (without a namespace), or <c>null</c>.</summary>
    public static string? Attr(this XElement? element, string name) => element?.Attribute(name)?.Value;

    /// <summary>A measurement attribute in points, or <paramref name="fallback"/>.</summary>
    public static double Measure(this XElement? element, string name, double fallback = 0, string unit = "in") =>
        Measurement.TryParse(element.Attr(name), unit, out var value) ? value : fallback;

    /// <summary>A measurement attribute in points, or <c>null</c> when it is missing or cannot be read.</summary>
    public static double? MeasureOrNull(this XElement? element, string name, string unit = "in") =>
        Measurement.TryParse(element.Attr(name), unit, out var value) ? value : null;

    /// <summary>An integer attribute, or <paramref name="fallback"/>.</summary>
    public static int Int(this XElement? element, string name, int fallback) =>
        int.TryParse(element.Attr(name)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    /// <summary>The element's own text (its text nodes), or an empty string.</summary>
    public static string Text(this XElement? element) => element is null ? string.Empty : string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
}

/// <summary>XFA measurements (XFA 3.3, "Measurements"): a number and a unit (<c>in</c>, <c>cm</c>, <c>mm</c>, <c>pt</c>, <c>mp</c>, <c>em</c>).</summary>
internal static class Measurement
{
    /// <summary>Parses a measurement into points; a number without a unit is in <paramref name="unit"/>.</summary>
    public static bool TryParse(string? text, string unit, out double points)
    {
        points = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            // An exponent only directly after a digit and before a digit or sign ("1e2"); "em" is a unit.
            if (span[end] is 'e' or 'E' && (end + 1 >= span.Length || !(char.IsAsciiDigit(span[end + 1]) || span[end + 1] is '-' or '+')))
            {
                break;
            }

            end++;
        }

        if (end == 0 || !double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            return false;
        }

        var suffix = span[end..].Trim();
        var factor = (suffix.Length == 0 ? unit : suffix.ToString().ToLowerInvariant()) switch
        {
            "in" => 72.0,
            "cm" => 72 / 2.54,
            "mm" => 72 / 25.4,
            "pt" => 1.0,
            "mp" => 0.001,
            "px" => 0.75,
            "em" => 10.0,
            _ => double.NaN,
        };
        if (double.IsNaN(factor))
        {
            return false;
        }

        points = Math.Clamp(value * factor, -1e6, 1e6);
        return true;
    }
}
