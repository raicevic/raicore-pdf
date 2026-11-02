using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>One item of a CSS <c>content</c> or <c>string-set</c> value.</summary>
internal abstract record ContentItem;

/// <summary>Literal text.</summary>
internal sealed record TextItem(string Text) : ContentItem;

/// <summary><c>counter(page|pages[, style])</c>.</summary>
internal sealed record CounterItem(string Name, string Style) : ContentItem;

/// <summary><c>string(name[, policy])</c>: a running string set with <c>string-set</c>.</summary>
internal sealed record StringItem(string Name, string Policy) : ContentItem;

/// <summary><c>element(name[, policy])</c>: a running element (<c>position: running(name)</c>).</summary>
internal sealed record ElementItem(string Name, string Policy) : ContentItem;

/// <summary><c>content()</c> in <c>string-set</c>: the element's text.</summary>
internal sealed record ElementTextItem : ContentItem;

/// <summary><c>attr(name)</c>: an attribute of the element (in <c>string-set</c>).</summary>
internal sealed record AttributeItem(string Name) : ContentItem;

/// <summary>Parses CSS content values: strings (with CSS escapes) and the functions used in paged media.</summary>
internal static class ContentValue
{
    /// <summary>Parses a value; returns null for <c>none</c>/<c>normal</c> (no content).</summary>
    public static IReadOnlyList<ContentItem>? Parse(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var items = new List<ContentItem>();
        var pos = 0;
        while (pos < trimmed.Length)
        {
            var c = trimmed[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
            }
            else if (c is '"' or '\'')
            {
                items.Add(new TextItem(ReadString(trimmed, ref pos)));
            }
            else if (char.IsLetter(c) || c == '-')
            {
                var start = pos;
                while (pos < trimmed.Length && (char.IsLetterOrDigit(trimmed[pos]) || trimmed[pos] is '-' or '_'))
                {
                    pos++;
                }

                var name = trimmed[start..pos].ToLowerInvariant();
                var arguments = pos < trimmed.Length && trimmed[pos] == '(' ? ReadArguments(trimmed, ref pos) : [];
                string Arg(int i, string fallback) => i < arguments.Count && arguments[i].Length > 0 ? arguments[i] : fallback;
                ContentItem? item = name switch
                {
                    "counter" => new CounterItem(Arg(0, "page").ToLowerInvariant(), Arg(1, "decimal").ToLowerInvariant()),
                    "string" => new StringItem(Arg(0, string.Empty), Arg(1, "first").ToLowerInvariant()),
                    "element" => new ElementItem(Arg(0, string.Empty), Arg(1, "first").ToLowerInvariant()),
                    "content" => new ElementTextItem(),
                    "attr" => new AttributeItem(Arg(0, string.Empty)),
                    _ => null,
                };
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            else
            {
                pos++;
            }
        }

        return items;
    }

    private static List<string> ReadArguments(string text, ref int pos)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        pos++; // (
        while (pos < text.Length && text[pos] != ')')
        {
            if (text[pos] is '"' or '\'')
            {
                current.Append(ReadString(text, ref pos));
            }
            else if (text[pos] == ',')
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                pos++;
            }
            else
            {
                current.Append(text[pos++]);
            }
        }

        pos++; // )
        arguments.Add(current.ToString().Trim());
        return arguments;
    }

    /// <summary>Reads a quoted CSS string, decoding escapes such as <c>\2014</c> and <c>\"</c>.</summary>
    private static string ReadString(string text, ref int pos)
    {
        var quote = text[pos++];
        var output = new StringBuilder();
        while (pos < text.Length && text[pos] != quote)
        {
            if (text[pos] == '\\' && pos + 1 < text.Length)
            {
                pos++;
                var hexStart = pos;
                while (pos < text.Length && pos - hexStart < 6 && Uri.IsHexDigit(text[pos]))
                {
                    pos++;
                }

                if (pos > hexStart)
                {
                    var code = int.Parse(text.AsSpan(hexStart, pos - hexStart), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    output.Append(code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "\uFFFD");
                    if (pos < text.Length && text[pos] == ' ')
                    {
                        pos++; // A single space ends a hex escape.
                    }
                }
                else
                {
                    output.Append(text[pos++]);
                }
            }
            else
            {
                output.Append(text[pos++]);
            }
        }

        pos++;
        return output.ToString();
    }
}
