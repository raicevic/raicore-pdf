using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// Makes the logical margins and padding (<c>margin-inline-start</c>, <c>padding-inline</c> and the like) follow the
/// element's direction. AngleSharp.Css maps them to physical sides as if every element were left to right: start is
/// left, end is right. So before the CSS is parsed, every horizontal margin and padding declaration also sets custom
/// properties with the side each one has in a right-to-left element (start on the right, end on the left), and the
/// cascade picks the winner of each, with the right origin, specificity and order. <see cref="ComputedStyle"/> reads
/// them for right-to-left elements; a universal rule in the <see cref="UserAgentStyleSheet"/> resets them on every
/// element, so they are not inherited.
/// </summary>
internal static partial class LogicalSides
{
    /// <summary>The custom property holding a box side's margin or padding in a right-to-left element.</summary>
    public static string Property(string box, string side) => $"--raicore-rtl-{box}-{side}";

    /// <summary>Adds the right-to-left side properties after each horizontal margin and padding declaration.</summary>
    public static string Rewrite(string css) =>
        DeclarationRegex().Replace(css, match =>
        {
            var box = match.Groups["box"].Value.ToLowerInvariant();
            var side = match.Groups["side"].Success ? match.Groups["side"].Value.ToLowerInvariant() : null;
            var (value, important) = SplitImportant(CommentRegex().Replace(match.Groups["value"].Value, " "));
            var parts = SplitValue(value);
            if (parts.Count == 0)
            {
                return match.Value;
            }

            // The values for the right and left sides of a right-to-left element.
            var (right, left) = side switch
            {
                null => (parts.ElementAtOrDefault(1) ?? parts[0], parts.ElementAtOrDefault(3) ?? parts.ElementAtOrDefault(1) ?? parts[0]),
                "left" => (null, parts[0]),
                "right" => (parts[0], null),
                "inline-start" => (parts[0], null),
                "inline-end" => (null, parts[0]),
                _ => (parts[0], parts.ElementAtOrDefault(1) ?? parts[0]), // inline: start end.
            };

            var declaration = match.Value.TrimEnd();
            var result = new StringBuilder(declaration);
            if (right is not null)
            {
                result.Append(CultureInfo.InvariantCulture, $"; {Property(box, "right")}: {right}{important}");
            }

            if (left is not null)
            {
                result.Append(CultureInfo.InvariantCulture, $"; {Property(box, "left")}: {left}{important}");
            }

            return result.Append(match.Value[declaration.Length..]).ToString(); // The whitespace after the value.
        });

    private static (string Value, string Important) SplitImportant(string value)
    {
        var match = ImportantRegex().Match(value);
        return match.Success ? (value[..match.Index], " !important") : (value, string.Empty);
    }

    /// <summary>The space-separated parts of a value, keeping functions such as <c>calc(1px + 2%)</c> whole.</summary>
    private static List<string> SplitValue(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c) && depth == 0)
            {
                if (start >= 0)
                {
                    parts.Add(value[start..i]);
                    start = -1;
                }

                continue;
            }

            if (start < 0)
            {
                start = i;
            }

            depth += c switch
            {
                '(' => 1,
                ')' => depth > 0 ? -1 : 0,
                _ => 0,
            };
        }

        if (start >= 0)
        {
            parts.Add(value[start..]);
        }

        return parts;
    }

    // A declaration starts a block, follows a semicolon or starts a style attribute, possibly after comments.
    [GeneratedRegex(
        @"(?<=(?:^|[{;])(?:\s|/\*.*?\*/)*)(?<box>margin|padding)(?:-(?<side>left|right|inline-start|inline-end|inline))?\s*:\s*(?<value>[^;{}]*)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"!\s*important\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantRegex();
}
