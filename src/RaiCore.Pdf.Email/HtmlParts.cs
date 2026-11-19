using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Email;

/// <summary>
/// An HTML body taken apart so that it can go into the document the converter builds (after the header block, maybe
/// with other bodies): its style sheets (<c>&lt;style&gt;</c> and <c>&lt;link&gt;</c> before the body), the content of
/// its <c>&lt;body&gt;</c>, the body element's own styling, and the document's language.
/// </summary>
/// <param name="Head">The style and link elements before the body, as they are.</param>
/// <param name="Body">The body's content; an unclosed comment at its end is closed.</param>
/// <param name="BodyAttributes">Attributes for an element standing in for the body (style, lang, dir), escaped; may be empty.</param>
/// <param name="Language">The <c>lang</c> of the <c>&lt;html&gt;</c> element, or <c>null</c>.</param>
internal sealed partial record HtmlParts(string Head, string Body, string BodyAttributes, string? Language)
{
    /// <summary>Takes an HTML document (or fragment) apart; never fails, whatever the markup.</summary>
    public static HtmlParts Split(string html)
    {
        var bodyTag = FindTag(html, "body", 0);
        int contentStart;
        int headEnd;
        var attributes = string.Empty;
        if (bodyTag.Start >= 0)
        {
            headEnd = bodyTag.Start;
            contentStart = bodyTag.End;
            attributes = BodyAttributesOf(html[bodyTag.Start..bodyTag.End]);
        }
        else if (FindTag(html, "/head", 0) is { Start: >= 0 } headClose)
        {
            headEnd = headClose.Start;
            contentStart = headClose.End;
        }
        else
        {
            headEnd = 0;
            contentStart = 0;
        }

        var contentEnd = LastIndexOfTag(html, "</body", contentStart);
        if (contentEnd < 0)
        {
            contentEnd = LastIndexOfTag(html, "</html", contentStart);
        }

        if (contentEnd < 0)
        {
            contentEnd = html.Length;
        }

        var body = html[contentStart..contentEnd];
        if (body.LastIndexOf("<!--", StringComparison.Ordinal) > body.LastIndexOf("-->", StringComparison.Ordinal))
        {
            body += "-->"; // Else it would hide everything after it.
        }

        var prolog = html[..headEnd];
        var htmlTag = FindTag(prolog, "html", 0);
        var language = htmlTag.Start >= 0 && HtmlLanguage().Match(prolog, htmlTag.Start, htmlTag.End - htmlTag.Start) is { Success: true } match
            ? match.Groups[1].Value
            : null;
        return new(StyleElements(prolog), body, attributes, language);
    }

    /// <summary>The <c>&lt;style&gt;</c> and <c>&lt;link&gt;</c> elements in text before the body, outside comments.</summary>
    private static string StyleElements(string prolog)
    {
        var head = new StringBuilder();
        var position = 0;
        while (position < prolog.Length)
        {
            var open = prolog.IndexOf('<', position);
            if (open < 0)
            {
                break;
            }

            if (string.CompareOrdinal(prolog, open, "<!--", 0, 4) == 0)
            {
                var close = prolog.IndexOf("-->", open + 4, StringComparison.Ordinal);
                position = close < 0 ? prolog.Length : close + 3;
                continue;
            }

            if (IsTagAt(prolog, open, "style"))
            {
                var contentStart = TagEnd(prolog, open);
                var close = prolog.IndexOf("</style", contentStart, StringComparison.OrdinalIgnoreCase);
                var end = close < 0 ? prolog.Length : TagEnd(prolog, close);
                head.Append(prolog, open, contentStart - open)
                    .Append(CleanStyleSheet(prolog[contentStart..(close < 0 ? prolog.Length : close)]))
                    .Append("</style>\n");
                position = end;
                continue;
            }

            if (IsTagAt(prolog, open, "link"))
            {
                var end = TagEnd(prolog, open);
                head.Append(prolog, open, end - open).Append('\n');
                position = end;
                continue;
            }

            position = open + 1;
        }

        return head.ToString();
    }

    /// <summary>
    /// A message's style sheet without what would change the PDF's pages rather than the message: <c>@page</c> rules
    /// (Word's HTML, as Outlook writes it, sets a Letter page with 1-inch margins), so the options' page applies. The
    /// <c>&lt;!--</c> and <c>--&gt;</c> that hide style sheets from old mail clients are removed too.
    /// </summary>
    internal static string CleanStyleSheet(string css)
    {
        var result = new StringBuilder(css.Length);
        var position = 0;
        while (position < css.Length)
        {
            var c = css[position];
            if (c == '/' && position + 1 < css.Length && css[position + 1] == '*')
            {
                var close = css.IndexOf("*/", position + 2, StringComparison.Ordinal);
                var end = close < 0 ? css.Length : close + 2;
                result.Append(css, position, end - position);
                position = end;
            }
            else if (c is '"' or '\'')
            {
                var end = StringEnd(css, position);
                result.Append(css, position, end - position);
                position = end;
            }
            else if (string.CompareOrdinal(css, position, "<!--", 0, 4) == 0)
            {
                position += 4;
            }
            else if (string.CompareOrdinal(css, position, "-->", 0, 3) == 0)
            {
                position += 3;
            }
            else if (c == '@' && string.Compare(css, position, "@page", 0, 5, StringComparison.OrdinalIgnoreCase) == 0
                && (position + 5 == css.Length || !(char.IsLetterOrDigit(css[position + 5]) || css[position + 5] is '-' or '_')))
            {
                position = BlockEnd(css, position);
            }
            else
            {
                result.Append(c);
                position++;
            }
        }

        return result.ToString();
    }

