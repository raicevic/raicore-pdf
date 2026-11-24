namespace RaiCore.Pdf.Xfa;

/// <summary>
/// Renders dynamic XFA forms (XML Forms Architecture, as Adobe LiveCycle and AEM Forms Designer make them) to ordinary
/// PDF pages: the form's data is merged into its template, the subforms are laid out and paginated into the template's
/// page areas, and the field values are drawn as text, so the result is a flattened PDF without XFA that every viewer
/// shows. The input is a PDF file with an XFA form (dynamic or static), or an XDP file.
/// </summary>
/// <remarks>
/// <para>
/// A dynamic XFA form's PDF pages are often only a placeholder ("Please wait... If this message is not eventually
/// replaced..."), since the viewer lays out the pages itself. To keep the PDF's own pages and AcroForm instead, remove
/// the XFA with <c>PdfEditor.RemoveXfa</c> (RaiCore.Pdf.Editing).
/// </para>
/// <para>
/// Supported: the template's subforms with positioned and flowed layout (<c>tb</c>, <c>lr-tb</c>, <c>rl-tb</c>),
/// tables and rows with column widths and spans, repeated subforms (<c>occur</c>), the data merge with normal
/// (match-once), global, data-reference and no bindings, page sets with ordered and simplex/duplex page areas and their
/// content areas, breaks (<c>breakBefore</c>, <c>breakAfter</c> and the older <c>break</c>), overflow leaders and
/// trailers, keep rules, growing text split between its lines across pages, draws and fields with their captions, margins, borders, fills, fonts and paragraph formats,
/// plain and rich text (an XHTML subset), images, lines, rectangles and arcs, check boxes and radio buttons, choice
/// lists, comb fields, picture clauses (numbers, dates, times, text) with the form's locales, rotation, and presence
/// (objects that are not printed are left out, as in a printout). Prototypes (<c>use</c>, <c>usehref</c> by id) are
/// resolved.
/// </para>
/// <para>
/// Not supported: scripts (FormCalc and JavaScript) are never run, so values they would calculate and objects they
/// would show, hide or add are missing; barcodes are drawn as their text; the output has no AcroForm fields. What is
/// left out is reported to <see cref="XfaConverterOptions.Diagnostics"/>.
/// </para>
/// <para>
/// Forms are untrusted input: their XML is read with no DTDs and nothing external resolved (linked images are not
/// loaded), and the work is bounded by the options' <see cref="XfaConverterOptions.Limits"/>.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = XfaToPdf.Convert("form.pdf");
/// pdf.Save("form-flattened.pdf");
/// </code>
/// </example>
public static class XfaToPdf
{
    /// <summary>Renders the XFA form of a PDF file (or an XDP file) to a PDF document.</summary>
    /// <param name="data">The PDF file's (or XDP file's) bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="XfaFormatException">The file has no XFA form, or its XFA is malformed.</exception>
    /// <exception cref="PdfFormatException">The data is not a PDF file that can be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF file is encrypted and the password is wrong or missing.</exception>
    /// <exception cref="ResourceLimitExceededException">The form exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] data, XfaConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return XfaConverter.Convert(data, options ?? new XfaConverterOptions(), CancellationToken.None);
    }

    /// <summary>Renders the XFA form of a PDF file (or an XDP file) read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], XfaConverterOptions?)"/>
    public static PdfDocument Convert(Stream data, XfaConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var buffer = new MemoryStream();
        data.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Renders the XFA form of a PDF file (or an XDP file) to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="XfaFormatException">The file has no XFA form, or its XFA is malformed.</exception>
    /// <exception cref="PdfFormatException">The file is not a PDF file that can be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF file is encrypted and the password is wrong or missing.</exception>
    /// <exception cref="ResourceLimitExceededException">The form exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, XfaConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Renders the XFA form of a PDF file (or an XDP file) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], XfaConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] data, XfaConverterOptions? options = null) => Convert(data, options).ToArray();

    /// <summary>Renders the XFA form of a PDF file (or an XDP file) read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], XfaConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream data, XfaConverterOptions? options = null) => Convert(data, options).ToArray();

    /// <summary>Renders the XFA form of a PDF file (or an XDP file) to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="data">The PDF file's (or XDP file's) bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="XfaFormatException">The file has no XFA form, or its XFA is malformed.</exception>
    /// <exception cref="PdfFormatException">The data is not a PDF file that can be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF file is encrypted and the password is wrong or missing.</exception>
    /// <exception cref="ResourceLimitExceededException">The form exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] data, XfaConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= new XfaConverterOptions();
        return Task.Run(() => XfaConverter.Convert(data, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Renders the XFA form of a PDF file (or an XDP file) read asynchronously from a stream (to its end; the stream is
    /// left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], XfaConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream data, XfaConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var buffer = new MemoryStream();
        await data.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
