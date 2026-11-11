using System.Globalization;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// A CSS length after parsing: points, a percentage of the containing block, <c>auto</c>, or a math function that
/// mixes both (<c>calc(100% - 20px)</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c>), resolved with the percentages.
/// </summary>
internal readonly record struct CssLength(double Value, CssLengthKind Kind)
{
    public static readonly CssLength Zero = new(0, CssLengthKind.Points);
    public static readonly CssLength Auto = new(0, CssLengthKind.Auto);

    public bool IsAuto => Kind == CssLengthKind.Auto;

    /// <summary>Whether the length depends on the percentage reference: a percentage or a calc expression.</summary>
    public bool HasPercentage => Kind is CssLengthKind.Percent or CssLengthKind.Calc;

    /// <summary>The expression of a <see cref="CssLengthKind.Calc"/> length.</summary>
    public CssCalc? Calc { get; private init; }

    public static CssLength FromCalc(CssCalc calc) => new(0, CssLengthKind.Calc) { Calc = calc };

    /// <summary>The length in points; percentages resolve against <paramref name="reference"/>, auto becomes 0.</summary>
    public double Resolve(double reference) => Kind switch
    {
        CssLengthKind.Points => Value,
        CssLengthKind.Percent => Value / 100 * reference,
        CssLengthKind.Calc => Calc?.Evaluate(reference) ?? 0,
        _ => 0,
    };

    /// <summary>
    /// The length as points plus a percentage of the reference: a length, a percentage, or a calc() of them (also clamped
    /// at 0, see <see cref="NonNegative"/>); <c>false</c> for auto and for min(), max() and clamp() of a percentage.
    /// </summary>
    public bool TryLinear(out double points, out double percent)
    {
        switch (Kind)
        {
            case CssLengthKind.Points:
                (points, percent) = (Value, 0);
                return true;
            case CssLengthKind.Percent:
                (points, percent) = (0, Value);
                return true;
            case CssLengthKind.Calc when Calc is not null:
                return Calc.TryLinear(out points, out percent);
            default:
                (points, percent) = (0, 0);
                return false;
        }
    }

    /// <summary>The length clamped at 0, for properties that cannot be negative (a calc() result can be).</summary>
    public CssLength NonNegative() => Kind switch
    {
        CssLengthKind.Calc when Calc is not null => FromCalc(Calc.NonNegative()),
        CssLengthKind.Points or CssLengthKind.Percent when Value < 0 => this with { Value = 0 },
        _ => this,
    };
}

internal enum CssLengthKind
{
    Points,
    Percent,
    Auto,

    /// <summary>A math function that depends on the percentage reference; see <see cref="CssLength.Calc"/>.</summary>
    Calc,
}

/// <summary>The viewport that <c>vw</c>, <c>vh</c>, <c>vmin</c> and <c>vmax</c> refer to (the page content area), in points; empty when unknown.</summary>
internal readonly record struct CssViewport(double Width, double Height);

/// <summary>Parses the CSS value strings that AngleSharp returns from computed styles.</summary>
internal static partial class CssValues
{
    /// <summary>CSS pixels are 1/96 inch; PDF points are 1/72 inch.</summary>
    public const double PointsPerPixel = 0.75;

    /// <summary>Parses a length; empty and invalid values are 0 (see <see cref="TryParseLength"/>).</summary>
    public static CssLength ParseLength(string? value, double fontSize = 12, double rootFontSize = 12, CssViewport viewport = default) =>
        TryParseLength(value, fontSize, rootFontSize, viewport) ?? CssLength.Zero;

    /// <summary>
    /// Parses a length, a percentage, a math function (<c>calc()</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c>) or
    /// a keyword that means auto. Returns <c>null</c> when the value is empty or invalid, so callers can fall back to
    /// the property's initial value.
    /// </summary>
    public static CssLength? TryParseLength(string? value, double fontSize = 12, double rootFontSize = 12, CssViewport viewport = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value is "auto" or "none" or "normal" or "initial")
        {
            return CssLength.Auto;
        }

        if (value.Contains('(', StringComparison.Ordinal))
        {
            return CssCalc.Parse(value, fontSize, rootFontSize, viewport)?.ToLength();
        }

