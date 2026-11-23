using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Sheets;

/// <summary>The format a <see cref="Workbook"/> was read from.</summary>
public enum WorkbookFormat
{
    /// <summary>An Excel workbook (<c>.xlsx</c>, <c>.xlsm</c>, also strict Office Open XML), or a new workbook.</summary>
    Xlsx,

    /// <summary>An Excel 97-2003 workbook (<c>.xls</c>), read only.</summary>
    Xls,

    /// <summary>Delimited text (CSV, TSV), read only.</summary>
    Csv,
}

/// <summary>Options for <see cref="Workbook.Open(byte[], WorkbookOptions?)"/>.</summary>
/// <remarks>Options are immutable once created and can be shared.</remarks>
public sealed class WorkbookOptions
{
    /// <summary>
    /// The limits that bound reading the workbook and the work on it: <see cref="ResourceLimits.MaxDecompressedBytes"/>
    /// (all its parts together), <see cref="ResourceLimits.MaxElements"/> (the XML elements of all the parts read) and
    /// <see cref="ResourceLimits.MaxNestingDepth"/>, <see cref="ResourceLimits.MaxTableCells"/> (the cells of each
    /// sheet), and <see cref="ResourceLimits.Timeout"/> (each operation: opening, an insertion or deletion, saving).
    /// Defaults to <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// The field delimiter of delimited text, or <c>null</c> (the default) to detect it as <see cref="CsvToPdf"/>
    /// does: a first line <c>sep=;</c>, else the one of comma, semicolon, tab and vertical bar that splits the first
    /// records most consistently.
    /// </summary>
    public char? CsvDelimiter { get; init; }

    /// <summary>
    /// The encoding of delimited text, or <c>null</c> (the default) to detect it: a byte order mark, else UTF-16,
    /// UTF-8 or Windows-1252.
    /// </summary>
    public Encoding? CsvEncoding { get; init; }
}

/// <summary>How <see cref="Workbook.Find(string, FindOptions?)"/> compares a cell with the text it looks for.</summary>
public enum FindMatch
{
    /// <summary>The cell contains the text.</summary>
    Contains,

    /// <summary>The whole cell is the text (an exact match).</summary>
    WholeCell,

    /// <summary>The text is a regular expression (.NET syntax) that matches somewhere in the cell.</summary>
    Regex,
}

/// <summary>What <see cref="Workbook.Find(string, FindOptions?)"/> looks at in a cell.</summary>
public enum FindLookIn
{
    /// <summary>
    /// The value as displayed: numbers and dates in their number formats (in <see cref="FindOptions.Culture"/>),
    /// formulas as their last calculated values. Excel's "Look in: Values".
    /// </summary>
    Values,

    /// <summary>
    /// The formula (with its leading <c>=</c>) of a cell that has one, else the value as stored: text as it is,
    /// numbers in the invariant culture, dates as ISO 8601. Excel's "Look in: Formulas".
    /// </summary>
    Formulas,
}

/// <summary>Options for <see cref="Workbook.Find(string, FindOptions?)"/> and <see cref="Worksheet.Find(string, FindOptions?)"/>.</summary>
/// <remarks>Options are immutable once created and can be shared.</remarks>
public sealed class FindOptions
{
    /// <summary>How a cell is compared with the text. Defaults to <see cref="FindMatch.Contains"/>.</summary>
    public FindMatch Match { get; init; } = FindMatch.Contains;

    /// <summary>Whether upper and lower case must match. Defaults to <c>false</c>.</summary>
    public bool MatchCase { get; init; }

    /// <summary>What is looked at in a cell. Defaults to <see cref="FindLookIn.Values"/>.</summary>
    public FindLookIn LookIn { get; init; } = FindLookIn.Values;

    /// <summary>The culture displayed values are formatted in, or <c>null</c> (the default) for the invariant culture.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>
    /// How long one regular expression match may take (<see cref="FindMatch.Regex"/>). Defaults to one second.
    /// </summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>When <see cref="Workbook.SaveAsCsv(Stream, CsvSaveOptions?)"/> puts a field in double quotes.</summary>
public enum CsvQuoting
{
    /// <summary>
    /// Only when it must, as RFC 4180 describes: a field with the separator, a double quote, a line break, or spaces
    /// at its start or end. Quotes inside a field are doubled.
    /// </summary>
    Minimal,

    /// <summary>Every field that is not empty.</summary>
    Always,
}

/// <summary>Options for <see cref="Workbook.SaveAsCsv(Stream, CsvSaveOptions?)"/>.</summary>
/// <remarks>Options are immutable once created and can be shared.</remarks>
public sealed class CsvSaveOptions
{
    /// <summary>
    /// The sheet to write (compared ignoring case), or <c>null</c> (the default) for the active one: the sheet Excel
    /// shows when it opens the workbook (the first one if that is not a worksheet). A workbook without worksheets
    /// (only chart sheets) is written as no rows.
    /// </summary>
    public string? SheetName { get; init; }

    /// <summary>
    /// The field separator: any character but a double quote, a line break or a NUL, for example <c>';'</c> or
    /// <c>'\t'</c>. Defaults to a comma.
    /// </summary>
    public char Separator { get; init; } = ',';

    /// <summary>
    /// The text encoding. Defaults to UTF-8. Others: <see cref="Encoding.Unicode"/> (UTF-16), and Windows-1252 (what
    /// Excel writes on Western Windows systems) as <c>CodePagesEncodingProvider.Instance.GetEncoding(1252)</c>, which
    /// replaces characters it cannot encode with <c>?</c>.
    /// </summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Whether the text starts with the encoding's byte order mark (for UTF-8, so that Excel reads it as UTF-8).
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool ByteOrderMark { get; init; }

    /// <summary>When fields are put in double quotes. Defaults to <see cref="CsvQuoting.Minimal"/>.</summary>
    public CsvQuoting Quoting { get; init; } = CsvQuoting.Minimal;

    /// <summary>The line ending after each row. Defaults to CRLF (<c>"\r\n"</c>), as RFC 4180 has it.</summary>
    public string LineEnding { get; init; } = "\r\n";

    /// <summary>
    /// Whether values are written as displayed: numbers and dates in their number formats, in <see cref="Culture"/>
    /// (what Excel writes). Defaults to <c>true</c>. When <c>false</c>, values are written as stored, the same in
    /// every culture: numbers in the invariant culture with all their digits, dates as ISO 8601
    /// (<c>2026-11-23</c>, <c>2026-11-23T08:30:00</c>), booleans as <c>TRUE</c> and <c>FALSE</c>.
    /// </summary>
    public bool FormattedValues { get; init; } = true;

    /// <summary>
    /// The culture of formatted values (decimal separators, month names), or <c>null</c> (the default) for the
    /// invariant culture.
    /// </summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>Whether hidden rows are written. Defaults to <c>true</c>, as Excel does.</summary>
    public bool IncludeHiddenRows { get; init; } = true;

    /// <summary>Whether hidden columns are written. Defaults to <c>true</c>, as Excel does.</summary>
    public bool IncludeHiddenColumns { get; init; } = true;
}
