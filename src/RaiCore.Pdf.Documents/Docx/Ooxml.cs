using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>
/// Reading WordprocessingML values: elements by local name in the main namespace (transitional or strict),
/// attributes by local name, on/off values, measures (twips, half-points, eighths of a point, EMUs and units) and colors.
/// </summary>
internal static class Ooxml
{
    /// <summary>The main namespace of transitional documents.</summary>
    public const string Main = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>The main namespace of strict documents.</summary>
    public const string StrictMain = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    /// <summary>The markup compatibility namespace (<c>mc:AlternateContent</c>).</summary>
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>Points per EMU (English Metric Unit, 914,400 per inch).</summary>
    public const double PointsPerEmu = 72.0 / 914400;

    /// <summary>Whether an element is in the main namespace.</summary>
    public static bool IsW(XElement element) => element.Name.NamespaceName is Main or StrictMain;

    /// <summary>Whether an element is <c>w:name</c>.</summary>
    public static bool Is(XElement element, string name) => element.Name.LocalName == name && IsW(element);

    /// <summary>The first child <c>w:name</c>, or <c>null</c>.</summary>
    public static XElement? Child(XElement? element, string name)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == name && IsW(child))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>The children <c>w:name</c>.</summary>
    public static IEnumerable<XElement> Children(XElement? element, string name) =>
        element is null ? [] : element.Elements().Where(c => c.Name.LocalName == name && IsW(c));

    /// <summary>An attribute's value by local name (any namespace), or <c>null</c>.</summary>
    public static string? Attr(XElement? element, string name)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName == name)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>The <c>w:val</c> of a child, or <c>null</c>.</summary>
    public static string? Val(XElement? element, string child) => Attr(Child(element, child), "val");

    /// <summary>
    /// An on/off property (<c>&lt;w:b/&gt;</c>, <c>&lt;w:b w:val="0"/&gt;</c>): <c>null</c> if the element is absent.
    /// </summary>
    public static bool? OnOff(XElement? element, string child) => Child(element, child) is { } property ? OnOff(property) : null;

    /// <summary>The value of an on/off element: true unless its <c>val</c> says false.</summary>
    public static bool OnOff(XElement property) => Attr(property, "val") is not { } value || IsOn(value);

    /// <summary>Whether an on/off value is on (anything but <c>0</c>, <c>false</c>, <c>off</c> and <c>none</c>).</summary>
    public static bool IsOn(string value) => value is not ("0" or "false" or "off" or "none");

    /// <summary>An integer attribute, or <c>null</c> if absent or unreadable.</summary>
    public static int? Int(XElement? element, string name) =>
        Attr(element, name) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// A measure in twentieths of a point (twips), or with a unit (<c>2.5cm</c>, <c>1in</c>, <c>12pt</c>), in points;
    /// <c>null</c> if absent or unreadable.
    /// </summary>
    public static double? Twips(XElement? element, string name) => Measure(Attr(element, name), 1 / 20.0);

    /// <summary>A measure in half-points (font sizes), or with a unit, in points.</summary>
    public static double? HalfPoints(XElement? element, string name) => Measure(Attr(element, name), 0.5);

    /// <summary>A measure in EMUs, or with a unit, in points.</summary>
    public static double? Emu(XElement? element, string name) => Measure(Attr(element, name), PointsPerEmu);

    /// <summary>A number in units of <paramref name="scale"/> points, or a number with a unit; finite and bounded.</summary>
    public static double? Measure(string? text, double scale)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        var unit = 1.0;
        var number = text;
        if (text.Length > 2 && char.IsAsciiLetter(text[^1]) && char.IsAsciiLetter(text[^2]))
        {
            number = text[..^2];
            switch (text[^2..].ToLowerInvariant())
            {
                case "pt":
                    unit = 1;
                    break;
                case "in":
                    unit = 72;
                    break;
                case "cm":
                    unit = 72 / 2.54;
                    break;
                case "mm":
                    unit = 72 / 25.4;
                    break;
                case "pc":
                case "pi":
                    unit = 12;
                    break;
                default:
                    return null;
            }

            scale = 1;
        }

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var points = value * scale * unit;
        return double.IsFinite(points) ? Math.Clamp(points, -100_000, 100_000) : null;
    }

    /// <summary>
    /// A color (<c>RRGGBB</c>), or <c>null</c> for <c>auto</c>, absent or unreadable values. Theme colors are
    /// resolved by <paramref name="theme"/> when the value itself is not a color.
    /// </summary>
    public static PdfColor? Color(string? value, string? themeColor = null, DocxTheme? theme = null)
    {
        if (value is { Length: 6 } && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return themeColor is not null && theme is not null ? theme.Color(themeColor) : null;
    }

    /// <summary>The highlight colors by name.</summary>
    public static PdfColor? Highlight(string? name) => name switch
    {
        "yellow" => PdfColor.FromRgb(255, 255, 0),
        "green" => PdfColor.FromRgb(0, 255, 0),
        "cyan" => PdfColor.FromRgb(0, 255, 255),
        "magenta" => PdfColor.FromRgb(255, 0, 255),
        "blue" => PdfColor.FromRgb(0, 0, 255),
        "red" => PdfColor.FromRgb(255, 0, 0),
        "darkBlue" => PdfColor.FromRgb(0, 0, 128),
        "darkCyan" => PdfColor.FromRgb(0, 128, 128),
        "darkGreen" => PdfColor.FromRgb(0, 128, 0),
        "darkMagenta" => PdfColor.FromRgb(128, 0, 128),
        "darkRed" => PdfColor.FromRgb(128, 0, 0),
        "darkYellow" => PdfColor.FromRgb(128, 128, 0),
        "darkGray" => PdfColor.FromRgb(128, 128, 128),
        "lightGray" => PdfColor.FromRgb(192, 192, 192),
        "black" => PdfColor.Black,
        "white" => PdfColor.FromRgb(255, 255, 255),
        _ => null,
    };

    /// <summary>
    /// A border element (<c>w:top</c> of <c>w:pBdr</c>, <c>w:tcBorders</c>, <c>w:tblBorders</c>), or <c>null</c> if
    /// absent. <c>nil</c> and <c>none</c> give <see cref="BorderLine.None"/>, which removes an inherited border.
    /// </summary>
    public static BorderLine? Border(XElement? element, DocxTheme? theme)
    {
        if (element is null)
        {
            return null;
        }

        var value = Attr(element, "val");
        if (value is null or "nil" or "none")
        {
            return BorderLine.None;
        }

        // Sizes are in eighths of a point (2 to 96); art borders measure in points, but are drawn as single lines here.
        var width = Math.Clamp((Measure(Attr(element, "sz"), 1 / 8.0) ?? 0.5), 0.25, 12);
        var color = Color(Attr(element, "color"), Attr(element, "themeColor"), theme) ?? PdfColor.Black;
        var style = value switch
        {
            "double" or "triple" or "thinThickSmallGap" or "thickThinSmallGap" or "thinThickMediumGap" or "thickThinMediumGap"
                or "thinThickLargeGap" or "thickThinLargeGap" or "thinThickThinSmallGap" or "thinThickThinMediumGap" or "thinThickThinLargeGap"
                or "doubleWave" => BorderLineStyle.Double,
            "dotted" or "dotDash" or "dotDotDash" => BorderLineStyle.Dotted,
            "dashed" or "dashSmallGap" or "dashDotStroked" => BorderLineStyle.Dashed,
            _ => BorderLineStyle.Single,
        };

        // A double line needs room for two lines and the gap between them.
        return new BorderLine(style == BorderLineStyle.Double ? Math.Max(width * 3, 1.5) : width, color, style);
    }

    /// <summary>The fill color of a <c>w:shd</c> element, or <c>null</c> for none, <c>auto</c> or a clear fill.</summary>
    public static PdfColor? Shading(XElement? shd, DocxTheme? theme)
    {
        if (shd is null)
        {
            return null;
        }

        var pattern = Attr(shd, "val");
        if (pattern is "nil")
        {
            return null;
        }

        var fill = Color(Attr(shd, "fill"), Attr(shd, "themeFill"), theme);
        if (pattern is "solid")
        {
            // A solid pattern is the pattern color all over.
            return Color(Attr(shd, "color"), Attr(shd, "themeColor"), theme) ?? fill ?? PdfColor.Black;
        }

        if (pattern is { } p && p.StartsWith("pct", StringComparison.Ordinal)
            && int.TryParse(p.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) && percent is > 0 and <= 100)
        {
            // A percentage pattern mixes the pattern color into the fill.
            var front = Color(Attr(shd, "color"), Attr(shd, "themeColor"), theme) ?? PdfColor.Black;
            var back = fill ?? PdfColor.FromRgb(255, 255, 255);
            return Mix(back, front, percent / 100.0);
        }

        return fill;
    }

    /// <summary>A mix of two colors: <paramref name="amount"/> of <paramref name="b"/> into <paramref name="a"/>.</summary>
    private static PdfColor Mix(PdfColor a, PdfColor b, double amount) => new(
        (a.Red * (1 - amount)) + (b.Red * amount), (a.Green * (1 - amount)) + (b.Green * amount), (a.Blue * (1 - amount)) + (b.Blue * amount));

    /// <summary>A number format by its name in <c>w:numFmt</c> and <c>w:pgNumType</c>.</summary>
    public static NumberFormat NumberFormatOf(string? name) => name switch
    {
        "decimalZero" => NumberFormat.DecimalZero,
        "lowerLetter" => NumberFormat.LowerLetter,
        "upperLetter" => NumberFormat.UpperLetter,
        "lowerRoman" => NumberFormat.LowerRoman,
        "upperRoman" => NumberFormat.UpperRoman,
        "ordinal" => NumberFormat.Ordinal,
        "bullet" => NumberFormat.Bullet,
        "none" => NumberFormat.None,
        _ => NumberFormat.Decimal,
    };

    /// <summary>
    /// A character of a symbol font (Symbol, Wingdings) as Unicode: the private-use code those fonts use mapped to the
    /// character it shows, for the common bullets; other private-use codes are kept.
    /// </summary>
    public static string SymbolCharacter(int code, string? font)
    {
        if (code is >= 0xF020 and <= 0xF0FF)
        {
            code -= 0xF000;
        }

        var wingdings = font is not null && font.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase);
        var symbol = font is not null && font.Equals("Symbol", StringComparison.OrdinalIgnoreCase);
        if (wingdings)
        {
            return code switch
            {
                0x9F or 0x6C => "\u25CF", // Bullets.
                0xA7 or 0x6E => "\u25AA", // Small squares.
                0xA8 => "\u25FB",
                0x71 or 0x6F => "\u25A1",
                0xD8 or 0xE0 or 0xE8 => "\u27A2", // Arrowheads.
                0xFC => "\u2713", // Check mark.
                0xFB => "\u2717",
                0x76 => "\u2756",
                _ => "\u2022",
            };
        }

        if (symbol)
        {
            return code switch
            {
                0xB7 => "\u2022",
                0x2D => "\u2212",
                0xAE => "\u2192",
                0xDE => "\u21D2",
                0xB0 => "\u00B0",
                0xB1 => "\u00B1",
                0xB4 => "\u00D7",
                0xA5 => "\u221E",
                0xA3 => "\u2264",
                0xB3 => "\u2265",
                0xB9 => "\u2260",
                0xBB => "\u2248",
                >= 0x61 and <= 0x7A => GreekLower[code - 0x61].ToString(),
                >= 0x41 and <= 0x5A => GreekUpper[code - 0x41].ToString(),
                _ => code is >= 0x20 and < 0x7F ? ((char)code).ToString() : "\u2022",
            };
        }

        return code is >= 0x20 and < 0xD800 ? ((char)code).ToString() : "\u2022";
    }

    /// <summary>The Greek letters the Symbol font has at <c>a</c> to <c>z</c>.</summary>
    private static ReadOnlySpan<char> GreekLower => "\u03B1\u03B2\u03C7\u03B4\u03B5\u03C6\u03B3\u03B7\u03B9\u03D5\u03BA\u03BB\u03BC\u03BD\u03BF\u03C0\u03B8\u03C1\u03C3\u03C4\u03C5\u03D6\u03C9\u03BE\u03C8\u03B6";

    /// <summary>The Greek letters the Symbol font has at <c>A</c> to <c>Z</c>.</summary>
    private static ReadOnlySpan<char> GreekUpper => "\u0391\u0392\u03A7\u0394\u0395\u03A6\u0393\u0397\u0399\u03D1\u039A\u039B\u039C\u039D\u039F\u03A0\u0398\u03A1\u03A3\u03A4\u03A5\u03C2\u03A9\u039E\u03A8\u0396";
}
