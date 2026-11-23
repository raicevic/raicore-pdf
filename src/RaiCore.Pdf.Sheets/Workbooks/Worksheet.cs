using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Workbooks;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets;

/// <summary>
/// A worksheet of a <see cref="Workbook"/>: its cells by address (<c>sheet["B3"]</c>) or by row and column (from 1),
/// its rows and columns, and the insertion and deletion of rows and columns.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InsertRows"/>, <see cref="DeleteRows"/>, <see cref="InsertColumns"/> and <see cref="DeleteColumns"/>
/// move what is below (or to the right) and every reference to it, as Excel does: formulas on every sheet (references,
/// ranges, whole rows and columns, sheet names, shared and array formulas), defined names (print areas and titles
/// too), merged cells, conditional formatting, data validation, hyperlinks, the autofilter, tables, comments,
/// drawings (pictures, charts and the ranges their series use), row and column sizes, visibility and outline levels,
/// and page breaks. References to deleted cells become <c>#REF!</c>; ranges shrink to what is left of them. A
/// reference to more than one sheet (<c>Sheet1:Sheet3!A1</c>) is not moved, since it names the same cells on every
/// sheet; structured references to tables (<c>Table1[Amount]</c>) need no change.
/// </para>
/// <para>
/// As in Excel, changing part of an array formula, or of a table's header or totals row, throws
/// <see cref="InvalidOperationException"/>, and so does an insertion that would push cells off the sheet.
/// </para>
/// </remarks>
public sealed class Worksheet
{
    /// <summary>The order of a worksheet's elements (ECMA-376 CT_Worksheet).</summary>
    internal static readonly string[] Order =
    [
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr", "sheetProtection", "protectedRanges",
        "scenarios", "autoFilter", "sortState", "dataConsolidate", "customSheetViews", "mergeCells", "phoneticPr", "conditionalFormatting",
        "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup", "headerFooter", "rowBreaks", "colBreaks",
        "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF", "drawingHF",
        "picture", "oleObjects", "controls", "webPublishItems", "tableParts", "extLst",
    ];

    private readonly Dictionary<long, XElement> _cells = [];
    private readonly Dictionary<int, XElement> _rows = [];
    private readonly List<int> _rowOrder = [];
    private readonly List<(Area Range, int Row, int Column)> _arrays = [];
    private Dictionary<string, (int Row, int Column, string Formula)>? _shared;

    internal Worksheet(Workbook workbook, XElement entry, string part, XDocument document)
    {
        Workbook = workbook;
        Entry = entry;
        Part = part;
        Document = document;
        Reindex();
    }

    /// <summary>The workbook the worksheet belongs to.</summary>
    public Workbook Workbook { get; }

    /// <summary>The worksheet's name (its tab).</summary>
    public string Name => (string?)Entry.Attribute("name") ?? string.Empty;

    /// <summary>The worksheet's position in <see cref="Workbook.Worksheets"/>, from 0.</summary>
    public int Index => Workbook.Worksheets.IndexOf(this);

    /// <summary>
    /// Whether the worksheet is hidden (also "very hidden", which only code can show). A workbook needs a visible
    /// sheet, so hiding the last one throws <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The workbook is read only (when set).</exception>
    public bool IsHidden
    {
        get => (string?)Entry.Attribute("state") is "hidden" or "veryHidden";
        set
        {
            if (value == IsHidden)
            {
                return;
            }

            Workbook.EnsureWritable("changed");
            if (value)
            {
                if (!Workbook.AllSheets.Any(s => s.Element != Entry && (string?)s.Element.Attribute("state") is null or "visible"))
                {
                    throw new InvalidOperationException("A workbook needs a visible sheet: the last one cannot be hidden.");
                }

                Entry.SetAttributeValue("state", "hidden");
                foreach (var view in Root.Element(Ns + "sheetViews")?.Elements(Ns + "sheetView") ?? [])
                {
                    view.Attribute("tabSelected")?.Remove();
                }

                var bookView = Workbook.WorkbookXml.Root!.Element(Workbook.Ns + "bookViews")?.Elements(Workbook.Ns + "workbookView").FirstOrDefault();
                var position = Workbook.AllSheets.FindIndex(s => s.Element == Entry);
                if (bookView is not null && (WorkbookNames.Integer(bookView, "activeTab") ?? 0) == position)
                {
                    bookView.SetAttributeValue("activeTab", Workbook.AllSheets.FindIndex(s => (string?)s.Element.Attribute("state") is null or "visible"));
                }

                Workbook.Package.MarkDirty(Part);
            }
            else
            {
                Entry.Attribute("state")?.Remove();
            }

            Workbook.Package.MarkDirty(Workbook.WorkbookPart);
        }
    }

