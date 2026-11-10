using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// An entry in the document outline (the bookmarks a PDF viewer shows in its navigation panel): a title, the place it
/// jumps to, and nested entries.
/// </summary>
/// <example>
/// <code>
/// var chapter = new PdfOutlineItem("Chapter 1", page, top: page.Height - 72);
/// chapter.Children.Add(new PdfOutlineItem("Section 1.1", page, top: 400));
/// doc.Outline.Add(chapter);
/// </code>
/// </example>
public sealed class PdfOutlineItem
{
    private string _title;
    private PdfPage _page;

    /// <summary>Creates an entry that jumps to <paramref name="page"/>, scrolled so that <paramref name="top"/> is at the top of the window.</summary>
    /// <param name="title">The title shown in the viewer; any Unicode text.</param>
    /// <param name="page">The target page. It must belong to the document the outline is saved with.</param>
    /// <param name="top">The PDF y coordinate (from the bottom of the page) to show at the top of the window, or <c>null</c> to keep the viewer's position.</param>
    public PdfOutlineItem(string title, PdfPage page, double? top = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(page);
        _title = title;
        _page = page;
        Top = top;
    }

    /// <summary>The title shown in the viewer; any Unicode text.</summary>
    public string Title
    {
        get => _title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _title = value;
        }
    }

    /// <summary>The target page. It must belong to the document the outline is saved with.</summary>
    public PdfPage Page
    {
        get => _page;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _page = value;
        }
    }

    /// <summary>The PDF y coordinate (from the bottom of the page) to show at the top of the window, or <c>null</c> to keep the viewer's position.</summary>
    public double? Top { get; set; }

    /// <summary>The PDF x coordinate to show at the left of the window, or <c>null</c> (the default) to keep the viewer's position.</summary>
    public double? Left { get; set; }

    /// <summary>Whether the entry starts expanded, showing its children. Defaults to <c>true</c>.</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>The nested entries, in order.</summary>
    public IList<PdfOutlineItem> Children { get; } = new List<PdfOutlineItem>();
}

/// <summary>How a PDF viewer opens the document (the catalog's <c>/PageMode</c>).</summary>
public enum PdfPageMode
{
    /// <summary>Shows the outline panel when the document has an outline, otherwise the viewer's default.</summary>
    Automatic,

    /// <summary>The viewer's default: no panel is requested.</summary>
    UseNone,

    /// <summary>Opens the outline (bookmarks) panel.</summary>
    UseOutlines,
}

/// <summary>Writes the outline tree (ISO 32000-2, section 12.3.3).</summary>
internal static class PdfOutlineWriter
{
    /// <summary>Writes the outline dictionary and its items; returns a reference to the outline dictionary.</summary>
    public static PdfReference Write(PdfFileWriter writer, IList<PdfOutlineItem> items, IReadOnlyDictionary<PdfPage, PdfReference> pages)
    {
        var root = new PdfDictionary { ["Type"] = new PdfName("Outlines") };
        var rootRef = writer.Add(root);
        var visible = WriteItems(writer, root, rootRef, items, pages, new HashSet<PdfOutlineItem>(ReferenceEqualityComparer.Instance));
        if (visible > 0)
        {
            root["Count"] = new PdfInteger(visible); // The number of items visible when the document opens.
        }

        return rootRef;
    }

    /// <summary>
    /// Writes <paramref name="items"/> as the children of <paramref name="parent"/>. Returns how many items show below
    /// the parent when it is open: each child, plus the visible descendants of the open ones.
    /// </summary>
    private static int WriteItems(
        PdfFileWriter writer, PdfDictionary parent, PdfReference parentRef, IList<PdfOutlineItem> items,
        IReadOnlyDictionary<PdfPage, PdfReference> pages, HashSet<PdfOutlineItem> seen)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var dictionaries = new PdfDictionary[items.Count];
        var refs = new PdfReference[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            dictionaries[i] = new PdfDictionary();
            refs[i] = writer.Add(dictionaries[i]);
        }

        parent["First"] = refs[0];
        parent["Last"] = refs[^1];

        var visible = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i] ?? throw new InvalidOperationException("The outline contains a null item.");
            if (!seen.Add(item))
            {
                throw new InvalidOperationException($"The outline item \"{item.Title}\" appears more than once in the outline.");
            }

            if (!pages.TryGetValue(item.Page, out var pageRef))
            {
                throw new InvalidOperationException($"The outline item \"{item.Title}\" points to a page that is not in this document.");
            }

            var dictionary = dictionaries[i];
            dictionary["Title"] = PdfString.FromText(item.Title);
            dictionary["Parent"] = parentRef;
            if (i > 0)
            {
                dictionary["Prev"] = refs[i - 1];
            }

            if (i < items.Count - 1)
            {
                dictionary["Next"] = refs[i + 1];
            }

            dictionary["Dest"] = new PdfArray(pageRef, new PdfName("XYZ"), Number(item.Left), Number(item.Top), PdfNull.Instance);

            // Count: the descendants shown when the item is open; negative when it is closed (how many it would show).
            var descendants = WriteItems(writer, dictionary, refs[i], item.Children, pages, seen);
            if (descendants > 0)
            {
                dictionary["Count"] = new PdfInteger(item.IsOpen ? descendants : -descendants);
            }

            visible += 1 + (item.IsOpen ? descendants : 0);
        }

        return visible;
    }

    /// <summary>A coordinate, or <c>null</c> to keep the viewer's current one.</summary>
    private static PdfObject Number(double? value) => value is { } v ? new PdfReal(v) : PdfNull.Instance;
}
