using System.Buffers.Binary;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>Character properties ([MS-DOC] 2.9.34 Chp), as far as they are shown.</summary>
internal struct DocChp
{
    public bool Bold;
    public bool Italic;
    public bool Strike;
    public bool DoubleStrike;
    public bool SmallCaps;
    public bool Caps;
    public bool Vanish;
    public bool Deleted;
    public bool Special;
    public bool FieldVanish;
    public bool Ole2;
    public bool RightToLeft;
    public byte Underline;
    public PdfColor? Color;
    public PdfColor? Highlight;
    public PdfColor? Shading;
    public int HalfPoints;
    public int HalfPointPosition;
    public byte Iss;
    public int Spacing;
    public int Font;
    public int PictureLocation;
    public int SymbolFont;
    public char SymbolCharacter;
    public int CharacterStyle;

    /// <summary>The default: 10 points in the document's default font, no picture, no symbol.</summary>
    public static DocChp Default(int font) => new() { HalfPoints = 20, Font = font, PictureLocation = -1, SymbolFont = -1, CharacterStyle = 10 };
}

/// <summary>Paragraph properties ([MS-DOC] 2.9.176 Pap), including the table position of the paragraph.</summary>
internal struct DocPap
{
    public int Style;
    public byte Justification;
    public bool Keep;
    public bool KeepFollow;
    public bool PageBreakBefore;
    public bool WidowControl;
    public bool InTable;
    public bool TableRowEnd;
    public bool InnerCellEnd;
    public bool InnerRowEnd;
    public bool RightToLeft;
    public bool ContextualSpacing;
    public int Depth;
    public int ListLevel;
    public int ListOverride;
    public int Left;
    public int Right;
    public int FirstLine;
    public int Before;
    public int After;
    public int Line;
    public bool LineMultiple;
    public int OutlineLevel;
    public BorderLine? BorderTop;
    public BorderLine? BorderLeft;
    public BorderLine? BorderBottom;
    public BorderLine? BorderRight;
    public BorderLine? BorderBetween;
    public PdfColor? Shading;
    public (short Position, byte Descriptor)[]? Tabs;

    /// <summary>The default: single spacing, widow control, body text.</summary>
    public static DocPap Default => new() { Line = 240, LineMultiple = true, OutlineLevel = 9, WidowControl = true };

    /// <summary>The table nesting level: 0 outside tables.</summary>
    public readonly int TableLevel => InTable ? Math.Max(1, Depth) : 0;
}

/// <summary>Section properties ([MS-DOC] 2.9.260 Sep) in twips.</summary>
internal struct DocSep
{
    public int PageWidth;
    public int PageHeight;
    public int Left;
    public int Right;
    public int Top;
    public int Bottom;
    public int Gutter;
    public int HeaderTop;
    public int FooterBottom;
    public byte Break;
    public bool TitlePage;
    public int Columns;
    public byte PageNumberFormat;
    public bool RestartPageNumbers;
    public int PageNumberStart;

    /// <summary>The defaults of [MS-DOC] 2.6.3 (Letter paper, 1.25-inch side margins, a new page).</summary>
    public static DocSep Default => new()
    {
        PageWidth = 12240, PageHeight = 15840, Left = 1800, Right = 1800, Top = 1440, Bottom = 1440, HeaderTop = 720,
        FooterBottom = 720, Break = 2, Columns = 1, PageNumberStart = 1,
    };
}

/// <summary>A cell of a table row definition ([MS-DOC] 2.9.318 TC80 and the cell modifiers).</summary>
internal sealed class DocCellDefinition
{
    public int Right;
    public byte HorizontalMerge;
    public byte VerticalMerge;
    public CellVerticalAlignment VerticalAlignment;
    public BorderLine? Top;
    public BorderLine? Left;
    public BorderLine? Bottom;
    public BorderLine? Right2;
    public PdfColor? Shading;
}

