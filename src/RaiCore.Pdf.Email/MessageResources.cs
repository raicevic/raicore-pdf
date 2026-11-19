using RaiCore.Pdf.Email.Mime;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Email;

/// <summary>
/// The resource loader of a message's conversion: images and style sheets come from the message's own parts, by
/// <c>cid:</c> reference (RFC 2392) or by <c>Content-Location</c> (RFC 2557, as in web archives). Other URLs go to the
/// options' loader, except <c>file:</c> URLs, which a message never loads.
/// </summary>
internal sealed class MessageResources
{
    /// <summary>The base of a message whose parts have only relative locations: a name that resolves nowhere (RFC 2606).</summary>
    private static readonly Uri LocalBase = new("https://message.invalid/");

    private readonly Dictionary<string, MimeEntity> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MimeEntity> _byIdIgnoringCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MimeEntity> _byLocation = new(StringComparer.Ordinal);
    private readonly ResourceLoader _next;

    public MessageResources(MessageContent content, ResourceLoader next, Uri? baseUri)
    {
        _next = next;
        var leaves = MessageReader.Leaves(content.Root).ToList();
        var html = content.Body.Find(b => b.Kind == BodyKind.Html).Part;
        BaseUri = AbsoluteLocation(html) ?? AbsoluteLocation(content.Root) ?? baseUri
            ?? (leaves.Exists(p => p.ContentLocation is { Length: > 0 } && AbsoluteLocation(p) is null) ? LocalBase : null);
        foreach (var part in leaves)
        {
            if (part.ContentId is { Length: > 0 } id)
            {
                _byId.TryAdd(id, part);
                _byIdIgnoringCase.TryAdd(id, part);
            }

            if (part.ContentLocation is { Length: > 0 } location && Resolve(location) is { } uri)
            {
                _byLocation.TryAdd(Key(uri), part);
            }
        }
    }

    /// <summary>
    /// The base URI of the HTML: the absolute <c>Content-Location</c> of the HTML body or the message, else the options'
    /// base URI, else (when parts have relative locations) one that only resolves them.
    /// </summary>
    public Uri? BaseUri { get; }

    /// <summary>Registers a part under a <c>cid:</c> reference of the converter's own, for images it shows (inline images, appended attachments).</summary>
    public string Reference(MimeEntity part, int index)
    {
        var id = $"raicore-part-{index}@message.invalid";
        _byId[id] = part;
        return "cid:" + id;
    }

    /// <summary>The loader for <see cref="HtmlConverterOptions.ResourceLoader"/>.</summary>
    public byte[]? Load(ResourceRequest request)
    {
        var uri = request.Uri;
        if (uri.Scheme == "cid")
        {
            var id = MimeHeaders.Unbracket(Uri.UnescapeDataString(uri.OriginalString[(uri.OriginalString.IndexOf(':', StringComparison.Ordinal) + 1)..]));
            if (_byId.TryGetValue(id, out var part) || _byIdIgnoringCase.TryGetValue(id, out part))
            {
                return part.Content;
            }
        }

        if (_byLocation.TryGetValue(Key(uri), out var located))
        {
            return located.Content;
        }

        // "/images/a.png" parses as a file path on Unix: it is relative to the base.
        if (uri.IsFile && uri.OriginalString.StartsWith('/') && BaseUri is not null && Uri.TryCreate(BaseUri, uri.OriginalString, out var relative)
            && _byLocation.TryGetValue(Key(relative), out located))
        {
            return located.Content;
        }

        if (uri.IsFile || uri.Scheme == "cid" || uri.Host == LocalBase.Host)
        {
            return null; // Never a local file; and a part that is not there is not elsewhere either.
        }

        return _next(request);
    }

    private Uri? Resolve(string location) =>
        IsAbsolute(location, out var absolute) ? absolute
        : BaseUri is not null && Uri.TryCreate(BaseUri, location, out var relative) ? relative
        : null;

    private static Uri? AbsoluteLocation(MimeEntity? part) =>
        part?.ContentLocation is { Length: > 0 } location && IsAbsolute(location, out var uri) ? uri : null;

    /// <summary>Whether a location is an absolute URI (not a path starting with a slash, which Unix takes for a file).</summary>
    private static bool IsAbsolute(string location, out Uri? uri)
    {
        uri = null;
        return !location.StartsWith('/') && !location.StartsWith('\\') && Uri.TryCreate(location, UriKind.Absolute, out uri);
    }

    /// <summary>A URI as a key: absolute, without its fragment.</summary>
    private static string Key(Uri uri)
    {
        var text = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
        var hash = text.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? text : text[..hash];
    }
}
