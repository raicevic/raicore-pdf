using System.Text.RegularExpressions;

namespace RaiCore.Pdf;

/// <summary>
/// How an attached file relates to the document (<c>/AFRelationship</c>, PDF 2.0 and PDF/A-3 associated files).
/// </summary>
public enum PdfAttachmentRelationship
{
    /// <summary>The relationship is not known or none of the others.</summary>
    Unspecified,

    /// <summary>The original source the document was created from (e.g. a word processor file).</summary>
    Source,

    /// <summary>Data the document's content is based on, such as a spreadsheet behind a chart.</summary>
    Data,

    /// <summary>An alternative representation of the content, such as the XML of an e-invoice.</summary>
    Alternative,

    /// <summary>A supplement to the content, such as extra data for the reader.</summary>
    Supplement,

    /// <summary>Data collected by the document's form (e.g. its field values as XML).</summary>
    FormData,

    /// <summary>A schema definition for other content, e.g. an XML schema.</summary>
    Schema,
}

/// <summary>
/// A file attached to a <see cref="PdfDocument"/> (an embedded file, listed in the viewer's attachments panel): written
/// to the document's <c>/EmbeddedFiles</c> name tree and its associated files (<c>/AF</c>), with a MIME type,
/// description, relationship, size and dates. See <see cref="PdfDocument.Attachments"/>.
/// </summary>
public sealed partial class PdfAttachment
{
    private readonly byte[] _data;

    /// <summary>Creates an attachment.</summary>
    /// <param name="fileName">The file name shown to the user, such as <c>invoice.xml</c> (Unicode is fine).</param>
    /// <param name="data">The file's content. It is copied.</param>
    /// <param name="mimeType">The MIME type, such as <c>text/xml</c>, <c>application/pdf</c> or the default <c>application/octet-stream</c>.</param>
    /// <exception cref="ArgumentException">The file name is empty, or the MIME type is not <c>type/subtype</c>.</exception>
    public PdfAttachment(string fileName, ReadOnlySpan<byte> data, string mimeType = "application/octet-stream")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (!MimeType().IsMatch(mimeType))
        {
            throw new ArgumentException($"'{mimeType}' is not a MIME type such as text/xml (type/subtype).", nameof(mimeType));
        }

        FileName = fileName;
        MediaType = mimeType;
        _data = data.ToArray();
    }

    /// <summary>The file name.</summary>
    public string FileName { get; }

    /// <summary>The MIME type (written as the embedded file's <c>/Subtype</c>).</summary>
    public string MediaType { get; }

    /// <summary>The file's content.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>A description shown by viewers (<c>/Desc</c>), or <c>null</c>.</summary>
    public string? Description { get; init; }

    /// <summary>How the file relates to the document. Defaults to <see cref="PdfAttachmentRelationship.Unspecified"/>.</summary>
    public PdfAttachmentRelationship Relationship { get; init; } = PdfAttachmentRelationship.Unspecified;

    /// <summary>When the file was last modified, or <c>null</c> for the document's modification date.</summary>
    public DateTimeOffset? ModificationDate { get; init; }

    /// <summary>When the file was created, or <c>null</c> to leave it out.</summary>
    public DateTimeOffset? CreationDate { get; init; }

    // Type and subtype of letters, digits and _.+- (no parameters), as PDF/A-3 (veraPDF) accepts them.
    [GeneratedRegex("^[A-Za-z0-9_.+-]+/[A-Za-z0-9_.+-]+$")]
    private static partial Regex MimeType();
}
