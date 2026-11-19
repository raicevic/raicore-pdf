using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Converts Excel workbooks (<c>.xlsx</c>, <c>.xlsm</c>, also strict Office Open XML) to PDF: each sheet as a table
/// with the workbook's number formats, fonts, fills, borders, alignment, merged cells, column widths and row heights,
/// print areas and repeated rows, each sheet from a new page.
/// </summary>
/// <remarks>
/// <para>
/// Cells show what a spreadsheet program shows: numbers and dates in their number formats (in the invariant culture
/// unless <see cref="XlsxConverterOptions.Culture"/> names one), formulas as their last calculated values (formulas
/// are never calculated), and text that does not wrap running on over the empty cells beside it.
/// </para>
/// <para>
/// Columns keep the widths the sheet gives them (columns it leaves unsized are fitted to their content when it sizes
/// none), and when they do not fit the page width they are printed in bands, as <see cref="SheetOptions"/> describes,
/// unless the sheet's page setup or <see cref="SheetOptions.FitToWidth"/> shrinks them to fit. Text is never clipped.
/// </para>
/// <para>
/// Not supported: charts, images and other drawings, conditional formatting, comments, headers and footers, pivot
/// tables, binary workbooks (<c>.xlsb</c>) and password-protected (encrypted) workbooks, which throw
/// <see cref="NotSupportedException"/>, as do Excel 97-2003 (<c>.xls</c>) files.
/// </para>
/// <para>
/// Workbooks are untrusted input: their parts are read with no DTDs and nothing external resolved, the decompressed
/// size of all parts together is bounded by <see cref="ResourceLimits.MaxDecompressedBytes"/>, the cells of each sheet
/// by <see cref="ResourceLimits.MaxTableCells"/> (rows times columns), and the pages and time by
/// <see cref="ResourceLimits.MaxPages"/> and <see cref="ResourceLimits.Timeout"/> (the options'
/// <see cref="SheetOptions.Limits"/>).
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = XlsxToPdf.Convert("report.xlsx", new XlsxConverterOptions { SheetNames = ["Summary"] });
/// pdf.Save("report.pdf");
/// </code>
/// </example>
public static class XlsxToPdf
{
    /// <summary>Converts a workbook to a PDF document.</summary>
    /// <param name="xlsx">The workbook's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="SpreadsheetFormatException">The data is not a valid workbook.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted or binary workbook, or an Excel 97-2003 file.</exception>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] xlsx, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        return XlsxConverter.Convert(xlsx, options ?? new XlsxConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a workbook read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], XlsxConverterOptions?)"/>
    public static PdfDocument Convert(Stream xlsx, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        using var buffer = new MemoryStream();
        xlsx.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a workbook file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="SpreadsheetFormatException">The file is not a valid workbook.</exception>
    /// <exception cref="NotSupportedException">The file is an encrypted or binary workbook, or an Excel 97-2003 file.</exception>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a workbook to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], XlsxConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] xlsx, XlsxConverterOptions? options = null) => Convert(xlsx, options).ToArray();

    /// <summary>Converts a workbook read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], XlsxConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream xlsx, XlsxConverterOptions? options = null) => Convert(xlsx, options).ToArray();

    /// <summary>Converts a workbook to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="xlsx">The workbook's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="SpreadsheetFormatException">The data is not a valid workbook.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted or binary workbook, or an Excel 97-2003 file.</exception>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] xlsx, XlsxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        options ??= new XlsxConverterOptions();
        return Task.Run(() => XlsxConverter.Convert(xlsx, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts a workbook read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], XlsxConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream xlsx, XlsxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        using var buffer = new MemoryStream();
        await xlsx.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
