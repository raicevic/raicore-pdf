namespace RaiCore.Pdf.Sheets;

/// <summary>What a cell holds, which decides its default alignment: numbers and dates go right, text to the start.</summary>
internal enum CellKind
{
    /// <summary>Nothing.</summary>
    Empty,

    /// <summary>Text.</summary>
    Text,

    /// <summary>A number, as written (with its sign, separators, currency or percent sign).</summary>
    Number,

    /// <summary>A date, a time or both, as written.</summary>
    Date,
}

/// <summary>How a cell's text is aligned horizontally.</summary>
internal enum CellAlignment
{
    /// <summary>By its kind, as spreadsheets do: numbers and dates to the right, text to the start of its direction.</summary>
    General,

    /// <summary>To the left.</summary>
    Left,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>To the right.</summary>
    Right,
}

/// <summary>
/// A cell of a sheet: its text as shown, what it is, its alignment, and its format (font, fill, borders) where the
/// source has one (a worksheet; CSV cells have none).
/// </summary>
internal readonly record struct SheetCell(string Text, CellKind Kind, CellAlignment Alignment = CellAlignment.General, CellFormat? Format = null,
    IReadOnlyList<CellRun>? Runs = null)
{
    /// <summary>An empty cell.</summary>
    public static SheetCell Empty { get; } = new(string.Empty, CellKind.Empty);

    /// <summary>Whether the cell is shown to the right (numbers and dates, unless aligned otherwise).</summary>
    public bool IsRightAligned => Alignment == CellAlignment.Right
        || (Alignment == CellAlignment.General && Kind is CellKind.Number or CellKind.Date);
}

/// <summary>A run of a cell's rich text: its text, and its font (<c>null</c> for the cell's).</summary>
internal sealed record CellRun(string Text, CellFont? Font);

/// <summary>The font of a run of rich text: what it does not give (family, size, color) is the cell's.</summary>
internal sealed record CellFont(string? Name, double? Size, bool Bold, bool Italic, CellUnderline Underline, bool Strikethrough, PdfColor? Color)
{
    /// <summary>The cell's format with this font.</summary>
    public CellFormat Apply(CellFormat format) => format with
    {
        FontName = Name ?? format.FontName,
        FontSize = Size ?? format.FontSize,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough,
        Color = Color ?? format.Color,
    };
}

/// <summary>
/// A sheet of any source (a CSV file, a worksheet): rows of cells, the first <see cref="HeaderRows"/> of which repeat at
/// the top of every page. Rows may have fewer cells than <see cref="ColumnCount"/>; the rest are empty.
/// </summary>
internal sealed class Sheet
{
    public Sheet(IReadOnlyList<SheetCell[]> rows, int headerRows, int columnCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(headerRows);
        Rows = rows;
        HeaderRows = Math.Min(headerRows, rows.Count);
        ColumnCount = Math.Max(columnCount, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
    }

    /// <summary>
    /// How a formatted sheet (a worksheet) is laid out, or <c>null</c> for a sheet of text only (CSV): its header rows
    /// are then shown in bold on a gray background, and its columns fitted to the content.
    /// </summary>
    public SheetFormatting? Formatting { get; init; }

    /// <summary>The sheet's name (in diagnostics), or <c>null</c> for CSV data.</summary>
    public string? Name { get; init; }

    /// <summary>The rows, top to bottom; header rows first.</summary>
    public IReadOnlyList<SheetCell[]> Rows { get; }

    /// <summary>How many of the first rows are header rows.</summary>
    public int HeaderRows { get; }

    /// <summary>The number of columns: the most cells a row has.</summary>
    public int ColumnCount { get; }

    /// <summary>The cell at a row and column, empty where the row is shorter.</summary>
    public SheetCell this[int row, int column] => column < Rows[row].Length ? Rows[row][column] : SheetCell.Empty;
}
