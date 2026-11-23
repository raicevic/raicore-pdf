using System.Globalization;
using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Workbooks;

/// <summary>The part names, content types and relationship types of the workbook parts this API reads and writes.</summary>
internal static class WorkbookNames
{
    public const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string StrictMain = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    public const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string StrictRelationships = "http://purl.oclc.org/ooxml/officeDocument/relationships";
    public const string WorkbookType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string WorksheetType = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";
    public const string StylesType = "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml";
    public const string SharedStringsType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml";

    /// <summary>The value of an attribute in the relationships namespace (<c>r:id</c>), strict or transitional.</summary>
    public static string? RelationshipId(XElement element) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Contains("relationships", StringComparison.Ordinal))?.Value;

    /// <summary>An integer attribute, or <c>null</c>.</summary>
    public static int? Integer(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>A boolean attribute (<c>1</c> or <c>true</c>).</summary>
    public static bool Flag(XElement element, string name) => (string?)element.Attribute(name) is "1" or "true";

    /// <summary>
    /// Escapes text for a string item: characters XML cannot hold as <c>_xHHHH_</c>, and text that looks like such an
    /// escape with an escaped underscore (<c>_x005F_</c>), as Excel does.
    /// </summary>
    public static string Escape(string text)
    {
        StringBuilder? result = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? replacement = null;
            if ((c < 0x20 && c is not '\t' and not '\n' and not '\r') || c is '\uFFFE' or '\uFFFF'
                || (char.IsHighSurrogate(c) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                || (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))))
            {
                replacement = "_x" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + "_";
            }
            else if (c == '_' && i + 6 < text.Length && text[i + 1] == 'x' && text[i + 6] == '_'
                && ushort.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            {
                replacement = "_x005F_";
            }

            if (replacement is not null)
            {
                result ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
                result.Append(replacement);
            }
            else
            {
                result?.Append(c);
            }
        }

        return result?.ToString() ?? text;
    }

    /// <summary>A <c>t</c> element for text, keeping its spaces.</summary>
    public static XElement TextElement(XNamespace ns, string text)
    {
        var escaped = Escape(text);
        var element = new XElement(ns + "t", escaped);
        if (escaped.Length > 0 && (char.IsWhiteSpace(escaped[0]) || char.IsWhiteSpace(escaped[^1]) || escaped.Contains('\n', StringComparison.Ordinal)))
        {
            element.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }

        return element;
    }

    /// <summary>The text of a string item (<c>si</c>, <c>is</c>): its <c>t</c> elements without the phonetic runs, unescaped.</summary>
    public static string Text(XElement item)
    {
        var direct = item.Elements().Where(e => e.Name.LocalName == "t").ToList();
        if (direct.Count == 1 && !item.Elements().Any(e => e.Name.LocalName == "r"))
        {
            return XlsxWorkbook.Unescape(direct[0].Value);
        }

        var text = new StringBuilder();
        foreach (var t in item.Descendants().Where(e => e.Name.LocalName == "t"))
        {
            if (!t.Ancestors().TakeWhile(a => a != item).Any(a => a.Name.LocalName is "rPh" or "phoneticPr"))
            {
                text.Append(t.Value);
            }
        }

        return XlsxWorkbook.Unescape(text.ToString());
    }

    /// <summary>
    /// Inserts an element among a parent's children where the schema's order puts it: after the last child that comes
    /// before it in <paramref name="order"/>.
    /// </summary>
    public static void InsertInOrder(XElement parent, XElement element, string[] order)
    {
        var position = Array.IndexOf(order, element.Name.LocalName);
        XElement? after = null;
        foreach (var child in parent.Elements())
        {
            var index = Array.IndexOf(order, child.Name.LocalName);
            if (index >= 0 && index <= position)
            {
                after = child;
            }
            else if (index > position)
            {
                break;
            }
        }

        if (after is not null)
        {
            after.AddAfterSelf(element);
        }
        else if (parent.Elements().FirstOrDefault(e => Array.IndexOf(order, e.Name.LocalName) > position) is { } before)
        {
            before.AddBeforeSelf(element);
        }
        else
        {
            parent.Add(element);
        }
    }
}

/// <summary>
/// The shared strings of a workbook: their texts, read once, and new strings appended (an existing plain string with
/// the same text is reused). The part is created when the workbook has none.
/// </summary>
internal sealed class SharedStringTable
{
    private readonly List<string> _texts = [];
    private readonly Dictionary<string, int> _plain = new(StringComparer.Ordinal);
    private readonly Func<(string Part, XDocument Document)> _create;
    private readonly WorkbookPackage _package;
    private string? _part;
    private XDocument? _document;

