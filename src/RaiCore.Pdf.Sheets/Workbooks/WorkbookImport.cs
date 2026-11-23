using System.Globalization;
using System.Xml.Linq;
using RaiCore.Pdf.Sheets.Csv;
using RaiCore.Pdf.Sheets.Xls;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Workbooks;

/// <summary>
/// Builds workbook packages in memory: a new workbook, and the read-only model of an Excel 97-2003 workbook or of
/// delimited text (their values, number formats, merged cells, and hidden rows and columns as worksheet parts).
/// </summary>
internal static class WorkbookImport
{
    private const string PackageRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    /// <summary>The sheets of a package to build: their names, whether hidden, and how to fill each worksheet part.</summary>
    internal sealed record SheetSpec(string Name, bool Hidden, Action<XDocument> Fill);

    /// <summary>A new workbook with empty worksheets.</summary>
    public static WorkbookPackage Empty(WorkbookGuard guard, IReadOnlyList<string> names, Action<XDocument> fill) =>
        Build(guard, [.. names.Select(n => new SheetSpec(n, false, fill))], null, false);

    /// <summary>An empty worksheet part.</summary>
    public static XDocument EmptyWorksheet(XNamespace ns, string relationships, bool selected)
    {
        XNamespace r = relationships;
        var view = new XElement(ns + "sheetView", selected ? new XAttribute("tabSelected", 1) : null, new XAttribute("workbookViewId", 0));

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(ns + "worksheet",
            new XAttribute("xmlns", ns.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", r.NamespaceName),
            new XElement(ns + "dimension", new XAttribute("ref", "A1")),
            new XElement(ns + "sheetViews", view),
            new XElement(ns + "sheetFormatPr", new XAttribute("defaultRowHeight", 15)),
            new XElement(ns + "sheetData"),
            new XElement(ns + "pageMargins", new XAttribute("left", 0.7), new XAttribute("right", 0.7), new XAttribute("top", 0.75),
                new XAttribute("bottom", 0.75), new XAttribute("header", 0.3), new XAttribute("footer", 0.3))));
    }

    /// <summary>Builds a package: the workbook part, its worksheets and a styles part (the default one, or <paramref name="styles"/>).</summary>
    private static WorkbookPackage Build(WorkbookGuard guard, List<SheetSpec> sheets, XDocument? styles, bool date1904)
    {
        XNamespace ns = WorkbookNames.Main;
        XNamespace r = WorkbookNames.Relationships;
        var package = WorkbookPackage.Create(guard);
        package.Add(WorkbookPackage.ContentTypesPart, new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"),
                new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"), new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"), new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")))), null);
        package.AddRelationship(string.Empty, PackageRelationships, "xl/workbook.xml");

        var sheetElements = new XElement(ns + "sheets");
        var workbook = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(ns + "workbook",
            new XAttribute("xmlns", ns.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", r.NamespaceName),
            date1904 ? new XElement(ns + "workbookPr", new XAttribute("date1904", 1)) : new XElement(ns + "workbookPr"),
            new XElement(ns + "bookViews", new XElement(ns + "workbookView", new XAttribute("activeTab", Math.Max(0, sheets.FindIndex(s => !s.Hidden))))),
            sheetElements));
        package.Add("xl/workbook.xml", workbook, WorkbookNames.WorkbookType);
        var selected = false;
        for (var i = 0; i < sheets.Count; i++)
        {
            var part = "xl/worksheets/sheet" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".xml";
            var select = !selected && !sheets[i].Hidden;
            selected |= select;
            var document = EmptyWorksheet(ns, r.NamespaceName, select);
            sheets[i].Fill(document);
            package.Add(part, document, WorkbookNames.WorksheetType);
            var id = package.AddRelationship("xl/workbook.xml", WorkbookNames.Relationships + "/worksheet", part);
            var sheet = new XElement(ns + "sheet", new XAttribute("name", sheets[i].Name), new XAttribute("sheetId", i + 1), new XAttribute(r + "id", id));
            if (sheets[i].Hidden)
            {
                sheet.Add(new XAttribute("state", "hidden"));
            }

            sheetElements.Add(sheet);
        }

        package.Add("xl/styles.xml", styles ?? WorkbookStyles.Default(ns), WorkbookNames.StylesType);
        package.AddRelationship("xl/workbook.xml", WorkbookNames.Relationships + "/styles", "xl/styles.xml");
        return package;
    }

