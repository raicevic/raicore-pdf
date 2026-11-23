namespace RaiCore.Pdf.Documents;

/// <summary>
/// Something a document converter could not show as the document does (reported through
/// <see cref="DocumentConverterOptions.Diagnostics"/>).
/// </summary>
/// <param name="Kind">What kind of issue it is.</param>
/// <param name="Message">A description, naming the content (for example the image's part name).</param>
public sealed record DocumentDiagnostic(DocumentDiagnosticKind Kind, string Message);

/// <summary>The kinds of <see cref="DocumentDiagnostic"/>.</summary>
public enum DocumentDiagnosticKind
{
    /// <summary>
    /// Content the converter does not support, left out or shown simplified: charts, SmartArt, embedded objects, text
    /// boxes (their text is laid out after their paragraph), several text columns (laid out as one), and the like.
    /// </summary>
    UnsupportedContent,

    /// <summary>
    /// An image that cannot be drawn (a format such as PICT, or damaged data); it is left out. A damaged WMF or EMF
    /// picture is drawn up to the damage and reported as this too.
    /// </summary>
    ImageNotLoaded,

    /// <summary>Content that does not fit where it is laid out: a line or table wider than its box, content taller than a page.</summary>
    Layout,
}
