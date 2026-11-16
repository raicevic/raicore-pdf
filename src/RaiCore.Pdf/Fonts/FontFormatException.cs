namespace RaiCore.Pdf.Fonts;

/// <summary>Thrown when font data is malformed or uses an unsupported format.</summary>
public sealed class FontFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public FontFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public FontFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public FontFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
