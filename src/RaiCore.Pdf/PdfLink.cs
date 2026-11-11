using System.Text;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// A place in the document to jump to: a page, scrolled so that a position is at the top-left of the window. Used by
/// links (<see cref="PdfPage.AddLink(double, double, double, double, PdfDestination)"/>) and named destinations
/// (<see cref="PdfDocument.NamedDestinations"/>).
/// </summary>
public sealed class PdfDestination
{
    /// <summary>Creates a destination on <paramref name="page"/>, scrolled so that <paramref name="top"/> is at the top of the window.</summary>
    /// <param name="page">The target page. It must belong to the document the destination is saved with.</param>
    /// <param name="top">The PDF y coordinate (from the bottom of the page) to show at the top of the window, or <c>null</c> to keep the viewer's position.</param>
    /// <param name="left">The PDF x coordinate to show at the left of the window, or <c>null</c> to keep the viewer's position.</param>
    public PdfDestination(PdfPage page, double? top = null, double? left = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        Page = page;
        Top = top;
        Left = left;
    }

    /// <summary>The target page.</summary>
    public PdfPage Page { get; }

    /// <summary>The PDF y coordinate (from the bottom of the page) to show at the top of the window, or <c>null</c> to keep the viewer's position.</summary>
    public double? Top { get; }

    /// <summary>The PDF x coordinate to show at the left of the window, or <c>null</c> to keep the viewer's position.</summary>
    public double? Left { get; }

    /// <summary>The destination array <c>[page /XYZ left top null]</c> (ISO 32000-2, section 12.3.2.2).</summary>
    /// <remarks>Positions on an imported page are measured from its media box, and written in its user space.</remarks>
    internal PdfArray ToArray(PdfReference page) =>
        new(page, new PdfName("XYZ"), Number(Left + Page.Origin.X), Number(Top + Page.Origin.Y), PdfNull.Instance);

    private static PdfObject Number(double? value) => value is { } v ? new PdfReal(v) : PdfNull.Instance;
}

/// <summary>
/// A link on a page (a link annotation): a rectangle that opens a URI or jumps to a place in the document when clicked.
/// Created with <see cref="PdfPage.AddLink(double, double, double, double, Uri)"/> and its overloads, or with the
/// <see cref="PdfCanvas"/> equivalents.
/// </summary>
public sealed class PdfLink
{
    internal PdfLink(double x, double y, double width, double height, Uri? uri, string? destinationName, PdfDestination? destination)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Uri = uri;
        DestinationName = destinationName;
        Destination = destination;
    }

    /// <summary>The left edge of the clickable area, in PDF points from the left of the page.</summary>
    public double X { get; }

    /// <summary>The bottom edge of the clickable area, in PDF points from the bottom of the page.</summary>
    public double Y { get; }

    /// <summary>The width of the clickable area.</summary>
    public double Width { get; }

    /// <summary>The height of the clickable area.</summary>
    public double Height { get; }

    /// <summary>The URI the link opens (a URI action), or <c>null</c> for a link within the document.</summary>
    public Uri? Uri { get; }

    /// <summary>The named destination (see <see cref="PdfDocument.NamedDestinations"/>) the link jumps to, or <c>null</c>.</summary>
    public string? DestinationName { get; }

    /// <summary>The explicit destination the link jumps to, or <c>null</c>.</summary>
    public PdfDestination? Destination { get; }

    /// <summary>
    /// A description of the link (the annotation's <c>/Contents</c>), such as its text or where it goes, which screen
    /// readers announce. In a <see cref="PdfDocument.Tagged"/> document a link without one is described by its URI or
    /// destination name, as PDF/UA requires a description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>The Link structure element of a tagged document the annotation belongs to.</summary>
    internal PdfStructureElement? StructureElement { get; set; }
}

