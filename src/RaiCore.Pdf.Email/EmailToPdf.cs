namespace RaiCore.Pdf.Email;

/// <summary>
/// Converts email messages (<c>.eml</c>, RFC 5322 and MIME), Outlook messages (<c>.msg</c>) and web archives
/// (<c>.mht</c>, <c>.mhtml</c>) to PDF through the HTML converter: a header block (from, to, cc, date, subject), the body (HTML, or plain text with its line
/// breaks and links) with its inline images in place, and the attachments embedded, listed and optionally appended.
/// </summary>
/// <remarks>
/// <para>
/// The body is what a mail client shows: the HTML version of a <c>multipart/alternative</c> (else the plain text one),
/// the root of a <c>multipart/related</c> with the images it refers to by <c>cid:</c> or <c>Content-Location</c>,
/// and the text, HTML and inline image parts of a <c>multipart/mixed</c> in order. Quoted-printable and base64 are
/// decoded; charsets include UTF-8, the ISO-8859 and Windows code pages, ISO-2022-JP, Shift_JIS, EUC-JP, GBK, GB18030,
/// Big5 and EUC-KR; encoded words (RFC 2047) and split, encoded parameters (RFC 2231) give header fields and file
/// names in any of them.
/// </para>
/// <para>
/// An Outlook message ([MS-OXMSG], recognized by its content, not its file name) is read from its properties: sender,
/// recipients, date and subject; the HTML body, else the HTML encapsulated in its compressed RTF body, else the plain
/// text one; and its attachments, with images shown by <c>cid:</c> in place and attached messages read the same way. It
/// is then converted exactly as the MIME message it stands for.
/// </para>
/// <para>
/// Messages are untrusted input: malformed ones are read as far as they can be, the way mail clients show them (a
/// missing closing boundary, a bad encoding or an unknown charset does not fail the conversion), and the options'
/// <see cref="Html.HtmlConverterOptions.Limits"/> bound the nesting (<see cref="ResourceLimits.MaxNestingDepth"/>), the
/// number of parts (<see cref="ResourceLimits.MaxElements"/>), the pages and the time, including what is appended. A
/// message never loads local files, and network resources only through a loader you supply.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = EmailToPdf.Convert("message.eml", new EmailConverterOptions { AppendAttachments = true });
/// pdf.Save("message.pdf");
/// </code>
/// </example>
public static class EmailToPdf
{
    /// <summary>Converts a message to a PDF document.</summary>
    /// <param name="message">The message's bytes: an <c>.eml</c>, <c>.msg</c> or <c>.mht</c> file.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="MessageFormatException">The message is an Outlook message (<c>.msg</c>) that is damaged beyond reading.</exception>
    /// <exception cref="ResourceLimitExceededException">The message exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] message, EmailConverterOptions? options = null) =>
        ConvertAsync(message, options).GetAwaiter().GetResult();

    /// <summary>Converts a message read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], EmailConverterOptions?)"/>
    public static PdfDocument Convert(Stream message, EmailConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var buffer = new MemoryStream();
        message.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a message file (<c>.eml</c>, <c>.msg</c>, <c>.mht</c>) to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ResourceLimitExceededException">The message exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, EmailConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a message to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], EmailConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] message, EmailConverterOptions? options = null) => Convert(message, options).ToArray();

    /// <summary>Converts a message read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], EmailConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream message, EmailConverterOptions? options = null) => Convert(message, options).ToArray();

    /// <summary>Converts a message to a PDF document, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="message">The message's bytes: an <c>.eml</c>, <c>.msg</c> or <c>.mht</c> file.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="MessageFormatException">The message is an Outlook message (<c>.msg</c>) that is damaged beyond reading.</exception>
    /// <exception cref="ResourceLimitExceededException">The message exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] message, EmailConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return EmailConverter.ConvertAsync(message, options ?? new EmailConverterOptions(), cancellationToken);
    }

    /// <summary>Converts a message read asynchronously from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="ConvertAsync(byte[], EmailConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream message, EmailConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var buffer = new MemoryStream();
        await message.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
