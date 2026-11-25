using System.Text;

namespace RaiCore.Pdf.Html.Css;

/// <summary>
/// CSS Nesting (CSS Nesting Module 1), which AngleSharp.Css does not parse: rewrites nested style rules into flat
/// ones before the style sheet is parsed. In a nested selector, <c>&amp;</c> stands for the parent rule's selector
/// (one rule per parent selector when it has several, which is what <c>:is()</c> matches for the usual case of
/// <c>&amp;</c> at the start); a nested selector without <c>&amp;</c> is relative to the parent, as a descendant or after
/// its leading combinator (<c>&gt; li</c>). Conditional group rules nested in a style rule (<c>@media</c>,
/// <c>@supports</c>, <c>@layer</c>, <c>@container</c>) wrap the parent's selector around their declarations. A sheet
/// without nested rules comes back unchanged; other at-rules (<c>@page</c>, <c>@font-face</c>, <c>@keyframes</c>, ...) are
/// kept as they are.
/// </summary>
internal static class CssNesting
{
    /// <summary>Nested rules deeper than this are dropped (with their declarations) instead of being flattened.</summary>
    private const int MaxDepth = 32;

    /// <summary>At-rules whose blocks hold rules (and, nested in a style rule, declarations) to flatten.</summary>
    private static readonly HashSet<string> GroupRules = new(StringComparer.OrdinalIgnoreCase) { "media", "supports", "layer", "container", "scope", "document", "-moz-document" };

    public static string Flatten(string css)
    {
        if (!css.Contains('{', StringComparison.Ordinal))
        {
            return css; // Declarations only (a style attribute), or nothing to nest.
        }

        var parser = new Parser(css);
        var output = new StringBuilder(css.Length + 64);
        parser.Sheet(output, depth: 0);
        return parser.Nested ? output.ToString() : css;
    }

    private sealed class Parser(string css)
    {
        private int _position;

        /// <summary>Whether a nested rule was found (and flattened).</summary>
        public bool Nested { get; private set; }

        /// <summary>A sheet or a group rule's block: rules and at-rules up to the closing brace (consumed) or the end.</summary>
        public void Sheet(StringBuilder output, int depth)
        {
            while (true)
            {
                SkipSpaceAndComments();
                if (_position >= css.Length)
                {
                    return;
                }

                if (css[_position] == '}')
                {
                    _position++;
                    return;
                }

                var start = _position;
                var (end, terminator) = ScanItem(_position);
                var prelude = css[start..end].Trim();
                _position = Math.Min(end + 1, css.Length);
                if (terminator != '{')
                {
                    // A statement (@import, @charset, @namespace) or a stray declaration: kept.
                    output.Append(css, start, end - start);
                    if (terminator == ';')
                    {
                        output.Append(';');
                    }
                    else if (terminator == '}')
                    {
                        return;
                    }

                    continue;
                }

                if (prelude.StartsWith('@'))
                {
                    var name = AtRuleName(prelude);
                    if (GroupRules.Contains(name) && depth < MaxDepth)
                    {
                        output.Append(prelude).Append(" {\n");
                        Sheet(output, depth + 1);
                        output.Append("}\n");
                    }
                    else
                    {
                        // Its block as it is (@page with its margin boxes, @font-face, @keyframes, ...).
                        var blockEnd = MatchingBrace(_position);
                        output.Append(prelude).Append(" {").Append(css, _position, blockEnd - _position).Append("}\n");
                        _position = Math.Min(blockEnd + 1, css.Length);
                    }

                    continue;
                }

                StyleRule(output, SplitSelectors(prelude), depth);
            }
        }

        /// <summary>
        /// The block of a style rule for <paramref name="selectors"/>, up to its closing brace (consumed): writes the rule
        /// with its own declarations, then the rules nested in it, flattened.
        /// </summary>
        private void StyleRule(StringBuilder output, List<string> selectors, int depth)
        {
            var declarations = new StringBuilder();
            var nested = new StringBuilder();
            Body(declarations, nested, selectors, depth);
            if (declarations.Length > 0 && selectors.Count > 0)
            {
                output.Append(string.Join(", ", selectors)).Append(" {").Append(declarations).Append("}\n");
            }

            output.Append(nested);
        }

