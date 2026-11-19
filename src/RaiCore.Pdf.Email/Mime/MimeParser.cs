using System.Text;

namespace RaiCore.Pdf.Email.Mime;

/// <summary>
/// Parses a MIME message (RFC 5322 and 2045-2049) into its parts, in one pass over its lines, leniently as mail clients
/// do: lines may end in CRLF or LF; a part whose closing boundary is missing ends at an enclosing part's boundary or at
/// the end; a header section ends at a line that is no header field. Nesting is limited by
/// <see cref="ResourceLimits.MaxNestingDepth"/> (and <see cref="MaxDepth"/>) and the number of parts by <see cref="ResourceLimits.MaxElements"/>;
/// attached messages are parsed up to <see cref="MaxMessageDepth"/> deep, deeper ones are kept as they are.
/// </summary>
internal sealed class MimeParser
{
    /// <summary>How deep attached messages in attached messages are parsed (each one is read once more).</summary>
    public const int MaxMessageDepth = 8;

    /// <summary>
    /// How deep parts may nest at most, whatever <see cref="ResourceLimits.MaxNestingDepth"/> allows: parsing and
    /// reading the parts recurse once per level, on the caller's stack. Real messages nest a few levels.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>How many header fields of a part are kept; the rest are read and dropped.</summary>
    private const int MaxHeaders = 1000;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly ResourceLimits _limits;
    private readonly PartCounter _parts;
    private readonly int _messageDepth;
    private readonly List<byte[]> _boundaries = [];
    private int _position;

    private MimeParser(ReadOnlyMemory<byte> data, ResourceLimits limits, PartCounter parts, int messageDepth)
    {
        _data = data;
        _limits = limits;
        _parts = parts;
        _messageDepth = messageDepth;
    }

    /// <summary>Parses a message.</summary>
    /// <exception cref="ResourceLimitExceededException">It nests deeper than <see cref="ResourceLimits.MaxNestingDepth"/> or has more than <see cref="ResourceLimits.MaxElements"/> parts.</exception>
    public static MimeEntity Parse(ReadOnlyMemory<byte> data, ResourceLimits limits) => new MimeParser(data, limits, new PartCounter(), 0).ParseEntity(0, ContentType.TextPlain);

    private MimeEntity ParseEntity(int depth, ContentType defaultType)
    {
        var maxDepth = Math.Min(_limits.MaxNestingDepth, MaxDepth);
        if (depth > maxDepth)
        {
            throw new ResourceLimitExceededException(
                nameof(ResourceLimits.MaxNestingDepth), maxDepth, $"The message's parts nest more than {maxDepth} deep (ResourceLimits.MaxNestingDepth).");
        }

        if (++_parts.Count > _limits.MaxElements)
        {
            throw new ResourceLimitExceededException(
                nameof(ResourceLimits.MaxElements), _limits.MaxElements, $"The message has more than {_limits.MaxElements} parts (ResourceLimits.MaxElements).");
        }

        var entity = new MimeEntity();
        ReadHeaders(entity);
        entity.ContentType = entity.Header("Content-Type") is { } type ? ContentType.Parse(type) : defaultType;
        if (entity.ContentType.IsMultipart && entity.ContentType.Parameter("boundary") is { Length: > 0 } boundary)
        {
            ParseMultipart(entity, Encoding.Latin1.GetBytes("--" + boundary.Trim()), depth);
            return entity;
        }

        var start = _position;
        var end = ReadToBoundary();
        entity.RawContent = _data[start..end];
        if (entity.ContentType.MediaType is "message/rfc822" or "message/global" && _messageDepth < MaxMessageDepth)
        {
            // Parsed from its own (decoded) content: the enclosing parts' boundaries do not apply inside it.
            var nested = new MimeParser(entity.Content, _limits, _parts, _messageDepth + 1);
            entity.Message = nested.ParseEntity(depth + 1, ContentType.TextPlain);
        }

        return entity;
    }

    private void ParseMultipart(MimeEntity entity, byte[] boundary, int depth)
    {
        _boundaries.Add(boundary);
        var childType = entity.ContentType.MediaType == "multipart/digest" ? ContentType.MessageRfc822 : ContentType.TextPlain;
        var closed = false;
        ReadToBoundary(); // The preamble.
        while (_position < _data.Length)
        {
            var (match, closing, next) = MatchBoundary(_position);
            if (match != _boundaries.Count - 1)
            {
                break; // An enclosing part's boundary: this one was never closed.
            }

            _position = next;
            if (closing)
            {
                closed = true;
                break;
            }

            entity.Children.Add(ParseEntity(depth + 1, childType));
        }

        _boundaries.RemoveAt(_boundaries.Count - 1);
        if (closed)
        {
            ReadToBoundary(); // The epilogue, up to an enclosing boundary.
        }
    }

