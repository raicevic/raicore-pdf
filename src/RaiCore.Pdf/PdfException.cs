namespace RaiCore.Pdf;

/// <summary>
/// The base class of the exceptions RaiCore.Pdf throws when it rejects its input: <see cref="PdfFormatException"/>,
/// <see cref="PdfPasswordException"/>, <see cref="Fonts.FontFormatException"/>, <see cref="ImageFormatException"/> and
/// <see cref="ResourceLimitExceededException"/>, and in the converter packages <c>DocumentFormatException</c>,
/// <c>SpreadsheetFormatException</c>, <c>PresentationFormatException</c> and <c>MessageFormatException</c> (one per
/// kind of input). Catch it to handle all of them at once.
/// </summary>
public class PdfException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PdfException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PdfException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