/// <summary>Writes link annotations and the named destinations tree (ISO 32000-2, sections 12.5.6.5 and 12.3.2.4).</summary>
internal static class PdfLinkWriter
{
    /// <summary>
    /// Writes a page's links as annotation objects and returns the page's <c>/Annots</c> array, or <c>null</c> when it
    /// has none. Links to named destinations the document does not define are left out. <paramref name="written"/> is
    /// told about each annotation written (for the structure tree of a tagged document).
    /// </summary>
    public static PdfArray? WriteAnnotations(
        PdfFileWriter writer, PdfPage page, IReadOnlyDictionary<PdfPage, PdfReference> pages, IDictionary<string, PdfDestination> names,
        Action<PdfLink, PdfDictionary, PdfReference>? written = null)
    {
        var annotations = new PdfArray();
        foreach (var link in page.Links)
        {
            var (x, y) = (link.X + page.Origin.X, link.Y + page.Origin.Y); // Imported pages: from the media box's corner.
            var annotation = new PdfDictionary
            {
                ["Type"] = new PdfName("Annot"),
                ["Subtype"] = new PdfName("Link"),
                ["Rect"] = PdfArray.OfNumbers(x, y, x + link.Width, y + link.Height),
                ["Border"] = PdfArray.OfNumbers(0, 0, 0), // No border: the link is the content under it.
            };

            if (link.Description is { Length: > 0 } description)
            {
                annotation["Contents"] = PdfString.FromText(description);
            }

            if (link.Uri is { } uri)
            {
                annotation["A"] = new PdfDictionary
                {
                    ["S"] = new PdfName("URI"),
                    ["URI"] = new PdfString(Encoding.ASCII.GetBytes(EncodeUri(uri))),
                };
            }
            else if (link.DestinationName is { } name)
            {
                if (!names.ContainsKey(name))
                {
                    continue;
                }

                annotation["Dest"] = PdfString.FromText(name);
            }
            else if (link.Destination is { } destination)
            {
                if (!pages.TryGetValue(destination.Page, out var target))
                {
                    throw new InvalidOperationException("A link points to a page that is not in this document.");
                }

                annotation["Dest"] = destination.ToArray(target);
            }

            var reference = writer.Add(annotation);
            annotations.Items.Add(reference);
            written?.Invoke(link, annotation, reference);
        }

        return annotations.Items.Count > 0 ? annotations : null;
    }

    /// <summary>
    /// Writes the named destinations as a name tree (a single leaf, keys sorted by their bytes) and returns a reference
    /// to it, for the catalog's <c>/Names /Dests</c>.
    /// </summary>
    public static PdfReference WriteNames(PdfFileWriter writer, IDictionary<string, PdfDestination> names, IReadOnlyDictionary<PdfPage, PdfReference> pages)
    {
        var entries = names
            .Select(entry => (Key: PdfString.FromText(entry.Key), entry.Key, entry.Value))
            .OrderBy(entry => entry.Key.Bytes.ToArray(), ByteComparer.Instance)
            .ToList();

        var array = new PdfArray();
        foreach (var (key, name, destination) in entries)
        {
            if (destination is null)
            {
                throw new InvalidOperationException($"The named destination \"{name}\" is null.");
            }

            if (!pages.TryGetValue(destination.Page, out var page))
            {
                throw new InvalidOperationException($"The named destination \"{name}\" points to a page that is not in this document.");
            }

            array.Items.Add(key);
            array.Items.Add(destination.ToArray(page));
        }

        return writer.Add(new PdfDictionary { ["Names"] = array });
    }

    /// <summary>
    /// The URI as 7-bit ASCII, as URI actions require: absolute URIs in their escaped form, relative ones as written, and
    /// any remaining space, control or non-ASCII character percent-encoded as UTF-8.
    /// </summary>
    internal static string EncodeUri(Uri uri)
    {
        var text = uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString;
        var builder = new StringBuilder(text.Length);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b is > 0x20 and < 0x7F)
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>Orders byte strings lexically, as name trees require.</summary>
    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
