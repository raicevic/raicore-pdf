using System.Globalization;

namespace RaiCore.Pdf.Sheets;

/// <summary>Options for <see cref="XlsxToPdf"/>.</summary>
/// <remarks>
/// Options are immutable once created and can be shared by conversions running at the same time.
/// </remarks>
public sealed class XlsxConverterOptions
{
    /// <summary>
    /// How the sheets are laid out: page size and orientation, fonts, gridlines, fitting to the page width, tagging and
    /// resource limits. Defaults to the <see cref="SheetOptions"/> defaults. <see cref="SheetOptions.Landscape"/> and
    /// <see cref="SheetOptions.FitToWidth"/> apply to every sheet; a sheet's own page setup can turn them on as well
    /// (see <see cref="UsePageSetup"/>). <see cref="SheetOptions.FontSize"/> is not used: cells keep the workbook's
    /// font sizes.
    /// </summary>
    public SheetOptions Sheet { get; init; } = new();

    /// <summary>
    /// The names of the sheets to convert (compared ignoring case), or <c>null</c> (the default) for all visible
    /// worksheets and chart sheets. Named sheets are converted even if they are hidden. Together with <see cref="SheetIndexes"/>, the
    /// sheets either names are converted. Sheets are converted in the workbook's order.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown by the conversion when the workbook has no sheet of a name.</exception>
    public IReadOnlyList<string>? SheetNames { get; init; }

    /// <summary>
    /// The positions of the sheets to convert, from 0 in the workbook's order (hidden sheets included), or <c>null</c>
    /// (the default). See <see cref="SheetNames"/>.
    /// </summary>
    public IReadOnlyList<int>? SheetIndexes { get; init; }

    /// <summary>
    /// Whether each sheet's name is shown as a heading above its table. Defaults to <c>false</c>. When more than one
    /// sheet is converted, each also gets a bookmark either way.
    /// </summary>
    public bool ShowSheetNames { get; init; }

    /// <summary>
    /// Whether each sheet's page setup is used: its print area, the rows it repeats at the top of every page (print
    /// titles), landscape orientation, print scale and fitting to the page width. Defaults to <c>true</c>. The page
    /// size and margins always come from <see cref="SheetOptions.Page"/>.
    /// </summary>
    public bool UsePageSetup { get; init; } = true;

    /// <summary>
    /// Whether the column widths and row heights the sheets set are ignored, and every column and row is fitted to its
    /// content instead. Defaults to <c>false</c>: columns and rows the sheet sizes keep their size (a column is widened
    /// for a number that does not fit), and the others are fitted.
    /// </summary>
    public bool AutoFit { get; init; }

    /// <summary>
    /// The culture numbers and dates are shown in: its decimal and thousands separators, month and day names, AM and PM
    /// designators, short date (for the built-in date format) and currency symbol (for the built-in currency formats).
    /// <c>null</c> (the default) is the invariant culture, with ISO 8601 short dates (<c>2026-11-19</c>) and the dollar
    /// for the built-in currency formats, so that output is the same on every machine.
    /// </summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>
    /// The date and time the sheets' headers and footers print for <c>&amp;D</c> and <c>&amp;T</c>, in
    /// <see cref="Culture"/>'s short formats; <c>null</c> (the default) for the time of the conversion. Set it for
    /// output that is the same every time.
    /// </summary>
    public DateTime? PrintDate { get; init; }

    /// <summary>
    /// How the worksheets' comments (notes) are printed: as each sheet's page setup asks (the default: listed at the end
    /// of the sheet if it says so, otherwise not printed), always listed at the end, as PDF notes at their cells, or not
    /// at all. See <see cref="SheetComments"/>.
    /// </summary>
    public SheetComments Comments { get; init; }
}

/// <summary>How the comments (notes) of worksheets are printed (<see cref="XlsxConverterOptions.Comments"/>).</summary>
public enum SheetComments
{
    /// <summary>
    /// As the sheet's page setup asks: listed at the end of the sheet when it says so (Excel's "At end of sheet"),
    /// otherwise not printed. Comments shown on the sheet ("As displayed on sheet") are not drawn.
    /// </summary>
    AsPageSetup,

    /// <summary>Listed at the end of each sheet, on a page of their own: each comment's cell and text, as Excel prints them.</summary>
    AtEnd,

    /// <summary>
    /// As PDF notes (text annotations, "sticky notes") at the top right corner of their cells, which viewers open to show
    /// the text.
    /// </summary>
    Annotations,

    /// <summary>Not printed.</summary>
    None,
}
