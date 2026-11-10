using System.Globalization;

namespace RaiCore.Pdf.Svg;

internal enum SvgPaintKind
{
    None,
    Color,
    CurrentColor,

    /// <summary>A paint server (gradient) referenced with <c>url(#id)</c>, with an optional fallback.</summary>
    Server,
}

/// <summary>The value of <c>fill</c> or <c>stroke</c>.</summary>
internal sealed record SvgPaint(SvgPaintKind Kind, SvgColor Color = default, string? ServerId = null, SvgPaint? Fallback = null)
{
    public static SvgPaint None { get; } = new(SvgPaintKind.None);

    public static SvgPaint Black { get; } = new(SvgPaintKind.Color, SvgColor.Black);

    public static SvgPaint CurrentColor { get; } = new(SvgPaintKind.CurrentColor);

    /// <summary>Parses a paint; null when invalid (the declaration is then ignored).</summary>
    public static SvgPaint? Parse(string value)
    {
        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return CurrentColor;
        }

        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var close = value.IndexOf(')', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            var id = SvgDocument.ParseUrl(value[..(close + 1)]);
            var rest = value[(close + 1)..].Trim();
            var fallback = rest.Length > 0 ? Parse(rest) : null;
            return id is null ? fallback : new SvgPaint(SvgPaintKind.Server, ServerId: id, Fallback: fallback);
        }

        return SvgColor.Parse(value) is { } color ? new SvgPaint(SvgPaintKind.Color, color) : null;
    }
}

internal enum SvgTextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>
/// The computed SVG style of an element: the inherited properties of its parent overridden by its own declarations
/// (presentation attributes, then <c>&lt;style&gt;</c> rules, then the <c>style</c> attribute). Lengths are in user units.
/// </summary>
internal sealed record SvgStyle
{
    /// <summary>Font size for <c>medium</c>, in CSS pixels.</summary>
    public const double DefaultFontSize = 16;

    public static SvgStyle Initial { get; } = new();

    // Inherited properties.
    public SvgPaint Fill { get; init; } = SvgPaint.Black;

    public double FillOpacity { get; init; } = 1;

    public bool FillEvenOdd { get; init; }

    public SvgPaint Stroke { get; init; } = SvgPaint.None;

    public double StrokeOpacity { get; init; } = 1;

    public double StrokeWidth { get; init; } = 1;

    public LineCap LineCap { get; init; } = LineCap.Butt;

    public LineJoin LineJoin { get; init; } = LineJoin.Miter;

    public double MiterLimit { get; init; } = 4;

    public IReadOnlyList<double> DashArray { get; init; } = [];

    public double DashOffset { get; init; }

    /// <summary>The <c>color</c> property, which <c>currentColor</c> refers to.</summary>
    public SvgColor Color { get; init; } = SvgColor.Black;

    public bool Visible { get; init; } = true;

    public bool ClipEvenOdd { get; init; }

    public double FontSize { get; init; } = DefaultFontSize;

    public string? FontFamily { get; init; }

    public int FontWeight { get; init; } = 400;

    public bool Italic { get; init; }

    public SvgTextAnchor TextAnchor { get; init; }

    // Not inherited.
    public bool Displayed { get; init; } = true;

    public double Opacity { get; init; } = 1;

    public string? ClipPath { get; init; }

    public SvgColor StopColor { get; init; } = SvgColor.Black;

    public double StopOpacity { get; init; } = 1;

