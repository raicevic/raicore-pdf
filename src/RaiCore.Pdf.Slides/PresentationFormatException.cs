namespace RaiCore.Pdf.Slides;

/// <summary>
/// Thrown when presentation data is malformed: a PPTX presentation that is not a valid ZIP package, whose parts are
/// not well-formed XML, or that has no presentation part; a PowerPoint 97-2003 presentation that is not a compound
/// file or whose document cannot be found.
/// </summary>
public sealed class PresentationFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public PresentationFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PresentationFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PresentationFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
