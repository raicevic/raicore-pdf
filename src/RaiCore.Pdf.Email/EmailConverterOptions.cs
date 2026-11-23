using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Email;

/// <summary>Options for <see cref="EmailToPdf"/>.</summary>
/// <remarks>
/// Options are immutable once created and can be shared by conversions running at the same time.
/// </remarks>
public sealed class EmailConverterOptions
{
    /// <summary>
    /// How the message's HTML (the header block and the body) is converted: page size and margins, fonts, resource
    /// limits, PDF/A, tagging, metadata, diagnostics. Defaults to the <see cref="HtmlConverterOptions"/> defaults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HtmlConverterOptions.UserStyleSheet"/> is applied after the built-in styles of the header block
    /// (classes <c>raicore-email-header</c>, <c>raicore-email-subject</c>, <c>raicore-email-text</c> for plain text
    /// bodies and <c>raicore-email-attachment</c> for appended attachments), so it can restyle them.
    /// </para>
    /// <para>
    /// Images and style sheets in the message are loaded from the message itself (<c>cid:</c> references and
    /// <c>Content-Location</c>). Anything else goes to <see cref="HtmlConverterOptions.ResourceLoader"/>, except
    /// <c>file:</c> URLs, which a message never loads: the default loader loads nothing from the network, so remote
    /// images stay out unless you supply a loader that fetches them.
    /// </para>
    /// <para>
    /// <see cref="HtmlConverterOptions.Title"/>, <see cref="HtmlConverterOptions.Author"/> and
    /// <see cref="HtmlConverterOptions.Creator"/> default to the message's subject, its sender and
    /// <c>RaiCore.Pdf.Email</c>.
    /// </para>
    /// </remarks>
    public HtmlConverterOptions Html { get; init; } = new();

    /// <summary>
    /// The header fields shown above the body, in order, by name: <c>Subject</c> as a heading, the others in a table
    /// (address fields such as <c>From</c> and <c>To</c> as a list of names and addresses). Any field can be named, such
    /// as <c>Reply-To</c>; fields the message does not have are left out. <c>null</c> (the default) shows
    /// <c>From</c>, <c>To</c>, <c>Cc</c>, <c>Date</c> and <c>Subject</c> for an email, and nothing for a web archive
    /// (a message without recipients that is a <c>multipart/related</c> page or says it was saved by a browser).
    /// An empty list shows no header block.
    /// </summary>
    public IReadOnlyList<string>? HeaderFields { get; init; }

    /// <summary>
    /// Labels for the header fields, by field name (case-insensitive), such as <c>["From"] = "Fra"</c>; the key
    /// <c>Attachments</c> labels the attachment list. Fields without a label show their name. Defaults to <c>null</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? HeaderLabels { get; init; }

    /// <summary>
    /// Whether the attachments are embedded in the PDF as attached files (<see cref="PdfDocument.Attachments"/>),
    /// with their names and media types, so the reader can open them. Defaults to <c>true</c>. PDF/A-1 and PDF/A-2 do
    /// not allow that: with <see cref="PdfAConformance.PdfA1B"/>, <see cref="PdfAConformance.PdfA1A"/>,
    /// <see cref="PdfAConformance.PdfA2B"/> or <see cref="PdfAConformance.PdfA2A"/>, attachments are not embedded
    /// (use <see cref="PdfAConformance.PdfA3B"/> or <see cref="PdfAConformance.PdfA3A"/>).
    /// </summary>
    public bool EmbedAttachments { get; init; } = true;

    /// <summary>
    /// Whether the header block lists the attachments by name and size. Defaults to <c>true</c>.
    /// </summary>
    public bool ListAttachments { get; init; } = true;

    /// <summary>
    /// Whether the attachments that can be shown are appended after the message, each from a new page: images
    /// (JPEG, PNG, GIF, BMP, WebP, SVG) and plain text files, followed by PDF files (their pages), HTML files and
    /// attached messages (converted like the message itself), each in the order they were attached. Others are only
    /// listed and embedded. Defaults to <c>false</c>.
    /// </summary>
    public bool AppendAttachments { get; init; }

    /// <summary>
    /// Whether the message itself, its bytes unchanged, is embedded in the PDF as an attached file
    /// (<see cref="PdfDocument.Attachments"/>), so that the original can be recovered from the PDF. Defaults to
    /// <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is named after the subject (with <c>_</c> for the characters file systems do not allow, such as
    /// <c>/</c> and <c>:</c>), or <c>message</c> if there is none: <c>.eml</c> with the media type
    /// <c>message/rfc822</c> for an email, <c>.mht</c> with <c>multipart/related</c> (the type of an MHTML
    /// document, RFC 2557) for a web archive. It is described as the original message (or web archive), its
    /// relationship to the document is <see cref="PdfAttachmentRelationship.Source"/> (<c>/AFRelationship /Source</c>,
    /// as PDF/A-3 asks of the file a document was made from), and it is embedded once, whatever
    /// <see cref="EmbedAttachments"/> and <see cref="AppendAttachments"/> are; if an embedded attachment has the same
    /// name, the original gets <c>(2)</c> and so on, like attachments of the same name do.
    /// </para>
    /// <para>
    /// PDF/A-1 and PDF/A-2 do not allow it, as for <see cref="EmbedAttachments"/>: with a PDF/A-1 or PDF/A-2 level
    /// (such as <see cref="PdfAConformance.PdfA2B"/>), the original is not embedded and a warning goes to
    /// <see cref="HtmlConverterOptions.Diagnostics"/> (use <see cref="PdfAConformance.PdfA3B"/> or
    /// <see cref="PdfAConformance.PdfA3A"/>).
    /// </para>
    /// </remarks>
    public bool EmbedOriginal { get; init; }
}
