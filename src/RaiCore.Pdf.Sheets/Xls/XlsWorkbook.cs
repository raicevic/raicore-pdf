using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// The workbook globals of a binary workbook ([MS-XLS], BIFF8 of Excel 97-2003, also BIFF5/7 of Excel 5.0/95): the
/// sheets and where their substreams start, the date system, the shared strings, the formats, and the print areas and
/// titles. Records it does not use (and those it cannot read) are skipped.
/// </summary>
internal sealed class XlsWorkbook
{
    // Record types ([MS-XLS] 2.3).
    internal const ushort Bof = 0x0809;
    internal const ushort Eof = 0x000A;
    private const ushort FilePass = 0x002F;
    private const ushort CodePageRecord = 0x0042;
    private const ushort DateMode = 0x0022;
    private const ushort FontRecord = 0x0031;
    private const ushort Format = 0x041E;
    private const ushort Xf = 0x00E0;
    private const ushort PaletteRecord = 0x0092;
    private const ushort BoundSheet = 0x0085;
    private const ushort Sst = 0x00FC;
    private const ushort Name = 0x0018;
    private const ushort ExternSheet = 0x0017;
    private const ushort MsoDrawingGroup = 0x00EB;

    /// <summary>The BOF type of the workbook globals.</summary>
    private const int GlobalsType = 0x0005;

    private readonly List<(int Sheet, bool Titles, byte[] Formula)> _names = [];
    private readonly List<int> _externSheets = [];
    private readonly Dictionary<int, CellRange> _printAreas = [];
    private readonly Dictionary<int, (int, int)> _printTitles = [];

    private XlsWorkbook(byte[] stream) => Stream = stream;

    /// <summary>The workbook stream.</summary>
    public byte[] Stream { get; }

    /// <summary>The BIFF version: 8 (Excel 97-2003) or 5 (Excel 5.0/95).</summary>
    public int Biff { get; private set; } = 8;

    /// <summary>The code page of the strings of a BIFF5 workbook; <c>null</c> in BIFF8, whose strings are Unicode.</summary>
    public Encoding? CodePage { get; private set; }

    public List<XlsxSheetEntry> Sheets { get; } = [];

    /// <summary>The OfficeArt data of the drawing group (MSODRAWINGGROUP records): the BLIP store of the sheets' pictures.</summary>
    public byte[] DrawingGroup { get; private set; } = [];

    /// <summary>The pictures of the BLIP store loaded so far, by their index (from 1); <c>null</c> for one that cannot be drawn.</summary>
    public Dictionary<int, PdfImage?> Pictures { get; } = [];

    /// <summary>The offset of each sheet's substream (its BOF record) in <see cref="Stream"/>, or -1.</summary>
    public List<int> SheetOffsets { get; } = [];

    public List<string> SharedStrings { get; } = [];

    /// <summary>The indexes of the shared strings with formatting runs (rich text, which is shown in the cell's font).</summary>
    public HashSet<int> RichStrings { get; } = [];

    public XlsStyles Styles { get; } = new();

    public FormatSettings Settings => Styles.Settings;

    /// <summary>The print area of a sheet, or <c>null</c>.</summary>
    public CellRange? PrintArea(int sheet) => _printAreas.TryGetValue(sheet, out var area) ? area : null;

    /// <summary>The rows a sheet repeats at the top of every page, or <c>null</c>.</summary>
    public (int First, int Last)? PrintTitleRows(int sheet) => _printTitles.TryGetValue(sheet, out var rows) ? rows : null;

