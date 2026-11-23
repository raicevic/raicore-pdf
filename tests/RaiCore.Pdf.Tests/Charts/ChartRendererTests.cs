using RaiCore.Pdf.Charts;
using static RaiCore.Pdf.Tests.Charts.ChartXml;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>
/// The geometry of drawn charts: bar sizes proportional to values, stacking, axis tick labels where the scale puts
/// them, pie slice angles, line and scatter points, legends, titles and data labels. Positions are read back from the
/// content stream (paths) and the text extractor (labels).
/// </summary>
public class ChartRendererTests
{
    private static readonly string[] Quarters = ["Q1", "Q2", "Q3", "Q4"];
    private static readonly string Accent1 = Rgb(0x44, 0x72, 0xC4);
    private static readonly string Accent2 = Rgb(0xED, 0x7D, 0x31);

    [Fact]
    public void Column_heights_are_proportional_to_values_and_ticks_follow_Excels_scale()
    {
        var drawing = Draw(Bars(Series(0, "North", Quarters, [10, 20, 30, 40])));
        var bars = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        Assert.Equal(4, bars.Count);
        Assert.All(bars, b => Assert.Equal(bars[0].Height / 10, b.Height / ((bars.IndexOf(b) + 1) * 10), 3));
        Assert.All(bars, b => Assert.Equal(bars[0].Y, b.Y, 3)); // They all start at the axis.
        Assert.True(bars.Zip(bars.Skip(1)).All(p => p.First.X < p.Second.X));

        // 10 to 40: Excel's axis runs from 0 to 45 by 5; a label's height is proportional to its value.
        // (The extractor finds "0" in "10" too: the lowest of them is the zero.)
        var zero = drawing.Find("0").Where(p => p.X < bars[0].X).MinBy(p => p.Y);
        var top = Assert.Single(drawing.Find("45"));
        Assert.Empty(drawing.Find("50"));
        Assert.Equal(bars[0].Y, zero.Y, 0);
        var forty = drawing.Find("40").Single(p => p.X < bars[0].X);
        Assert.Equal(bars[3].Y + bars[3].Height, forty.Y, 0);
        Assert.Equal((top.Y - zero.Y) / 45 * 40, forty.Y - zero.Y, 0);
        foreach (var category in Quarters)
        {
            Assert.True(drawing.Find(category)[0].Y < bars[0].Y);
        }
    }

    [Fact]
    public void Clustered_series_sit_side_by_side_with_the_gap_and_overlap()
    {
        var drawing = Draw(Bars(Series(0, "A", Quarters, [1, 2, 3, 4]) + Series(1, "B", Quarters, [4, 3, 2, 1])));
        var a = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        var b = drawing.Rects().Where(r => r.Color == Accent2).ToList();
        Assert.Equal((4, 4), (a.Count, b.Count));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(a[i].X + a[i].Width, b[i].X, 3); // No overlap, no gap between series.
        }

