using System.Globalization;

namespace RaiCore.Pdf;

/// <summary>
/// Tagging (ISO 32000-1, sections 14.6–14.8): in a <see cref="PdfDocument.Tagged"/> document, everything drawn is
/// marked content that belongs to a structure element, or an artifact.
/// </summary>
/// <remarks>
/// Marked-content sequences are written flat and lazily: a sequence opens before the first operator drawn under a tag
/// and stays open while consecutive drawing has the same tag, so nested tags (a link in a paragraph) become consecutive
/// sequences, and a tag with nothing drawn writes nothing. Drawing outside any tag becomes an artifact. Each content
/// stream (the page's, and each transparency group's form) numbers its marked content (MCIDs) on its own.
/// </remarks>
public sealed partial class PdfCanvas
{
    /// <summary>The tags begun and not yet ended: a structure element, or an artifact kind.</summary>
    private readonly List<Tag> _tags = [];

    /// <summary>The marked content of the stream being drawn: the page's, or the innermost open group's.</summary>
    private MarkedContentStream _marks = new();

    /// <summary>The tag whose marked-content sequence is open in the stream being drawn, if any.</summary>
    private Tag? _openTag;

    /// <summary>The marked content of the page's own content stream.</summary>
    internal MarkedContentStream PageMarks { get; private set; } = null!;

    /// <summary>
    /// The structure element that content drawn now belongs to: the innermost tag begun with
    /// <see cref="BeginTag(PdfStructureElement)"/>, or <c>null</c> inside an artifact or outside any tag.
    /// </summary>
    public PdfStructureElement? CurrentElement => CurrentTag.Element;

    /// <summary>The document the canvas draws in; <c>null</c> for the drawing surface of an SVG image.</summary>
    internal PdfDocument? Document => _page.Document;

    /// <summary>
    /// The tag content drawn now gets: the outermost artifact begun (everything inside an artifact is an artifact),
    /// else the innermost element; outside any tag, an artifact.
    /// </summary>
    private Tag CurrentTag
    {
        get
        {
            foreach (var tag in _tags)
            {
                if (tag.Element is null)
                {
                    return tag;
                }
            }

            return _tags.Count > 0 ? _tags[^1] : Tag.Untagged;
        }
    }

    /// <summary>
    /// Starts content that belongs to <paramref name="element"/>: everything drawn until the matching
    /// <see cref="EndTag"/> becomes the element's marked content. Tags nest; the innermost one wins. Links added
    /// meanwhile belong to the element if it is a <see cref="PdfStructureTypes.Link"/>, otherwise to a new link element
    /// inside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not <see cref="PdfDocument.Tagged"/>.</exception>
    /// <exception cref="ArgumentException">The element belongs to another document.</exception>
    public PdfCanvas BeginTag(PdfStructureElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Tagging)
        {
            throw new InvalidOperationException("Set PdfDocument.Tagged before drawing tagged content.");
        }

        if (!ReferenceEquals(element.Document, _page.Document))
        {
            throw new ArgumentException("The structure element belongs to another document.", nameof(element));
        }

        _tags.Add(new Tag(element, null));
        return this;
    }

    /// <summary>
    /// Starts an artifact: content that is not part of the document's logical content (page headers and footers,
    /// backgrounds, decorative rules), which screen readers skip. Ends with <see cref="EndTag"/>. In an untagged
    /// document this does nothing.
    /// </summary>
    public PdfCanvas BeginArtifact(PdfArtifactKind kind = PdfArtifactKind.Layout)
    {
        _tags.Add(new Tag(null, kind));
        return this;
    }

    /// <summary>Begins a tag that keeps the current one (so that a matching <see cref="EndTag"/> balances it).</summary>
    internal PdfCanvas BeginInherited()
    {
        _tags.Add(CurrentTag);
        return this;
    }

    /// <summary>Ends the innermost tag begun with <see cref="BeginTag(PdfStructureElement)"/> or <see cref="BeginArtifact"/>.</summary>
    /// <exception cref="InvalidOperationException">No tag is open.</exception>
    public PdfCanvas EndTag()
    {
        if (_tags.Count == 0)
        {
            throw new InvalidOperationException("EndTag was called without a matching BeginTag or BeginArtifact.");
        }

        _tags.RemoveAt(_tags.Count - 1);
        return this;
    }

    /// <summary>Whether the document is tagged, so that drawing is marked.</summary>
    private bool Tagging => _page.Document is { Tagged: true };

    /// <summary>
    /// Opens the marked-content sequence for the current tag before an operator is drawn, closing the one open before
    /// if it differs. Outside any tag, the content is an artifact.
    /// </summary>
    private void MarkContent()
    {
        var tag = CurrentTag;
        if (_openTag == tag)
        {
            return;
        }

        CloseMarkedContent();
        if (tag.Element is { } element)
        {
            var mcid = _marks.Allocate(element);
            element.AddContent(new ContentKid(_page, _marks, mcid));
            _ops.Append('/').Append(element.Type).Append(" <</MCID ").Append(mcid.ToString(CultureInfo.InvariantCulture)).Append(">> BDC\n");
        }
        else if (tag.Artifact is { } kind && kind != PdfArtifactKind.Layout)
        {
            _ops.Append("/Artifact <</Type /").Append(kind).Append(">> BDC\n");
        }
        else
        {
            _ops.Append("/Artifact BMC\n");
        }

        _openTag = tag;
    }

    /// <summary>Closes the open marked-content sequence, if any (at the end of a stream, or before a group).</summary>
    private void CloseMarkedContent()
    {
        if (_openTag is not null)
        {
            _ops.Append("EMC\n");
            _openTag = null;
        }
    }

    /// <summary>A tag: a structure element, or (with a <c>null</c> element) an artifact.</summary>
    private sealed record Tag(PdfStructureElement? Element, PdfArtifactKind? Artifact)
    {
        /// <summary>Content drawn outside any tag: an artifact.</summary>
        public static readonly Tag Untagged = new(null, PdfArtifactKind.Layout);
    }
}
