namespace RaiCore.Pdf.Sheets;

/// <summary>
/// What a column needs, in points (cell padding and borders included): <see cref="Max"/> to show every cell on one line,
/// <see cref="Min"/> to break no word, and <see cref="Floor"/>, the least it may get (some characters of text, and its
/// widest number or date whole). <see cref="Weight"/> is how much text its cells hold on average (their mean width on
/// one line): columns that wrap share the width in proportion to it, so their cells wrap to similar numbers of lines.
/// </summary>
internal readonly record struct ColumnNeeds(double Min, double Max, double Floor, double Weight);

/// <summary>A group of consecutive columns that fit the page width together, with their widths in points.</summary>
internal sealed record ColumnBand(int Start, double[] Widths)
{
    /// <summary>The number of columns.</summary>
    public int Count => Widths.Length;
}

/// <summary>
/// Chooses column widths within the page width, wrapping as little as it can:
/// <list type="number">
/// <item>Every column at its <see cref="ColumnNeeds.Max"/> width if they all fit.</item>
/// <item>Else, if every column fits without breaking a word (at its need: <see cref="ColumnNeeds.Min"/>, and at least
/// its floor), the columns whose widest lines are within an equal share of the width keep them, and the others get
/// <c>clamp(level * weight, need, max)</c> for the level that fills the rest: they share it in proportion to their
/// weights.</item>
/// <item>Else words wider than their column are broken: the same between floor and need.</item>
/// <item>Columns whose floors do not fit together are split into bands, as few as the floors allow, with the columns
/// spread over them so that each band has about the same share of the needs; each band is sized on its own as above.</item>
/// </list>
/// </summary>
internal static class ColumnFitter
{
    /// <summary>Splits the columns into bands that fit <paramref name="available"/> points and sizes them.</summary>
    public static List<ColumnBand> Fit(ColumnNeeds[] columns, double available)
    {
        var floors = Split(columns, available, double.PositiveInfinity);
        if (floors.Count > 1)
        {
            // As many bands, with the needs spread evenly; if that takes more bands, the tightest packing it is.
            var target = columns.Sum(c => Need(c, available)) / floors.Count;
            var balanced = Split(columns, available, target);
            if (balanced.Count == floors.Count)
            {
                floors = balanced;
            }
        }

        return [.. floors.Select(b => new ColumnBand(b.Start, Distribute(columns.AsSpan(b.Start, b.Count).ToArray(), available)))];
    }

    /// <summary>
    /// Splits the columns from the left into bands whose floors fit <paramref name="available"/> points, each ending once
    /// its needs reach <paramref name="target"/> points (a column goes into the band if less than half of its need is
    /// over the target).
    /// </summary>
    private static List<(int Start, int Count)> Split(ColumnNeeds[] columns, double available, double target)
    {
        var bands = new List<(int, int)>();
        var start = 0;
        while (start < columns.Length)
        {
            var end = start + 1;
            var floors = Math.Min(columns[start].Floor, available);
            var needs = Need(columns[start], available);
            while (end < columns.Length && floors + columns[end].Floor <= available && needs + (Need(columns[end], available) / 2) <= target)
            {
                floors += columns[end].Floor;
                needs += Need(columns[end], available);
                end++;
            }

            bands.Add((start, end - start));
            start = end;
        }

        return bands;
    }

    /// <summary>What a column needs to break no word, within the page width.</summary>
    private static double Need(ColumnNeeds column, double available)
    {
        var max = Math.Min(column.Max, available);
        return Math.Clamp(column.Min, Math.Min(column.Floor, max), max);
    }

    /// <summary>The widths of columns whose floors fit <paramref name="available"/> points together (or of one column).</summary>
    public static double[] Distribute(ColumnNeeds[] columns, double available)
    {
        var max = columns.Select(c => Math.Min(c.Max, available)).ToArray();
        var floor = columns.Select((c, i) => Math.Min(c.Floor, max[i])).ToArray();
        var need = columns.Select(c => Need(c, available)).ToArray();
        var weight = columns.Select(c => Math.Max(c.Weight, 1)).ToArray();
        if (max.Sum() <= available)
        {
            return max;
        }

        return need.Sum() <= available ? Fill(need, max, weight, available) : Fill(floor, need, weight, available);
    }

    /// <summary>
    /// Widths between <paramref name="low"/> and <paramref name="high"/> that add up to no more than
    /// <paramref name="available"/> (the lows must fit). Columns whose highs are within an equal share of the width keep
    /// them (repeatedly, as the share of the others grows), as long as the others' lows still fit; the others get
    /// <c>clamp(level * weight, low, high)</c> with the highest level that fits.
    /// </summary>
    public static double[] Fill(double[] low, double[] high, double[] weight, double available)
    {
        var whole = new bool[low.Length];
        var remaining = available;
        while (true)
        {
            var others = Enumerable.Range(0, low.Length).Where(i => !whole[i]).ToArray();
            if (others.Length == 0)
            {
                break;
            }

            var share = remaining / others.Length;
            var fitting = others.Where(i => high[i] <= share).ToArray();
            var left = remaining - fitting.Sum(i => high[i]);
            if (fitting.Length == 0 || others.Except(fitting).Sum(i => low[i]) > left)
            {
                break;
            }

            foreach (var i in fitting)
            {
                whole[i] = true;
            }

            remaining = left;
        }

        double below = 0, above = high.Select((h, i) => h / weight[i]).Max();
        for (var i = 0; i < 64; i++)
        {
            var level = (below + above) / 2;
            if (Widths(low, high, weight, whole, level).Sum() <= available)
            {
                below = level;
            }
            else
            {
                above = level;
            }
        }

        return Widths(low, high, weight, whole, below);
    }

    private static double[] Widths(double[] low, double[] high, double[] weight, bool[] whole, double level) =>
        [.. low.Select((l, i) => whole[i] ? high[i] : Math.Clamp(level * weight[i], l, Math.Max(l, high[i])))];
}
