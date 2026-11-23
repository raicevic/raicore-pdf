namespace RaiCore.Pdf.Email;

/// <summary>
/// Thrown when message data is malformed: an Outlook message (.msg) whose compound file is damaged or that is not a message, or a compressed RTF body that cannot be read.
/// </summary>
public sealed class MessageFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public MessageFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public MessageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public MessageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
