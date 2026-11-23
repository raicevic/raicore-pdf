using RaiCore.Pdf.Documents.Docx;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// Converts Word documents (<c>.docx</c>, also <c>.docm</c>, <c>.dotx</c> and strict Office Open XML) to PDF with the
/// layout engine: styles, numbered and bulleted lists, tables, sections with their page setup, headers and footers
/// with page numbers, images, hyperlinks, bookmarks and notes.
/// </summary>
/// <remarks>
/// <para>
/// Supported: paragraph and character styles (with their <c>basedOn</c> chains, document defaults and theme fonts);
/// alignment, indents, spacing, line spacing, borders and shading of paragraphs; keep with next, keep lines together,
/// page breaks before and widow control; fonts, sizes, bold, italic, underline, strikethrough, colors, highlighting,
/// superscript, subscript, capitals and hidden text; multi-level numbering (<c>%1.%2.</c> labels, decimal, letters,
/// roman numerals, ordinals, bullets, restarts); tab stops (left, center, right and decimal, with leaders; measured on
/// the line, so they are approximate in wrapped lines); line, page and column breaks; tables with their grid, merged
/// cells (both directions), borders, shading, cell padding and vertical alignment, table styles (with their first row,
/// last row, first column and banded rows), rows repeated on every page and rows that may not break; sections with
/// their page size, orientation, margins and start (new page, continuous, even or odd page); headers and footers for
/// the first, even and other pages of each section, with page numbers (<c>PAGE</c>, <c>NUMPAGES</c>,
/// <c>SECTIONPAGES</c>, restarts and formats); PNG, JPEG, GIF, BMP, TIFF and WebP images, inline or anchored (anchored
/// images with text wrapping float at the left or right margin, others stand on a line of their own before their
/// paragraph); hyperlinks to web addresses and to bookmarks; page references (<c>PAGEREF</c>, as in tables of
/// contents) with the PDF's page numbers; other fields as their last result; footnotes (at the end of their section)
/// and endnotes (at the end of the document), linked from their marks; tracked changes as if accepted; content
/// controls; equations as plain text; charts as vector graphics (newer chart types as their stored picture).
/// </para>
/// <para>
/// Not supported: text boxes and shapes as floating frames (their text is laid out after their paragraph), surface
/// charts, SmartArt, embedded objects (their preview image is shown), comments, several text columns (laid out as one), footnotes at the
/// bottom of the page, line numbers, page borders, watermarks, right-to-left and vertical text beyond what the layout
/// engine does by itself. What is left out is reported to <see cref="DocumentConverterOptions.Diagnostics"/>.
/// Password-protected (encrypted) documents throw <see cref="NotSupportedException"/>; a Word 97-2003 (<c>.doc</c>)
/// document is converted as <see cref="DocToPdf"/> converts it; macros in <c>.docm</c> files are ignored.
/// </para>
/// <para>
/// Documents are untrusted input: their parts are read with no DTDs and nothing external resolved, and bounded by the
/// options' <see cref="DocumentConverterOptions.Limits"/>: the decompressed size of all parts together by
/// <see cref="ResourceLimits.MaxDecompressedBytes"/>, their XML elements by <see cref="ResourceLimits.MaxElements"/>, the
/// nesting of elements and tables by <see cref="ResourceLimits.MaxNestingDepth"/>, images, pages and time by
/// <see cref="ResourceLimits.MaxImagePixels"/>, <see cref="ResourceLimits.MaxPages"/> and <see cref="ResourceLimits.Timeout"/>.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = DocxToPdf.Convert("letter.docx");
/// pdf.Save("letter.pdf");
/// </code>
/// </example>
public static class DocxToPdf
{
    /// <summary>Converts a Word document to a PDF document.</summary>
    /// <param name="docx">The document's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="DocumentFormatException">The data is not a valid Word document (a ZIP package with a main document part).</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted document (or an unsupported Word 6/95 document).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] docx, DocxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(docx);
        return DocxConverter.Convert(docx, options ?? new DocxConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a Word document read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], DocxConverterOptions?)"/>
    public static PdfDocument Convert(Stream docx, DocxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(docx);
        using var buffer = new MemoryStream();
        docx.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a Word document file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="DocumentFormatException">The file is not a valid Word document (a ZIP package with a main document part).</exception>
    /// <exception cref="NotSupportedException">The file is an encrypted document (or an unsupported Word 6/95 document).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, DocxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a Word document to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], DocxConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] docx, DocxConverterOptions? options = null) => Convert(docx, options).ToArray();

    /// <summary>Converts a Word document read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], DocxConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream docx, DocxConverterOptions? options = null) => Convert(docx, options).ToArray();

    /// <summary>Converts a Word document to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="docx">The document's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="DocumentFormatException">The data is not a valid Word document (a ZIP package with a main document part).</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted document (or an unsupported Word 6/95 document).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] docx, DocxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docx);
        options ??= new DocxConverterOptions();
        return Task.Run(() => DocxConverter.Convert(docx, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts a Word document read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], DocxConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream docx, DocxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docx);
        using var buffer = new MemoryStream();
        await docx.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