    /// <summary>Reads the workbook globals of a workbook stream.</summary>
    /// <exception cref="SpreadsheetFormatException">The stream does not start with the workbook globals.</exception>
    /// <exception cref="NotSupportedException">The workbook is encrypted, or of a BIFF version before 5.</exception>
    public static XlsWorkbook Read(byte[] stream, CultureInfo culture, Action? checkpoint)
    {
        var workbook = new XlsWorkbook(stream);
        var reader = new BiffReader(stream);
        if (!reader.Next() || reader.Id != Bof)
        {
            if (stream.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(stream) is 0x0009 or 0x0209 or 0x0409)
            {
                throw new NotSupportedException("The workbook is an Excel 2.x-4.0 file (BIFF2-4), which is not supported: only Excel 5.0 and later (.xls) are.");
            }

            throw new SpreadsheetFormatException("The workbook stream does not start with a BOF record: it is not an Excel workbook.");
        }

        var data = reader.Data;
        var version = BiffReader.U16(data, 0);
        workbook.Biff = version == 0x0500 || (version != 0x0600 && data.Length < 16) ? 5 : 8;
        if (BiffReader.U16(data, 2) is var type && type != GlobalsType && type != 0)
        {
            throw new SpreadsheetFormatException($"The workbook stream starts with a substream of type 0x{type:X4}, not the workbook globals.");
        }

        if (workbook.Biff == 5)
        {
            workbook.CodePage = CodePageEncoding(1252);
        }

        var date1904 = false;
        var count = 0;
        using var drawingGroup = new MemoryStream();
        while (reader.Next() && reader.Id != Eof)
        {
            if (++count % 4096 == 0)
            {
                checkpoint?.Invoke();
            }

            data = reader.Data;
            switch (reader.Id)
            {
                case FilePass:
                    throw new NotSupportedException(
                        "The workbook is encrypted (password-protected, also when it opens without a password with Excel's default password), which is not supported.");
                case CodePageRecord when workbook.Biff == 5:
                    workbook.CodePage = CodePageEncoding(BiffReader.U16(data, 0));
                    break;
                case DateMode:
                    date1904 = BiffReader.U16(data, 0) == 1;
                    break;
                case FontRecord:
                    workbook.ReadFont(data, reader.Breaks);
                    break;
                case Format:
                    var position = 2;
                    workbook.Styles.NumberFormats[BiffReader.U16(data, 0)] =
                        BiffReader.ReadString(data, reader.Breaks, ref position, workbook.Biff == 8 ? 2 : 1, workbook.CodePage);
                    break;
                case Xf:
                    workbook.Styles.Xfs.Add(workbook.Biff == 8 ? ReadXf8(data) : ReadXf5(data));
                    break;
                case PaletteRecord:
                    var palette = new List<uint>();
                    for (var i = 0; i < BiffReader.U16(data, 0) && 2 + (4 * i) + 3 <= data.Length; i++)
                    {
                        var at = 2 + (4 * i);
                        palette.Add(((uint)data[at] << 16) | ((uint)data[at + 1] << 8) | data[at + 2]);
                    }

                    workbook.Styles.Palette = palette;
                    break;
                case BoundSheet:
                    workbook.ReadBoundSheet(data, reader.Breaks);
                    break;
                case MsoDrawingGroup when workbook.Biff == 8:
                    drawingGroup.Write(data);
                    break;
                case Sst when workbook.Biff == 8:
                    workbook.ReadSharedStrings(data, reader.Breaks, checkpoint);
                    break;
                case ExternSheet when workbook.Biff == 8:
                    for (var i = 0; i < BiffReader.U16(data, 0) && 2 + (6 * i) + 6 <= data.Length; i++)
                    {
                        workbook._externSheets.Add(BiffReader.U16(data, 2 + (6 * i) + 2));
                    }

                    break;
                case Name when workbook.Biff == 8:
                    workbook.ReadName(data, reader.Breaks);
                    break;
            }
        }

        workbook.Styles.Settings = new FormatSettings(culture, date1904);
        workbook.Styles.Complete();
        workbook.DrawingGroup = drawingGroup.ToArray();
        workbook.FindSubstreams(reader.NextPosition, checkpoint);
        workbook.ResolveNames();
        return workbook;
    }

    /// <summary>The encoding of a code page (CODEPAGE record), Windows-1252 for ones it does not know.</summary>
    private static Encoding CodePageEncoding(int codePage)
    {
        codePage = codePage switch
        {
            32768 => 10000, // Mac Roman.
            32769 or 1200 => 1252,
            _ => codePage,
        };
        try
        {
            return (codePage is > 0 and < 65535 ? CodePagesEncodingProvider.Instance.GetEncoding(codePage) : null)
                ?? CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? System.Text.Encoding.Latin1;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? System.Text.Encoding.Latin1;
        }
    }

    private void ReadFont(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks)
    {
        if (data.Length < 15)
        {
            Styles.Fonts.Add(new XlsFont(null, null, false, false, CellUnderline.None, false, 0x7FFF));
            return;
        }

        var position = 14;
        var name = BiffReader.ReadString(data, breaks, ref position, 1, CodePage).Trim();
        var size = BiffReader.U16(data, 0) / 20.0;
        var style = BiffReader.U16(data, 2);
        Styles.Fonts.Add(new XlsFont(
            name.Length is > 0 and <= 64 ? name : null,
            size is >= 1 and <= 409 ? size : null,
            BiffReader.U16(data, 6) >= 700,
            (style & 0x02) != 0,
            data[10] switch
            {
                0 => CellUnderline.None,
                2 or 0x22 => CellUnderline.Double,
                _ => CellUnderline.Single,
            },
            (style & 0x08) != 0,
            BiffReader.U16(data, 4)));
    }

