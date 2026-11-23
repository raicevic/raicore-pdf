using RaiCore.Pdf.Slides.Ppt;

namespace RaiCore.Pdf.Slides;

/// <summary>
/// Converts PowerPoint 97-2003 presentations (<c>.ppt</c>, <c>.pps</c>, <c>.pot</c>: the binary format of
/// PowerPoint 97 to 2003, still written by PowerPoint since) to PDF: each slide as one page of the slide's size,
/// drawn with its master's background and shapes, color scheme, shapes, text, pictures and groups. A presentation
/// without slides to convert gives one blank page.
/// </summary>
/// <remarks>
/// <para>
/// Text takes its styles from the master's text styles (by text type and indent level) under its own paragraph and
/// character runs: alignment, indents, bullets (characters, symbol-font bullets as Unicode), spacing and line
/// spacing; font, size, bold, italic, underline, color, superscript and subscript, and links. Shapes are drawn from
/// their preset or free-form geometry with solid, gradient, pattern (as an average color) and picture fills, lines
/// with dashes and arrowheads, rotated, flipped and grouped; tables, which the format stores as groups of shapes, as
/// those shapes. Pictures (PNG, JPEG, TIFF, and device-independent bitmaps) keep their cropping. The master's date,
/// footer and slide number placeholders show as the presentation's header and footer settings say.
/// </para>
/// <para>
/// Not supported: EMF, WMF and PICT pictures, embedded objects without a picture (charts and other objects are
/// drawn as the picture PowerPoint stores with them), automatic numbering, automatic dates, WordArt, shadows, 3D
/// effects, animations and transitions, notes pages and comments. What is left out is reported to
/// <see cref="PptxConverterOptions.Diagnostics"/>. Password-protected (encrypted) presentations, PowerPoint 95 and
/// older files throw <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// Presentations are untrusted input: every length, offset and count in the file is checked, damaged edit histories
/// are recovered from what remains, and the work is bounded by the options' <see cref="PptxConverterOptions.Limits"/>
/// (<see cref="ResourceLimits.MaxElements"/> counts the file's records, <see cref="ResourceLimits.MaxNestingDepth"/>
/// bounds the nesting of groups, <see cref="ResourceLimits.MaxDecompressedBytes"/> the streams and pictures read).
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = PptToPdf.Convert("deck.ppt");
/// pdf.Save("deck.pdf");
/// </code>
/// </example>
public static class PptToPdf
{
    /// <summary>Converts a presentation to a PDF document.</summary>
    /// <param name="ppt">The presentation's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="PresentationFormatException">The data is not a valid PowerPoint 97-2003 presentation.</exception>
    /// <exception cref="NotSupportedException">The presentation is encrypted, or a PowerPoint 95 or older file.</exception>
    /// <exception cref="ResourceLimitExceededException">The presentation exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] ppt, PptxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ppt);
        return PptConverter.Convert(ppt, options ?? new PptxConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a presentation read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], PptxConverterOptions?)"/>
    public static PdfDocument Convert(Stream ppt, PptxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ppt);
        using var buffer = new MemoryStream();
        ppt.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a presentation file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="PresentationFormatException">The file is not a valid PowerPoint 97-2003 presentation.</exception>
    /// <exception cref="NotSupportedException">The presentation is encrypted, or a PowerPoint 95 or older file.</exception>
    /// <exception cref="ResourceLimitExceededException">The presentation exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, PptxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a presentation to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], PptxConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] ppt, PptxConverterOptions? options = null) => Convert(ppt, options).ToArray();

    /// <summary>Converts a presentation read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], PptxConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream ppt, PptxConverterOptions? options = null) => Convert(ppt, options).ToArray();

    /// <summary>Converts a presentation to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="ppt">The presentation's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="PresentationFormatException">The data is not a valid PowerPoint 97-2003 presentation.</exception>
    /// <exception cref="NotSupportedException">The presentation is encrypted, or a PowerPoint 95 or older file.</exception>
    /// <exception cref="ResourceLimitExceededException">The presentation exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] ppt, PptxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ppt);
        options ??= new PptxConverterOptions();
        return Task.Run(() => PptConverter.Convert(ppt, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts a presentation read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], PptxConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream ppt, PptxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ppt);
        using var buffer = new MemoryStream();
        await ppt.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
