using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Csv;
using RaiCore.Pdf.Sheets.Workbooks;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// A workbook to read, change and save: an Excel workbook (<c>.xlsx</c>, <c>.xlsm</c>) such as a template, or a new
/// one. Cells are read and written by address (<c>sheet["B3"]</c>) or by row and column, found by their text, rows and
/// columns inserted and deleted with every reference to them moved, and the workbook saved as <c>.xlsx</c> or a sheet
/// as delimited text (CSV).
/// </summary>
/// <remarks>
/// <para>
/// <b>Saving keeps the template.</b> Only the parts of the package that change are rewritten: the worksheets that are
/// edited, the shared strings and styles when values need new ones, and the parts that refer to moved cells. Every
/// other part (styles, themes, charts, images, macros, printer settings, custom XML) is written back with the same
/// bytes, and a workbook saved without changes is written exactly as it was read.
/// </para>
/// <para>
/// <b>Formulas are not calculated.</b> Cells with formulas keep their last calculated values. When cells change, the
/// saved workbook asks Excel to calculate everything when it opens (<c>fullCalcOnLoad</c>) and has no calculation
/// chain, so the values are up to date there.
/// </para>
/// <para>
/// <b>Excel 97-2003 workbooks (<c>.xls</c>) and delimited text (CSV)</b> are read only: their values, number formats,
/// merged cells and hidden rows and columns can be read, found and saved as delimited text, and converted to PDF, but
/// changing them or saving them as <c>.xlsx</c> throws <see cref="NotSupportedException"/>. Their formulas are only
/// their calculated values; CSV values are text, as written.
/// </para>
/// <para>
/// Workbooks are untrusted input: they are read with no DTDs and nothing external resolved, within the
/// <see cref="WorkbookOptions.Limits"/>. Malformed data throws <see cref="SpreadsheetFormatException"/>; encrypted and
/// binary (<c>.xlsb</c>) workbooks throw <see cref="NotSupportedException"/>.
/// </para>
/// <para>A workbook is not thread-safe: use one from one thread at a time.</para>
/// </remarks>
/// <example>
/// <code>
/// var workbook = Workbook.Open("template.xlsx");
/// var sheet = workbook.Worksheets["Invoice"];
/// sheet["B3"].SetValue("Alice Example");
/// sheet["B4"].SetValue(new DateTime(2026, 11, 23));
/// sheet.InsertRows(10, 2);
/// workbook.Save("invoice.xlsx");
/// </code>
/// </example>
public sealed class Workbook
{
    private static readonly string[] WorkbookOrder =
    [
        "fileVersion", "fileSharing", "workbookPr", "AlternateContent", "absPath", "revisionPtr", "workbookProtection", "bookViews", "sheets",
        "functionGroups", "externalReferences", "definedNames", "calcPr", "oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr",
        "smartTagTypes", "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst",
    ];

    private readonly Dictionary<CultureInfo, FormatSettings> _settings = [];
    private readonly byte[]? _source;
    private readonly WorkbookOptions _options;
    private bool _cellsChanged;

