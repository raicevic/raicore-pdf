using System.Globalization;
using System.Xml.Linq;

namespace RaiCore.Pdf.DrawingML;

/// <summary>
/// DrawingML colors, shared by the converters and the chart engine: RGB, scheme, system, preset, scRGB and HSL colors,
/// with their transforms (alpha, luminance modulation and offset, tint, shade, saturation, hue, ...). Scheme colors
/// (<c>accent1</c>, <c>tx1</c>, ...) are resolved by the caller, which knows the theme and color map.
/// </summary>
internal static class DrawingMLColors
{
    /// <summary>The colors of the Office theme (2013-2022), used for scheme colors a caller cannot resolve.</summary>
    internal static readonly IReadOnlyDictionary<string, PdfColor> OfficeTheme = new Dictionary<string, PdfColor>(StringComparer.Ordinal)
    {
        ["dk1"] = PdfColor.FromRgb(0, 0, 0),
        ["lt1"] = PdfColor.FromRgb(255, 255, 255),
        ["dk2"] = PdfColor.FromRgb(0x44, 0x54, 0x6A),
        ["lt2"] = PdfColor.FromRgb(0xE7, 0xE6, 0xE6),
        ["accent1"] = PdfColor.FromRgb(0x44, 0x72, 0xC4),
        ["accent2"] = PdfColor.FromRgb(0xED, 0x7D, 0x31),
        ["accent3"] = PdfColor.FromRgb(0xA5, 0xA5, 0xA5),
        ["accent4"] = PdfColor.FromRgb(0xFF, 0xC0, 0x00),
        ["accent5"] = PdfColor.FromRgb(0x5B, 0x9B, 0xD5),
        ["accent6"] = PdfColor.FromRgb(0x70, 0xAD, 0x47),
        ["hlink"] = PdfColor.FromRgb(0x05, 0x63, 0xC1),
        ["folHlink"] = PdfColor.FromRgb(0x95, 0x4F, 0x72),
    };

    private static readonly Dictionary<string, int> Presets = new(StringComparer.Ordinal)
    {
        ["black"] = 0x000000, ["white"] = 0xFFFFFF, ["red"] = 0xFF0000, ["green"] = 0x008000, ["blue"] = 0x0000FF,
        ["yellow"] = 0xFFFF00, ["cyan"] = 0x00FFFF, ["aqua"] = 0x00FFFF, ["magenta"] = 0xFF00FF, ["fuchsia"] = 0xFF00FF,
        ["gray"] = 0x808080, ["grey"] = 0x808080, ["silver"] = 0xC0C0C0, ["maroon"] = 0x800000, ["olive"] = 0x808000,
        ["lime"] = 0x00FF00, ["navy"] = 0x000080, ["purple"] = 0x800080, ["teal"] = 0x008080, ["orange"] = 0xFFA500,
        ["brown"] = 0xA52A2A, ["pink"] = 0xFFC0CB, ["gold"] = 0xFFD700, ["violet"] = 0xEE82EE, ["indigo"] = 0x4B0082,
        ["dkBlue"] = 0x00008B, ["dkRed"] = 0x8B0000, ["dkGreen"] = 0x006400, ["dkGray"] = 0xA9A9A9, ["dkGrey"] = 0xA9A9A9,
        ["dkCyan"] = 0x008B8B, ["dkMagenta"] = 0x8B008B, ["dkOrange"] = 0xFF8C00, ["dkViolet"] = 0x9400D3,
        ["ltBlue"] = 0xADD8E6, ["ltGreen"] = 0x90EE90, ["ltGray"] = 0xD3D3D3, ["ltGrey"] = 0xD3D3D3, ["ltYellow"] = 0xFFFFE0,
        ["ltCyan"] = 0xE0FFFF, ["ltPink"] = 0xFFB6C1, ["ltCoral"] = 0xF08080, ["coral"] = 0xFF7F50, ["crimson"] = 0xDC143C,
        ["tomato"] = 0xFF6347, ["salmon"] = 0xFA8072, ["khaki"] = 0xF0E68C, ["beige"] = 0xF5F5DC, ["ivory"] = 0xFFFFF0,
        ["tan"] = 0xD2B48C, ["chocolate"] = 0xD2691E, ["skyBlue"] = 0x87CEEB, ["steelBlue"] = 0x4682B4,
        ["royalBlue"] = 0x4169E1, ["slateGray"] = 0x708090, ["forestGreen"] = 0x228B22, ["seaGreen"] = 0x2E8B57,
        ["darkGray"] = 0xA9A9A9, ["lightGray"] = 0xD3D3D3, ["whiteSmoke"] = 0xF5F5F5, ["gainsboro"] = 0xDCDCDC,
    };

