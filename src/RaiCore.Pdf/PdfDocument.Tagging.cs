namespace RaiCore.Pdf;

// Tagged PDF and PDF/UA: the logical structure that screen readers and reflow use.
public sealed partial class PdfDocument
{
    private bool _tagged;
    private PdfStructureElement? _structureRoot;

    /// <summary>
    /// The structure elements of layout objects (blocks, table cells, tagged text runs) rendered into the document, by
    /// reference; <c>null</c> marks an artifact. Filled by the layout engine before it draws.
    /// </summary>
    internal Dictionary<object, PdfStructureElement?> LayoutStructure { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether the document is tagged (ISO 32000-1, section 14.8): content drawn between
    /// <see cref="PdfCanvas.BeginTag(PdfStructureElement)"/> and <see cref="PdfCanvas.EndTag"/> belongs to the structure
    /// elements under <see cref="StructureRoot"/>, everything else is written as artifacts, and links are tagged. Set it
    /// before drawing. <see cref="PdfUA"/> implies it.
    /// </summary>
    public bool Tagged
    {
        get => _tagged || PdfUA;
        set => _tagged = value;
    }

    /// <summary>
    /// Whether to produce PDF/UA-1 (ISO 14289-1, universal accessibility): a <see cref="Tagged"/> document that declares
    /// its conformance in its XMP metadata and has its title shown in the viewer's title bar. Saving then requires a
    /// <see cref="PdfDocumentInfo.Title"/>, a <see cref="Language"/> and embedded fonts (not the standard 14 fonts), and
    /// the content's tags must follow PDF/UA (e.g. figures need alternate text).
    /// </summary>
    public bool PdfUA { get; set; }

    /// <summary>
    /// The root of the logical structure: the <see cref="PdfStructureTypes.Document"/> element that the document's
    /// headings, paragraphs, tables and figures are added to.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not <see cref="Tagged"/>.</exception>
    public PdfStructureElement StructureRoot =>
        Tagged
            ? _structureRoot ??= new PdfStructureElement(PdfStructureTypes.Document, null, this)
            : throw new InvalidOperationException("Only a tagged document has a structure tree: set PdfDocument.Tagged first.");

    /// <summary>Checks what PDF/UA requires of the document as a whole before it is written.</summary>
    private void ValidatePdfUA(IEnumerable<Fonts.PdfFont> fonts)
    {
        if (!PdfUA)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Info.Title))
        {
            throw new InvalidOperationException("PDF/UA requires a document title (PdfDocument.Info.Title).");
        }

        if (string.IsNullOrWhiteSpace(Language))
        {
            throw new InvalidOperationException("PDF/UA requires the document's language (PdfDocument.Language).");
        }

        if (!IncludeXmpMetadata)
        {
            throw new InvalidOperationException("PDF/UA requires XMP metadata (PdfDocument.IncludeXmpMetadata).");
        }

        // A standard font is fine when a substitute is embedded in its place (only Helvetica, Times and Courier can be).
        if (fonts.FirstOrDefault(font => font is Fonts.StandardPdfFont standard
                && !(Fonts.StandardFontInfo.UsesWinAnsi(standard.Font) && StandardFontSubstitute?.Invoke(standard.Font) is not null)) is { } unembedded)
        {
            throw new InvalidOperationException(
                $"PDF/UA requires embedded fonts, but the standard font {unembedded} is not embedded. Load a font file with PdfFont.Load, "
                + "or set PdfDocument.StandardFontSubstitute (EmbedStandardFonts() from RaiCore.Pdf.Fonts) for Helvetica, Times and Courier.");
        }
    }
}
