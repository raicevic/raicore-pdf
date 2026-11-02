using System.Text;

namespace RaiCore.Pdf.Layout.Text;

/// <summary>A position where a line may (or must) be broken.</summary>
/// <param name="Position">UTF-16 index of the first character of the next line.</param>
/// <param name="Required">A mandatory break (after a line feed, paragraph separator, ...).</param>
public readonly record struct LineBreakOpportunity(int Position, bool Required);

/// <summary>
/// The Unicode Line Breaking Algorithm (UAX #14), with the default (untailored) rules and the Unicode
/// version in <see cref="UnicodeVersion"/>. Verified against the official LineBreakTest.txt.
/// </summary>
public static class LineBreaker
{
    /// <summary>The Unicode version of the line breaking data.</summary>
    public static string UnicodeVersion => LineBreakData.UnicodeVersion;

    /// <summary>
    /// All break opportunities in <paramref name="text"/>, in order. The end of the text is always included.
    /// </summary>
    public static IReadOnlyList<LineBreakOpportunity> GetBreakOpportunities(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<LineBreakOpportunity>();
        if (text.Length == 0)
        {
            return result;
        }

        // Work on code points; remember where each starts in UTF-16.
        var codePoints = new List<int>(text.Length);
        var offsets = new List<int>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            codePoints.Add(rune.Value);
            offsets.Add(i);
            i += rune.Utf16SequenceLength;
        }

        var breaks = Analyze(codePoints);
        for (var i = 1; i < codePoints.Count; i++)
        {
            if (breaks[i] != Break.None)
            {
                result.Add(new LineBreakOpportunity(offsets[i], breaks[i] == Break.Required));
            }
        }