/// <summary>A table row definition ([MS-DOC] 2.9.307 Tap) from the modifiers of a row's end mark, in twips.</summary>
internal sealed class DocRowDefinition
{
    public int Left;
    public int GapHalf;
    public byte Justification;
    public bool Header;
    public bool CantSplit;
    public int Height;
    public BorderLine? Top;
    public BorderLine? LeftBorder;
    public BorderLine? Bottom;
    public BorderLine? RightBorder;
    public BorderLine? InsideHorizontal;
    public BorderLine? InsideVertical;
    public int? PaddingTop;
    public int? PaddingBottom;
    public int? PaddingLeft;
    public int? PaddingRight;
    public List<DocCellDefinition> Cells { get; } = [];
}

/// <summary>Decodes property values and applies modifiers to the property structures.</summary>
internal static class DocProperties
{
    /// <summary>The most cells a row definition has (Word allows 63).</summary>
    private const int MaxCells = 64;

    /// <summary>The 16 colors of an <c>ico</c> index ([MS-DOC] 2.9.119 Ico).</summary>
    private static readonly PdfColor[] Palette =
    [
        PdfColor.Black, PdfColor.FromRgb(0, 0, 255), PdfColor.FromRgb(0, 255, 255), PdfColor.FromRgb(0, 255, 0),
        PdfColor.FromRgb(255, 0, 255), PdfColor.FromRgb(255, 0, 0), PdfColor.FromRgb(255, 255, 0), PdfColor.White,
        PdfColor.FromRgb(0, 0, 128), PdfColor.FromRgb(0, 128, 128), PdfColor.FromRgb(0, 128, 0), PdfColor.FromRgb(128, 0, 128),
        PdfColor.FromRgb(128, 0, 0), PdfColor.FromRgb(128, 128, 0), PdfColor.FromRgb(128, 128, 128), PdfColor.FromRgb(192, 192, 192),
    ];

    /// <summary>The shares of the foreground color of the percentage shading patterns 2 to 13 ([MS-DOC] 2.9.121 Ipat).</summary>
    private static ReadOnlySpan<byte> Percentages => [5, 10, 20, 25, 30, 40, 50, 60, 70, 75, 80, 90];

    /// <summary>A color by its <c>ico</c> index, or <c>null</c> for automatic (0) and unknown indexes.</summary>
    public static PdfColor? Ico(int ico) => ico is >= 1 and <= 16 ? Palette[ico - 1] : null;

    /// <summary>A <c>COLORREF</c> ([MS-DOC] 2.9.43), or <c>null</c> when automatic.</summary>
    public static PdfColor? ColorRef(ReadOnlySpan<byte> value) =>
        value.Length < 4 || value[3] == 0xFF ? null : PdfColor.FromRgb(value[0], value[1], value[2]);

    /// <summary>A border from a <c>Brc80</c> (4 bytes) or <c>Brc</c> (8 bytes) ([MS-DOC] 2.9.17, 2.9.16).</summary>
    public static BorderLine Border(ReadOnlySpan<byte> value)
    {
        if (value.Length >= 8)
        {
            return Border(value[4], value[5], ColorRef(value) ?? PdfColor.Black);
        }

        if (value.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(value) == 0xFFFFFFFF)
        {
            return BorderLine.None;
        }

        return Border(value[0], value[1], Ico(value[2]) ?? PdfColor.Black);
    }

    private static BorderLine Border(byte eighths, byte type, PdfColor color)
    {
        if (type is 0 or 0xFF)
        {
            return BorderLine.None;
        }

        var width = Math.Clamp(eighths / 8.0, 0.25, 6);
        var style = type switch
        {
            3 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 => BorderLineStyle.Double,
            6 or 9 => BorderLineStyle.Dotted,
            7 or 8 or 22 or 23 => BorderLineStyle.Dashed,
            _ => BorderLineStyle.Single,
        };
        return new BorderLine(type == 5 ? 0.25 : width, color, style);
    }

