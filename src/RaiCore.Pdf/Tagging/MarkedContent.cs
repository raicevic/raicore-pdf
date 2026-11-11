namespace RaiCore.Pdf;

/// <summary>A piece of a structure element: a child element, a marked-content sequence or an annotation.</summary>
internal abstract record StructureKid;

/// <summary>A child structure element.</summary>
internal sealed record ElementKid(PdfStructureElement Element) : StructureKid;

/// <summary>A marked-content sequence with an MCID in a content stream (the page's, or a transparency group's).</summary>
internal sealed record ContentKid(PdfPage Page, MarkedContentStream Stream, int Mcid) : StructureKid;

/// <summary>A link annotation on a page (an object reference, <c>/OBJR</c>).</summary>
internal sealed record AnnotationKid(PdfPage Page, PdfLink Link) : StructureKid;

/// <summary>
/// The marked content of one content stream: the structure element each MCID belongs to (MCIDs count from 0 in each
/// stream). The parent tree maps the stream's <c>/StructParents</c> key to this list.
/// </summary>
internal sealed class MarkedContentStream
{
    private readonly List<PdfStructureElement> _owners = [];

    /// <summary>The element each MCID (the index) belongs to.</summary>
    public IReadOnlyList<PdfStructureElement> Owners => _owners;

    /// <summary>Allocates the next MCID for content of <paramref name="element"/>.</summary>
    public int Allocate(PdfStructureElement element)
    {
        _owners.Add(element);
        return _owners.Count - 1;
    }
}
