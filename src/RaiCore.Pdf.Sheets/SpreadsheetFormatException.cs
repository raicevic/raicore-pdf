namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Thrown when spreadsheet data is malformed: an XLSX workbook that is not a valid ZIP package, whose parts are not
/// well-formed XML, or that has no workbook part.
/// </summary>
public sealed class SpreadsheetFormatException : PdfException
{
    /// <summary>Creates the exception.</summary>
    public SpreadsheetFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SpreadsheetFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public SpreadsheetFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
