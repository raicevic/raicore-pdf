namespace RaiCore.Pdf.Email.Mime;

/// <summary>
/// One part of a MIME message (RFC 2045-2049): its header fields, its content (still transfer-encoded) and, for a
/// multipart, its parts; for an attached message (<c>message/rfc822</c>), the message itself.
/// </summary>
internal sealed class MimeEntity
{
    private byte[]? _content;
    private string? _text;
    private ContentType? _disposition;
    private bool _dispositionParsed;

    /// <summary>The header fields in order: their names, and their unfolded values as bytes in a Latin-1 string.</summary>
    public List<MimeHeader> Headers { get; } = [];

    /// <summary>The content type; <c>text/plain</c> when the part has none (or <c>message/rfc822</c> in a digest).</summary>
    public ContentType ContentType { get; set; } = ContentType.TextPlain;

    /// <summary>The content as it is in the message, still transfer-encoded. Empty for a multipart.</summary>
    public ReadOnlyMemory<byte> RawContent { get; set; }

    /// <summary>The parts of a multipart, in order.</summary>
    public List<MimeEntity> Children { get; } = [];

    /// <summary>The message in a <c>message/rfc822</c> part, parsed; <c>null</c> for other parts.</summary>
    public MimeEntity? Message { get; set; }

    /// <summary>The <c>Content-Transfer-Encoding</c>, lowercase; empty when there is none.</summary>
    public string TransferEncoding => Header("Content-Transfer-Encoding")?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>The <c>Content-Disposition</c>, or <c>null</c>.</summary>
    public ContentType? Disposition
    {
        get
        {
            if (!_dispositionParsed)
            {
                _disposition = Header("Content-Disposition") is { } value ? ContentType.ParseDisposition(value) : null;
                _dispositionParsed = true;
            }

            return _disposition;
        }
    }

    /// <summary>Whether the part says it is an attachment (<c>Content-Disposition: attachment</c>).</summary>
    public bool IsAttachment => Disposition?.MediaType == "attachment";

    /// <summary>Whether the part says it is shown inline (<c>Content-Disposition: inline</c>).</summary>
    public bool IsInline => Disposition?.MediaType == "inline";

    /// <summary>The <c>Content-ID</c> without its angle brackets, or <c>null</c>.</summary>
    public string? ContentId => Header("Content-ID") is { } id ? MimeHeaders.Unbracket(id) : null;

    /// <summary>The <c>Content-Location</c> with folding whitespace removed, or <c>null</c>.</summary>
    public string? ContentLocation => Header("Content-Location") is { } location
        ? MimeHeaders.Decode(string.Concat(location.Where(c => !char.IsWhiteSpace(c))))
        : null;

    /// <summary>The file name: the disposition's <c>filename</c>, else the content type's <c>name</c>; <c>null</c> when neither is given.</summary>
    public string? FileName =>
        Disposition?.Parameter("filename") is { Length: > 0 } fileName ? fileName
        : ContentType.Parameter("name") is { Length: > 0 } name ? name
        : null;

    /// <summary>The content with its transfer encoding (base64, quoted-printable) undone. Decoded once.</summary>
    public byte[] Content => _content ??= TransferDecoding.Decode(RawContent.Span, TransferEncoding);

    /// <summary>The first value of a header field (names compare case-insensitively), unfolded but not decoded; <c>null</c> if absent.</summary>
    public string? Header(string name)
    {
        foreach (var header in Headers)
        {
            if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>Whether the part's media type is <paramref name="mediaType"/>, such as <c>text/html</c>.</summary>
    public bool Is(string mediaType) => ContentType.MediaType == mediaType;

    /// <summary>The text of a text part, decoded with its charset (see <see cref="Charsets.Decode"/>). Decoded once.</summary>
    public string Text => _text ??= Charsets.Decode(Content, ContentType.Parameter("charset"), Is("text/html"));
}

/// <summary>A header field: its name and its unfolded value (the raw bytes as a Latin-1 string).</summary>
internal readonly record struct MimeHeader(string Name, string Value);
