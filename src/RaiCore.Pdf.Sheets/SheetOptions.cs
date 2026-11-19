using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// How a sheet is laid out as a table on pages: page, orientation, font, gridlines, fitting, tagging and resource limits.
/// Shared by the converters of this package (<see cref="CsvConverterOptions.Sheet"/>).
/// </summary>
/// <remarks>
/// <para>
/// The table is as wide as its content needs, up to the page's content width. Each column gets the width of its widest
/// cell when they all fit. When they do not, narrow columns keep their width and the wide ones share the rest, wrapping
/// their text between words; only words wider than their column are broken, and numbers and dates are not broken at
/// all. Text is never clipped: when the columns would get too narrow to read (less than about six characters, or less
/// than their widest number), the columns are split into groups that each fit the page width, and the rows are printed
/// once for every group, one group after the other, each from a new page.
/// </para>
/// <para>Options are immutable once created and can be shared by conversions running at the same time.</para>
/// </remarks>
public sealed class SheetOptions
{
    /// <summary>The page size and margins. Defaults to A4 with 1.5 cm margins.</summary>
    public PageSetup Page { get; init; } = new(PdfPageSize.A4, Thickness.All(72 / 2.54 * 1.5));

    /// <summary>
    /// Whether the pages are in landscape orientation: the page size of <see cref="Page"/> with its longer side
    /// across. Defaults to <c>false</c> (the page as given).
    /// </summary>
    public bool Landscape { get; init; }

    /// <summary>
    /// Whether the text is made smaller, down to <see cref="MinimumFontSize"/>, so that the columns fit the page width
    /// without wrapping (as a spreadsheet's "fit all columns on one page"). Columns that still do not fit at the
    /// minimum size wrap as usual. Defaults to <c>false</c>.
    /// </summary>
    public bool FitToWidth { get; init; }

    /// <summary>The font size in points. Defaults to 9.</summary>
    public double FontSize { get; init; } = 9;

    /// <summary>The smallest font size <see cref="FitToWidth"/> uses, in points. Defaults to 6.</summary>
    public double MinimumFontSize { get; init; } = 6;

    /// <summary>
    /// The fonts <see cref="FontFamily"/> and <see cref="FallbackFontFamilies"/> are found in. Defaults to the bundled
    /// Liberation fonts only, so output is identical on every machine; add <see cref="FontCollection.AddSystemFonts"/>
    /// or your own font files for more families and scripts.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// The font family of the text (the header row in its bold face), or a generic family such as <c>sans-serif</c>
    /// (the default), <c>serif</c> or <c>monospace</c>.
    /// </summary>
    public string FontFamily { get; init; } = "sans-serif";

    /// <summary>
    /// Font families tried, in order, for characters <see cref="FontFamily"/> lacks (e.g. a CJK or Arabic font). The
    /// bundled Liberation Sans comes last. Defaults to none.
    /// </summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>Whether the cells have thin gray borders, as a spreadsheet's gridlines. Defaults to <c>true</c>.</summary>
    public bool Gridlines { get; init; } = true;

    /// <summary>The document's title (its metadata), or <c>null</c> (the default) for none.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Whether the PDF is tagged (accessible): the table with its header cells (<c>TH</c>, column scope) and data cells
    /// (<c>TD</c>). Defaults to <c>false</c>. For PDF/UA, also set <see cref="PdfDocument.PdfUA"/>, a title and
    /// <see cref="PdfDocument.Language"/> on the document.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>
    /// The limits that bound a conversion: <see cref="ResourceLimits.MaxTableCells"/> (rows times columns),
    /// <see cref="ResourceLimits.MaxPages"/> and <see cref="ResourceLimits.Timeout"/>. Defaults to
    /// <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;
}