        var match = LengthRegex().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return match.Groups[2].Value switch
        {
            "" => new(number * PointsPerPixel, CssLengthKind.Points),
            "%" => new(number, CssLengthKind.Percent),
            var unit => ToPoints(number, unit, fontSize, rootFontSize, viewport) is { } points ? new(points, CssLengthKind.Points) : null,
        };
    }

    /// <summary>Converts a number with a length unit to points; <c>null</c> for unknown units (or viewport units without a viewport).</summary>
    public static double? ToPoints(double number, string unit, double fontSize, double rootFontSize, CssViewport viewport) => unit.ToUpperInvariant() switch
    {
        "PX" => number * PointsPerPixel,
        "PT" => number,
        "PC" => number * 12,
        "IN" => number * 72,
        "CM" => number * 72 / 2.54,
        "MM" => number * 72 / 25.4,
        "Q" => number * 72 / 101.6,
        "EM" => number * fontSize,
        "REM" => number * rootFontSize,
        "EX" or "CH" => number * fontSize / 2,
        "VW" when viewport.Width > 0 => number * viewport.Width / 100,
        "VH" when viewport.Height > 0 => number * viewport.Height / 100,
        "VMIN" when viewport.Width > 0 && viewport.Height > 0 => number * Math.Min(viewport.Width, viewport.Height) / 100,
        "VMAX" when viewport.Width > 0 && viewport.Height > 0 => number * Math.Max(viewport.Width, viewport.Height) / 100,
        _ => null,
    };

    /// <summary>Splits a value at whitespace outside parentheses, so <c>calc(1px + 2px) 3px</c> has two components.</summary>
    public static List<string> SplitComponents(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = -1;
        for (var i = 0; i <= value.Length; i++)
        {
            var c = i < value.Length ? value[i] : ' ';
            depth += c switch { '(' => 1, ')' => -1, _ => 0 };
            if (char.IsWhiteSpace(c) && depth <= 0)
            {
                if (start >= 0)
                {
                    parts.Add(value[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return parts;
    }

    /// <summary>
    /// Parses rgb()/rgba()/#hex/basic names (AngleSharp computes every color as <c>rgba(r, g, b, a)</c>), keeping the
    /// alpha: <c>transparent</c> is a color with alpha 0. Returns null for <c>none</c> and invalid values.
    /// </summary>
    public static PdfColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().ToUpperInvariant();
        if (value == "NONE")
        {
            return null;
        }

        if (value == "TRANSPARENT")
        {
            return new PdfColor(0, 0, 0, 0);
        }

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                var alpha = hex.Length == 8 ? (rgb & 0xFF) / 255.0 : 1;
                if (hex.Length == 8)
                {
                    rgb >>= 8;
                }

                return PdfColor.FromRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);
            }

            return null;
        }

        if (value.StartsWith("RGB", StringComparison.Ordinal))
        {
            var open = value.IndexOf('(', StringComparison.Ordinal);
            var close = value.LastIndexOf(')');
            if (open < 0 || close < open)
            {
                return null;
            }

            var parts = value[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return null;
            }

            double Channel(string s) => s.EndsWith('%')
                ? double.Parse(s.TrimEnd('%'), CultureInfo.InvariantCulture) * 2.55
                : double.Parse(s, CultureInfo.InvariantCulture);
            var a = parts.Length > 3
                ? parts[3].EndsWith('%') ? double.Parse(parts[3].TrimEnd('%'), CultureInfo.InvariantCulture) / 100 : double.Parse(parts[3], CultureInfo.InvariantCulture)
                : 1;
            return PdfColor.FromRgba(ClampByte(Channel(parts[0])), ClampByte(Channel(parts[1])), ClampByte(Channel(parts[2])), a);
        }

        return value switch
        {
            "BLACK" => PdfColor.Black,
            "WHITE" => PdfColor.White,
            "RED" => PdfColor.FromRgb(255, 0, 0),
            "GREEN" => PdfColor.FromRgb(0, 128, 0),
            "BLUE" => PdfColor.FromRgb(0, 0, 255),
            "GRAY" or "GREY" => PdfColor.FromRgb(128, 128, 128),
            _ => null,
        };
    }

    /// <summary>Splits a font-family list, removing quotes.</summary>
    public static IReadOnlyList<string> ParseFontFamilies(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ["serif"];
        }

        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in value)
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ',')
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return result.Count > 0 ? result : ["serif"];

        void Flush()
        {
            var family = current.ToString().Trim();
            if (family.Length > 0)
            {
                result.Add(family);
            }

            current.Clear();
        }
    }

    public static int ParseFontWeight(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "NORMAL" => 400,
        "BOLD" => 700,
        "BOLDER" => 700,
        "LIGHTER" => 300,
        var number when int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) => Math.Clamp(w, 1, 1000),
        _ => 400,
    };

    public static int ParseInteger(string? value, int fallback) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    [System.Text.RegularExpressions.GeneratedRegex(@"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([a-zA-Z%]*)$")]
    private static partial System.Text.RegularExpressions.Regex LengthRegex();

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
