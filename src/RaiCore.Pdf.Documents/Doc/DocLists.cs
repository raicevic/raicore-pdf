using System.Buffers.Binary;
using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Doc;

/// <summary>
/// Lists ([MS-DOC] 2.9.147 PlfLst with its LVLs, 2.9.144 PlfLfo with its overrides): the levels' number formats,
/// start values, label texts and properties, and counters per list as Word counts them.
/// </summary>
internal sealed class DocLists
{
    /// <summary>The most lists and overrides read; more is damage.</summary>
    private const int MaxLists = 32767;

    private readonly List<ListDefinition> _lists = [];
    private readonly List<ListOverride> _overrides = [];
    private readonly Dictionary<int, int?[]> _counters = [];
    private readonly HashSet<int> _started = [];
    private readonly Dictionary<int, int> _lastLevels = [];

    /// <summary>A level of a list ([MS-DOC] 2.9.150 LVL).</summary>
    internal sealed class Level
    {
        public int Start;
        public NumberFormat Format;
        public bool Legal;
        public bool NoRestart;
        public int RestartLimit;
        public LabelSuffix Suffix;
        public byte[] Papx = [];
        public byte[] Chpx = [];
        public string Text = string.Empty;
    }

    private sealed class ListDefinition(int id, Level[] levels)
    {
        public int Id { get; } = id;

        public Level[] Levels { get; } = levels;
    }

    private sealed class ListOverride(int listId)
    {
        public int ListId { get; } = listId;

        public int?[] Starts { get; } = new int?[9];

        public Level?[] Levels { get; } = new Level?[9];
    }

    private DocLists(DocFile file)
    {
        var (fcLst, lcbLst) = file.FcLcb(73);
        if (lcbLst >= 2 && fcLst < file.Table.Length)
        {
            ReadLists(file.Table.AsSpan((int)fcLst));
        }

        var (fcLfo, lcbLfo) = file.FcLcb(74);
        if (lcbLfo >= 4 && fcLfo < file.Table.Length)
        {
            ReadOverrides(file.Table.AsSpan((int)fcLfo, (int)Math.Min(lcbLfo, (uint)(file.Table.Length - (int)fcLfo))));
        }
    }

    public static DocLists Read(DocFile file) => new(file);

    /// <summary>The definition of a level of a list override (1-based <paramref name="ilfo"/>), or <c>null</c>.</summary>
    public Level? LevelOf(int ilfo, int ilvl)
    {
        if (ilfo < 1 || ilfo > _overrides.Count || ilvl is < 0 or > 8)
        {
            return null;
        }

        var listOverride = _overrides[ilfo - 1];
        return listOverride.Levels[ilvl] ?? (ListById(listOverride.ListId) is { } list ? LevelOf(list, ilvl) : null);
    }

