namespace RaiCore.Pdf.Sheets;

/// <content>Converting a <see cref="Workbook"/>.</content>
public static partial class XlsxToPdf
{
    /// <summary>
    /// Converts a workbook, with its changes, to a PDF document: an Excel workbook as it would be saved, an Excel
    /// 97-2003 workbook or delimited text as read (they are read only).
    /// </summary>
    /// <param name="workbook">The workbook.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(Workbook workbook, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        return workbook.ConvertToPdf(options ?? new XlsxConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a workbook, with its changes, to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(Workbook, XlsxConverterOptions?)"/>
    public static byte[] ConvertToBytes(Workbook workbook, XlsxConverterOptions? options = null) => Convert(workbook, options).ToArray();
}
