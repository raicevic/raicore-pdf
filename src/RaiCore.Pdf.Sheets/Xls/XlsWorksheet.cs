using System.Buffers.Binary;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>What a cell record holds.</summary>
internal enum XlsValueKind
{
    /// <summary>Nothing (a cell with a format only).</summary>
    Blank,

    Number,

    Text,

    Boolean,

    /// <summary>An error value (<c>#DIV/0!</c>), as its text.</summary>
    Error,
}

/// <summary>
/// The value of a cell record, as stored: formulas are their last calculated values. <see cref="Runs"/> are the
/// formatting runs of rich text (the first character and the font index of each), or <c>null</c>.
/// </summary>
internal readonly record struct XlsValue(XlsValueKind Kind, double Number = 0, string Text = "", IReadOnlyList<(int First, int Font)>? Runs = null)
{
    public static XlsValue Blank => new(XlsValueKind.Blank);

    /// <summary>The text of an error code ([MS-XLS] 2.5.10).</summary>
    public static XlsValue Error(int code) => new(XlsValueKind.Error, code, code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        0x2A => "#N/A",
        0x2B => "#GETTING_DATA",
        _ => "#N/A",
    });
}

/// <summary>
/// Reads a worksheet substream of a binary workbook ([MS-XLS] 2.1.7.20.5): its cells, row and column sizes and
/// visibility, merged ranges, page setup, headers and footers, page breaks, comments and conditional formatting, into
/// the worksheet the XLSX converter builds sheets from. Embedded substreams (charts) are skipped, and the records of
/// content it does not support only counted.
/// </summary>
internal static class XlsWorksheet
{
    // Record types ([MS-XLS] 2.3).
    private const ushort Row = 0x0208;
    private const ushort ColInfo = 0x007D;
    private const ushort DefColWidth = 0x0055;
    private const ushort StandardWidth = 0x0099;
    private const ushort DefaultRowHeight = 0x0225;
    private const ushort WsBool = 0x0081;
    private const ushort Setup = 0x00A1;
    private const ushort Window2 = 0x023E;
    private const ushort MergedCells = 0x00E5;
    private const ushort LabelSst = 0x00FD;
    private const ushort Label = 0x0204;
    private const ushort RString = 0x00D6;
    private const ushort Number = 0x0203;
    private const ushort Rk = 0x027E;
    private const ushort MulRk = 0x00BD;
    private const ushort Blank = 0x0201;
    private const ushort MulBlank = 0x00BE;
    private const ushort BoolErr = 0x0205;
    private const ushort Formula = 0x0006;
    private const ushort Formula3 = 0x0206;
    private const ushort Formula4 = 0x0406;
    private const ushort StringRecord = 0x0207;
    private const ushort Obj = 0x005D;
    private const ushort Note = 0x001C;
    private const ushort Cf = 0x01B1;
    private const ushort Cf12 = 0x087A;
    private const ushort CfHeader = 0x01B0;
    private const ushort CfHeader12 = 0x0879;
    private const ushort CfEx = 0x087B;
    private const ushort Header = 0x0014;
    private const ushort Footer = 0x0015;
    private const ushort HeaderFooter = 0x089C;
    private const ushort HorizontalPageBreaks = 0x001B;
    private const ushort VerticalPageBreaks = 0x001A;
    private const ushort SxView = 0x00B0;
    private const ushort Sxli = 0x00B5;
    private const ushort SxAddl = 0x0864;
    private const ushort MsoDrawing = 0x00EC;
    private const ushort Txo = 0x01B6;

    /// <summary>The BOF type of a worksheet (or dialog sheet).</summary>
    private const int WorksheetType = 0x0010;

    /// <summary>The most columns a BIFF8 worksheet has.</summary>
    private const int MaxColumns = 256;

    /// <summary>Called for each cell record: its row, column, XF index and value.</summary>
    public delegate void CellSink(int row, int column, int xf, XlsValue value);

