using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Rewrites the legacy <c>page-break-before/after/inside</c> properties to <c>break-before/after/inside</c> before the CSS
/// is parsed. AngleSharp.Css reports wrong computed values for the legacy properties (always "always"/"avoid"), so only
/// the modern ones are read; CSS Fragmentation defines the legacy ones as aliases of them. Also spells <c>recto</c> and
/// <c>verso</c> as <c>right</c> and <c>left</c>, and applies <see cref="CssAliases.Rewrite"/> for other properties
/// AngleSharp.Css does not keep.
/// </summary>
internal static partial class LegacyBreaks
{
    public static string Rewrite(string css) =>
        CssAliases.Rewrite(SpreadRegex().Replace(
            LegacyRegex().Replace(css, match =>
            {
                var side = match.Groups[1].Value.ToLowerInvariant();
                var value = match.Groups[2].Value.ToLowerInvariant();
                var modern = value == "always" ? "page" : value;
                return $"break-{side}: {modern}";
            }),
            match => $"{match.Groups[1].Value}: {(match.Groups[2].Value.Equals("recto", StringComparison.OrdinalIgnoreCase) ? "right" : "left")}"));

    /// <summary>
    /// <c>break-before/after: recto</c> and <c>verso</c>, which AngleSharp.Css drops: in left-to-right page progression
    /// they are the right and left pages.
    /// </summary>
    [GeneratedRegex(@"(?<![\w-])(break-(?:before|after))\s*:\s*(recto|verso)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpreadRegex();

    [GeneratedRegex(@"page-break-(before|after|inside)\s*:\s*(always|avoid|left|right|auto)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyRegex();
}
