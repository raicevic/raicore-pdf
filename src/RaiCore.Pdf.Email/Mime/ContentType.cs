using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Email.Mime;

/// <summary>
/// A <c>Content-Type</c> (<c>type/subtype; name=value</c>) or <c>Content-Disposition</c> (<c>attachment;
/// filename=value</c>) header: the value before the parameters, lowercase, and the parameters, decoded. Parameters may
/// be split and charset-encoded (RFC 2231: <c>filename*0*=utf-8''...</c>) or, as many mailers write them, hold
/// encoded words (RFC 2047: <c>name="=?utf-8?B?...?="</c>).
/// </summary>
internal sealed class ContentType
{
    private readonly Dictionary<string, string> _parameters;

    private ContentType(string mediaType, Dictionary<string, string> parameters)
    {
        MediaType = mediaType;
        _parameters = parameters;
    }

    /// <summary>The default content type of a part without one (RFC 2045, section 5.2).</summary>
    public static ContentType TextPlain { get; } = new("text/plain", new(StringComparer.OrdinalIgnoreCase));

    /// <summary>The default content type of a part in a <c>multipart/digest</c>.</summary>
    public static ContentType MessageRfc822 { get; } = new("message/rfc822", new(StringComparer.OrdinalIgnoreCase));

    /// <summary>The media type (<c>text/html</c>) or disposition (<c>attachment</c>), lowercase.</summary>
    public string MediaType { get; }

    /// <summary>The part of the media type before the slash: <c>text</c>, <c>multipart</c>, <c>image</c>.</summary>
    public string Type => MediaType.IndexOf('/', StringComparison.Ordinal) is var slash and >= 0 ? MediaType[..slash] : MediaType;

    /// <summary>Whether this is a <c>multipart/*</c> type.</summary>
    public bool IsMultipart => Type == "multipart";

    /// <summary>A parameter's decoded value (names compare case-insensitively), or <c>null</c>.</summary>
    public string? Parameter(string name) => _parameters.GetValueOrDefault(name);

    /// <summary>Parses a <c>Content-Type</c> value; a missing or malformed type gives <c>text/plain</c> with the parameters found.</summary>
    public static ContentType Parse(string value)
    {
        var (type, parameters) = Split(value);
        type = type.ToLowerInvariant();
        var slash = type.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == type.Length - 1 || type.IndexOf('/', slash + 1) >= 0)
        {
            // "text" alone, "/html", "text/": a mail client shows it as text.
            type = type == "text" ? "text/plain" : string.Empty;
        }

