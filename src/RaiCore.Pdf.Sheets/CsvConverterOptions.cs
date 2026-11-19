using System.Text;

namespace RaiCore.Pdf.Sheets;

/// <summary>Options for <see cref="CsvToPdf"/>.</summary>
/// <remarks>
/// Options are immutable once created and can be shared by conversions running at the same time.
/// </remarks>
public sealed class CsvConverterOptions
{
    /// <summary>
    /// How the table is laid out: page size and orientation, font, gridlines, fitting to the page width, tagging and
    /// resource limits. Defaults to the <see cref="SheetOptions"/> defaults.
    /// </summary>
    public SheetOptions Sheet { get; init; } = new();

    /// <summary>
    /// The field delimiter, such as <c>','</c>, <c>';'</c>, <c>'\t'</c> or <c>'|'</c>. <c>null</c> (the default)
    /// detects it: a <c>sep=</c> first line (as Excel writes it) names it; otherwise the one of comma, semicolon, tab and
    /// vertical bar that splits the first records most consistently into more than one field. A <c>sep=</c> line is
    /// never shown as data.
    /// </summary>
    public char? Delimiter { get; init; }

    /// <summary>
    /// The text encoding of the data. <c>null</c> (the default) detects it: a byte order mark names UTF-8, UTF-16 or
    /// UTF-32 (and wins over this option too); otherwise UTF-16 if every other byte is zero, UTF-8 if the bytes are
    /// valid UTF-8, and Windows-1252 (which also reads ISO-8859-1 and ASCII) if they are not. Code page encodings are
    /// available from <see cref="CodePagesEncodingProvider.Instance"/>, also with invariant globalization.
    /// </summary>
    public Encoding? Encoding { get; init; }

    /// <summary>
    /// Whether the first record holds the column names: it is shown in bold and repeated at the top of every page.
    /// Defaults to <c>true</c>.
    /// </summary>
    public bool HasHeaderRow { get; init; } = true;

    /// <summary>
    /// The decimal separator of the numbers in the data (<c>'.'</c> in <c>1,234.56</c>, <c>','</c> in
    /// <c>1.234,56</c>), which tells numbers (right-aligned) from text. <c>null</c> (the default) detects it from the
    /// numbers in the data; where they do not tell, it is a comma in semicolon-delimited data and a point otherwise.
    /// Numbers are shown as written either way. For a culture's separator, pass
    /// <c>culture.NumberFormat.NumberDecimalSeparator[0]</c>.
    /// </summary>
    public char? DecimalSeparator { get; init; }
}