    public SharedStringTable(WorkbookPackage package, string? part, Func<(string Part, XDocument Document)> create)
    {
        _package = package;
        _create = create;
        _part = part;
        _document = part is null ? null : package.Xml(part);
        if (_document?.Root is not { } root)
        {
            return;
        }

        foreach (var item in root.Elements())
        {
            if (item.Name.LocalName != "si")
            {
                continue;
            }

            var text = WorkbookNames.Text(item);
            if (item.Elements().Count() == 1 && item.Elements().First().Name.LocalName == "t")
            {
                _plain.TryAdd(text, _texts.Count);
            }

            _texts.Add(text);
        }
    }

    public int Count => _texts.Count;

    /// <summary>The text of a string, or <c>null</c> for an index out of range.</summary>
    public string? this[int index] => index >= 0 && index < _texts.Count ? _texts[index] : null;

    /// <summary>The index of a string with the text, appended if there is none.</summary>
    public int Add(string text)
    {
        if (_plain.TryGetValue(text, out var index))
        {
            UpdateCounts(1);
            return index;
        }

        if (_document?.Root is null)
        {
            (_part, _document) = _create();
        }

        var root = _document.Root!;
        root.Add(new XElement(root.Name.Namespace + "si", WorkbookNames.TextElement(root.Name.Namespace, text)));
        index = _texts.Count;
        _texts.Add(text);
        _plain[text] = index;
        UpdateCounts(1);
        return index;
    }

    /// <summary>Updates the counts of the part (references and unique strings) and marks it changed.</summary>
    private void UpdateCounts(int references)
    {
        var root = _document!.Root!;
        if (WorkbookNames.Integer(root, "count") is { } count)
        {
            root.SetAttributeValue("count", Math.Max(count + references, _texts.Count));
        }

        if (root.Attribute("uniqueCount") is not null || root.Attribute("count") is not null)
        {
            root.SetAttributeValue("uniqueCount", _texts.Count);
        }

        _package.MarkDirty(_part!);
    }
}

/// <summary>
/// The cell styles of a workbook as far as values need them: the number format of each cell style (<c>cellXfs</c>),
/// and new cell styles that are an existing one with another number format. The part is created when the workbook has
/// none.
/// </summary>
internal sealed class WorkbookStyles
{
    /// <summary>The first id of custom number formats.</summary>
    private const int FirstCustomFormat = 164;

    private readonly WorkbookPackage _package;
    private readonly Func<(string Part, XDocument Document)> _create;
    private readonly Dictionary<int, string> _numberFormats = [];
    private readonly Dictionary<string, NumberFormat> _parsed = new(StringComparer.Ordinal);
    private List<XElement> _xfs = [];
    private string? _part;
    private XDocument? _document;

    public WorkbookStyles(WorkbookPackage package, string? part, Func<(string Part, XDocument Document)> create)
    {
        _package = package;
        _create = create;
        _part = part;
        _document = part is null ? null : package.Xml(part);
        Read();
    }

    private void Read()
    {
        _xfs = [];
        _numberFormats.Clear();
        if (_document?.Root is not { } root)
        {
            return;
        }

        foreach (var section in root.Elements())
        {
            switch (section.Name.LocalName)
            {
                case "numFmts":
                    foreach (var format in section.Elements().Where(e => e.Name.LocalName == "numFmt"))
                    {
                        if (WorkbookNames.Integer(format, "numFmtId") is { } id && (string?)format.Attribute("formatCode") is { } code)
                        {
                            _numberFormats[id] = code;
                        }
                    }

                    break;
                case "cellXfs":
                    _xfs = [.. section.Elements().Where(e => e.Name.LocalName == "xf")];
                    break;
            }
        }
    }

    /// <summary>The number format id of a cell style (0, General, for one out of range).</summary>
    public int NumberFormatId(int style) => style >= 0 && style < _xfs.Count ? WorkbookNames.Integer(_xfs[style], "numFmtId") ?? 0 : 0;

    /// <summary>The code of a number format: custom, built in (in the settings' culture), or General.</summary>
    public string FormatCode(int id, FormatSettings settings) =>
        _numberFormats.TryGetValue(id, out var code) ? code : NumberFormat.BuiltInCode(id, settings) ?? "General";

    /// <summary>The number format of a cell style in a culture.</summary>
    public NumberFormat Format(int style, FormatSettings settings)
    {
        var code = FormatCode(NumberFormatId(style), settings);
        if (!_parsed.TryGetValue(code, out var format))
        {
            format = NumberFormat.Parse(code);
            if (_parsed.Count < 4096)
            {
                _parsed[code] = format;
            }
        }

        return format;
    }

