using System.Globalization;
using System.Xml;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>A row's size and visibility.</summary>
internal readonly record struct XlsxRow(double? Height, bool Hidden);

/// <summary>The width (in characters) and visibility of a run of columns, from 0.</summary>
internal readonly record struct XlsxColumns(int First, int Last, double? Width, bool Hidden);

/// <summary>
/// A worksheet part as read: its cells (formatted for display), row and column sizes, merged ranges and page setup.
/// Only cells that show something (a value, a fill or a border) are kept, and they count against the cell limit.
/// </summary>
internal sealed class XlsxWorksheet
{
    /// <summary>A worksheet without cells (its part is missing).</summary>
    public static XlsxWorksheet Empty => new();

    /// <summary>The cells, by <see cref="Key"/>.</summary>
    public Dictionary<long, SheetCell> Cells { get; } = [];

    /// <summary>The last row and column with a cell that shows something, or -1.</summary>
    public int LastRow { get; private set; } = -1;

    public int LastColumn { get; private set; } = -1;

    public Dictionary<int, XlsxRow> Rows { get; } = [];

    public List<XlsxColumns> Columns { get; } = [];

    public List<CellRange> Merges { get; } = [];

    public bool Landscape { get; set; }

    /// <summary>The print scale in percent, or <c>null</c>.</summary>
    public int? Scale { get; set; }

    /// <summary>Whether the sheet is shrunk to fit pages (<c>sheetPr/pageSetUpPr/@fitToPage</c>).</summary>
    public bool FitToPage { get; set; }

    /// <summary>The number of pages the width is fitted to (0 for no limit).</summary>
    public int FitToWidth { get; set; } = 1;

    /// <summary>The width of columns without one, in characters, or <c>null</c>.</summary>
    public double? DefaultColumnWidth { get; set; }

    /// <summary>The width of columns without one in characters without the padding, or <c>null</c>.</summary>
    public double? BaseColumnWidth { get; set; }

    /// <summary>Whether rows are hidden unless the sheet lists them.</summary>
    public bool ZeroHeight { get; set; }

    public bool RightToLeft { get; set; }

    /// <summary>
    /// What the sheet has that is left out or simplified, as far as its own part (or record stream) tells: conditional
    /// formatting, headers and footers, page breaks, embedded objects, sparklines, and cells with rich, rotated or shrunk
    /// text. The parts it refers to (drawings, comments) are counted by <see cref="XlsxContent"/>.
    /// </summary>
    public UnsupportedContent Unsupported { get; } = new();

    /// <summary>The key of a cell in <see cref="Cells"/>.</summary>
    public static long Key(int row, int column) => ((long)row << 16) | (uint)column;

    /// <summary>Reads a worksheet part.</summary>
    /// <param name="reader">The part's XML.</param>
    /// <param name="workbook">The workbook, for shared strings, styles and the date system.</param>
    /// <param name="maxCells">The most cells that show something the sheet may have.</param>
    /// <param name="checkpoint">Called regularly; throws to stop.</param>
    public static XlsxWorksheet Read(XmlReader reader, XlsxWorkbook workbook, long maxCells, Action? checkpoint)
    {
        var sheet = new XlsxWorksheet();
        var section = string.Empty;
        int row = -1, column = -1;
        var read = 0L;
        var fallbackDepth = -1;
        HashSet<string>? extendedRules = null;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var depth = reader.Depth;
            if (fallbackDepth >= 0 && depth > fallbackDepth)
            {
                continue; // The fallback of markup compatibility repeats its choice for older readers.
            }

            fallbackDepth = -1;
            if (reader.LocalName == "Fallback" && reader.NamespaceURI == XlsxContent.MarkupCompatibility)
            {
                fallbackDepth = depth;
                continue;
            }

            if (depth == 1)
            {
                section = reader.LocalName;
                switch (section)
                {
                    case "sheetFormatPr":
                        sheet.DefaultColumnWidth = Width(reader.GetAttribute("defaultColWidth"));
                        sheet.BaseColumnWidth = Width(reader.GetAttribute("baseColWidth"));
                        sheet.ZeroHeight = reader.GetAttribute("zeroHeight") is "1" or "true";
                        break;
                    case "pageSetup":
                        sheet.Landscape = reader.GetAttribute("orientation") == "landscape";
                        sheet.Scale = Integer(reader.GetAttribute("scale"));
                        sheet.FitToWidth = Integer(reader.GetAttribute("fitToWidth")) ?? 1;
                        break;
                }

                continue;
            }

            switch (section, depth, reader.LocalName)
            {
                case ("sheetPr", 2, "pageSetUpPr"):
                    sheet.FitToPage = reader.GetAttribute("fitToPage") is "1" or "true";
                    break;
                case ("sheetViews", 2, "sheetView"):
                    sheet.RightToLeft |= reader.GetAttribute("rightToLeft") is "1" or "true";
                    break;
                case ("cols", 2, "col"):
                    var first = Integer(reader.GetAttribute("min"));
                    var last = Integer(reader.GetAttribute("max")) ?? first;
                    if (first is >= 1 and <= XlsxWorkbook.MaxColumns && last >= first)
                    {
                        sheet.Columns.Add(new XlsxColumns(first.Value - 1, Math.Min(last.Value, XlsxWorkbook.MaxColumns) - 1,
                            Width(reader.GetAttribute("width")), reader.GetAttribute("hidden") is "1" or "true"));
                    }

                    break;
                case ("mergeCells", 2, "mergeCell"):
                    if (reader.GetAttribute("ref") is { } reference && XlsxWorkbook.ParseRange(reference) is { Row1: >= 0, Column1: >= 0 } range)
                    {
                        sheet.Merges.Add(range);
                    }

                    break;
                case ("sheetData", 2, "row"):
                    row = Integer(reader.GetAttribute("r")) is { } r ? r - 1 : row + 1;
                    column = -1;
                    if (row is >= 0 and < XlsxWorkbook.MaxRows)
                    {
                        var height = Width(reader.GetAttribute("ht"));
                        var custom = reader.GetAttribute("customHeight") is "1" or "true";
                        sheet.Rows[row] = new XlsxRow(custom ? height : null, reader.GetAttribute("hidden") is "1" or "true");
                    }

                    break;
                case ("sheetData", 3, "c"):
                    if (++read % 4096 == 0)
                    {
                        checkpoint?.Invoke();
                    }

                    sheet.ReadCell(reader, workbook, ref row, ref column, maxCells);
                    break;
                default:
                    sheet.ReadUnsupported(reader, section, ref extendedRules);
                    break;
            }
        }

