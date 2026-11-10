using RaiCore.Pdf.Html.Css;

namespace RaiCore.Pdf.Tests.Html;

public class CssCalcTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    [Theory]
    [InlineData("calc(1px + 2px)", 3)]
    [InlineData("calc(10px - 4px - 3px)", 3)] // Left to right.
    [InlineData("calc(12px / 2 / 3)", 2)]
    [InlineData("calc(10px + 2px * 3)", 16)] // * before +.
    [InlineData("calc(2px * 3 + 10px)", 16)]
    [InlineData("calc(10px - 6px / 2)", 7)] // / before -.
    [InlineData("calc((10px + 2px) * 3)", 36)]
    [InlineData("calc(2 * (10px + 5px))", 30)]
    [InlineData("calc((1 + 2) * 10px)", 30)]
    [InlineData("calc(10px * min(2, 3))", 20)]
    [InlineData("calc(100px / (1 + 3))", 25)]
    [InlineData("calc(calc(10px + 5px) * 2)", 30)]
    [InlineData("calc(-1 * 10px)", -10)]
    [InlineData("calc(10px - -5px)", 15)]
    [InlineData("calc(1e1px + 2.5E-1px)", 10.25)]
    [InlineData("calc(.5px + 1.5px)", 2)]
    [InlineData("CALC(10PX + 2px)", 12)]
    [InlineData("calc(  1px   +   2px  )", 3)]
    [InlineData("calc(1px\t+\n2px)", 3)]
    [InlineData("calc(2px*3)", 6)] // * and / need no whitespace.
    [InlineData("calc(6px/3)", 2)]
    [InlineData("min(10px, 20px, 5px)", 5)]
    [InlineData("max(10px, 20px, 5px)", 20)]
    [InlineData("min(10px)", 10)]
    [InlineData("clamp(10px, 5px, 20px)", 10)]
    [InlineData("clamp(10px, 15px, 20px)", 15)]
    [InlineData("clamp(10px, 25px, 20px)", 20)]
    [InlineData("clamp(30px, 25px, 20px)", 30)] // The minimum wins over the maximum.
    [InlineData("max(min(10px, 20px), calc(4px * 2))", 10)]
    public void Expressions_without_percentages_fold_to_points(string css, double pixels)
    {
        var length = Parse(css);

        Assert.Equal(CssLengthKind.Points, length.Kind);
        Assert.Equal(pixels * Px, length.Value, 9);
    }

    [Theory]
    [InlineData("calc(1in + 2.54cm + 72pt + 6pc + 25.4mm + 101.6Q)", 6 * 72)]
    [InlineData("calc(1em + 1rem + 2ex + 2ch)", 10 + 20 + 10 + 10)]
    [InlineData("calc(1px + 1em)", 0.75 + 10)]
    [InlineData("calc(10vw + 10vh)", 40 + 60)]
    [InlineData("calc(10vmin + 10vmax)", 40 + 60)]
    public void Units_convert_to_points(string css, double points)
    {
        // Font size 10pt, root font size 20pt, a 400 x 600 pt viewport.
        var length = CssValues.TryParseLength(css, 10, 20, new CssViewport(400, 600));

        Assert.Equal(CssLengthKind.Points, length!.Value.Kind);
        Assert.Equal(points, length.Value.Value, 9);
    }

    [Fact]
    public void Viewport_units_need_a_viewport()
    {
        Assert.Null(CssValues.TryParseLength("calc(10vw + 1px)"));
        Assert.Equal(40, CssValues.ParseLength("10vw", viewport: new CssViewport(400, 600)).Value, 9);
    }

    [Theory]
    [InlineData("calc(50%)", 50)]
    [InlineData("calc(100% / 4)", 25)]
    [InlineData("calc(10% * 2 + 5%)", 25)]
    [InlineData("calc(-1 * 10%)", -10)]
    [InlineData("min(50%)", 50)]
    public void Pure_percentages_stay_percentages(string css, double percent)
    {
        var length = Parse(css);

        Assert.Equal(CssLengthKind.Percent, length.Kind);
        Assert.Equal(percent, length.Value, 9);
    }

    [Theory]
    [InlineData("calc(100% - 20px)", 400, 400 - 15)]
    [InlineData("calc(100% - 20px)", 100, 100 - 15)]
    [InlineData("calc((100% - 20px) / 3)", 300, (300 - 15) / 3.0)]
    [InlineData("calc(2 * (10px + 5%))", 200, 2 * (7.5 + 10))]
    [InlineData("calc(10px + 2 * 5%)", 200, 7.5 + 20)]
    [InlineData("calc(5% + 2px / 2)", 200, 10 + 0.75)]
    [InlineData("calc(100% - 2 * 10px)", 200, 200 - 15)]
    [InlineData("calc(1px - 100%)", 200, 0.75 - 200)]
    [InlineData("calc(calc(10% + 5px) * 2)", 200, 2 * (20 + 3.75))]
    [InlineData("calc((20% + 5px) - 10px)", 200, 40 - 3.75)] // var() substitution as AngleSharp serializes it.
    [InlineData("min(100%, 300px)", 100, 100)]
    [InlineData("min(100%, 300px)", 400, 225)]
    [InlineData("max(10%, 30px)", 100, 22.5)]
    [InlineData("max(10%, 30px)", 400, 40)]
    [InlineData("clamp(100px, 50%, 400px)", 100, 75)]
    [InlineData("clamp(100px, 50%, 400px)", 400, 200)]
    [InlineData("clamp(100px, 50%, 400px)", 1000, 300)]
    [InlineData("clamp(100px, (50% + 32px), 400px)", 200, 124)] // Nested calc() as AngleSharp serializes it.
    [InlineData("clamp(100px, calc(50% + 32px), 400px)", 200, 124)]
    [InlineData("min(max(10px, 5%), calc(100% - 16px))", 100, 7.5)]
    [InlineData("min(max(10px, 5%), calc(100% - 16px))", 400, 20)]
    [InlineData("min(max(10px, 5%), calc(100% - 16px))", 10, -2)]
    [InlineData("calc(min(70px, 50%) + 16px)", 80, 40 + 12)]
    [InlineData("calc(min(70px, 50%) + 16px)", 400, 52.5 + 12)]
    [InlineData("calc(100% - min(10px, 5%) * 2)", 100, 100 - 10)]
    [InlineData("calc(min(10%, 10px) - min(10%, 10px))", 300, 0)]
    public void Percentages_resolve_against_the_reference(string css, double reference, double points)
    {
        var length = Parse(css);

        Assert.Equal(CssLengthKind.Calc, length.Kind);
        Assert.True(length.HasPercentage);
        Assert.Equal(points, length.Resolve(reference), 9);
    }

    [Theory]
    [InlineData("calc(1px+2px)")] // + and - need whitespace on both sides.
    [InlineData("calc(100%-20px)")]
    [InlineData("calc(1px -2px)")]
    [InlineData("calc(1px +2px)")]
    [InlineData("calc(1px- 2px)")]
    [InlineData("calc(1px+ 2px)")]
    [InlineData("calc((1px)-(2px))")]
    [InlineData("calc(10px + 5)")] // Lengths and numbers do not mix.
    [InlineData("calc(50% + 3)")]
    [InlineData("calc(10px * 2px)")]
    [InlineData("calc(5% * 3px)")]
    [InlineData("calc(5% / 2px)")]
    [InlineData("calc(10px * 2.5em / 1em)")]
    [InlineData("calc(2 / 10px)")]
    [InlineData("calc(10px / 0)")] // Division by zero.
    [InlineData("calc(10px / (1 - 1))")]
    [InlineData("calc(1e400px)")] // Not finite.
    [InlineData("Infinitypx")]
    [InlineData("calc(0 + 5px)")] // 0 is a number inside calc().
    [InlineData("calc(5)")] // A number is not a length.
    [InlineData("min(1, 2)")]
    [InlineData("calc()")]
    [InlineData("calc(10px")]
    [InlineData("calc(10px))")]
    [InlineData("calc(10px 5px)")]
    [InlineData("calc(10px, 5px)")]
    [InlineData("calc(10px + )")]
    [InlineData("calc(10foo)")]
    [InlineData("calc(var(--gap) * 2)")] // Variables are substituted by AngleSharp before values get here.
    [InlineData("min()")]
    [InlineData("min(10px,)")]
    [InlineData("max(10px, 5)")]
    [InlineData("clamp(1px, 2px)")]
    [InlineData("clamp(1px, 2px, 3px, 4px)")]
    [InlineData("foo(10px)")]
    [InlineData("(10px)")]
    [InlineData("-calc(10px)")]
    [InlineData("calc(10px) 5px")]
    public void Invalid_expressions_are_rejected(string css)
    {
        Assert.Null(CssValues.TryParseLength(css));
        Assert.Equal(CssLength.Zero, CssValues.ParseLength(css));
    }

    [Fact]
    public void Plain_values_are_unchanged()
    {
        Assert.Equal(new CssLength(7.5, CssLengthKind.Points), Parse("10px"));
        Assert.Equal(new CssLength(50, CssLengthKind.Percent), Parse("50%"));
        Assert.Equal(CssLength.Auto, Parse("auto"));
        Assert.Null(CssValues.TryParseLength("10foo"));
        Assert.Null(CssValues.TryParseLength(""));
        Assert.Equal(CssLength.Zero, CssValues.ParseLength(null));
    }

    [Fact]
    public void Non_negative_clamps_calc_results_at_zero()
    {
        var width = Parse("calc(50% - 100px)").NonNegative();

        Assert.Equal(0, width.Resolve(100));
        Assert.Equal(200 - 75, width.Resolve(400), 9);
        Assert.Equal(0, new CssLength(-5, CssLengthKind.Points).NonNegative().Value);
        Assert.Equal(-5, Parse("calc(10% - 5pt)").Resolve(0), 9); // Margins can be negative.
    }

    [Fact]
    public void Components_split_outside_parentheses()
    {
        Assert.Equal(["calc(1px + 2px)", "3px", "min(1px, 2%)"], CssValues.SplitComponents("  calc(1px + 2px)  3px\tmin(1px, 2%) "));
        Assert.Empty(CssValues.SplitComponents(" "));
    }

    private static CssLength Parse(string css) => CssValues.TryParseLength(css) ?? throw new Xunit.Sdk.XunitException($"'{css}' did not parse.");
}