    /// <summary>
    /// Counts a list paragraph and returns its label text (the level's text with the counters of the levels it refers
    /// to), or <c>null</c> when the list or level is not defined.
    /// </summary>
    public string? Count(int ilfo, int ilvl)
    {
        if (LevelOf(ilfo, ilvl) is not { } definition)
        {
            return null;
        }

        var listOverride = _overrides[ilfo - 1];
        var list = ListById(listOverride.ListId);
        if (!_counters.TryGetValue(listOverride.ListId, out var counters))
        {
            counters = new int?[9];
            _counters[listOverride.ListId] = counters;
        }

        if (_started.Add(ilfo))
        {
            for (var l = 0; l < 9; l++)
            {
                if (listOverride.Starts[l] is { } start)
                {
                    counters[l] = start - 1;
                }
            }
        }

        // Levels skipped on the way down from the list's last paragraph count as their start value from now on.
        for (var skipped = _lastLevels.GetValueOrDefault(listOverride.ListId, -1) + 1; skipped < ilvl; skipped++)
        {
            counters[skipped] ??= Find(listOverride, list, skipped)?.Start ?? 1;
        }

        _lastLevels[listOverride.ListId] = ilvl;
        counters[ilvl] = counters[ilvl] is { } current ? current + 1 : definition.Start;

        // Deeper levels restart; one with fNoRestart only when a level above its ilvlRestartLim is counted.
        for (var deeper = ilvl + 1; deeper < 9; deeper++)
        {
            if (Find(listOverride, list, deeper) is not { NoRestart: true } level || ilvl < level.RestartLimit)
            {
                counters[deeper] = null;
            }
        }

        var text = new StringBuilder();
        foreach (var c in definition.Text)
        {
            if (c < 9)
            {
                var referenced = Find(listOverride, list, c);
                var value = counters[c] ?? referenced?.Start ?? 1;
                var format = referenced?.Format ?? NumberFormat.Decimal;
                if (definition.Legal && c != ilvl && format is not (NumberFormat.None or NumberFormat.Bullet))
                {
                    format = NumberFormat.Decimal;
                }

                text.Append(Numbering.Format(value, format));
            }
            else if (c >= ' ')
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private static Level? Find(ListOverride listOverride, ListDefinition? list, int level) =>
        listOverride.Levels[level] ?? (list is null ? null : LevelOf(list, level));

    private static Level? LevelOf(ListDefinition list, int level) =>
        level < list.Levels.Length ? list.Levels[level] : list.Levels.Length == 1 ? list.Levels[0] : null;

    private ListDefinition? ListById(int id) => _lists.Find(l => l.Id == id);

    /// <summary>The number format of an <c>nfc</c> ([MS-OSHARED] 2.2.1.3 MSONFC).</summary>
    private static NumberFormat FormatOf(int nfc) => nfc switch
    {
        1 => NumberFormat.UpperRoman,
        2 => NumberFormat.LowerRoman,
        3 => NumberFormat.UpperLetter,
        4 => NumberFormat.LowerLetter,
        5 => NumberFormat.Ordinal,
        22 => NumberFormat.DecimalZero,
        23 => NumberFormat.Bullet,
        255 => NumberFormat.None,
        _ => NumberFormat.Decimal,
    };

    /// <summary>The lists: <c>cLst</c> LSTFs, then the LVLs of each list in order (9, or 1 for a simple list).</summary>
    private void ReadLists(ReadOnlySpan<byte> data)
    {
        var count = Math.Min((int)BinaryPrimitives.ReadInt16LittleEndian(data), MaxLists);
        if (count <= 0 || 2 + (28L * count) > data.Length)
        {
            return;
        }

        var position = 2 + (28 * count);
        for (var i = 0; i < count; i++)
        {
            var lstf = data.Slice(2 + (28 * i), 28);
            var simple = (lstf[26] & 1) != 0;
            var levels = new Level[simple ? 1 : 9];
            for (var l = 0; l < levels.Length; l++)
            {
                levels[l] = ReadLevel(data, ref position) ?? new Level();
            }

            _lists.Add(new ListDefinition(BinaryPrimitives.ReadInt32LittleEndian(lstf), levels));
        }
    }

    /// <summary>A level: LVLF (28 bytes), its paragraph and character modifiers, and its text (Xst); <c>null</c> when truncated.</summary>
    private static Level? ReadLevel(ReadOnlySpan<byte> data, ref int position)
    {
        if (position < 0 || position + 28 > data.Length)
        {
            position = -1;
            return null;
        }

        var lvlf = data.Slice(position, 28);
        var level = new Level
        {
            Start = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(lvlf), -1_000_000, 1_000_000),
            Format = FormatOf(lvlf[4]),
            Legal = (lvlf[5] & 0x04) != 0,
            NoRestart = (lvlf[5] & 0x08) != 0,
            RestartLimit = lvlf[26],
            Suffix = lvlf[15] switch
            {
                1 => LabelSuffix.Space,
                2 => LabelSuffix.Nothing,
                _ => LabelSuffix.Tab,
            },
        };
        var chpxSize = lvlf[24];
        var papxSize = lvlf[25];
        position += 28;
        if (position + papxSize + chpxSize + 2 > data.Length)
        {
            position = -1;
            return level;
        }

        level.Papx = data.Slice(position, papxSize).ToArray();
        position += papxSize;
        level.Chpx = data.Slice(position, chpxSize).ToArray();
        position += chpxSize;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
        position += 2;
        if (2 * length > data.Length - position)
        {
            position = -1;
            return level;
        }

        level.Text = Encoding.Unicode.GetString(data.Slice(position, 2 * length));
        position += 2 * length;
        return level;
    }

    /// <summary>The overrides: <c>lfoMac</c> LFOs (16 bytes), then the LFOData of each with its level overrides.</summary>
    private void ReadOverrides(ReadOnlySpan<byte> data)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (count <= 0 || count > MaxLists || 4 + (16L * count) > data.Length)
        {
            return;
        }

        var position = 4 + (16 * count);
        for (var i = 0; i < count; i++)
        {
            var lfo = data.Slice(4 + (16 * i), 16);
            var listOverride = new ListOverride(BinaryPrimitives.ReadInt32LittleEndian(lfo));
            _overrides.Add(listOverride);
            var levels = lfo[12];
            if (position < 0)
            {
                continue;
            }

            // LFOData (one for each LFO): cp, then the LFOLVLs, each followed by a LVL when it has formatting.
            position += 4;
            for (var l = 0; l < levels && position >= 0 && position + 8 <= data.Length; l++)
            {
                var start = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
                var flags = BinaryPrimitives.ReadInt32LittleEndian(data[(position + 4)..]);
                position += 8;
                var index = flags & 0x0F;
                if (index > 8)
                {
                    continue;
                }

                if ((flags & 0x10) != 0)
                {
                    listOverride.Starts[index] = Math.Clamp(start, -1_000_000, 1_000_000);
                }

                if ((flags & 0x20) != 0)
                {
                    listOverride.Levels[index] = ReadLevel(data, ref position);
                }
            }
        }
    }
}
