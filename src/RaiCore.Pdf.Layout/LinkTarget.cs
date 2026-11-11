namespace RaiCore.Pdf.Layout;

/// <summary>
/// Where a link goes: a URI (a web page, a <c>mailto:</c> address) or a place in the document named by a block's
/// <see cref="Blocks.Block.Id"/> or a paragraph's <see cref="Paragraph.AddAnchor"/>. Used for links in text
/// (<see cref="Paragraph.BeginLink"/>) and on whole blocks (<see cref="Blocks.Block.Link"/>).
/// </summary>
public sealed class LinkTarget
{
    private LinkTarget(Uri? uri, string? destination)
    {
        Uri = uri;
        Destination = destination;
    }

    /// <summary>The URI the link opens, or <c>null</c> for a link within the document.</summary>
    public Uri? Uri { get; }

    /// <summary>The id of the block or anchor the link jumps to, or <c>null</c> for a link to a URI.</summary>
    public string? Destination { get; }

    /// <summary>A link that opens a URI. Relative URIs are kept as they are.</summary>
    public static LinkTarget ToUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new LinkTarget(uri, null);
    }

    /// <summary>
    /// A link to the block or paragraph anchor with this id (see <see cref="Blocks.Block.Id"/> and
    /// <see cref="Paragraph.AddAnchor"/>). A link to an id that nothing in the document has is left out of the PDF.
    /// </summary>
    public static LinkTarget ToDestination(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return new LinkTarget(null, id);
    }

    /// <inheritdoc />
    public override string ToString() => Uri?.ToString() ?? "#" + Destination;

    /// <summary>
    /// Adds the link annotation for a rectangle in the canvas's coordinates, with a description for screen readers (such
    /// as the link's text); empty rectangles are skipped.
    /// </summary>
    internal void AddTo(PdfCanvas canvas, double x, double y, double width, double height, string? description = null)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var link = Uri is { } uri ? canvas.AddLink(x, y, width, height, uri) : canvas.AddLink(x, y, width, height, Destination!);
        link.Description = description;
    }
}
