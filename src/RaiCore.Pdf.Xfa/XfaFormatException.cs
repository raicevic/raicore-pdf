namespace RaiCore.Pdf.Xfa;

/// <summary>
/// Thrown when an XFA form is malformed: the PDF file has no XFA form, its XFA packets are not well-formed XML (or use
/// a DTD), or the XDP has no template.
/// </summary>
/// <remarks>A PDF file that cannot be read at all throws <see cref="PdfFormatException"/> instead.</remarks>
public sealed class XfaFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public XfaFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public XfaFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public XfaFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