    /// <summary>The read-only model of an Excel 97-2003 workbook: its worksheets' values, number formats and layout.</summary>
    public static WorkbookPackage FromXls(byte[] data, WorkbookGuard guard)
    {
        var limits = guard.Limits;
        var (xls, _) = XlsConverter.Open(data, limits, CultureInfo.InvariantCulture, guard.Checkpoint);
        XNamespace ns = WorkbookNames.Main;
        var styles = WorkbookStyles.Default(ns);
        var cellXfs = styles.Root!.Element(ns + "cellXfs")!;
        var numFmts = new XElement(ns + "numFmts");
        styles.Root!.AddFirst(numFmts);
        var styleOf = new Dictionary<int, int>();
        var formatIds = new Dictionary<int, int>();

        int Style(int xf)
        {
            if (styleOf.TryGetValue(xf, out var style))
            {
                return style;
            }

            var id = xf >= 0 && xf < xls.Styles.Xfs.Count ? xls.Styles.Xfs[xf].NumberFormat : 0;
            if (!formatIds.TryGetValue(id, out var numFmtId))
            {
                if (xls.Styles.NumberFormats.TryGetValue(id, out var code))
                {
                    numFmtId = 164 + formatIds.Count;
                    numFmts.Add(new XElement(ns + "numFmt", new XAttribute("numFmtId", numFmtId), new XAttribute("formatCode", code)));
                }
                else
                {
                    numFmtId = id is >= 0 and < 164 ? id : 0;
                }

                formatIds[id] = numFmtId;
            }

            if (numFmtId == 0)
            {
                styleOf[xf] = 0;
                return 0;
            }

            cellXfs.Add(new XElement(ns + "xf", new XAttribute("numFmtId", numFmtId), new XAttribute("fontId", 0), new XAttribute("fillId", 0),
                new XAttribute("borderId", 0), new XAttribute("xfId", 0), new XAttribute("applyNumberFormat", 1)));
            style = cellXfs.Elements().Count() - 1;
            styleOf[xf] = style;
            return style;
        }

        var specs = new List<SheetSpec>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in xls.Sheets.Where(s => s.IsWorksheet))
        {
            var name = UniqueName(entry.Name, names);
            var offset = xls.SheetOffsets[entry.Index];
            specs.Add(new SheetSpec(name, entry.Hidden, document =>
            {
                var layout = new XlsxWorksheet();
                var rows = new SortedDictionary<int, SortedDictionary<int, XElement>>();
                var count = 0L;
                XlsWorksheet.Read(xls, offset, layout, guard.Checkpoint, (row, column, xf, value) =>
                {
                    if (row is < 0 or >= XlsxWorkbook.MaxRows || column is < 0 or >= XlsxWorkbook.MaxColumns)
                    {
                        return;
                    }

                    if (!rows.TryGetValue(row, out var cells))
                    {
                        cells = [];
                        rows[row] = cells;
                    }

                    if (!cells.ContainsKey(column) && ++count > limits.MaxTableCells)
                    {
                        throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), limits.MaxTableCells,
                            $"The worksheet '{name}' has more cells than ResourceLimits.MaxTableCells allows ({limits.MaxTableCells}).");
                    }

                    var c = new XElement(ns + "c", new XAttribute("r", FormulaReferences.Address(row, column)));
                    var style = Style(xf);
                    if (style != 0)
                    {
                        c.Add(new XAttribute("s", style));
                    }

                    switch (value.Kind)
                    {
                        case XlsValueKind.Number when double.IsFinite(value.Number):
                            c.Add(new XElement(ns + "v", value.Number.ToString("R", CultureInfo.InvariantCulture)));
                            break;
                        case XlsValueKind.Text:
                            c.Add(new XAttribute("t", "inlineStr"), new XElement(ns + "is", WorkbookNames.TextElement(ns, value.Text)));
                            break;
                        case XlsValueKind.Boolean:
                            c.Add(new XAttribute("t", "b"), new XElement(ns + "v", value.Number != 0 ? "1" : "0"));
                            break;
                        case XlsValueKind.Error:
                            c.Add(new XAttribute("t", "e"), new XElement(ns + "v", value.Text));
                            break;
                    }

                    cells[column] = c;
                });
                Fill(document, rows, layout);
            }));
        }

        var package = Build(guard, specs, styles, xls.Settings.Date1904);
        if (numFmts.HasElements)
        {
            numFmts.SetAttributeValue("count", numFmts.Elements().Count());
        }
        else
        {
            numFmts.Remove();
        }

        cellXfs.SetAttributeValue("count", cellXfs.Elements().Count());
        return package;
    }

    /// <summary>The read-only model of delimited text: one worksheet of text cells, as written.</summary>
    public static WorkbookPackage FromCsv(byte[] data, WorkbookOptions options, WorkbookGuard guard)
    {
        if (options.CsvDelimiter is '"' or '\r' or '\n')
        {
            throw new ArgumentException("The delimiter cannot be a quote or a line break.", nameof(options));
        }

        var text = CsvText.Decode(data, options.CsvEncoding);
        var (sepDelimiter, sepLength) = CsvDialect.SepLine(text);
        if (sepLength > 0)
        {
            text = text[sepLength..];
        }

        var delimiter = options.CsvDelimiter ?? sepDelimiter ?? CsvDialect.Detect(text);
        var records = CsvParser.Parse(text, delimiter, guard.Limits.MaxTableCells, checkpoint: guard.Checkpoint);
        XNamespace ns = WorkbookNames.Main;
        return Build(guard, [new SheetSpec("Sheet1", false, document =>
        {
            var rows = new SortedDictionary<int, SortedDictionary<int, XElement>>();
            for (var r = 0; r < records.Count && r < XlsxWorkbook.MaxRows; r++)
            {
                var cells = new SortedDictionary<int, XElement>();
                for (var c = 0; c < records[r].Length && c < XlsxWorkbook.MaxColumns; c++)
                {
                    if (records[r][c].Length > 0)
                    {
                        cells[c] = new XElement(ns + "c", new XAttribute("r", FormulaReferences.Address(r, c)), new XAttribute("t", "inlineStr"),
                            new XElement(ns + "is", WorkbookNames.TextElement(ns, records[r][c])));
                    }
                }

                if (cells.Count > 0)
                {
                    rows[r] = cells;
                }

                if ((r & 1023) == 1023)
                {
                    guard.Checkpoint();
                }
            }

            Fill(document, rows, new XlsxWorksheet());
        })], null, false);
    }

    /// <summary>Fills a worksheet part with rows of cells and a layout's row heights and visibility, columns and merged ranges.</summary>
    private static void Fill(XDocument document, SortedDictionary<int, SortedDictionary<int, XElement>> rows, XlsxWorksheet layout)
    {
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var data = root.Element(ns + "sheetData")!;
        foreach (var index in rows.Keys.Union(layout.Rows.Keys).Order())
        {
            var row = new XElement(ns + "row", new XAttribute("r", index + 1));
            if (layout.Rows.TryGetValue(index, out var info))
            {
                if (info.Height is { } height)
                {
                    row.Add(new XAttribute("ht", height.ToString("R", CultureInfo.InvariantCulture)), new XAttribute("customHeight", 1));
                }

                if (info.Hidden)
                {
                    row.Add(new XAttribute("hidden", 1));
                }
            }

            if (rows.TryGetValue(index, out var cells))
            {
                row.Add(cells.Values);
            }

            data.Add(row);
        }

        if (layout.ZeroHeight)
        {
            root.Element(ns + "sheetFormatPr")!.SetAttributeValue("zeroHeight", 1);
        }

        if (layout.Columns.Count > 0)
        {
            var cols = new XElement(ns + "cols");
            foreach (var column in layout.Columns.OrderBy(c => c.First))
            {
                var col = new XElement(ns + "col", new XAttribute("min", column.First + 1), new XAttribute("max", column.Last + 1),
                    new XAttribute("width", (column.Width ?? 9.140625).ToString("R", CultureInfo.InvariantCulture)));
                if (column.Width is not null)
                {
                    col.Add(new XAttribute("customWidth", 1));
                }

                if (column.Hidden)
                {
                    col.Add(new XAttribute("hidden", 1));
                }

                cols.Add(col);
            }

            data.AddBeforeSelf(cols);
        }

        if (layout.Merges.Count > 0)
        {
            var merges = new XElement(ns + "mergeCells", new XAttribute("count", layout.Merges.Count));
            foreach (var merge in layout.Merges)
            {
                merges.Add(new XElement(ns + "mergeCell", new XAttribute("ref",
                    FormulaReferences.Address(merge.Row1, merge.Column1) + ":" + FormulaReferences.Address(merge.Row2, merge.Column2))));
            }

            data.AddAfterSelf(merges);
        }
    }

    /// <summary>A sheet name that no other sheet has (ignoring case): the name, or the name with a number.</summary>
    private static string UniqueName(string name, HashSet<string> names)
    {
        var candidate = name.Length is > 0 and <= 31 ? name : "Sheet" + (names.Count + 1).ToString(CultureInfo.InvariantCulture);
        for (var i = 2; !names.Add(candidate); i++)
        {
            candidate = name[..Math.Min(name.Length, 25)] + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return candidate;
    }
}