    private Workbook(WorkbookPackage package, WorkbookFormat format, byte[]? source, WorkbookOptions options)
    {
        Package = package;
        Format = format;
        _source = source;
        _options = options;
        var root = package.Relationships(string.Empty);
        WorkbookPart = root.FirstOrDefault(r => r.ShortType == "officeDocument" && !r.External).Target ?? "xl/workbook.xml";
        if (WorkbookPart.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The workbook is a binary workbook (.xlsb), which is not supported.");
        }

        WorkbookXml = package.Xml(WorkbookPart) ?? throw new SpreadsheetFormatException("The package has no workbook part: it is not an XLSX workbook.");
        if (WorkbookXml.Root!.Name.LocalName != "workbook")
        {
            throw new SpreadsheetFormatException("The workbook part has no workbook element.");
        }

        var ns = Ns;
        var relationships = package.Relationships(WorkbookPart);
        Date1904 = WorkbookXml.Root.Element(ns + "workbookPr") is { } properties && WorkbookNames.Flag(properties, "date1904");
        Invariant = new FormatSettings(CultureInfo.InvariantCulture, Date1904);
        _settings[CultureInfo.InvariantCulture] = Invariant;

        string? Part(string type) => relationships.FirstOrDefault(r => r.ShortType == type && !r.External && package.Contains(r.Target)).Target;
        Strings = new SharedStringTable(package, Part("sharedStrings"), CreateSharedStrings);
        Styles = new WorkbookStyles(package, Part("styles"), CreateStyles);

        var worksheets = new List<Worksheet>();
        foreach (var element in WorkbookXml.Root.Element(ns + "sheets")?.Elements(ns + "sheet") ?? [])
        {
            package.Guard.Checkpoint();
            var id = WorkbookNames.RelationshipId(element);
            var relationship = relationships.FirstOrDefault(r => r.Id == id && !r.External);
            Worksheet? worksheet = null;
            if (relationship.ShortType == "worksheet" && !worksheets.Any(w => w.Part == relationship.Target) && package.Xml(relationship.Target) is { } document)
            {
                if (document.Root!.Name.LocalName != "worksheet")
                {
                    throw new SpreadsheetFormatException($"The worksheet part '{relationship.Target}' has no worksheet element.");
                }

                worksheet = new Worksheet(this, element, relationship.Target, document);
                worksheets.Add(worksheet);
            }

            AllSheets.Add(new SheetEntry(element, worksheet));
        }

        Worksheets = new WorksheetCollection(this, worksheets);
    }

    /// <summary>The format the workbook was read from (<see cref="WorkbookFormat.Xlsx"/> for a new one).</summary>
    public WorkbookFormat Format { get; }

    /// <summary>
    /// Whether the workbook is read only: an Excel 97-2003 workbook or delimited text, which can be read and saved as
    /// delimited text but not changed.
    /// </summary>
    public bool IsReadOnly => Format != WorkbookFormat.Xlsx;

    /// <summary>Whether serial dates count from 1904-01-01 (the 1904 date system) rather than from 1900-01-00.</summary>
    public bool Date1904 { get; }

    /// <summary>The worksheets, in the workbook's order (chart sheets and other sheets without cells are left out).</summary>
    public WorksheetCollection Worksheets { get; }

    /// <summary>
    /// The active worksheet: the one Excel shows when it opens the workbook, or the first worksheet if that is not a
    /// worksheet; <c>null</c> if the workbook has none.
    /// </summary>
    public Worksheet? ActiveWorksheet
    {
        get
        {
            var view = WorkbookXml.Root!.Element(Ns + "bookViews")?.Elements(Ns + "workbookView").FirstOrDefault();
            var active = view is null ? 0 : WorkbookNames.Integer(view, "activeTab") ?? 0;
            return (active >= 0 && active < AllSheets.Count ? AllSheets[active].Worksheet : null) ?? (Worksheets.Count > 0 ? Worksheets[0] : null);
        }
    }

    internal WorkbookPackage Package { get; }

    internal string WorkbookPart { get; }

    internal XDocument WorkbookXml { get; }

    internal XNamespace Ns => WorkbookXml.Root!.Name.Namespace;

    /// <summary>Every sheet in the workbook's order (worksheets, chart sheets, dialog sheets), as local sheet ids count them.</summary>
    internal List<SheetEntry> AllSheets { get; } = [];

    internal SharedStringTable Strings { get; }

    internal WorkbookStyles Styles { get; }

    /// <summary>The invariant culture in the workbook's date system.</summary>
    internal FormatSettings Invariant { get; }

    internal ResourceLimits Limits => Package.Guard.Limits;

