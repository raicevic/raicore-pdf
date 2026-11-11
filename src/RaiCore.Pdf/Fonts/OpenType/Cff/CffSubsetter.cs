using System.Text;

namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>
/// Subsets the <c>CFF </c> or <c>CFF2</c> outlines of an OpenType font to a bare CID-keyed CFF font program, for
/// embedding as <c>/FontFile3</c> with <c>/Subtype /CIDFontType0C</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only the requested glyphs (and <c>.notdef</c>) are kept, renumbered in their original order. The output is always
/// CID-keyed with a charset that maps each kept glyph to its original glyph ID as CID, so a PDF can keep using the
/// original glyph IDs as CIDs (Identity-H) without a CIDToGIDMap. Name-keyed fonts become CID-keyed with one Font
/// DICT holding their Private DICT.
/// </para>
/// <para>
/// By default the kept glyphs are desubroutinized: subroutine calls are inlined, so the output has no subroutines.
/// The PDF stream is Flate-compressed, and inlined charstrings compress better than subroutinized ones: on Source
/// Sans 3 and Noto Sans JP, from a few glyphs to every glyph, the compressed desubroutinized subset was 3-15% smaller.
/// With <c>keepSubroutines</c>, only the global and local subroutines the kept glyphs call (transitively) are kept,
/// renumbered with the bias of the new subroutine counts; when a subroutine number is not a literal right before
/// the call, or a global subroutine would need different local numbers for different Font DICTs, the glyphs are
/// desubroutinized after all.
/// </para>
/// <para>
/// CFF2 fonts are always desubroutinized and converted to CFF at the default instance: <c>blend</c>s are reduced to
/// their default values, argument lists longer than CFF's 48 are split, and the advance widths are added to the
/// charstrings.
/// </para>
/// </remarks>
internal static class CffSubsetter
{
    private const int SidAdobe = 391; // The first custom string, after the 391 standard strings.
    private const int SidIdentity = 392;

    // Top DICT entries that are copied (all numeric; string IDs would point into the old String INDEX).
    private static readonly int[] CopiedTopEntries =
        [CffDict.FontBBox, CffDict.IsFixedPitch, CffDict.ItalicAngle, CffDict.UnderlinePosition, CffDict.UnderlineThickness];

    /// <summary>
    /// Returns the subset CFF font program. Throws <see cref="FontFormatException"/> for malformed fonts and
    /// <see cref="NotSupportedException"/> for charstring features the subsetter does not handle.
    /// </summary>
    public static byte[] Subset(OpenTypeFont font, IEnumerable<int> glyphIds, bool keepSubroutines = false)
    {
        var cff = CffFont.Load(font);
        var glyphs = glyphIds.Append(0).Where(g => g >= 0 && g < cff.NumGlyphs).Distinct().Order().ToArray();
        var interpreter = new CharStringInterpreter(cff);

        var subroutines = !keepSubroutines || cff.IsCff2 ? null : SubroutineSubset.TryCreate(cff, interpreter, glyphs);
        var charStrings = subroutines?.CharStrings ?? glyphs
            .Select(g => CharStringFlattener.Encode(
                CharStringFlattener.Flatten(interpreter, g), cff.IsCff2 ? font.GetAdvanceWidth(g) : null))
            .ToArray();

        // Keep only the Font DICTs the kept glyphs use.
        var oldFontDicts = glyphs.Select(cff.FontDictIndex).Distinct().Order().ToArray();
        var fontDictMap = new Dictionary<int, int>();
        for (var i = 0; i < oldFontDicts.Length; i++)
        {
            fontDictMap[oldFontDicts[i]] = i;
        }

        var fdSelect = glyphs.Select(g => fontDictMap[cff.FontDictIndex(g)]).ToArray();
        var localSubrs = oldFontDicts
            .Select(fd => subroutines is not null && subroutines.Local.TryGetValue(fd, out var subrs) ? subrs : [])
            .ToArray();
        var privateDicts = oldFontDicts
            .Select((fd, i) => PrivateDict(cff.FontDicts[fd].Private, localSubrs[i].Length > 0))
            .ToArray();

        // A name-keyed or CFF2 font's FontMatrix moves from the Top DICT to its Font DICT.
        var fontMatrix = cff.IsCidKeyed && !cff.IsCff2 ? null : cff.TopDict.Get(CffDict.FontMatrix);
        var fontMatrices = oldFontDicts.Select(fd => cff.FontDicts[fd].FontDict?.Get(CffDict.FontMatrix) ?? fontMatrix).ToArray();

        return Build(font, cff, glyphs, charStrings, subroutines?.Global ?? [], fdSelect, fontMatrices, privateDicts, localSubrs);
    }

