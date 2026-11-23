using RaiCore.Pdf.Documents.Doc;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// Converts Word 97-2003 documents (<c>.doc</c>, the binary format of Word 97 to Word 2003, which later versions still
/// save) to PDF with the layout engine: styles, numbered and bulleted lists, tables, sections with their page setup,
/// headers and footers with page numbers, pictures, hyperlinks, bookmarks and notes.
/// </summary>
/// <remarks>
/// <para>
/// Supported ([MS-DOC]): the text of the piece table (8-bit code page 1252 and UTF-16 pieces, fast-saved documents);
/// the style sheet (paragraph and character styles with their bases) and font table; fonts, sizes, bold, italic,
/// underline (single, double, dotted, dashed, wavy), strikethrough, colors (<c>ico</c> and RGB), highlighting and
/// character shading, superscript, subscript, raised and lowered text, letter spacing, capitals, small capitals and
/// hidden text (left out); alignment, indents, spacing, line spacing, keep together, keep with next, page breaks
/// before, widow control, borders and shading of paragraphs; tab stops (left, center, right and decimal, with
/// leaders); lists (<c>PlfLst</c> and <c>PlfLfo</c> with their overrides, multi-level labels counted as Word counts
/// them); line, page and column breaks; tables with their cell edges, merged cells (both directions), borders,
/// shading, padding, vertical alignment, row heights, header rows repeated on every page and nested tables; sections
/// with their page size, orientation, margins and start; headers and footers for the first, odd and even pages;
/// page numbers (<c>PAGE</c>, <c>NUMPAGES</c>, <c>SECTIONPAGES</c>), page references (<c>PAGEREF</c>) with the PDF's
/// page numbers, hyperlinks (<c>HYPERLINK</c> fields) and other fields as their last result; inline and floating PNG,
/// JPEG, TIFF and bitmap pictures at their size; the text of text boxes (laid out after their paragraph); bookmarks;
/// footnotes and endnotes; revisions as if accepted (deleted text left out); the title and author of the summary
/// information.
/// </para>
/// <para>
/// Not supported: encrypted (password-protected) documents and documents in the Word 6 and Word 95 formats (both
/// throw <see cref="NotSupportedException"/>), EMF, WMF and PICT pictures, drawing shapes other than pictures and text
/// boxes, embedded objects (their preview picture is shown when it can be drawn), comments, several text columns
/// (laid out as one), frames and positioned paragraphs, footnotes at the bottom of the page (they follow their
/// section), right-to-left and vertical text beyond what the layout engine does by itself. What is left out is
/// reported to <see cref="DocumentConverterOptions.Diagnostics"/>.
/// </para>
/// <para>
/// Documents are untrusted input: every offset and size in the file is checked against the stream it points into,
/// and reading is bounded by the options' <see cref="DocumentConverterOptions.Limits"/>: the compound file's entries
/// by <see cref="ResourceLimits.MaxElements"/> and its streams by <see cref="ResourceLimits.MaxDecompressedBytes"/>,
/// paragraphs and runs by <see cref="ResourceLimits.MaxElements"/>, the nesting of tables by
/// <see cref="ResourceLimits.MaxNestingDepth"/>, the cells of each table by <see cref="ResourceLimits.MaxTableCells"/>,
/// each picture by <see cref="ResourceLimits.MaxResourceBytes"/> (a larger one is left out) and its pixels by
/// <see cref="ResourceLimits.MaxImagePixels"/>, and pages and time by <see cref="ResourceLimits.MaxPages"/> and
/// <see cref="ResourceLimits.Timeout"/>.
/// </para>
/// <para>A Word document (<c>.docx</c>, a ZIP package) given to it is converted as <see cref="DocxToPdf"/> converts it.</para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = DocToPdf.Convert("letter.doc");
/// pdf.Save("letter.pdf");
/// </code>
/// </example>
public static class DocToPdf
{
    /// <summary>Converts a Word 97-2003 document to a PDF document.</summary>
    /// <param name="doc">The document's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="PdfFormatException">The data is not a Word 97-2003 document (a compound file with a WordDocument stream) or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted (password-protected) or in the Word 6 or Word 95 format.</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] doc, DocConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return DocConverter.Convert(doc, options ?? new DocConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a Word 97-2003 document read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], DocConverterOptions?)"/>
    public static PdfDocument Convert(Stream doc, DocConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        using var buffer = new MemoryStream();
        doc.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a Word 97-2003 file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="PdfFormatException">The file is not a Word 97-2003 document (a compound file with a WordDocument stream) or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted (password-protected) or in the Word 6 or Word 95 format.</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, DocConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a Word 97-2003 document to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], DocConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] doc, DocConverterOptions? options = null) => Convert(doc, options).ToArray();

    /// <summary>Converts a Word 97-2003 document read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], DocConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream doc, DocConverterOptions? options = null) => Convert(doc, options).ToArray();

    /// <summary>Converts a Word 97-2003 document to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="doc">The document's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="PdfFormatException">The data is not a Word 97-2003 document (a compound file with a WordDocument stream) or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted (password-protected) or in the Word 6 or Word 95 format.</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] doc, DocConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new DocConverterOptions();
        return Task.Run(() => DocConverter.Convert(doc, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts a Word 97-2003 document read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], DocConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream doc, DocConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        using var buffer = new MemoryStream();
        await doc.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