    /// <summary>Creates a new workbook with one empty worksheet, <c>Sheet1</c>.</summary>
    public static Workbook Create()
    {
        var guard = new WorkbookGuard(ResourceLimits.Default);
        var package = WorkbookImport.Empty(guard, ["Sheet1"], _ => { });
        return new Workbook(package, WorkbookFormat.Xlsx, null, new WorkbookOptions());
    }

    /// <summary>Opens a workbook: an Excel workbook (<c>.xlsx</c>, <c>.xlsm</c>), an Excel 97-2003 workbook (<c>.xls</c>, read only) or delimited text (read only), told apart by their content.</summary>
    /// <param name="data">The file's bytes.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="SpreadsheetFormatException">The data is not a valid workbook.</exception>
    /// <exception cref="NotSupportedException">The data is an encrypted or binary (<c>.xlsb</c>) workbook, or an Excel 2.x-4.0 file.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static Workbook Open(byte[] data, WorkbookOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return OpenData(data.AsSpan().ToArray(), options ?? new WorkbookOptions());
    }

    /// <summary>Opens a workbook read from a stream (to its end; the stream is left open).</summary>
    /// <inheritdoc cref="Open(byte[], WorkbookOptions?)"/>
    public static Workbook Open(Stream stream, WorkbookOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return OpenData(buffer.ToArray(), options ?? new WorkbookOptions());
    }

    /// <summary>Opens a workbook file.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <exception cref="SpreadsheetFormatException">The file is not a valid workbook.</exception>
    /// <exception cref="NotSupportedException">The file is an encrypted or binary (<c>.xlsb</c>) workbook, or an Excel 2.x-4.0 file.</exception>
    /// <exception cref="ResourceLimitExceededException">The workbook exceeds one of the options' limits.</exception>
    public static Workbook Open(string path, WorkbookOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OpenData(File.ReadAllBytes(path), options ?? new WorkbookOptions());
    }

    private static Workbook OpenData(byte[] data, WorkbookOptions options)
    {
        var limits = options.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        var guard = new WorkbookGuard(limits);
        if (XlsConverter.IsBinary(data))
        {
            return new Workbook(WorkbookImport.FromXls(data, guard), WorkbookFormat.Xls, data, options);
        }

        if (data.AsSpan().StartsWith("PK"u8))
        {
            return new Workbook(WorkbookPackage.Open(data, guard), WorkbookFormat.Xlsx, null, options);
        }

        return new Workbook(WorkbookImport.FromCsv(data, options, guard), WorkbookFormat.Csv, data, options);
    }

    /// <summary>Finds the cells of all worksheets whose text matches, in the workbook's order, row by row.</summary>
    /// <param name="text">The text to find (a regular expression with <see cref="FindMatch.Regex"/>).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults: the displayed value contains the text, ignoring case.</param>
    /// <returns>The cells found.</returns>
    /// <exception cref="ArgumentException">The text is empty, or not a valid regular expression.</exception>
    /// <exception cref="RegexMatchTimeoutException">A regular expression match took longer than <see cref="FindOptions.RegexTimeout"/>.</exception>
    public IReadOnlyList<Cell> Find(string text, FindOptions? options = null)
    {
        var match = Matcher(text, options ??= new FindOptions());
        return [.. Worksheets.SelectMany(s => s.FindAll(match, options))];
    }

    /// <summary>A function that tells whether a cell's text matches the text to find.</summary>
    internal static Func<string, bool> Matcher(string text, FindOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(options);
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (options.Match)
        {
            case FindMatch.WholeCell:
                return value => string.Equals(value, text, comparison);
            case FindMatch.Regex:
                var regex = new Regex(text, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, options.RegexTimeout);
                return regex.IsMatch;
            default:
                return value => value.Contains(text, comparison);
        }
    }

    /// <summary>Saves the workbook as an Excel workbook.</summary>
    /// <param name="path">The file's path; it is overwritten.</param>
    /// <exception cref="NotSupportedException">The workbook is read only (<see cref="IsReadOnly"/>).</exception>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        EnsureWritable("saved as .xlsx");
        using var file = File.Create(path);
        Save(file);
    }

    /// <summary>Saves the workbook as an Excel workbook to a stream (which is left open).</summary>
    /// <param name="stream">The stream.</param>
    /// <exception cref="NotSupportedException">The workbook is read only (<see cref="IsReadOnly"/>).</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EnsureWritable("saved as .xlsx");
        Package.Guard.Start();
        Prepare();
        Package.Save(stream);
    }

    /// <summary>The workbook saved as an Excel workbook.</summary>
    /// <exception cref="NotSupportedException">The workbook is read only (<see cref="IsReadOnly"/>).</exception>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();
        Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>Saves a worksheet as delimited text (CSV) to a file.</summary>
    /// <param name="path">The file's path; it is overwritten.</param>
    /// <param name="options">The options (the sheet, separator, encoding, quoting and values), or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have, or a separator that cannot be one.</exception>
    public void SaveAsCsv(string path, CsvSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= new CsvSaveOptions();
        var sheet = CsvSheet(options);
        using var file = File.Create(path);
        CsvWriter.Write(sheet, file, options);
    }

    /// <summary>Saves a worksheet as delimited text (CSV) to a stream (which is left open).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">The options (the sheet, separator, encoding, quoting and values), or <c>null</c> for the defaults.</param>
    /// <exception cref="ArgumentException">The options name a sheet the workbook does not have, or a separator that cannot be one.</exception>
    public void SaveAsCsv(Stream stream, CsvSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new CsvSaveOptions();
        CsvWriter.Write(CsvSheet(options), stream, options);
    }

    private Worksheet? CsvSheet(CsvSaveOptions options)
    {
        if (options.Separator is '"' or '\r' or '\n' or '\0')
        {
            throw new ArgumentException("The separator cannot be a double quote, a line break or a NUL.", nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.Encoding, nameof(options));
        ArgumentNullException.ThrowIfNull(options.LineEnding, nameof(options));
        if (options.SheetName is { } name)
        {
            return Worksheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The workbook has no worksheet named '{name}'.", nameof(options));
        }

        return ActiveWorksheet;
    }

    /// <summary>Adds an empty worksheet after the others.</summary>
    internal Worksheet AddWorksheet(string name)
    {
        EnsureWritable("changed");
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > 31 || name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0 || name[0] == '\'' || name[^1] == '\''
            || name.Equals("History", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A sheet name has 1 to 31 characters, none of [ ] : * ? / \\, and does not start or end with an apostrophe.", nameof(name));
        }

        if (AllSheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"The workbook already has a sheet named '{name}'.", nameof(name));
        }

        var folder = XlsxPackage.FolderOf(WorkbookPart);
        var number = 1;
        string part;
        do
        {
            part = (folder.Length > 0 ? folder + "/" : string.Empty) + "worksheets/sheet" + number.ToString(CultureInfo.InvariantCulture) + ".xml";
            number++;
        }
        while (Package.Contains(part));

        var document = WorkbookImport.EmptyWorksheet(Ns, RelationshipNamespace, selected: false);
        Package.Add(part, document, WorkbookNames.WorksheetType);
        var id = Package.AddRelationship(WorkbookPart, RelationshipType("worksheet"), part);
        var ns = Ns;
        var root = WorkbookXml.Root!;
        var sheets = root.Element(ns + "sheets");
        if (sheets is null)
        {
            sheets = new XElement(ns + "sheets");
            WorkbookNames.InsertInOrder(root, sheets, WorkbookOrder);
        }

        var sheetId = AllSheets.Select(s => WorkbookNames.Integer(s.Element, "sheetId") ?? 0).DefaultIfEmpty(0).Max() + 1;
        var element = new XElement(ns + "sheet", new XAttribute("name", name), new XAttribute("sheetId", sheetId),
            new XAttribute(XName.Get("id", RelationshipNamespace), id));
        sheets.Add(element);
        Package.MarkDirty(WorkbookPart);
        var worksheet = new Worksheet(this, element, part, document);
        AllSheets.Add(new SheetEntry(element, worksheet));
        return worksheet;
    }

    /// <summary>The namespace of relationship ids (<c>r:id</c>): strict or transitional, as the workbook is.</summary>
    internal string RelationshipNamespace => Ns.NamespaceName == WorkbookNames.StrictMain ? WorkbookNames.StrictRelationships : WorkbookNames.Relationships;

    /// <summary>The full type of a relationship (<c>worksheet</c>), strict or transitional, as the workbook is.</summary>
    internal string RelationshipType(string type) => RelationshipNamespace + "/" + type;

    /// <summary>The settings to format values in a culture (the invariant one for <c>null</c>), in the workbook's date system.</summary>
    internal FormatSettings Settings(CultureInfo? culture)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (!_settings.TryGetValue(culture, out var settings))
        {
            settings = new FormatSettings(culture, Date1904);
            _settings[culture] = settings;
        }

        return settings;
    }

    /// <summary>Throws <see cref="NotSupportedException"/> when the workbook is read only.</summary>
    internal void EnsureWritable(string what)
    {
        if (IsReadOnly)
        {
            throw new NotSupportedException($"The workbook was read from {(Format == WorkbookFormat.Xls ? "an Excel 97-2003 (.xls) workbook" : "delimited text")}, which is read only: it cannot be {what}. Save it with SaveAsCsv, or convert it with XlsxToPdf.");
        }
    }

    /// <summary>Records that cells of a worksheet changed: the worksheet is rewritten, and Excel recalculates on opening.</summary>
    internal void CellsChanged(Worksheet sheet)
    {
        Package.MarkDirty(sheet.Part);
        _cellsChanged = true;
    }

    /// <summary>Inserts or deletes rows or columns of a worksheet, moving every reference to them.</summary>
    internal void Shift(Worksheet sheet, bool rows, int start, int count, bool delete)
    {
        EnsureWritable("changed");
        Package.Guard.Start();
        ReferenceShifter.Apply(this, sheet, new ShiftOperation(sheet.Name, rows, start, count, delete));
        _cellsChanged = true;
    }

    /// <summary>
    /// Gets the package ready to save after cells changed: no calculation chain (it would name cells that moved or no
    /// longer have formulas), full calculation when Excel opens it, and the used ranges of changed worksheets.
    /// </summary>
    private void Prepare()
    {
        foreach (var sheet in Worksheets.Where(s => Package.IsDirty(s.Part)))
        {
            sheet.UpdateDimension();
        }

        if (!_cellsChanged)
        {
            return;
        }

        foreach (var relationship in Package.Relationships(WorkbookPart).Where(r => r.ShortType == "calcChain"))
        {
            if (!relationship.External)
            {
                Package.Remove(relationship.Target);
            }

            Package.RemoveRelationship(WorkbookPart, relationship.Id);
        }

        var root = WorkbookXml.Root!;
        var calc = root.Element(Ns + "calcPr");
        if (calc is null)
        {
            calc = new XElement(Ns + "calcPr");
            WorkbookNames.InsertInOrder(root, calc, WorkbookOrder);
        }

        if ((string?)calc.Attribute("fullCalcOnLoad") != "1")
        {
            calc.SetAttributeValue("fullCalcOnLoad", "1");
            Package.MarkDirty(WorkbookPart);
        }
    }

    /// <summary>Converts the workbook to PDF: as saved (an Excel workbook), or from the file it was read from.</summary>
    internal PdfDocument ConvertToPdf(XlsxConverterOptions options, CancellationToken cancellationToken)
    {
        switch (Format)
        {
            case WorkbookFormat.Xls:
                return XlsConverter.Convert(_source!, options, cancellationToken);
            case WorkbookFormat.Csv:
                return CsvConverter.Convert(_source!, new CsvConverterOptions
                {
                    Sheet = options.Sheet,
                    Delimiter = _options.CsvDelimiter,
                    Encoding = _options.CsvEncoding,
                }, cancellationToken);
            default:
                return XlsxConverter.Convert(ToArray(), options, cancellationToken);
        }
    }

    private (string Part, XDocument Document) CreateSharedStrings()
    {
        var part = UniquePart("sharedStrings.xml");
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(Ns + "sst", new XAttribute("xmlns", Ns.NamespaceName),
            new XAttribute("count", 0), new XAttribute("uniqueCount", 0)));
        Package.Add(part, document, WorkbookNames.SharedStringsType);
        Package.AddRelationship(WorkbookPart, RelationshipType("sharedStrings"), part);
        return (part, document);
    }

    private (string Part, XDocument Document) CreateStyles()
    {
        var part = UniquePart("styles.xml");
        var document = WorkbookStyles.Default(Ns);
        Package.Add(part, document, WorkbookNames.StylesType);
        Package.AddRelationship(WorkbookPart, RelationshipType("styles"), part);
        return (part, document);
    }

    private string UniquePart(string name)
    {
        var folder = XlsxPackage.FolderOf(WorkbookPart);
        var part = (folder.Length > 0 ? folder + "/" : string.Empty) + name;
        for (var i = 2; Package.Contains(part); i++)
        {
            part = (folder.Length > 0 ? folder + "/" : string.Empty) + Path.GetFileNameWithoutExtension(name) + i.ToString(CultureInfo.InvariantCulture) + ".xml";
        }

        return part;
    }
}

