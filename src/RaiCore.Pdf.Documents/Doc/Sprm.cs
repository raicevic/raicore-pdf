using System.Buffers.Binary;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// Reads the property modifiers ([MS-DOC] 2.2.5.1 Sprm and 2.6) of a list of them (a <c>grpprl</c>) one by one: each is
/// a 2-byte code whose <c>spra</c> bits give the operand's size (1, 2, 3 or 4 bytes, or a size that precedes the
/// operand), followed by the operand. A modifier whose operand runs past the end ends the list.
/// </summary>
internal ref struct SprmReader(ReadOnlySpan<byte> grpprl)
{
    private readonly ReadOnlySpan<byte> _data = grpprl;
    private int _position;

    /// <summary>The current modifier's code (<c>sprm</c>).</summary>
    public ushort Code { get; private set; }

    /// <summary>The current modifier's operand.</summary>
    public ReadOnlySpan<byte> Operand { get; private set; }

    /// <summary>Moves to the next modifier, returning whether there is one.</summary>
    public bool Next()
    {
        if (_position + 2 > _data.Length)
        {
            return false;
        }

        var code = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        var start = _position + 2;
        int length;
        switch (code >> 13)
        {
            case 0:
            case 1:
                length = 1;
                break;
            case 2:
            case 4:
            case 5:
                length = 2;
                break;
            case 3:
                length = 4;
                break;
            case 7:
                length = 3;
                break;
            default:
                if (start >= _data.Length)
                {
                    return false;
                }

                if (code is Sprms.TDefTable or Sprms.TDefTable10)
                {
                    // A 2-byte size, counting itself less one ([MS-DOC] 2.9.311 TDefTableOperand).
                    if (start + 2 > _data.Length)
                    {
                        return false;
                    }

                    length = Math.Max(0, BinaryPrimitives.ReadUInt16LittleEndian(_data[start..]) - 1);
                    start += 2;
                }
                else if (code == Sprms.PChgTabs && _data[start] == 255)
                {
                    // The size is computed from the counts ([MS-DOC] 2.9.183 PChgTabsOperand).
                    var deleted = start + 1 < _data.Length ? _data[start + 1] : 0;
                    var added = start + 2 + (4 * deleted);
                    length = added < _data.Length ? 2 + (4 * deleted) + (3 * _data[added]) : int.MaxValue;
                    start++;
                }
                else
                {
                    length = _data[start];
                    start++;
                }

                break;
        }

        if (length > _data.Length - start)
        {
            return false;
        }

        Code = code;
        Operand = _data.Slice(start, length);
        _position = start + length;
        return true;
    }
}

/// <summary>The property modifiers the reader applies ([MS-DOC] 2.6), by name.</summary>
internal static class Sprms
{
    // Character properties (2.6.1).
    public const ushort CFRMarkDel = 0x0800;
    public const ushort CFRMarkIns = 0x0801;
    public const ushort CFFldVanish = 0x0802;
    public const ushort CPicLocation = 0x6A03;
    public const ushort CFData = 0x0806;
    public const ushort CFOle2 = 0x080A;
    public const ushort CHighlight = 0x2A0C;
    public const ushort CFSpec = 0x0855;
    public const ushort CSymbol = 0x6A09;
    public const ushort CIstd = 0x4A30;
    public const ushort CPlain = 0x2A33;
    public const ushort CFBold = 0x0835;
    public const ushort CFItalic = 0x0836;
    public const ushort CFStrike = 0x0837;
    public const ushort CFOutline = 0x0838;
    public const ushort CFShadow = 0x0839;
    public const ushort CFSmallCaps = 0x083A;
    public const ushort CFCaps = 0x083B;
    public const ushort CFVanish = 0x083C;
    public const ushort CFtc = 0x4A3D;
    public const ushort CKul = 0x2A3E;
    public const ushort CDxaSpace = 0x8840;
    public const ushort CIco = 0x2A42;
    public const ushort CHps = 0x4A43;
    public const ushort CHpsPos = 0x4845;
    public const ushort CIss = 0x2A48;
    public const ushort CRgFtc0 = 0x4A4F;
    public const ushort CRgFtc1 = 0x4A50;
    public const ushort CRgFtc2 = 0x4A51;
    public const ushort CFDStrike = 0x2A53;
    public const ushort CFBiDi = 0x085A;
    public const ushort CFBoldBi = 0x085C;
    public const ushort CFItalicBi = 0x085D;
    public const ushort CShd80 = 0x4866;
    public const ushort CCv = 0x6870;
    public const ushort CShd = 0xCA71;

