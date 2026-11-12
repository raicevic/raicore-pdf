namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>One item's main-axis inputs to the flex algorithm (border-box sizes; margins separately).</summary>
/// <param name="BaseSize">The flex base size.</param>
/// <param name="Min">The minimum main size.</param>
/// <param name="Max">The maximum main size (at least <paramref name="Min"/>).</param>
/// <param name="Grow">The flex grow factor.</param>
/// <param name="Shrink">The flex shrink factor.</param>
/// <param name="Margins">The sum of the item's non-auto main-axis margins.</param>
internal readonly record struct FlexMainInput(double BaseSize, double Min, double Max, double Grow, double Shrink, double Margins)
{
    /// <summary>The base size clamped by the min and max sizes (CSS: hypothetical main size).</summary>
    public double Hypothetical => Math.Clamp(BaseSize, Min, Max);
}

/// <summary>The main-axis algorithms of CSS Flexbox Level 1, independent of pagination and drawing.</summary>
internal static class FlexAlgorithm
{
    private const double Epsilon = 1e-9;

    /// <summary>Splits items into lines (CSS Flexbox section 9.3); every line has at least one item.</summary>
    public static List<(int Start, int End)> BreakLines(IReadOnlyList<FlexMainInput> items, double available, double gap, bool wrap)
    {
        var lines = new List<(int Start, int End)>();
        if (items.Count == 0)
        {
            return lines;
        }

        if (!wrap)
        {
            lines.Add((0, items.Count));
            return lines;
        }

        var start = 0;
        var used = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            var outer = items[i].Hypothetical + items[i].Margins;
            var needed = i == start ? outer : used + gap + outer;
            if (i > start && needed > available + 1e-6)
            {
                lines.Add((start, i));
                start = i;
                used = outer;
            }
            else
            {
                used = needed;
            }
        }

