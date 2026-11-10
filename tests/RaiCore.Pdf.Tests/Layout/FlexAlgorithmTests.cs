using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

public class FlexAlgorithmTests
{
    private static FlexMainInput Item(double basis, double grow = 0, double shrink = 1, double min = 0, double max = double.PositiveInfinity, double margins = 0) =>
        new(basis, min, max, grow, shrink, margins);

    [Fact]
    public void Grow_factors_share_the_free_space()
    {
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(0, grow: 1), Item(0, grow: 2)], 300);

        Assert.Equal([100, 200], sizes);
    }

    [Fact]
    public void Grow_adds_to_the_base_size()
    {
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(50, grow: 1), Item(100, grow: 1)], 250);

        Assert.Equal([100, 150], sizes);
    }

    [Fact]
    public void Shrinking_is_weighted_by_the_base_size()
    {
        // 60 pt overflow, shared 2:1 because the bases are 200 and 100.
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(200), Item(100)], 240);

        Assert.Equal([160, 80], sizes);
    }

    [Fact]
    public void Items_frozen_at_their_minimum_pass_the_rest_on()
    {
        // Unconstrained, both would shrink to 50; the first stops at 90 and the second absorbs the rest.
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(100, min: 90), Item(100)], 100);

        Assert.Equal([90, 10], sizes);
    }

    [Fact]
    public void Items_frozen_at_their_maximum_pass_the_rest_on()
    {
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(0, grow: 1, max: 120), Item(0, grow: 1)], 400);

        Assert.Equal([120, 280], sizes);
    }

    [Fact]
    public void Grow_factors_below_one_take_only_part_of_the_space()
    {
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(0, grow: 0.25)], 100);

        Assert.Equal([25], sizes);
    }

    [Fact]
    public void Inflexible_items_keep_their_size()
    {
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(80, shrink: 0), Item(80)], 100);

        Assert.Equal([80, 20], sizes);
    }

    [Fact]
    public void Margins_take_space_from_the_line()
    {
        var sizes = FlexAlgorithm.ResolveFlexibleLengths([Item(0, grow: 1, margins: 20), Item(0, grow: 1)], 120);

        Assert.Equal([50, 50], sizes);
    }

    [Fact]
    public void Lines_break_where_the_next_item_and_gap_do_not_fit()
    {
        var items = new[] { Item(100), Item(100), Item(100) };

        Assert.Equal([(0, 2), (2, 3)], FlexAlgorithm.BreakLines(items, 210, 10, wrap: true));
        Assert.Equal([(0, 1), (1, 2), (2, 3)], FlexAlgorithm.BreakLines(items, 209, 10, wrap: true));
        Assert.Equal([(0, 3)], FlexAlgorithm.BreakLines(items, 100, 10, wrap: false));
    }

    [Fact]
    public void An_item_wider_than_the_line_gets_a_line_of_its_own()
    {
        Assert.Equal([(0, 1), (1, 2)], FlexAlgorithm.BreakLines([Item(500), Item(10)], 100, 0, wrap: true));
    }

    [Theory]
    [InlineData(JustifyContent.FlexStart, 0, 50)]
    [InlineData(JustifyContent.FlexEnd, 100, 150)]
    [InlineData(JustifyContent.Center, 50, 100)]
    [InlineData(JustifyContent.SpaceBetween, 0, 150)]
    [InlineData(JustifyContent.SpaceAround, 25, 125)]
    [InlineData(JustifyContent.SpaceEvenly, 100.0 / 3, 200.0 / 3 + 50)]
    public void Justify_content_distributes_the_free_space(JustifyContent justify, double first, double second)
    {
        var (starts, _) = FlexAlgorithm.Justify([50, 50], [0, 0], 200, 0, justify);

        Assert.Equal(first, starts[0], 6);
        Assert.Equal(second, starts[1], 6);
    }

    [Theory]
    [InlineData(JustifyContent.SpaceBetween, 0)]
    [InlineData(JustifyContent.SpaceAround, -25)]
    [InlineData(JustifyContent.SpaceEvenly, -25)]
    [InlineData(JustifyContent.Center, -25)]
    public void Overflowing_lines_fall_back_to_start_or_center(JustifyContent justify, double first)
    {
        var (starts, _) = FlexAlgorithm.Justify([75, 75], [0, 0], 100, 0, justify);

        Assert.Equal(first, starts[0], 6);
    }

    [Fact]
    public void Auto_margins_absorb_the_free_space_before_justify_content()
    {
        var (starts, autoMargin) = FlexAlgorithm.Justify([50, 50], [0, 1], 200, 0, JustifyContent.Center);

        Assert.Equal(100, autoMargin);
        Assert.Equal([0, 50], starts); // The second item's margin box starts at 50; its auto left margin is 100.
    }

    [Fact]
    public void Gaps_separate_items()
    {
        var (starts, _) = FlexAlgorithm.Justify([50, 50, 50], [0, 0, 0], 200, 10, JustifyContent.FlexStart);

        Assert.Equal([0, 60, 120], starts);
    }
}
