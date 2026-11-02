using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Rewrites the legacy <c>page-break-before/after/inside</c> properties to <c>break-before/after/inside</c> before the CSS
/// is parsed. AngleSharp.Css reports wrong computed values for the legacy properties (always "always"/"avoid"), so only
/// the modern ones are read; CSS Fragmentation defines the legacy ones as aliases of them.
/// </summary>
internal static partial class LegacyBreaks
{
    public static string Rewrite(string css) =>
        LegacyRegex().Replace(css, match =>
        {
            var side = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value.ToLowerInvariant();
            var modern = value == "always" ? "page" : value;
            return $"break-{side}: {modern}";
        });

    [GeneratedRegex(@"page-break-(before|after|inside)\s*:\s*(always|avoid|left|right|auto)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyRegex();
}
