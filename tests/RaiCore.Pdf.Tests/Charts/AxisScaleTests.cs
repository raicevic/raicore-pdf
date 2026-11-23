using RaiCore.Pdf.Charts;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>
/// Excel's automatic value axis scaling. The expected scales are what Excel shows for these data ranges in a chart of
/// the default size (at most 10 intervals): positive data starts at zero unless its minimum is at least 5/6 of its
/// maximum, and the maximum is the first major unit at or above the maximum plus a twentieth of the range (Jon
/// Peltier's "How Excel Calculates Automatic Chart Axis Limits", and Microsoft's KB 214075 for negative data).
/// </summary>
public class AxisScaleTests
{
    [Theory]
    [InlineData(1, 5, 0, 6, 1)]             // 1..5: 0 to 6 by 1.
    [InlineData(10, 40, 0, 45, 5)]          // 10, 20, 30, 40: 0 to 45 by 5.
    [InlineData(25, 100, 0, 120, 20)]       // 25..100: 103.75 needs 11 tens, so twenties: 0 to 120.
    [InlineData(0.1, 0.9, 0, 1, 0.1)]       // Fractions: 0 to 1 by 0.1.
    [InlineData(1.2, 8.2, 0, 9, 1)]         // The POI sample's sales: 0 to 9.
    [InlineData(1.8, 5, 0, 6, 1)]           // Office's sample chart (4.3, 2.5, 3.5, 4.5; 2.4, 4.4, 1.8, 2.8; 2, 2, 3, 5): 0 to 6.
    [InlineData(95, 100, 94, 101, 1)]       // 95 is at least 5/6 of 100: the axis starts near the data.
    [InlineData(1000, 1100, 980, 1120, 20)] // Large values close together.
    [InlineData(-20, 40, -30, 50, 10)]      // Both signs: a twentieth of the range at both ends.
    [InlineData(-2, 38, -5, 45, 5)]         // -2 - 40/20 = -4 rounds to -5 (a tenth would need -10).
    [InlineData(-40, -10, -45, 0, 5)]       // Negative data mirrors positive data: up to zero.
    [InlineData(5, 5, 0, 6, 1)]             // A single value: from zero to it.
    [InlineData(0, 0, 0, 1, 0.2)]           // Nothing but zeros: 0 to 1.
    [InlineData(0, 1e6, 0, 1.2e6, 2e5)]     // Millions.
    public void Automatic_scales_follow_Excel(double min, double max, double expectedMin, double expectedMax, double expectedUnit)
    {
        var scale = AxisScale.Automatic(min, max);
        Assert.Equal(expectedMin, scale.Min, 9);
        Assert.Equal(expectedMax, scale.Max, 9);
        Assert.Equal(expectedUnit, scale.MajorUnit, 9);
        Assert.Equal(expectedUnit / 5, scale.MinorUnit, 9);
    }

    [Fact]
    public void Peltiers_example_rounds_up_to_the_next_major_unit()
    {
        // Ymax + (Ymax - Ymin) / 20 = 3.1 gives 3.5 with the automatic unit of 0.5, 4 with a unit of 1, 3.2 with 0.1.
        Assert.Equal(3.5, AxisScale.Automatic(1, 3).Max, 9);
        Assert.Equal(4, AxisScale.Automatic(1, 3, fixedMajor: 1).Max, 9);
        Assert.Equal(3.2, AxisScale.Automatic(1, 3, fixedMajor: 0.1).Max, 9);

        // With the minimum fixed at 2, the padding comes from it: 3 + (3 - 2) / 20 = 3.05, so 3.1 by 0.1.
        var fixedMin = AxisScale.Automatic(2.5, 3, fixedMin: 2, fixedMajor: 0.1);
        Assert.Equal((2, 3.1), (fixedMin.Min, Math.Round(fixedMin.Max, 9)));
    }

    [Fact]
    public void The_five_sixths_rule_decides_whether_the_axis_starts_at_zero()
    {
        Assert.Equal(0, AxisScale.Automatic(2.49999, 3).Min);
        Assert.NotEqual(0, AxisScale.Automatic(2.5, 3).Min);
    }

    [Fact]
    public void Fixed_values_are_kept_and_the_unit_fits_them()
    {
        var scale = AxisScale.Automatic(3, 7, fixedMin: 0, fixedMax: 100);
        Assert.Equal((0, 100, 10), (scale.Min, scale.Max, scale.MajorUnit));
        var units = AxisScale.Automatic(0, 50, fixedMajor: 25, fixedMinor: 5);
        Assert.Equal((0, 75, 25, 5), (units.Min, units.Max, units.MajorUnit, units.MinorUnit));

        // A minimum not below the maximum is ignored.
        Assert.Equal((0, 6), (AxisScale.Automatic(1, 5, fixedMin: 9, fixedMax: 9).Min, AxisScale.Automatic(1, 5, fixedMin: 9, fixedMax: 9).Max));
    }

    [Fact]
    public void Shorter_axes_get_fewer_intervals()
    {
        Assert.Equal(5, AxisScale.Automatic(10, 40).MajorUnit);
        Assert.Equal(10, AxisScale.Automatic(10, 40, maxIntervals: 5).MajorUnit);
        Assert.Equal(20, AxisScale.Automatic(10, 40, maxIntervals: 3).MajorUnit);
    }

    [Fact]
    public void Ticks_are_clean_decimal_values()
    {
        var ticks = AxisScale.Automatic(0.1, 0.9).MajorTicks();
        Assert.Equal(11, ticks.Count);
        Assert.Equal(0.3, ticks[3]);
        Assert.Equal("0.3", ChartNumberFormat.General(ticks[3]));
        Assert.Equal(51, AxisScale.Automatic(0.1, 0.9).MinorTicks().Count);
    }

    [Fact]
    public void Logarithmic_scales_run_between_powers_of_the_base()
    {
        var scale = AxisScale.Automatic(3, 2000, logBase: 10);
        Assert.Equal((1, 10000), (scale.Min, scale.Max));
        Assert.Equal([1, 10, 100, 1000, 10000], scale.MajorTicks().Select(t => Math.Round(t, 6)));
        Assert.Equal(0.5, scale.Fraction(100), 9);
        Assert.Equal(100, scale.ValueAt(0.5), 6);
        Assert.Equal(double.NegativeInfinity, scale.Fraction(-1));
        Assert.Equal([2, 4, 8, 16], AxisScale.Automatic(3, 15, logBase: 2).MajorTicks().Select(t => Math.Round(t, 6)));
    }

    [Theory]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity)]
    [InlineData(-1e308, 1e308)]
    [InlineData(1e-300, 2e-300)]
    [InlineData(1e300, 1e300)]
    public void Hostile_ranges_give_finite_scales_with_bounded_ticks(double min, double max)
    {
        var scale = AxisScale.Automatic(min, max);
        Assert.True(double.IsFinite(scale.Min) && double.IsFinite(scale.Max) && scale.Max > scale.Min, $"{scale}");
        Assert.InRange(scale.MajorTicks().Count, 1, AxisScale.MaxTicks + 1);
        var log = AxisScale.Automatic(min, max, logBase: 10);
        Assert.InRange(log.MajorTicks().Count, 1, AxisScale.MaxTicks + 1);
    }

    [Fact]
    public void Tiny_fixed_units_fall_back_to_automatic_ones()
    {
        var scale = AxisScale.Automatic(0, 100, fixedMajor: 1e-9, fixedMinor: 1e-12);
        Assert.Equal(20, scale.MajorUnit);
        Assert.Equal(4, scale.MinorUnit);
    }
}
