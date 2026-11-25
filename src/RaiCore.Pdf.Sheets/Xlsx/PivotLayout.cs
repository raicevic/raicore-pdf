namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// A line of a pivot table's row or column area (a row item, <c>i</c>, or the binary format's <c>SXLIItem</c>): its kind
/// (<c>data</c>, a subtotal's function such as <c>default</c> or <c>sum</c>, <c>grand</c> or <c>blank</c>), how many of
/// its leading items repeat the line before's (<c>r</c>, <c>cSic</c>), and how many items it shows.
/// </summary>
internal readonly record struct PivotLine(string Kind, int Repeated, int Items)
{
    /// <summary>The level of the line's last item: 0 for the outermost field.</summary>
    public int Level => Math.Max(0, Repeated + Math.Max(Items, 1) - 1);

    /// <summary>Whether the line is a subtotal (of any function).</summary>
    public bool IsSubtotal => Kind is not ("data" or "grand" or "blank");
}

/// <summary>
/// Where a pivot table's parts are, as both workbook formats store it: its range (the location, page fields not
/// included), the offsets of its first header row, first data row and first data column in it, the counts of its row,
/// column and page fields, the lines of its row and column areas, and which style elements its style info shows.
/// </summary>
internal sealed record PivotLayout(CellRange Range, int FirstHeaderRow, int FirstDataRow, int FirstDataColumn)
{
    public int RowFields { get; init; }

    public int ColumnFields { get; init; }

    public int PageFields { get; init; }

    public IReadOnlyList<PivotLine> Rows { get; init; } = [];

    public IReadOnlyList<PivotLine> Columns { get; init; } = [];

    public bool ShowRowHeaders { get; init; } = true;

    public bool ShowColumnHeaders { get; init; } = true;

    public bool ShowRowStripes { get; init; }

    public bool ShowColumnStripes { get; init; }

    public bool ShowLastColumn { get; init; } = true;
}

/// <summary>
/// Applies a pivot table style to the cells of a pivot table: each element of the style over its region of the table
/// in the order of precedence (later ones over earlier ones), its font, fill and borders (the outer borders on the
/// region's edges, the inner ones between its rows and columns) over the cells' own formats.
/// </summary>
internal static class PivotStyler
{
    /// <summary>The style elements of pivot tables in the order they are applied.</summary>
    internal static readonly string[] Order =
    [
        "wholeTable", "pageFieldLabels", "pageFieldValues", "firstColumnStripe", "secondColumnStripe", "firstRowStripe",
        "secondRowStripe", "blankRow", "firstColumn", "headerRow", "firstHeaderCell", "firstColumnSubheading",
        "secondColumnSubheading", "thirdColumnSubheading", "firstRowSubheading", "secondRowSubheading", "thirdRowSubheading",
        "firstSubtotalColumn", "secondSubtotalColumn", "thirdSubtotalColumn", "firstSubtotalRow", "secondSubtotalRow",
        "thirdSubtotalRow", "lastColumn", "totalRow",
    ];

    private static readonly string[] Levels = ["first", "second", "third"];

    /// <summary>What the style elements set on a cell, layered.</summary>
    private sealed class Layer
    {
        public bool? Bold;
        public bool? Italic;
        public bool? Underline;
        public bool? Strikethrough;
        public PdfColor? Color;
        public PdfColor? Fill;
        public CellBorder? Top;
        public CellBorder? Right;
        public CellBorder? Bottom;
        public CellBorder? Left;
    }

    /// <summary>
    /// Applies <paramref name="style"/> to the pivot table's cells in <paramref name="sheet"/>; <c>false</c> when the
    /// table is larger than <paramref name="maxCells"/> allows.
    /// </summary>
    public static bool Apply(PivotLayout layout, IReadOnlyDictionary<string, PivotStyleFormat> style, XlsxWorksheet sheet, long maxCells)
    {
        var range = layout.Range;
        if ((long)(range.Row2 - range.Row1 + 1) * (range.Column2 - range.Column1 + 1) > maxCells)
        {
            return false;
        }

        var layers = new Dictionary<(int Row, int Column), Layer>();
        var regions = Regions(layout).ToLookup(r => r.Element);
        foreach (var element in Order)
        {
            if (!style.TryGetValue(element, out var format))
            {
                continue;
            }

            foreach (var (_, row1, column1, row2, column2) in regions[element])
            {
                for (var row = Math.Max(row1, 0); row <= row2; row++)
                {
                    for (var column = Math.Max(column1, 0); column <= column2; column++)
                    {
                        if (!layers.TryGetValue((row, column), out var layer))
                        {
                            layers[(row, column)] = layer = new Layer();
                        }

                        var f = format.Format;
                        layer.Bold = f.Bold ?? layer.Bold;
                        layer.Italic = f.Italic ?? layer.Italic;
                        layer.Underline = f.Underline is { } u ? u != CellUnderline.None : layer.Underline;
                        layer.Strikethrough = f.Strikethrough ?? layer.Strikethrough;
                        layer.Color = f.Color ?? layer.Color;
                        layer.Fill = f.Fill ?? layer.Fill;
                        layer.Top = (row == row1 ? f.Top : format.Horizontal) ?? layer.Top;
                        layer.Bottom = (row == row2 ? f.Bottom : format.Horizontal) ?? layer.Bottom;
                        layer.Left = (column == column1 ? f.Left : format.Vertical) ?? layer.Left;
                        layer.Right = (column == column2 ? f.Right : format.Vertical) ?? layer.Right;
                    }
                }
            }
        }

        foreach (var ((row, column), layer) in layers)
        {
            var key = XlsxWorksheet.Key(row, column);
            var cell = sheet.Cells.TryGetValue(key, out var found) ? found : SheetCell.Empty;
            var format = cell.Format ?? CellFormat.Default;
            format = format with
            {
                Bold = layer.Bold ?? format.Bold,
                Italic = layer.Italic ?? format.Italic,
                Underline = layer.Underline is { } underline ? (underline ? CellUnderline.Single : CellUnderline.None) : format.Underline,
                Strikethrough = layer.Strikethrough ?? format.Strikethrough,
                Color = layer.Color ?? format.Color,
                Fill = layer.Fill ?? format.Fill,
                Top = layer.Top ?? format.Top,
                Right = layer.Right ?? format.Right,
                Bottom = layer.Bottom ?? format.Bottom,
                Left = layer.Left ?? format.Left,
            };
            sheet.Add(row, column, cell with { Format = format }, maxCells);
        }

        return true;
    }