    private static byte[] Build(
        OpenTypeFont font, CffFont cff, int[] glyphs, byte[][] charStrings, byte[][] globalSubrs, int[] fdSelect,
        CffOperand[]?[] fontMatrices, byte[][] privateDicts, byte[][][] localSubrs)
    {
        var charset = Charset(glyphs);
        var fdSelectData = FdSelect(fdSelect);
        var name = Encoding.ASCII.GetBytes(font.PostScriptName);
        byte[][] strings = [Encoding.ASCII.GetBytes("Adobe"), Encoding.ASCII.GetBytes("Identity")];

        // Offsets are written as 5-byte integers, so DICT sizes are known before the offsets are.
        var topSize = TopDict(cff, font, glyphs, 0, 0, 0, 0).Length;
        var charsetOffset = 4 + CffIndex.SizeOf([name]) + CffIndex.SizeOf([new byte[topSize]])
            + CffIndex.SizeOf(strings) + CffIndex.SizeOf(globalSubrs);
        var fdSelectOffset = charsetOffset + charset.Length;
        var charStringsOffset = fdSelectOffset + fdSelectData.Length;
        var fdArrayOffset = charStringsOffset + CffIndex.SizeOf(charStrings);

        var fontDicts = new byte[privateDicts.Length][];
        var privateOffset = fdArrayOffset + CffIndex.SizeOf([.. fontMatrices.Select(m => FontDict(m, 0, 0))]);
        for (var i = 0; i < fontDicts.Length; i++)
        {
            fontDicts[i] = FontDict(fontMatrices[i], privateDicts[i].Length, privateOffset);
            privateOffset += privateDicts[i].Length + (localSubrs[i].Length > 0 ? CffIndex.SizeOf(localSubrs[i]) : 0);
        }

        using var output = new MemoryStream();
        output.Write([1, 0, 4, 4]);
        CffIndex.Write(output, [name]);
        CffIndex.Write(output, [TopDict(cff, font, glyphs, charsetOffset, fdSelectOffset, charStringsOffset, fdArrayOffset)]);
        CffIndex.Write(output, strings);
        CffIndex.Write(output, globalSubrs);
        output.Write(charset);
        output.Write(fdSelectData);
        CffIndex.Write(output, charStrings);
        CffIndex.Write(output, fontDicts);
        for (var i = 0; i < privateDicts.Length; i++)
        {
            output.Write(privateDicts[i]);
            if (localSubrs[i].Length > 0)
            {
                CffIndex.Write(output, localSubrs[i]);
            }
        }

        return output.ToArray();
    }

