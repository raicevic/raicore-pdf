using System.Text;

namespace RaiCore.Pdf.Layout.Text;

/// <summary>The Unicode Bidi_Class property values (UAX #9 table 4). Order matches tools/generate_bidi_data.py.</summary>
internal enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON, LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>The Bidi_Paired_Bracket_Type property.</summary>
internal enum BracketType : byte
{
    None,
    Open,
    Close,
}

internal static partial class BidiData
{
    public static BidiClass GetClass(int codePoint)
    {
        var index = RangeStarts.BinarySearch(codePoint);
        return (BidiClass)RangeClasses[index >= 0 ? index : ~index - 1];
    }

    /// <summary>A bracket's type and an id shared by both brackets of a pair (canonical equivalents included).</summary>
    public static (BracketType Type, int Pair) GetBracket(int codePoint)
    {
        var index = BracketCodePoints.BinarySearch(codePoint);
        if (index < 0)
        {
            return (BracketType.None, 0);
        }

        var type = (BracketType)BracketKinds[index];
        var opening = type == BracketType.Open ? codePoint : BracketPairs[index];

        // U+2329/U+232A are canonically equivalent to U+3008/U+3009 (UAX #9 BD16).
        return (type, opening == 0x2329 ? 0x3008 : opening);
    }
}

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9) up to rule L2: resolves embedding levels for paragraphs and reorders
/// lines. Characters removed by rule X9 (embedding controls and boundary neutrals) are reported as such and take the
/// level of the character before them (or after, at the start of a paragraph).
/// </summary>
internal static class BidiAlgorithm
{
    /// <summary>The deepest embedding level (BD2).</summary>
    public const int MaxDepth = 125;

    /// <summary>Whether a class is removed by rule X9.</summary>
    public static bool IsRemovedByX9(BidiClass c) => c is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    /// <summary>Whether a class is an explicit formatting character (embedding, override or isolate control).</summary>
    public static bool IsExplicitControl(BidiClass c) => c is >= BidiClass.LRE and <= BidiClass.PDI;

    /// <summary>
    /// Resolves the levels of every UTF-16 code unit of a text, which may hold several paragraphs (rule P1).
    /// <paramref name="paragraphLevel"/> is 0 (left-to-right), 1 (right-to-left) or <c>null</c> to take each
    /// paragraph's direction from its first strong character (rules P2 and P3).
    /// </summary>
    public static BidiText Resolve(string text, int? paragraphLevel)
    {
        // Work on code points, then spread the results over the code units.
        var codePoints = new List<int>(text.Length);
        var unitStarts = new List<int>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            codePoints.Add(rune.Value);
            unitStarts.Add(i);
            i += rune.Utf16SequenceLength;
        }

        var n = codePoints.Count;
        var types = new BidiClass[n];
        var bracketTypes = new BracketType[n];
        var bracketPairs = new int[n];
        for (var i = 0; i < n; i++)
        {
            types[i] = BidiData.GetClass(codePoints[i]);
            (bracketTypes[i], bracketPairs[i]) = BidiData.GetBracket(codePoints[i]);
        }

        var levels = new byte[n];
        var paragraphs = new List<(int Start, int End, int Level)>();
        var start = 0;
        for (var i = 0; i < n; i++)
        {
            if (types[i] == BidiClass.B || i == n - 1)
            {
                var end = i + 1;
                var level = ResolveParagraph(types.AsSpan(start, end - start), bracketTypes.AsSpan(start, end - start), bracketPairs.AsSpan(start, end - start), paragraphLevel, levels.AsSpan(start, end - start));
                paragraphs.Add((start, end, level));
                start = end;
            }
        }

        var unitLevels = new byte[text.Length];
        var unitTypes = new BidiClass[text.Length];
        for (var i = 0; i < n; i++)
        {
            var end = i + 1 < n ? unitStarts[i + 1] : text.Length;
            for (var u = unitStarts[i]; u < end; u++)
            {
                unitLevels[u] = levels[i];
                unitTypes[u] = types[i];
            }
        }

