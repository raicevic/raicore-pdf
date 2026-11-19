using System.Diagnostics;

namespace RaiCore.Pdf.Sheets.Csv;

/// <summary>Reads CSV data into a <see cref="Sheet"/> and renders it.</summary>
internal static class CsvConverter
{
    /// <summary>The creator the PDFs name.</summary>
    public const string Creator = "RaiCore.Pdf.Sheets";

    /// <summary>Converts CSV data within the options' limits.</summary>
    public static PdfDocument Convert(byte[] data, CsvConverterOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options.Sheet, nameof(options));
        var limits = options.Sheet.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        using (limits.Enforce(cancellationToken))
        {
            var clock = Stopwatch.StartNew();
            var checkpoint = Checkpoint(limits, clock, cancellationToken);
            var sheet = Read(data, options, limits.MaxTableCells, checkpoint);
            return SheetRenderer.Render(sheet, options.Sheet, Creator, checkpoint);
        }
    }

    /// <summary>
    /// A checkpoint for the work outside the layout engine (which checks the limits itself): throws when the conversion is
    /// canceled or has taken longer than <see cref="ResourceLimits.Timeout"/>.
    /// </summary>
    internal static Action Checkpoint(ResourceLimits limits, Stopwatch clock, CancellationToken cancellationToken) => () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limits.Timeout != Timeout.InfiniteTimeSpan && clock.Elapsed > limits.Timeout)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.Timeout), (long)limits.Timeout.TotalMilliseconds,
                $"The conversion took longer than ResourceLimits.Timeout ({limits.Timeout}).");
        }
    };

    /// <summary>Reads CSV data into a sheet: decoded, split into records and its cells classified.</summary>
    /// <param name="data">The data.</param>
    /// <param name="options">The options.</param>
    /// <param name="maxCells">The most cells (rows times columns) the data may have.</param>
    /// <param name="checkpoint">Called regularly; throws to stop.</param>
    public static Sheet Read(byte[] data, CsvConverterOptions options, long maxCells = long.MaxValue, Action? checkpoint = null)
    {
        if (options.Delimiter is '"' or '\r' or '\n')
        {
            throw new ArgumentException("The delimiter cannot be a quote or a line break.", nameof(options));
        }

        var text = CsvText.Decode(data, options.Encoding);
        var (sepDelimiter, sepLength) = CsvDialect.SepLine(text);
        if (sepLength > 0)
        {
            text = text[sepLength..];
        }

        var delimiter = options.Delimiter ?? sepDelimiter ?? CsvDialect.Detect(text);
        var records = CsvParser.Parse(text, delimiter, maxCells, checkpoint: checkpoint);
        var headerRows = options.HasHeaderRow && records.Count > 0 ? 1 : 0;
        var decimalSeparator = options.DecimalSeparator
            ?? CellValues.DetectDecimalSeparator(records.Skip(headerRows).SelectMany(r => r), delimiter);

        var rows = new List<SheetCell[]>(records.Count);
        for (var r = 0; r < records.Count; r++)
        {
            var fields = records[r];
            var cells = new SheetCell[fields.Length];
            for (var c = 0; c < fields.Length; c++)
            {
                cells[c] = new SheetCell(fields[c], CellValues.Classify(fields[c], decimalSeparator));
            }

            rows.Add(cells);
            if (r % 1024 == 1023)
            {
                checkpoint?.Invoke();
            }
        }

        return new Sheet(rows, headerRows);
    }
}