    /// <summary>Where a quoted CSS string starting at <paramref name="start"/> ends: after its closing quote, or at the end of its line.</summary>
    private static int StringEnd(string css, int start)
    {
        var quote = css[start];
        for (var i = start + 1; i < css.Length; i++)
        {
            if (css[i] == '\\')
            {
                i++;
            }
            else if (css[i] == quote || css[i] == '\n')
            {
                return i + 1;
            }
        }

        return css.Length;
    }

    /// <summary>Where an at-rule starting at <paramref name="start"/> ends: after its block (nested blocks, comments and strings skipped) or its semicolon.</summary>
    private static int BlockEnd(string css, int start)
    {
        var depth = 0;
        for (var i = start; i < css.Length; i++)
        {
            switch (css[i])
            {
                case ';' when depth == 0:
                    return i + 1;
                case '{':
                    depth++;
                    break;
                case '}':
                    if (--depth <= 0)
                    {
                        return i + 1;
                    }

                    break;
                case '"' or '\'':
                    i = StringEnd(css, i) - 1;
                    break;
                case '/' when i + 1 < css.Length && css[i + 1] == '*':
                    var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? css.Length : close + 1;
                    break;
            }
        }

        return css.Length;
    }

    /// <summary>The body element's styling as attributes for a <c>div</c>: <c>bgcolor</c> and <c>text</c> as CSS, then <c>style</c>, <c>lang</c> and <c>dir</c>.</summary>
    private static string BodyAttributesOf(string tag)
    {
        var style = new StringBuilder();
        var others = new StringBuilder();
        foreach (Match match in Attribute().Matches(tag))
        {
            var name = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value;
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            value = value.Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
            switch (name)
            {
                case "bgcolor":
                    style.Append("background-color: ").Append(value).Append("; ");
                    break;
                case "text":
                    style.Append("color: ").Append(value).Append("; ");
                    break;
                case "style":
                    style.Append(value).Append(' ');
                    break;
                case "lang" or "dir":
                    others.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
                    break;
            }
        }

        return (style.Length > 0 ? $" style=\"{style.ToString().TrimEnd()}\"" : string.Empty) + others;
    }

    /// <summary>
    /// The first start tag named <paramref name="name"/> (or end tag, for <c>/name</c>) outside comments, from its
    /// <c>&lt;</c> to after its <c>&gt;</c>; <c>(-1, -1)</c> if there is none.
    /// </summary>
    private static (int Start, int End) FindTag(string html, string name, int from)
    {
        var position = from;
        while (position < html.Length)
        {
            var open = html.IndexOf('<', position);
            if (open < 0)
            {
                break;
            }

            if (string.CompareOrdinal(html, open, "<!--", 0, 4) == 0)
            {
                var close = html.IndexOf("-->", open + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                position = close + 3;
                continue;
            }

            if (IsTagAt(html, open, name))
            {
                return (open, TagEnd(html, open));
            }

            position = open + 1;
        }

        return (-1, -1);
    }

    /// <summary>The last <paramref name="tag"/> (such as <c>&lt;/body</c>) followed by a tag delimiter, at or after <paramref name="from"/>; -1 if none.</summary>
    private static int LastIndexOfTag(string html, string tag, int from)
    {
        var position = html.Length;
        while (position > from)
        {
            var index = html.LastIndexOf(tag, position - 1, position - from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var after = index + tag.Length;
            if (after == html.Length || html[after] is '>' or ' ' or '\t' or '\r' or '\n' or '/')
            {
                return index;
            }

            position = index;
        }

        return -1;
    }

    /// <summary>Whether a tag named <paramref name="name"/> starts at <paramref name="open"/> (its <c>&lt;</c>).</summary>
    private static bool IsTagAt(string html, int open, string name)
    {
        var after = open + 1 + name.Length;
        return after <= html.Length
            && string.Compare(html, open + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (after == html.Length || html[after] is '>' or ' ' or '\t' or '\r' or '\n' or '/');
    }

    /// <summary>Where the tag starting at <paramref name="open"/> ends: after its <c>&gt;</c> outside quoted attribute values, or at the end.</summary>
    private static int TagEnd(string html, int open)
    {
        var quote = '\0';
        var previous = '\0'; // The last character outside whitespace.
        for (var i = open + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'' && previous == '=')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }

            if (!char.IsWhiteSpace(c))
            {
                previous = c;
            }
        }

        return html.Length;
    }

    [GeneratedRegex("""\s([A-Za-z][A-Za-z0-9_:-]*)\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex("""\slang\s*=\s*["']?([A-Za-z0-9-]{1,35})""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlLanguage();
}
