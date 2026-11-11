using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Rewrites properties AngleSharp.Css drops before the CSS is parsed (see <see cref="LegacyBreaks.Rewrite"/>):
/// <c>-webkit-hyphens</c> and <c>-webkit-hyphenate-character</c> become their standard names, and
/// <c>hyphenate-limit-chars</c>, which AngleSharp.Css does not know, becomes a custom property (which it keeps, and
/// inherits like the real one).
/// </summary>
internal static partial class CssAliases
{
    /// <summary>The custom property that carries <c>hyphenate-limit-chars</c>.</summary>
    public const string HyphenateLimitChars = "--raicore-hyphenate-limit-chars";

    public static string Rewrite(string css) =>
        AliasRegex().Replace(css, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "hyphenate-limit-chars" => HyphenateLimitChars + ":",
            var name => name["-webkit-".Length..] + ":",
        });

    [GeneratedRegex(@"(?<![\w-])(-webkit-hyphens|-webkit-hyphenate-character|hyphenate-limit-chars)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex AliasRegex();
}