        return new(type.Length == 0 ? "text/plain" : type, parameters);
    }

    /// <summary>Parses a <c>Content-Disposition</c> value; the disposition is lowercase, possibly empty.</summary>
    public static ContentType ParseDisposition(string value)
    {
        var (type, parameters) = Split(value);
        return new(type.ToLowerInvariant(), parameters);
    }

    /// <summary>Splits a header value into the value before the first <c>;</c> and the decoded parameters.</summary>
    private static (string Value, Dictionary<string, string> Parameters) Split(string header)
    {
        var position = 0;
        var value = ReadUntilSemicolon(header, ref position, stopAtEquals: false, skipComments: true).Trim();
        var plain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extended = new Dictionary<string, List<(int Index, bool Encoded, string Value)>>(StringComparer.OrdinalIgnoreCase);
        while (position < header.Length)
        {
            position++; // The ';'.
            var name = ReadUntilSemicolon(header, ref position, stopAtEquals: true, skipComments: true).Trim();
            if (position >= header.Length || header[position] != '=')
            {
                continue; // "; ;" or a name without a value.
            }

            position++;
            SkipWhitespace(header, ref position);
            string raw;
            if (position < header.Length && header[position] == '"')
            {
                raw = ReadQuoted(header, ref position);
                ReadUntilSemicolon(header, ref position, stopAtEquals: false, skipComments: true); // Anything after the closing quote.
            }
            else
            {
                // Not skipping comments: an unquoted file name such as report(1).pdf is more common than a comment here.
                raw = ReadUntilSemicolon(header, ref position, stopAtEquals: false, skipComments: false).Trim();
            }

            if (name.Length == 0)
            {
                continue;
            }

            var star = name.IndexOf('*', StringComparison.Ordinal);
            if (star < 0)
            {
                plain.TryAdd(name, raw);
                continue;
            }

            // RFC 2231: name*=charset'lang'%XX, name*0=..., name*1*=... in any order.
            var baseName = name[..star];
            var rest = name[(star + 1)..];
            var encoded = rest.EndsWith('*') || rest.Length == 0;
            var indexText = rest.TrimEnd('*');
            var index = 0;
            if (indexText.Length > 0 && !int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out index))
            {
                continue;
            }

            if (baseName.Length == 0 || index > 10_000)
            {
                continue;
            }

            if (!extended.TryGetValue(baseName, out var sections))
            {
                extended[baseName] = sections = [];
            }

            sections.Add((index, encoded, raw));
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in plain)
        {
            parameters[name] = MimeHeaders.Decode(raw);
        }

        foreach (var (name, sections) in extended)
        {
            parameters[name] = JoinSections(sections); // Takes precedence over a plain value (RFC 2231, section 4.1).
        }

        return (value, parameters);
    }

    /// <summary>
    /// Puts the sections of an RFC 2231 parameter together, in index order: the encoded ones are percent-decoded
    /// bytes in the charset the first section names (<c>charset'language'</c>), the others plain text.
    /// </summary>
    private static string JoinSections(List<(int Index, bool Encoded, string Value)> sections)
    {
        sections.Sort((a, b) => a.Index.CompareTo(b.Index));
        string? charset = null;
        var text = new StringBuilder();
        var bytes = new List<byte>();
        var first = true;
        var previous = -1;
        foreach (var (index, encoded, raw) in sections)
        {
            if (index == previous)
            {
                continue; // A repeated section: the first one counts.
            }

            previous = index;
            var value = raw;
            if (encoded && first)
            {
                var quote1 = value.IndexOf('\'', StringComparison.Ordinal);
                var quote2 = quote1 < 0 ? -1 : value.IndexOf('\'', quote1 + 1);
                if (quote2 >= 0)
                {
                    charset = value[..quote1];
                    value = value[(quote2 + 1)..];
                }
            }

            first = false;
            if (encoded)
            {
                PercentDecode(value, bytes);
            }
            else
            {
                Flush(bytes, charset, text);
                text.Append(MimeHeaders.Decode(value));
            }
        }

        Flush(bytes, charset, text);
        return text.ToString();

        static void Flush(List<byte> bytes, string? charset, StringBuilder text)
        {
            if (bytes.Count > 0)
            {
                text.Append(Charsets.Decode(bytes.ToArray(), charset, html: false));
                bytes.Clear();
            }
        }
    }

    /// <summary>Appends the bytes of a percent-encoded value (a <c>%</c> not followed by two hex digits stays as it is).</summary>
    private static void PercentDecode(string value, List<byte> bytes)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add((byte)((Uri.FromHex(value[i + 1]) << 4) | Uri.FromHex(value[i + 2])));
                i += 2;
            }
            else
            {
                bytes.Add((byte)value[i]); // The header's bytes, as a Latin-1 string.
            }
        }
    }

    /// <summary>Reads up to the next <c>;</c> (or <c>=</c>), skipping comments in parentheses if asked; quoted strings are unquoted.</summary>
    private static string ReadUntilSemicolon(string header, ref int position, bool stopAtEquals, bool skipComments)
    {
        var text = new StringBuilder();
        while (position < header.Length)
        {
            var c = header[position];
            if (c == ';' || (stopAtEquals && c == '='))
            {
                break;
            }

            if (c == '(' && skipComments)
            {
                SkipComment(header, ref position);
                continue;
            }

            if (c == '"' && !stopAtEquals)
            {
                text.Append(ReadQuoted(header, ref position));
                continue;
            }

            text.Append(c);
            position++;
        }

        return text.ToString();
    }

    /// <summary>Reads a quoted string starting at its opening quote, unescaping <c>\x</c>; an unclosed one runs to the end.</summary>
    private static string ReadQuoted(string header, ref int position)
    {
        var text = new StringBuilder();
        position++; // The opening quote.
        while (position < header.Length)
        {
            var c = header[position++];
            if (c == '"')
            {
                break;
            }

            if (c == '\\' && position < header.Length)
            {
                c = header[position++];
            }

            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>Skips a comment starting at its <c>(</c>; comments nest, and an unclosed one runs to the end.</summary>
    internal static void SkipComment(string header, ref int position)
    {
        var depth = 0;
        while (position < header.Length)
        {
            var c = header[position++];
            if (c == '\\')
            {
                position++;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return;
            }
        }
    }

    private static void SkipWhitespace(string header, ref int position)
    {
        while (position < header.Length && header[position] is ' ' or '\t' or '\r' or '\n')
        {
            position++;
        }
    }
}