    /// <summary>The regions of the table's style elements: each element with its rows and columns (inclusive).</summary>
    internal static IEnumerable<(string Element, int Row1, int Column1, int Row2, int Column2)> Regions(PivotLayout layout)
    {
        var (top, left, bottom, right) = (layout.Range.Row1, layout.Range.Column1, layout.Range.Row2, layout.Range.Column2);
        var dataRow = Math.Clamp(top + layout.FirstDataRow, top, bottom + 1);
        var dataColumn = Math.Clamp(left + layout.FirstDataColumn, left, right + 1);
        var headerRow = Math.Clamp(top + layout.FirstHeaderRow, top, dataRow);
        var grandColumn = layout.ShowLastColumn && layout.Columns.Count > 0 && layout.Columns[^1].Kind == "grand";

        yield return ("wholeTable", top, left, bottom, right);

        // Page fields: a field a row above the table (and a blank row), its name in the first column, its item in the next.
        for (var i = 0; i < Math.Min(layout.PageFields, 1000); i++)
        {
            var row = top - layout.PageFields - 1 + i;
            if (row >= 0)
            {
                yield return ("pageFieldLabels", row, left, row, left);
                yield return ("pageFieldValues", row, left + 1, row, left + 1);
            }
        }

        if (layout.ShowColumnHeaders && dataRow > top)
        {
            yield return ("headerRow", top, left, dataRow - 1, right);
            yield return ("firstHeaderCell", top, left, top, left);
        }

        if (layout.ShowRowHeaders && dataColumn > left && dataRow <= bottom)
        {
            yield return ("firstColumn", dataRow, left, bottom, dataColumn - 1);
        }

        // The row area's lines, from the first data row down.
        var stripe = 0;
        for (var i = 0; i < layout.Rows.Count && dataRow + i <= bottom; i++)
        {
            var line = layout.Rows[i];
            var row = dataRow + i;
            var level = Levels[Math.Min(line.Level, 2)];
            switch (line.Kind)
            {
                case "grand":
                    yield return ("totalRow", row, left, row, right);
                    continue;
                case "blank":
                    yield return ("blankRow", row, left, row, right);
                    continue;
            }

            if (layout.ShowRowStripes)
            {
                yield return (stripe++ % 2 == 0 ? "firstRowStripe" : "secondRowStripe", row, left, row, right);
            }

            if (line.IsSubtotal)
            {
                yield return ($"{level}SubtotalRow", row, left, row, right);
            }
            else if (layout.ShowRowHeaders && line.Level < layout.RowFields - 1)
            {
                yield return ($"{level}RowSubheading", row, left, row, right);
            }
        }

        // The column area's lines, from the first data column right; their header rows are the column fields' rows.
        stripe = 0;
        for (var i = 0; i < layout.Columns.Count && dataColumn + i <= right; i++)
        {
            var line = layout.Columns[i];
            var column = dataColumn + i;
            if (line.Kind == "grand")
            {
                if (grandColumn)
                {
                    yield return ("lastColumn", headerRow, column, bottom, column);
                }

                continue;
            }

            if (layout.ShowColumnStripes && dataRow <= bottom)
            {
                yield return (stripe++ % 2 == 0 ? "firstColumnStripe" : "secondColumnStripe", dataRow, column, bottom, column);
            }

            if (line.IsSubtotal && line.Kind != "blank")
            {
                yield return ($"{Levels[Math.Min(line.Level, 2)]}SubtotalColumn", headerRow, column, bottom, column);
            }
        }

        if (layout.ShowColumnHeaders)
        {
            for (var level = 0; level < Math.Min(layout.ColumnFields - 1, 3); level++)
            {
                var row = headerRow + level;
                if (row < dataRow && dataColumn <= right)
                {
                    yield return ($"{Levels[level]}ColumnSubheading", row, dataColumn, row, right);
                }
            }
        }
    }
}
