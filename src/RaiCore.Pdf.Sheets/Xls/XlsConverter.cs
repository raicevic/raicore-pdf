using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using RaiCore.Pdf.Cfb;
using RaiCore.Pdf.Sheets.Csv;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// Reads the sheets of a binary workbook (<c>.xls</c>) into formatted <see cref="Sheet"/>s, as the XLSX converter
/// builds them, and renders them one after the other.
/// </summary>
internal static class XlsConverter
{
    /// <summary>
    /// Whether data is (or claims to be) a binary workbook: a compound file (also an encrypted XLSX workbook, which is
    /// one too), or a BIFF stream without one.
    /// </summary>
    public static bool IsBinary(ReadOnlySpan<byte> data) => CompoundFile.IsCompoundFile(data) || IsBiffStream(data);

    /// <summary>
    /// Converts a workbook within the options' limits; an XLSX workbook (a ZIP package, as <c>.xls</c> files sometimes
    /// are) is converted as one.
    /// </summary>
    public static PdfDocument Convert(byte[] data, XlsxConverterOptions options, CancellationToken cancellationToken)
    {
        if (!IsBinary(data) && data.AsSpan().StartsWith("PK"u8))
        {
            return XlsxConverter.Convert(data, options, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(options.Sheet, nameof(options));
        var limits = options.Sheet.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        using (limits.Enforce(cancellationToken))
        {
            var clock = Stopwatch.StartNew();
            var checkpoint = CsvConverter.Checkpoint(limits, clock, cancellationToken);
            var (workbook, title) = Open(data, limits, options.Culture ?? CultureInfo.InvariantCulture, checkpoint);
            var chosen = XlsxConverter.Choose(workbook.Sheets, options);
            return SheetRenderer.Render(Sheets(workbook, chosen, options, limits, checkpoint), options.Sheet, XlsxConverter.Creator, title, checkpoint);
        }
    }

    /// <summary>Reads the sheets of a workbook (for tests): all the options select.</summary>
    internal static List<Sheet> Read(byte[] data, XlsxConverterOptions? options = null)
    {
        options ??= new XlsxConverterOptions();
        var limits = options.Sheet.Limits;
        var (workbook, _) = Open(data, limits, options.Culture ?? CultureInfo.InvariantCulture, null);
        return [.. Sheets(workbook, XlsxConverter.Choose(workbook.Sheets, options), options, limits, null)];
    }

    /// <summary>
    /// Opens a workbook: finds its workbook stream (<c>Workbook</c>, or <c>Book</c> for Excel 5.0/95) in the compound
    /// file and reads its globals, and the title of its summary information.
    /// </summary>
    /// <exception cref="SpreadsheetFormatException">The data is not a binary workbook, or is damaged beyond reading.</exception>
    /// <exception cref="NotSupportedException">The workbook is encrypted, or of an older version.</exception>
    internal static (XlsWorkbook Workbook, string? Title) Open(byte[] data, ResourceLimits limits, CultureInfo culture, Action? checkpoint)
    {
        if (!CompoundFile.IsCompoundFile(data))
        {
            if (IsBiffStream(data))
            {
                return (XlsWorkbook.Read(data, culture, checkpoint), null);
            }

            throw new SpreadsheetFormatException(data.AsSpan().StartsWith("PK"u8)
                ? "The data is a ZIP package, not an Excel 97-2003 workbook: convert it with XlsxToPdf."
                : "The data is not an Excel 97-2003 workbook (a compound file).");
        }

        try
        {
            var file = CompoundFile.Open(data, limits);
            var root = file.Root;
            var stream = root["Workbook"] is { Kind: CfbEntryKind.Stream } workbook ? workbook
                : root["Book"] is { Kind: CfbEntryKind.Stream } book ? book
                : null;
            if (stream is null)
            {
                if (root["EncryptedPackage"] is not null || root["EncryptionInfo"] is not null)
                {
                    throw new NotSupportedException("The workbook is a password-protected (encrypted) workbook, which is not supported.");
                }

                throw new SpreadsheetFormatException("The compound file has no Workbook stream: it is not an Excel workbook.");
            }

            var bytes = file.Read(stream);
            checkpoint?.Invoke();
            return (XlsWorkbook.Read(bytes, culture, checkpoint), Title(file));
        }
        catch (PdfFormatException ex)
        {
            throw new SpreadsheetFormatException("The workbook's compound file is damaged: " + ex.Message, ex);
        }
    }

    /// <summary>Whether data starts like a BIFF stream (a BOF record of any version) without a compound file around it.</summary>
    internal static bool IsBiffStream(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(data) is 0x0809 or 0x0409 or 0x0209 or 0x0009
        && BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) is >= 4 and <= 32;

    /// <summary>The title of the summary information, or <c>null</c>.</summary>
    private static string? Title(CompoundFile file)
    {
        if (file.Root["\x05SummaryInformation"] is not { Kind: CfbEntryKind.Stream } stream)
        {
            return null;
        }

        try
        {
            return PropertySetStream.Summary(file.Read(stream)).TryGetValue(PropertySetStream.Title, out var value)
                && value is string title && title.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        }
        catch (Exception ex) when (ex is PdfFormatException or ResourceLimitExceededException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads and builds the chosen worksheets one at a time, so that only one is in memory while it is laid out, and
    /// reports what each has that is not supported, and the chosen chart sheets.
    /// </summary>
    private static IEnumerable<Sheet> Sheets(XlsWorkbook workbook, List<XlsxSheetEntry> chosen, XlsxConverterOptions options, ResourceLimits limits, Action? checkpoint)
    {
        var diagnostics = options.Sheet.Diagnostics;
        var bookmarks = chosen.Count(s => s.IsWorksheet) > 1;
        foreach (var entry in chosen)
        {
            checkpoint?.Invoke();
            if (!entry.IsWorksheet)
            {
                XlsxConverter.ReportChartSheet(entry, diagnostics);
                continue;
            }

            var worksheet = XlsWorksheet.Read(workbook, workbook.SheetOffsets[entry.Index], limits.MaxTableCells, checkpoint, limits);
            worksheet.Unsupported.Report(entry.Name, diagnostics);
            yield return XlsxConverter.Build(worksheet, entry, workbook.PrintArea(entry.Index), workbook.PrintTitleRows(entry.Index),
                workbook.Styles.DefaultFontSize, workbook.Styles.DefaultFontName, options, limits.MaxTableCells, bookmarks);
        }
    }
}
