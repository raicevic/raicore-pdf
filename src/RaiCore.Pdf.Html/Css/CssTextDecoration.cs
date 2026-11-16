using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Splits a <c>text-decoration</c> shorthand that gives a thickness (<c>underline wavy red 2px</c>, valid in CSS Text
/// Decoration 4) into its longhands before AngleSharp.Css parses the CSS: it knows the shorthand only without a
/// thickness, and drops the whole declaration otherwise. Shorthands without a thickness are left to AngleSharp.
/// </summary>
internal static partial class CssTextDecoration
{
    private static readonly HashSet<string> Lines = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "underline", "overline", "line-through", "blink", "spelling-error", "grammar-error",
    };

    private static readonly HashSet<string> Styles = new(StringComparer.OrdinalIgnoreCase) { "solid", "double", "dotted", "dashed", "wavy" };

    /// <summary>Replaces every <c>text-decoration</c> shorthand with a thickness by its four longhands.</summary>
    public static string RewriteShorthand(string css)
    {
        if (!css.Contains("text-decoration", StringComparison.OrdinalIgnoreCase))
        {
            return css;
        }

        var result = new StringBuilder(css.Length);
        var position = 0;
        foreach (Match match in ShorthandRegex().Matches(css))
        {
            if (match.Index < position)
            {
                continue;
            }

            var valueStart = match.Index + match.Length;
            var valueEnd = CssBackgrounds.ValueEnd(css, valueStart);
            if (Longhands(css[valueStart..valueEnd]) is not { } longhands)
            {
                continue;
            }

            result.Append(css, position, match.Index - position);
            result.Append(string.Join("; ", longhands.Select(l => $"{l.Property}: {l.Value}")));
            position = valueEnd;
        }

        return position == 0 ? css : result.Append(css, position, css.Length - position).ToString();
    }

    /// <summary>
    /// The longhands a shorthand value with a thickness stands for (with <c>!important</c> on each, when the value has
    /// it); <c>null</c> for a value without a thickness, with variables, or that is not a text-decoration value.
    /// </summary>
    public static List<(string Property, string Value)>? Longhands(string value)
    {
        var important = ImportantRegex().Match(value);
        var text = (important.Success ? value[..important.Index] : value).Trim();
        if (text.Length == 0 || text.Contains("var(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var lines = new List<string>();
        string? style = null, color = null, thickness = null;
        foreach (var token in CssValues.SplitComponents(text))
        {
            if (Lines.Contains(token))
            {
                lines.Add(token);
            }
            else if (Styles.Contains(token) && style is null)
            {
                style = token;
            }
            else if (IsThickness(token) && thickness is null)
            {
                thickness = token;
            }
            else if (color is null)
            {
                color = token;
            }
            else
            {
                return null; // Two colors: not a valid value; left to AngleSharp, which drops it.
            }
        }

        if (thickness is null)
        {
            return null;
        }

        var suffix = important.Success ? " !important" : string.Empty;
        return
        [
            ("text-decoration-line", (lines.Count == 0 ? "none" : string.Join(' ', lines)) + suffix),
            ("text-decoration-style", (style ?? "solid") + suffix),
            ("text-decoration-color", (color ?? "currentcolor") + suffix),
            ("text-decoration-thickness", thickness + suffix),
        ];
    }

    /// <summary>A thickness: <c>auto</c>, <c>from-font</c>, a length or percentage, or a math function.</summary>
    private static bool IsThickness(string token) =>
        token.Equals("auto", StringComparison.OrdinalIgnoreCase) || token.Equals("from-font", StringComparison.OrdinalIgnoreCase)
        || LengthRegex().IsMatch(token);

    [GeneratedRegex(@"(?<=(?:^|[{;])(?:\s|/\*.*?\*/)*)text-decoration\s*:", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ShorthandRegex();

    [GeneratedRegex(@"!\s*important\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantRegex();

    [GeneratedRegex(@"^(?:[+-]?(?:\d+\.?\d*|\.\d+)(?:[a-z]+|%)?|(?:calc|min|max|clamp)\(.*\))$", RegexOptions.IgnoreCase)]
    private static partial Regex LengthRegex();
}
