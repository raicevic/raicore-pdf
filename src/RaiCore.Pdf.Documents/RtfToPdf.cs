using RaiCore.Pdf.Documents.Rtf;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// Converts Rich Text Format documents (<c>.rtf</c>, as Word, WordPad, LibreOffice and other writers save them) to PDF
/// with the layout engine: styles, numbered and bulleted lists, tables, sections with their page setup, headers and
/// footers with page numbers, pictures, hyperlinks, bookmarks and notes.
/// </summary>
/// <remarks>
/// <para>
/// Supported (RTF 1.9.1): text in the document's and its fonts' code pages (<c>\ansicpg</c>, <c>\fcharset</c>,
/// <c>\cpg</c>, double-byte code pages too) and Unicode (<c>\u</c> with <c>\uc</c>); the font, color and style tables
/// (paragraph and character styles with <c>\sbasedon</c> chains, headings by their names); fonts, sizes, bold, italic,
/// underline (single, double, dotted, dashed, wavy), strikethrough, colors, highlighting and character shading,
/// superscript, subscript, raised and lowered text, letter spacing, capitals, small capitals and hidden text (left
/// out); alignment, indents, spacing, line spacing (<c>\sl</c>, <c>\slmult</c>), keep together, keep with next, page
/// breaks before, widow control, borders and shading of paragraphs; tab stops (left, center, right and decimal, with
/// leaders); lists (<c>\listtable</c> and <c>\listoverridetable</c>, multi-level labels counted as Word counts them),
/// old-style paragraph numbering (<c>\pn</c>), and the writer's own label text (<c>\listtext</c>) where no list is
/// defined; line, page and column breaks; tables with their cell edges, merged cells (both directions), borders,
/// shading, padding, vertical alignment, row heights, header rows repeated on every page and nested tables
/// (<c>\itap</c>, <c>\nestcell</c>, <c>\nestrow</c>); sections with their page size, orientation, margins and start;
/// headers and footers for the first, left and right pages; page numbers (<c>\chpgn</c>, <c>PAGE</c>,
/// <c>NUMPAGES</c>, <c>SECTIONPAGES</c>), page references (<c>PAGEREF</c>) with the PDF's page numbers, hyperlinks
/// (<c>HYPERLINK</c> fields) and other fields as their last result; PNG and JPEG pictures, device-independent bitmaps
/// and EMF and WMF pictures (drawn as vector graphics), scaled to their goal size (the shape picture <c>\shppict</c> rather than its fallback); bookmarks;
/// footnotes and endnotes; revisions as if accepted (deleted text left out); the title and author of the document's
/// information (<c>\info</c>); comments as <see cref="DocumentConverterOptions.Comments"/> asks (listed at the end, or as PDF notes).
/// </para>
/// <para>
/// Not supported: PICT pictures, drawing objects (<c>\do</c>), shapes as drawings (their pictures and text
/// are laid out in the text), embedded objects (their result is shown), several text columns
/// (laid out as one), frames and positioned paragraphs, footnotes at the bottom of the page (they follow their
/// section), right-to-left and vertical text beyond what the layout engine does by itself. What is left out is
/// reported to <see cref="DocumentConverterOptions.Diagnostics"/>.
/// </para>
/// <para>
/// Documents are untrusted input: they are read in one pass without recursion, and bounded by the options'
/// <see cref="DocumentConverterOptions.Limits"/>: their tokens (control words, groups and runs of text) by
/// <see cref="ResourceLimits.MaxElements"/>, the nesting of groups and tables by <see cref="ResourceLimits.MaxNestingDepth"/>,
/// the cells of each table by <see cref="ResourceLimits.MaxTableCells"/>, each picture's data by
/// <see cref="ResourceLimits.MaxResourceBytes"/> (a larger one is left out) and its pixels by
/// <see cref="ResourceLimits.MaxImagePixels"/>, and pages and time by <see cref="ResourceLimits.MaxPages"/> and
/// <see cref="ResourceLimits.Timeout"/>.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = RtfToPdf.Convert("letter.rtf");
/// pdf.Save("letter.pdf");
/// </code>
/// </example>
public static class RtfToPdf
{
    /// <summary>Converts an RTF document to a PDF document.</summary>
    /// <param name="rtf">The document's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="DocumentFormatException">The data is not an RTF document (it does not start with <c>{\rtf</c>).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] rtf, RtfConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rtf);
        return RtfConverter.Convert(rtf, options ?? new RtfConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts an RTF document read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], RtfConverterOptions?)"/>
    public static PdfDocument Convert(Stream rtf, RtfConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rtf);
        using var buffer = new MemoryStream();
        rtf.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts an RTF file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="DocumentFormatException">The file is not an RTF document (it does not start with <c>{\rtf</c>).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, RtfConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts an RTF document to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], RtfConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] rtf, RtfConverterOptions? options = null) => Convert(rtf, options).ToArray();

    /// <summary>Converts an RTF document read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], RtfConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream rtf, RtfConverterOptions? options = null) => Convert(rtf, options).ToArray();

    /// <summary>Converts an RTF document to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="rtf">The document's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="DocumentFormatException">The data is not an RTF document (it does not start with <c>{\rtf</c>).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] rtf, RtfConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rtf);
        options ??= new RtfConverterOptions();
        return Task.Run(() => RtfConverter.Convert(rtf, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts an RTF document read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], RtfConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream rtf, RtfConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rtf);
        using var buffer = new MemoryStream();
        await rtf.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
