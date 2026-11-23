using RaiCore.Pdf.Cfb;
using RaiCore.Pdf.Documents.Docx;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Documents.Odt;
using RaiCore.Pdf.Documents.Rtf;
using RaiCore.Pdf.Documents.Text;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// Gets the plain text of a word-processing document directly, without converting it to PDF: Word documents
/// (<c>.docx</c>, <c>.docm</c>, <c>.dotx</c>, strict Office Open XML), Word 97-2003 documents (<c>.doc</c>), RTF
/// documents and OpenDocument text documents (<c>.odt</c>, <c>.ott</c>, flat <c>.fodt</c>). The format is detected
/// from the data.
/// </summary>
/// <remarks>
/// <para>
/// The document is read into the same model the converters lay out, and its text is written from it with no layout:
/// paragraphs in order, list labels as the converters count them, tables cell by cell. By default the text is that of
/// the document's body (with its text boxes); <see cref="DocumentTextOptions"/> adds headers and footers, notes,
/// comments, hidden text, deleted text of tracked changes, or field codes, and sets the separators.
/// </para>
/// <para>
/// Documents are untrusted input and are read within <see cref="DocumentTextOptions.Limits"/>, as the converters read
/// them (images are not read at all). Malformed data throws <see cref="DocumentFormatException"/>; password-protected
/// (encrypted) documents and Word 6/95 documents throw <see cref="NotSupportedException"/>.
/// </para>
/// <para>Thread-safe: extractions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// string text = DocumentText.Extract("report.docx");
/// string all = DocumentText.Extract("report.doc", new DocumentTextOptions { IncludeHeadersAndFooters = true, IncludeNotes = true });
/// </code>
/// </example>
public static class DocumentText
{
    /// <summary>Gets the text of a word-processing document.</summary>
    /// <param name="document">The document's bytes (DOCX, DOC, RTF or ODT).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <returns>The text, each paragraph and table row ended by its separator.</returns>
    /// <exception cref="DocumentFormatException">The data is not a valid document in one of the supported formats.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted document (or an unsupported Word 6/95 document).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static string Extract(byte[] document, DocumentTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Extract(document, options ?? new DocumentTextOptions(), CancellationToken.None);
    }

    /// <summary>Gets the text of a word-processing document read from a stream (to its end; the stream is left open).</summary>
    /// <inheritdoc cref="Extract(byte[], DocumentTextOptions?)"/>
    public static string Extract(Stream document, DocumentTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var buffer = new MemoryStream();
        document.CopyTo(buffer);
        return Extract(buffer.ToArray(), options);
    }

    /// <summary>Gets the text of a word-processing document file.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <returns>The text, each paragraph and table row ended by its separator.</returns>
    /// <exception cref="DocumentFormatException">The file is not a valid document in one of the supported formats.</exception>
    /// <exception cref="NotSupportedException">The file is an encrypted document (or an unsupported Word 6/95 document).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    public static string Extract(string path, DocumentTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Extract(File.ReadAllBytes(path), options);
    }

    /// <summary>Gets the text of a word-processing document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="document">The document's bytes (DOCX, DOC, RTF or ODT).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the extraction.</param>
    /// <returns>The text, each paragraph and table row ended by its separator.</returns>
    /// <exception cref="DocumentFormatException">The data is not a valid document in one of the supported formats.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted document (or an unsupported Word 6/95 document).</exception>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<string> ExtractAsync(byte[] document, DocumentTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new DocumentTextOptions();
        return Task.Run(() => Extract(document, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Gets the text of a word-processing document read asynchronously from a stream (to its end; the stream is left open).
    /// </summary>
    /// <inheritdoc cref="ExtractAsync(byte[], DocumentTextOptions?, CancellationToken)"/>
    public static async Task<string> ExtractAsync(Stream document, DocumentTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ExtractAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    private static string Extract(byte[] data, DocumentTextOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options.Limits, nameof(options));
        ArgumentNullException.ThrowIfNull(options.ParagraphSeparator, nameof(options));
        ArgumentNullException.ThrowIfNull(options.TableCellSeparator, nameof(options));
        ArgumentNullException.ThrowIfNull(options.TableRowSeparator, nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        using (options.Limits.Enforce(cancellationToken))
        {
            var context = new ConversionContext(options.Limits, null, cancellationToken) { Extras = Extras(options) };
            var document = Read(data, context);
            return new PlainTextWriter(document, options, context).Write();
        }
    }

    /// <summary>What the readers keep for the options, beyond what a conversion shows.</summary>
    internal static ReadExtras Extras(DocumentTextOptions options) =>
        ReadExtras.NoImages
        | (options.IncludeHiddenText ? ReadExtras.HiddenText : 0)
        | (options.IncludeDeletedText ? ReadExtras.DeletedText : 0)
        | (options.FieldCodes ? ReadExtras.FieldCodes : 0)
        | (options.IncludeComments ? ReadExtras.Comments : 0);

    /// <summary>
    /// Reads a document of any of the supported formats into the model: RTF by its <c>{\rtf</c> start, Word 97-2003 (and
    /// encrypted Office documents) by the Compound File signature, OpenDocument packages by their <c>content.xml</c> and
    /// other ZIP packages as Word documents, and flat OpenDocument XML.
    /// </summary>
    internal static WordDocument Read(byte[] data, ConversionContext context)
    {
        if (RtfReader.IsRtf(data))
        {
            return RtfConverter.Read(data, context);
        }

        if (CompoundFile.IsCompoundFile(data))
        {
            return DocxConverter.Read(data, context);
        }

        if (data.AsSpan().StartsWith("PK"u8))
        {
            bool odf;
            using (var package = DocxPackage.OpenZip(data, context))
            {
                odf = package.Contains("content.xml") && !package.Contains("[Content_Types].xml");
            }

            return odf ? OdtConverter.Read(data, context) : DocxConverter.Read(data, context);
        }

        if (OdfPackage.LooksLikeXml(data))
        {
            return OdtConverter.Read(data, context);
        }

        throw new DocumentFormatException("The data is not a word-processing document of a supported format (DOCX, DOC, RTF or ODT).");
    }
}