    /// <summary>An XF record of BIFF8 ([MS-XLS] 2.4.353), 20 bytes.</summary>
    private static XlsXf ReadXf8(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
        {
            return new XlsXf { Font = BiffReader.U16(data, 0), NumberFormat = BiffReader.U16(data, 2) };
        }

        var borders = BiffReader.U32(data, 10);
        var more = BiffReader.U32(data, 14);
        var fill = BiffReader.U16(data, 18);
        return new XlsXf
        {
            Font = BiffReader.U16(data, 0),
            NumberFormat = BiffReader.U16(data, 2),
            Horizontal = data[6] & 0x07,
            Wrap = (data[6] & 0x08) != 0,
            Vertical = (data[6] >> 4) & 0x07,
            Indent = data[8] & 0x0F,
            Rotated = data[7] is not (0 or 255),
            Stacked = data[7] == 255,
            ShrinkToFit = (data[8] & 0x10) != 0,
            Lines = ((int)(borders & 0x0F), (int)((borders >> 4) & 0x0F), (int)((borders >> 8) & 0x0F), (int)((borders >> 12) & 0x0F)),
            LineColors = ((int)((borders >> 16) & 0x7F), (int)((borders >> 23) & 0x7F), (int)(more & 0x7F), (int)((more >> 7) & 0x7F)),
            Pattern = (int)(more >> 26),
            Foreground = fill & 0x7F,
            Background = (fill >> 7) & 0x7F,
        };
    }

    /// <summary>An XF record of BIFF5, 16 bytes.</summary>
    private static XlsXf ReadXf5(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            return new XlsXf { Font = BiffReader.U16(data, 0), NumberFormat = BiffReader.U16(data, 2) };
        }

