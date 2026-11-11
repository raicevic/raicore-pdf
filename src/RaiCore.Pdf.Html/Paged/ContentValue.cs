using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Html.Paged;

/// <summary>One item of a CSS <c>content</c> or <c>string-set</c> value.</summary>
internal abstract record ContentItem;

/// <summary>Literal text.</summary>
internal sealed record TextItem(string Text) : ContentItem;

/// <summary>
/// <c>counter(name[, style])</c>: the innermost counter of that name (in margin boxes, the page counters
/// <c>page</c> and <c>pages</c> and other counters of the page context).
/// </summary>
internal sealed record CounterItem(string Name, string Style) : ContentItem;

/// <summary><c>counters(name, separator[, style])</c>: all counters of that name in scope, outermost first.</summary>
internal sealed record CountersItem(string Name, string Separator, string Style) : ContentItem;

/// <summary>
/// The element a cross-reference points to: the fragment of a URL taken from an attribute
/// (<c>attr(href)</c>, <c>attr(href url)</c>) or written out (<c>"#id"</c>, <c>url(#id)</c>).
/// </summary>
/// <param name="Attribute">The attribute holding the URL, or <c>null</c>.</param>
/// <param name="Url">The URL when written out, or <c>null</c>.</param>
internal sealed record TargetReference(string? Attribute, string? Url);

/// <summary>
/// <c>target-counter(url, name[, style])</c>: the value of a counter at the target element (for <c>page</c>, the
/// page it is on).
/// </summary>
internal sealed record TargetCounterItem(TargetReference Target, string Name, string Style) : ContentItem;

/// <summary><c>target-counters(url, name, separator[, style])</c>: all counters of that name at the target element.</summary>
internal sealed record TargetCountersItem(TargetReference Target, string Name, string Separator, string Style) : ContentItem;

/// <summary>
/// <c>target-text(url[, content|before|after|first-letter])</c>: the text of the target element (or of its
/// <c>::before</c>/<c>::after</c>, or its first letter).
/// </summary>
internal sealed record TargetTextItem(TargetReference Target, string Part) : ContentItem;

/// <summary><c>leader(dotted|solid|space|string)</c>: the pattern repeated to fill the rest of the line.</summary>
internal sealed record LeaderItem(string Pattern) : ContentItem;

/// <summary><c>string(name[, policy])</c>: a running string set with <c>string-set</c>.</summary>
internal sealed record StringItem(string Name, string Policy) : ContentItem;

/// <summary><c>element(name[, policy])</c>: a running element (<c>position: running(name)</c>).</summary>
internal sealed record ElementItem(string Name, string Policy) : ContentItem;

/// <summary>
/// <c>content(part)</c> in <c>string-set</c> and <c>bookmark-label</c>: the element's text (<c>text</c>, the default),
/// or another part such as <c>first-letter</c>.
/// </summary>
internal sealed record ElementTextItem(string Part = "text") : ContentItem;

/// <summary><c>attr(name)</c>: an attribute of the element (in <c>string-set</c>, <c>bookmark-label</c> and <c>content</c>).</summary>
internal sealed record AttributeItem(string Name) : ContentItem;

/// <summary>
/// <c>open-quote</c>, <c>close-quote</c>, <c>no-open-quote</c> or <c>no-close-quote</c> (in the <c>content</c> of
/// <c>::before</c> and <c>::after</c>).
/// </summary>
internal sealed record QuoteItem(string Kind) : ContentItem;

/// <summary><c>url(address)</c>: an image (in the <c>content</c> of <c>::before</c> and <c>::after</c>).</summary>
internal sealed record ImageItem(string Url) : ContentItem;