        return sheet;
    }

    /// <summary>
    /// Counts what an element of the sheet's other sections holds that is not supported: conditional formatting rules
    /// (those of the extension list too, unless a rule of the main list stands for them), sparklines, header and footer
    /// texts, page breaks set by hand, and embedded objects.
    /// </summary>
    private void ReadUnsupported(XmlReader reader, string section, ref HashSet<string>? extendedRules)
    {
        switch (section, reader.LocalName)
        {
            case ("conditionalFormatting", "cfRule"):
                Unsupported.Add(SheetDiagnosticKind.ConditionalFormatting);
                break;
            case ("conditionalFormatting", "id"):
                // The extension list's rule that adds to this one (a data bar of Excel 2010).
                (extendedRules ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(XlsxWorkbook.ReadText(reader).Trim());
                break;
            case ("extLst", "cfRule"):
                if (reader.GetAttribute("id") is not { } id || extendedRules?.Contains(id.Trim()) != true)
                {
                    Unsupported.Add(SheetDiagnosticKind.ConditionalFormatting);
                }

                break;
            case ("extLst", "sparkline"):
                Unsupported.Add(SheetDiagnosticKind.Sparkline);
                break;
            case ("headerFooter", "oddHeader" or "oddFooter" or "evenHeader" or "evenFooter" or "firstHeader" or "firstFooter"):
                if (!string.IsNullOrWhiteSpace(XlsxWorkbook.ReadText(reader)))
                {
                    Unsupported.Add(SheetDiagnosticKind.HeaderFooter);
                }

                break;
            case ("rowBreaks" or "colBreaks", "brk"):
                if (reader.GetAttribute("man") is "1" or "true")
                {
                    Unsupported.Add(SheetDiagnosticKind.PageBreak);
                }

                break;
            case ("oleObjects", "oleObject"):
                Unsupported.Add(SheetDiagnosticKind.OleObject);
                break;
        }
    }

    private void ReadCell(XmlReader reader, XlsxWorkbook workbook, ref int row, ref int column, long maxCells)
    {
        if (reader.GetAttribute("r") is { } reference && XlsxWorkbook.ParseReference(reference) is ( >= 0, >= 0) position)
        {
            (row, column) = position;
        }
        else
        {
            column++;
        }

        var type = reader.GetAttribute("t");
        var style = Integer(reader.GetAttribute("s")) ?? 0;
        string? value = null, inline = null;
        var rich = false;
        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            while (reader.Read() && reader.Depth > depth)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
                {
                    switch (reader.LocalName)
                    {
                        case "v":
                            value = XlsxWorkbook.ReadText(reader);
                            break;
                        case "is":
                            inline = XlsxWorkbook.ReadRichText(reader, out rich);
                            break;
                    }
                }
            }
        }

        if (row is < 0 or >= XlsxWorkbook.MaxRows || column is < 0 or >= XlsxWorkbook.MaxColumns)
        {
            return;
        }

        var cell = workbook.Cell(type, value, inline, style);
        Add(row, column, cell, maxCells);
        if (cell.Kind != CellKind.Empty)
        {
            rich = type == "s" ? workbook.RichStrings.Count > 0 && Integer(value) is { } index && workbook.RichStrings.Contains(index) : rich && type == "inlineStr";
            CountFormatting(workbook.Styles.CellStyle(style), rich);
        }
    }

    /// <summary>Counts a cell that shows text with formatting that is not supported: rich text, rotation, shrinking to fit.</summary>
    internal void CountFormatting(XlsxCellStyle style, bool rich)
    {
        if (rich)
        {
            Unsupported.Add(SheetDiagnosticKind.RichText);
        }

        if (style.Rotated)
        {
            Unsupported.Add(SheetDiagnosticKind.RotatedText);
        }

        if (style.ShrinkToFit)
        {
            Unsupported.Add(SheetDiagnosticKind.ShrinkToFit);
        }
    }

    /// <summary>
    /// Sets a cell (in range) if it shows something (a value, a fill or a border), and removes it otherwise.
    /// </summary>
    /// <exception cref="ResourceLimitExceededException">The sheet has more such cells than <paramref name="maxCells"/>.</exception>
    internal void Add(int row, int column, SheetCell cell, long maxCells)
    {
        if (cell.Kind == CellKind.Empty && !(cell.Format?.IsVisible ?? false))
        {
            Cells.Remove(Key(row, column));
            return;
        }

        Cells[Key(row, column)] = cell;
        if (Cells.Count > maxCells)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxTableCells), maxCells,
                $"The worksheet has more cells than ResourceLimits.MaxTableCells allows ({maxCells}).");
        }

        LastRow = Math.Max(LastRow, row);
        LastColumn = Math.Max(LastColumn, column);
    }

    private static int? Integer(string? value) =>
        int.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? Width(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) && result >= 0
            ? Math.Min(result, 2000)
            : null;
}
