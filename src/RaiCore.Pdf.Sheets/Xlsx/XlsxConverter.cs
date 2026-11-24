using System.Diagnostics;
using System.Globalization;
using RaiCore.Pdf.Sheets.Csv;
using RaiCore.Pdf.Sheets.Xls;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>Reads the sheets of a workbook into formatted <see cref="Sheet"/>s and renders them, one after the other.</summary>
internal static class XlsxConverter
{
    /// <summary>The creator the PDFs name.</summary>
    public const string Creator = CsvConverter.Creator;

    /// <summary>The padding of a column in characters, added to a base width (five pixels of seven-pixel digits).</summary>
    private const double ColumnPadding = 5 / 7.0;

    /// <summary>Converts a workbook within the options' limits.</summary>
    public static PdfDocument Convert(byte[] data, XlsxConverterOptions options, CancellationToken cancellationToken)
    {
        if (XlsConverter.IsBinary(data))
        {
            // An Excel 97-2003 workbook (or an encrypted one, which the binary converter tells apart).
            return XlsConverter.Convert(data, options, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(options.Sheet, nameof(options));
        var limits = options.Sheet.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        using (limits.Enforce(cancellationToken))
        {
            var clock = Stopwatch.StartNew();
            var checkpoint = CsvConverter.Checkpoint(limits, clock, cancellationToken);
            using var package = XlsxPackage.Open(data, limits, checkpoint);
            var workbook = XlsxWorkbook.Read(package, options.Culture ?? CultureInfo.InvariantCulture);
            var chosen = Choose(workbook.Sheets, options);
            return SheetRenderer.Render(Sheets(package, workbook, chosen, options, limits, checkpoint), options.Sheet, Creator, workbook.Title, checkpoint);
        }
    }

    /// <summary>Reads the sheets of a workbook (for tests): all the options select.</summary>
    internal static List<Sheet> Read(byte[] data, XlsxConverterOptions? options = null)
    {
        options ??= new XlsxConverterOptions();
        var limits = options.Sheet.Limits;
        using var package = XlsxPackage.Open(data, limits);
        var workbook = XlsxWorkbook.Read(package, options.Culture ?? CultureInfo.InvariantCulture);
        return [.. Sheets(package, workbook, Choose(workbook.Sheets, options), options, limits, null)];
    }

    /// <summary>
    /// Reads and builds the chosen worksheets one at a time, so that only one is in memory while it is laid out, and
    /// reports what each has that is not supported (when diagnostics are wanted), and the chosen chart sheets.
    /// </summary>
    private static IEnumerable<Sheet> Sheets(XlsxPackage package, XlsxWorkbook workbook, List<XlsxSheetEntry> chosen, XlsxConverterOptions options, ResourceLimits limits, Action? checkpoint)
    {
        var diagnostics = options.Sheet.Diagnostics;
        var bookmarks = chosen.Count(s => s.IsWorksheet) > 1;
        var images = new Dictionary<string, PdfImage?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in chosen)
        {
            checkpoint?.Invoke();
            if (!entry.IsWorksheet)
            {
                ReportChartSheet(entry, diagnostics);
                continue;
            }

            var worksheet = entry.Path is { } path
                ? package.Parse(path, reader => XlsxWorksheet.Read(reader, workbook, limits.MaxTableCells, checkpoint), XlsxWorksheet.Empty)
                : XlsxWorksheet.Empty;
            if (worksheet.ConditionalFormats.Count > 0)
            {
                ConditionalFormatting.Apply(worksheet, worksheet.ConditionalFormats, workbook.Styles, checkpoint);
            }

            if (entry.Path is { } part)
            {
                worksheet.Drawings.AddRange(new XlsxDrawing(package, workbook, limits, worksheet.Unsupported, images).Read(part));
                if (diagnostics is not null)
                {
                    XlsxContent.Collect(package, part, worksheet.Unsupported, checkpoint);
                    worksheet.Unsupported.Report(entry.Name, diagnostics);
                }
            }

            yield return Build(worksheet, entry, workbook, options, limits.MaxTableCells, bookmarks);
        }
    }

    /// <summary>Reports a chosen chart sheet, which is not converted.</summary>
    internal static void ReportChartSheet(XlsxSheetEntry entry, Action<SheetDiagnostic>? diagnostics)
    {
        if (entry.IsChartSheet)
        {
            var content = new UnsupportedContent();
            content.Add(SheetDiagnosticKind.ChartSheet);
            content.Report(entry.Name, diagnostics);
        }
    }

    /// <summary>
    /// The worksheets to convert, in the workbook's order: those the options name or number (hidden ones too), else the
    /// visible ones. Chart sheets and other sheets that are not worksheets are left out.
    /// </summary>
    internal static List<XlsxSheetEntry> Select(XlsxWorkbook workbook, XlsxConverterOptions options) => Select(workbook.Sheets, options);

    /// <inheritdoc cref="Select(XlsxWorkbook, XlsxConverterOptions)"/>
    internal static List<XlsxSheetEntry> Select(IReadOnlyList<XlsxSheetEntry> sheets, XlsxConverterOptions options) =>
        [.. Choose(sheets, options).Where(s => s.IsWorksheet)];

    /// <summary>
    /// The sheets the options choose, in the workbook's order: those they name or number (hidden ones too), else the
    /// visible worksheets and chart sheets. Of these, the worksheets are converted and the chart sheets reported.
    /// </summary>
    internal static List<XlsxSheetEntry> Choose(IReadOnlyList<XlsxSheetEntry> sheets, XlsxConverterOptions options)
    {
        if (options.SheetNames is null && options.SheetIndexes is null)
        {
            return [.. sheets.Where(s => (s.IsWorksheet || s.IsChartSheet) && !s.Hidden)];
        }

        var chosen = new HashSet<int>();
        foreach (var name in options.SheetNames ?? [])
        {
            ArgumentNullException.ThrowIfNull(name, nameof(options));
            var sheet = sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The workbook has no sheet named '{name}'.", nameof(options));
            chosen.Add(sheet.Index);
        }

        foreach (var index in options.SheetIndexes ?? [])
        {
            if (index < 0 || index >= sheets.Count)
            {
                throw new ArgumentException($"The workbook has no sheet at index {index} (it has {sheets.Count}).", nameof(options));
            }

            chosen.Add(index);
        }

        return [.. sheets.Where(s => chosen.Contains(s.Index))];
    }

    /// <summary>
    /// Builds the sheet to print: the print area (or the used range from A1), without hidden rows and columns, with the
    /// print titles (rows repeated on every page) as header rows, merged ranges, sizes and page settings.
    /// </summary>
    internal static Sheet Build(XlsxWorksheet worksheet, XlsxSheetEntry entry, XlsxWorkbook workbook, XlsxConverterOptions options, long maxCells, bool bookmark) =>
        Build(worksheet, entry, workbook.PrintArea(entry), workbook.PrintTitleRows(entry), workbook.Styles.DefaultFontSize, workbook.Styles.DefaultFontName, options, maxCells, bookmark,
            (workbook.MajorFont, workbook.MinorFont));

    /// <summary>
    /// Builds the sheet to print from a worksheet of any workbook format, with its print area and print titles (the
    /// rows repeated on every page) and the workbook's default font.
    /// </summary>
    internal static Sheet Build(XlsxWorksheet worksheet, XlsxSheetEntry entry, CellRange? printArea, (int First, int Last)? printTitleRows,
        double defaultFontSize, string? defaultFontName, XlsxConverterOptions options, long maxCells, bool bookmark, (string? Major, string? Minor) themeFonts = default)
    {
        var usePageSetup = options.UsePageSetup;
        var (usedRows, usedColumns) = SheetDrawings.UsedRange(worksheet);
        int firstRow = 0, firstColumn = 0, lastRow = usedRows, lastColumn = usedColumns;
        if (usePageSetup && printArea is { } area)
        {
            (firstRow, lastRow) = area.Row1 < 0 ? (0, usedRows) : (area.Row1, area.Row2);
            (firstColumn, lastColumn) = area.Column1 < 0 ? (0, usedColumns) : (area.Column1, area.Column2);
        }

        var titles = usePageSetup ? printTitleRows : null;
        var columnInfo = new XlsxColumns?[Math.Max(lastColumn + 1, 0)];
        foreach (var info in worksheet.Columns)
        {
            for (var c = info.First; c <= Math.Min(info.Last, lastColumn); c++)
            {
                columnInfo[c] = info;
            }
        }

        bool RowVisible(int r) => worksheet.Rows.TryGetValue(r, out var info) ? !info.Hidden : !worksheet.ZeroHeight;
        bool ColumnVisible(int c) => columnInfo[c] is not { } info || (!info.Hidden && info.Width != 0);

        var rows = new List<int>();
        if (titles is var (titleFirst, titleLast) && titleLast < firstRow)
        {
            rows.AddRange(Enumerable.Range(titleFirst, titleLast - titleFirst + 1).Where(RowVisible));
        }

        if (lastRow >= firstRow && lastColumn >= firstColumn)
        {
            rows.AddRange(Enumerable.Range(firstRow, lastRow - firstRow + 1).Where(RowVisible));
        }

        var columns = lastColumn >= firstColumn ? Enumerable.Range(firstColumn, lastColumn - firstColumn + 1).Where(ColumnVisible).ToList() : [];
        if (columns.Count == 0)
        {
            rows.Clear();
        }

        if ((long)rows.Count * columns.Count > maxCells)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), maxCells,
                $"The sheet '{entry.Name}' has more cells to print ({rows.Count} rows by {columns.Count} columns) than ResourceLimits.MaxTableCells allows ({maxCells}).");
        }

        var headerRows = 0;
        var leadRows = 0;
        if (titles is var (first, last))
        {
            headerRows = rows.Count(r => r >= first && r <= last);
            leadRows = headerRows > 0 ? rows.Count(r => r < first) : 0;
        }

        var rowArray = rows.ToArray();
        var columnArray = columns.ToArray();
        var grid = new SheetCell[rowArray.Length][];
        for (var r = 0; r < grid.Length; r++)
        {
            grid[r] = new SheetCell[columnArray.Length];
            Array.Fill(grid[r], SheetCell.Empty);
        }

        foreach (var (key, cell) in worksheet.Cells)
        {
            var r = Array.BinarySearch(rowArray, (int)(key >> 16));
            var c = Array.BinarySearch(columnArray, (int)(key & 0xFFFF));
            if (r >= 0 && c >= 0)
            {
                grid[r][c] = cell;
            }
        }

        var merges = Merges(worksheet, rowArray, columnArray, grid);
        var designed = worksheet.DefaultColumnWidth is not null || worksheet.Columns.Any(c => c.Width is not null);
        var defaultWidth = worksheet.DefaultColumnWidth ?? ((worksheet.BaseColumnWidth ?? 8) + ColumnPadding);
        return new Sheet(grid, headerRows, columnArray.Length)
        {
            Formatting = new SheetFormatting
            {
                ColumnWidths = options.AutoFit ? [] : [.. columnArray.Select(c => columnInfo[c]?.Width ?? (designed ? defaultWidth : (double?)null))],
                RowHeights = options.AutoFit ? [] : [.. rowArray.Select(r => worksheet.Rows.TryGetValue(r, out var info) ? info.Height : null)],
                Merges = merges,
                LeadRows = leadRows,
                DefaultFontSize = defaultFontSize,
                DefaultFontName = defaultFontName,
                Scale = usePageSetup && !worksheet.FitToPage && worksheet.Scale is { } scale ? Math.Clamp(scale, 10, 400) / 100.0 : 1,
                Landscape = usePageSetup && worksheet.Landscape,
                FitToWidth = usePageSetup && worksheet.FitToPage && worksheet.FitToWidth > 0,
                RightToLeft = worksheet.RightToLeft,
                Heading = options.ShowSheetNames ? entry.Name : null,
                Bookmark = bookmark ? entry.Name : null,
                Drawings = SheetDrawings.Map(worksheet, rowArray, columnArray, defaultWidth),
                MajorFont = themeFonts.Major,
                MinorFont = themeFonts.Minor,
                HeaderFooter = usePageSetup && !worksheet.HeaderFooter.IsEmpty ? worksheet.HeaderFooter : null,
                PrintContext = (options.PrintDate ?? DateTime.Now, options.Culture ?? CultureInfo.InvariantCulture, null),
                RowBreaks = usePageSetup ? Breaks(worksheet.RowBreaks, rowArray, headerRows + leadRows) : [],
                ColumnBreaks = usePageSetup ? Breaks(worksheet.ColumnBreaks, columnArray, 0) : [],
            },
            Name = entry.Name,
        };
    }

    /// <summary>
    /// The merged ranges in the printed rows and columns: each shows the value of its first cell (even where that cell is
    /// hidden), with the borders of the cells at its edges. Ranges that overlap one taken already are left out.
    /// </summary>
    private static List<CellMerge> Merges(XlsxWorksheet worksheet, int[] rows, int[] columns, SheetCell[][] grid)
    {
        var merges = new List<CellMerge>();
        if (worksheet.Merges.Count == 0 || rows.Length == 0)
        {
            return merges;
        }

        var taken = new HashSet<long>();
        foreach (var range in worksheet.Merges)
        {
            var (r1, r2) = Span(rows, range.Row1, range.Row2);
            var (c1, c2) = Span(columns, range.Column1, range.Column2);
            if (r1 > r2 || c1 > c2)
            {
                continue;
            }

            var free = true;
            for (var r = r1; r <= r2 && free; r++)
            {
                for (var c = c1; c <= c2 && free; c++)
                {
                    free = !taken.Contains(XlsxWorksheet.Key(r, c));
                }
            }

            if (!free)
            {
                continue;
            }

            SheetCell Raw(int row, int column) => worksheet.Cells.TryGetValue(XlsxWorksheet.Key(row, column), out var cell) ? cell : SheetCell.Empty;
            var anchor = Raw(range.Row1, range.Column1);
            var format = anchor.Format ?? CellFormat.Default;
            var right = (Raw(range.Row1, range.Column2).Format ?? CellFormat.Default).Right;
            var bottom = (Raw(range.Row2, range.Column1).Format ?? CellFormat.Default).Bottom;
            if (right != format.Right || bottom != format.Bottom)
            {
                format = format with { Right = right, Bottom = bottom };
            }

            grid[r1][c1] = anchor with { Format = format };
            if (r1 == r2 && c1 == c2)
            {
                continue;
            }

            for (var r = r1; r <= r2; r++)
            {
                for (var c = c1; c <= c2; c++)
                {
                    taken.Add(XlsxWorksheet.Key(r, c));
                }
            }

            merges.Add(new CellMerge(r1, c1, r2 - r1 + 1, c2 - c1 + 1));
        }

        return merges;
    }

    /// <summary>
    /// The printed rows or columns that page breaks set by hand start a page at: the first printed one at or after each
    /// break, after the first <paramref name="after"/> (the rows printed before the body); ascending, once each.
    /// </summary>
    internal static List<int> Breaks(IReadOnlyList<int> breaks, int[] printed, int after)
    {
        var result = new SortedSet<int>();
        foreach (var at in breaks)
        {
            var index = Array.BinarySearch(printed, at);
            index = index < 0 ? ~index : index;
            if (index > after && index < printed.Length)
            {
                result.Add(index);
            }
        }

        return [.. result];
    }

    /// <summary>The first and last index of the sorted values within [low, high] (first &gt; last if none).</summary>
    private static (int First, int Last) Span(int[] values, int low, int high)
    {
        var first = Array.BinarySearch(values, low);
        first = first < 0 ? ~first : first;
        var last = Array.BinarySearch(values, high);
        last = last < 0 ? ~last - 1 : last;
        return (first, last);
    }
}