        var fill = BiffReader.U32(data, 8);
        var borders = BiffReader.U32(data, 12);
        return new XlsXf
        {
            Font = BiffReader.U16(data, 0),
            NumberFormat = BiffReader.U16(data, 2),
            Horizontal = data[6] & 0x07,
            Wrap = (data[6] & 0x08) != 0,
            Vertical = (data[6] >> 4) & 0x07,
            Rotated = (data[7] & 0x03) is 2 or 3,
            Stacked = (data[7] & 0x03) == 1,
            Lines = ((int)((borders >> 3) & 0x07), (int)((borders >> 6) & 0x07), (int)(borders & 0x07), (int)((fill >> 22) & 0x07)),
            LineColors = ((int)((borders >> 16) & 0x7F), (int)((borders >> 23) & 0x7F), (int)((borders >> 9) & 0x7F), (int)((fill >> 25) & 0x7F)),
            Pattern = (int)((fill >> 16) & 0x3F),
            Foreground = (int)(fill & 0x7F),
            Background = (int)((fill >> 7) & 0x7F),
        };
    }

    /// <summary>A BOUNDSHEET record: a sheet's substream offset, visibility, type and name.</summary>
    private void ReadBoundSheet(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks)
    {
        if (data.Length < 7)
        {
            return;
        }

        var position = 6;
        var name = BiffReader.ReadString(data, breaks, ref position, 1, CodePage);
        Sheets.Add(new XlsxSheetEntry(
            name.Length == 0 ? $"Sheet{Sheets.Count + 1}" : name,
            null,
            Sheets.Count,
            (data[4] & 0x03) != 0,
            data[5] == 0,
            data[5] == 2));
        SheetOffsets.Add((int)Math.Min(BiffReader.U32(data, 0), int.MaxValue));
    }

    /// <summary>The SST record and its CONTINUE records: the shared strings, read as far as the data goes.</summary>
    private void ReadSharedStrings(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks, Action? checkpoint)
    {
        var position = 8;
        while (position < data.Length)
        {
            if (position + 5 <= data.Length && (data[position + 2] & 0x08) != 0 && BiffReader.U16(data, position + 3) > 0)
            {
                RichStrings.Add(SharedStrings.Count);
            }

            SharedStrings.Add(BiffReader.ReadString(data, breaks, ref position, 2, null, extended: true));
            if (SharedStrings.Count % 4096 == 0)
            {
                checkpoint?.Invoke();
            }
        }
    }

    /// <summary>A NAME record: kept if it is a built-in print area or print titles of a sheet.</summary>
    private void ReadName(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks)
    {
        if (data.Length < 15 || (BiffReader.U16(data, 0) & 0x0020) == 0)
        {
            return;
        }

        var length = data[3];
        var formulaLength = BiffReader.U16(data, 4);
        var sheet = BiffReader.U16(data, 8) - 1;
        var position = 14;
        var name = BiffReader.ReadUnicode(data, breaks, ref position, length);
        if (name.Length == 1 && name[0] is '\x06' or '\x07' && position + formulaLength <= data.Length)
        {
            _names.Add((sheet, name[0] == '\x07', data.Slice(position, formulaLength).ToArray()));
        }
    }

    /// <summary>Resolves the print areas and titles once the sheets are known.</summary>
    private void ResolveNames()
    {
        foreach (var (localSheet, titles, formula) in _names)
        {
            foreach (var (externSheet, range) in Areas(formula))
            {
                var sheet = localSheet >= 0 ? localSheet : externSheet >= 0 && externSheet < _externSheets.Count ? _externSheets[externSheet] : -1;
                if (sheet < 0 || sheet >= Sheets.Count)
                {
                    continue;
                }

                if (!titles)
                {
                    _printAreas.TryAdd(sheet, range);
                }
                else if (range.Column1 < 0 && range.Row1 >= 0)
                {
                    _printTitles.TryAdd(sheet, (range.Row1, range.Row2));
                }
            }
        }
    }

    /// <summary>
    /// The areas of a name's formula ([MS-XLS] 2.5.198): its 3-D and plain references and areas, also in unions. Whole
    /// columns have no rows (-1), whole rows no columns. Returns the EXTERNSHEET index of each (-1 for none).
    /// </summary>
    internal static List<(int ExternSheet, CellRange Range)> Areas(ReadOnlySpan<byte> formula)
    {
        var areas = new List<(int, CellRange)>();
        var i = 0;
        while (i < formula.Length)
        {
            var token = formula[i++];
            switch (token)
            {
                case 0x3B or 0x5B or 0x7B when i + 10 <= formula.Length: // PtgArea3d
                    areas.Add((BiffReader.U16(formula, i), Area(formula[(i + 2)..])));
                    i += 10;
                    break;
                case 0x3A or 0x5A or 0x7A when i + 6 <= formula.Length: // PtgRef3d
                    var row = BiffReader.U16(formula, i + 2);
                    var column = BiffReader.U16(formula, i + 4) & 0x3FFF;
                    areas.Add((BiffReader.U16(formula, i), new CellRange(row, column, row, column)));
                    i += 6;
                    break;
                case 0x25 or 0x45 or 0x65 when i + 8 <= formula.Length: // PtgArea
                    areas.Add((-1, Area(formula[i..])));
                    i += 8;
                    break;
                case 0x29 or 0x49 or 0x69: // PtgMemFunc: the areas follow.
                    i += 2;
                    break;
                case 0x26 or 0x46 or 0x66: // PtgMemArea
                    i += 6;
                    break;
                case 0x10: // PtgUnion
                    break;
                case 0x3D or 0x5D or 0x7D: // PtgArea3dErr
                    i += 10;
                    break;
                case 0x3C or 0x5C or 0x7C: // PtgRef3dErr
                    i += 6;
                    break;
                default:
                    return areas;
            }
        }

        return areas;
    }

    /// <summary>An area's rows and columns (8 bytes: first and last row, first and last column).</summary>
    private static CellRange Area(ReadOnlySpan<byte> data)
    {
        int row1 = BiffReader.U16(data, 0), row2 = BiffReader.U16(data, 2);
        int column1 = BiffReader.U16(data, 4) & 0x3FFF, column2 = BiffReader.U16(data, 6) & 0x3FFF;
        var wholeColumns = row1 == 0 && row2 >= 0xFFFF;
        var wholeRows = column1 == 0 && column2 >= 0xFF;
        return new CellRange(
            wholeColumns ? -1 : Math.Min(row1, row2),
            wholeRows ? -1 : Math.Min(column1, column2),
            wholeColumns ? -1 : Math.Max(row1, row2),
            wholeRows ? -1 : Math.Max(column1, column2));
    }

    /// <summary>
    /// Checks that each sheet's offset is a BOF record; where one is not (a damaged or badly written file), the sheets
    /// take the substreams that follow the globals, in order.
    /// </summary>
    private void FindSubstreams(int start, Action? checkpoint)
    {
        bool IsBof(int offset) => offset >= 0 && offset <= Stream.Length - 4 && BinaryPrimitives.ReadUInt16LittleEndian(Stream.AsSpan(offset)) == Bof;
        if (SheetOffsets.All(IsBof))
        {
            return;
        }

        var found = new List<int>();
        var reader = new BiffReader(Stream, start);
        var depth = 0;
        var count = 0;
        while (reader.Next())
        {
            if (++count % 4096 == 0)
            {
                checkpoint?.Invoke();
            }

            if (reader.Id == Bof)
            {
                if (depth == 0)
                {
                    found.Add(reader.Position);
                }

                depth++;
            }
            else if (reader.Id == Eof && depth > 0)
            {
                depth--;
            }
        }

        for (var i = 0; i < SheetOffsets.Count; i++)
        {
            if (!IsBof(SheetOffsets[i]))
            {
                SheetOffsets[i] = i < found.Count ? found[i] : -1;
            }
        }
    }
}