        var unitParagraphs = paragraphs.Select(p => (unitStarts[p.Start], p.End < n ? unitStarts[p.End] : text.Length, p.Level)).ToList();
        return new BidiText(unitLevels, unitTypes, unitParagraphs);
    }

    /// <summary>
    /// Resolves one paragraph's levels into <paramref name="levels"/> and returns its embedding level.
    /// <paramref name="bracketPairs"/> identifies each bracket's pair (the same value for both brackets).
    /// </summary>
    public static int ResolveParagraph(
        ReadOnlySpan<BidiClass> originalTypes, ReadOnlySpan<BracketType> bracketTypes, ReadOnlySpan<int> bracketPairs,
        int? paragraphLevel, Span<byte> levels)
    {
        var n = originalTypes.Length;
        var matchingPdi = MatchIsolates(originalTypes, out var matchingInitiator);
        var baseLevel = paragraphLevel ?? FirstStrongLevel(originalTypes, 0, n, matchingPdi) ?? 0;
        if (n == 0)
        {
            return baseLevel;
        }

        var types = originalTypes.ToArray();
        ResolveExplicit(types, originalTypes, matchingPdi, baseLevel, levels);

        // X9: embedding controls and boundary neutrals are left out of the remaining rules.
        var kept = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            if (!IsRemovedByX9(originalTypes[i]))
            {
                kept.Add(i);
            }
        }

        // X10: level runs, chained into isolating run sequences across isolates. Their sos and eos compare the explicit
        // levels, before any sequence's implicit levels are resolved.
        var explicitLevels = levels.ToArray();
        foreach (var sequence in IsolatingRunSequences(kept, originalTypes, levels, matchingPdi, matchingInitiator))
        {
            ResolveSequence(sequence, types, originalTypes, bracketTypes, bracketPairs, levels, explicitLevels, matchingPdi, kept, baseLevel);
        }

        // Removed characters take the level of the preceding (or, at the start, the following) character.
        var previous = -1;
        for (var i = 0; i < n; i++)
        {
            if (!IsRemovedByX9(originalTypes[i]))
            {
                previous = levels[i];
            }
            else if (previous >= 0)
            {
                levels[i] = (byte)previous;
            }
        }

        var next = baseLevel;
        for (var i = n - 1; i >= 0; i--)
        {
            if (!IsRemovedByX9(originalTypes[i]))
            {
                next = levels[i];
            }
            else if (!HasKeptBefore(originalTypes, i))
            {
                levels[i] = (byte)next;
            }
        }

        return baseLevel;
    }

    /// <summary>
    /// Line levels (rule L1): segment and paragraph separators, and whitespace and isolate controls before them or at
    /// the end of the line, go back to the paragraph level. Characters removed by X9 within such sequences follow.
    /// </summary>
    public static byte[] LineLevels(ReadOnlySpan<byte> levels, ReadOnlySpan<BidiClass> originalTypes, int paragraphLevel)
    {
        var result = levels.ToArray();
        var trailing = true; // Whitespace up to the end of the line or the next separator.
        for (var i = result.Length - 1; i >= 0; i--)
        {
            var type = originalTypes[i];
            if (type is BidiClass.S or BidiClass.B)
            {
                result[i] = (byte)paragraphLevel;
                trailing = true;
            }
            else if (type is BidiClass.WS or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI || IsRemovedByX9(type))
            {
                if (trailing)
                {
                    result[i] = (byte)paragraphLevel;
                }
            }
            else
            {
                trailing = false;
            }
        }

        return result;
    }

    /// <summary>The visual order (rule L2): for each position left to right, the index of the item shown there.</summary>
    public static int[] VisualOrder(ReadOnlySpan<byte> levels)
    {
        var order = new int[levels.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        if (levels.Length == 0)
        {
            return order;
        }

        byte highest = 0, lowestOdd = byte.MaxValue;
        foreach (var level in levels)
        {
            highest = Math.Max(highest, level);
            if ((level & 1) == 1)
            {
                lowestOdd = Math.Min(lowestOdd, level);
            }
        }

        for (int level = highest; level >= lowestOdd && level > 0; level--)
        {
            for (var i = 0; i < order.Length; i++)
            {
                if (levels[order[i]] < level)
                {
                    continue;
                }

                var end = i;
                while (end + 1 < order.Length && levels[order[end + 1]] >= level)
                {
                    end++;
                }

                Array.Reverse(order, i, end - i + 1);
                i = end;
            }
        }

        return order;
    }

    private static bool HasKeptBefore(ReadOnlySpan<BidiClass> types, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!IsRemovedByX9(types[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>BD9: the matching PDI of each isolate initiator (-1 if none), and the initiator of each matched PDI.</summary>
    private static int[] MatchIsolates(ReadOnlySpan<BidiClass> types, out int[] matchingInitiator)
    {
        var matching = new int[types.Length];
        matchingInitiator = new int[types.Length];
        Array.Fill(matching, -1);
        Array.Fill(matchingInitiator, -1);
        var open = new Stack<int>();
        for (var i = 0; i < types.Length; i++)
        {
            switch (types[i])
            {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    open.Push(i);
                    break;
                case BidiClass.PDI when open.Count > 0:
                    var initiator = open.Pop();
                    matching[initiator] = i;
                    matchingInitiator[i] = initiator;
                    break;
                case BidiClass.B:
                    open.Clear();
                    break;
            }
        }

        return matching;
    }

    /// <summary>
    /// P2 and P3: the level of the first strong character in [start, end), skipping isolates, or <c>null</c> if none.
    /// </summary>
    private static int? FirstStrongLevel(ReadOnlySpan<BidiClass> types, int start, int end, int[] matchingPdi)
    {
        for (var i = start; i < end; i++)
        {
            switch (types[i])
            {
                case BidiClass.L:
                    return 0;
                case BidiClass.R or BidiClass.AL:
                    return 1;
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI:
                    if (matchingPdi[i] < 0)
                    {
                        return null; // The isolate runs to the end of the paragraph.
                    }

                    i = matchingPdi[i];
                    break;
                case BidiClass.B:
                    return null;
            }
        }

        return null;
    }

    /// <summary>Rules X1-X8: explicit embedding levels and directional overrides.</summary>
    private static void ResolveExplicit(BidiClass[] types, ReadOnlySpan<BidiClass> originalTypes, int[] matchingPdi, int baseLevel, Span<byte> levels)
    {
        // Each entry: level, override (ON for neutral, else L or R) and whether it is an isolate.
        var stackLevel = new byte[MaxDepth + 2];
        var stackOverride = new BidiClass[MaxDepth + 2];
        var stackIsolate = new bool[MaxDepth + 2];
        var depth = 0;
        stackLevel[0] = (byte)baseLevel;
        stackOverride[0] = BidiClass.ON;
        stackIsolate[0] = false;
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;

        for (var i = 0; i < types.Length; i++)
        {
            var type = originalTypes[i];
            switch (type)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    var rtl = type is BidiClass.RLE or BidiClass.RLO;
                    var level = NextLevel(stackLevel[depth], rtl);
                    levels[i] = stackLevel[depth];
                    if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        depth++;
                        stackLevel[depth] = (byte)level;
                        stackOverride[depth] = type == BidiClass.RLO ? BidiClass.R : type == BidiClass.LRO ? BidiClass.L : BidiClass.ON;
                        stackIsolate[depth] = false;
                    }
                    else if (overflowIsolates == 0)
                    {
                        overflowEmbeddings++;
                    }

                    break;
                }

                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    levels[i] = stackLevel[depth];
                    if (stackOverride[depth] != BidiClass.ON)
                    {
                        types[i] = stackOverride[depth];
                    }

                    var rtl = type == BidiClass.RLI
                        || (type == BidiClass.FSI && FirstStrongLevel(originalTypes, i + 1, matchingPdi[i] >= 0 ? matchingPdi[i] : types.Length, matchingPdi) == 1);
                    var level = NextLevel(stackLevel[depth], rtl);
                    if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                    {
                        validIsolates++;
                        depth++;
                        stackLevel[depth] = (byte)level;
                        stackOverride[depth] = BidiClass.ON;
                        stackIsolate[depth] = true;
                    }
                    else
                    {
                        overflowIsolates++;
                    }

                    break;
                }

                case BidiClass.PDI:
                    if (overflowIsolates > 0)
                    {
                        overflowIsolates--;
                    }
                    else if (validIsolates > 0)
                    {
                        overflowEmbeddings = 0;
                        while (!stackIsolate[depth])
                        {
                            depth--;
                        }

                        depth--;
                        validIsolates--;
                    }

                    levels[i] = stackLevel[depth];
                    if (stackOverride[depth] != BidiClass.ON)
                    {
                        types[i] = stackOverride[depth];
                    }

                    break;
                case BidiClass.PDF:
                    levels[i] = stackLevel[depth];
                    if (overflowIsolates > 0)
                    {
                    }
                    else if (overflowEmbeddings > 0)
                    {
                        overflowEmbeddings--;
                    }
                    else if (!stackIsolate[depth] && depth > 0)
                    {
                        depth--;
                    }

                    break;
                case BidiClass.B:
                    levels[i] = (byte)baseLevel;
                    break;
                case BidiClass.BN:
                    levels[i] = stackLevel[depth];
                    break;
                default:
                    levels[i] = stackLevel[depth];
                    if (stackOverride[depth] != BidiClass.ON)
                    {
                        types[i] = stackOverride[depth];
                    }

                    break;
            }
        }
    }

    /// <summary>The least odd (right-to-left) or even (left-to-right) level greater than <paramref name="level"/>.</summary>
    private static int NextLevel(int level, bool rtl) => rtl ? (level + 1) | 1 : (level + 2) & ~1;

    /// <summary>BD13: isolating run sequences, as lists of indices of characters not removed by X9.</summary>
    private static List<List<int>> IsolatingRunSequences(List<int> kept, ReadOnlySpan<BidiClass> types, Span<byte> levels, int[] matchingPdi, int[] matchingInitiator)
    {
        // Level runs (BD7) over the kept characters.
        var runs = new List<List<int>>();
        var runOf = new Dictionary<int, int>();
        for (var k = 0; k < kept.Count; k++)
        {
            var i = kept[k];
            if (k == 0 || levels[i] != levels[kept[k - 1]])
            {
                runs.Add([]);
            }

            runs[^1].Add(i);
            runOf[i] = runs.Count - 1;
        }

        var sequences = new List<List<int>>();
        foreach (var run in runs)
        {
            // A run starting with a PDI that matches an initiator continues that initiator's sequence.
            var first = run[0];
            if (types[first] == BidiClass.PDI && matchingInitiator[first] >= 0)
            {
                continue;
            }

            var sequence = new List<int>();
            var current = run;
            while (true)
            {
                sequence.AddRange(current);
                var last = current[^1];
                if (types[last] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI && matchingPdi[last] >= 0
                    && runOf.TryGetValue(matchingPdi[last], out var nextRun))
                {
                    current = runs[nextRun];
                    continue;
                }

                break;
            }

            sequences.Add(sequence);
        }

        return sequences;
    }

    /// <summary>Rules W1-W7, N0-N2 and I1-I2 on one isolating run sequence.</summary>
    private static void ResolveSequence(
        List<int> sequence, BidiClass[] types, ReadOnlySpan<BidiClass> originalTypes, ReadOnlySpan<BracketType> bracketTypes,
        ReadOnlySpan<int> bracketPairs, Span<byte> levels, byte[] explicitLevels, int[] matchingPdi, List<int> kept, int baseLevel)
    {
        var level = explicitLevels[sequence[0]];
        var count = sequence.Count;

        // sos and eos (X10): the higher of this level and the adjacent kept character's (or the paragraph's).
        var firstKept = kept.BinarySearch(sequence[0]);
        var before = firstKept > 0 ? explicitLevels[kept[firstKept - 1]] : baseLevel;
        var lastIndex = sequence[^1];
        var lastIsOpenIsolate = originalTypes[lastIndex] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI && matchingPdi[lastIndex] < 0;
        var lastKept = kept.BinarySearch(lastIndex);
        var after = !lastIsOpenIsolate && lastKept + 1 < kept.Count ? explicitLevels[kept[lastKept + 1]] : baseLevel;
        var sos = (Math.Max(before, level) & 1) == 1 ? BidiClass.R : BidiClass.L;
        var eos = (Math.Max(after, level) & 1) == 1 ? BidiClass.R : BidiClass.L;

        var t = new BidiClass[count];
        for (var k = 0; k < count; k++)
        {
            t[k] = types[sequence[k]];
        }

        // W1: non-spacing marks take the type of the previous character (ON after isolate controls, sos at the start).
        for (var k = 0; k < count; k++)
        {
            if (t[k] == BidiClass.NSM)
            {
                t[k] = k == 0 ? sos
                    : originalTypes[sequence[k - 1]] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON
                    : t[k - 1];
            }
        }

        // W2: European numbers after Arabic letters become Arabic numbers. W3: Arabic letters become R.
        var lastStrong = sos;
        for (var k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.L or BidiClass.R or BidiClass.AL)
            {
                lastStrong = t[k];
            }
            else if (t[k] == BidiClass.EN && lastStrong == BidiClass.AL)
            {
                t[k] = BidiClass.AN;
            }
        }

        for (var k = 0; k < count; k++)
        {
            if (t[k] == BidiClass.AL)
            {
                t[k] = BidiClass.R;
            }
        }

        // W4: a single separator between two numbers of the same type joins them.
        for (var k = 1; k < count - 1; k++)
        {
            if (t[k] == BidiClass.ES && t[k - 1] == BidiClass.EN && t[k + 1] == BidiClass.EN)
            {
                t[k] = BidiClass.EN;
            }
            else if (t[k] == BidiClass.CS && t[k - 1] == t[k + 1] && t[k - 1] is BidiClass.EN or BidiClass.AN)
            {
                t[k] = t[k - 1];
            }
        }

        // W5: European terminators next to European numbers become European numbers.
        for (var k = 0; k < count; k++)
        {
            if (t[k] != BidiClass.ET)
            {
                continue;
            }

            var end = k;
            while (end < count && t[end] == BidiClass.ET)
            {
                end++;
            }

            if ((k > 0 && t[k - 1] == BidiClass.EN) || (end < count && t[end] == BidiClass.EN))
            {
                Array.Fill(t, BidiClass.EN, k, end - k);
            }

            k = end - 1;
        }

        // W6: remaining separators and terminators become neutral.
        for (var k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                t[k] = BidiClass.ON;
            }
        }

        // W7: European numbers after L (or an L sos) become L.
        lastStrong = sos;
        for (var k = 0; k < count; k++)
        {
            if (t[k] is BidiClass.L or BidiClass.R)
            {
                lastStrong = t[k];
            }
            else if (t[k] == BidiClass.EN && lastStrong == BidiClass.L)
            {
                t[k] = BidiClass.L;
            }
        }

        var embedding = (level & 1) == 1 ? BidiClass.R : BidiClass.L;
        ResolveBrackets(sequence, t, originalTypes, bracketTypes, bracketPairs, sos, embedding);

        // N1 and N2: runs of neutrals take the direction of the strong text around them if both sides agree (numbers
        // count as R), else the embedding direction.
        for (var k = 0; k < count; k++)
        {
            if (!IsNeutralOrIsolate(t[k]))
            {
                continue;
            }

            var end = k;
            while (end < count && IsNeutralOrIsolate(t[end]))
            {
                end++;
            }

            var leading = k == 0 ? sos : StrongForNeutrals(t[k - 1]);
            var trailing = end == count ? eos : StrongForNeutrals(t[end]);
            var resolved = leading == trailing ? leading : embedding;
            Array.Fill(t, resolved, k, end - k);
            k = end - 1;
        }

        // I1 and I2: implicit levels.
        for (var k = 0; k < count; k++)
        {
            var i = sequence[k];
            if ((level & 1) == 0)
            {
                levels[i] = (byte)(level + (t[k] == BidiClass.R ? 1 : t[k] is BidiClass.AN or BidiClass.EN ? 2 : 0));
            }
            else
            {
                levels[i] = (byte)(level + (t[k] is BidiClass.L or BidiClass.EN or BidiClass.AN ? 1 : 0));
            }
        }
    }

    private static bool IsNeutralOrIsolate(BidiClass c) =>
        c is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

    /// <summary>The direction a resolved type gives neighboring neutrals: numbers count as R.</summary>
    private static BidiClass StrongForNeutrals(BidiClass c) => c is BidiClass.EN or BidiClass.AN ? BidiClass.R : c;

    /// <summary>
    /// N0: paired brackets take the embedding direction if the text inside has it, else the opposite direction if the
    /// text inside and the context before have that; non-spacing marks after a changed bracket follow it.
    /// </summary>
    private static void ResolveBrackets(
        List<int> sequence, BidiClass[] t, ReadOnlySpan<BidiClass> originalTypes, ReadOnlySpan<BracketType> bracketTypes,
        ReadOnlySpan<int> bracketPairs, BidiClass sos, BidiClass embedding)
    {
        // BD16: identify the pairs with a stack of at most 63 openings.
        var pairs = new List<(int Open, int Close)>();
        var stack = new List<(int Pair, int Position)>();
        for (var k = 0; k < sequence.Count; k++)
        {
            var i = sequence[k];
            if (t[k] != BidiClass.ON || bracketTypes[i] == BracketType.None)
            {
                continue;
            }

            if (bracketTypes[i] == BracketType.Open)
            {
                if (stack.Count == 63)
                {
                    break;
                }

                stack.Add((bracketPairs[i], k));
            }
            else
            {
                for (var s = stack.Count - 1; s >= 0; s--)
                {
                    if (stack[s].Pair == bracketPairs[i])
                    {
                        pairs.Add((stack[s].Position, k));
                        stack.RemoveRange(s, stack.Count - s);
                        break;
                    }
                }
            }
        }

        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));
        var opposite = embedding == BidiClass.L ? BidiClass.R : BidiClass.L;
        foreach (var (open, close) in pairs)
        {
            var foundEmbedding = false;
            var foundOpposite = false;
            for (var k = open + 1; k < close; k++)
            {
                var strong = StrongForBrackets(t[k]);
                if (strong == embedding)
                {
                    foundEmbedding = true;
                    break;
                }

                if (strong == opposite)
                {
                    foundOpposite = true;
                }
            }

            BidiClass? resolved = null;
            if (foundEmbedding)
            {
                resolved = embedding;
            }
            else if (foundOpposite)
            {
                var context = sos;
                for (var k = open - 1; k >= 0; k--)
                {
                    if (StrongForBrackets(t[k]) is { } strong)
                    {
                        context = strong;
                        break;
                    }
                }

                resolved = context == opposite ? opposite : embedding;
            }

            if (resolved is { } direction)
            {
                foreach (var k in (ReadOnlySpan<int>)[open, close])
                {
                    t[k] = direction;
                    for (var m = k + 1; m < sequence.Count && originalTypes[sequence[m]] == BidiClass.NSM; m++)
                    {
                        t[m] = direction;
                    }
                }
            }
        }
    }

    private static BidiClass? StrongForBrackets(BidiClass c) => c switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.EN or BidiClass.AN => BidiClass.R,
        _ => null,
    };
}

