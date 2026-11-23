namespace RaiCore.Pdf.Charts;

/// <summary>
/// The scale of a value axis: its minimum, maximum and major and minor units, linear or logarithmic. Made by
/// <see cref="Automatic"/>, which follows Excel's automatic scaling.
/// </summary>
/// <param name="Min">The minimum.</param>
/// <param name="Max">The maximum (greater than the minimum).</param>
/// <param name="MajorUnit">The distance between major ticks (a factor for a log scale).</param>
/// <param name="MinorUnit">The distance between minor ticks.</param>
/// <param name="LogBase">The logarithm base, or <c>null</c> for a linear scale.</param>
internal readonly record struct AxisScale(double Min, double Max, double MajorUnit, double MinorUnit, double? LogBase = null)
{
    /// <summary>The most intervals Excel divides a value axis into.</summary>
    public const int MaxIntervals = 10;

    /// <summary>The most ticks drawn on an axis whose units are given by the file.</summary>
    internal const int MaxTicks = 1000;

    /// <summary>The position of a value along the axis, from 0 at the minimum to 1 at the maximum (outside for values out of range).</summary>
    public double Fraction(double value)
    {
        if (LogBase is { } b)
        {
            if (value <= 0)
            {
                return double.NegativeInfinity;
            }

            var lo = Math.Log(Min, b);
            return (Math.Log(value, b) - lo) / (Math.Log(Max, b) - lo);
        }

        return (value - Min) / (Max - Min);
    }

    /// <summary>The value at a fraction of the axis.</summary>
    public double ValueAt(double fraction) => LogBase is { } b
        ? Math.Pow(b, Math.Log(Min, b) + (fraction * (Math.Log(Max, b) - Math.Log(Min, b))))
        : Min + (fraction * (Max - Min));

    /// <summary>The values of the major ticks, from the minimum to the maximum.</summary>
    public IReadOnlyList<double> MajorTicks() => Ticks(MajorUnit);

    /// <summary>The values of the minor ticks, from the minimum to the maximum.</summary>
    public IReadOnlyList<double> MinorTicks() => Ticks(MinorUnit);

    private List<double> Ticks(double unit)
    {
        var ticks = new List<double>();
        if (LogBase is { } b)
        {
            var factor = unit > 1 ? unit : b;
            var start = Math.Pow(b, Math.Floor(Math.Log(Min, b) + 1e-9));
            for (var value = start; value <= Max * (1 + 1e-9) && ticks.Count <= MaxTicks; value *= factor)
            {
                if (value >= Min * (1 - 1e-9))
                {
                    ticks.Add(value);
                }
            }

            return ticks;
        }

        var first = Math.Ceiling((Min / unit) - 1e-9);
        var last = Math.Floor((Max / unit) + 1e-9);
        if (!double.IsFinite(first) || !double.IsFinite(last) || last - first > MaxTicks)
        {
            return [Min, Max];
        }

        for (var k = first; k <= last; k++)
        {
            ticks.Add(Clean(k * unit));
        }

        return ticks;
    }

    /// <summary>A value rounded to 12 significant digits, so that tick values print as Excel prints them (0.3, not 0.30000000000000004).</summary>
    internal static double Clean(double value)
    {
        if (value == 0 || !double.IsFinite(value))
        {
            return value;
        }

        var digits = 11 - (int)Math.Floor(Math.Log10(Math.Abs(value)));
        return digits is >= 0 and <= 15 ? Math.Round(value, digits) : value;
    }

    /// <summary>
    /// The automatic scale of a value axis, as Excel makes it. Positive data starts at zero unless its minimum is at
    /// least 5/6 of its maximum; the maximum is the first major unit at or above the data's maximum plus a twentieth of
    /// its range (mirrored for negative data; data of both signs is padded by a twentieth at both ends); the major unit
    /// is the smallest of 1, 2 or 5 times a power of ten that divides the axis into at most
    /// <paramref name="maxIntervals"/> intervals. Values the file fixes (minimum, maximum, units) are kept.
    /// </summary>
    /// <param name="dataMin">The least value plotted on the axis (NaN for none).</param>
    /// <param name="dataMax">The greatest value plotted on the axis (NaN for none).</param>
    /// <param name="fixedMin">The minimum the file fixes, or <c>null</c>.</param>
    /// <param name="fixedMax">The maximum the file fixes, or <c>null</c>.</param>
    /// <param name="fixedMajor">The major unit the file fixes, or <c>null</c>.</param>
    /// <param name="fixedMinor">The minor unit the file fixes, or <c>null</c>.</param>
    /// <param name="logBase">The logarithm base of a log scale, or <c>null</c>.</param>
    /// <param name="maxIntervals">The most intervals the axis has room for (Excel's at most 10).</param>
    public static AxisScale Automatic(double dataMin, double dataMax, double? fixedMin = null, double? fixedMax = null,
        double? fixedMajor = null, double? fixedMinor = null, double? logBase = null, int maxIntervals = MaxIntervals)
    {
        maxIntervals = Math.Clamp(maxIntervals, 1, MaxIntervals);
        if (!double.IsFinite(dataMin) || !double.IsFinite(dataMax))
        {
            (dataMin, dataMax) = (0, 0);
        }

        // Keep the arithmetic finite.
        const double Limit = 1e300;
        (dataMin, dataMax) = (Math.Clamp(Math.Min(dataMin, dataMax), -Limit, Limit), Math.Clamp(Math.Max(dataMin, dataMax), -Limit, Limit));
        fixedMin = fixedMin is { } fmin && double.IsFinite(fmin) ? Math.Clamp(fmin, -Limit, Limit) : null;
        fixedMax = fixedMax is { } fmax && double.IsFinite(fmax) ? Math.Clamp(fmax, -Limit, Limit) : null;
        if (fixedMin is { } a && fixedMax is { } b && a >= b)
        {
            (fixedMin, fixedMax) = (null, null);
        }

        if (logBase is { } logarithm)
        {
            return Logarithmic(dataMin, dataMax, fixedMin, fixedMax, fixedMajor, logarithm);
        }

        // The padded range Excel fits major units around.
        double lo, hi;
        var autoMin = fixedMin is null;
        var autoMax = fixedMax is null;
        var range = dataMax - dataMin;
        if (dataMin == 0 && dataMax == 0 && autoMin && autoMax && fixedMajor is null)
        {
            // Nothing but zeros (or no data): 0 to 1.
            return new AxisScale(0, 1, 0.2, fixedMinor is { } zm && zm > 0 && 1 / zm <= MaxTicks ? zm : 0.04);
        }

        if (dataMin == dataMax)
        {
            // A single value: from zero to it.
            (lo, hi) = dataMax > 0 ? (0.0, dataMax) : dataMax < 0 ? (dataMin, 0.0) : (0.0, 1.0);
            range = hi - lo;
            hi += autoMax && hi > 0 ? range / 20 : 0;
            lo -= autoMin && lo < 0 ? range / 20 : 0;
        }
        else if (dataMin >= 0)
        {
            lo = dataMin >= dataMax * 5 / 6 ? dataMin - (range / 20) : 0;
            hi = dataMax + ((dataMax - (fixedMin ?? dataMin)) / 20);
        }
        else if (dataMax <= 0)
        {
            hi = dataMax <= dataMin * 5 / 6 ? dataMax + (range / 20) : 0;
            lo = dataMin - (((fixedMax ?? dataMax) - dataMin) / 20);
        }
        else
        {
            lo = dataMin - (range / 20);
            hi = dataMax + (range / 20);
        }

        lo = fixedMin ?? lo;
        hi = fixedMax ?? hi;
        if (fixedMin is { } min && fixedMax is null && hi <= min)
        {
            hi = min + Math.Max(Math.Abs(min), 1);
        }

        if (fixedMax is { } max && fixedMin is null && lo >= max)
        {
            lo = max - Math.Max(Math.Abs(max), 1);
        }

        var roundLo = autoMin && lo != 0;
        var roundHi = autoMax && hi != 0;
        var unit = fixedMajor is { } major && major > 0 && (hi - lo) / major <= MaxTicks ? major : NiceUnit(lo, hi, maxIntervals, roundLo, roundHi);
        if (roundLo)
        {
            // The first major unit at or below the padded minimum.
            lo = Math.Floor((lo / unit) + 1e-9) * unit;
        }

        if (roundHi)
        {
            // The first major unit above the padded maximum.
            hi = Above(hi, unit);
        }

        if (hi <= lo)
        {
            hi = lo + unit;
        }

        var minor = fixedMinor is { } m && m > 0 && (hi - lo) / m <= MaxTicks ? m : unit / 5;
        return new AxisScale(Clean(lo), Clean(hi), unit, minor);
    }

    /// <summary>
    /// The smallest major unit of 1, 2 or 5 times a power of ten that divides the range into at most
    /// <paramref name="maxIntervals"/> intervals once the automatic ends are rounded out to it.
    /// </summary>
    internal static double NiceUnit(double lo, double hi, int maxIntervals, bool roundLo = true, bool roundHi = true)
    {
        var range = hi - lo;
        if (!(range > 0) || !double.IsFinite(range))
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(range / maxIntervals));
        for (var e = exponent - 1; e <= exponent + 2; e++)
        {
            foreach (var mantissa in (ReadOnlySpan<double>)[1, 2, 5])
            {
                var unit = mantissa * Math.Pow(10, e);
                var start = roundLo ? Math.Floor((lo / unit) + 1e-9) : lo / unit;
                var end = roundHi ? Above(hi, unit) / unit : hi / unit;
                if (end - start <= maxIntervals + 1e-9)
                {
                    return Clean(unit);
                }
            }
        }

        return Clean(Math.Pow(10, exponent + 3));
    }

    /// <summary>
    /// The first multiple of a unit above a value: strictly above for positive values (3.1 by 0.1 gives 3.2, as Excel
    /// does), at or above for negative ones (whose padding already moved them toward zero).
    /// </summary>
    private static double Above(double value, double unit)
    {
        var steps = value / unit;
        return (value > 0 ? Math.Floor(steps + 1e-9) + 1 : Math.Ceiling(steps - 1e-9)) * unit;
    }

    /// <summary>A logarithmic scale from the power of the base at or below the data to the one at or above it.</summary>
    private static AxisScale Logarithmic(double dataMin, double dataMax, double? fixedMin, double? fixedMax, double? fixedMajor, double logBase)
    {
        static double Power(double b, double value, bool up) =>
            Math.Pow(b, up ? Math.Ceiling(Math.Log(value, b) - 1e-9) : Math.Floor(Math.Log(value, b) + 1e-9));

        var lo = fixedMin is { } fmin && fmin > 0 ? fmin : dataMin > 0 ? Power(logBase, dataMin, up: false) : 1;
        var hi = fixedMax is { } fmax && fmax > 0 ? fmax : dataMax > 0 ? Power(logBase, dataMax, up: true) : lo * logBase;
        if (hi <= lo)
        {
            hi = lo * logBase;
        }

        // At most 1000 decades, so tick lists stay bounded.
        if (Math.Log(hi / lo, logBase) > MaxTicks)
        {
            hi = lo * Math.Pow(logBase, MaxTicks);
        }

        var major = fixedMajor is { } m && m > 1 ? m : logBase;
        return new AxisScale(lo, hi, major, major, logBase);
    }
}