    /// <summary>Reads a worksheet into a worksheet the sheet builder uses, its cells formatted for display.</summary>
    /// <param name="workbook">The workbook, for its stream, strings and formats.</param>
    /// <param name="offset">The offset of the sheet's BOF record in the stream.</param>
    /// <param name="maxCells">The most cells that show something the sheet may have.</param>
    /// <param name="checkpoint">Called regularly; throws to stop.</param>
    /// <param name="limits">The limits its drawing is read within, or <c>null</c> to leave its drawing out (its objects only counted).</param>
    public static XlsxWorksheet Read(XlsWorkbook workbook, int offset, long maxCells, Action? checkpoint, ResourceLimits? limits = null)
    {
        var sheet = new XlsxWorksheet();
        var colored = new Dictionary<(CellFormat, PdfColor), CellFormat>();
        var styles = workbook.Styles;
        Read(workbook, offset, sheet, checkpoint, (row, column, xf, value) =>
        {
            var style = styles.CellStyle(xf);
            var alignment = style.Alignment;
            FormattedValue formatted;
            IReadOnlyList<CellRun>? runs = null;
            switch (value.Kind)
            {
                case XlsValueKind.Number:
                    formatted = style.NumberFormat.Format(value.Number, workbook.Settings);
                    break;
                case XlsValueKind.Text:
                    formatted = style.NumberFormat.FormatText(value.Text);
                    runs = value.Runs is { Count: > 0 } && formatted.Text == value.Text ? styles.Runs(value.Text, value.Runs) : null;
                    break;
                case XlsValueKind.Boolean or XlsValueKind.Error:
                    formatted = new FormattedValue(value.Text, CellKind.Text);
                    alignment = alignment == CellAlignment.General ? CellAlignment.Center : alignment;
                    break;
                default:
                    formatted = new FormattedValue(string.Empty, CellKind.Text);
                    break;
            }

            var cell = XlsxWorkbook.ToCell(formatted, style.Format, alignment, colored);
            cell = runs is null || cell.Kind == CellKind.Empty ? cell : cell with { Runs = runs };
            sheet.Add(row, column, cell, maxCells);
            if (value.Kind == XlsValueKind.Number && cell.Kind != CellKind.Empty)
            {
                sheet.Numbers[XlsxWorksheet.Key(row, column)] = value.Number; // For conditional formatting.
            }
        }, limits);

        // The pivot tables' styles over their cells (before conditional formatting, which goes over them).
        sheet.PivotTables?.Apply(sheet, styles.Colors, maxCells);
        return sheet;
    }