    /// <summary>A background from an <c>Shd80</c> (2 bytes) or <c>Shd</c> (10 bytes) ([MS-DOC] 2.9.249, 2.9.248), or <c>null</c>.</summary>
    public static PdfColor? Shading(ReadOnlySpan<byte> value)
    {
        PdfColor? fore, back;
        int pattern;
        if (value.Length >= 10)
        {
            fore = ColorRef(value);
            back = ColorRef(value[4..]);
            pattern = BinaryPrimitives.ReadUInt16LittleEndian(value[8..]);
        }
        else if (value.Length >= 2)
        {
            var shd = BinaryPrimitives.ReadUInt16LittleEndian(value);
            if (shd == 0xFFFF)
            {
                return null;
            }

            fore = Ico(shd & 0x1F);
            back = Ico((shd >> 5) & 0x1F);
            pattern = shd >> 10;
        }
        else
        {
            return null;
        }

        switch (pattern)
        {
            case 0:
                return back;
            case 1:
                return fore ?? PdfColor.Black;
            case >= 2 and <= 13:
                var share = Percentages[pattern - 2] / 100.0;
                var front = fore ?? PdfColor.Black;
                var behind = back ?? PdfColor.White;
                return new PdfColor(
                    (front.Red * share) + (behind.Red * (1 - share)),
                    (front.Green * share) + (behind.Green * (1 - share)),
                    (front.Blue * share) + (behind.Blue * (1 - share)));
            default:
                return back;
        }
    }

    /// <summary>A toggle operand ([MS-DOC] 2.9.355 ToggleOperand): off, on, the style's value or its opposite.</summary>
    private static bool Toggle(byte operand, bool styleValue) => operand switch
    {
        0 => false,
        1 => true,
        0x80 => styleValue,
        0x81 => !styleValue,
        _ => styleValue,
    };

    /// <summary>Applies character modifiers; toggles that refer to the style compare with <paramref name="style"/>.</summary>
    public static void ApplyCharacter(ref DocChp chp, ReadOnlySpan<byte> grpprl, in DocChp style)
    {
        var reader = new SprmReader(grpprl);
        while (reader.Next())
        {
            ApplyCharacter(ref chp, reader.Code, reader.Operand, style);
        }
    }

