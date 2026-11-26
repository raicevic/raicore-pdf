using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// CSS <c>box-shadow</c>. AngleSharp.Css throws computing some malformed shadows (fuzzing found <c>box-shadow:
/// 1px1px</c>), so before the CSS is parsed each <c>box-shadow</c> declaration becomes the custom property
/// <see cref="Property"/>, which AngleSharp keeps as text through the cascade (with <c>var()</c> resolved);
/// <see cref="ComputedStyle"/> parses it here. A universal rule in the <see cref="UserAgentStyleSheet"/> keeps it from
/// being inherited. <c>text-shadow</c> is removed (not drawn).
/// </summary>
internal static partial class CssShadows
{
    /// <summary>The custom property holding an element's <c>box-shadow</c>.</summary>
    public const string Property = "--raicore-box-shadow";

    /// <summary>Renames <c>box-shadow</c> declarations to <see cref="Property"/> and removes <c>text-shadow</c> ones.</summary>
    public static string Rewrite(string css) =>
        css.Contains("shadow", StringComparison.OrdinalIgnoreCase)
            ? ShadowRegex().Replace(css, match => match.Groups["kind"].Value.Equals("box", StringComparison.OrdinalIgnoreCase) ? Property + ":" + match.Groups["value"].Value : string.Empty)
            : css;

    /// <summary>
    /// The shadows of a computed <c>box-shadow</c> value: comma-separated shadows of 2 to 4 lengths (offsets, blur,
    /// spread), an optional color (the text color if none) and <c>inset</c>. Invalid shadows make the whole value none,
    /// as in CSS; a negative blur is invalid.
    /// </summary>
    public static IReadOnlyList<BoxShadow> Parse(string value, Func<string, CssLength?> length, PdfColor currentColor)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("initial", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var shadows = new List<BoxShadow>();
        foreach (var part in Split(value, ','))
        {
            var lengths = new List<double>();
            PdfColor? color = null;
            var inset = false;
            foreach (var token in Split(part, ' '))
            {
                if (token.Equals("inset", StringComparison.OrdinalIgnoreCase) && !inset)
                {
                    inset = true;
                }
                else if (length(token) is { } l && l.Kind is CssLengthKind.Points or CssLengthKind.Calc && lengths.Count < 4)
                {
                    lengths.Add(l.Resolve(0));
                }
                else if (color is null && (token.Equals("currentcolor", StringComparison.OrdinalIgnoreCase) ? currentColor : CssValues.ParseColor(token)) is { } c)
                {
                    color = c;
                }
                else
                {
                    return [];
                }
            }

            if (lengths.Count < 2 || (lengths.Count > 2 && lengths[2] < 0))
            {
                return [];
            }

            shadows.Add(new BoxShadow(lengths[0], lengths[1], lengths.ElementAtOrDefault(2), lengths.ElementAtOrDefault(3), color ?? currentColor) { Inset = inset });
        }

        return shadows;
    }

    /// <summary>The parts of a value split at a separator outside parentheses, trimmed, without empty ones.</summary>
    private static List<string> Split(string value, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            var c = i < value.Length ? value[i] : separator;
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && (c == separator || (separator == ' ' && char.IsWhiteSpace(c))))
            {
                if (value[start..i].Trim() is { Length: > 0 } part)
                {
                    parts.Add(part);
                }

                start = i + 1;
            }
        }

        return parts;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w-])(?<kind>box|text)-shadow\s*:(?<value>[^;}]*;?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ShadowRegex();
}
