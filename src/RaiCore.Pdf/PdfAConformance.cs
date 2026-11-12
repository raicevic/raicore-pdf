namespace RaiCore.Pdf;

/// <summary>
/// A PDF/A (ISO 19005) conformance level for archiving: the document is self-contained (all fonts embedded, colors tied
/// to an sRGB output intent, XMP metadata that identifies it as PDF/A) and uses no features that keep it from rendering
/// the same way in the future (encryption, JavaScript, external content). See <see cref="PdfDocument.PdfAConformance"/>.
/// </summary>
public enum PdfAConformance
{
    /// <summary>No PDF/A conformance (the default).</summary>
    None,

    /// <summary>
    /// PDF/A-2b (ISO 19005-2, level B, "basic"): the visual appearance is preserved. Builds on PDF 1.7 and allows
    /// transparency, JPEG 2000 and OpenType fonts; attached files must themselves be PDF/A.
    /// </summary>
    PdfA2B,

    /// <summary>
    /// PDF/A-3b (ISO 19005-3, level B): like PDF/A-2b, but any file may be attached (associated files), such as the
    /// invoice XML of a ZUGFeRD or Factur-X e-invoice.
    /// </summary>
    PdfA3B,
}
