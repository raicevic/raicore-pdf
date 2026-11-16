using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Replaces <c>rgb()</c> and <c>rgba()</c> colors whose alpha has a unit (<c>rgb(1 2 3 / 4px)</c>), which CSS makes
/// invalid, with a keyword that is not a color, so that the declaration is dropped as CSS requires. AngleSharp.Css 1.4
/// throws <see cref="InvalidOperationException"/> on them instead (fuzzing found it), already while the HTML is parsed,
/// so this runs on the HTML before parsing, on the style sheets the converter rewrites, and on the values the
/// diagnostics check.
/// </summary>
internal static partial class CssColorGuard
{
    /// <summary>The keyword that replaces an invalid color: not a color, so the declaration is invalid.</summary>
    public const string Invalid = "invalid-color";

    public static string Rewrite(string text) =>
        text.Contains('/') && text.Contains("rgb", StringComparison.OrdinalIgnoreCase) ? AlphaWithUnit().Replace(text, Invalid) : text;

    /// <summary>The modern syntax with an alpha that is a dimension: a number followed by a unit (not a percentage).</summary>
    [GeneratedRegex(@"\brgba?\([^()/]*/\s*[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[-+]?\d+)?[a-z][a-z0-9-]*\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlphaWithUnit();
}
