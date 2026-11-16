namespace RaiCore.Pdf.Html;

/// <summary>
/// Loads a resource the document references (a style sheet, image or font). Returns <c>null</c> if it cannot be loaded;
/// the document is then converted without it and a diagnostic says why.
/// </summary>
/// <remarks>
/// The loader is called on the conversion's own thread. It should observe <see cref="ResourceRequest.CancellationToken"/>,
/// which is canceled when the conversion is canceled or runs out of time (<see cref="ResourceLimits.Timeout"/>).
/// </remarks>
/// <param name="request">What to load.</param>
public delegate byte[]? ResourceLoader(ResourceRequest request);

/// <summary>A resource a <see cref="ResourceLoader"/> is asked to load.</summary>
/// <param name="Uri">The absolute URI of the resource: the reference resolved against <see cref="HtmlConverterOptions.BaseUri"/>.</param>
/// <param name="Kind">What the document uses the resource for.</param>
/// <param name="CancellationToken">Canceled when the conversion is canceled or times out.</param>
public sealed record ResourceRequest(Uri Uri, ResourceKind Kind, CancellationToken CancellationToken = default);

/// <summary>What a document uses a resource for (<see cref="ResourceRequest.Kind"/>).</summary>
public enum ResourceKind
{
    /// <summary>A style sheet: <c>&lt;link rel="stylesheet"&gt;</c>.</summary>
    StyleSheet,

    /// <summary>An image: <c>&lt;img&gt;</c>, or a CSS image such as <c>background-image</c>.</summary>
    Image,

    /// <summary>A web font: a <c>url()</c> in an <c>@font-face</c> rule's <c>src</c>.</summary>
    Font,
}

/// <summary>Built-in resource loading.</summary>
public static class ResourceLoaders
{
    /// <summary>
    /// Loads <c>data:</c> and <c>file:</c> URIs; everything else returns <c>null</c>. Files larger than the
    /// <see cref="ResourceLimits.MaxResourceBytes"/> in effect are not read.
    /// </summary>
    public static byte[]? Default(ResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.Uri;
        if (uri.Scheme == "data")
        {
            return DecodeDataUri(uri.OriginalString);
        }

        if (uri.IsFile && File.Exists(uri.LocalPath))
        {
            return ReadFile(uri.LocalPath, ResourceLimits.Current.MaxResourceBytes);
        }

        return null;
    }

    /// <summary>Decodes a <c>data:[mediatype][;base64],payload</c> URI, or returns <c>null</c> if it is malformed.</summary>
    public static byte[]? DecodeDataUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
        {
            return null;
        }

        var header = uri[5..comma];
        var payload = uri[(comma + 1)..];
        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Uri.UnescapeDataString(payload).Replace(" ", string.Empty, StringComparison.Ordinal))
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file, or returns <c>null</c> if it is larger than <paramref name="maxBytes"/>: also for files of unknown
    /// length (devices and pipes), which are read only that far.
    /// </summary>
    private static byte[]? ReadFile(string path, long maxBytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.CanSeek && file.Length > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Loads a resource referenced by <paramref name="reference"/> (a <c>data:</c> URI, or a URL that resolved to
    /// <paramref name="uri"/>), and on failure says why, for diagnostics.
    /// </summary>
    /// <param name="loader">The resource loader.</param>
    /// <param name="reference">The reference as written.</param>
    /// <param name="uri">The resolved URL; <c>null</c> when it is relative and there is no base URI.</param>
    /// <param name="kind">What the resource is, for the request and the message.</param>
    /// <param name="failure">Why nothing was loaded, or <c>null</c>.</param>
    internal static byte[]? Fetch(ResourceLoader loader, string reference, Uri? uri, ResourceKind kind, out (HtmlDiagnosticKind Kind, string Message)? failure)
    {
        failure = null;
        byte[]? data;
        var subject = Subject(reference);
        var what = kind switch
        {
            ResourceKind.StyleSheet => "style sheet",
            ResourceKind.Font => "font",
            _ => "image",
        };
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            data = DecodeDataUri(reference);
            if (data is null)
            {
                failure = (HtmlDiagnosticKind.ResourceNotLoaded, $"The data: URI of the {what} is malformed.");
                return null;
            }
        }
        else if (uri is null)
        {
            failure = (HtmlDiagnosticKind.ResourceNotLoaded,
                $"The {what} {subject} is a relative URL and there is no base URI to resolve it against (HtmlConverterOptions.BaseUri).");
            return null;
        }
        else
        {
            data = Load(loader, uri, kind);
            if (data is null)
            {
                failure = (HtmlDiagnosticKind.ResourceNotLoaded,
                    $"The {what} {subject} could not be loaded: the resource loader returned nothing (the default one loads only file: and data: URIs, and no files larger than ResourceLimits.MaxResourceBytes).");
                return null;
            }
        }

        var max = ResourceLimits.Current.MaxResourceBytes;
        if (data.LongLength > max)
        {
            failure = (HtmlDiagnosticKind.ResourceTooLarge, $"The {what} {subject} is {data.LongLength} bytes, more than ResourceLimits.MaxResourceBytes ({max}).");
            return null;
        }

        return data;
    }

    /// <summary>
    /// Calls the loader with a token that is canceled when the conversion is canceled or reaches its deadline
    /// (<see cref="ResourceLimits.Timeout"/>), and then turns a cancellation into the conversion's own exception.
    /// </summary>
    private static byte[]? Load(ResourceLoader loader, Uri uri, ResourceKind kind)
    {
        var scope = ResourceLimits.CurrentScope;
        if (scope is null)
        {
            return loader(new ResourceRequest(uri, kind));
        }

        scope.Checkpoint();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
        var remaining = scope.Remaining;
        if (remaining != Timeout.InfiniteTimeSpan && remaining.TotalMilliseconds < int.MaxValue)
        {
            deadline.CancelAfter(remaining);
        }

        byte[]? data;
        try
        {
            data = loader(new ResourceRequest(uri, kind, deadline.Token));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            scope.Checkpoint(); // Throws the cancellation or the timeout.
            throw;
        }

        scope.Checkpoint(); // Loading may have taken a while.
        return data;
    }

    /// <summary>A reference as a diagnostic's subject: a long <c>data:</c> URI is cut after its media type and a few characters.</summary>
    internal static string Subject(string reference) =>
        reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && reference.Length > 48 ? string.Concat(reference.AsSpan(0, 48), "\u2026") : reference;

    /// <summary>
    /// Whether the exception is about a single resource being too large (it is then skipped, like a broken one) rather
    /// than about the whole conversion (which then fails).
    /// </summary>
    internal static bool IsPerResource(ResourceLimitExceededException exception) =>
        exception.LimitName is nameof(ResourceLimits.MaxImagePixels) or nameof(ResourceLimits.MaxResourceBytes)
            or nameof(ResourceLimits.MaxFontBytes) or nameof(ResourceLimits.MaxDecompressedBytes);
}