    /// <summary>
    /// Computes the style of an element from its parent's. <paramref name="viewport"/> is the size percentages of
    /// lengths refer to.
    /// </summary>
    public static SvgStyle Compute(SvgStyle parent, SvgElement element, (double Width, double Height) viewport)
    {
        var style = parent with
        {
            Displayed = true,
            Opacity = 1,
            ClipPath = null,
            StopColor = SvgColor.Black,
            StopOpacity = 1,
        };

        var declared = element.Declared;
        if (declared.Count == 0)
        {
            return style;
        }

        // The font size first: em lengths of the other properties refer to it.
        if (declared.TryGetValue("font-size", out var fontSize))
        {
            style = style with { FontSize = ParseFontSize(fontSize, parent.FontSize) ?? style.FontSize };
        }

        // color before the paints, which may refer to it.
        if (declared.TryGetValue("color", out var color))
        {
            style = style with { Color = IsInherit(color) ? parent.Color : SvgColor.Parse(color) ?? style.Color };
        }

        foreach (var (name, raw) in declared)
        {
            var value = raw.Trim();
            var inherit = IsInherit(value);
            style = name switch
            {
                "fill" => style with { Fill = inherit ? parent.Fill : SvgPaint.Parse(value) ?? style.Fill },
                "stroke" => style with { Stroke = inherit ? parent.Stroke : SvgPaint.Parse(value) ?? style.Stroke },
                "fill-opacity" => style with { FillOpacity = inherit ? parent.FillOpacity : ParseOpacity(value) ?? style.FillOpacity },
                "stroke-opacity" => style with { StrokeOpacity = inherit ? parent.StrokeOpacity : ParseOpacity(value) ?? style.StrokeOpacity },
                "opacity" => style with { Opacity = inherit ? parent.Opacity : ParseOpacity(value) ?? 1 },
                "fill-rule" => style with { FillEvenOdd = inherit ? parent.FillEvenOdd : IsEvenOdd(value, style.FillEvenOdd) },
                "clip-rule" => style with { ClipEvenOdd = inherit ? parent.ClipEvenOdd : IsEvenOdd(value, style.ClipEvenOdd) },
                "stroke-width" => style with
                {
                    StrokeWidth = inherit ? parent.StrokeWidth
                        : SvgLength.Parse(value) is { } w && w.Value >= 0 ? w.Resolve(style.FontSize, Diagonal(viewport)) : style.StrokeWidth,
                },
                "stroke-linecap" => style with
                {
                    LineCap = inherit ? parent.LineCap : value.ToUpperInvariant() switch
                    {
                        "BUTT" => LineCap.Butt,
                        "ROUND" => LineCap.Round,
                        "SQUARE" => LineCap.Square,
                        _ => style.LineCap,
                    },
                },
                "stroke-linejoin" => style with
                {
                    LineJoin = inherit ? parent.LineJoin : value.ToUpperInvariant() switch
                    {
                        "MITER" or "MITER-CLIP" => LineJoin.Miter,
                        "ROUND" => LineJoin.Round,
                        "BEVEL" => LineJoin.Bevel,
                        _ => style.LineJoin,
                    },
                },
                "stroke-miterlimit" => style with
                {
                    MiterLimit = inherit ? parent.MiterLimit
                        : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m >= 1 && double.IsFinite(m) ? m : style.MiterLimit,
                },
                "stroke-dasharray" => style with { DashArray = inherit ? parent.DashArray : ParseDashArray(value, style.FontSize, viewport) ?? style.DashArray },
                "stroke-dashoffset" => style with
                {
                    DashOffset = inherit ? parent.DashOffset
                        : SvgLength.Parse(value) is { } o ? o.Resolve(style.FontSize, Diagonal(viewport)) : style.DashOffset,
                },
                "display" => style with { Displayed = !value.Equals("none", StringComparison.OrdinalIgnoreCase) },
                "visibility" => style with
                {
                    Visible = inherit ? parent.Visible : value.ToUpperInvariant() switch
                    {
                        "VISIBLE" => true,
                        "HIDDEN" or "COLLAPSE" => false,
                        _ => style.Visible,
                    },
                },
                "clip-path" => style with { ClipPath = SvgDocument.ParseUrl(value) },
                "stop-color" => style with
                {
                    StopColor = value.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ? style.Color : SvgColor.Parse(value) ?? SvgColor.Black,
                },
                "stop-opacity" => style with { StopOpacity = ParseOpacity(value) ?? 1 },
                "font-family" => style with { FontFamily = inherit ? parent.FontFamily : value },
                "font-weight" => style with { FontWeight = inherit ? parent.FontWeight : ParseFontWeight(value, parent.FontWeight) },
                "font-style" => style with
                {
                    Italic = inherit ? parent.Italic : value.ToUpperInvariant() switch
                    {
                        "ITALIC" or "OBLIQUE" => true,
                        "NORMAL" => false,
                        _ => style.Italic,
                    },
                },
                "text-anchor" => style with
                {
                    TextAnchor = inherit ? parent.TextAnchor : value.ToUpperInvariant() switch
                    {
                        "START" => SvgTextAnchor.Start,
                        "MIDDLE" => SvgTextAnchor.Middle,
                        "END" => SvgTextAnchor.End,
                        _ => style.TextAnchor,
                    },
                },
                _ => style,
            };
        }

        return style;
    }