    /// <summary>
    /// The color of a color element (<c>a:srgbClr</c>, <c>a:schemeClr</c>, ...) with its transforms applied, or
    /// <c>null</c> if it is not a color element.
    /// </summary>
    /// <param name="color">The color element.</param>
    /// <param name="scheme">Resolves a scheme color name (<c>accent1</c>, <c>tx1</c>, <c>phClr</c> excluded), or <c>null</c>.</param>
    /// <param name="placeholder">The color <c>phClr</c> stands for, or <c>null</c>.</param>
    public static PdfColor? Resolve(XElement color, Func<string, PdfColor?>? scheme, PdfColor? placeholder) =>
        Base(color, scheme, placeholder) is { } value ? Transform(value, color) : null;

    /// <summary>The first color of an element that has a color child (such as <c>a:solidFill</c>), or <c>null</c>.</summary>
    public static PdfColor? Child(XElement? element, Func<string, PdfColor?>? scheme, PdfColor? placeholder)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var child in element.Elements())
        {
            if (Resolve(child, scheme, placeholder) is { } color)
            {
                return color;
            }
        }

        return null;
    }

    /// <summary>
    /// The color of a color element before its transforms. A scheme color the resolver does not know falls back to the
    /// usual aliases (<c>tx1</c> is <c>dk1</c>, ...) and then to the Office theme.
    /// </summary>
    public static PdfColor? Base(XElement color, Func<string, PdfColor?>? scheme, PdfColor? placeholder)
    {
        switch (color.Name.LocalName)
        {
            case "srgbClr":
                return Hex(Attr(color, "val"));
            case "sysClr":
                return Hex(Attr(color, "lastClr")) ?? Attr(color, "val") switch
                {
                    "window" or "highlightText" or "btnHighlight" or "menu" or "infoBk" => PdfColor.White,
                    "btnFace" or "3dLight" or "menuBar" => PdfColor.FromRgb(0xF0, 0xF0, 0xF0),
                    "grayText" or "btnShadow" => PdfColor.FromRgb(0x6D, 0x6D, 0x6D),
                    "highlight" => PdfColor.FromRgb(0x00, 0x78, 0xD7),
                    "hotLight" => PdfColor.FromRgb(0x00, 0x66, 0xCC),
                    _ => PdfColor.Black,
                };
            case "prstClr":
                return Attr(color, "val") is { } name && Presets.TryGetValue(name, out var rgb)
                    ? PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                    : PdfColor.Black;
            case "scrgbClr":
                return new PdfColor(
                    ToSrgb(Percent(color, "r")),
                    ToSrgb(Percent(color, "g")),
                    ToSrgb(Percent(color, "b")));
            case "hslClr":
            {
                var hue = (Number(color, "hue") ?? 0) / 60000 / 360;
                return FromHsl(hue - Math.Floor(hue), Percent(color, "sat"), Percent(color, "lum"), 1);
            }

            case "schemeClr":
                return Attr(color, "val") switch
                {
                    "phClr" => placeholder ?? Scheme("tx1", scheme),
                    { } value => Scheme(value, scheme),
                    _ => PdfColor.Black,
                };
            default:
                return null;
        }
    }

    /// <summary>A scheme color through the resolver, its alias, or the Office theme (black when unknown).</summary>
    public static PdfColor Scheme(string name, Func<string, PdfColor?>? scheme)
    {
        if (scheme?.Invoke(name) is { } color)
        {
            return color;
        }

        var alias = name switch
        {
            "bg1" => "lt1",
            "tx1" => "dk1",
            "bg2" => "lt2",
            "tx2" => "dk2",
            _ => name,
        };
        return (alias != name ? scheme?.Invoke(alias) : null) ?? OfficeTheme.GetValueOrDefault(alias, PdfColor.Black);
    }

    /// <summary>An RGB color in hex (<c>4472C4</c>), or <c>null</c>.</summary>
    public static PdfColor? Hex(string? value)
    {
        if (value is { Length: 6 } && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return null;
    }

    /// <summary>Applies the transforms (child elements) of a color element in order.</summary>
    public static PdfColor Transform(PdfColor color, XElement element)
    {
        var (r, g, b, a) = (color.Red, color.Green, color.Blue, color.Alpha);
        foreach (var transform in element.Elements())
        {
            var value = (Number(transform, "val") ?? 0) / 100000;
            switch (transform.Name.LocalName)
            {
                case "alpha":
                    a = value;
                    break;
                case "alphaMod":
                    a *= value;
                    break;
                case "alphaOff":
                    a += value;
                    break;
                case "tint":
                    // Toward white in linear RGB, as Office does.
                    (r, g, b) = (Tint(r, value), Tint(g, value), Tint(b, value));
                    break;
                case "shade":
                    (r, g, b) = (Shade(r, value), Shade(g, value), Shade(b, value));
                    break;
                case "lumMod" or "lumOff" or "lum" or "satMod" or "satOff" or "sat" or "hueMod" or "hueOff" or "hue":
                {
                    var (h, s, l) = ToHsl(r, g, b);
                    switch (transform.Name.LocalName)
                    {
                        case "lumMod":
                            l *= value;
                            break;
                        case "lumOff":
                            l += value;
                            break;
                        case "lum":
                            l = value;
                            break;
                        case "satMod":
                            s *= value;
                            break;
                        case "satOff":
                            s += value;
                            break;
                        case "sat":
                            s = value;
                            break;
                        case "hueMod":
                            h *= value;
                            break;
                        case "hueOff":
                            h += (Number(transform, "val") ?? 0) / 60000 / 360;
                            break;
                        case "hue":
                            h = (Number(transform, "val") ?? 0) / 60000 / 360;
                            break;
                    }

                    h -= Math.Floor(h);
                    var rgb = FromHsl(h, Math.Clamp(s, 0, 1), Math.Clamp(l, 0, 1), 1);
                    (r, g, b) = (rgb.Red, rgb.Green, rgb.Blue);
                    break;
                }

                case "comp":
                {
                    var (h, s, l) = ToHsl(r, g, b);
                    var rgb = FromHsl((h + 0.5) % 1, s, l, 1);
                    (r, g, b) = (rgb.Red, rgb.Green, rgb.Blue);
                    break;
                }

                case "inv":
                    (r, g, b) = (1 - r, 1 - g, 1 - b);
                    break;
                case "gray":
                    r = g = b = (0.299 * r) + (0.587 * g) + (0.114 * b);
                    break;
                case "red":
                    r = value;
                    break;
                case "green":
                    g = value;
                    break;
                case "blue":
                    b = value;
                    break;
                case "redMod":
                    r *= value;
                    break;
                case "greenMod":
                    g *= value;
                    break;
                case "blueMod":
                    b *= value;
                    break;
                case "redOff":
                    r += value;
                    break;
                case "greenOff":
                    g += value;
                    break;
                case "blueOff":
                    b += value;
                    break;
            }
        }

        static double C(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
        return new PdfColor(C(r), C(g), C(b), C(a));
    }

    /// <summary>Applies luminance modulation and offset (as <c>a:lumMod</c> and <c>a:lumOff</c> do) to a color.</summary>
    public static PdfColor Luminance(PdfColor color, double modulation, double offset)
    {
        var (h, s, l) = ToHsl(color.Red, color.Green, color.Blue);
        return FromHsl(h, s, Math.Clamp((l * modulation) + offset, 0, 1), color.Alpha);
    }

    private static double Tint(double c, double tint) => ToSrgb(1 - ((1 - ToLinear(c)) * Math.Clamp(tint, 0, 1)));

    private static double Shade(double c, double shade) => ToSrgb(ToLinear(c) * Math.Clamp(shade, 0, 1));

    /// <summary>sRGB to linear light.</summary>
    public static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>Linear light to sRGB.</summary>
    public static double ToSrgb(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;
    }

    /// <summary>RGB (0..1) to hue, saturation and luminance (0..1).</summary>
    public static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = ((g - b) / d) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        return (h / 6, s, l);
    }

    /// <summary>Hue, saturation and luminance (0..1) to a color.</summary>
    public static PdfColor FromHsl(double h, double s, double l, double alpha)
    {
        if (s == 0)
        {
            return new PdfColor(l, l, l, alpha);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        static double Channel(double p, double q, double t)
        {
            t -= Math.Floor(t);
            return t < 1.0 / 6 ? p + ((q - p) * 6 * t)
                : t < 0.5 ? q
                : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6)
                : p;
        }

        return new PdfColor(Channel(p, q, h + (1.0 / 3)), Channel(p, q, h), Channel(p, q, h - (1.0 / 3)), alpha);
    }

    /// <summary>A percentage attribute (100000 = 1) clamped to 0..1.</summary>
    private static double Percent(XElement element, string name) => Math.Clamp((Number(element, name) ?? 0) / 100000, 0, 1);

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    /// <summary>A numeric attribute (strict files may write percentages with a % sign), or <c>null</c>.</summary>
    internal static double? Number(XElement? element, string name)
    {
        if ((string?)element?.Attribute(name) is not { } text)
        {
            return null;
        }

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
}