        // The gap between clusters is 150% of a bar's width.
        Assert.Equal(1.5 * a[0].Width, a[1].X - (b[0].X + b[0].Width), 3);
        Assert.Equal(a[0].Height * 4, b[0].Height, 3);
    }

    [Fact]
    public void Stacked_columns_start_where_the_series_before_ends()
    {
        var drawing = Draw(Bars(Series(0, "A", Quarters, [10, 20, 30, 40]) + Series(1, "B", Quarters, [15, 25, 5, 35]), grouping: "stacked"));
        var a = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        var b = drawing.Rects().Where(r => r.Color == Accent2).ToList();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(a[i].Y + a[i].Height, b[i].Y, 3);
            Assert.Equal(a[i].X, b[i].X, 3);
        }

        Assert.Equal(a[0].Height * 1.5, b[0].Height, 3);
        Assert.Single(drawing.Find("80")); // Totals up to 75: 0 to 80 by 10.
    }

    [Fact]
    public void Stacked_negative_values_stack_downwards()
    {
        var drawing = Draw(Bars(Series(0, "A", ["a"], [-10]) + Series(1, "B", ["a"], [-5]), grouping: "stacked"));
        var a = Assert.Single(drawing.Rects(), r => r.Color == Accent1);
        var b = Assert.Single(drawing.Rects(), r => r.Color == Accent2);
        Assert.Equal(a.Y, b.Y + b.Height, 3); // B hangs below A.
        Assert.Equal(a.Height / 2, b.Height, 3);
    }

    [Fact]
    public void Negative_bars_can_be_inverted()
    {
        var drawing = Draw(Bars(Series(0, "A", ["a", "b"], [-10, 10], "<c:invertIfNegative val=\"1\"/>")));
        var white = Assert.Single(drawing.Paths(), p => p.Fill == Rgb(255, 255, 255) && p.Segments.Count == 1 && p.Stroke == Accent1);
        var colored = Assert.Single(drawing.Rects(), r => r.Color == Accent1);
        Assert.True(white.Segments[0].Args[1] < colored.Y); // The inverted bar is the negative one.
    }

    [Fact]
    public void Percent_stacked_columns_fill_the_axis_to_100_percent()
    {
        var drawing = Draw(Bars(Series(0, "A", Quarters, [10, 20, 30, 40]) + Series(1, "B", Quarters, [30, 20, 10, 40]), grouping: "percentStacked"));
        var a = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        var b = drawing.Rects().Where(r => r.Color == Accent2).ToList();
        var totals = a.Zip(b).Select(p => p.First.Height + p.Second.Height).ToList();
        Assert.All(totals, t => Assert.Equal(totals[0], t, 3));
        Assert.Equal(totals[0] / 4, a[0].Height, 3);
        var hundred = Assert.Single(drawing.Find("100%"));
        Assert.Equal(b[0].Y + b[0].Height, hundred.Y, 0);
        Assert.Empty(drawing.Find("120%"));
    }

    [Fact]
    public void Horizontal_bars_grow_right_with_the_first_category_at_the_bottom()
    {
        var drawing = Draw(Bars(Series(0, "Sales", Quarters, [8.2, 3.2, 1.4, 1.2]), direction: "bar"));
        var bars = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        Assert.Equal(4, bars.Count);
        Assert.All(bars, b => Assert.Equal(bars[0].X, b.X, 3));
        Assert.Equal(bars[0].Width / 8.2 * 3.2, bars[1].Width, 3);
        Assert.True(bars[0].Y < bars[3].Y); // Q1 is lowest on the page (PDF y grows upwards).
        Assert.True(drawing.Find("Q1")[0].Y < drawing.Find("Q4")[0].Y);
        Assert.Single(drawing.Find("9")); // 1.2 to 8.2: 0 to 9 by 1, along the bottom.
        Assert.True(drawing.Find("9")[0].Y < bars[0].Y);
    }

    [Fact]
    public void Negative_values_hang_below_the_axis()
    {
        var drawing = Draw(Bars(Series(0, "A", ["a", "b"], [-20, 40])));
        var bars = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        Assert.Equal(bars[0].Y + bars[0].Height, bars[1].Y, 3); // The negative bar ends where the positive one starts.
        Assert.Equal(bars[0].Height * 2, bars[1].Height, 3);
        Assert.Single(drawing.Find("-30")); // -20 to 40: -30 to 50 by 10.
        Assert.Single(drawing.Find("50"));
    }

    [Fact]
    public void Reversed_and_log_axes()
    {
        var reversed = Draw(Bars(Series(0, "A", Quarters, [1, 2, 3, 4]), extra: string.Empty)
            .Replace("<c:catAx><c:axId val=\"1\"/><c:scaling><c:orientation val=\"minMax\"/>", "<c:catAx><c:axId val=\"1\"/><c:scaling><c:orientation val=\"maxMin\"/>"));
        var bars = reversed.Rects().Where(r => r.Color == Accent1).ToList();
        Assert.True(bars[0].X > bars[3].X);

        var log = Draw(Space(Group("barChart", "<c:barDir val=\"col\"/>", Series(0, "A", ["a", "b", "c"], [1, 10, 1000])) + CatAx(1, 2)
            + ValAx(2, 1, scaling: "<c:logBase val=\"10\"/><c:orientation val=\"minMax\"/>")));
        var tops = log.Rects().Where(r => r.Color == Accent1).Select(r => r.Y + r.Height).ToList();
        var ten = log.Find("10").Where(p => p.X < 40).MinBy(p => p.Y);
        var hundred = log.Find("100").MinBy(p => p.Y);
        var thousand = log.Find("1000").Single();
        Assert.Equal(hundred.Y - ten.Y, thousand.Y - hundred.Y, 0); // Decades are equally spaced.
        Assert.Equal(ten.Y, tops[1], 0);
    }

    [Fact]
    public void Pie_slices_take_their_share_of_the_circle_clockwise_from_the_top()
    {
        var drawing = Draw(Space(Group("pieChart", "<c:varyColors val=\"1\"/><c:firstSliceAng val=\"0\"/>", Series(0, "Fruit", ["Apples", "Pears", "Plums", "Figs"], [50, 25, 15, 10]), 0, 0)), 400, 400);
        var slices = drawing.Paths().Where(p => p.Fill is not null && p.Segments.Count > 0 && p.Segments[0].Op == "m").ToList();
        Assert.Equal(4, slices.Count);
        Assert.Equal([Rgb(0x44, 0x72, 0xC4), Rgb(0xED, 0x7D, 0x31), Rgb(0xA5, 0xA5, 0xA5), Rgb(0xFF, 0xC0, 0x00)], slices.Select(s => s.Fill));
        var center = (X: slices[0].Segments[0].Args[0], Y: slices[0].Segments[0].Args[1]);
        double Angle(double x, double y) => (Math.Atan2(x - center.X, y - center.Y) * 180 / Math.PI + 360) % 360; // Clockwise from up.
        var expectedStart = 0.0;
        foreach (var (slice, share) in slices.Zip(new[] { 0.5, 0.25, 0.15, 0.10 }))
        {
            var first = slice.Segments[1].Args;
            var last = slice.Segments.Last(s => s.Op == "c").Args;
            Assert.Equal(expectedStart, Angle(first[0], first[1]) % 360, 1);
            Assert.Equal((expectedStart + (share * 360)) % 360, Angle(last[4], last[5]) % 360, 1);
            expectedStart += share * 360;
        }

        // The radius: the arc ends on the circle.
        var radius = Math.Sqrt(Math.Pow(slices[0].Segments[1].Args[0] - center.X, 2) + Math.Pow(slices[0].Segments[1].Args[1] - center.Y, 2));
        Assert.InRange(radius, 100, 200);
    }

    [Fact]
    public void Pie_labels_show_percentages_and_exploded_slices_move_out()
    {
        var labels = "<c:dLbls><c:showPercent val=\"1\"/><c:showCatName val=\"1\"/></c:dLbls>";
        var drawing = Draw(Space(Group("pieChart", "<c:varyColors val=\"1\"/>", Series(0, "Fruit", ["Apples", "Pears"], [3, 1], "<c:explosion val=\"20\"/>" + labels), 0, 0)), 400, 400);
        Assert.Single(drawing.Find("Apples, 75%"));
        Assert.Single(drawing.Find("Pears, 25%"));
        var slices = drawing.Paths().Where(p => p.Fill is not null && p.Segments.Count > 0 && p.Segments[0].Op == "m").ToList();
        Assert.NotEqual(slices[0].Segments[0].Args[0], slices[1].Segments[0].Args[0]); // Each slice's center moved out its own way.
    }

    [Fact]
    public void Doughnut_rings_leave_the_hole_and_nest_series()
    {
        var drawing = Draw(Space(Group("doughnutChart", "<c:varyColors val=\"1\"/><c:holeSize val=\"50\"/>",
            Series(0, "Inner", ["a", "b"], [1, 1]) + Series(1, "Outer", ["a", "b"], [1, 3]), 0, 0)), 400, 400);
        var rings = drawing.Paths().Where(p => p.Fill is not null && p.Segments.Count > 0 && p.Segments[0].Op == "m").ToList();
        Assert.Equal(4, rings.Count);
        double Radius((string Op, double[] Args) a, (string Op, double[] Args) b) => Math.Sqrt(Math.Pow(a.Args[0] - b.Args[0], 2) + Math.Pow(a.Args[1] - b.Args[1], 2));

        // Each ring segment: outer arc from the top, then a line in to the inner arc.
        var inner = rings[0].Segments;
        var outer = rings[2].Segments;
        var outerTop = outer[0].Args[1];
        var innerTop = inner[0].Args[1];
        Assert.True(outerTop > innerTop);
        var line = inner.First(s => s.Op == "l");
        var start = inner[0];
        Assert.True(line.Args[1] < start.Args[1]);
        _ = Radius(start, line);
    }

    [Fact]
    public void Line_points_sit_in_the_middle_of_their_categories()
    {
        var drawing = Draw(Space(Group("lineChart", "<c:grouping val=\"standard\"/><c:marker val=\"1\"/>", Series(0, "Visitors", ["Jan", "Feb", "Mar", "Apr", "May"], [1, 2, 3, 4, 5])) + CatAx(1, 2) + ValAx(2, 1)));
        var line = drawing.Paths().First(p => p.Stroke == Accent1 && p.Segments.Count == 5);
        var points = line.Segments.Select(s => (X: s.Args[0], Y: s.Args[1])).ToList();
        var steps = points.Zip(points.Skip(1)).Select(p => (p.Second.X - p.First.X, p.Second.Y - p.First.Y)).ToList();
        Assert.All(steps, s => Assert.Equal(steps[0].Item1, s.Item1, 3));
        Assert.All(steps, s => Assert.Equal(steps[0].Item2, s.Item2, 3));
        Assert.Equal(drawing.Find("Jan")[0].X, points[0].X, 0);
        Assert.Equal(drawing.Find("5").Single(p => p.X < points[0].X).Y, points[4].Y, 0);
        Assert.Single(drawing.Find("6")); // 1 to 5: 0 to 6 by 1.

        // Markers: the first automatic symbol, a diamond (four-sided closed paths in the series' color).
        Assert.Equal(5, drawing.Paths().Count(p => p.Fill == Accent1 && p.Segments.Count(s => s.Op == "l") == 3));
    }

    [Fact]
    public void Smooth_lines_are_curves_and_blanks_break_lines()
    {
        var smooth = Draw(Space(Group("lineChart", "<c:grouping val=\"standard\"/>", Series(0, "T", ["a", "b", "c"], [3, 7, 2], "<c:marker><c:symbol val=\"none\"/></c:marker><c:smooth val=\"1\"/>")) + CatAx(1, 2) + ValAx(2, 1)));
        var curve = smooth.Paths().Single(p => p.Stroke == Accent1 && p.Segments.Count > 1);
        Assert.Equal(["m", "c", "c"], curve.Segments.Select(s => s.Op));

        var gap = Draw(Space(Group("lineChart", "<c:grouping val=\"standard\"/>", Series(0, "T", ["a", "b", "c", "d", "e"], [1, 2, null, 4, 5], "<c:marker><c:symbol val=\"none\"/></c:marker>")) + CatAx(1, 2) + ValAx(2, 1)));
        Assert.Equal(["m", "l", "m", "l"], gap.Paths().Single(p => p.Stroke == Accent1).Segments.Select(s => s.Op));

        var span = Draw(Space(Group("lineChart", "<c:grouping val=\"standard\"/>", Series(0, "T", ["a", "b", "c", "d", "e"], [1, 2, null, 4, 5], "<c:marker><c:symbol val=\"none\"/></c:marker>")) + CatAx(1, 2) + ValAx(2, 1),
            chart: "<c:autoTitleDeleted val=\"1\"/>").Replace("</c:plotArea>", "</c:plotArea><c:dispBlanksAs val=\"span\"/>"));
        Assert.Equal(["m", "l", "l", "l"], span.Paths().Single(p => p.Stroke == Accent1).Segments.Select(s => s.Op));
    }

    [Fact]
    public void Scatter_points_are_placed_by_their_x_values()
    {
        var series = Series(0, "Measured", null, [2.5, 3.5, 4, 6.5], "<c:marker><c:symbol val=\"none\"/></c:marker>" + Numbers("xVal", [1, 2, 4, 8]), valueElement: "yVal");
        var drawing = Draw(Space(Group("scatterChart", "<c:scatterStyle val=\"lineMarker\"/>", series) + ValAx(1, 2, "b") + ValAx(2, 1, "l")));
        var line = drawing.Paths().Single(p => p.Stroke == Accent1);
        var xs = line.Segments.Select(s => s.Args[0]).ToList();
        Assert.Equal((xs[1] - xs[0]) * 2, xs[2] - xs[1], 3);
        Assert.Equal((xs[1] - xs[0]) * 4, xs[3] - xs[2], 3);
        var one = drawing.Find("1").MinBy(p => p.Y); // The X axis's labels are the lowest.
        Assert.Equal(one.X, xs[0], 0);
        Assert.Single(drawing.Find("9")); // X 1 to 8: 0 to 9.
    }

    [Fact]
    public void Area_polygons_close_along_the_axis_or_the_series_below()
    {
        var drawing = Draw(Space(Group("areaChart", "<c:grouping val=\"stacked\"/>", Series(0, "A", Quarters, [4, 6, 5, 8]) + Series(1, "B", Quarters, [2, 3, 4, 1])) + CatAx(1, 2) + ValAx(2, 1)));
        var a = drawing.Paths().Single(p => p.Fill == Accent1 && p.Segments.Count >= 8);
        var b = drawing.Paths().Single(p => p.Fill == Accent2 && p.Segments.Count >= 8);
        var aTop = a.Segments.Take(4).Select(s => s.Args[1]).ToList();
        var bBottom = b.Segments.Skip(4).Take(4).Select(s => s.Args[1]).Reverse().ToList();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(aTop[i], bBottom[i], 3);
        }

        var aBottom = a.Segments.Skip(4).Take(4).Select(s => s.Args[1]).Distinct().ToList();
        Assert.Single(aBottom); // The first series closes along the axis.
    }

    [Fact]
    public void Legends_list_series_or_pie_points_with_keys()
    {
        var drawing = Draw(Bars(Series(0, "North", Quarters, [1, 2, 3, 4]) + Series(1, "South", Quarters, [2, 3, 4, 5]), legend: Legend("r")));
        var north = Assert.Single(drawing.Find("North"));
        var south = Assert.Single(drawing.Find("South"));
        Assert.True(north.Y > south.Y); // Top-down in series order.
        Assert.Contains(drawing.Rects(), r => r.Color == Accent1 && r.X > 400 && Math.Abs(r.Y + (r.Height / 2) - north.Y) < 2);

        // Stacked columns list their series top-down as stacked; a bottom legend lists them left to right.
        var stacked = Draw(Bars(Series(0, "North", Quarters, [1, 2, 3, 4]) + Series(1, "South", Quarters, [2, 3, 4, 5]), grouping: "stacked", legend: Legend("r")));
        Assert.True(stacked.Find("North")[0].Y < stacked.Find("South")[0].Y);
        var bottom = Draw(Bars(Series(0, "North", Quarters, [1, 2, 3, 4]) + Series(1, "South", Quarters, [2, 3, 4, 5]), legend: Legend("b")));
        Assert.True(bottom.Find("North")[0].X < bottom.Find("South")[0].X);
        Assert.True(bottom.Find("North")[0].Y < 30);

        // Deleted entries are left out; pies list their categories.
        var deleted = Draw(Bars(Series(0, "North", Quarters, [1, 2, 3, 4]) + Series(1, "South", Quarters, [2, 3, 4, 5]),
            legend: Legend("r", "<c:legendEntry><c:idx val=\"1\"/><c:delete val=\"1\"/></c:legendEntry>")));
        Assert.Empty(deleted.Find("South"));
        var pie = Draw(Space(Group("pieChart", "<c:varyColors val=\"1\"/>", Series(0, "Fruit", ["Apples", "Pears"], [1, 2]), 0, 0), legend: Legend("r")));
        Assert.Single(pie.Find("Apples"));
        Assert.Empty(pie.Find("Fruit"));
    }

    [Fact]
    public void Titles_axis_titles_and_data_labels_are_drawn_as_text()
    {
        var labels = "<c:dLbls><c:numFmt formatCode=\"0.0\" sourceLinked=\"0\"/><c:showVal val=\"1\"/></c:dLbls>";
        var axisTitle = "<c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>Revenue</a:t></a:r></a:p></c:rich></c:tx></c:title>";
        var drawing = Draw(Bars(Series(0, "North", Quarters, [10, 20, 30, 40], labels), title: Title("Quarterly sales"),
            extra: string.Empty).Replace("<c:majorGridlines/>", "<c:majorGridlines/>" + axisTitle));
        var title = Assert.Single(drawing.Find("Quarterly sales"));
        Assert.True(title.Y > 270);
        Assert.InRange(title.X, 200, 280);
        var bars = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        var label = Assert.Single(drawing.Find("40.0"));
        Assert.Equal(bars[3].X + (bars[3].Width / 2), label.X, 0);
        Assert.True(label.Y > bars[3].Y + bars[3].Height);
        Assert.Contains("Revenue", drawing.Text(), StringComparison.Ordinal);
        Assert.Contains("0 1 -1 0", drawing.Content, StringComparison.Ordinal); // The value axis title turned a quarter.
    }

    [Fact]
    public void Deleted_axes_and_hidden_labels_draw_no_text()
    {
        var xml = Bars(Series(0, "North", Quarters, [10, 20, 30, 40]))
            .Replace("<c:catAx><c:axId val=\"1\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/>", "<c:catAx><c:axId val=\"1\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"1\"/>")
            .Replace("<c:tickLblPos val=\"nextTo\"/><c:crossAx val=\"1\"/>", "<c:tickLblPos val=\"none\"/><c:crossAx val=\"1\"/>");
        var drawing = Draw(xml);
        Assert.Equal(string.Empty, drawing.Text().Trim());
        Assert.Equal(4, drawing.Rects().Count(r => r.Color == Accent1));
    }

    [Fact]
    public void Secondary_axes_put_a_second_scale_on_the_right()
    {
        var plot = Group("barChart", "<c:barDir val=\"col\"/><c:grouping val=\"clustered\"/>", Series(0, "Units", Quarters, [10, 20, 30, 40]), 1, 2)
            + Group("lineChart", "<c:grouping val=\"standard\"/>", Series(1, "Share", Quarters, [0.1, 0.2, 0.3, 0.4], format: "0%"), 3, 4)
            + CatAx(1, 2) + ValAx(2, 1)
            + CatAx(3, 4).Replace("<c:delete val=\"0\"/>", "<c:delete val=\"1\"/>")
            + ValAx(4, 3, "r", crosses: "<c:crosses val=\"max\"/>").Replace("<c:majorGridlines/>", string.Empty);
        var drawing = Draw(Space(plot));
        var right = drawing.Find("40%");
        Assert.Single(right);
        Assert.True(right[0].X > 420);
        var bars = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        var line = drawing.Paths().First(p => p.Stroke == Accent2 && p.Segments.Count == 4);
        Assert.Equal(bars[3].X + (bars[3].Width / 2), line.Segments[3].Args[0], 1); // Same categories.
        Assert.Equal(right[0].Y, line.Segments[3].Args[1], 0);
    }

    [Fact]
    public void Plot_areas_with_a_manual_layout_are_placed_as_given()
    {
        var layout = "<c:layout><c:manualLayout><c:layoutTarget val=\"inner\"/><c:xMode val=\"edge\"/><c:yMode val=\"edge\"/><c:x val=\"0.25\"/><c:y val=\"0.2\"/><c:w val=\"0.5\"/><c:h val=\"0.6\"/></c:manualLayout></c:layout>";
        var drawing = Draw(Bars(Series(0, "A", Quarters, [10, 20, 30, 40])).Replace("<c:plotArea><c:layout/>", "<c:plotArea>" + layout), 400, 300);
        var bars = drawing.Rects().Where(r => r.Color == Accent1).ToList();
        Assert.All(bars, b => Assert.InRange(b.X, 100, 300));
        Assert.Equal(300 * 0.2, bars[0].Y, 1); // The bottom of the inner plot area: 20% up from the bottom.
    }

    [Fact]
    public void Radar_bubble_and_stock_charts_are_drawn()
    {
        var radar = Draw(Space(Group("radarChart", "<c:radarStyle val=\"marker\"/>", Series(0, "Model A", ["Speed", "Power", "Range"], [4, 3, 5])) + CatAx(1, 2) + ValAx(2, 1)), 400, 400);
        Assert.Single(radar.Find("Speed"));
        var polygon = radar.Paths().First(p => p.Stroke == Accent1 && p.Segments.Count == 4);
        Assert.Equal(polygon.Segments[0].Args, polygon.Segments[3].Args); // Closed.

        var bubbles = Draw(Space(Group("bubbleChart", string.Empty, Series(0, "M", null, [4, 2], Numbers("xVal", [1, 2]) + Numbers("bubbleSize", [10, 40]), valueElement: "yVal")) + ValAx(1, 2, "b") + ValAx(2, 1)));
        var circles = bubbles.Paths().Where(p => p.Fill == Accent1 && p.Segments.Count(s => s.Op == "c") == 4).ToList();
        Assert.Equal(2, circles.Count);
        double Width(PaintedPath p) => p.Segments.Where(s => s.Args.Length >= 2).Max(s => s.Args[^2]) - p.Segments.Where(s => s.Args.Length >= 2).Min(s => s.Args[^2]);
        Assert.Equal(Width(circles[0]) * 2, Width(circles[1]), 1); // Areas proportional to sizes: four times the size, twice the width.

        var stockSeries = Series(0, "High", ["d1", "d2"], [10, 12], "<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>") + Series(1, "Low", ["d1", "d2"], [5, 6], "<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>")
            + Series(2, "Close", ["d1", "d2"], [8, 7], "<c:spPr><a:ln><a:noFill/></a:ln></c:spPr><c:marker><c:symbol val=\"dash\"/></c:marker>");
        var stock = Draw(Space(Group("stockChart", "<c:hiLowLines/>", stockSeries) + CatAx(1, 2) + ValAx(2, 1)));
        var hiLow = stock.Paths().Where(p => p.Paint == "S" && p.Segments.Count == 2 && Math.Abs(p.Segments[0].Args[0] - p.Segments[1].Args[0]) < 0.01).ToList();
        Assert.True(hiLow.Count >= 2);
    }

    [Fact]
    public void Charts_draw_into_images_and_hostile_values_stay_finite()
    {
        var chart = Read(Bars(Series(0, "A", Quarters, [double.MaxValue, -double.MaxValue, 1e-300, 0])));
        var document = new PdfDocument();
        var page = document.AddPage(PdfPageSize.A4);
        var image = ChartRenderer.ToImage(chart, 300, 200, Options());
        Assert.Equal((300, 200), (image.NaturalWidth, image.NaturalHeight));
        page.Canvas.DrawImage(image, 10, 10, 300, 200);
        ChartRenderer.Draw(page.Canvas, chart, 0, 0, 1e9, 10, Options()); // Too large: nothing.
        ChartRenderer.Draw(page.Canvas, chart, double.NaN, 0, 100, 100, Options());
        Assert.NotEmpty(document.ToArray());

        var empty = Read(Bars(string.Empty));
        Draw(Bars(string.Empty));
        Draw(Space(Group("pieChart", string.Empty, Series(0, "A", ["a"], [0]), 0, 0)));
        Draw(Space(Group("lineChart", string.Empty, Series(0, "A", ["a"], [double.NaN])) + CatAx(1, 2) + ValAx(2, 1, scaling: "<c:logBase val=\"10\"/>")));
        Draw(Space(Group("scatterChart", string.Empty, Series(0, "A", null, [-1, 0], Numbers("xVal", [-5, 0]), valueElement: "yVal")) + ValAx(1, 2, "b", scaling: "<c:logBase val=\"2\"/>") + ValAx(2, 1)));
        Draw(Space(Group("barChart", string.Empty, Series(0, "A", null, [5])) + ValAx(2, 1, extra: "<c:majorUnit val=\"0.000000001\"/>")));
        Assert.Empty(empty.PlotArea.Groups[0].Series);
    }

    [Fact]
    public void Default_chart_areas_follow_the_host_format()
    {
        var opaque = Draw(Bars(Series(0, "A", Quarters, [1, 2, 3, 4])), opaque: true);
        Assert.Contains(opaque.Paths(), p => p.Fill == Rgb(255, 255, 255) && p.Stroke is not null);
        var transparent = Draw(Bars(Series(0, "A", Quarters, [1, 2, 3, 4])), opaque: false);
        Assert.DoesNotContain(transparent.Paths(), p => p.Fill == Rgb(255, 255, 255));
    }
}
