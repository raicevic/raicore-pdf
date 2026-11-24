namespace RaiCore.Pdf.Sheets;

/// <summary>
/// Something in a sheet that the conversion left out or could not show as a spreadsheet program prints it (reported
/// through <see cref="SheetOptions.Diagnostics"/>).
/// </summary>
/// <param name="SheetName">The name of the sheet it concerns; <c>null</c> for CSV data, which has no sheets.</param>
/// <param name="Kind">What kind of content it is.</param>
/// <param name="Message">A description, with how many there are (for example <c>2 pictures are left out.</c>).</param>
public sealed record SheetDiagnostic(string? SheetName, SheetDiagnosticKind Kind, string Message);

/// <summary>
/// The kinds of <see cref="SheetDiagnostic"/>: each kind of content the conversion leaves out or shows simplified, and
/// layout problems. Each is reported at most once per sheet, with the count in its message.
/// </summary>
public enum SheetDiagnosticKind
{
    /// <summary>
    /// Charts in a worksheet that the chart engine cannot draw (newer chart types such as treemaps and waterfalls, the
    /// charts of Excel 97-2003 workbooks, charts whose part is missing); they are left out.
    /// </summary>
    Chart,

    /// <summary>A chart sheet that would be converted (visible, or named by the options); it is left out.</summary>
    ChartSheet,

    /// <summary>
    /// Pictures that cannot be drawn: in a format that cannot be read (such as PICT) or damaged, linked rather than
    /// embedded, or with more pixels than <see cref="ResourceLimits.MaxImagePixels"/> allows; they are left out.
    /// </summary>
    Picture,

    /// <summary>
    /// Shapes the converter cannot draw: SmartArt, ink, form controls (buttons, check boxes) and, in Excel 97-2003
    /// workbooks, free forms and the less common shape types; they are left out. (Shapes and text boxes with preset or
    /// free-form geometry are drawn.)
    /// </summary>
    Shape,

    /// <summary>Embedded and linked objects (OLE), such as a document shown as its icon, and ActiveX controls; they are left out.</summary>
    OleObject,

    /// <summary>Comments (notes and threaded comments); they are left out.</summary>
    Comment,

    /// <summary>
    /// Conditional formatting rules that are not applied: rules with formulas, data bars, icon sets, dates, and all the
    /// rules of Excel 97-2003 workbooks; cells keep their own format. (Rules comparing values, color scales, top and
    /// bottom, average, duplicate, text, blank and error rules of .xlsx workbooks are applied.)
    /// </summary>
    ConditionalFormatting,

    /// <summary>Sparklines (the small charts in cells); they are left out.</summary>
    Sparkline,

    /// <summary>Pivot tables; their cells are shown as they are stored, without the pivot table's style.</summary>
    PivotTable,

    /// <summary>Headers and footers. No longer reported: they are printed.</summary>
    HeaderFooter,

    /// <summary>Page breaks set by hand. No longer reported: they start pages (between rows) and bands (between columns).</summary>
    PageBreak,

    /// <summary>
    /// Cells of Excel 97-2003 workbooks with rich text (runs of text in different fonts); their text is shown in the
    /// cell's font. (Rich text of .xlsx workbooks is shown.)
    /// </summary>
    RichText,

    /// <summary>Cells with rotated text; their text is shown horizontally. (Vertically stacked text is shown.)</summary>
    RotatedText,

    /// <summary>Cells set to shrink their text to fit. No longer reported: such text is shown smaller to fit its column.</summary>
    ShrinkToFit,

    /// <summary>
    /// Content that does not fit where it is laid out: wider than the page (a column the sheet makes wider than the page,
    /// a number wider than its column) and drawn past its edge, or taller than a page and cut off.
    /// </summary>
    Layout,
}
