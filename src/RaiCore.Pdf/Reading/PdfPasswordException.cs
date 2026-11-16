namespace RaiCore.Pdf;

/// <summary>
/// Thrown when an encrypted PDF file is opened without its password: the password given (or the empty password, when
/// none was given) is neither the file's user password nor its owner password.
/// </summary>
public sealed class PdfPasswordException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public PdfPasswordException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PdfPasswordException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfPasswordException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
