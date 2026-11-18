using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Replaces modern-syntax colors whose alpha AngleSharp.Css 1.4 cannot read with a keyword that is not a color, so that
/// the declaration is dropped as CSS requires. On <c>rgb()</c>, <c>rgba()</c>, <c>lab()</c>, <c>lch()</c>,
/// <c>oklab()</c> and <c>oklch()</c> with an empty alpha (<c>rgb(1 2 3 / )</c>, also when only a comment is there) or
/// an alpha that is a number with something after it (<c>rgb(1 2 3 / 4px)</c>, <c>4\70 x</c>), AngleSharp.Css throws
/// <see cref="InvalidOperationException"/> instead (fuzzing found them), already while the HTML is parsed. CSS makes
/// all of them invalid. This runs on the HTML before parsing, on the style sheets the converter rewrites, and on the
/// values the diagnostics check.
/// </summary>
internal static partial class CssColorGuard
{
    /// <summary>The keyword that replaces an invalid color: not a color, so the declaration is invalid.</summary>
    public const string Invalid = "invalid-color";

    public static string Rewrite(string text) =>
        text.Contains('/') && MayHaveColor(text) ? ColorFunction().Replace(text, m => IsUnreadableAlpha(m.Groups["arguments"].Value) ? Invalid : m.Value) : text;

    private static bool MayHaveColor(string text) =>
        text.Contains("rgb", StringComparison.OrdinalIgnoreCase) || text.Contains("lab", StringComparison.OrdinalIgnoreCase)
        || text.Contains("lch", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the arguments of a color function have an alpha (after a slash) that is empty, or starts like a number
    /// but is not just a number or a percentage. Keywords and functions (<c>none</c>, <c>var()</c>) are left alone.
    /// </summary>
    private static bool IsUnreadableAlpha(string arguments)
    {
        var plain = Comment().Replace(arguments, " ");
        var slash = plain.LastIndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var alpha = plain[(slash + 1)..].Trim();
        return alpha.Length == 0 || (StartsLikeNumber(alpha) && !PlainNumber().IsMatch(alpha));
    }

    private static bool StartsLikeNumber(string alpha) => alpha[0] is (>= '0' and <= '9') or '.' or '+' or '-';

    /// <summary>A color function whose alpha the guard checks, with its arguments (no nested parentheses).</summary>
    [GeneratedRegex(@"\b(?:rgba?|lab|lch|oklab|oklch)\((?<arguments>[^()]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColorFunction();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    /// <summary>A number or a percentage, as an alpha may be.</summary>
    [GeneratedRegex(@"^[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?%?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();
}
