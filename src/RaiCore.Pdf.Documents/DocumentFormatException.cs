namespace RaiCore.Pdf.Documents;

/// <summary>
/// Thrown when word-processing document data is malformed: a DOCX or ODT package that is not a valid ZIP package or whose parts are not well-formed XML, data that is not RTF, or a Word 97-2003 document that is not a compound file or is damaged beyond reading.
/// </summary>
public sealed class DocumentFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public DocumentFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DocumentFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public DocumentFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
