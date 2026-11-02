using System.Globalization;

namespace RaiCore.Pdf.Html.Css;

/// <summary>A CSS length after parsing: points, a percentage of the containing block, or <c>auto</c>.</summary>
internal readonly record struct CssLength(double Value, CssLengthKind Kind)
{
    public static readonly CssLength Zero = new(0, CssLengthKind.Points);
    public static readonly CssLength Auto = new(0, CssLengthKind.Auto);

    public bool IsAuto => Kind == CssLengthKind.Auto;

    /// <summary>The length in points; percentages resolve against <paramref name="reference"/>, auto becomes 0.</summary>
    public double Resolve(double reference) => Kind switch
    {
        CssLengthKind.Points => Value,
        CssLengthKind.Percent => Value / 100 * reference,
        _ => 0,
    };
}

internal enum CssLengthKind
{
    Points,
    Percent,
    Auto,
}

/// <summary>Parses the CSS value strings that AngleSharp returns from computed styles.</summary>
internal static partial class CssValues
{
    /// <summary>CSS pixels are 1/96 inch; PDF points are 1/72 inch.</summary>
    public const double PointsPerPixel = 0.75;

    public static CssLength ParseLength(string? value, double fontSize = 12, double rootFontSize = 12)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CssLength.Zero;
        }

        value = value.Trim();
        if (value is "auto" or "none" or "normal" or "initial")
        {
            return CssLength.Auto;
        }

        var match = LengthRegex().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return CssLength.Zero;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "" or "PX" => new(number * PointsPerPixel, CssLengthKind.Points),
            "PT" => new(number, CssLengthKind.Points),
            "PC" => new(number * 12, CssLengthKind.Points),
            "IN" => new(number * 72, CssLengthKind.Points),
            "CM" => new(number * 72 / 2.54, CssLengthKind.Points),
            "MM" => new(number * 72 / 25.4, CssLengthKind.Points),
            "Q" => new(number * 72 / 101.6, CssLengthKind.Points),
            "EM" => new(number * fontSize, CssLengthKind.Points),
            "REM" => new(number * rootFontSize, CssLengthKind.Points),
            "EX" => new(number * fontSize / 2, CssLengthKind.Points),
            "CH" => new(number * fontSize / 2, CssLengthKind.Points),
            "%" => new(number, CssLengthKind.Percent),
            _ => CssLength.Zero,
        };
    }

    /// <summary>Parses rgb()/rgba()/#hex/basic names. Returns null for transparent; translucent colors are blended with white.</summary>
    public static PdfColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().ToUpperInvariant();
        if (value is "TRANSPARENT" or "NONE")
        {
            return null;
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

                return Blend((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);
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
            return Blend(ClampByte(Channel(parts[0])), ClampByte(Channel(parts[1])), ClampByte(Channel(parts[2])), a);
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

    /// <summary>Translucent colors are blended with white (PDF transparency comes later); fully transparent is null.</summary>
    private static PdfColor? Blend(byte r, byte g, byte b, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (alpha <= 0)
        {
            return null;
        }

        return new PdfColor(
            ((r / 255.0) * alpha) + (1 - alpha),
            ((g / 255.0) * alpha) + (1 - alpha),
            ((b / 255.0) * alpha) + (1 - alpha));
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
