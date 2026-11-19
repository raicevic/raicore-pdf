using RaiCore.Pdf.Sheets;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>Column widths within the page width: as little wrapping as possible, nothing narrower than readable.</summary>
public class ColumnFitterTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void Columns_that_fit_get_their_widest_line()
    {
        var bands = ColumnFitter.Fit([new(10, 50, 10, 40), new(20, 80, 20, 60)], 200);
        var band = Assert.Single(bands);
        Assert.Equal(0, band.Start);
        Assert.Equal([50.0, 80.0], band.Widths);
    }

    [Fact]
    public void Narrow_columns_keep_their_width_and_wide_ones_share_the_rest_by_weight()
    {
        // 300 points: the first column fits whole; the others share 260 in proportion to their weights (3:1).
        var widths = ColumnFitter.Distribute([new(10, 40, 10, 40), new(30, 900, 30, 300), new(30, 400, 30, 100)], 300);
        Assert.Equal(40, widths[0], Tolerance);
        Assert.Equal(195, widths[1], 1e-3);
        Assert.Equal(65, widths[2], 1e-3);
        Assert.Equal(300, widths.Sum(), 1e-3);
    }

    [Fact]
    public void No_word_is_broken_while_every_column_can_take_its_widest_word()
    {
        // The light column would get 20 by weight, but its widest word needs 60.
        var widths = ColumnFitter.Distribute([new(60, 400, 30, 10), new(20, 900, 30, 300)], 300);
        Assert.Equal(60, widths[0], 1e-3);
        Assert.Equal(240, widths[1], 1e-3);
    }

    [Fact]
    public void Long_words_are_broken_when_the_words_do_not_fit_but_never_below_the_floor()
    {
        var widths = ColumnFitter.Distribute([new(250, 400, 50, 100), new(250, 400, 80, 100)], 300);
        Assert.Equal(300, widths.Sum(), 1e-3);
        Assert.Equal(150, widths[0], 1e-3);
        Assert.Equal(150, widths[1], 1e-3);

        widths = ColumnFitter.Distribute([new(250, 400, 50, 10), new(250, 400, 80, 1000)], 300);
        Assert.Equal(50, widths[0], 1e-3);
        Assert.Equal(250, widths[1], 1e-3);
    }

    [Fact]
    public void A_column_wider_than_the_page_gets_the_page()
    {
        var band = Assert.Single(ColumnFitter.Fit([new(500, 900, 400, 900)], 300));
        Assert.Equal([300.0], band.Widths);
    }

    [Fact]
    public void Columns_whose_floors_do_not_fit_are_split_into_balanced_bands()
    {
        // Ten columns of floor 60 on 300 points: two bands of five, not five and five by accident: weights differ.
        ColumnNeeds[] columns = [.. Enumerable.Range(0, 10).Select(i => new ColumnNeeds(60, 200, 60, 100))];
        var bands = ColumnFitter.Fit(columns, 300);
        Assert.Equal([(0, 5), (5, 5)], bands.Select(b => (b.Start, b.Count)));
        Assert.All(bands, b => Assert.Equal(300, b.Widths.Sum(), 1e-3));

        // Packed by floors, eleven columns would make bands of 5, 5 and 1; balanced, the needs spread over three bands.
        columns = [.. Enumerable.Range(0, 11).Select(i => new ColumnNeeds(60, 200, 60, 100))];
        bands = ColumnFitter.Fit(columns, 300);
        Assert.Equal(3, bands.Count);
        Assert.Equal(11, bands.Sum(b => b.Count));
        Assert.All(bands, b => Assert.InRange(b.Count, 3, 4));
        Assert.All(bands, b => Assert.True(b.Widths.Sum() <= 300 + 1e-3));
    }

    [Fact]
    public void Balancing_never_adds_bands()
    {
        // One heavy column at the end: balanced splitting would need a third band, so the packing by floors stays.
        ColumnNeeds[] columns = [new(50, 60, 50, 50), new(50, 60, 50, 50), new(50, 60, 50, 50), new(50, 60, 50, 50), new(250, 300, 250, 300)];
        var bands = ColumnFitter.Fit(columns, 300);
        Assert.Equal([(0, 4), (4, 1)], bands.Select(b => (b.Start, b.Count)));
    }

    [Fact]
    public void Fill_uses_the_highest_level_that_fits()
    {
        var widths = ColumnFitter.Fill([10, 10], [100, 100], [1, 3], 100);
        Assert.Equal(25, widths[0], 1e-3);
        Assert.Equal(75, widths[1], 1e-3);
        Assert.True(widths.Sum() <= 100);
    }

    [Fact]
    public void Balanced_bands_are_used_only_when_they_are_as_few()
    {
        // Packed by floors: three bands of two. Balanced by needs, the last two columns would go apart: four bands.
        ColumnNeeds[] columns = [new(200, 300, 150, 100), new(150, 300, 150, 100), new(100, 300, 100, 100), new(200, 300, 150, 100), new(300, 300, 100, 100), new(200, 300, 50, 100)];
        Assert.Equal([(0, 2), (2, 2), (4, 2)], ColumnFitter.Fit(columns, 300).Select(b => (b.Start, b.Count)));
    }

    [Fact]
    public void Columns_keep_their_width_only_while_the_others_lows_still_fit()
    {
        // The first column would fit an equal share whole (30 of 50), but then the second could not get its 80.
        var widths = ColumnFitter.Fill([10, 80], [30, 200], [1, 1], 100);
        Assert.Equal(20, widths[0], 1e-3);
        Assert.Equal(80, widths[1], 1e-3);
    }
}