/// <summary>Resolved bidi levels of a text: per UTF-16 code unit, with each paragraph's range and level.</summary>
internal sealed class BidiText(byte[] levels, BidiClass[] types, IReadOnlyList<(int Start, int End, int Level)> paragraphs)
{
    /// <summary>The resolved level of each code unit.</summary>
    public byte[] Levels { get; } = levels;

    /// <summary>The original bidi class of each code unit.</summary>
    public BidiClass[] Types { get; } = types;

    /// <summary>The paragraphs (rule P1), with their embedding levels.</summary>
    public IReadOnlyList<(int Start, int End, int Level)> Paragraphs { get; } = paragraphs;

    /// <summary>Whether every level is 0: plain left-to-right text that needs no reordering.</summary>
    public bool IsLeftToRight => Array.TrueForAll(Levels, l => l == 0);

    /// <summary>The embedding level of the paragraph containing a code unit.</summary>
    public int ParagraphLevelAt(int index)
    {
        foreach (var (start, end, level) in Paragraphs)
        {
            if (index < end)
            {
                return level;
            }
        }

        return Paragraphs.Count > 0 ? Paragraphs[^1].Level : 0;
    }

    /// <summary>The levels of a line [start, end) after rule L1.</summary>
    public byte[] LineLevels(int start, int end) =>
        BidiAlgorithm.LineLevels(Levels.AsSpan(start, end - start), Types.AsSpan(start, end - start), ParagraphLevelAt(start));
}