    /// <summary>
    /// Reads a worksheet's records: its layout into <paramref name="sheet"/> and its cells, as stored, to
    /// <paramref name="cells"/>. Reads nothing unless the substream at the offset is a worksheet.
    /// </summary>
    /// <param name="workbook">The workbook.</param>
    /// <param name="offset">The offset of the sheet's BOF record.</param>
    /// <param name="sheet">Receives the layout (and, with <paramref name="limits"/>, the drawings).</param>
    /// <param name="checkpoint">Called regularly; throws to stop.</param>
    /// <param name="cells">Receives the cells.</param>
    /// <param name="limits">The limits its drawing is read within, or <c>null</c> to leave its drawing out (its objects only counted).</param>
    public static void Read(XlsWorkbook workbook, int offset, XlsxWorksheet sheet, Action? checkpoint, CellSink cells, ResourceLimits? limits = null)
    {
        var drawing = limits is not null && workbook.Biff == 8 ? new XlsDrawingRecords() : null;
        var conditional = workbook.Biff == 8 ? new XlsConditionalFormatting(workbook.Styles) : null;
        var notes = new XlsNotes(workbook.Biff, workbook.CodePage);
        var pivots = workbook.Biff == 8 ? new XlsPivotTables() : null;
        var reader = new BiffReader(workbook.Stream, offset);
        if (offset < 0 || !reader.Next() || reader.Id != XlsWorkbook.Bof || BiffReader.U16(reader.Data, 2) != WorksheetType)
        {
            return;
        }

        var depth = 1;
        var count = 0;
        (int Row, int Column, int Xf)? pending = null;
        while (reader.Next())
        {
            if (++count % 4096 == 0)
            {
                checkpoint?.Invoke();
            }

            var id = reader.Id;
            if (id == XlsWorkbook.Bof)
            {
                depth++; // An embedded chart's substream.
                continue;
            }

            if (id == XlsWorkbook.Eof && --depth == 0)
            {
                break;
            }

            if (depth != 1)
            {
                continue;
            }

            var data = reader.Data;
            if (pending is var (pendingRow, pendingColumn, pendingXf))
            {
                // A formula's string result follows it in a STRING record, maybe after its shared or array formula.
                if (id == StringRecord)
                {
                    var position = 0;
                    Emit(pendingRow, pendingColumn, pendingXf, new XlsValue(XlsValueKind.Text,
                        Text: BiffReader.ReadString(data, reader.Breaks, ref position, 2, workbook.CodePage)));
                    pending = null;
                    continue;
                }

                if (id is not (0x04BC or 0x0221 or 0x0236))
                {
                    Emit(pendingRow, pendingColumn, pendingXf, new XlsValue(XlsValueKind.Text));
                    pending = null;
                }
            }

            if (id is Obj or Txo or Note)
            {
                notes.Read(id, data, reader.Breaks);
            }

            switch (id)
            {
                case Row when data.Length >= 14:
                    var flags = BiffReader.U16(data, 12);
                    sheet.Rows[BiffReader.U16(data, 0)] = new XlsxRow(
                        (flags & 0x40) != 0 ? (BiffReader.U16(data, 6) & 0x7FFF) / 20.0 : null,
                        (flags & 0x20) != 0);
                    break;
                case ColInfo when data.Length >= 10:
                    var first = BiffReader.U16(data, 0);
                    var last = Math.Min(BiffReader.U16(data, 2), MaxColumns - 1);
                    if (first <= last)
                    {
                        sheet.Columns.Add(new XlsxColumns(first, last, BiffReader.U16(data, 4) / 256.0, (BiffReader.U16(data, 8) & 0x01) != 0));
                    }

                    break;
                case DefColWidth when data.Length >= 2:
                    sheet.BaseColumnWidth = Math.Min(BiffReader.U16(data, 0), 255);
                    break;
                case StandardWidth when data.Length >= 2:
                    sheet.DefaultColumnWidth = BiffReader.U16(data, 0) / 256.0;
                    break;
                case DefaultRowHeight when data.Length >= 2:
                    sheet.ZeroHeight = (BiffReader.U16(data, 0) & 0x02) != 0;
                    break;
                case WsBool when data.Length >= 2:
                    sheet.FitToPage = (BiffReader.U16(data, 0) & 0x0100) != 0;
                    break;
                case Setup when data.Length >= 12:
                    var options = BiffReader.U16(data, 10);
                    var noPrinterSettings = (options & 0x04) != 0;
                    sheet.Landscape = !noPrinterSettings && (options & 0x02) == 0 && (options & 0x40) == 0;
                    sheet.Scale = noPrinterSettings ? null : BiffReader.U16(data, 2);
                    sheet.FitToWidth = BiffReader.U16(data, 6);
                    sheet.CommentsAtEnd = (options & 0x0220) == 0x0220; // Print the notes (fNotes), at the end (fEndNotes),
                    sheet.CommentsAsDisplayed = (options & 0x0220) == 0x0020; // or as displayed.
                    if (data.Length >= 32)
                    {
                        // The header and footer margins, in inches; the first page number when it is set.
                        static double Margin(double inches, double fallback) => double.IsFinite(inches) ? Math.Clamp(inches, 0, 50) * 72 : fallback;
                        sheet.HeaderFooter = sheet.HeaderFooter with
                        {
                            HeaderMargin = Margin(BitConverter.ToDouble(data.Slice(16, 8)), sheet.HeaderFooter.HeaderMargin),
                            FooterMargin = Margin(BitConverter.ToDouble(data.Slice(24, 8)), sheet.HeaderFooter.FooterMargin),
                            FirstPageNumber = (options & 0x80) != 0 && !noPrinterSettings ? (short)BiffReader.U16(data, 4) : 1,
                        };
                    }

                    break;
                case Header or Footer when data.Length > 0:
                {
                    var position = 0;
                    var text = BiffReader.ReadString(data, reader.Breaks, ref position, workbook.Biff == 8 ? 2 : 1, workbook.CodePage);
                    text = string.IsNullOrWhiteSpace(text) ? null : text;
                    sheet.HeaderFooter = id == Header ? sheet.HeaderFooter with { OddHeader = text } : sheet.HeaderFooter with { OddFooter = text };
                    break;
                }

                case HeaderFooter when data.Length >= 38:
                {
                    // Even and first page texts (strings without their lengths, which come before them) and flags.
                    var hfFlags = BiffReader.U16(data, 28);
                    var texts = new string?[4];
                    var position = 38;
                    for (var i = 0; i < 4; i++)
                    {
                        var length = BiffReader.U16(data, 30 + (2 * i));
                        texts[i] = length == 0 ? null : BiffReader.ReadUnicode(data, reader.Breaks, ref position, length);
                    }

                    sheet.HeaderFooter = sheet.HeaderFooter with
                    {
                        DifferentOddEven = (hfFlags & 0x01) != 0,
                        DifferentFirst = (hfFlags & 0x02) != 0,
                        ScaleWithDocument = (hfFlags & 0x04) != 0,
                        EvenHeader = texts[0],
                        EvenFooter = texts[1],
                        FirstHeader = texts[2],
                        FirstFooter = texts[3],
                    };
                    break;
                }

                case HorizontalPageBreaks or VerticalPageBreaks when data.Length >= 2:
                {
                    // Each break: the row (or column) that starts a page, then (in BIFF8) the columns (rows) it spans.
                    var size = workbook.Biff == 8 ? 6 : 2;
                    var breaks = id == HorizontalPageBreaks ? sheet.RowBreaks : sheet.ColumnBreaks;
                    for (var i = 0; i < BiffReader.U16(data, 0) && 2 + (size * (i + 1)) <= data.Length && breaks.Count < 100_000; i++)
                    {
                        if (BiffReader.U16(data, 2 + (size * i)) is var at and > 0)
                        {
                            breaks.Add(at);
                        }
                    }

                    break;
                }
                case Window2 when data.Length >= 2:
                    sheet.RightToLeft = (BiffReader.U16(data, 0) & 0x0040) != 0;
                    break;
                case MergedCells:
                    for (var i = 0; i < BiffReader.U16(data, 0) && 2 + (8 * i) + 8 <= data.Length; i++)
                    {
                        var at = 2 + (8 * i);
                        int row1 = BiffReader.U16(data, at), row2 = BiffReader.U16(data, at + 2);
                        int column1 = BiffReader.U16(data, at + 4), column2 = Math.Min(BiffReader.U16(data, at + 6), MaxColumns - 1);
                        if (row1 <= row2 && column1 <= column2)
                        {
                            sheet.Merges.Add(new CellRange(row1, column1, row2, column2));
                        }
                    }

                    break;
                case LabelSst when data.Length >= 10:
                    var index = BiffReader.U32(data, 6);
                    Cell(data, new XlsValue(XlsValueKind.Text, Text: index < (uint)workbook.SharedStrings.Count ? workbook.SharedStrings[(int)index] : string.Empty,
                        Runs: index <= int.MaxValue && workbook.SharedRuns.TryGetValue((int)index, out var shared) ? shared : null));
                    break;
                case Label or RString when data.Length >= 8:
                    var start = 6;
                    var label = BiffReader.ReadString(data, reader.Breaks, ref start, 2, workbook.CodePage);
                    Cell(data, new XlsValue(XlsValueKind.Text, Text: label, Runs: id == RString ? StringRuns(data, start, workbook.Biff) : null));
                    break;
                case Number when data.Length >= 14:
                    Cell(data, NumberValue(BinaryPrimitives.ReadDoubleLittleEndian(data[6..])));
                    break;
                case Rk when data.Length >= 10:
                    Cell(data, NumberValue(RkValue(BiffReader.U32(data, 6))));
                    break;
                case MulRk when data.Length >= 6:
                    for (var i = 0; 4 + (6 * i) + 6 <= data.Length; i++)
                    {
                        Emit(BiffReader.U16(data, 0), BiffReader.U16(data, 2) + i, BiffReader.U16(data, 4 + (6 * i)), NumberValue(RkValue(BiffReader.U32(data, 6 + (6 * i)))));
                    }

                    break;
                case Blank when data.Length >= 6:
                    Cell(data, XlsValue.Blank);
                    break;
                case MulBlank when data.Length >= 6:
                    for (var i = 0; 4 + (2 * i) + 2 <= data.Length - 2; i++)
                    {
                        Emit(BiffReader.U16(data, 0), BiffReader.U16(data, 2) + i, BiffReader.U16(data, 4 + (2 * i)), XlsValue.Blank);
                    }

                    break;
                case BoolErr when data.Length >= 8:
                    Cell(data, data[7] != 0 ? XlsValue.Error(data[6]) : new XlsValue(XlsValueKind.Boolean, data[6] != 0 ? 1 : 0, data[6] != 0 ? "TRUE" : "FALSE"));
                    break;
                case Formula or Formula3 or Formula4 when data.Length >= 14: // Also the older record types some writers use.
                    if (BiffReader.U16(data, 12) != 0xFFFF)
                    {
                        Cell(data, NumberValue(BinaryPrimitives.ReadDoubleLittleEndian(data[6..])));
                    }
                    else if (data[6] == 0)
                    {
                        pending = (BiffReader.U16(data, 0), BiffReader.U16(data, 2), BiffReader.U16(data, 4));
                    }
                    else
                    {
                        Cell(data, data[6] switch
                        {
                            1 => new XlsValue(XlsValueKind.Boolean, data[8] != 0 ? 1 : 0, data[8] != 0 ? "TRUE" : "FALSE"),
                            2 => XlsValue.Error(data[8]),
                            _ => new XlsValue(XlsValueKind.Text),
                        });
                    }

                    break;
                case MsoDrawing when drawing is not null:
                    drawing.AddDrawing(data);
                    break;
                case Obj when drawing is not null:
                    drawing.AddObject(data, HasSubrecord(data, 0x0009));
                    break;
                case CfHeader when conditional is not null:
                    conditional.Header(data, false);
                    break;
                case CfHeader12 when conditional is not null:
                    conditional.Header(data, true);
                    break;
                case Cf when conditional is not null:
                    conditional.Rule(data);
                    break;
                case Cf12 when conditional is not null:
                    conditional.Rule12(data);
                    break;
                case CfEx when conditional is not null:
                    conditional.Extension(data);
                    break;
                case Txo when drawing is not null:
                    drawing.AddText(data, reader.Breaks);
                    break;
                case SxView when pivots is not null:
                    pivots.View(data);
                    break;
                case Sxli when pivots is not null:
                    pivots.Lines(data);
                    break;
                case SxAddl when pivots is not null:
                    pivots.Addition(data);
                    break;
                default:
                    CountUnsupported(id, data, workbook.Biff, sheet.Unsupported);
                    break;
            }
        }

        if (conditional is not null)
        {
            sheet.ConditionalRules.AddRange(conditional.Rules);
        }

        sheet.Comments.AddRange(notes.Comments);
        sheet.PivotTables = pivots;

        if (drawing is not null && limits is not null)
        {
            sheet.Drawings.AddRange(XlsDrawing.Read(workbook, drawing, limits, sheet.Unsupported, notes.Shown, sheet.CommentBoxes));
        }

        if (pending is var (lastRow, lastColumn, lastXf))
        {
            Emit(lastRow, lastColumn, lastXf, new XlsValue(XlsValueKind.Text));
        }

        void Cell(ReadOnlySpan<byte> data, XlsValue value) => Emit(BiffReader.U16(data, 0), BiffReader.U16(data, 2), BiffReader.U16(data, 4), value);

        void Emit(int row, int column, int xf, XlsValue value)
        {
            if (column < MaxColumns)
            {
                cells(row, column, xf, value);
            }
        }
    }