        /// <summary>
        /// The items of a style rule's block (or of a group rule nested in one): its declarations go to
        /// <paramref name="declarations"/>, its nested rules, flattened, to <paramref name="nested"/>.
        /// </summary>
        private void Body(StringBuilder declarations, StringBuilder nested, List<string> parents, int depth)
        {
            while (true)
            {
                SkipSpaceAndComments();
                if (_position >= css.Length)
                {
                    return;
                }

                if (css[_position] == '}')
                {
                    _position++;
                    return;
                }

                var start = _position;
                var (end, terminator) = ScanItem(_position);
                var text = css[start..end].Trim();
                _position = Math.Min(end + 1, css.Length);
                if (terminator == '{' && text.StartsWith("--", StringComparison.Ordinal))
                {
                    // A custom property whose value is a block ({} values have no use here): dropped.
                    _position = Math.Min(MatchingBrace(_position) + 1, css.Length);
                    continue;
                }

                if (terminator != '{')
                {
                    if (text.Length > 0)
                    {
                        declarations.Append(' ').Append(text).Append(';');
                    }

                    if (terminator == '}')
                    {
                        return;
                    }

                    continue;
                }

                Nested = true;
                if (depth + 1 >= MaxDepth)
                {
                    _position = Math.Min(MatchingBrace(_position) + 1, css.Length); // Too deep: dropped.
                    continue;
                }

                if (text.StartsWith('@'))
                {
                    if (GroupRules.Contains(AtRuleName(text)))
                    {
                        // A group rule in a style rule: its declarations apply to the parent's selector, under its condition.
                        var innerDeclarations = new StringBuilder();
                        var innerNested = new StringBuilder();
                        Body(innerDeclarations, innerNested, parents, depth + 1);
                        nested.Append(text).Append(" {\n");
                        if (innerDeclarations.Length > 0 && parents.Count > 0)
                        {
                            nested.Append(string.Join(", ", parents)).Append(" {").Append(innerDeclarations).Append("}\n");
                        }

                        nested.Append(innerNested).Append("}\n");
                    }
                    else
                    {
                        _position = Math.Min(MatchingBrace(_position) + 1, css.Length); // No meaning here: dropped.
                    }

                    continue;
                }

                StyleRule(nested, Resolve(parents, SplitSelectors(text)), depth + 1);
            }
        }

        /// <summary>The selectors of a nested rule, with <c>&amp;</c> standing for each parent selector.</summary>
        private static List<string> Resolve(List<string> parents, List<string> selectors)
        {
            var resolved = new List<string>();
            foreach (var selector in selectors)
            {
                foreach (var parent in parents)
                {
                    resolved.Add(selector.Contains('&', StringComparison.Ordinal) ? selector.Replace("&", parent, StringComparison.Ordinal) : parent + " " + selector);
                }
            }

            return resolved;
        }

        /// <summary>A selector list split at its top-level commas, trimmed, without empty entries.</summary>
        private static List<string> SplitSelectors(string list)
        {
            var selectors = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < list.Length; i++)
            {
                switch (list[i])
                {
                    case '\\':
                        i++;
                        break;
                    case '"' or '\'':
                        i = StringEnd(list, i);
                        break;
                    case '(' or '[':
                        depth++;
                        break;
                    case ')' or ']':
                        depth = Math.Max(0, depth - 1);
                        break;
                    case ',' when depth == 0:
                        Add(list[start..i]);
                        start = i + 1;
                        break;
                }
            }

            Add(list[start..]);
            return selectors;

            void Add(string selector)
            {
                if (selector.Trim() is { Length: > 0 } trimmed)
                {
                    selectors.Add(trimmed);
                }
            }
        }

        private static string AtRuleName(string prelude)
        {
            var end = 1;
            while (end < prelude.Length && (char.IsLetterOrDigit(prelude[end]) || prelude[end] is '-' or '_'))
            {
                end++;
            }

            return prelude[1..end];
        }

        /// <summary>
        /// The end of the item starting at <paramref name="start"/>: the first <c>;</c>, <c>{</c> or <c>}</c> outside
        /// parentheses, brackets, strings and comments (the terminator), or the end of the sheet (terminator <c>\0</c>).
        /// </summary>
        private (int End, char Terminator) ScanItem(int start)
        {
            var depth = 0;
            for (var i = start; i < css.Length; i++)
            {
                switch (css[i])
                {
                    case '\\':
                        i++;
                        break;
                    case '"' or '\'':
                        i = StringEnd(css, i);
                        break;
                    case '/' when i + 1 < css.Length && css[i + 1] == '*':
                        i = CommentEnd(i);
                        break;
                    case '(' or '[':
                        depth++;
                        break;
                    case ')' or ']':
                        depth = Math.Max(0, depth - 1);
                        break;
                    case ';' or '{' or '}' when depth == 0:
                        return (i, css[i]);
                }
            }

            return (css.Length, '\0');
        }

        /// <summary>The closing brace of the block whose content starts at <paramref name="start"/>, or the end of the sheet.</summary>
        private int MatchingBrace(int start)
        {
            var depth = 0;
            for (var i = start; i < css.Length; i++)
            {
                switch (css[i])
                {
                    case '\\':
                        i++;
                        break;
                    case '"' or '\'':
                        i = StringEnd(css, i);
                        break;
                    case '/' when i + 1 < css.Length && css[i + 1] == '*':
                        i = CommentEnd(i);
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}' when depth == 0:
                        return i;
                    case '}':
                        depth--;
                        break;
                }
            }

            return css.Length;
        }

        private void SkipSpaceAndComments()
        {
            while (_position < css.Length)
            {
                if (char.IsWhiteSpace(css[_position]))
                {
                    _position++;
                }
                else if (css[_position] == '/' && _position + 1 < css.Length && css[_position + 1] == '*')
                {
                    _position = CommentEnd(_position) + 1;
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>The last character of the comment starting at <paramref name="start"/> (or the end of the sheet).</summary>
        private int CommentEnd(int start)
        {
            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? css.Length - 1 : end + 1;
        }

        /// <summary>The closing quote of the string starting at <paramref name="start"/> (or where it ends unclosed).</summary>
        private static int StringEnd(string text, int start)
        {
            var quote = text[start];
            for (var i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == quote || text[i] == '\n')
                {
                    return i;
                }
            }

            return text.Length - 1;
        }
    }
}