    /// <summary>The reference length for percentages that are neither horizontal nor vertical (e.g. stroke widths).</summary>
    public static double Diagonal((double Width, double Height) viewport) =>
        Math.Sqrt(((viewport.Width * viewport.Width) + (viewport.Height * viewport.Height)) / 2);

    private static bool IsInherit(string value) => value.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase);

    private static bool IsEvenOdd(string value, bool current) => value.ToUpperInvariant() switch
    {
        "EVENODD" => true,
        "NONZERO" => false,
        _ => current,
    };

    private static double? ParseOpacity(string value)
    {
        var percent = value.EndsWith('%');
        if (!double.TryParse(percent ? value[..^1] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) || !double.IsFinite(opacity))
        {
            return null;
        }

        return Math.Clamp(percent ? opacity / 100 : opacity, 0, 1);
    }

    private static double? ParseFontSize(string value, double parentSize)
    {
        value = value.Trim();
        if (IsInherit(value))
        {
            return parentSize;
        }

        var keyword = value.ToUpperInvariant() switch
        {
            "XX-SMALL" => 9,
            "X-SMALL" => 10,
            "SMALL" => 13,
            "MEDIUM" => 16,
            "LARGE" => 18,
            "X-LARGE" => 24,
            "XX-LARGE" => 32,
            "SMALLER" => parentSize / 1.2,
            "LARGER" => parentSize * 1.2,
            _ => (double?)null,
        };
        if (keyword is not null)
        {
            return keyword;
        }

        return SvgLength.Parse(value) is { } length && length.Value >= 0 ? length.Resolve(parentSize, parentSize) : null;
    }

    private static int ParseFontWeight(string value, int parentWeight) => value.ToUpperInvariant() switch
    {
        "NORMAL" => 400,
        "BOLD" => 700,
        "BOLDER" => parentWeight >= 600 ? 900 : 700,
        "LIGHTER" => parentWeight >= 600 ? 400 : 100,
        _ => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) ? Math.Clamp(weight, 1, 1000) : parentWeight,
    };

    private static List<double>? ParseDashArray(string value, double fontSize, (double Width, double Height) viewport)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var lengths = new List<double>();
        foreach (var part in value.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (SvgLength.Parse(part) is not { } length || length.Value < 0)
            {
                return null; // A negative or invalid length makes the whole value invalid.
            }

            lengths.Add(length.Resolve(fontSize, Diagonal(viewport)));
        }

        if (lengths.All(l => l == 0))
        {
            return []; // All zero: a solid line.
        }

        return lengths.Count % 2 == 1 ? [.. lengths, .. lengths] : lengths;
    }
}

internal enum SvgUnit
{
    None,
    Px,
    Pt,
    Pc,
    Mm,
    Cm,
    In,
    Em,
    Ex,
    Percent,
}

/// <summary>A length with its unit, as written in SVG.</summary>
internal readonly record struct SvgLength(double Value, SvgUnit Unit)
{
    public bool IsPercent => Unit == SvgUnit.Percent;

    /// <summary>Parses a length such as <c>10</c>, <c>2.5mm</c> or <c>50%</c>; null when invalid.</summary>
    public static SvgLength? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var scanner = new SvgScanner(text);
        if (!scanner.TryReadNumber(out var value))
        {
            return null;
        }

        var unit = scanner.ReadName().ToString().ToUpperInvariant() switch
        {
            "" => SvgUnit.None,
            "PX" => SvgUnit.Px,
            "PT" => SvgUnit.Pt,
            "PC" => SvgUnit.Pc,
            "MM" => SvgUnit.Mm,
            "CM" => SvgUnit.Cm,
            "IN" => SvgUnit.In,
            "EM" => SvgUnit.Em,
            "EX" => SvgUnit.Ex,
            "%" => SvgUnit.Percent,
            _ => (SvgUnit?)null,
        };

        return unit is { } u && scanner.AtEnd ? new SvgLength(value, u) : null;
    }

    /// <summary>The length in user units (CSS pixels); percentages are of <paramref name="reference"/>.</summary>
    public double Resolve(double fontSize, double reference) => Unit switch
    {
        SvgUnit.Pt => Value * 4 / 3,
        SvgUnit.Pc => Value * 16,
        SvgUnit.Mm => Value * 96 / 25.4,
        SvgUnit.Cm => Value * 96 / 2.54,
        SvgUnit.In => Value * 96,
        SvgUnit.Em => Value * fontSize,
        SvgUnit.Ex => Value * fontSize / 2,
        SvgUnit.Percent => Value / 100 * reference,
        _ => Value,
    };
}