    /// <summary>The cell at an address (<c>B3</c>, also <c>$B$3</c>).</summary>
    /// <param name="address">The address.</param>
    /// <exception cref="ArgumentException">The address is not a cell's.</exception>
    public Cell this[string address]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(address);
            if (Area.Parse(address) is not { Kind: AreaKind.Cell } area)
            {
                throw new ArgumentException($"'{address}' is not a cell address such as B3.", nameof(address));
            }

            return new Cell(this, area.First.Row, area.First.Column);
        }
    }

    /// <summary>The number of the last row with a value or a formula, or 0 if the sheet has none.</summary>
    public int LastRow => UsedCells().Select(c => c.Row + 1).DefaultIfEmpty(0).Max();

    /// <summary>The number of the last column with a value or a formula, or 0 if the sheet has none.</summary>
    public int LastColumn => UsedCells().Select(c => c.Column + 1).DefaultIfEmpty(0).Max();

    /// <summary>The cells with a value or a formula, row by row.</summary>
    public IEnumerable<Cell> Cells => UsedCells().Select(c => new Cell(this, c.Row, c.Column));

    /// <summary>The merged ranges (<c>A1:C1</c>).</summary>
    public IReadOnlyList<string> MergedRanges =>
        [.. Root.Element(Ns + "mergeCells")?.Elements(Ns + "mergeCell").Select(m => (string?)m.Attribute("ref")).OfType<string>() ?? []];

    internal XElement Entry { get; }

    internal string Part { get; }

    internal XDocument Document { get; }

    internal XElement Root => Document.Root!;

    internal XNamespace Ns => Root.Name.Namespace;

    /// <summary>The cell at a row and a column, both from 1 (<c>Cell(3, 2)</c> is B3).</summary>
    /// <param name="row">The row, from 1 to 1,048,576.</param>
    /// <param name="column">The column, from 1 to 16,384.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is outside the sheet.</exception>
    public Cell Cell(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, XlsxWorkbook.MaxRows);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, XlsxWorkbook.MaxColumns);
        return new Cell(this, row - 1, column - 1);
    }

    /// <summary>A row, from 1: its visibility, outline level and height.</summary>
    /// <param name="row">The row, from 1 to 1,048,576.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row is outside the sheet.</exception>
    public Row Row(int row)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, XlsxWorkbook.MaxRows);
        return new Row(this, row - 1);
    }

    /// <summary>A column, from 1: its visibility, outline level and width.</summary>
    /// <param name="column">The column, from 1 to 16,384.</param>
    /// <exception cref="ArgumentOutOfRangeException">The column is outside the sheet.</exception>
    public Column Column(int column)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, XlsxWorkbook.MaxColumns);
        return new Column(this, column - 1);
    }

    /// <summary>Finds the cells whose text matches, row by row.</summary>
    /// <param name="text">The text to find (a regular expression with <see cref="FindMatch.Regex"/>).</param>
    /// <param name="options">The options, or <c>null</c> for the defaults: the displayed value contains the text, ignoring case.</param>
    /// <returns>The cells found.</returns>
    /// <exception cref="ArgumentException">The text is empty, or not a valid regular expression.</exception>
    /// <exception cref="System.Text.RegularExpressions.RegexMatchTimeoutException">A regular expression match took longer than <see cref="FindOptions.RegexTimeout"/>.</exception>
    public IReadOnlyList<Cell> Find(string text, FindOptions? options = null)
    {
        options ??= new FindOptions();
        return [.. FindAll(Workbook.Matcher(text, options), options)];
    }

    internal IEnumerable<Cell> FindAll(Func<string, bool> match, FindOptions options)
    {
        foreach (var cell in Cells)
        {
            var text = options.LookIn == FindLookIn.Formulas
                ? cell.Formula is { } formula ? "=" + formula : cell.RawText()
                : cell.GetDisplayText(options.Culture);
            if (match(text))
            {
                yield return cell;
            }
        }
    }

    /// <summary>
    /// Inserts empty rows before a row, moving it and the rows below down, and every reference to them. Rows inserted
    /// inside an outline group join it.
    /// </summary>
    /// <param name="row">The row to insert before, from 1.</param>
    /// <param name="count">How many rows to insert.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row or count is outside the sheet.</exception>
    /// <exception cref="InvalidOperationException">Cells would be pushed off the sheet, or an array formula or table header would be split.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void InsertRows(int row, int count = 1) => Shift(true, row, count, false);

    /// <summary>Deletes rows, moving the rows below up, and every reference to them; references to deleted cells become <c>#REF!</c>.</summary>
    /// <param name="row">The first row to delete, from 1.</param>
    /// <param name="count">How many rows to delete.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rows are outside the sheet.</exception>
    /// <exception cref="InvalidOperationException">Part of an array formula, or a table's header or totals row, would be deleted.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void DeleteRows(int row, int count = 1) => Shift(true, row, count, true);

    /// <summary>Inserts empty columns before a column, moving it and the columns to its right, and every reference to them.</summary>
    /// <param name="column">The column to insert before, from 1.</param>
    /// <param name="count">How many columns to insert.</param>
    /// <exception cref="ArgumentOutOfRangeException">The column or count is outside the sheet.</exception>
    /// <exception cref="InvalidOperationException">Cells would be pushed off the sheet, or an array formula would be split.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void InsertColumns(int column, int count = 1) => Shift(false, column, count, false);

    /// <summary>Deletes columns, moving the columns to their right left, and every reference to them; references to deleted cells become <c>#REF!</c>.</summary>
    /// <param name="column">The first column to delete, from 1.</param>
    /// <param name="count">How many columns to delete.</param>
    /// <exception cref="ArgumentOutOfRangeException">The columns are outside the sheet.</exception>
    /// <exception cref="InvalidOperationException">Part of an array formula would be deleted.</exception>
    /// <exception cref="NotSupportedException">The workbook is read only.</exception>
    public void DeleteColumns(int column, int count = 1) => Shift(false, column, count, true);

    private void Shift(bool rows, int start, int count, bool delete)
    {
        var max = rows ? XlsxWorkbook.MaxRows : XlsxWorkbook.MaxColumns;
        ArgumentOutOfRangeException.ThrowIfLessThan(start, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, max);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, max - start + 1);
        Workbook.Shift(this, rows, start - 1, count, delete);
    }

    /// <summary>The cells with a value or a formula, row by row, from 0.</summary>
    private IEnumerable<(int Row, int Column)> UsedCells()
    {
        foreach (var row in _rowOrder)
        {
            var cells = new List<(int Row, int Column)>();
            foreach (var c in _rows[row].Elements(Ns + "c"))
            {
                if (Position(c) is var (r, column) && HasContent(c) && _cells.TryGetValue(XlsxWorksheet.Key(r, column), out var indexed) && indexed == c)
                {
                    cells.Add((r, column));
                }
            }

            cells.Sort();
            foreach (var cell in cells)
            {
                yield return cell;
            }
        }
    }

    /// <summary>Whether a cell element holds a value or a formula.</summary>
    private static bool HasContent(XElement c) => c.Elements().Any(e => e.Name.LocalName is "v" or "is" or "f");

    /// <summary>The position of a cell element by its reference, from 0.</summary>
    internal static (int Row, int Column)? Position(XElement c) =>
        Area.Parse((string?)c.Attribute("r")) is { Kind: AreaKind.Cell } area ? (area.First.Row, area.First.Column) : null;

    /// <summary>The row index of a row element, from 0, or <c>null</c>.</summary>
    internal static int? RowIndex(XElement row) => WorkbookNames.Integer(row, "r") is { } r and >= 1 and <= XlsxWorkbook.MaxRows ? r - 1 : null;

    /// <summary>The number of cell elements.</summary>
    internal int CellCount => _cells.Count;

    /// <summary>The cell element at a position, or <c>null</c>.</summary>
    internal XElement? FindCell(int row, int column) => _cells.GetValueOrDefault(XlsxWorksheet.Key(row, column));

    /// <summary>The row element of a row, or <c>null</c>.</summary>
    internal XElement? FindRow(int row) => _rows.GetValueOrDefault(row);

    /// <summary>
    /// Reads the index of rows and cells from the sheet data, giving rows and cells without a reference the one their
    /// position implies. Throws when the sheet has more cells than <see cref="ResourceLimits.MaxTableCells"/>.
    /// </summary>
    internal void Reindex()
    {
        _cells.Clear();
        _rows.Clear();
        _rowOrder.Clear();
        _arrays.Clear();
        _shared = null;
        var data = Root.Element(Ns + "sheetData");
        if (data is null)
        {
            return;
        }

        var maxCells = Workbook.Limits.MaxTableCells;
        var guard = Workbook.Package.Guard;
        var row = -1;
        var count = 0L;
        foreach (var rowElement in data.Elements(Ns + "row"))
        {
            row = RowIndex(rowElement) ?? row + 1;
            if (row >= XlsxWorkbook.MaxRows)
            {
                break;
            }

            if (rowElement.Attribute("r") is null)
            {
                rowElement.SetAttributeValue("r", row + 1);
            }

            if (!_rows.TryAdd(row, rowElement))
            {
                continue;
            }

            _rowOrder.Add(row);
            var column = -1;
            foreach (var c in rowElement.Elements(Ns + "c"))
            {
                if (Position(c) is var (cellRow, cellColumn) && cellRow == row)
                {
                    column = cellColumn;
                }
                else if (++column < XlsxWorkbook.MaxColumns)
                {
                    c.SetAttributeValue("r", FormulaReferences.Address(row, column));
                }
                else
                {
                    continue;
                }

                if (++count > maxCells)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), maxCells,
                        $"The worksheet '{Name}' has more cells than ResourceLimits.MaxTableCells allows ({maxCells}).");
                }

                if ((count & 4095) == 0)
                {
                    guard.Checkpoint();
                }

                _cells.TryAdd(XlsxWorksheet.Key(row, column), c);
                if (c.Element(Ns + "f") is { } f && (string?)f.Attribute("t") == "array" && Area.Parse((string?)f.Attribute("ref")) is { } range)
                {
                    _arrays.Add((range, row, column));
                }
            }
        }

        _rowOrder.Sort();
    }

    /// <summary>The array formula whose range has a cell other than its first, or <c>null</c>.</summary>
    internal Area? ArrayAt(int row, int column)
    {
        foreach (var (range, r, c) in _arrays)
        {
            var (r1, c1, r2, c2) = range.Bounds;
            if (row >= r1 && row <= r2 && column >= c1 && column <= c2 && (row != r || column != c || r1 != r2 || c1 != c2))
            {
                return range;
            }
        }

        return null;
    }

    /// <summary>The formula of a cell of a shared formula (by its <c>si</c>), copied from the formula's first cell; <c>null</c> if it has none.</summary>
    internal string? SharedFormula(string index, int row, int column)
    {
        if (_shared is null)
        {
            _shared = [];
            foreach (var c in _cells.Values)
            {
                if (c.Element(Ns + "f") is { } f && (string?)f.Attribute("t") == "shared" && (string?)f.Attribute("si") is { } si
                    && f.Value.Length > 0 && Position(c) is var (r, col))
                {
                    _shared.TryAdd(si, (r, col, f.Value));
                }
            }
        }

        return _shared.TryGetValue(index, out var master) ? FormulaReferences.Translate(master.Formula, row - master.Row, column - master.Column) : null;
    }

    /// <summary>
    /// Turns the cells of shared formulas into cells with formulas of their own (the formula of the first cell
    /// copied to each), for the shared formulas <paramref name="which"/> chooses by their first cell's formula.
    /// </summary>
    internal bool UnshareFormulas(Func<string, bool> which)
    {
        var masters = new Dictionary<string, (int Row, int Column, string Formula)>();
        var cells = new List<(XElement F, int Row, int Column)>();
        foreach (var c in _cells.Values)
        {
            if (c.Element(Ns + "f") is { } f && (string?)f.Attribute("t") == "shared" && (string?)f.Attribute("si") is { } si && Position(c) is var (row, column))
            {
                cells.Add((f, row, column));
                if (f.Value.Length > 0)
                {
                    masters.TryAdd(si, (row, column, f.Value));
                }
            }
        }

        var chosen = masters.Where(m => which(m.Value.Formula)).Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var (f, row, column) in cells)
        {
            var si = (string)f.Attribute("si")!;
            if (!chosen.Contains(si))
            {
                continue;
            }

            var master = masters[si];
            var formula = f.Value.Length > 0 ? f.Value : FormulaReferences.Translate(master.Formula, row - master.Row, column - master.Column);
            f.Attribute("t")!.Remove();
            f.Attribute("si")!.Remove();
            f.Attribute("ref")?.Remove();
            f.Value = formula;
            changed = true;
        }

        _shared = null;
        return changed;
    }

    /// <summary>The cell element at a position, created (empty, in the row's or column's style) if there is none.</summary>
    internal XElement GetOrCreateCell(int row, int column)
    {
        var key = XlsxWorksheet.Key(row, column);
        if (_cells.TryGetValue(key, out var cell))
        {
            return cell;
        }

        var rowElement = GetOrCreateRow(row);
        cell = new XElement(Ns + "c", new XAttribute("r", FormulaReferences.Address(row, column)));
        var style = WorkbookNames.Flag(rowElement, "customFormat") ? (string?)rowElement.Attribute("s") : (string?)FindColumn(column)?.Attribute("style");
        if (style is not null and not "0")
        {
            cell.SetAttributeValue("s", style);
        }

        XElement? before = null;
        foreach (var child in rowElement.Elements())
        {
            if (child.Name != Ns + "c" || (Position(child) is var (_, c) && c > column))
            {
                before = child;
                break;
            }
        }

        if (before is not null)
        {
            before.AddBeforeSelf(cell);
        }
        else
        {
            rowElement.Add(cell);
        }

        rowElement.Attribute("spans")?.Remove();
        _cells[key] = cell;
        return cell;
    }

    /// <summary>The row element of a row, created if there is none.</summary>
    internal XElement GetOrCreateRow(int row)
    {
        if (_rows.TryGetValue(row, out var element))
        {
            return element;
        }

        element = new XElement(Ns + "row", new XAttribute("r", row + 1));
        var position = _rowOrder.BinarySearch(row);
        position = ~position;
        if (position < _rowOrder.Count)
        {
            _rows[_rowOrder[position]].AddBeforeSelf(element);
        }
        else
        {
            SheetData().Add(element);
        }

        _rowOrder.Insert(position, row);
        _rows[row] = element;
        return element;
    }

    /// <summary>The sheet data element, created if there is none.</summary>
    internal XElement SheetData()
    {
        var data = Root.Element(Ns + "sheetData");
        if (data is null)
        {
            data = new XElement(Ns + "sheetData");
            WorkbookNames.InsertInOrder(Root, data, Order);
        }

        return data;
    }

    /// <summary>Removes a cell element (one with nothing left in it).</summary>
    internal void RemoveCell(int row, int column)
    {
        if (_cells.Remove(XlsxWorksheet.Key(row, column), out var cell))
        {
            cell.Remove();
        }
    }

    /// <summary>The <c>col</c> element that covers a column (from 0), or <c>null</c>.</summary>
    internal XElement? FindColumn(int column) =>
        Root.Element(Ns + "cols")?.Elements(Ns + "col").FirstOrDefault(c =>
            WorkbookNames.Integer(c, "min") is { } min && min - 1 <= column && (WorkbookNames.Integer(c, "max") ?? min) - 1 >= column);

    /// <summary>
    /// A <c>col</c> element for exactly one column (from 0): the one that covers it split around it, or a new one in
    /// the default width.
    /// </summary>
    internal XElement GetOrCreateColumn(int column)
    {
        var number = column + 1;
        if (FindColumn(column) is { } existing)
        {
            var min = WorkbookNames.Integer(existing, "min")!.Value;
            var max = WorkbookNames.Integer(existing, "max") ?? min;
            if (min == number && max == number)
            {
                return existing;
            }

            var single = new XElement(existing);
            single.SetAttributeValue("min", number);
            single.SetAttributeValue("max", number);
            existing.AddAfterSelf(single);
            if (max > number)
            {
                var after = new XElement(existing);
                after.SetAttributeValue("min", number + 1);
                after.SetAttributeValue("max", max);
                single.AddAfterSelf(after);
            }

            if (min < number)
            {
                existing.SetAttributeValue("max", number - 1);
            }
            else
            {
                existing.Remove();
            }

            return single;
        }

        var cols = Root.Element(Ns + "cols");
        if (cols is null)
        {
            cols = new XElement(Ns + "cols");
            WorkbookNames.InsertInOrder(Root, cols, Order);
        }

        var format = Root.Element(Ns + "sheetFormatPr");
        var width = format?.Attribute("defaultColWidth")?.Value
            ?? (format is not null && WorkbookNames.Integer(format, "baseColWidth") is { } baseWidth and >= 0 and <= 255
                ? (baseWidth + 5 / 7.0 + 0.0001).ToString("0.########", CultureInfo.InvariantCulture)
                : "9.140625");
        var element = new XElement(Ns + "col", new XAttribute("min", number), new XAttribute("max", number), new XAttribute("width", width));
        var next = cols.Elements(Ns + "col").FirstOrDefault(c => (WorkbookNames.Integer(c, "min") ?? 0) > number);
        if (next is not null)
        {
            next.AddBeforeSelf(element);
        }
        else
        {
            cols.Add(element);
        }

        return element;
    }

    /// <summary>Whether rows the sheet does not list are hidden (<c>sheetFormatPr/@zeroHeight</c>).</summary>
    internal bool RowsHiddenByDefault => Root.Element(Ns + "sheetFormatPr") is { } format && WorkbookNames.Flag(format, "zeroHeight");

    /// <summary>Raises the outline level the sheet declares for rows or columns to at least a level.</summary>
    internal void DeclareOutlineLevel(bool rows, int level)
    {
        if (level <= 0)
        {
            return;
        }

        var format = Root.Element(Ns + "sheetFormatPr");
        if (format is null)
        {
            format = new XElement(Ns + "sheetFormatPr", new XAttribute("defaultRowHeight", "15"));
            WorkbookNames.InsertInOrder(Root, format, Order);
        }

        var name = rows ? "outlineLevelRow" : "outlineLevelCol";
        if ((WorkbookNames.Integer(format, name) ?? 0) < level)
        {
            format.SetAttributeValue(name, level);
        }
    }

    /// <summary>Sets the used range (<c>dimension</c>) from the cells, for a worksheet that is saved changed.</summary>
    internal void UpdateDimension()
    {
        if (Root.Element(Ns + "dimension") is not { } dimension)
        {
            return;
        }

        int r1 = int.MaxValue, c1 = int.MaxValue, r2 = -1, c2 = -1;
        foreach (var key in _cells.Keys)
        {
            var row = (int)(key >> 16);
            var column = (int)(key & 0xFFFF);
            r1 = Math.Min(r1, row);
            r2 = Math.Max(r2, row);
            c1 = Math.Min(c1, column);
            c2 = Math.Max(c2, column);
        }

        var value = r2 < 0 ? "A1" : r1 == r2 && c1 == c2 ? FormulaReferences.Address(r1, c1) : FormulaReferences.Address(r1, c1) + ":" + FormulaReferences.Address(r2, c2);
        dimension.SetAttributeValue("ref", value);
    }
}
