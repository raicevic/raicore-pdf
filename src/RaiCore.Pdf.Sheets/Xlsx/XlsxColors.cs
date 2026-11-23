using System.Globalization;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// Spreadsheet colors: ARGB values, the indexed palette (custom or the default one), theme colors and tints.
/// </summary>
internal sealed class XlsxColors
{
    /// <summary>The default indexed palette (ECMA-376 18.8.27), 0 to 63; 64 is the system foreground, 65 the background.</summary>
    internal static readonly uint[] DefaultPalette =
    [
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
        0x800000, 0x008000, 0x000080, 0x808000, 0x800080, 0x008080, 0xC0C0C0, 0x808080,
        0x9999FF, 0x993366, 0xFFFFCC, 0xCCFFFF, 0x660066, 0xFF8080, 0x0066CC, 0xCCCCFF,
        0x000080, 0xFF00FF, 0xFFFF00, 0x00FFFF, 0x800080, 0x800000, 0x008080, 0x0000FF,
        0x00CCFF, 0xCCFFFF, 0xCCFFCC, 0xFFFF99, 0x99CCFF, 0xFF99CC, 0xCC99FF, 0xFFCC99,
        0x3366FF, 0x33CCCC, 0x99CC00, 0xFFCC00, 0xFF9900, 0xFF6600, 0x666699, 0x969696,
        0x003366, 0x339966, 0x003300, 0x333300, 0x993300, 0x993366, 0x333399, 0x333333,
    ];

    /// <summary>The Office theme's colors, in the order theme indexes name them (light 1, dark 1, light 2, dark 2, ...).</summary>
    private static readonly uint[] DefaultTheme =
    [
        0xFFFFFF, 0x000000, 0xE7E6E6, 0x44546A, 0x4472C4, 0xED7D31, 0xA5A5A5, 0xFFC000, 0x5B9BD5, 0x70AD47, 0x0563C1, 0x954F72,
    ];

    private readonly uint[] _palette;
    private readonly uint[] _theme;

    public XlsxColors(IReadOnlyList<uint>? palette = null, IReadOnlyList<uint>? theme = null)
    {
        _palette = palette is { Count: > 0 } ? [.. palette] : DefaultPalette;
        _theme = theme is { Count: >= 4 } ? [.. theme] : DefaultTheme;
    }

    /// <summary>A color of the default palette (for the colors of number formats).</summary>
    public static PdfColor Indexed(int index) => FromRgb(index is >= 0 and < 64 ? DefaultPalette[index] : 0);

    /// <summary>
    /// Reads a color element's attributes: <c>rgb</c> (ARGB hex), <c>indexed</c>, <c>theme</c> (with <c>tint</c>) or
    /// <c>auto</c>. Returns <c>null</c> for automatic colors and ones it cannot read.
    /// </summary>
    public PdfColor? Resolve(string? rgb, string? indexed, string? theme, string? tint, string? auto)
    {
        uint? value = null;
        if (rgb is { } hex && hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var argb))
        {
            value = argb & 0xFFFFFF;
        }
        else if (theme is not null && int.TryParse(theme, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
        {
            value = t < _theme.Length ? _theme[t] : null;
        }
        else if (indexed is not null && int.TryParse(indexed, NumberStyles.None, CultureInfo.InvariantCulture, out var i))
        {
            value = PaletteValue(i);
        }
        else if (auto is "1" or "true")
        {
            return null;
        }

        if (value is not { } color)
        {
            return null;
        }

        if (tint is not null && double.TryParse(tint, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) && double.IsFinite(amount) && amount != 0)
        {
            color = Tint(color, Math.Clamp(amount, -1, 1));
        }

        return FromRgb(color);
    }

    /// <summary>
    /// A color of the indexed palette (the workbook's own, or the default one): black for 64 (the system foreground),
    /// <c>null</c> for 65 (the system background) and indexes out of range.
    /// </summary>
    public PdfColor? PaletteColor(int index) => PaletteValue(index) is { } value ? FromRgb(value) : null;

    private uint? PaletteValue(int index) => index >= 0 && index < _palette.Length ? _palette[index] : index == 64 ? 0x000000u : null;

    /// <summary>
    /// Lightens (positive tint) or darkens (negative) a color in HSL, as spreadsheets apply theme tints: the luminance
    /// moves toward white or black by the tint's fraction.
    /// </summary>
    internal static uint Tint(uint rgb, double tint)
    {
        var (h, s, l) = ToHsl(rgb);
        l = tint < 0 ? l * (1 + tint) : (l * (1 - tint)) + tint;
        return FromHsl(h, s, Math.Clamp(l, 0, 1));
    }

    private static (double H, double S, double L) ToHsl(uint rgb)
    {
        var r = ((rgb >> 16) & 0xFF) / 255.0;
        var g = ((rgb >> 8) & 0xFF) / 255.0;
        var b = (rgb & 0xFF) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h / 6, s, l);
    }

    private static uint FromHsl(double h, double s, double l)
    {
        if (s == 0)
        {
            var v = (uint)Math.Round(l * 255);
            return (v << 16) | (v << 8) | v;
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        var r = Channel(p, q, h + (1 / 3.0));
        var g = Channel(p, q, h);
        var b = Channel(p, q, h - (1 / 3.0));
        return (r << 16) | (g << 8) | b;

        static uint Channel(double p, double q, double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            var value = t < 1 / 6.0 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6) : p;
            return (uint)Math.Clamp(Math.Round(value * 255), 0, 255);
        }
    }

    private static PdfColor FromRgb(uint rgb) => PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
