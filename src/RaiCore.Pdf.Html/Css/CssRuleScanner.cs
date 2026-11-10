using System.Text;

namespace RaiCore.Pdf.Html.Css;

/// <summary>A declaration: property name (lowercase) and its raw value.</summary>
internal readonly record struct CssDeclaration(string Property, string Value);

/// <summary>A style rule from raw CSS: its selector text and declarations.</summary>
internal sealed record CssStyleRule(string Selector, IReadOnlyList<CssDeclaration> Declarations);

/// <summary>An <c>@page</c> rule: its selector (e.g. "" or ":first"), declarations and margin boxes (e.g. "top-center").</summary>
internal sealed record CssPageRule(string Selector, IReadOnlyList<CssDeclaration> Declarations, IReadOnlyDictionary<string, IReadOnlyList<CssDeclaration>> MarginBoxes);

/// <summary>
/// A small CSS scanner for what AngleSharp.Css does not keep: <c>@page</c> rules with margin boxes and the
/// <c>size</c> descriptor, <c>@font-face</c> rules, and declarations it drops such as <c>string-set</c> and
/// <c>position: running()</c>.
/// Handles comments, strings, escapes and nesting; rules inside <c>@media</c> are kept only for print/all media.
/// </summary>
internal static class CssRuleScanner
{
    public static (List<CssStyleRule> StyleRules, List<CssPageRule> PageRules) Scan(string css)
    {
        var styles = new List<CssStyleRule>();
        var pages = new List<CssPageRule>();
        ScanBlock(StripComments(css), styles, pages, fontFaces: null);
        return (styles, pages);
    }

    /// <summary>The descriptors of every <c>@font-face</c> rule (including those inside print media rules).</summary>
    public static List<IReadOnlyList<CssDeclaration>> ScanFontFaces(string css)
    {
        var fontFaces = new List<IReadOnlyList<CssDeclaration>>();
        ScanBlock(StripComments(css), [], [], fontFaces);
        return fontFaces;
    }

    private static void ScanBlock(string css, List<CssStyleRule> styles, List<CssPageRule> pages, List<IReadOnlyList<CssDeclaration>>? fontFaces)
    {
        var pos = 0;
        while (pos < css.Length)
        {
            var open = IndexOutsideStrings(css, '{', pos);
            if (open < 0)
            {
                return;
            }

            var prelude = css[pos..open].Trim();
            var close = MatchingBrace(css, open);
            var body = css[(open + 1)..close];
            pos = close + 1;

            // Statements such as "@import url(x);" end with a semicolon before the next block.
            var semicolon = prelude.LastIndexOf(';');
            if (semicolon >= 0)
            {
                prelude = prelude[(semicolon + 1)..].Trim();
            }

            if (prelude.StartsWith("@page", StringComparison.OrdinalIgnoreCase))
            {
                pages.Add(ParsePage(prelude[5..].Trim(), body));
            }
            else if (prelude.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                var media = prelude[6..].ToUpperInvariant();
                if (media.Contains("PRINT", StringComparison.Ordinal) || media.Contains("ALL", StringComparison.Ordinal) || media.Trim().Length == 0)
                {
                    ScanBlock(body, styles, pages, fontFaces);
                }
            }
            else if (prelude.StartsWith("@supports", StringComparison.OrdinalIgnoreCase))
            {
                ScanBlock(body, styles, pages, fontFaces);
            }
            else if (prelude.Equals("@font-face", StringComparison.OrdinalIgnoreCase))
            {
                fontFaces?.Add(ParseDeclarations(body));
            }
            else if (!prelude.StartsWith('@') && prelude.Length > 0)
            {
                styles.Add(new CssStyleRule(prelude, ParseDeclarations(body)));
            }
        }
    }

    private static CssPageRule ParsePage(string selector, string body)
    {
        var boxes = new Dictionary<string, IReadOnlyList<CssDeclaration>>(StringComparer.OrdinalIgnoreCase);
        var declarations = new StringBuilder();
        var pos = 0;
        while (pos < body.Length)
        {
            var at = IndexOutsideStrings(body, '@', pos);
            if (at < 0)
            {
                declarations.Append(body, pos, body.Length - pos);
                break;
            }

            declarations.Append(body, pos, at - pos);
            var open = IndexOutsideStrings(body, '{', at);
            if (open < 0)
            {
                break;
            }

            var close = MatchingBrace(body, open);
            boxes[body[(at + 1)..open].Trim()] = ParseDeclarations(body[(open + 1)..close]);
            pos = close + 1;
        }

        return new CssPageRule(selector, ParseDeclarations(declarations.ToString()), boxes);
    }

    public static List<CssDeclaration> ParseDeclarations(string body)
    {
        var result = new List<CssDeclaration>();
        foreach (var part in SplitOutsideStrings(body, ';'))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var value = part[(colon + 1)..].Trim();
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^10].Trim();
            }

            result.Add(new CssDeclaration(part[..colon].Trim().ToLowerInvariant(), value));
        }

        return result;
    }

    private static string StripComments(string css)
    {
        var output = new StringBuilder(css.Length);
        for (var i = 0; i < css.Length; i++)
        {
            if (css[i] is '"' or '\'')
            {
                var end = EndOfString(css, i);
                output.Append(css, i, end - i + 1);
                i = end;
            }
            else if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 1;
                output.Append(' ');
            }
            else
            {
                output.Append(css[i]);
            }
        }

        return output.ToString();
    }

    private static int EndOfString(string text, int start)
    {
        var quote = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    private static int IndexOutsideStrings(string text, char target, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = EndOfString(text, i);
            }
            else if (text[i] == target)
            {
                return i;
            }
        }

        return -1;
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = EndOfString(text, i);
            }
            else if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return text.Length;
    }

    private static IEnumerable<string> SplitOutsideStrings(string text, char separator)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"' or '\'':
                    i = EndOfString(text, i);
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    if (text[i] == separator && depth == 0)
                    {
                        yield return text[start..i];
                        start = i + 1;
                    }

                    break;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
