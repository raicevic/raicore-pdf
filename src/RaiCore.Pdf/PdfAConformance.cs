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

    /// <summary>
    /// PDF/A-2a (ISO 19005-2, level A, "accessible"): PDF/A-2b plus the document's logical structure. The document is
    /// <see cref="PdfDocument.Tagged"/>, and all of its text maps to Unicode.
    /// </summary>
    PdfA2A,

    /// <summary>PDF/A-3a (ISO 19005-3, level A): PDF/A-3b plus the logical structure and Unicode text of PDF/A-2a.</summary>
    PdfA3A,

    /// <summary>
    /// PDF/A-1b (ISO 19005-1, level B): the visual appearance is preserved, for archives that require the first part.
    /// Builds on PDF 1.4, so the document must not use transparency: translucent colors, opacity
    /// (<see cref="PdfCanvas.BeginGroup"/>), gradients with translucent colors, and images with semi-transparent
    /// pixels are rejected when it is saved. Files cannot be attached.
    /// </summary>
    PdfA1B,

    /// <summary>
    /// PDF/A-1a (ISO 19005-1, level A): PDF/A-1b plus the logical structure and Unicode text of PDF/A-2a, with the
    /// structure types of PDF 1.4 (table header, body and footer groups are left out: their rows belong to the table).
    /// </summary>
    PdfA1A,
}

/// <summary>What a <see cref="PdfAConformance"/> level stands for.</summary>
internal static class PdfAConformanceLevels
{
    /// <summary>The part of ISO 19005 (1, 2 or 3), or 0 for <see cref="PdfAConformance.None"/>.</summary>
    public static int Part(this PdfAConformance level) => level switch
    {
        PdfAConformance.PdfA1B or PdfAConformance.PdfA1A => 1,
        PdfAConformance.PdfA2B or PdfAConformance.PdfA2A => 2,
        PdfAConformance.PdfA3B or PdfAConformance.PdfA3A => 3,
        _ => 0,
    };

    /// <summary>Whether the level is A (accessible: tagged, with Unicode text), not B.</summary>
    public static bool IsLevelA(this PdfAConformance level) => level is PdfAConformance.PdfA1A or PdfAConformance.PdfA2A or PdfAConformance.PdfA3A;
}
