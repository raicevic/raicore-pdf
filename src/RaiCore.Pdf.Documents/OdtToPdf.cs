using RaiCore.Pdf.Documents.Odt;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// Converts OpenDocument text documents (<c>.odt</c>, also <c>.ott</c> templates and flat <c>.fodt</c> XML documents;
/// ODF 1.0 to 1.3) to PDF with the layout engine: styles, numbered and bulleted lists, tables, page styles with their
/// headers and footers and page numbers, images, links, bookmarks and notes.
/// </summary>
/// <remarks>
/// <para>
/// Supported: common, automatic and default styles with their parent chains and font face declarations; margins,
/// indents, alignment, line spacing (proportional, fixed, at least and leading), borders, background, keep with next,
/// keep together, page breaks before and after, widows and orphans, and tab stops (left, center, right and decimal,
/// with leaders) of paragraphs; fonts, sizes, bold, italic, underline, strikethrough, colors, backgrounds,
/// superscript, subscript, small capitals, letter case changes, letter spacing and hidden text; headings with their
/// outline levels and outline numbering; lists with their list styles (numbers with prefix, suffix, start value and the
/// higher levels they display, letters, roman numerals, bullets), nesting, continued numbering and indents (in both
/// positioning modes); tables with their column widths, header rows repeated on every page, cells spanning columns and
/// rows, borders, padding, backgrounds and vertical alignment, and nested tables; sections and indexes (tables of
/// contents) as their content; master pages with their page layout (size, orientation, margins, header and footer
/// height), headers and footers for left, right and first pages, switched by paragraph and table styles, and page
/// number restarts and formats; PNG, JPEG, GIF, BMP, TIFF, WebP and SVG images in the package (or embedded as base64 in
/// flat documents), in the line or anchored (anchored images with text wrapping float at the left or right margin,
/// others stand on a line of their own before their paragraph); the replacement images of charts and embedded objects;
/// links to web addresses and to bookmarks; page numbers, page counts and page references computed from the PDF's
/// pages, other fields as their text; footnotes (at the end of their section) and endnotes (at the end of the
/// document), linked from their citations; tracked changes as if accepted.
/// </para>
/// <para>
/// Not supported: text boxes and drawing shapes as floating frames (their text is laid out after their paragraph),
/// charts and embedded objects beyond their replacement images, EMF, WMF and StarView images, comments, forms,
/// several text columns (laid out as one), footnotes at the bottom of the page, line numbers, page borders and
/// backgrounds, right-to-left and vertical text beyond what the layout engine does by itself. What is left out is
/// reported to <see cref="DocumentConverterOptions.Diagnostics"/>. Password-protected (encrypted) documents throw
/// <see cref="NotSupportedException"/>; scripts (macros) are ignored.
/// </para>
/// <para>
/// Documents are untrusted input: their parts are read with no DTDs and nothing external resolved (linked images are
/// left out), and bounded by the options' <see cref="DocumentConverterOptions.Limits"/>: the decompressed size of all
/// parts together (and of embedded data) by <see cref="ResourceLimits.MaxDecompressedBytes"/>, their XML elements, and
/// the table rows and cells that repeat others, by <see cref="ResourceLimits.MaxElements"/>, the cells of a table by
/// <see cref="ResourceLimits.MaxTableCells"/>, the nesting of elements and tables by
/// <see cref="ResourceLimits.MaxNestingDepth"/>, images, pages and time by <see cref="ResourceLimits.MaxImagePixels"/>,
/// <see cref="ResourceLimits.MaxPages"/> and <see cref="ResourceLimits.Timeout"/>.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = OdtToPdf.Convert("letter.odt");
/// pdf.Save("letter.pdf");
/// </code>
/// </example>
public static class OdtToPdf
{
    /// <summary>Converts an OpenDocument text document to a PDF document.</summary>
    /// <param name="odt">The document's bytes (a ZIP package, or a flat XML document).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="DocumentFormatException">The data is not a valid OpenDocument text document.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] odt, OdtConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(odt);
        return OdtConverter.Convert(odt, options ?? new OdtConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts an OpenDocument text document read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], OdtConverterOptions?)"/>
    public static PdfDocument Convert(Stream odt, OdtConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(odt);
        using var buffer = new MemoryStream();
        odt.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts an OpenDocument text document file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="DocumentFormatException">The file is not a valid OpenDocument text document.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, OdtConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts an OpenDocument text document to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], OdtConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] odt, OdtConverterOptions? options = null) => Convert(odt, options).ToArray();

    /// <summary>Converts an OpenDocument text document read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], OdtConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream odt, OdtConverterOptions? options = null) => Convert(odt, options).ToArray();

    /// <summary>Converts an OpenDocument text document to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="odt">The document's bytes (a ZIP package, or a flat XML document).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="DocumentFormatException">The data is not a valid OpenDocument text document.</exception>
    /// <exception cref="NotSupportedException">The document is encrypted.</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] odt, OdtConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(odt);
        options ??= new OdtConverterOptions();
        return Task.Run(() => OdtConverter.Convert(odt, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts an OpenDocument text document read asynchronously from a stream (to its end; the stream is left open)
    /// to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], OdtConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream odt, OdtConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(odt);
        using var buffer = new MemoryStream();
        await odt.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
