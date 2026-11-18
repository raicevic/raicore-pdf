namespace RaiCore.Pdf.Extraction;

/// <summary>How <see cref="TextExtractor"/> orders the text of a page.</summary>
public enum TextExtractionMode
{
    /// <summary>
    /// Reading order: lines from top to bottom, words separated by spaces, columns one after the other (a column is
    /// read to its end before the next one starts), paragraphs separated by an empty line, and right-to-left text
    /// (Arabic, Hebrew) in logical order. Tables are read row by row. The default.
    /// </summary>
    Reading,

    /// <summary>
    /// The physical layout, as <c>pdftotext -layout</c> does: each line across the whole page, with spaces that keep
    /// columns and table cells roughly where they are. Good for tables and forms read by eye or by fixed positions.
    /// </summary>
    Layout,

    /// <summary>
    /// The order the text is drawn in the file, with a line break where the drawing moves to another line. Fast, and
    /// often close to reading order, but it depends entirely on the producer.
    /// </summary>
    Raw,
}

/// <summary>Options for <see cref="TextExtractor"/>.</summary>
public sealed record TextExtractionOptions
{
    /// <summary>The default options: reading order, invisible text included.</summary>
    public static TextExtractionOptions Default { get; } = new();

    /// <summary>How the text of a page is ordered. Defaults to <see cref="TextExtractionMode.Reading"/>.</summary>
    public TextExtractionMode Mode { get; init; } = TextExtractionMode.Reading;

    /// <summary>
    /// Whether text drawn invisibly is included: the text layer of scanned pages that were run through OCR is drawn
    /// that way, under the page image. Defaults to <c>true</c>.
    /// </summary>
    public bool IncludeInvisibleText { get; init; } = true;

    /// <summary>
    /// What <see cref="TextExtractor.ExtractText"/> puts between the texts of two pages. Defaults to a form feed
    /// (<c>\f</c>), as <c>pdftotext</c> does.
    /// </summary>
    public string PageSeparator { get; init; } = "\f";
}