    private static byte[] TopDict(CffFont cff, OpenTypeFont font, int[] glyphs, int charset, int fdSelect, int charStrings, int fdArray)
    {
        var dict = new CffDictWriter().Int(SidAdobe).Int(SidIdentity).Int(0).Op(CffDict.Ros);
        foreach (var op in CopiedTopEntries)
        {
            if (cff.TopDict.Get(op) is { } operands)
            {
                dict.Operands(operands).Op(op);
            }
        }

        if (!cff.TopDict.Has(CffDict.FontBBox))
        {
            dict.Int(font.XMin).Int(font.YMin).Int(font.XMax).Int(font.YMax).Op(CffDict.FontBBox);
        }

        if (cff.IsCidKeyed && !cff.IsCff2 && cff.TopDict.Get(CffDict.FontMatrix) is { } matrix)
        {
            dict.Operands(matrix).Op(CffDict.FontMatrix);
        }

        return dict.Int(glyphs[^1] + 1).Op(CffDict.CidCount)
            .Int32(charset).Op(CffDict.Charset)
            .Int32(fdSelect).Op(CffDict.FdSelect)
            .Int32(charStrings).Op(CffDict.CharStrings)
            .Int32(fdArray).Op(CffDict.FdArray)
            .ToArray();
    }

    private static byte[] FontDict(CffOperand[]? fontMatrix, int privateSize, int privateOffset)
    {
        var dict = new CffDictWriter();
        if (fontMatrix is not null)
        {
            dict.Operands(fontMatrix).Op(CffDict.FontMatrix);
        }

        return dict.Int32(privateSize).Int32(privateOffset).Op(CffDict.Private).ToArray();
    }

    /// <summary>The Private DICT without its old Subrs offset; local subroutines, if any, follow it directly.</summary>
    private static byte[] PrivateDict(CffDict source, bool hasSubrs)
    {
        var dict = new CffDictWriter();
        foreach (var (op, operands) in source.Entries)
        {
            if (op != CffDict.Subrs)
            {
                dict.Operands(operands).Op(op);
            }
        }

        if (hasSubrs)
        {
            dict.Int32(dict.Length + 6).Op(CffDict.Subrs); // 5-byte offset + 1-byte operator.
        }

        return dict.ToArray();
    }

    /// <summary>Maps each new glyph ID (after .notdef) to its original glyph ID as CID: format 0 or 2, whichever is smaller.</summary>
    private static byte[] Charset(int[] glyphs)
    {
        var ranges = new List<(int First, int Left)>();
        for (var i = 1; i < glyphs.Length; i++)
        {
            if (ranges.Count > 0 && ranges[^1].First + ranges[^1].Left + 1 == glyphs[i])
            {
                ranges[^1] = (ranges[^1].First, ranges[^1].Left + 1);
            }
            else
            {
                ranges.Add((glyphs[i], 0));
            }
        }

        using var output = new MemoryStream();
        if (ranges.Count * 4 < (glyphs.Length - 1) * 2)
        {
            output.WriteByte(2);
            foreach (var (first, left) in ranges)
            {
                WriteUInt16(output, first);
                WriteUInt16(output, left);
            }
        }
        else
        {
            output.WriteByte(0);
            foreach (var gid in glyphs.Skip(1))
            {
                WriteUInt16(output, gid);
            }
        }

        return output.ToArray();
    }

    /// <summary>The Font DICT of each new glyph: format 0 or 3, whichever is smaller.</summary>
    private static byte[] FdSelect(int[] fontDicts)
    {
        var ranges = new List<(int First, int FontDict)>();
        for (var i = 0; i < fontDicts.Length; i++)
        {
            if (i == 0 || fontDicts[i] != fontDicts[i - 1])
            {
                ranges.Add((i, fontDicts[i]));
            }
        }

        using var output = new MemoryStream();
        if (5 + (ranges.Count * 3) < 1 + fontDicts.Length)
        {
            output.WriteByte(3);
            WriteUInt16(output, ranges.Count);
            foreach (var (first, fontDict) in ranges)
            {
                WriteUInt16(output, first);
                output.WriteByte((byte)fontDict);
            }

            WriteUInt16(output, fontDicts.Length);
        }
        else
        {
            output.WriteByte(0);
            foreach (var fontDict in fontDicts)
            {
                output.WriteByte((byte)fontDict);
            }
        }

        return output.ToArray();
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }
}
