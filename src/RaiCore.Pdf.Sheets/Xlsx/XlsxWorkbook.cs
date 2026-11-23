using System.Globalization;
using System.Text;
using System.Xml;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>
/// A sheet of a workbook: its name, part, position, whether it is hidden, and whether it is a worksheet or a chart sheet.
/// </summary>
internal sealed record XlsxSheetEntry(string Name, string? Path, int Index, bool Hidden, bool IsWorksheet, bool IsChartSheet = false);

/// <summary>A range of cells in a sheet, from 0; -1 where it is open (whole columns have no rows, whole rows no columns).</summary>
internal readonly record struct CellRange(int Row1, int Column1, int Row2, int Column2);

/// <summary>
/// The workbook part and what all its sheets share: the sheets, the date system, the shared strings, the styles (with
/// the theme's colors), the defined print areas and titles, and the title of the document properties.
/// </summary>
internal sealed class XlsxWorkbook
{
    /// <summary>The most rows a worksheet has.</summary>
    public const int MaxRows = 1_048_576;

    /// <summary>The most columns a worksheet has.</summary>
    public const int MaxColumns = 16_384;

    private readonly List<(int? SheetId, string Name, string Value)> _definedNames = [];
    private readonly Dictionary<(CellFormat, PdfColor), CellFormat> _colored = [];

    private XlsxWorkbook(FormatSettings settings) => Settings = settings;

    public List<XlsxSheetEntry> Sheets { get; } = [];

    public FormatSettings Settings { get; private set; }

    public XlsxStyles Styles { get; private set; } = null!;

    public List<string> SharedStrings { get; private set; } = [];

    /// <summary>The indexes of the shared strings with formatted runs (rich text, which is shown in the cell's font).</summary>
    public HashSet<int> RichStrings { get; } = [];

    /// <summary>The title of the document properties, or <c>null</c>.</summary>
    public string? Title { get; private set; }

    /// <summary>Reads the workbook part of a package and the parts it shares.</summary>
    /// <exception cref="NotSupportedException">The workbook is binary (<c>.xlsb</c>).</exception>
    /// <exception cref="SpreadsheetFormatException">The package has no workbook part.</exception>
    public static XlsxWorkbook Read(XlsxPackage package, CultureInfo culture)
    {
        var root = package.Relationships(string.Empty);
        var path = root.FirstOrDefault(r => r.Type == "officeDocument").Target ?? "xl/workbook.xml";
        if (path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The workbook is a binary workbook (.xlsb), which is not supported.");
        }

        if (!package.Contains(path))
        {
            throw new SpreadsheetFormatException("The package has no workbook part: it is not an XLSX workbook.");
        }

        var workbook = new XlsxWorkbook(new FormatSettings(culture, date1904: false));
        var core = root.FirstOrDefault(r => r.Type == "core-properties").Target ?? "docProps/core.xml";
        workbook.Title = package.Parse(core, ReadTitle, null);

        var relationships = package.Relationships(path);
        var date1904 = package.Parse(path, reader => workbook.ReadWorkbook(reader, relationships), false);
        workbook.Settings = new FormatSettings(culture, date1904);

        string? Part(string type) => relationships.FirstOrDefault(r => r.Type == type).Target;
        var theme = Part("theme") is { } themePath ? package.Parse(themePath, ReadTheme, null) : null;
        workbook.Styles = Part("styles") is { } stylesPath
            ? package.Parse(stylesPath, reader => XlsxStyles.Read(reader, workbook.Settings, theme), XlsxStyles.Empty(workbook.Settings))
            : XlsxStyles.Empty(workbook.Settings);
        workbook.SharedStrings = Part("sharedStrings") is { } stringsPath ? package.Parse(stringsPath, workbook.ReadSharedStrings, []) : [];
        return workbook;
    }

