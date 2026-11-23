using RaiCore.Pdf.Slides.Pptx;

namespace RaiCore.Pdf.Slides;

/// <summary>
/// Converts PowerPoint presentations (<c>.pptx</c>, <c>.pptm</c>, <c>.ppsx</c>, <c>.potx</c>, also strict Office Open
/// XML) to PDF: each slide as one page of the slide's size, drawn with its master's and layout's shapes, background,
/// theme colors and fonts, shapes, text, pictures, tables and groups. A presentation without slides to convert gives
/// one blank page.
/// </summary>
/// <remarks>
/// <para>
/// Placeholders take their position, shape and text styles from the layout and master (by index and type), and text
/// boxes from the presentation's default text style. Text frames keep their insets, vertical anchoring, wrapping and
/// autofit (text that does not fit a frame set to shrink is made smaller); paragraphs their alignment, indents,
/// bullets (characters, numbers, symbol-font bullets as Unicode), spacing and line spacing; runs their font, size,
/// bold, italic, underline, strikethrough, color, superscript and subscript, capitals, character spacing and links.
/// Shapes are drawn from their preset or free-form geometry with solid, gradient, pattern (as an average color) and
/// picture fills, lines with dashes and arrowheads, rotated, flipped and grouped. Pictures (PNG, JPEG, GIF, BMP, TIFF,
/// WebP, SVG) keep their cropping and the shape they are clipped to; tables their style (bands, header row and column),
/// cell fills, borders, merged cells and text, rows growing to fit their text. SmartArt is drawn from the drawing
/// PowerPoint stores with it. Charts (columns, bars, lines, pies, doughnuts, areas, scatter, bubble, radar and stock
/// charts) are drawn as vector graphics from their cached data, with Excel's axis scaling and their text extractable.
/// </para>
/// <para>
/// Not supported: surface charts, SmartArt without a stored drawing, PICT pictures, embedded objects without a
/// picture, video and audio (their poster picture is drawn), animations and transitions, 3D effects, shadows, glow,
/// reflections, soft edges, WordArt transforms, multiple text columns, notes pages and comments. What is left out is
/// reported to <see cref="PptxConverterOptions.Diagnostics"/>. Password-protected (encrypted) presentations and
/// PowerPoint 97-2003 (<c>.ppt</c>) files throw <see cref="NotSupportedException"/>: convert the latter with
/// <see cref="PptToPdf"/>.
/// </para>
/// <para>
/// Presentations are untrusted input: their parts are read with no DTDs and nothing external resolved (linked pictures
/// are not loaded), and the work is bounded by the options' <see cref="PptxConverterOptions.Limits"/>.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = PptxToPdf.Convert("deck.pptx");
/// pdf.Save("deck.pdf");
/// </code>
/// </example>
public static class PptxToPdf
{
    /// <summary>Converts a presentation to a PDF document.</summary>
    /// <param name="pptx">The presentation's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="PresentationFormatException">The data is not a valid presentation.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted presentation or a PowerPoint 97-2003 file.</exception>
    /// <exception cref="ResourceLimitExceededException">The presentation exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] pptx, PptxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pptx);
        return PptxConverter.Convert(pptx, options ?? new PptxConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a presentation read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], PptxConverterOptions?)"/>
    public static PdfDocument Convert(Stream pptx, PptxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pptx);
        using var buffer = new MemoryStream();
        pptx.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a presentation file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="PresentationFormatException">The file is not a valid presentation.</exception>
    /// <exception cref="NotSupportedException">The file is an encrypted presentation or a PowerPoint 97-2003 file.</exception>
    /// <exception cref="ResourceLimitExceededException">The presentation exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, PptxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a presentation to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], PptxConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] pptx, PptxConverterOptions? options = null) => Convert(pptx, options).ToArray();

    /// <summary>Converts a presentation read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], PptxConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream pptx, PptxConverterOptions? options = null) => Convert(pptx, options).ToArray();

    /// <summary>Converts a presentation to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="pptx">The presentation's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="PresentationFormatException">The data is not a valid presentation.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted presentation or a PowerPoint 97-2003 file.</exception>
    /// <exception cref="ResourceLimitExceededException">The presentation exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] pptx, PptxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pptx);
        options ??= new PptxConverterOptions();
        return Task.Run(() => PptxConverter.Convert(pptx, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts a presentation read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], PptxConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream pptx, PptxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pptx);
        using var buffer = new MemoryStream();
        await pptx.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