    // Paragraph properties (2.6.2).
    public const ushort PIstd = 0x4600;
    public const ushort PJc80 = 0x2403;
    public const ushort PFKeep = 0x2405;
    public const ushort PFKeepFollow = 0x2406;
    public const ushort PFPageBreakBefore = 0x2407;
    public const ushort PIlvl = 0x260A;
    public const ushort PIlfo = 0x460B;
    public const ushort PChgTabsPapx = 0xC60D;
    public const ushort PDxaRight80 = 0x840E;
    public const ushort PDxaLeft80 = 0x840F;
    public const ushort PDxaLeft180 = 0x8411;
    public const ushort PDyaLine = 0x6412;
    public const ushort PDyaBefore = 0xA413;
    public const ushort PDyaAfter = 0xA414;
    public const ushort PChgTabs = 0xC615;
    public const ushort PFInTable = 0x2416;
    public const ushort PFTtp = 0x2417;
    public const ushort PBrcTop80 = 0x6424;
    public const ushort PBrcLeft80 = 0x6425;
    public const ushort PBrcBottom80 = 0x6426;
    public const ushort PBrcRight80 = 0x6427;
    public const ushort PBrcBetween80 = 0x6428;
    public const ushort PShd80 = 0x442D;
    public const ushort PFWidowControl = 0x2431;
    public const ushort POutLvl = 0x2640;
    public const ushort PFBiDi = 0x2441;
    public const ushort PFInnerTableCell = 0x244B;
    public const ushort PFInnerTtp = 0x244C;
    public const ushort PShd = 0xC64D;
    public const ushort PBrcTop = 0xC64E;
    public const ushort PBrcLeft = 0xC64F;
    public const ushort PBrcBottom = 0xC650;
    public const ushort PBrcRight = 0xC651;
    public const ushort PBrcBetween = 0xC652;
    public const ushort PDxaRight = 0x845D;
    public const ushort PDxaLeft = 0x845E;
    public const ushort PDxaLeft1 = 0x8460;
    public const ushort PJc = 0x2461;
    public const ushort PHugePapx = 0x6646;
    public const ushort PItap = 0x6649;
    public const ushort PDtap = 0x664A;
    public const ushort PFContextualSpacing = 0x246D;

    // Table properties (2.6.4).
    public const ushort TJc90 = 0x5400;
    public const ushort TDxaGapHalf = 0x9602;
    public const ushort TFCantSplit = 0x3403;
    public const ushort TTableHeader = 0x3404;
    public const ushort TTableBorders80 = 0xD605;
    public const ushort TDefTable10 = 0xD606;
    public const ushort TDyaRowHeight = 0x9407;
    public const ushort TDefTable = 0xD608;
    public const ushort TDefTableShd80 = 0xD609;
    public const ushort TDefTableShd = 0xD612;
    public const ushort TTableBorders = 0xD613;
    public const ushort TSetBrc80 = 0xD620;
    public const ushort TVertMerge = 0xD62B;
    public const ushort TVertAlign = 0xD62C;
    public const ushort TSetBrc = 0xD62F;
    public const ushort TCellPaddingDefault = 0xD634;
    public const ushort TFCantSplit90 = 0x3644;
    public const ushort TJc = 0x548A;

    // Section properties (2.6.3).
    public const ushort SBkc = 0x3009;
    public const ushort SFTitlePage = 0x300A;
    public const ushort SCcolumns = 0x500B;
    public const ushort SNfcPgn = 0x300E;
    public const ushort SFPgnRestart = 0x3011;
    public const ushort SDyaHdrTop = 0xB017;
    public const ushort SDyaHdrBottom = 0xB018;
    public const ushort SPgnStart97 = 0x501C;
    public const ushort SXaPage = 0xB01F;
    public const ushort SYaPage = 0xB020;
    public const ushort SDxaLeft = 0xB021;
    public const ushort SDxaRight = 0xB022;
    public const ushort SDyaTop = 0x9023;
    public const ushort SDyaBottom = 0x9024;
    public const ushort SDzaGutter = 0xB025;
}