    /// <summary>
    /// The formatting runs after the string of an RSTRING record ([MS-XLS] 2.4.218): a count and, for each run, its first
    /// character and font index (two bytes each in BIFF8, one in BIFF5).
    /// </summary>
    private static List<(int First, int Font)> StringRuns(ReadOnlySpan<byte> data, int position, int biff)
    {
        var runs = new List<(int First, int Font)>();
        var size = biff == 8 ? 2 : 1;
        if (position + size > data.Length)
        {
            return runs;
        }

        var count = size == 2 ? BiffReader.U16(data, position) : data[position];
        position += size;
        for (var i = 0; i < count && position + (2 * size) <= data.Length; i++, position += 2 * size)
        {
            runs.Add(size == 2 ? (BiffReader.U16(data, position), BiffReader.U16(data, position + 2)) : (data[position], data[position + 1]));
        }

        return runs;
    }

    /// <summary>
    /// Counts a record of content that is not supported: a drawing object (OBJ: chart, picture, embedded object, shape or
    /// control; not those that do not print, nor the shapes of comments and groups), a conditional formatting rule (CF,
    /// CF12, of BIFF5), and a pivot table of BIFF5 (SXVIEW; those of BIFF8 are read for their style).
    /// </summary>
    private static void CountUnsupported(ushort id, ReadOnlySpan<byte> data, int biff, UnsupportedContent unsupported)
    {
        switch (id)
        {
            case Obj:
                if (ObjectKind(data, biff) is { } kind)
                {
                    unsupported.Add(kind);
                }

                break;
            case Cf or Cf12:
                unsupported.Add(SheetDiagnosticKind.ConditionalFormatting);
                break;
            case SxView:
                unsupported.Add(SheetDiagnosticKind.PivotTable);
                break;
        }
    }

