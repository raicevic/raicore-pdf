using System.Text;
using System.Text.RegularExpressions;

namespace RaiCore.Pdf.Email;

/// <summary>
/// Plain text bodies as HTML: escaped, with web and mail addresses as links, in an element that keeps the line breaks
/// and wraps long lines (<c>white-space: pre-wrap</c>). <c>format=flowed</c> text (RFC 3676) has its soft line breaks
/// joined first, so its paragraphs wrap to the page.
/// </summary>
internal static partial class PlainText
{
    /// <summary>Appends <paramref name="text"/> as an HTML element of the given class.</summary>
    public static void AppendHtml(StringBuilder html, string text, bool flowed, bool deleteSpace, string cssClass)
    {
        text = text.ReplaceLineEndings("\n");
        if (flowed)
        {
            text = Unflow(text, deleteSpace);
        }

        html.Append("<div class=\"").Append(cssClass).Append("\">");
        AppendLinked(html, text);
        html.Append("</div>\n");
    }

    /// <summary>Appends text escaped for HTML, with URLs (<c>http:</c>, <c>https:</c>, <c>ftp:</c>, <c>mailto:</c>, <c>www.</c>) as links.</summary>
    public static void AppendLinked(StringBuilder html, string text)
    {
        var position = 0;
        foreach (Match match in Url().Matches(text))
        {
            var url = TrimUrl(match.Value);
            if (url.Length == 0)
            {
                continue;
            }

            AppendEscaped(html, text.AsSpan(position, match.Index - position));
            var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + url : url;
            html.Append("<a href=\"");
            AppendEscaped(html, href);
            html.Append("\">");
            AppendEscaped(html, url);
            html.Append("</a>");
            position = match.Index + url.Length;
        }

        AppendEscaped(html, text.AsSpan(position));
    }

    /// <summary>Appends text with <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> and <c>"</c> escaped, and characters HTML cannot hold dropped.</summary>
    public static void AppendEscaped(StringBuilder html, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':
                    html.Append("&amp;");
                    break;
                case '<':
                    html.Append("&lt;");
                    break;
                case '>':
                    html.Append("&gt;");
                    break;
                case '"':
                    html.Append("&quot;");
                    break;
                case '\0':
                    break;
                default:
                    html.Append(c);
                    break;
            }
        }
    }

    /// <summary>Escapes text for HTML.</summary>
    public static string Escape(string text)
    {
        var html = new StringBuilder(text.Length);
        AppendEscaped(html, text);
        return html.ToString();
    }

    /// <summary>
    /// Joins the lines of <c>format=flowed</c> text that end in a space (soft line breaks) with the next line of the
    /// same quote depth, removing space-stuffing, and with <c>DelSp=yes</c> the space before each soft break. The
    /// signature separator <c>-- </c> is never joined. Quoted lines keep their <c>&gt;</c> marks.
    /// </summary>
    public static string Unflow(string text, bool deleteSpace)
    {
        var result = new StringBuilder(text.Length);
        var paragraph = new StringBuilder();
        var depth = -1;
        foreach (var raw in text.Split('\n'))
        {
            var quotes = 0;
            while (quotes < raw.Length && raw[quotes] == '>')
            {
                quotes++;
            }

            var line = raw[quotes..];
            if (line.StartsWith(' '))
            {
                line = line[1..]; // Space-stuffing.
            }

            if (depth >= 0 && quotes != depth)
            {
                EndParagraph(); // A flowed line followed by one of another depth: the flow ends there.
            }

            var soft = line.EndsWith(' ') && line != "-- ";
            if (soft && deleteSpace)
            {
                line = line[..^1];
            }

            paragraph.Append(line);
            depth = quotes;
            if (!soft)
            {
                EndParagraph();
            }
        }

        if (depth >= 0)
        {
            EndParagraph();
        }

        return result.ToString(0, Math.Max(0, result.Length - 1)); // Without the last line break, which Split added.

        void EndParagraph()
        {
            if (depth > 0)
            {
                result.Append('>', depth).Append(' ');
            }

            result.Append(paragraph).Append('\n');
            paragraph.Clear();
            depth = -1;
        }
    }

    /// <summary>A URL without the punctuation that usually follows one in text, and without an unbalanced closing parenthesis.</summary>
    private static string TrimUrl(string url)
    {
        var parentheses = 0;
        var brackets = 0;
        foreach (var c in url)
        {
            parentheses += c switch { '(' => 1, ')' => -1, _ => 0 };
            brackets += c switch { '[' => 1, ']' => -1, _ => 0 };
        }

        var end = url.Length;
        while (end > 0)
        {
            var last = url[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*')
            {
                end--;
            }
            else if (last == ')' && parentheses < 0)
            {
                parentheses++;
                end--;
            }
            else if (last == ']' && brackets < 0)
            {
                brackets++;
                end--;
            }
            else
            {
                break;
            }
        }

        url = url[..end];
        return url.ToLowerInvariant() is "www." or "http://" or "https://" or "ftp://" or "mailto:" ? string.Empty : url;
    }

    [GeneratedRegex("""\b(?:(?:https?|ftp)://|mailto:|www\.)[^\s<>"\u0000-\u001F\u007F]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}
