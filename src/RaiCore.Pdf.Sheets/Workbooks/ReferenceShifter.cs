using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Workbooks;

/// <summary>
/// Inserts or deletes rows or columns of a worksheet and moves everything that refers to them, as Excel does: the
/// worksheet's cells, rows, columns and ranges (merged cells, conditional formatting, data validation, hyperlinks,
/// the autofilter, page breaks), the formulas of every sheet, the defined names, the worksheet's tables, drawings and
/// comments, and the ranges of every chart and pivot cache.
/// </summary>
internal static class ReferenceShifter
{
    private const string SpreadsheetDrawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private const string VmlExcel = "urn:schemas-microsoft-com:office:excel";

    /// <summary>The containers that go when their last child goes (several have to have one).</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "mergeCells", "hyperlinks", "dataValidations", "rowBreaks", "colBreaks", "protectedRanges", "ignoredErrors", "conditionalFormattings",
        "sparklineGroup", "sparklineGroups", "cellWatches", "sortState", "ext", "extLst",
    };

    public static void Apply(Workbook workbook, Worksheet target, ShiftOperation operation)
    {
        var package = workbook.Package;
        var relationships = package.Relationships(target.Part);
        Validate(workbook, target, operation, relationships);

        // Shared formulas: their cells get formulas of their own where moving would change them differently.
        foreach (var sheet in workbook.Worksheets)
        {
            var isTarget = sheet == target;
            if (sheet.UnshareFormulas(f => isTarget || FormulaReferences.RefersToSheet(f, sheet.Name, operation.Sheet)))
            {
                package.MarkDirty(sheet.Part);
            }
        }

        var outline = InsertedOutline(target, operation);
        ShiftSheetData(target, operation);
        ShiftColumns(target, operation);
        ShiftRanges(target, operation);
        target.Reindex();
        if (outline is var (level, hidden))
        {
            for (var row = operation.Start; row < operation.End; row++)
            {
                var element = target.GetOrCreateRow(row);
                element.SetAttributeValue("outlineLevel", level);
                element.SetAttributeValue("hidden", hidden ? "1" : null);
            }
        }

        CopyFormatting(target, operation);
        package.MarkDirty(target.Part);
        foreach (var sheet in workbook.Worksheets)
        {
            package.Guard.Checkpoint();
            if (ShiftFormulas(sheet, operation))
            {
                package.MarkDirty(sheet.Part);
            }
        }

        ShiftDefinedNames(workbook, operation);
        ShiftPackageParts(workbook, operation);
        foreach (var relationship in relationships.Where(r => !r.External && package.Contains(r.Target)))
        {
            package.Guard.Checkpoint();
            switch (relationship.ShortType)
            {
                case "table":
                    ShiftTable(workbook, target, relationship, operation);
                    break;
                case "drawing":
                    Update(package, relationship.Target, document => document.Root!.Descendants()
                        .Where(e => e.Name.LocalName is "twoCellAnchor" or "oneCellAnchor").ToList()
                        .Aggregate(false, (changed, anchor) => ShiftAnchor(anchor, operation) | changed));
                    break;
                case "comments":
                    Update(package, relationship.Target, document => ShiftCellAttributes(document, "comment", "ref", operation));
                    break;
                case "threadedComment" or "threadedComments":
                    Update(package, relationship.Target, document => ShiftCellAttributes(document, "threadedComment", "ref", operation));
                    break;
                case "vmlDrawing":
                    ShiftVml(package, relationship.Target, operation);
                    break;
                case "pivotTable":
                    Update(package, relationship.Target, document => document.Root!.Elements().Where(e => e.Name.LocalName == "location").ToList()
                        .Aggregate(false, (changed, location) => ShiftAttribute(location, "ref", operation, removeIfDeleted: false) | changed));
                    break;
            }
        }
    }

    /// <summary>
    /// Throws, before anything changes, when the operation would split an array formula, delete part of a table's
    /// header or totals row, or push cells with values off the sheet.
    /// </summary>
    private static void Validate(Workbook workbook, Worksheet target, ShiftOperation operation, List<PartRelationship> relationships)
    {
        var ns = target.Ns;
        var lastUsed = -1;
        foreach (var c in target.Root.Element(ns + "sheetData")?.Elements(ns + "row").SelectMany(r => r.Elements(ns + "c")) ?? [])
        {
            if (Worksheet.Position(c) is not var (row, column))
            {
                continue;
            }

            if (c.Element(ns + "f") is { } f && (string?)f.Attribute("t") == "array" && Area.Parse((string?)f.Attribute("ref")) is { } array)
            {
                var (r1, c1, r2, c2) = array.Bounds;
                var (low, high) = operation.Rows ? (r1, r2) : (c1, c2);
                var split = operation.Delete
                    ? low < operation.End && high >= operation.Start && !(low >= operation.Start && high < operation.End)
                    : low < operation.Start && operation.Start <= high;
                if (split)
                {
                    throw new InvalidOperationException($"The array formula in {array} on '{target.Name}' would be split; an array formula can only be changed as a whole.");
                }
            }

            if (c.Elements().Any(e => e.Name.LocalName is "v" or "f" or "is"))
            {
                lastUsed = Math.Max(lastUsed, operation.Rows ? row : column);
            }
        }

        if (!operation.Delete && lastUsed >= operation.Start && operation.Index(lastUsed) is null)
        {
            throw new InvalidOperationException($"Inserting {operation.Count} {(operation.Rows ? "rows" : "columns")} would push cells with values off the sheet '{target.Name}'.");
        }

        if (!operation.Rows || !operation.Delete)
        {
            return;
        }

        foreach (var relationship in relationships.Where(r => r.ShortType == "table" && !r.External))
        {
            if (workbook.Package.Xml(relationship.Target)?.Root is not { } table || Area.Parse((string?)table.Attribute("ref")) is not { } area)
            {
                continue;
            }

            var (r1, _, r2, _) = area.Bounds;
            var header = Math.Max(WorkbookNames.Integer(table, "headerRowCount") ?? 1, 0);
            var totals = Math.Max(WorkbookNames.Integer(table, "totalsRowCount") ?? 0, 0);
            var overlaps = r1 < operation.End && r2 >= operation.Start;
            var covered = r1 >= operation.Start && r2 < operation.End;
            if (!overlaps || covered)
            {
                continue;
            }

            var deleted = Math.Min(r2, operation.End - 1) - Math.Max(r1, operation.Start) + 1;
            if ((header > 0 && operation.Start <= r1 + header - 1 && operation.End > r1)
                || (totals > 0 && operation.End > r2 - totals + 1)
                || r2 - r1 + 1 - deleted <= header + totals)
            {
                throw new InvalidOperationException($"Deleting the rows would delete part of the table '{(string?)table.Attribute("name")}' header or totals row, or all its data rows.");
            }
        }
    }

    /// <summary>The outline level (and visibility) rows inserted inside an outline group get: the group's, or <c>null</c>.</summary>
    private static (int Level, bool Hidden)? InsertedOutline(Worksheet target, ShiftOperation operation)
    {
        if (!operation.Rows || operation.Delete || operation.Start == 0
            || target.FindRow(operation.Start - 1) is not { } above || target.FindRow(operation.Start) is not { } below)
        {
            return null;
        }

        var level = Math.Min(WorkbookNames.Integer(above, "outlineLevel") ?? 0, WorkbookNames.Integer(below, "outlineLevel") ?? 0);
        return level > 0 ? (Math.Min(level, 7), WorkbookNames.Flag(above, "hidden") && WorkbookNames.Flag(below, "hidden")) : null;
    }

    /// <summary>
    /// Gives inserted rows the format of the row above (its style and height, and its cells' styles), and inserted
    /// columns that of the column to the left, as Excel does by default. Nothing is copied when it would take the sheet
    /// past <see cref="ResourceLimits.MaxTableCells"/>.
    /// </summary>
    private static void CopyFormatting(Worksheet target, ShiftOperation operation)
    {
        if (operation.Delete || operation.Start == 0)
        {
            return;
        }

        var ns = target.Ns;
        var source = operation.Start - 1;
        var styled = new List<(int Row, int Column, string Style)>();
        foreach (var row in target.Root.Element(ns + "sheetData")?.Elements(ns + "row") ?? [])
        {
            foreach (var c in row.Elements(ns + "c"))
            {
                if ((string?)c.Attribute("s") is { } style and not "0" && Worksheet.Position(c) is var (r, column)
                    && (operation.Rows ? r : column) == source)
                {
                    styled.Add((r, column, style));
                }
            }
        }

        if ((long)styled.Count * operation.Count > target.Workbook.Limits.MaxTableCells - target.CellCount)
        {
            return;
        }

        for (var index = operation.Start; index < operation.End; index++)
        {
            foreach (var (r, column, style) in styled)
            {
                var cell = operation.Rows ? target.GetOrCreateCell(index, column) : target.GetOrCreateCell(r, index);
                cell.SetAttributeValue("s", style);
            }
        }

        if (operation.Rows && target.FindRow(source) is { } above)
        {
            for (var index = operation.Start; index < operation.End; index++)
            {
                var row = target.GetOrCreateRow(index);
                foreach (var name in new[] { "s", "customFormat", "ht", "customHeight" })
                {
                    if (above.Attribute(name) is { } attribute && row.Attribute(name) is null)
                    {
                        row.SetAttributeValue(name, attribute.Value);
                    }
                }
            }
        }
        else if (!operation.Rows && target.FindColumn(source) is { } left && target.FindColumn(operation.Start) is null)
        {
            var copy = new XElement(left);
            copy.SetAttributeValue("min", operation.Start + 1);
            copy.SetAttributeValue("max", operation.End);
            copy.Attribute("hidden")?.Remove();
            copy.Attribute("outlineLevel")?.Remove();
            copy.Attribute("collapsed")?.Remove();
            left.AddAfterSelf(copy);
        }
    }

    /// <summary>Moves the rows and cells of the sheet data, and the ranges of array formulas and data tables.</summary>
    private static void ShiftSheetData(Worksheet target, ShiftOperation operation)
    {
        var ns = target.Ns;
        if (target.Root.Element(ns + "sheetData") is not { } data)
        {
            return;
        }

        foreach (var row in data.Elements(ns + "row").ToList())
        {
            if (Worksheet.RowIndex(row) is not { } index)
            {
                continue;
            }

            if (operation.Rows)
            {
                if (operation.Index(index) is not { } moved)
                {
                    row.Remove();
                    continue;
                }

                if (moved != index)
                {
                    row.SetAttributeValue("r", moved + 1);
                }
            }

            var changedRow = false;
            foreach (var c in row.Elements(ns + "c").ToList())
            {
                if (Worksheet.Position(c) is not var (cellRow, cellColumn))
                {
                    continue;
                }

                var newRow = operation.Rows ? operation.Index(cellRow) : cellRow;
                var newColumn = operation.Rows ? cellColumn : operation.Index(cellColumn);
                if (newRow is null || newColumn is null)
                {
                    c.Remove();
                    changedRow = true;
                    continue;
                }

                if (newRow != cellRow || newColumn != cellColumn)
                {
                    c.SetAttributeValue("r", FormulaReferences.Address(newRow.Value, newColumn.Value));
                    changedRow = true;
                }

                if (c.Element(ns + "f") is { } f)
                {
                    ShiftAttribute(f, "ref", operation, removeIfDeleted: false);
                    if ((string?)f.Attribute("t") == "dataTable")
                    {
                        ShiftAttribute(f, "r1", operation, removeIfDeleted: false);
                        ShiftAttribute(f, "r2", operation, removeIfDeleted: false);
                    }
                }
            }

            if (changedRow && !operation.Rows)
            {
                row.Attribute("spans")?.Remove();
            }
        }
    }

    /// <summary>Moves the column ranges (<c>cols/col</c>) of a column operation: widths, styles, visibility and outline levels.</summary>
    private static void ShiftColumns(Worksheet target, ShiftOperation operation)
    {
        if (operation.Rows || target.Root.Element(target.Ns + "cols") is not { } cols)
        {
            return;
        }

        foreach (var col in cols.Elements(target.Ns + "col").ToList())
        {
            if (WorkbookNames.Integer(col, "min") is not { } min || min < 1)
            {
                continue;
            }

            var max = Math.Max(WorkbookNames.Integer(col, "max") ?? min, min);
            if (operation.Span(min - 1, Math.Min(max, XlsxWorkbook.MaxColumns) - 1) is not var (low, high))
            {
                col.Remove();
                continue;
            }

            if (low != min - 1 || high != max - 1)
            {
                col.SetAttributeValue("min", low + 1);
                col.SetAttributeValue("max", high + 1);
            }
        }

        if (!cols.HasElements)
        {
            cols.Remove();
        }
    }

    /// <summary>
    /// Moves the worksheet's own ranges: merged cells, conditional formatting, data validation, hyperlinks, the
    /// autofilter and its sort, page breaks, protected ranges, ignored errors, watches, sparklines and the extensions'
    /// ranges, and the anchors of controls.
    /// </summary>
    private static void ShiftRanges(Worksheet target, ShiftOperation operation)
    {
        var ns = target.Ns;
        foreach (var section in target.Root.Elements().Where(e => e.Name.LocalName is not ("sheetData" or "cols" or "sheetViews")).ToList())
        {
            foreach (var element in section.DescendantsAndSelf().ToList())
            {
                if (element.Document is null)
                {
                    continue;
                }

                var main = element.Name.Namespace == ns;
                switch (element.Name.LocalName)
                {
                    case "dimension" when main:
                        if (Area.Parse((string?)element.Attribute("ref")) is { } dimension)
                        {
                            element.SetAttributeValue("ref", (FormulaReferences.Shift(dimension, operation) ?? new Area(AreaKind.Cell, default, default)).ToString());
                        }

                        break;
                    case "mergeCell" when main:
                        if (Area.Parse((string?)element.Attribute("ref")) is { } merge
                            && (FormulaReferences.Shift(merge, operation) is not { } moved || moved.Kind == AreaKind.Cell || moved.Bounds is var (r1, c1, r2, c2) && r1 == r2 && c1 == c2))
                        {
                            Remove(element);
                        }
                        else
                        {
                            ShiftAttribute(element, "ref", operation, removeIfDeleted: true);
                        }

                        break;
                    case "autoFilter" when main:
                        ShiftAutoFilter(element, operation);
                        break;
                    case "sortState" or "sortCondition" when main:
                        ShiftAttribute(element, "ref", operation, removeIfDeleted: true);
                        break;
                    case "hyperlink" when main:
                        ShiftAttribute(element, "ref", operation, removeIfDeleted: true);
                        break;
                    case "conditionalFormatting" or "dataValidation" or "protectedRange" or "ignoredError" when main:
                        if (element.Attribute("sqref") is { } sqref)
                        {
                            if (FormulaReferences.ShiftList(sqref.Value, operation) is { } list)
                            {
                                sqref.Value = list;
                            }
                            else
                            {
                                Remove(element);
                            }
                        }

                        break;
                    case "cellWatch" when main:
                        ShiftAttribute(element, "r", operation, removeIfDeleted: true);
                        break;
                    case "inputCells" when main:
                        ShiftAttribute(element, "r", operation, removeIfDeleted: false);
                        break;
                    case "brk" when main && element.Parent?.Name.LocalName == (operation.Rows ? "rowBreaks" : "colBreaks"):
                        if (WorkbookNames.Integer(element, "id") is { } id and >= 0)
                        {
                            if (operation.Index(id) is { } movedId)
                            {
                                element.SetAttributeValue("id", movedId);
                            }
                            else
                            {
                                Remove(element);
                            }
                        }

                        break;
                    case "sqref" when !main:
                        // An extension's range (x14 conditional formatting, data validation, sparklines): xm:sqref.
                        if (FormulaReferences.ShiftList(element.Value, operation) is { } extended)
                        {
                            if (extended != element.Value)
                            {
                                element.Value = extended;
                            }
                        }
                        else
                        {
                            Remove(element.Parent ?? element);
                        }

                        break;
                    case "anchor" when element.Elements().Any(e => e.Name.LocalName == "from"):
                        ShiftAnchor(element, operation);
                        break;
                }
            }
        }

        UpdateCounts(target);
    }

    /// <summary>Sets the counts of the worksheet's lists after items went.</summary>
    private static void UpdateCounts(Worksheet target)
    {
        var ns = target.Ns;
        foreach (var name in new[] { "mergeCells", "dataValidations", "rowBreaks", "colBreaks" })
        {
            if (target.Root.Element(ns + name) is { } list && list.Attribute("count") is not null)
            {
                list.SetAttributeValue("count", list.Elements().Count());
                if (list.Attribute("manualBreakCount") is not null)
                {
                    list.SetAttributeValue("manualBreakCount", list.Elements().Count(e => WorkbookNames.Flag(e, "man")));
                }
            }
        }

        foreach (var list in target.Root.Descendants().Where(e => e.Name.Namespace != ns && e.Name.LocalName == "dataValidations" && e.Attribute("count") is not null))
        {
            list.SetAttributeValue("count", list.Elements().Count(e => e.Name.LocalName == "dataValidation"));
        }
    }

    /// <summary>Removes an element, and the containers it leaves empty.</summary>
    private static void Remove(XElement element)
    {
        var parent = element.Parent;
        element.Remove();
        while (parent is not null && !parent.HasElements && Containers.Contains(parent.Name.LocalName))
        {
            var next = parent.Parent;
            parent.Remove();
            parent = next;
        }
    }

    /// <summary>Moves an attribute that holds an area; removes the element (or leaves it) when the area is deleted.</summary>
    private static bool ShiftAttribute(XElement element, string name, ShiftOperation operation, bool removeIfDeleted)
    {
        if (element.Attribute(name) is not { } attribute || Area.Parse(attribute.Value) is not { } area)
        {
            return false;
        }

        if (FormulaReferences.Shift(area, operation) is not { } moved)
        {
            if (removeIfDeleted)
            {
                Remove(element);
                return true;
            }

            return false;
        }

        if (moved == area)
        {
            return false;
        }

        attribute.Value = moved.ToString();
        return true;
    }

    /// <summary>Moves an autofilter's range and the columns of its filters; removes it if its range is deleted.</summary>
    private static bool ShiftAutoFilter(XElement filter, ShiftOperation operation)
    {
        if (Area.Parse((string?)filter.Attribute("ref")) is not { } area)
        {
            return false;
        }

        if (FormulaReferences.Shift(area, operation) is not { } moved)
        {
            Remove(filter);
            return true;
        }

        if (moved == area)
        {
            return false;
        }

        filter.SetAttributeValue("ref", moved.ToString());
        if (!operation.Rows)
        {
            var first = area.Bounds.Column1;
            var newFirst = moved.Bounds.Column1;
            foreach (var column in filter.Elements().Where(e => e.Name.LocalName == "filterColumn").ToList())
            {
                if (WorkbookNames.Integer(column, "colId") is not { } id)
                {
                    continue;
                }

                if (operation.Index(first + id) is { } index)
                {
                    column.SetAttributeValue("colId", index - newFirst);
                }
                else
                {
                    column.Remove();
                }
            }
        }

        foreach (var sort in filter.Elements().Where(e => e.Name.LocalName == "sortState").ToList())
        {
            ShiftAttribute(sort, "ref", operation, removeIfDeleted: true);
            foreach (var condition in sort.Elements().Where(e => e.Name.LocalName == "sortCondition").ToList())
            {
                ShiftAttribute(condition, "ref", operation, removeIfDeleted: true);
            }
        }

        return true;
    }

    /// <summary>Moves the references in a worksheet's formulas: cells, conditional formatting, data validation, extensions and hyperlink locations.</summary>
    private static bool ShiftFormulas(Worksheet sheet, ShiftOperation operation)
    {
        var ns = sheet.Ns;
        var changed = false;
        foreach (var row in sheet.Root.Element(ns + "sheetData")?.Elements(ns + "row") ?? [])
        {
            foreach (var f in row.Elements(ns + "c").Select(c => c.Element(ns + "f")).OfType<XElement>())
            {
                changed |= ShiftText(f, sheet.Name, operation);
            }
        }

        foreach (var section in sheet.Root.Elements().Where(e => e.Name.LocalName != "sheetData"))
        {
            foreach (var element in section.DescendantsAndSelf())
            {
                switch (element.Name.LocalName)
                {
                    case "formula" or "formula1" or "formula2" when element.Name.Namespace == ns && !element.HasElements:
                    case "f" when element.Name.Namespace != ns && !element.HasElements:
                        changed |= ShiftText(element, sheet.Name, operation);
                        break;
                    case "hyperlink" when element.Attribute("location") is { } location:
                        var shifted = FormulaReferences.Shift(location.Value, sheet.Name, operation);
                        if (!ReferenceEquals(shifted, location.Value))
                        {
                            location.Value = shifted;
                            changed = true;
                        }

                        break;
                }
            }
        }

        return changed;
    }

    /// <summary>Moves the references in an element's formula text.</summary>
    private static bool ShiftText(XElement element, string? sheet, ShiftOperation operation)
    {
        var text = element.Value;
        if (text.Length == 0)
        {
            return false;
        }

        var shifted = FormulaReferences.Shift(text, sheet, operation);
        if (ReferenceEquals(shifted, text))
        {
            return false;
        }

        element.Value = shifted;
        return true;
    }

    /// <summary>Moves the references of the defined names (print areas and titles too); local names' own sheet is the one without a name.</summary>
    private static void ShiftDefinedNames(Workbook workbook, ShiftOperation operation)
    {
        var changed = false;
        foreach (var name in workbook.WorkbookXml.Root!.Element(workbook.Ns + "definedNames")?.Elements(workbook.Ns + "definedName") ?? [])
        {
            var local = WorkbookNames.Integer(name, "localSheetId");
            var sheet = local is { } index && index >= 0 && index < workbook.AllSheets.Count ? workbook.AllSheets[index].Name : null;
            changed |= ShiftText(name, sheet, operation);
        }

        if (changed)
        {
            workbook.Package.MarkDirty(workbook.WorkbookPart);
        }
    }

    /// <summary>
    /// Moves the references of the parts any sheet may refer from: the series and labels of charts, the sources of
    /// pivot caches, and the formulas of tables.
    /// </summary>
    private static void ShiftPackageParts(Workbook workbook, ShiftOperation operation)
    {
        var package = workbook.Package;
        var tableSheets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in workbook.Worksheets)
        {
            foreach (var relationship in package.Relationships(sheet.Part).Where(r => r.ShortType == "table" && !r.External))
            {
                tableSheets.TryAdd(relationship.Target, sheet.Name);
            }
        }

        foreach (var part in package.PartNames.ToList())
        {
            var type = package.ContentType(part) ?? string.Empty;
            if (type.EndsWith("drawingml.chart+xml", StringComparison.Ordinal) || type.EndsWith("chartex+xml", StringComparison.Ordinal))
            {
                package.Guard.Checkpoint();
                Update(package, part, document => document.Root!.Descendants().Where(e => e.Name.LocalName == "f" && !e.HasElements).ToList()
                    .Aggregate(false, (changed, f) => ShiftText(f, null, operation) | changed));
            }
            else if (type.EndsWith("pivotCacheDefinition+xml", StringComparison.Ordinal))
            {
                Update(package, part, document => document.Root!.Descendants().Where(e => e.Name.LocalName == "worksheetSource"
                        && operation.IsSheet((string?)e.Attribute("sheet"))).ToList()
                    .Aggregate(false, (changed, source) => ShiftAttribute(source, "ref", operation, removeIfDeleted: false) | changed));
            }
            else if (tableSheets.TryGetValue(part, out var tableSheet))
            {
                Update(package, part, document => document.Root!.Descendants().Where(e => e.Name.LocalName is "calculatedColumnFormula" or "totalsRowFormula").ToList()
                    .Aggregate(false, (changed, f) => ShiftText(f, tableSheet, operation) | changed));
            }
        }
    }

    /// <summary>Changes a part's XML with <paramref name="change"/>, and marks it changed if it says it did.</summary>
    private static void Update(WorkbookPackage package, string part, Func<XDocument, bool> change)
    {
        if (package.Xml(part) is { } document && change(document))
        {
            package.MarkDirty(part);
        }
    }

    /// <summary>
    /// Moves a table of the changed worksheet: its range, its autofilter and sort, and its columns (a column operation
    /// inside it adds or removes table columns; inserted ones get unique names in their header cells). A table whose
    /// range is deleted is removed.
    /// </summary>
    private static void ShiftTable(Workbook workbook, Worksheet target, PartRelationship relationship, ShiftOperation operation)
    {
        var package = workbook.Package;
        if (package.Xml(relationship.Target)?.Root is not { } table || Area.Parse((string?)table.Attribute("ref")) is not { } area)
        {
            return;
        }

        if (FormulaReferences.Shift(area, operation) is not { } moved)
        {
            RemoveTable(workbook, target, relationship);
            return;
        }

        if (moved == area)
        {
            return;
        }

        table.SetAttributeValue("ref", moved.ToString());
        var ns = table.Name.Namespace;
        var (r1, c1, _, c2) = area.Bounds;
        if (!operation.Rows && table.Element(ns + "tableColumns") is { } columns)
        {
            var list = columns.Elements(ns + "tableColumn").ToList();
            if (operation.Delete)
            {
                for (var k = 0; k < list.Count; k++)
                {
                    if (c1 + k >= operation.Start && c1 + k < operation.End)
                    {
                        list[k].Remove();
                    }
                }
            }
            else if (operation.Start > c1 && operation.Start <= c2)
            {
                var names = list.Select(c => (string?)c.Attribute("name") ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var id = list.Select(c => WorkbookNames.Integer(c, "id") ?? 0).DefaultIfEmpty(0).Max();
                var at = operation.Start - c1;
                var header = (WorkbookNames.Integer(table, "headerRowCount") ?? 1) > 0;
                var added = new List<XElement>();
                var number = list.Count;
                for (var k = 0; k < operation.Count && at + k <= XlsxWorkbook.MaxColumns; k++)
                {
                    string name;
                    do
                    {
                        name = "Column" + (++number).ToString(CultureInfo.InvariantCulture);
                    }
                    while (!names.Add(name));

                    added.Add(new XElement(ns + "tableColumn", new XAttribute("id", ++id), new XAttribute("name", name)));
                    if (header)
                    {
                        new Cell(target, r1, operation.Start + k).SetValue(name);
                    }
                }

                if (at < list.Count)
                {
                    list[at].AddBeforeSelf(added);
                }
                else
                {
                    columns.Add(added);
                }
            }

            columns.SetAttributeValue("count", columns.Elements(ns + "tableColumn").Count());
        }

        foreach (var filter in table.Elements(ns + "autoFilter").ToList())
        {
            ShiftAutoFilter(filter, operation);
        }

        foreach (var sort in table.Elements(ns + "sortState").ToList())
        {
            ShiftAttribute(sort, "ref", operation, removeIfDeleted: true);
            foreach (var condition in sort.Elements(ns + "sortCondition").ToList())
            {
                ShiftAttribute(condition, "ref", operation, removeIfDeleted: true);
            }
        }

        package.MarkDirty(relationship.Target);
    }

    /// <summary>Removes a table whose cells were all deleted: its part, relationship and <c>tablePart</c>.</summary>
    private static void RemoveTable(Workbook workbook, Worksheet target, PartRelationship relationship)
    {
        var ns = target.Ns;
        if (target.Root.Element(ns + "tableParts") is { } parts)
        {
            foreach (var part in parts.Elements(ns + "tablePart").Where(p => WorkbookNames.RelationshipId(p) == relationship.Id).ToList())
            {
                part.Remove();
            }

            if (parts.HasElements)
            {
                parts.SetAttributeValue("count", parts.Elements(ns + "tablePart").Count());
            }
            else
            {
                parts.Remove();
            }
        }

        workbook.Package.RemoveRelationship(target.Part, relationship.Id);
        workbook.Package.Remove(relationship.Target);
    }

    /// <summary>
    /// Moves a drawing anchor (<c>from</c>, <c>to</c>): one that moves and sizes with cells (the default) by each edge,
    /// one that only moves with them by its top left corner, and not one that is absolute. Edges in deleted rows or
    /// columns go to where they were.
    /// </summary>
    private static bool ShiftAnchor(XElement anchor, ShiftOperation operation)
    {
        var from = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "from");
        var to = anchor.Elements().FirstOrDefault(e => e.Name.LocalName == "to");
        if (from is null || Point(from, operation) is not var (fromElement, fromIndex))
        {
            return false;
        }

        var mode = anchor.Name.LocalName == "oneCellAnchor" ? "oneCell" : (string?)anchor.Attribute("editAs") ?? "twoCell";
        if (mode == "absolute")
        {
            return false;
        }

        var newFrom = operation.Boundary(fromIndex, out var fromDeleted);
        var changed = SetPoint(from, fromElement, newFrom, fromDeleted, operation);
        if (to is not null && Point(to, operation) is var (toElement, toIndex))
        {
            var newTo = mode == "twoCell" ? operation.Boundary(toIndex, out var toDeleted)
                : Math.Min(Math.Max(toIndex + (newFrom - fromIndex), newFrom), operation.Max);
            toDeleted = mode == "twoCell" && toIndex >= operation.Start && toIndex < operation.End && operation.Delete;
            changed |= SetPoint(to, toElement, newTo, toDeleted, operation);
        }

        return changed;

        static (XElement Element, int Index)? Point(XElement point, ShiftOperation operation)
        {
            var element = point.Elements().FirstOrDefault(e => e.Name.LocalName == (operation.Rows ? "row" : "col"));
            return element is not null && int.TryParse(element.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? (element, index) : null;
        }

        static bool SetPoint(XElement point, XElement element, int index, bool deleted, ShiftOperation operation)
        {
            var changed = false;
            var text = index.ToString(CultureInfo.InvariantCulture);
            if (element.Value != text)
            {
                element.Value = text;
                changed = true;
            }

            if (deleted && point.Elements().FirstOrDefault(e => e.Name.LocalName == (operation.Rows ? "rowOff" : "colOff")) is { } offset && offset.Value != "0")
            {
                offset.Value = "0";
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>Moves the cell references of elements (comments); elements on deleted cells are removed.</summary>
    private static bool ShiftCellAttributes(XDocument document, string elementName, string attributeName, ShiftOperation operation)
    {
        var changed = false;
        foreach (var element in document.Root!.Descendants().Where(e => e.Name.LocalName == elementName).ToList())
        {
            changed |= ShiftAttribute(element, attributeName, operation, removeIfDeleted: true);
        }

        return changed;
    }

    /// <summary>
    /// Moves the shapes of a VML drawing (the legacy one comments and form controls use): a note's cell and every
    /// shape's anchor. Notes on deleted cells are removed. A VML part that is not well-formed XML is left as it is.
    /// </summary>
    private static void ShiftVml(WorkbookPackage package, string part, ShiftOperation operation)
    {
        XDocument? document;
        try
        {
            document = package.Xml(part);
        }
        catch (SpreadsheetFormatException)
        {
            return;
        }

        if (document?.Root is null)
        {
            return;
        }

        XNamespace x = VmlExcel;
        var changed = false;
        foreach (var data in document.Root.Descendants(x + "ClientData").ToList())
        {
            if ((string?)data.Attribute("ObjectType") == "Note"
                && data.Element(x + (operation.Rows ? "Row" : "Column")) is { } position
                && int.TryParse(position.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                if (operation.Index(index) is not { } moved)
                {
                    (data.Parent ?? data).Remove();
                    changed = true;
                    continue;
                }

                if (moved != index)
                {
                    position.Value = moved.ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
            }

            if (data.Element(x + "Anchor") is { } anchor)
            {
                var values = anchor.Value.Split(',');
                if (values.Length == 8 && values.All(v => int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                {
                    var numbers = values.Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
                    var (first, second) = operation.Rows ? (2, 6) : (0, 4);
                    numbers[first] = operation.Boundary(numbers[first], out _);
                    numbers[second] = Math.Max(operation.Boundary(numbers[second], out _), numbers[first]);
                    var text = string.Join(", ", numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
                    if (numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)).SequenceEqual(values.Select(v => v.Trim())) is false)
                    {
                        anchor.Value = text;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            package.MarkDirty(part);
        }
    }
}