    /// <summary>
    /// Reads header fields up to the empty line that ends them (which is skipped), a boundary line or a line that is no
    /// header field (both left for the content).
    /// </summary>
    private void ReadHeaders(MimeEntity entity)
    {
        var data = _data.Span;
        var first = true;
        StringBuilder? value = null;
        string? name = null;
        while (_position < data.Length)
        {
            var (lineEnd, next) = Line(data, _position);
            var line = data[_position..lineEnd];
            if (line.IsEmpty)
            {
                _position = next;
                break;
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                if (name is not null)
                {
                    value!.Append(Encoding.Latin1.GetString(line)); // Unfolding: the line break goes, the whitespace stays.
                    _position = next;
                    continue;
                }

                if (first)
                {
                    _position = next; // Leading whitespace lines before the first field.
                    continue;
                }
            }

            if (MatchBoundary(_position).Match >= 0)
            {
                break;
            }

            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsFieldName(line[..colon]))
            {
                if (first && line.StartsWith("From "u8))
                {
                    _position = next; // An mbox "From " line before the message.
                    first = false;
                    continue;
                }

                break; // Not a header field: the content starts here.
            }

            Add(entity, name, value);
            name = Encoding.Latin1.GetString(line[..colon]).TrimEnd();
            value = new StringBuilder(Encoding.Latin1.GetString(line[(colon + 1)..]));
            first = false;
            _position = next;
        }

        Add(entity, name, value);

        static void Add(MimeEntity entity, string? name, StringBuilder? value)
        {
            if (name is not null && entity.Headers.Count < MaxHeaders)
            {
                entity.Headers.Add(new(name, value!.ToString().Trim()));
            }
        }
    }

    /// <summary>
    /// Skips lines up to a boundary line of this part or an enclosing one (or the end), and returns where the content
    /// before it ends: the line break before the boundary belongs to the boundary.
    /// </summary>
    private int ReadToBoundary()
    {
        var data = _data.Span;
        var start = _position;
        if (_boundaries.Count == 0)
        {
            _position = data.Length;
            return data.Length;
        }

        while (_position < data.Length)
        {
            if (MatchBoundary(_position).Match >= 0)
            {
                var end = _position;
                if (end > start && data[end - 1] == '\n')
                {
                    end--;
                    if (end > start && data[end - 1] == '\r')
                    {
                        end--;
                    }
                }

                return end;
            }

            _position = Line(data, _position).Next;
        }

        return data.Length;
    }

    /// <summary>
    /// Whether the line at <paramref name="position"/> is a boundary line: <c>--boundary</c>, optionally followed by
    /// whitespace, or by <c>--</c> (closing, with anything after it). Returns the innermost matching boundary's index in the stack (-1 for none),
    /// whether it closes, and where the next line starts.
    /// </summary>
    private (int Match, bool Closing, int Next) MatchBoundary(int position)
    {
        var data = _data.Span;
        if (_boundaries.Count == 0 || position + 2 > data.Length || data[position] != '-' || data[position + 1] != '-')
        {
            return (-1, false, position);
        }

        var (lineEnd, next) = Line(data, position);
        var line = data[position..lineEnd];
        for (var i = _boundaries.Count - 1; i >= 0; i--)
        {
            var boundary = _boundaries[i];
            if (!line.StartsWith(boundary))
            {
                continue;
            }

            var rest = line[boundary.Length..];
            if (rest.StartsWith("--"u8))
            {
                return (i, true, next); // A closing boundary, whatever follows it (some mailers add text).
            }

            if (rest.TrimEnd(" \t"u8).IsEmpty)
            {
                return (i, false, next);
            }
        }

        return (-1, false, position);
    }

    /// <summary>The end of the line at <paramref name="start"/> (before its CRLF or LF) and the start of the next one.</summary>
    private static (int End, int Next) Line(ReadOnlySpan<byte> data, int start)
    {
        var newline = data[start..].IndexOf((byte)'\n');
        if (newline < 0)
        {
            return (data.Length, data.Length);
        }

        var end = start + newline;
        return (end > start && data[end - 1] == '\r' ? end - 1 : end, end + 1);
    }

    /// <summary>Whether bytes are a header field name: printable ASCII other than the colon, allowing trailing whitespace.</summary>
    private static bool IsFieldName(ReadOnlySpan<byte> name)
    {
        name = name.TrimEnd(" \t"u8);
        if (name.IsEmpty)
        {
            return false;
        }

        foreach (var b in name)
        {
            if (b is <= 32 or >= 127)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number of parts parsed so far, shared with the parsers of attached messages.</summary>
    private sealed class PartCounter
    {
        public int Count { get; set; }
    }
}
