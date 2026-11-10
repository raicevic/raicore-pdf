using System.Text.RegularExpressions;
using AngleSharp.Dom;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>Hyperlinks (<c>a href</c>) and the destinations they jump to (elements with an <c>id</c>, <c>a name</c>).</summary>
internal sealed partial class BoxBuilder
{
    /// <summary>
    /// Where an <c>a href</c> element goes, or <c>null</c> for other elements and links that go nowhere: <c>#id</c> is
    /// a link within the document; anything else a URI, resolved against the base URI when there is one (kept as
    /// written otherwise). Empty and <c>javascript:</c> links are dropped.
    /// </summary>
    private LinkTarget? LinkFor(IElement element)
    {
        if (element.LocalName != "a" || element.GetAttribute("href")?.Trim() is not { } href)
        {
            return null;
        }

        if (href.StartsWith('#'))
        {
            var id = Uri.UnescapeDataString(href[1..]);
            return id.Length > 0 ? LinkTarget.ToDestination(id) : null;
        }

        if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Not ResolveUri: on Unix, Uri parses "/path" as an absolute file path, while for a link it is relative to the base.
        var uri = _baseUri is not null && Uri.TryCreate(_baseUri, href, out var resolved) ? resolved
            : SchemeRegex().IsMatch(href) && Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute
            : Uri.TryCreate(href, UriKind.Relative, out var relative) ? relative
            : null;
        return uri is null ? null : LinkTarget.ToUri(uri);
    }

    /// <summary>The destination name of a block-level element: its id, or the name of an <c>a name</c>.</summary>
    private static string? IdFor(IElement element) =>
        element.Id is { Length: > 0 } id ? id
        : element.LocalName == "a" && element.GetAttribute("name") is { Length: > 0 } name ? name
        : null;

    /// <summary>Marks the position of an inline element (its id, and the name of an <c>a name</c>) as a destination.</summary>
    private static void AddAnchors(IElement element, InlineCollector collector)
    {
        if (element.Id is { Length: > 0 } id)
        {
            collector.AddAnchor(id);
        }

        if (element.LocalName == "a" && element.GetAttribute("name") is { Length: > 0 } name && name != element.Id)
        {
            collector.AddAnchor(name);
        }
    }

    /// <summary>
    /// Links the blocks added from <paramref name="start"/> on as a whole when they are inside an inline link (an
    /// <c>a href</c> around a <c>div</c>): they move into a container without a box of its own, which is the link.
    /// </summary>
    private static void LinkBlocks(List<Block> children, int start, LinkTarget? link)
    {
        if (link is null || children.Count <= start)
        {
            return;
        }

        var linked = new ContainerBlock(null, children.Skip(start).ToList()) { Link = link };
        children.RemoveRange(start, children.Count - start);
        children.Add(linked);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemeRegex();
}