    /// <summary>Applies one character modifier.</summary>
    public static void ApplyCharacter(ref DocChp chp, ushort code, ReadOnlySpan<byte> op, in DocChp style)
    {
        if (op.Length == 0)
        {
            return;
        }

        switch (code)
        {
            case Sprms.CFBold:
                chp.Bold = Toggle(op[0], style.Bold);
                break;
            case Sprms.CFItalic:
                chp.Italic = Toggle(op[0], style.Italic);
                break;
            case Sprms.CFStrike:
                chp.Strike = Toggle(op[0], style.Strike);
                break;
            case Sprms.CFDStrike:
                chp.DoubleStrike = Toggle(op[0], style.DoubleStrike);
                break;
            case Sprms.CFSmallCaps:
                chp.SmallCaps = Toggle(op[0], style.SmallCaps);
                break;
            case Sprms.CFCaps:
                chp.Caps = Toggle(op[0], style.Caps);
                break;
            case Sprms.CFVanish:
                chp.Vanish = Toggle(op[0], style.Vanish);
                break;
            case Sprms.CFRMarkDel:
                chp.Deleted = op[0] != 0;
                break;
            case Sprms.CFSpec:
                chp.Special = op[0] != 0;
                break;
            case Sprms.CFFldVanish:
                chp.FieldVanish = op[0] != 0;
                break;
            case Sprms.CFOle2:
                chp.Ole2 = op[0] != 0;
                break;
            case Sprms.CFBiDi:
                chp.RightToLeft = op[0] != 0;
                break;
            case Sprms.CKul:
                chp.Underline = op[0];
                break;
            case Sprms.CIco:
                chp.Color = Ico(op[0]);
                break;
            case Sprms.CCv when op.Length >= 4:
                chp.Color = ColorRef(op);
                break;
            case Sprms.CHighlight:
                chp.Highlight = Ico(op[0]);
                break;
            case Sprms.CShd80 when op.Length >= 2:
            case Sprms.CShd:
                chp.Shading = Shading(op);
                break;
            case Sprms.CHps when op.Length >= 2:
                chp.HalfPoints = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(op), 2, 3276);
                break;
            case Sprms.CHpsPos when op.Length >= 2:
                chp.HalfPointPosition = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(op), -3276, 3276);
                break;
            case Sprms.CIss:
                chp.Iss = op[0];
                break;
            case Sprms.CDxaSpace when op.Length >= 2:
                chp.Spacing = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(op), -1440, 1440);
                break;
            case Sprms.CRgFtc0 when op.Length >= 2:
            case Sprms.CFtc when op.Length >= 2:
                chp.Font = BinaryPrimitives.ReadUInt16LittleEndian(op);
                break;
            case Sprms.CPicLocation when op.Length >= 4:
                chp.PictureLocation = BinaryPrimitives.ReadInt32LittleEndian(op);
                break;
            case Sprms.CSymbol when op.Length >= 4:
                chp.SymbolFont = BinaryPrimitives.ReadUInt16LittleEndian(op);
                chp.SymbolCharacter = (char)BinaryPrimitives.ReadUInt16LittleEndian(op[2..]);
                break;
        }
    }

    /// <summary>Applies paragraph modifiers (table modifiers are read separately).</summary>
    public static void ApplyParagraph(ref DocPap pap, ReadOnlySpan<byte> grpprl)
    {
        var reader = new SprmReader(grpprl);
        while (reader.Next())
        {
            ApplyParagraph(ref pap, reader.Code, reader.Operand);
        }
    }

    /// <summary>Applies one paragraph modifier.</summary>
    public static void ApplyParagraph(ref DocPap pap, ushort code, ReadOnlySpan<byte> op)
    {
        if (op.Length == 0)
        {
            return;
        }

        var word = op.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(op) : 0;
        switch (code)
        {
            case Sprms.PIstd when op.Length >= 2:
                pap.Style = (ushort)word;
                break;
            case Sprms.PJc80:
            case Sprms.PJc:
                pap.Justification = op[0];
                break;
            case Sprms.PFKeep:
                pap.Keep = op[0] != 0;
                break;
            case Sprms.PFKeepFollow:
                pap.KeepFollow = op[0] != 0;
                break;
            case Sprms.PFPageBreakBefore:
                pap.PageBreakBefore = op[0] != 0;
                break;
            case Sprms.PFWidowControl:
                pap.WidowControl = op[0] != 0;
                break;
            case Sprms.PIlvl:
                pap.ListLevel = Math.Min((int)op[0], 8);
                break;
            case Sprms.PIlfo when op.Length >= 2:
                pap.ListOverride = word;
                break;
            case Sprms.PDxaLeft80 when op.Length >= 2:
            case Sprms.PDxaLeft when op.Length >= 2:
                pap.Left = word;
                break;
            case Sprms.PDxaRight80 when op.Length >= 2:
            case Sprms.PDxaRight when op.Length >= 2:
                pap.Right = word;
                break;
            case Sprms.PDxaLeft180 when op.Length >= 2:
            case Sprms.PDxaLeft1 when op.Length >= 2:
                pap.FirstLine = word;
                break;
            case Sprms.PDyaBefore when op.Length >= 2:
                pap.Before = Math.Max(0, (int)(ushort)word);
                break;
            case Sprms.PDyaAfter when op.Length >= 2:
                pap.After = Math.Max(0, (int)(ushort)word);
                break;
            case Sprms.PDyaLine when op.Length >= 4:
                pap.Line = word;
                pap.LineMultiple = BinaryPrimitives.ReadInt16LittleEndian(op[2..]) != 0;
                break;
            case Sprms.PFInTable:
                pap.InTable = op[0] != 0;
                break;
            case Sprms.PFTtp:
                pap.TableRowEnd = op[0] != 0;
                break;
            case Sprms.PFInnerTableCell:
                pap.InnerCellEnd = op[0] != 0;
                break;
            case Sprms.PFInnerTtp:
                pap.InnerRowEnd = op[0] != 0;
                break;
            case Sprms.PItap when op.Length >= 4:
                pap.Depth = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(op), 0, 4096);
                break;
            case Sprms.PDtap when op.Length >= 4:
                pap.Depth = (int)Math.Clamp(pap.Depth + (long)BinaryPrimitives.ReadInt32LittleEndian(op), 0, 4096);
                break;
            case Sprms.PFBiDi:
                pap.RightToLeft = op[0] != 0;
                break;
            case Sprms.PFContextualSpacing:
                pap.ContextualSpacing = op[0] != 0;
                break;
            case Sprms.POutLvl:
                pap.OutlineLevel = op[0];
                break;
            case Sprms.PBrcTop80:
            case Sprms.PBrcTop:
                pap.BorderTop = Border(op);
                break;
            case Sprms.PBrcLeft80:
            case Sprms.PBrcLeft:
                pap.BorderLeft = Border(op);
                break;
            case Sprms.PBrcBottom80:
            case Sprms.PBrcBottom:
                pap.BorderBottom = Border(op);
                break;
            case Sprms.PBrcRight80:
            case Sprms.PBrcRight:
                pap.BorderRight = Border(op);
                break;
            case Sprms.PBrcBetween80:
            case Sprms.PBrcBetween:
                pap.BorderBetween = Border(op);
                break;
            case Sprms.PShd80 when op.Length >= 2:
            case Sprms.PShd:
                pap.Shading = Shading(op);
                break;
            case Sprms.PChgTabsPapx:
            case Sprms.PChgTabs:
                pap.Tabs = ChangeTabs(pap.Tabs, op, code == Sprms.PChgTabs);
                break;
        }
    }

    /// <summary>
    /// Applies a tab change ([MS-DOC] 2.9.182 PChgTabsPapxOperand, 2.9.183 PChgTabsOperand): the stops deleted, then
    /// those added (a style's change also lists the tolerance of each deletion, which is skipped).
    /// </summary>
    private static (short, byte)[] ChangeTabs((short Position, byte Descriptor)[]? tabs, ReadOnlySpan<byte> op, bool withClose)
    {
        var result = new List<(short Position, byte Descriptor)>(tabs ?? []);
        var deleted = op[0];
        var at = 1;
        var size = withClose ? 4 : 2;
        if (at + (size * deleted) >= op.Length)
        {
            return [.. result];
        }

        for (var i = 0; i < deleted; i++)
        {
            var position = BinaryPrimitives.ReadInt16LittleEndian(op[(at + (2 * i))..]);
            result.RemoveAll(t => t.Position == position);
        }

        at += size * deleted;
        var added = op[at++];
        if (at + (3 * added) > op.Length)
        {
            return [.. result];
        }

        for (var i = 0; i < added && result.Count < 64; i++)
        {
            var position = BinaryPrimitives.ReadInt16LittleEndian(op[(at + (2 * i))..]);
            var descriptor = op[at + (2 * added) + i];
            result.RemoveAll(t => t.Position == position);
            result.Add((position, descriptor));
        }

        result.Sort((a, b) => a.Position.CompareTo(b.Position));
        return [.. result];
    }

    /// <summary>Applies section modifiers.</summary>
    public static void ApplySection(ref DocSep sep, ReadOnlySpan<byte> grpprl)
    {
        var reader = new SprmReader(grpprl);
        while (reader.Next())
        {
            var op = reader.Operand;
            var word = op.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(op) : 0;
            var unsigned = op.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(op) : 0;
            switch (reader.Code)
            {
                case Sprms.SBkc:
                    sep.Break = op[0];
                    break;
                case Sprms.SFTitlePage:
                    sep.TitlePage = op[0] != 0;
                    break;
                case Sprms.SCcolumns when op.Length >= 2:
                    sep.Columns = Math.Clamp(unsigned + 1, 1, 45);
                    break;
                case Sprms.SNfcPgn:
                    sep.PageNumberFormat = op[0];
                    break;
                case Sprms.SFPgnRestart:
                    sep.RestartPageNumbers = op[0] != 0;
                    break;
                case Sprms.SPgnStart97 when op.Length >= 2:
                    sep.PageNumberStart = unsigned;
                    break;
                case Sprms.SXaPage when op.Length >= 2:
                    sep.PageWidth = unsigned;
                    break;
                case Sprms.SYaPage when op.Length >= 2:
                    sep.PageHeight = unsigned;
                    break;
                case Sprms.SDxaLeft when op.Length >= 2:
                    sep.Left = unsigned;
                    break;
                case Sprms.SDxaRight when op.Length >= 2:
                    sep.Right = unsigned;
                    break;
                case Sprms.SDyaTop when op.Length >= 2:
                    sep.Top = word;
                    break;
                case Sprms.SDyaBottom when op.Length >= 2:
                    sep.Bottom = word;
                    break;
                case Sprms.SDzaGutter when op.Length >= 2:
                    sep.Gutter = unsigned;
                    break;
                case Sprms.SDyaHdrTop when op.Length >= 2:
                    sep.HeaderTop = unsigned;
                    break;
                case Sprms.SDyaHdrBottom when op.Length >= 2:
                    sep.FooterBottom = unsigned;
                    break;
            }
        }
    }

    /// <summary>The row definition of a table row's end mark from its modifiers, or <c>null</c> when it has none.</summary>
    public static DocRowDefinition? Row(ReadOnlySpan<byte> grpprl)
    {
        DocRowDefinition? row = null;
        var reader = new SprmReader(grpprl);
        while (reader.Next())
        {
            var op = reader.Operand;
            switch (reader.Code)
            {
                case Sprms.TDefTable:
                    row ??= new DocRowDefinition();
                    DefineCells(row, op);
                    break;
                case Sprms.TDxaGapHalf when op.Length >= 2:
                    (row ??= new DocRowDefinition()).GapHalf = Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(op), 0, 31680);
                    break;
                case Sprms.TJc90 when op.Length >= 2:
                case Sprms.TJc when op.Length >= 2:
                    (row ??= new DocRowDefinition()).Justification = op[0];
                    break;
                case Sprms.TTableHeader:
                    (row ??= new DocRowDefinition()).Header = op.Length > 0 && op[0] != 0;
                    break;
                case Sprms.TFCantSplit:
                case Sprms.TFCantSplit90:
                    (row ??= new DocRowDefinition()).CantSplit = op.Length > 0 && op[0] != 0;
                    break;
                case Sprms.TDyaRowHeight when op.Length >= 2:
                    (row ??= new DocRowDefinition()).Height = BinaryPrimitives.ReadInt16LittleEndian(op);
                    break;
                case Sprms.TTableBorders80 when op.Length >= 24:
                case Sprms.TTableBorders when op.Length >= 48:
                    row ??= new DocRowDefinition();
                    var size = reader.Code == Sprms.TTableBorders ? 8 : 4;
                    row.Top = Border(op.Slice(0, size));
                    row.LeftBorder = Border(op.Slice(size, size));
                    row.Bottom = Border(op.Slice(2 * size, size));
                    row.RightBorder = Border(op.Slice(3 * size, size));
                    row.InsideHorizontal = Border(op.Slice(4 * size, size));
                    row.InsideVertical = Border(op.Slice(5 * size, size));
                    break;
                case Sprms.TDefTableShd80:
                case Sprms.TDefTableShd:
                    if (row is not null)
                    {
                        var each = reader.Code == Sprms.TDefTableShd ? 10 : 2;
                        for (var i = 0; i < row.Cells.Count && (i + 1) * each <= op.Length; i++)
                        {
                            row.Cells[i].Shading = Shading(op.Slice(i * each, each));
                        }
                    }

                    break;
                case Sprms.TSetBrc80 when row is not null && op.Length >= 7:
                case Sprms.TSetBrc when row is not null && op.Length >= 11:
                    var line = Border(op[3..]);
                    for (var i = op[0]; i < Math.Min((int)op[1], row.Cells.Count); i++)
                    {
                        var cell = row.Cells[i];
                        cell.Top = (op[2] & 1) != 0 ? line : cell.Top;
                        cell.Left = (op[2] & 2) != 0 ? line : cell.Left;
                        cell.Bottom = (op[2] & 4) != 0 ? line : cell.Bottom;
                        cell.Right2 = (op[2] & 8) != 0 ? line : cell.Right2;
                    }

                    break;
                case Sprms.TVertMerge when row is not null && op.Length >= 2:
                    if (op[0] < row.Cells.Count)
                    {
                        row.Cells[op[0]].VerticalMerge = op[1];
                    }

                    break;
                case Sprms.TVertAlign when row is not null && op.Length >= 3:
                    for (var i = op[0]; i < Math.Min((int)op[1], row.Cells.Count); i++)
                    {
                        row.Cells[i].VerticalAlignment = VerticalAlignment(op[2]);
                    }

                    break;
                case Sprms.TCellPaddingDefault when op.Length >= 6:
                    row ??= new DocRowDefinition();
                    if (op[3] == 3)
                    {
                        var padding = BinaryPrimitives.ReadUInt16LittleEndian(op[4..]);
                        row.PaddingTop = (op[2] & 1) != 0 ? padding : row.PaddingTop;
                        row.PaddingLeft = (op[2] & 2) != 0 ? padding : row.PaddingLeft;
                        row.PaddingBottom = (op[2] & 4) != 0 ? padding : row.PaddingBottom;
                        row.PaddingRight = (op[2] & 8) != 0 ? padding : row.PaddingRight;
                    }

                    break;
            }
        }

        return row;
    }

    private static CellVerticalAlignment VerticalAlignment(int value) => value switch
    {
        1 => CellVerticalAlignment.Center,
        2 => CellVerticalAlignment.Bottom,
        _ => CellVerticalAlignment.Top,
    };

    /// <summary>The cells of a row ([MS-DOC] 2.9.311 TDefTableOperand): their edges and TC80 merges, alignment and borders.</summary>
    private static void DefineCells(DocRowDefinition row, ReadOnlySpan<byte> op)
    {
        if (op.Length < 1)
        {
            return;
        }

        var count = Math.Min((int)op[0], MaxCells);
        if (1 + (2 * (count + 1)) > op.Length)
        {
            count = Math.Max(0, ((op.Length - 1) / 2) - 1);
        }

        row.Cells.Clear();
        if (count == 0)
        {
            return;
        }

        row.Left = BinaryPrimitives.ReadInt16LittleEndian(op[1..]);
        var descriptors = 1 + (2 * (count + 1));
        for (var i = 0; i < count; i++)
        {
            var cell = new DocCellDefinition { Right = BinaryPrimitives.ReadInt16LittleEndian(op[(1 + (2 * (i + 1)))..]) };
            var at = descriptors + (20 * i);
            if (at + 20 <= op.Length)
            {
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(op[at..]);
                cell.HorizontalMerge = (byte)(flags & 3);
                cell.VerticalMerge = (byte)((flags >> 5) & 3);
                cell.VerticalAlignment = VerticalAlignment((flags >> 7) & 3);
                cell.Top = CellBorder(op.Slice(at + 4, 4));
                cell.Left = CellBorder(op.Slice(at + 8, 4));
                cell.Bottom = CellBorder(op.Slice(at + 12, 4));
                cell.Right2 = CellBorder(op.Slice(at + 16, 4));
            }

            row.Cells.Add(cell);
        }
    }

    /// <summary>A cell's own border: unset (all zeros, the table's) or a line.</summary>
    private static BorderLine? CellBorder(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value) == 0 ? null : Border(value);
}