    /// <summary>Reads the sheets and defined names; returns whether the workbook uses the 1904 date system.</summary>
    private bool ReadWorkbook(XmlReader reader, List<(string Id, string Type, string Target)> relationships)
    {
        var date1904 = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "workbookPr" when reader.Depth == 1:
                    date1904 = reader.GetAttribute("date1904") is "1" or "true";
                    break;
                case "sheet" when reader.Depth == 2:
                    var id = RelationshipId(reader);
                    var relationship = relationships.FirstOrDefault(r => r.Id == id);
                    Sheets.Add(new XlsxSheetEntry(
                        reader.GetAttribute("name") ?? $"Sheet{Sheets.Count + 1}",
                        relationship.Target,
                        Sheets.Count,
                        reader.GetAttribute("state") is "hidden" or "veryHidden",
                        relationship.Type == "worksheet",
                        relationship.Type == "chartsheet"));
                    break;
                case "definedName" when reader.Depth == 2:
                    var name = reader.GetAttribute("name") ?? string.Empty;
                    int? sheet = int.TryParse(reader.GetAttribute("localSheetId"), NumberStyles.None, CultureInfo.InvariantCulture, out var local) ? local : null;
                    if (name is "_xlnm.Print_Area" or "_xlnm.Print_Titles")
                    {
                        _definedNames.Add((sheet, name, ReadText(reader)));
                    }

                    break;
            }
        }

        return date1904;
    }

    /// <summary>The value of the relationship id attribute (<c>r:id</c>, in whichever namespace the file uses).</summary>
    private static string? RelationshipId(XmlReader reader)
    {
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.LocalName == "id" && reader.NamespaceURI.Contains("relationships", StringComparison.Ordinal))
                {
                    var value = reader.Value;
                    reader.MoveToElement();
                    return value;
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        return null;
    }

    private static string? ReadTitle(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "title" && reader.Depth == 1)
            {
                var title = ReadText(reader).Trim();
                return title.Length == 0 ? null : title;
            }
        }

        return null;
    }

    /// <summary>The theme's colors in the order theme indexes name them: light 1, dark 1, light 2, dark 2, accents, links.</summary>
    private static List<uint>? ReadTheme(XmlReader reader)
    {
        string[] names = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
        var colors = new uint?[names.Length];
        var current = -1;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var index = Array.IndexOf(names, reader.LocalName);
            if (index >= 0)
            {
                current = index;
            }
            else if (current >= 0 && colors[current] is null && reader.LocalName is "srgbClr" or "sysClr"
                && (reader.GetAttribute(reader.LocalName == "srgbClr" ? "val" : "lastClr") is { } value)
                && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
            {
                colors[current] = rgb & 0xFFFFFF;
            }
            else if (reader.LocalName == "fontScheme")
            {
                break;
            }
        }

        return colors.Take(4).All(c => c is not null) ? [.. colors.Select(c => c ?? 0)] : null;
    }

    private List<string> ReadSharedStrings(XmlReader reader)
    {
        var strings = new List<string>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                strings.Add(ReadRichText(reader, out var rich));
                if (rich)
                {
                    RichStrings.Add(strings.Count - 1);
                }
            }
        }

        return strings;
    }

    /// <summary>
    /// The text of a string item (<c>si</c>, <c>is</c>): its <c>t</c> elements, of rich text runs too, without the
    /// phonetic runs (<c>rPh</c>), with <c>_xHHHH_</c> escapes decoded. Leaves the reader on the item's end.
    /// </summary>
    internal static string ReadRichText(XmlReader reader) => ReadRichText(reader, out _);

    /// <inheritdoc cref="ReadRichText(XmlReader)"/>
    /// <param name="reader">The reader, on the item's start.</param>
    /// <param name="rich">Whether a run of the item has its own font properties (<c>r/rPr</c>).</param>
    internal static string ReadRichText(XmlReader reader, out bool rich)
    {
        rich = false;
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        int textDepth = -1, skipDepth = -1;
        while (reader.Read() && reader.Depth > depth)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when skipDepth < 0 && !reader.IsEmptyElement:
                    if (reader.LocalName is "rPh" or "phoneticPr")
                    {
                        skipDepth = reader.Depth;
                    }
                    else if (reader.LocalName == "t")
                    {
                        textDepth = reader.Depth;
                    }
                    else if (reader.LocalName == "rPr" && reader.Depth == depth + 2)
                    {
                        rich = true;
                    }

                    break;
                case XmlNodeType.EndElement:
                    if (reader.Depth == skipDepth)
                    {
                        skipDepth = -1;
                    }
                    else if (reader.Depth == textDepth)
                    {
                        textDepth = -1;
                    }

                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace
                    when textDepth >= 0 && skipDepth < 0:
                    text.Append(reader.Value);
                    break;
            }
        }

        return Unescape(text.ToString());
    }

    /// <summary>The text content of the current element. Leaves the reader on its end.</summary>
    internal static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var depth = reader.Depth;
        var text = new StringBuilder();
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                text.Append(reader.Value);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The text with every surrogate that is not half of a pair (which escapes and binary strings can hold, but text
    /// layout cannot shape) replaced by U+FFFD, the replacement character.
    /// </summary>
    internal static string WithoutLoneSurrogates(string text)
    {
        var i = 0;
        while (i < text.Length && !char.IsSurrogate(text[i]))
        {
            i++;
        }

        if (i == text.Length)
        {
            return text;
        }

        var result = new StringBuilder(text.Length).Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(c).Append(text[++i]);
            }
            else
            {
                result.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            }
        }

        return result.ToString();
    }

    /// <summary>Decodes the <c>_xHHHH_</c> escapes of Office Open XML strings (control characters, <c>_x005F_</c> for an underscore).</summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains("_x", StringComparison.Ordinal))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_' && i + 6 < text.Length && text[i + 1] == 'x' && text[i + 6] == '_'
                && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            {
                result.Append((char)code);
                i += 6;
            }
            else
            {
                result.Append(text[i]);
            }
        }

        return WithoutLoneSurrogates(result.ToString());
    }

    /// <summary>The cell for a value of a type (<c>t</c>) in a cell style, formatted for display.</summary>
    public SheetCell Cell(string? type, string? value, string? inline, int styleIndex)
    {
        var style = Styles.CellStyle(styleIndex);
        var format = style.NumberFormat;
        var alignment = style.Alignment;
        FormattedValue formatted;
        switch (type)
        {
            case "s":
                var index = int.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var i) ? i : -1;
                formatted = format.FormatText(index >= 0 && index < SharedStrings.Count ? SharedStrings[index] : string.Empty);
                break;
            case "inlineStr":
                formatted = format.FormatText(inline ?? value ?? string.Empty);
                break;
            case "str":
                formatted = format.FormatText(Unescape(value ?? string.Empty));
                break;
            case "b":
                formatted = new FormattedValue(value?.Trim() is "1" or "true" ? "TRUE" : value?.Trim() is "0" or "false" ? "FALSE" : value ?? string.Empty, CellKind.Text);
                alignment = alignment == CellAlignment.General ? CellAlignment.Center : alignment;
                break;
            case "e":
                formatted = new FormattedValue(value ?? string.Empty, CellKind.Text);
                alignment = alignment == CellAlignment.General ? CellAlignment.Center : alignment;
                break;
            case "d":
                formatted = DateValue(value, format);
                break;
            default:
                if (string.IsNullOrWhiteSpace(value))
                {
                    formatted = new FormattedValue(inline ?? string.Empty, CellKind.Text);
                }
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                {
                    formatted = format.Format(number, Settings);
                }
                else
                {
                    formatted = new FormattedValue(value, CellKind.Text);
                }

                break;
        }

        return ToCell(formatted, style.Format, alignment, _colored);
    }

    /// <summary>
    /// The cell for a formatted value in a cell format: in the format's color where the number format gives one (the
    /// colored formats are kept in <paramref name="colored"/>, so that equal cells share them), empty without text.
    /// </summary>
    internal static SheetCell ToCell(FormattedValue formatted, CellFormat cellFormat, CellAlignment alignment, Dictionary<(CellFormat, PdfColor), CellFormat> colored)
    {
        if (formatted.Color is { } color)
        {
            if (!colored.TryGetValue((cellFormat, color), out var withColor))
            {
                withColor = cellFormat with { Color = color };
                colored[(cellFormat, color)] = withColor;
            }

            cellFormat = withColor;
        }

        var kind = formatted.Text.Length == 0 ? CellKind.Empty : formatted.Kind;
        return new SheetCell(formatted.Text, kind, alignment, cellFormat);
    }

    /// <summary>An ISO 8601 date value (<c>t="d"</c>, from strict files): as a serial date in the cell's format.</summary>
    private FormattedValue DateValue(string? value, NumberFormat format)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            return new FormattedValue(value ?? string.Empty, CellKind.Text);
        }

        double serial;
        if (Settings.Date1904)
        {
            serial = (date - new DateTime(1904, 1, 1)).TotalDays;
        }
        else
        {
            serial = (date - new DateTime(1899, 12, 30)).TotalDays;
            serial = serial < 61 ? serial - 1 : serial;
        }

        var shown = format.IsDate ? format : NumberFormat.Parse(date.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss");
        return shown.Format(serial, Settings);
    }

    /// <summary>The print area of a sheet, or <c>null</c>.</summary>
    public CellRange? PrintArea(XlsxSheetEntry sheet) => Areas(sheet, "_xlnm.Print_Area").Cast<CellRange?>().FirstOrDefault();

    /// <summary>The rows a sheet repeats at the top of every page (its print titles), or <c>null</c>.</summary>
    public (int First, int Last)? PrintTitleRows(XlsxSheetEntry sheet) =>
        Areas(sheet, "_xlnm.Print_Titles").Where(a => a.Column1 < 0 && a.Row1 >= 0).Select(a => ((int, int)?)(a.Row1, a.Row2)).FirstOrDefault();

    private IEnumerable<CellRange> Areas(XlsxSheetEntry sheet, string name)
    {
        foreach (var (sheetId, definedName, value) in _definedNames)
        {
            if (definedName != name || (sheetId is { } id && id != sheet.Index))
            {
                continue;
            }

            foreach (var (sheetName, range) in ParseAreas(value))
            {
                if (sheetId is not null ? sheetName is null || string.Equals(sheetName, sheet.Name, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(sheetName, sheet.Name, StringComparison.OrdinalIgnoreCase))
                {
                    yield return range;
                }
            }
        }
    }

    /// <summary>
    /// Parses a defined name's areas: <c>Sheet1!$A$1:$D$20</c>, <c>'My sheet'!$1:$2</c>, <c>Sheet1!$A:$B</c>, several
    /// separated by commas. Areas it cannot read (<c>#REF!</c>) are left out.
    /// </summary>
    internal static List<(string? Sheet, CellRange Range)> ParseAreas(string value)
    {
        var result = new List<(string?, CellRange)>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (i == value.Length || (value[i] == ',' && !quoted))
            {
                var area = value[start..i].Trim();
                start = i + 1;
                var bang = area.LastIndexOf('!');
                string? sheet = null;
                if (bang >= 0)
                {
                    sheet = area[..bang];
                    if (sheet.Length >= 2 && sheet[0] == '\'' && sheet[^1] == '\'')
                    {
                        sheet = sheet[1..^1].Replace("''", "'", StringComparison.Ordinal);
                    }

                    area = area[(bang + 1)..];
                }

                if (ParseRange(area) is { } range)
                {
                    result.Add((sheet, range));
                }
            }
        }

        return result;
    }

    /// <summary>Parses <c>$A$1:$D$20</c>, <c>A1</c>, <c>$A:$D</c> (whole columns) or <c>$1:$3</c> (whole rows).</summary>
    internal static CellRange? ParseRange(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var first = ParseReference(colon < 0 ? text : text.AsSpan(0, colon));
        var second = colon < 0 ? first : ParseReference(text.AsSpan(colon + 1));
        if (first is not var (r1, c1) || second is not var (r2, c2) || (r1 < 0) != (r2 < 0) || (c1 < 0) != (c2 < 0))
        {
            return null;
        }

        return new CellRange(Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2));
    }

    /// <summary>A reference's row and column from 0, -1 where it has none (<c>$A</c>, <c>$1</c>); <c>null</c> if invalid.</summary>
    internal static (int Row, int Column)? ParseReference(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        var i = 0;
        if (i < text.Length && text[i] == '$')
        {
            i++;
        }

        var column = 0;
        var letters = 0;
        while (i < text.Length && char.IsAsciiLetter(text[i]) && letters < 4)
        {
            column = (column * 26) + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            letters++;
            i++;
        }

        if (i < text.Length && text[i] == '$')
        {
            i++;
        }

        var row = 0;
        var digits = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]) && digits < 8)
        {
            row = (row * 10) + (text[i] - '0');
            digits++;
            i++;
        }

        if (i != text.Length || (letters == 0 && digits == 0) || column > MaxColumns || row > MaxRows || (digits > 0 && row == 0))
        {
            return null;
        }

        return (digits == 0 ? -1 : row - 1, letters == 0 ? -1 : column - 1);
    }
}
