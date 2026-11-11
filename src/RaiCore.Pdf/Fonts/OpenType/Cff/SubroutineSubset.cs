namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>
/// The kept glyphs' charstrings with only the subroutines they call (transitively), renumbered: each call's
/// subroutine number literal is rewritten for the new numbering and the new bias (107, 1131 or 32768, by count).
/// </summary>
internal sealed class SubroutineSubset
{
    private SubroutineSubset(byte[][] charStrings, byte[][] global, Dictionary<int, byte[][]> local)
    {
        CharStrings = charStrings;
        Global = global;
        Local = local;
    }

    /// <summary>The kept glyphs' charstrings, in glyph order.</summary>
    public byte[][] CharStrings { get; }

    public byte[][] Global { get; }

    /// <summary>Local subroutines by (old) Font DICT index.</summary>
    public Dictionary<int, byte[][]> Local { get; }

    /// <summary>Returns null when the calls cannot be renumbered in place (then the glyphs are desubroutinized).</summary>
    public static SubroutineSubset? TryCreate(CffFont cff, CharStringInterpreter interpreter, int[] glyphs)
    {
        var collector = new Collector();
        foreach (var gid in glyphs)
        {
            collector.FontDict = cff.FontDictIndex(gid);
            interpreter.Run(gid, collector);
        }

        if (collector.NonLiteralCall)
        {
            return null;
        }

        // New numbers keep the original order.
        var globalMap = Renumber(collector.Global);
        var localMaps = collector.Local.ToDictionary(kv => kv.Key, kv => Renumber(kv.Value));
        var globalBias = CffIndex.Bias(globalMap.Count);

        // The new operand of every call site; a site that would need two different numbers makes this fail.
        var patches = new Dictionary<CharStringKey, SortedDictionary<int, (int End, int Value)>>();
        foreach (var call in collector.Calls)
        {
            int value;
            if (call.Global)
            {
                value = globalMap[call.Index] - globalBias;
            }
            else
            {
                var map = localMaps[call.FontDict];
                value = map[call.Index] - CffIndex.Bias(map.Count);
            }

            if (!patches.TryGetValue(call.Caller, out var sites))
            {
                patches[call.Caller] = sites = [];
            }

            if (sites.TryGetValue(call.Start, out var existing) && existing.Value != value)
            {
                return null;
            }

            sites[call.Start] = (call.End, value);
        }

        byte[] Rewrite(CharStringKey key, ReadOnlySpan<byte> code) =>
            patches.TryGetValue(key, out var sites) ? Patch(code, sites) : code.ToArray();

        var charStrings = glyphs
            .Select(g => Rewrite(new CharStringKey(CharStringKey.Glyph, cff.FontDictIndex(g), g), cff.CharStrings[g]))
            .ToArray();
        var global = globalMap.Keys
            .Select(i => Rewrite(new CharStringKey(CharStringKey.Global, -1, i), cff.GlobalSubrs[i]))
            .ToArray();
        var local = localMaps.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Keys
                .Select(i => Rewrite(new CharStringKey(CharStringKey.Local, kv.Key, i), cff.FontDicts[kv.Key].LocalSubrs[i]))
                .ToArray());
        return new SubroutineSubset(charStrings, global, local);
    }

    private static SortedDictionary<int, int> Renumber(SortedSet<int> used)
    {
        var map = new SortedDictionary<int, int>();
        foreach (var index in used)
        {
            map[index] = map.Count;
        }

        return map;
    }

    /// <summary>Replaces the number literals at the given byte ranges.</summary>
    private static byte[] Patch(ReadOnlySpan<byte> code, SortedDictionary<int, (int End, int Value)> sites)
    {
        using var output = new MemoryStream(code.Length + 8);
        var pos = 0;
        foreach (var (start, (end, value)) in sites)
        {
            output.Write(code[pos..start]);
            CharStringFlattener.WriteInteger(output, value);
            pos = end;
        }

        output.Write(code[pos..]);
        return output.ToArray();
    }

    private readonly record struct Call(CharStringKey Caller, int Start, int End, bool Global, int FontDict, int Index);

    private sealed class Collector : ICharStringVisitor
    {
        public int FontDict { get; set; }

        public SortedSet<int> Global { get; } = [];

        public Dictionary<int, SortedSet<int>> Local { get; } = [];

        public List<Call> Calls { get; } = [];

        public bool NonLiteralCall { get; private set; }

        public void Operator(int op, ReadOnlySpan<int> args, ReadOnlySpan<byte> hintMask)
        {
        }

        public void SubroutineCall(bool global, int index, CharStringKey caller, int operandStart, int operandEnd)
        {
            if (global)
            {
                Global.Add(index);
            }
            else
            {
                if (!Local.TryGetValue(FontDict, out var set))
                {
                    Local[FontDict] = set = [];
                }

                set.Add(index);
            }

            if (operandStart < 0)
            {
                NonLiteralCall = true;
            }
            else
            {
                Calls.Add(new Call(caller, operandStart, operandEnd, global, FontDict, index));
            }
        }
    }
}
