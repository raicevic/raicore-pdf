using System.Globalization;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Sheets.Drawings;

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

    /// <summary>Whether text too wide for its column is shown smaller, to fit (unless it wraps).</summary>
    public bool ShrinkToFit { get; init; }

    /// <summary>Whether the text is stacked vertically, one character under the other.</summary>
    public bool Stacked { get; init; }

    /// <summary>The text's rotation in degrees, counterclockwise (-90 to 90; 0 for none).</summary>
    public int Rotation { get; init; }

    /// <summary>A data bar of conditional formatting drawn behind the text, or <c>null</c>.</summary>
    public CellDataBar? DataBar { get; init; }

    /// <summary>An icon of conditional formatting drawn at the start of the cell, or <c>null</c>.</summary>
    public CellIcon? Icon { get; init; }

    /// <summary>Whether the text is hidden (a data bar or icon set that shows no value).</summary>
    public bool HideText { get; init; }

    /// <summary>Whether the cell shows anything without text: a fill, a border, a data bar or an icon.</summary>
    public bool IsVisible => Fill is not null || Top.IsVisible || Right.IsVisible || Bottom.IsVisible || Left.IsVisible || DataBar is not null || Icon is not null;
}

/// <summary>
/// A data bar (conditional formatting): a bar from <see cref="Start"/> to <see cref="End"/>, fractions of the cell's width
/// from its left, in a color (fading to white along a gradient bar), with a border or not.
/// </summary>
internal sealed record CellDataBar(double Start, double End, PdfColor Color, bool Gradient, PdfColor? Border);

/// <summary>An icon of an icon set (conditional formatting): the set's name (<c>3TrafficLights1</c>) and the icon's index, from 0 for the lowest values.</summary>
internal sealed record CellIcon(string Set, int Index);

/// <summary>
/// A comment (note) of a worksheet: its cell's reference as the sheet names it (<c>B3</c>), its row and column in the
/// sheet as printed, its text and its author.
/// </summary>
internal sealed record SheetComment(string Reference, int Row, int Column, string Text, string? Author);

/// <summary>How a sheet prints its comments.</summary>
internal enum CommentPrinting
{
    /// <summary>Not at all.</summary>
    None,

    /// <summary>Listed after the sheet.</summary>
    AtEnd,

    /// <summary>As PDF notes at their cells.</summary>
    Notes,
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

    /// <summary>The drawings (pictures, shapes, charts), anchored to the sheet's rows and columns, in z-order.</summary>
    public IReadOnlyList<SheetDrawing> Drawings { get; init; } = [];

    /// <summary>The theme's font for headings (<c>+mj-lt</c>), or <c>null</c>.</summary>
    public string? MajorFont { get; init; }

    /// <summary>The theme's font for body text (<c>+mn-lt</c>), or <c>null</c>.</summary>
    public string? MinorFont { get; init; }

    /// <summary>The headers and footers, or <c>null</c> for none.</summary>
    public SheetHeaderFooter? HeaderFooter { get; init; }

    /// <summary>The date, time and culture headers and footers print, and the file name (<c>&amp;F</c>), if known.</summary>
    public (DateTime Date, CultureInfo Culture, string? File) PrintContext { get; init; } = (DateTime.MinValue, CultureInfo.InvariantCulture, null);

    /// <summary>The printed rows (after the header rows) that start a page, ascending, by page breaks set by hand.</summary>
    public IReadOnlyList<int> RowBreaks { get; init; } = [];

    /// <summary>The comments of the printed cells, by row and column, and how they are printed.</summary>
    public (IReadOnlyList<SheetComment> List, CommentPrinting Printing) Comments { get; init; } = ([], CommentPrinting.None);

    /// <summary>The printed columns (not the first) that start a page, ascending, by page breaks set by hand.</summary>
    public IReadOnlyList<int> ColumnBreaks { get; init; } = [];
}
