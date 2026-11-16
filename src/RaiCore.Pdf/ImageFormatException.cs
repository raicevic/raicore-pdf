namespace RaiCore.Pdf;

/// <summary>Thrown when image data is malformed or uses an unsupported format.</summary>
public sealed class ImageFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public ImageFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
