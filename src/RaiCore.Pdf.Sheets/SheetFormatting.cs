using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets;

/// <summary>How a cell's text is underlined.</summary>
internal enum CellUnderline
{
    /// <summary>Not underlined.</summary>
    None,

    /// <summary>One line.</summary>
    Single,

    /// <summary>Two lines.</summary>
    Double,
}

/// <summary>One side of a cell's border: <see cref="Width"/> 0 for none.</summary>
internal readonly record struct CellBorder(double Width, PdfColor Color, BorderStyle Style = BorderStyle.Solid)
{
    /// <summary>No border.</summary>
    public static CellBorder None => default;

    /// <summary>Whether there is a line.</summary>
    public bool IsVisible => Width > 0;
}

/// <summary>
/// The format of a worksheet cell: its font, colors, borders, alignment and wrapping. Font sizes are in points at a
/// scale of 100%; <c>null</c> where the sheet's default applies.
/// </summary>
internal sealed record CellFormat
{
    /// <summary>The format of cells that have none of their own.</summary>
    public static CellFormat Default { get; } = new();

    /// <summary>The font's family name, as the workbook names it (<c>Calibri</c>, <c>Arial</c>), or <c>null</c>.</summary>
    public string? FontName { get; init; }

    /// <summary>The font size in points, or <c>null</c> for the sheet's default.</summary>
    public double? FontSize { get; init; }

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public CellUnderline Underline { get; init; }

    public bool Strikethrough { get; init; }

    /// <summary>The text color, or <c>null</c> for black.</summary>
    public PdfColor? Color { get; init; }

    /// <summary>The background, or <c>null</c> for none.</summary>
    public PdfColor? Fill { get; init; }

    public CellBorder Top { get; init; }

    public CellBorder Right { get; init; }

    public CellBorder Bottom { get; init; }

    public CellBorder Left { get; init; }

    /// <summary>The vertical alignment: spreadsheets put text at the bottom of its cell by default.</summary>
    public VerticalAlignment Vertical { get; init; } = VerticalAlignment.Bottom;

    /// <summary>Whether the text wraps within its column; otherwise it runs on over empty cells beside it.</summary>
    public bool Wrap { get; init; }

    /// <summary>The indent from the aligned side, in levels (each about three characters).</summary>
    public int Indent { get; init; }

    /// <summary>Whether the cell shows anything without text: a fill or a border.</summary>
    public bool IsVisible => Fill is not null || Top.IsVisible || Right.IsVisible || Bottom.IsVisible || Left.IsVisible;
}

/// <summary>A range of merged cells, in the sheet's rows and columns: shown as one cell.</summary>
internal readonly record struct CellMerge(int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>
/// The layout of a formatted sheet (a worksheet): column widths and row heights, merged cells, scale and page settings.
/// </summary>
internal sealed class SheetFormatting
{
    /// <summary>
    /// The width of each column in characters, as spreadsheets measure it (widths of the default font's digits, padding
    /// included), or <c>null</c> where the column is fitted to its content. Shorter than the sheet's columns where the
    /// rest are fitted.
    /// </summary>
    public IReadOnlyList<double?> ColumnWidths { get; init; } = [];

    /// <summary>The least height of each row in points at 100%, or <c>null</c> where it fits its content.</summary>
    public IReadOnlyList<double?> RowHeights { get; init; } = [];

    /// <summary>The merged cells, which must not overlap.</summary>
    public IReadOnlyList<CellMerge> Merges { get; init; } = [];

    /// <summary>
    /// How many of the first rows are printed only once, before the header rows (a worksheet's print titles that do not
    /// start at its first printed row).
    /// </summary>
    public int LeadRows { get; init; }

    /// <summary>The font size of cells without one, in points.</summary>
    public double DefaultFontSize { get; init; } = 11;

    /// <summary>The font family of cells without one, or <c>null</c> for <see cref="SheetOptions.FontFamily"/>.</summary>
    public string? DefaultFontName { get; init; }

    /// <summary>The print scale: 1 for 100%.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Whether the sheet asks to be printed in landscape orientation.</summary>
    public bool Landscape { get; init; }

    /// <summary>Whether the sheet asks to be shrunk to fit the page width.</summary>
    public bool FitToWidth { get; init; }

    /// <summary>Whether the columns run from right to left.</summary>
    public bool RightToLeft { get; init; }

    /// <summary>A heading shown above the table (the sheet's name), or <c>null</c>.</summary>
    public string? Heading { get; init; }

    /// <summary>A bookmark for the sheet's first page (its name), or <c>null</c>.</summary>
    public string? Bookmark { get; init; }
}
