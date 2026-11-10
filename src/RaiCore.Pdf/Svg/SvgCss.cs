using System.Text;

namespace RaiCore.Pdf.Svg;

/// <summary>A compound selector such as <c>path.st0#logo</c> (any part may be missing).</summary>
internal sealed record SvgCompoundSelector(string? Type, string? Id, IReadOnlyList<string> Classes)
{
    public bool Matches(SvgElement element) =>
        (Type is null || element.Name == Type)
        && (Id is null || element.Id == Id)
        && Classes.All(element.HasClass);
}

/// <summary>A selector: compound selectors joined by descendant (space) or child (<c>&gt;</c>) combinators.</summary>
internal sealed record SvgSelector(IReadOnlyList<SvgCompoundSelector> Parts, IReadOnlyList<bool> ChildCombinators)
{
    /// <summary>CSS specificity packed into one number: ids, then classes, then types.</summary>
    public int Specificity =>
        Parts.Sum(p => (p.Id is null ? 0 : 10_000) + (p.Classes.Count * 100) + (p.Type is null ? 0 : 1));

    public bool Matches(SvgElement element) => Matches(element, Parts.Count - 1);

    private bool Matches(SvgElement element, int index)
    {
        if (!Parts[index].Matches(element))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        if (ChildCombinators[index - 1])
        {
            return element.Parent is { } parent && Matches(parent, index - 1);
        }

        for (var ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (Matches(ancestor, index - 1))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record SvgCssRule(SvgSelector Selector, int Order, IReadOnlyList<(string Name, string Value, bool Important)> Declarations);

/// <summary>
/// A small CSS parser for <c>&lt;style&gt;</c> elements in SVG files: rules with type, class and id selectors and
/// descendant or child combinators (as exported by Illustrator, Inkscape and Figma). At-rules and selectors with
/// anything else (attributes, pseudo-classes) are skipped.
/// </summary>
internal static class SvgCss
{
    public static List<SvgCssRule> Parse(string css, ref int order)
    {
        var rules = new List<SvgCssRule>();
        var text = RemoveComments(css);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('{', i);
            if (open < 0)
            {
                break;
            }

            var prelude = text[i..open].Trim();
            var close = FindBlockEnd(text, open);
            var body = text[(open + 1)..Math.Min(close, text.Length)];
            i = close + 1;

            // At-rules (@media, @font-face, @import ...) are not supported; a statement at-rule before a rule ends at ';'.
            while (prelude.StartsWith('@'))
            {
                var semicolon = prelude.IndexOf(';', StringComparison.Ordinal);
                if (semicolon < 0)
                {
                    break;
                }

                prelude = prelude[(semicolon + 1)..].Trim();
            }

            if (prelude.Length == 0 || prelude.StartsWith('@'))
            {
                continue;
            }

            var declarations = ParseDeclarations(body);
            foreach (var part in prelude.Split(','))
            {
                if (ParseSelector(part) is { } selector)
                {
                    rules.Add(new SvgCssRule(selector, order++, declarations));
                }
            }
        }

        return rules;
    }

    /// <summary>Parses declarations such as <c>fill: red; stroke-width: 2 !important</c>; property names are lower-cased.</summary>
    public static List<(string Name, string Value, bool Important)> ParseDeclarations(string text)
    {
        var result = new List<(string, string, bool)>();
        foreach (var declaration in text.Split(';'))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim();
            var important = false;
            var bang = value.LastIndexOf('!');
            if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..bang].Trim();
            }

            if (name.Length > 0 && value.Length > 0)
            {
                result.Add((name, value, important));
            }
        }

        return result;
    }

    private static SvgSelector? ParseSelector(string text)
    {
        var parts = new List<SvgCompoundSelector>();
        var child = new List<bool>();
        var pendingChild = false;
        foreach (var token in text.Replace(">", " > ", StringComparison.Ordinal).Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == ">")
            {
                if (parts.Count == 0 || pendingChild)
                {
                    return null;
                }

                pendingChild = true;
                continue;
            }

            if (ParseCompound(token) is not { } compound)
            {
                return null;
            }

            if (parts.Count > 0)
            {
                child.Add(pendingChild);
            }

            parts.Add(compound);
            pendingChild = false;
        }

        return parts.Count == 0 || pendingChild ? null : new SvgSelector(parts, child);
    }

    private static SvgCompoundSelector? ParseCompound(string token)
    {
        string? type = null;
        string? id = null;
        var classes = new List<string>();
        var i = 0;
        if (token[0] == '*')
        {
            i = 1;
        }
        else if (IsNameChar(token[0]))
        {
            i = ReadName(token, 0);
            type = token[..i];
        }

        while (i < token.Length)
        {
            var kind = token[i];
            var end = ReadName(token, i + 1);
            if (end == i + 1 || kind is not ('.' or '#'))
            {
                return null; // Attribute selectors, pseudo-classes and the like are not supported.
            }

            var name = token[(i + 1)..end];
            if (kind == '.')
            {
                classes.Add(name);
            }
            else
            {
                id = name;
            }

            i = end;
        }

        return new SvgCompoundSelector(type, id, classes);
    }

    private static int ReadName(string text, int start)
    {
        var i = start;
        while (i < text.Length && IsNameChar(text[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > 127;

    private static int FindBlockEnd(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
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

    private static string RemoveComments(string css)
    {
        var result = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            var start = css.IndexOf("/*", i, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(css, i, css.Length - i);
                break;
            }

            result.Append(css, i, start - i);
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            i = end < 0 ? css.Length : end + 2;
        }

        return result.ToString();
    }
}
