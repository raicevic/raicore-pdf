using RaiCore.Pdf.Sheets.Xls;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Converts Excel 97-2003 workbooks (<c>.xls</c>, the binary BIFF8 format, also Excel 5.0/95 workbooks) to PDF, as
/// <see cref="XlsxToPdf"/> converts <c>.xlsx</c> workbooks: each sheet as a table with the workbook's number formats,
/// fonts, fills, borders, alignment, merged cells, column widths and row heights, print areas and repeated rows, each
/// sheet from a new page.
/// </summary>
/// <remarks>
/// <para>
/// Cells show what a spreadsheet program shows: numbers and dates in their number formats (in the invariant culture
/// unless <see cref="XlsxConverterOptions.Culture"/> names one), formulas as their last calculated values (formulas
/// are never calculated), and text that does not wrap running on over the empty cells beside it. Columns and pages are
/// laid out as <see cref="XlsxToPdf"/> lays them out, with the same options.
/// </para>
/// <para>
/// Not supported, and reported through <see cref="SheetOptions.Diagnostics"/>: charts, images and other drawings,
/// conditional formatting, comments, headers and footers. Not supported either: Excel 2.x-4.0 files and
/// password-protected (encrypted) workbooks, which throw <see cref="NotSupportedException"/>; this includes workbooks
/// protected with Excel's default password, which open without asking for one. Data that is an XLSX workbook (as
/// <c>.xls</c> files sometimes are) is converted as one, and <see cref="XlsxToPdf"/> likewise converts Excel 97-2003
/// workbooks.
/// </para>
/// <para>
/// Workbooks are untrusted input: every length and offset in the file is checked, the size of the workbook stream is
/// bounded by <see cref="ResourceLimits.MaxDecompressedBytes"/>, the cells of each sheet by
/// <see cref="ResourceLimits.MaxTableCells"/> (rows times columns), and the pages and time by
/// <see cref="ResourceLimits.MaxPages"/> and <see cref="ResourceLimits.Timeout"/> (the options'
/// <see cref="SheetOptions.Limits"/>). Records that cannot be read are skipped.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = XlsToPdf.Convert("report.xls", new XlsxConverterOptions { SheetNames = ["Summary"] });
/// pdf.Save("report.pdf");
/// </code>
/// </example>
public static class XlsToPdf
{
    /// <summary>Converts a workbook to a PDF document.</summary>
    /// <param name="xls">The workbook's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="SpreadsheetFormatException">The data is not a valid workbook, or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The workbook is encrypted, or an Excel 2.x-4.0 file.</exception>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] xls, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xls);
        return XlsConverter.Convert(xls, options ?? new XlsxConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts a workbook read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], XlsxConverterOptions?)"/>
    public static PdfDocument Convert(Stream xls, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xls);
        using var buffer = new MemoryStream();
        xls.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a workbook file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="SpreadsheetFormatException">The file is not a valid workbook, or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The workbook is encrypted, or an Excel 2.x-4.0 file.</exception>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, XlsxConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts a workbook to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], XlsxConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] xls, XlsxConverterOptions? options = null) => Convert(xls, options).ToArray();

    /// <summary>Converts a workbook read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], XlsxConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream xls, XlsxConverterOptions? options = null) => Convert(xls, options).ToArray();

    /// <summary>Converts a workbook to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="xls">The workbook's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="SpreadsheetFormatException">The data is not a valid workbook, or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The workbook is encrypted, or an Excel 2.x-4.0 file.</exception>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] xls, XlsxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xls);
        options ??= new XlsxConverterOptions();
        return Task.Run(() => XlsConverter.Convert(xls, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts a workbook read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], XlsxConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream xls, XlsxConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xls);
        using var buffer = new MemoryStream();
        await xls.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
