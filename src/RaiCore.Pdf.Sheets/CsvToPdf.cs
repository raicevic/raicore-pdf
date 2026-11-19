using RaiCore.Pdf.Sheets.Csv;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Converts CSV data (RFC 4180: comma-, semicolon-, tab- or bar-separated values) to PDF: a table with the header row
/// repeated at the top of every page, column widths fitted to the content within the page width, and numbers and dates
/// right-aligned.
/// </summary>
/// <remarks>
/// <para>
/// The delimiter, the text encoding and the decimal separator are detected unless the options name them (see
/// <see cref="CsvConverterOptions"/>). Quoted fields may hold delimiters, line breaks and doubled quotes; records end
/// with CRLF, LF or CR.
/// </para>
/// <para>
/// Text is never clipped: columns wrap their text between words when they do not all fit the page width, words wider
/// than their column are broken, and when the columns cannot all get a readable width they are split into groups that
/// are printed one after the other (see <see cref="SheetOptions"/>).
/// </para>
/// <para>
/// CSV data is untrusted input: malformed data is read the way spreadsheet programs read it (a quote that is never
/// closed is an ordinary character, rows with fewer or more fields than others are padded with empty cells, bytes that
/// are not valid in the encoding become U+FFFD), and the options'
/// <see cref="SheetOptions.Limits"/> bound the cells (<see cref="ResourceLimits.MaxTableCells"/>, rows times columns),
/// the pages and the time.
/// </para>
/// <para>Thread-safe: conversions can run in parallel.</para>
/// </remarks>
/// <example>
/// <code>
/// var pdf = CsvToPdf.Convert("orders.csv", new CsvConverterOptions { Sheet = new SheetOptions { Landscape = true } });
/// pdf.Save("orders.pdf");
/// </code>
/// </example>
public static class CsvToPdf
{
    /// <summary>Converts CSV data to a PDF document.</summary>
    /// <param name="csv">The data's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ResourceLimitExceededException">The data exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(byte[] csv, CsvConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(csv);
        return CsvConverter.Convert(csv, options ?? new CsvConverterOptions(), CancellationToken.None);
    }

    /// <summary>Converts CSV data read from a stream (to its end; the stream is left open) to a PDF document.</summary>
    /// <inheritdoc cref="Convert(byte[], CsvConverterOptions?)"/>
    public static PdfDocument Convert(Stream csv, CsvConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(csv);
        using var buffer = new MemoryStream();
        csv.CopyTo(buffer);
        return Convert(buffer.ToArray(), options);
    }

    /// <summary>Converts a CSV file to a PDF document.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="ResourceLimitExceededException">The data exceeds one of the options' limits.</exception>
    public static PdfDocument Convert(string path, CsvConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Convert(File.ReadAllBytes(path), options);
    }

    /// <summary>Converts CSV data to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], CsvConverterOptions?)"/>
    public static byte[] ConvertToBytes(byte[] csv, CsvConverterOptions? options = null) => Convert(csv, options).ToArray();

    /// <summary>Converts CSV data read from a stream (to its end; the stream is left open) to PDF bytes.</summary>
    /// <inheritdoc cref="Convert(byte[], CsvConverterOptions?)"/>
    public static byte[] ConvertToBytes(Stream csv, CsvConverterOptions? options = null) => Convert(csv, options).ToArray();

    /// <summary>Converts CSV data to a PDF document on the thread pool, observing <paramref name="cancellationToken"/>.</summary>
    /// <param name="csv">The data's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <exception cref="ResourceLimitExceededException">The data exceeds one of the options' limits.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(byte[] csv, CsvConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        options ??= new CsvConverterOptions();
        return Task.Run(() => CsvConverter.Convert(csv, options, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Converts CSV data read asynchronously from a stream (to its end; the stream is left open) to a PDF document.
    /// </summary>
    /// <inheritdoc cref="ConvertAsync(byte[], CsvConverterOptions?, CancellationToken)"/>
    public static async Task<PdfDocument> ConvertAsync(Stream csv, CsvConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        using var buffer = new MemoryStream();
        await csv.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ConvertAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }
}
