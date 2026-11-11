namespace RaiCore.Pdf;

/// <summary>
/// Password protection and permissions for a document (the standard security handler, ISO 32000-2, 7.6.4). Set it on
/// <see cref="PdfDocument.Encryption"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every string and stream in the file is encrypted with AES. A viewer asks for a password when
/// <see cref="UserPassword"/> is set; with an empty user password the file opens without one, and the viewer enforces
/// <see cref="Permissions"/>. Entering the <see cref="OwnerPassword"/> gives full access whatever the permissions.
/// Permissions are advisory: a conforming viewer honors them, but a determined user with an unrestricted tool can
/// ignore them once the file is open.
/// </para>
/// <para>
/// Passwords for <see cref="PdfEncryptionAlgorithm.Aes256"/> are prepared with a simplified SASLprep (RFC 4013):
/// non-ASCII spaces become U+0020, characters that map to nothing (such as the soft hyphen and zero-width joiners)
/// are removed, the text is NFKC-normalized, and control, private-use, non-character, surrogate and other prohibited
/// code points are rejected. Unassigned code points and the bidirectional rule are not checked. The UTF-8 bytes are
/// then cut to 127 bytes, as ISO 32000-2 requires. Passwords for <see cref="PdfEncryptionAlgorithm.Aes128"/> must be
/// in PDFDocEncoding (printable Latin-1) and are cut to 32 bytes.
/// </para>
/// <para>Encrypted output is not byte-for-byte reproducible: keys, salts and initialization vectors are random.</para>
/// </remarks>
/// <example>
/// <code>
/// doc.Encryption = new PdfEncryption
/// {
///     OwnerPassword = "owner secret",
///     Permissions = PdfPermissions.Print | PdfPermissions.PrintHighQuality,
/// };
/// </code>
/// </example>
public sealed class PdfEncryption
{
    /// <summary>
    /// The password needed to open the document. <c>null</c> or empty (the default) lets anyone open it, subject to
    /// <see cref="Permissions"/>.
    /// </summary>
    public string? UserPassword { get; init; }

    /// <summary>
    /// The password that gives full access, whatever <see cref="Permissions"/> says. When <c>null</c> or empty, a random
    /// owner password is generated (and discarded), so nobody can lift the restrictions. Use a password different from
    /// <see cref="UserPassword"/>: when the two are equal, the user password gives full access.
    /// </summary>
    public string? OwnerPassword { get; init; }

    /// <summary>What a user who opens the document without the owner password may do. Defaults to everything.</summary>
    public PdfPermissions Permissions { get; init; } = PdfPermissions.All;

    /// <summary>The encryption algorithm. Defaults to <see cref="PdfEncryptionAlgorithm.Aes256"/>.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; init; } = PdfEncryptionAlgorithm.Aes256;

    /// <summary>
    /// Whether the document's XMP metadata stream (if any) is encrypted too. Defaults to <c>true</c>; <c>false</c> leaves it
    /// readable by search engines and indexers that do not know the password.
    /// </summary>
    public bool EncryptMetadata { get; init; } = true;
}

/// <summary>The encryption algorithm used by <see cref="PdfEncryption"/>.</summary>
public enum PdfEncryptionAlgorithm
{
    /// <summary>
    /// AES with a 256-bit key and SHA-2 password hashing (security handler revision 6, ISO 32000-2). Supported by
    /// Acrobat X and later, PDFium (Chrome, Edge), pdf.js (Firefox), macOS Preview, poppler and qpdf.
    /// </summary>
    Aes256,

    /// <summary>
    /// AES with a 128-bit key and MD5-based key derivation (security handler revision 4, PDF 1.6). Only for readers
    /// that predate AES-256; its password hashing is far weaker.
    /// </summary>
    Aes128,
}

/// <summary>
/// What a user may do with an encrypted document opened without the owner password (the <c>/P</c> entry,
/// ISO 32000-2, table 22). The values are the bits of <c>/P</c>.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>Nothing beyond viewing.</summary>
    None = 0,

    /// <summary>Print the document (at low resolution unless <see cref="PrintHighQuality"/> is also set). Bit 3.</summary>
    Print = 1 << 2,

    /// <summary>Change the document in ways other than those covered by the other flags. Bit 4.</summary>
    Modify = 1 << 3,

    /// <summary>Copy or otherwise extract text and graphics. Bit 5.</summary>
    Copy = 1 << 4,

    /// <summary>Add or change annotations and, together with <see cref="Modify"/>, fill and create form fields. Bit 6.</summary>
    Annotate = 1 << 5,

    /// <summary>Fill in existing form fields (including signature fields), even without <see cref="Annotate"/>. Bit 9.</summary>
    FillForms = 1 << 8,

    /// <summary>
    /// Extract text and graphics for accessibility (bit 10). PDF 2.0 deprecates this restriction, so with
    /// <see cref="PdfEncryptionAlgorithm.Aes256"/> the bit is always set, as ISO 32000-2 requires.
    /// </summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Assemble the document: insert, rotate or delete pages and create bookmarks or thumbnails. Bit 11.</summary>
    Assemble = 1 << 10,

    /// <summary>Print at full quality (bit 12). Requires <see cref="Print"/>.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>Every permission.</summary>
    All = Print | Modify | Copy | Annotate | FillForms | ExtractForAccessibility | Assemble | PrintHighQuality,
}