        lines.Add((start, items.Count));
        return lines;
    }

    /// <summary>
    /// Resolves the main sizes of one line's items (CSS Flexbox section 9.7): free space is shared by the grow factors,
    /// or overflow by the shrink factors weighted by the base sizes, and items that hit their min or max size are frozen
    /// there while the rest is redistributed.
    /// </summary>
    /// <param name="items">The line's items.</param>
    /// <param name="available">The line's main size minus the gaps between its items.</param>
    public static double[] ResolveFlexibleLengths(IReadOnlyList<FlexMainInput> items, double available)
    {
        var count = items.Count;
        var target = new double[count];
        var frozen = new bool[count];
        var growing = items.Sum(i => i.Hypothetical + i.Margins) < available;

        // Inflexible items are frozen at their hypothetical size.
        for (var i = 0; i < count; i++)
        {
            var item = items[i];
            var factor = growing ? item.Grow : item.Shrink;
            if (factor <= 0 || (growing && item.BaseSize > item.Hypothetical) || (!growing && item.BaseSize < item.Hypothetical))
            {
                frozen[i] = true;
                target[i] = item.Hypothetical;
            }
        }

        double FreeSpace()
        {
            var used = 0.0;
            for (var i = 0; i < count; i++)
            {
                used += (frozen[i] ? target[i] : items[i].BaseSize) + items[i].Margins;
            }

            return available - used;
        }

        var initialFree = FreeSpace();
        while (frozen.Contains(false))
        {
            var free = FreeSpace();
            var factorSum = 0.0;
            for (var i = 0; i < count; i++)
            {
                if (!frozen[i])
                {
                    factorSum += growing ? items[i].Grow : items[i].Shrink;
                }
            }

            // Factors summing to less than 1 take only that fraction of the free space.
            if (factorSum < 1)
            {
                var fraction = initialFree * factorSum;
                if (Math.Abs(fraction) < Math.Abs(free))
                {
                    free = fraction;
                }
            }

            if (growing)
            {
                for (var i = 0; i < count; i++)
                {
                    if (!frozen[i])
                    {
                        target[i] = items[i].BaseSize + (free * items[i].Grow / factorSum);
                    }
                }
            }
            else
            {
                var scaledSum = 0.0;
                for (var i = 0; i < count; i++)
                {
                    if (!frozen[i])
                    {
                        scaledSum += items[i].Shrink * items[i].BaseSize;
                    }
                }

                for (var i = 0; i < count; i++)
                {
                    if (!frozen[i])
                    {
                        target[i] = scaledSum > Epsilon
                            ? items[i].BaseSize - (Math.Abs(free) * items[i].Shrink * items[i].BaseSize / scaledSum)
                            : items[i].BaseSize;
                    }
                }
            }

            // Clamp to the min/max sizes and freeze the violators (all items if the violations cancel out).
            var total = 0.0;
            var clamped = new double[count];
            for (var i = 0; i < count; i++)
            {
                if (!frozen[i])
                {
                    clamped[i] = Math.Clamp(target[i], items[i].Min, items[i].Max);
                    total += clamped[i] - target[i];
                }
            }

            var froze = false;
            for (var i = 0; i < count; i++)
            {
                if (frozen[i])
                {
                    continue;
                }

                var minViolation = clamped[i] > target[i] + Epsilon;
                var maxViolation = clamped[i] < target[i] - Epsilon;
                if (Math.Abs(total) <= Epsilon || (total > 0 && minViolation) || (total < 0 && maxViolation))
                {
                    frozen[i] = true;
                    froze = true;
                }

                target[i] = clamped[i];
            }

            if (!froze)
            {
                // Every round freezes an item, unless the sizes are not numbers (infinite or NaN factors or sizes):
                // stop then instead of looping forever, at sizes that are numbers.
                for (var i = 0; i < count; i++)
                {
                    if (!frozen[i])
                    {
                        frozen[i] = true;
                        target[i] = double.IsFinite(target[i]) ? target[i] : items[i].Hypothetical;
                    }
                }
            }
        }

        return target;
    }

    /// <summary>
    /// Positions one line's items on the main axis (CSS Flexbox section 9.5): auto margins take positive free space
    /// first, then justify-content distributes what is left. Returns each item's margin-box start and the size its
    /// auto margins received.
    /// </summary>
    /// <param name="outerSizes">Each item's main size plus its non-auto margins.</param>
    /// <param name="autoMarginCounts">The number of auto main-axis margins of each item (0-2).</param>
    /// <param name="available">The line's main size.</param>
    /// <param name="gap">The gap between items.</param>
    /// <param name="justify">The justify-content value.</param>
    public static (double[] Starts, double AutoMargin) Justify(IReadOnlyList<double> outerSizes, IReadOnlyList<int> autoMarginCounts, double available, double gap, JustifyContent justify)
    {
        var count = outerSizes.Count;
        var starts = new double[count];
        if (count == 0)
        {
            return (starts, 0);
        }

        var free = available - outerSizes.Sum() - (gap * (count - 1));
        var autoMargins = autoMarginCounts.Sum();
        var autoMargin = 0.0;
        if (autoMargins > 0)
        {
            autoMargin = Math.Max(0, free) / autoMargins;
            free = Math.Min(0, free);
        }

        // Negative free space: space-between falls back to flex-start, space-around/evenly to center (CSS Box Alignment).
        var (start, between) = justify switch
        {
            JustifyContent.FlexEnd => (free, 0.0),
            JustifyContent.Center => (free / 2, 0.0),
            JustifyContent.SpaceBetween when count > 1 && free > 0 => (0.0, free / (count - 1)),
            JustifyContent.SpaceAround when free > 0 => (free / count / 2, free / count),
            JustifyContent.SpaceEvenly when free > 0 => (free / (count + 1), free / (count + 1)),
            JustifyContent.SpaceAround or JustifyContent.SpaceEvenly => (free / 2, 0.0),
            _ => (0.0, 0.0),
        };

        var position = start;
        for (var i = 0; i < count; i++)
        {
            starts[i] = position;
            position += outerSizes[i] + (autoMarginCounts[i] * autoMargin) + gap + between;
        }

        return (starts, autoMargin);
    }
}