/// <summary>A sheet of the workbook part: its element, and its worksheet if it is one.</summary>
internal sealed record SheetEntry(XElement Element, Worksheet? Worksheet)
{
    public string Name => (string?)Element.Attribute("name") ?? string.Empty;
}

/// <summary>The worksheets of a <see cref="Workbook"/>, by position and by name.</summary>
public sealed class WorksheetCollection : IReadOnlyList<Worksheet>
{
    private readonly Workbook _workbook;
    private readonly List<Worksheet> _items;

    internal WorksheetCollection(Workbook workbook, List<Worksheet> items)
    {
        _workbook = workbook;
        _items = items;
    }

    /// <summary>The number of worksheets.</summary>
    public int Count => _items.Count;

    /// <summary>The worksheet at a position, from 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no worksheet at the position.</exception>
    public Worksheet this[int index] => _items[index];

    /// <summary>The worksheet with a name (compared ignoring case).</summary>
    /// <exception cref="KeyNotFoundException">The workbook has no worksheet with the name.</exception>
    public Worksheet this[string name] => Find(name) ?? throw new KeyNotFoundException($"The workbook has no worksheet named '{name}'.");

    /// <summary>Whether the workbook has a worksheet with a name (compared ignoring case).</summary>
    public bool Contains(string name) => Find(name) is not null;

    /// <summary>
    /// Adds an empty worksheet after the others. The name has 1 to 31 characters, none of <c>[ ] : * ? / \</c>, and
    /// is not the name of another sheet.
    /// </summary>
    /// <param name="name">The worksheet's name.</param>
    /// <returns>The new worksheet.</returns>
    /// <exception cref="ArgumentException">The name is not a valid sheet name, or another sheet has it.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public Worksheet Add(string name)
    {
        var worksheet = _workbook.AddWorksheet(name);
        _items.Add(worksheet);
        return worksheet;
    }

    /// <summary>The position of a worksheet, or -1.</summary>
    public int IndexOf(Worksheet worksheet) => _items.IndexOf(worksheet);

    /// <inheritdoc/>
    public IEnumerator<Worksheet> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private Worksheet? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _items.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