    /// <summary>
    /// The kind of a drawing object (an OBJ record, [MS-XLS] 2.4.181: its common data first, then its subrecords), or
    /// <c>null</c> for one that is not counted: a group, a comment's shape (its NOTE record counts), and objects that
    /// do not print (such as the drop-down arrows of filters and lists).
    /// </summary>
    internal static SheetDiagnosticKind? ObjectKind(ReadOnlySpan<byte> data, int biff)
    {
        int type;
        if (biff == 8)
        {
            // FtCmo: ft 0x15, cb, object type, id, flags (fPrint 0x10).
            if (data.Length < 10 || BiffReader.U16(data, 0) != 0x15 || (BiffReader.U16(data, 8) & 0x10) == 0)
            {
                return null;
            }

            type = BiffReader.U16(data, 4);
        }
        else
        {
            if (data.Length < 6)
            {
                return null;
            }

            type = BiffReader.U16(data, 4);
        }

        return type switch
        {
            0x00 or 0x19 => null,
            0x05 => SheetDiagnosticKind.Chart,
            0x08 => biff == 8 && HasSubrecord(data, 0x0009) ? SheetDiagnosticKind.OleObject : SheetDiagnosticKind.Picture,
            _ => SheetDiagnosticKind.Shape,
        };
    }

    /// <summary>Whether a BIFF8 OBJ record has a subrecord of a type (0x0009: the formula of an embedded object, FtPictFmla).</summary>
    private static bool HasSubrecord(ReadOnlySpan<byte> data, int type)
    {
        var position = 0;
        while (position + 4 <= data.Length)
        {
            var ft = BiffReader.U16(data, position);
            if (ft == type)
            {
                return true;
            }

            if (ft is 0x0000 or 0x0013)
            {
                return false; // The end, or a list's data (whose size its length does not give).
            }

            position += 4 + BiffReader.U16(data, position + 2);
        }

        return false;
    }

    /// <summary>A number, or the error a spreadsheet shows for one that is not finite.</summary>
    private static XlsValue NumberValue(double number) => double.IsFinite(number) ? new XlsValue(XlsValueKind.Number, number) : XlsValue.Error(0x24);

    /// <summary>
    /// The number of an RK value ([MS-XLS] 2.5.217): a 30-bit integer or the high 30 bits of a double, divided by 100
    /// when its lowest bit is set.
    /// </summary>
    internal static double RkValue(uint rk)
    {
        var number = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 0x01) != 0 ? number / 100 : number;
    }
}