        var last = Resolve(LineBreakData.Get(codePoints[^1])).Class;
        result.Add(new LineBreakOpportunity(text.Length, last is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL));
        return result;
    }

    internal enum Break : byte
    {
        None,
        Allowed,
        Required,
    }

    /// <summary>Break decision before each code point (index 0 is never a break).</summary>
    internal static Break[] Analyze(IReadOnlyList<int> codePoints)
    {
        var n = codePoints.Count;
        var raw = new Props[n];
        for (var i = 0; i < n; i++)
        {
            raw[i] = Resolve(LineBreakData.Get(codePoints[i]));
        }

        // LB9/LB10: a combining mark or ZWJ takes the properties of the base it attaches to;
        // one with no base becomes AL (with the properties of "A").
        var unit = new Props[n];
        var attached = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (raw[i].Class is LineBreakClass.CM or LineBreakClass.ZWJ)
            {
                attached[i] = i > 0 && !IsBreakOrSpace(BaseClassBefore(raw, attached, i));
                unit[i] = attached[i] ? unit[i - 1] : Props.LatinA;
            }
            else
            {
                unit[i] = raw[i];
            }
        }

        var result = new Break[n];
        for (var i = 1; i < n; i++)
        {
            result[i] = Decide(codePoints, raw, unit, attached, i);
        }

        return result;
    }

    /// <summary>Class of the character a CM/ZWJ at <paramref name="i"/> would attach to (the start of its sequence).</summary>
    private static LineBreakClass BaseClassBefore(Props[] raw, bool[] attached, int i)
    {
        var j = i - 1;
        while (j >= 0 && raw[j].Class is LineBreakClass.CM or LineBreakClass.ZWJ && attached[j])
        {
            j--;
        }

        // j is the base, or a CM/ZWJ that did not attach (which LB10 turned into AL).
        return raw[j].Class is LineBreakClass.CM or LineBreakClass.ZWJ ? LineBreakClass.AL : raw[j].Class;
    }

    private static bool IsBreakOrSpace(LineBreakClass c) =>
        c is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW;

    private static Break Decide(IReadOnlyList<int> cps, Props[] raw, Props[] unit, bool[] attached, int i)
    {
        var before = raw[i - 1].Class;
        var after = raw[i].Class;

        // LB4, LB5: hard line breaks.
        if (before == LineBreakClass.BK)
        {
            return Break.Required;
        }

        if (before == LineBreakClass.CR)
        {
            return after == LineBreakClass.LF ? Break.None : Break.Required;
        }

        if (before is LineBreakClass.LF or LineBreakClass.NL)
        {
            return Break.Required;
        }

        // LB6, LB7.
        if (after is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW)
        {
            return Break.None;
        }

        // LB8: ZW SP* ÷
        var k = i - 1;
        while (k > 0 && raw[k].Class == LineBreakClass.SP)
        {
            k--;
        }

        if (raw[k].Class == LineBreakClass.ZW)
        {
            return Break.Allowed;
        }

        // LB8a: ZWJ ×
        if (before == LineBreakClass.ZWJ)
        {
            return Break.None;
        }

        // LB9: never break inside a combining sequence.
        if (attached[i])
        {
            return Break.None;
        }

        // From here on, compare whole units: A is the unit ending before i, B the one starting at i.
        var a = unit[i - 1];
        var b = unit[i];
        var A = a.Class;
        var B = b.Class;

        // Unit-level neighbours, skipping attached combining marks.
        int UnitStart(int index)
        {
            while (index > 0 && attached[index])
            {
                index--;
            }

            return index;
        }

        int PrevUnit(int index) => index <= 0 ? -1 : UnitStart(index - 1); // Unit before the one starting at index.
        int NextUnit(int index)
        {
            var next = index + 1;
            while (next < raw.Length && attached[next])
            {
                next++;
            }

            return next < raw.Length ? next : -1;
        }

        var aStart = UnitStart(i - 1);
        LineBreakClass? ClassAt(int index) => index < 0 ? null : unit[index].Class;

        // Walks back from the unit starting at aStart over spaces; returns the first non-space unit start, or -1 (sot).
        int SkipSpacesBack(int start)
        {
            var s = start;
            while (s >= 0 && unit[s].Class == LineBreakClass.SP)
            {
                s = PrevUnit(s);
            }

            return s;
        }

        // LB11
        if (B == LineBreakClass.WJ || A == LineBreakClass.WJ)
        {
            return Break.None;
        }

        // LB12, LB12a
        if (A == LineBreakClass.GL)
        {
            return Break.None;
        }

        if (B == LineBreakClass.GL && A is not (LineBreakClass.SP or LineBreakClass.HY or LineBreakClass.HH))
        {
            return Break.None;
        }

        // LB13
        if (B is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY)
        {
            return Break.None;
        }

        // LB14: OP SP* ×
        var beforeSpaces = SkipSpacesBack(aStart);
        if (beforeSpaces >= 0 && unit[beforeSpaces].Class == LineBreakClass.OP)
        {
            return Break.None;
        }

        // LB15a: (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW) [Pi & QU] SP* ×
        if (beforeSpaces >= 0 && unit[beforeSpaces].Class == LineBreakClass.QU && unit[beforeSpaces].IsPi)
        {
            var prev = PrevUnit(beforeSpaces);
            if (prev < 0 || unit[prev].Class is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL
                or LineBreakClass.OP or LineBreakClass.QU or LineBreakClass.GL or LineBreakClass.SP or LineBreakClass.ZW)
            {
                return Break.None;
            }
        }

        // LB15b: × [Pf & QU] (SP | GL | WJ | CL | QU | CP | EX | IS | SY | BK | CR | LF | NL | ZW | eot)
        if (B == LineBreakClass.QU && b.IsPf)
        {
            var next = ClassAt(NextUnit(i));
            if (next is null or LineBreakClass.SP or LineBreakClass.GL or LineBreakClass.WJ or LineBreakClass.CL or LineBreakClass.QU
                or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY or LineBreakClass.BK
                or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.ZW)
            {
                return Break.None;
            }
        }

        // LB15c: SP ÷ IS NU
        if (A == LineBreakClass.SP && B == LineBreakClass.IS && ClassAt(NextUnit(i)) == LineBreakClass.NU)
        {
            return Break.Allowed;
        }

        // LB15d: × IS
        if (B == LineBreakClass.IS)
        {
            return Break.None;
        }

        // LB16: (CL | CP) SP* × NS
        if (B == LineBreakClass.NS && beforeSpaces >= 0 && unit[beforeSpaces].Class is LineBreakClass.CL or LineBreakClass.CP)
        {
            return Break.None;
        }

        // LB17: B2 SP* × B2
        if (B == LineBreakClass.B2 && beforeSpaces >= 0 && unit[beforeSpaces].Class == LineBreakClass.B2)
        {
            return Break.None;
        }

        // LB18: SP ÷
        if (A == LineBreakClass.SP)
        {
            return Break.Allowed;
        }

        // LB19: × [QU - Pi], [QU - Pf] ×
        if ((B == LineBreakClass.QU && !b.IsPi) || (A == LineBreakClass.QU && !a.IsPf))
        {
            return Break.None;
        }

        // LB19a: quotation marks not surrounded by East Asian characters.
        if (B == LineBreakClass.QU)
        {
            var next = NextUnit(i);
            if (!a.IsEastAsian || next < 0 || !unit[next].IsEastAsian)
            {
                return Break.None;
            }
        }

        if (A == LineBreakClass.QU)
        {
            var prev = PrevUnit(aStart);
            if (!b.IsEastAsian || prev < 0 || !unit[prev].IsEastAsian)
            {
                return Break.None;
            }
        }

        // LB20
        if (A == LineBreakClass.CB || B == LineBreakClass.CB)
        {
            return Break.Allowed;
        }

        // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) (HY | HH) × (AL | HL)
        if (A is LineBreakClass.HY or LineBreakClass.HH && B is LineBreakClass.AL or LineBreakClass.HL)
        {
            var prev = ClassAt(PrevUnit(aStart));
            if (prev is null or LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL
                or LineBreakClass.SP or LineBreakClass.ZW or LineBreakClass.CB or LineBreakClass.GL)
            {
                return Break.None;
            }
        }

        // LB21
        if (B is LineBreakClass.BA or LineBreakClass.HH or LineBreakClass.HY or LineBreakClass.NS || A == LineBreakClass.BB)
        {
            return Break.None;
        }

        // LB21a: HL (HY | HH) × [^HL]
        if (A is LineBreakClass.HY or LineBreakClass.HH && B != LineBreakClass.HL && ClassAt(PrevUnit(aStart)) == LineBreakClass.HL)
        {
            return Break.None;
        }

        // LB21b, LB22
        if ((A == LineBreakClass.SY && B == LineBreakClass.HL) || B == LineBreakClass.IN)
        {
            return Break.None;
        }

        // LB23
        if ((A is LineBreakClass.AL or LineBreakClass.HL && B == LineBreakClass.NU) || (A == LineBreakClass.NU && B is LineBreakClass.AL or LineBreakClass.HL))
        {
            return Break.None;
        }

        // LB23a
        if ((A == LineBreakClass.PR && B is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM)
            || (A is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && B == LineBreakClass.PO))
        {
            return Break.None;
        }

        // LB24
        if ((A is LineBreakClass.PR or LineBreakClass.PO && B is LineBreakClass.AL or LineBreakClass.HL)
            || (A is LineBreakClass.AL or LineBreakClass.HL && B is LineBreakClass.PR or LineBreakClass.PO))
        {
            return Break.None;
        }

        // LB25: numbers.
        if (B is LineBreakClass.PO or LineBreakClass.PR || B == LineBreakClass.NU)
        {
            // NU (SY | IS)* (CL | CP)? × (PO | PR)   and   NU (SY | IS)* × NU
            var s = aStart;
            if (B != LineBreakClass.NU && unit[s].Class is LineBreakClass.CL or LineBreakClass.CP)
            {
                s = PrevUnit(s);
            }

            while (s >= 0 && unit[s].Class is LineBreakClass.SY or LineBreakClass.IS)
            {
                s = PrevUnit(s);
            }

            if (s >= 0 && unit[s].Class == LineBreakClass.NU)
            {
                return Break.None;
            }
        }

        if (A is LineBreakClass.PO or LineBreakClass.PR)
        {
            if (B == LineBreakClass.NU)
            {
                return Break.None;
            }

            if (B == LineBreakClass.OP)
            {
                var next = NextUnit(i);
                var nextClass = ClassAt(next);
                if (nextClass == LineBreakClass.NU || (nextClass == LineBreakClass.IS && ClassAt(NextUnit(next)) == LineBreakClass.NU))
                {
                    return Break.None;
                }
            }
        }

        if (A is LineBreakClass.HY or LineBreakClass.IS && B == LineBreakClass.NU)
        {
            return Break.None;
        }

        // LB26, LB27: Korean syllable blocks.
        if ((A == LineBreakClass.JL && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3)
            || (A is LineBreakClass.JV or LineBreakClass.H2 && B is LineBreakClass.JV or LineBreakClass.JT)
            || (A is LineBreakClass.JT or LineBreakClass.H3 && B == LineBreakClass.JT)
            || (A is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3 && B == LineBreakClass.PO)
            || (A == LineBreakClass.PR && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3))
        {
            return Break.None;
        }

        // LB28
        if (A is LineBreakClass.AL or LineBreakClass.HL && B is LineBreakClass.AL or LineBreakClass.HL)
        {
            return Break.None;
        }

        // LB28a: Brahmic orthographic syllables. "Base" is AK, U+25CC DOTTED CIRCLE or AS.
        bool IsAksaraBase(int index) => index >= 0 && (unit[index].Class is LineBreakClass.AK or LineBreakClass.AS || cps[index] == 0x25CC);
        bool IsAkOrCircle(int index) => index >= 0 && (unit[index].Class == LineBreakClass.AK || cps[index] == 0x25CC);
        if ((A == LineBreakClass.AP && IsAksaraBase(i))
            || (IsAksaraBase(aStart) && B is LineBreakClass.VF or LineBreakClass.VI)
            || (A == LineBreakClass.VI && IsAksaraBase(PrevUnit(aStart)) && IsAkOrCircle(i))
            || (IsAksaraBase(aStart) && IsAksaraBase(i) && ClassAt(NextUnit(i)) == LineBreakClass.VF))
        {
            return Break.None;
        }

        // LB29
        if (A == LineBreakClass.IS && B is LineBreakClass.AL or LineBreakClass.HL)
        {
            return Break.None;
        }

        // LB30
        if ((A is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU && B == LineBreakClass.OP && !b.IsEastAsian)
            || (A == LineBreakClass.CP && !a.IsEastAsian && B is LineBreakClass.AL or LineBreakClass.HL or LineBreakClass.NU))
        {
            return Break.None;
        }

        // LB30a: regional indicator pairs (flags).
        if (A == LineBreakClass.RI && B == LineBreakClass.RI)
        {
            var count = 0;
            for (var s = aStart; s >= 0 && unit[s].Class == LineBreakClass.RI; s = PrevUnit(s))
            {
                count++;
            }

            if (count % 2 == 1)
            {
                return Break.None;
            }
        }

        // LB30b
        if (B == LineBreakClass.EM && (A == LineBreakClass.EB || (a.IsExtendedPictographic && a.IsUnassigned)))
        {
            return Break.None;
        }

        // LB31
        return Break.Allowed;
    }

    /// <summary>LB1: resolve classes whose behavior depends on context outside the algorithm.</summary>
    internal static Props Resolve(ushort packed)
    {
        var props = new Props(packed);
        return props.Class switch
        {
            LineBreakClass.AI or LineBreakClass.SG or LineBreakClass.XX => props.WithClass(LineBreakClass.AL),
            LineBreakClass.SA => props.WithClass(props.IsMark ? LineBreakClass.CM : LineBreakClass.AL),
            LineBreakClass.CJ => props.WithClass(LineBreakClass.NS),
            _ => props,
        };
    }

    /// <summary>Line_Break class plus the other character properties the rules consult.</summary>
    internal readonly record struct Props(ushort Packed)
    {
        /// <summary>Properties of U+0041, used for combining marks with no base (LB10).</summary>
        public static readonly Props LatinA = new((ushort)LineBreakClass.AL);

        public LineBreakClass Class => (LineBreakClass)(Packed & 0x3F);

        public bool IsEastAsian => (Packed & (1 << 6)) != 0;

        public bool IsExtendedPictographic => (Packed & (1 << 7)) != 0;

        public bool IsMark => (Packed & (1 << 8)) != 0;

        public bool IsPi => (Packed & (1 << 9)) != 0;

        public bool IsPf => (Packed & (1 << 10)) != 0;

        public bool IsUnassigned => (Packed & (1 << 11)) != 0;

        public Props WithClass(LineBreakClass c) => new((ushort)((Packed & ~0x3F) | (int)c));
    }
}

/// <summary>Unicode Line_Break property values. Order matches tools/generate_line_break_data.py.</summary>
internal enum LineBreakClass : byte
{
    XX, BK, CR, LF, NL, SP, ZW, WJ, GL, CM, ZWJ, CB, BA, BB, B2, HY, HH,
    CL, CP, EX, IN, NS, OP, QU, IS, NU, PO, PR, SY, AI, AK, AL, AP, AS,
    CJ, EB, EM, H2, H3, HL, ID, JL, JV, JT, RI, SA, SG, VF, VI,
}

internal static partial class LineBreakData
{
    private static readonly ushort[] Latin1 = BuildLatin1();

    public static ushort Get(int codePoint)
    {
        if ((uint)codePoint < 256)
        {
            return Latin1[codePoint];
        }

        var starts = RangeStarts;
        var index = starts.BinarySearch(codePoint);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return RangeValues[index];
    }

    private static ushort[] BuildLatin1()
    {
        var table = new ushort[256];
        var starts = RangeStarts;
        var range = 0;
        for (var c = 0; c < 256; c++)
        {
            while (range + 1 < starts.Length && starts[range + 1] <= c)
            {
                range++;
            }

            table[c] = RangeValues[range];
        }

        return table;
    }
}