    /// <summary>The id of a number format code: a custom format with the code, a built-in one, or a new custom one.</summary>
    public int NumberFormatId(string code, FormatSettings invariant)
    {
        foreach (var (id, existing) in _numberFormats)
        {
            if (existing == code)
            {
                return id;
            }
        }

        for (var id = 0; id <= 49; id++)
        {
            // Only the formats that do not depend on the culture (not dates, not currencies).
            if (id is not (>= 5 and <= 8) and not 14 and not 22 and not (>= 27 and <= 36) and not 42 and not 44
                && NumberFormat.BuiltInCode(id, invariant) == code)
            {
                return id;
            }
        }

        var document = Document();
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var formats = root.Element(ns + "numFmts");
        if (formats is null)
        {
            formats = new XElement(ns + "numFmts");
            root.AddFirst(formats);
        }

        var newId = Math.Max(FirstCustomFormat, _numberFormats.Count == 0 ? 0 : _numberFormats.Keys.Max() + 1);
        formats.Add(new XElement(ns + "numFmt", new XAttribute("numFmtId", newId), new XAttribute("formatCode", code)));
        formats.SetAttributeValue("count", formats.Elements(ns + "numFmt").Count());
        _numberFormats[newId] = code;
        _package.MarkDirty(_part!);
        return newId;
    }

    /// <summary>
    /// The index of a cell style that is a style (its font, fill, borders, alignment, protection) with another number
    /// format: an existing one that is the same, or a new one appended.
    /// </summary>
    public int WithNumberFormat(int style, int numberFormatId)
    {
        if (NumberFormatId(style) == numberFormatId && style >= 0 && style < _xfs.Count)
        {
            return style;
        }

        var document = Document();
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var cellXfs = root.Element(ns + "cellXfs");
        if (cellXfs is null)
        {
            cellXfs = new XElement(ns + "cellXfs", new XElement(ns + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0),
                new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0)));
            WorkbookNames.InsertInOrder(root, cellXfs, StyleOrder);
            _xfs = [.. cellXfs.Elements(ns + "xf")];
        }

        var source = style >= 0 && style < _xfs.Count ? _xfs[style] : _xfs.Count > 0 ? _xfs[0] : new XElement(ns + "xf");
        var xf = new XElement(source);
        xf.SetAttributeValue("numFmtId", numberFormatId);
        xf.SetAttributeValue("applyNumberFormat", numberFormatId == 0 ? null : "1");
        for (var i = 0; i < _xfs.Count; i++)
        {
            if (XNode.DeepEquals(_xfs[i], xf))
            {
                return i;
            }
        }

        cellXfs.Add(xf);
        cellXfs.SetAttributeValue("count", cellXfs.Elements().Count(e => e.Name.LocalName == "xf"));
        _xfs.Add(xf);
        _package.MarkDirty(_part!);
        return _xfs.Count - 1;
    }

    private static readonly string[] StyleOrder = ["numFmts", "fonts", "fills", "borders", "cellStyleXfs", "cellXfs", "cellStyles", "dxfs", "tableStyles", "colors", "extLst"];

    private XDocument Document()
    {
        if (_document?.Root is null)
        {
            (_part, _document) = _create();
            Read();
        }

        return _document;
    }

    /// <summary>A styles part with what Excel needs: a font, the two required fills, a border, and a default cell style.</summary>
    public static XDocument Default(XNamespace ns) => new(new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(ns + "styleSheet", new XAttribute("xmlns", ns.NamespaceName),
            new XElement(ns + "fonts", new XAttribute("count", 1),
                new XElement(ns + "font", new XElement(ns + "sz", new XAttribute("val", 11)), new XElement(ns + "name", new XAttribute("val", "Calibri")),
                    new XElement(ns + "family", new XAttribute("val", 2)))),
            new XElement(ns + "fills", new XAttribute("count", 2),
                new XElement(ns + "fill", new XElement(ns + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(ns + "fill", new XElement(ns + "patternFill", new XAttribute("patternType", "gray125")))),
            new XElement(ns + "borders", new XAttribute("count", 1),
                new XElement(ns + "border", new XElement(ns + "left"), new XElement(ns + "right"), new XElement(ns + "top"), new XElement(ns + "bottom"), new XElement(ns + "diagonal"))),
            new XElement(ns + "cellStyleXfs", new XAttribute("count", 1),
                new XElement(ns + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0))),
            new XElement(ns + "cellXfs", new XAttribute("count", 1),
                new XElement(ns + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0), new XAttribute("xfId", 0))),
            new XElement(ns + "cellStyles", new XAttribute("count", 1),
                new XElement(ns + "cellStyle", new XAttribute("name", "Normal"), new XAttribute("xfId", 0), new XAttribute("builtinId", 0)))));
}