/// <summary>
/// Parses CSS content values: strings (with CSS escapes), quotes, images and the functions used in generated content
/// and paged media.
/// </summary>
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
                var quoted = new List<bool>();
                var arguments = pos < trimmed.Length && trimmed[pos] == '(' ? ReadArguments(trimmed, ref pos, quoted) : [];
                string Arg(int i, string fallback) => i < arguments.Count && arguments[i].Length > 0 ? arguments[i] : fallback;
                ContentItem? item = name switch
                {
                    "counter" => new CounterItem(Arg(0, "page"), Arg(1, "decimal").ToLowerInvariant()),
                    "counters" => new CountersItem(Arg(0, string.Empty), arguments.Count > 1 ? arguments[1] : string.Empty, Arg(2, "decimal").ToLowerInvariant()),
                    "target-counter" => new TargetCounterItem(Target(Arg(0, string.Empty)), Arg(1, "page"), Arg(2, "decimal").ToLowerInvariant()),
                    "target-counters" => new TargetCountersItem(
                        Target(Arg(0, string.Empty)), Arg(1, string.Empty), arguments.Count > 2 ? arguments[2] : string.Empty, Arg(3, "decimal").ToLowerInvariant()),
                    "target-text" => new TargetTextItem(Target(Arg(0, string.Empty)), TextPart(Arg(1, "content"))),
                    "leader" => new LeaderItem(quoted.Count > 0 && quoted[0] ? Arg(0, string.Empty) : Arg(0, "dotted").ToLowerInvariant() switch
                    {
                        "dotted" => ". ",
                        "solid" => "_",
                        "space" => " ",
                        _ => string.Empty,
                    }),
                    "string" => new StringItem(Arg(0, string.Empty), Arg(1, "first").ToLowerInvariant()),
                    "element" => new ElementItem(Arg(0, string.Empty), Arg(1, "first").ToLowerInvariant()),
                    "content" => new ElementTextItem(Arg(0, "text").ToLowerInvariant()),
                    "attr" => new AttributeItem(Arg(0, string.Empty)),
                    "open-quote" or "close-quote" or "no-open-quote" or "no-close-quote" => new QuoteItem(name),
                    "url" => new ImageItem(string.Join(',', arguments).Trim()), // An unquoted data URI has commas.
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

    /// <summary>
    /// The URL argument of a <c>target-*()</c> function: <c>attr(name [url])</c>, or a URL as a string or
    /// <c>url()</c>.
    /// </summary>
    private static TargetReference Target(string argument)
    {
        var lower = argument.ToLowerInvariant();
        if (lower.StartsWith("attr(", StringComparison.Ordinal) && argument.EndsWith(')'))
        {
            var inner = argument[5..^1].Trim();
            var space = inner.IndexOfAny([' ', '\t']);
            return new TargetReference(space < 0 ? inner : inner[..space], null);
        }

        if (lower.StartsWith("url(", StringComparison.Ordinal) && argument.EndsWith(')'))
        {
            argument = argument[4..^1].Trim().Trim('"', '\'');
        }

        return new TargetReference(null, argument);
    }

    /// <summary>The part of <c>target-text()</c>: <c>content</c>, <c>before</c>, <c>after</c> or <c>first-letter</c>.</summary>
    private static string TextPart(string argument)
    {
        var part = argument.Trim().ToLowerInvariant();
        if (part.StartsWith("content(", StringComparison.Ordinal) && part.EndsWith(')'))
        {
            part = part[8..^1].Trim(); // content(before), as some write it.
        }

        return part is "before" or "after" or "first-letter" ? part : "content";
    }

    /// <summary>
    /// Reads a function's arguments, separated by commas. Top-level strings are decoded (<paramref name="quoted"/>
    /// says which arguments were strings); nested functions such as <c>attr(href)</c> are kept as written.
    /// </summary>
    private static List<string> ReadArguments(string text, ref int pos, List<bool> quoted)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var isString = false;
        var depth = 0;
        pos++; // (
        while (pos < text.Length && (depth > 0 || text[pos] != ')'))
        {
            var c = text[pos];
            if (c is '"' or '\'' && depth == 0)
            {
                current.Append(ReadString(text, ref pos));
                isString = true;
            }
            else if (c is '"' or '\'')
            {
                var start = pos++;
                while (pos < text.Length && text[pos] != c)
                {
                    pos += text[pos] == '\\' ? 2 : 1;
                }

                pos = Math.Min(pos + 1, text.Length);
                current.Append(text, start, pos - start);
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(current.ToString().Trim());
                quoted.Add(isString);
                current.Clear();
                isString = false;
                pos++;
            }
            else
            {
                depth += c == '(' ? 1 : c == ')' ? -1 : 0;
                current.Append(c);
                pos++;
            }
        }

        pos++; // )
        arguments.Add(current.ToString().Trim());
        quoted.Add(isString);
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
