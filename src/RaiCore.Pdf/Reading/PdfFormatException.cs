namespace RaiCore.Pdf;

/// <summary>Thrown when a PDF file cannot be read: it is not a PDF, or it is damaged beyond repair.</summary>
public sealed class PdfFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PdfFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PdfFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PdfFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
