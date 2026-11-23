using System.Xml.Linq;
using RaiCore.Pdf.Charts;
using static RaiCore.Pdf.Tests.Charts.ChartXml;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>The chart reader: groups, series and their cached data, axes, legend, titles, formats and colors.</summary>
public class ChartReaderTests
{
    private static readonly string[] Quarters = ["Q1", "Q2", "Q3", "Q4"];

    [Fact]
    public void Groups_series_and_cached_data_are_read()
    {
        var chart = Read(Bars(Series(0, "North", Quarters, [10, 20, null, 40]) + Series(1, "South", Quarters, [1, 2, 3, 4], format: "0.0"), grouping: "stacked"));
        var group = Assert.Single(chart.PlotArea.Groups);
        Assert.Equal((ChartKind.Bar, ChartGrouping.Stacked, false, 100.0), (group.Kind, group.Grouping, group.Horizontal, group.Overlap));
        Assert.Equal([1, 2], group.AxisIds);
        Assert.Equal(["North", "South"], group.Series.Select(s => s.Name));
        var north = group.Series[0];
        Assert.Equal(Quarters, north.Categories!.Texts!);
        Assert.Equal([10, 20, null, 40], north.Values.Numbers);
        Assert.Equal("General", north.Values.FormatCode);
        Assert.Equal("0.0", group.Series[1].Values.FormatCode);
        Assert.Equal(2, chart.PlotArea.Axes.Count);
        Assert.Equal((AxisKind.Category, AxisPosition.Bottom, 2L), (chart.PlotArea.Axes[0].Kind, chart.PlotArea.Axes[0].Position, chart.PlotArea.Axes[0].CrossAxisId));
        Assert.Equal((AxisKind.Value, AxisPosition.Left), (chart.PlotArea.Axes[1].Kind, chart.PlotArea.Axes[1].Position));
        Assert.NotNull(chart.PlotArea.Axes[1].MajorGridlines);
    }

    [Fact]
    public void Series_are_ordered_by_their_order_and_colored_by_their_index()
    {
        var series = "<c:ser><c:idx val=\"1\"/><c:order val=\"0\"/>" + Numbers("val", [1]) + "</c:ser><c:ser><c:idx val=\"0\"/><c:order val=\"1\"/>" + Numbers("val", [2]) + "</c:ser>";
        var chart = Read(Bars(series));
        var group = chart.PlotArea.Groups[0];
        Assert.Equal([1, 0], group.Series.Select(s => s.Index));
        Assert.Equal(PdfColor.FromRgb(0xED, 0x7D, 0x31), group.Series[0].AutoColor); // accent2 for index 1.
        Assert.Equal(PdfColor.FromRgb(0x44, 0x72, 0xC4), group.Series[1].AutoColor);
        Assert.Equal(MarkerSymbol.Square, group.Series[0].AutoMarker);
    }

    [Fact]
    public void Style_colors_cycle_the_theme_accents_darker_after_six_series()
    {
        var chart = Read(Bars(Series(0, "A", null, [1])), scheme: name => name == "accent1" ? PdfColor.FromRgb(0x80, 0x00, 0x00) : null);
        Assert.Equal(PdfColor.FromRgb(0x80, 0, 0), chart.StyleColor(0, 1));
        Assert.Equal(PdfColor.FromRgb(0xED, 0x7D, 0x31), chart.StyleColor(1, 2)); // Office's accent2 where the theme has none.

        // The seventh series: accent1 at 60% luminance.
        var seventh = chart.StyleColor(6, 7);
        Assert.Equal(0x4D, (int)Math.Round(seventh.Red * 255));
        Assert.Equal(0, seventh.Green, 3);

        // Style 1 is shades of gray; styles 3 to 8 are shades of one accent.
        chart.Style = 1;
        var gray = chart.StyleColor(0, 3);
        Assert.Equal(gray.Red, gray.Green);
        Assert.True(chart.StyleColor(2, 3).Red > gray.Red);
        chart.Style = 3;
        Assert.True(chart.StyleColor(0, 3).Red < chart.StyleColor(2, 3).Red);
    }

    [Fact]
    public void Theme_colors_follow_the_color_map_override()
    {
        var xml = Bars(Series(0, "A", null, [1], extra: "<c:spPr><a:solidFill><a:schemeClr val=\"tx1\"><a:lumMod val=\"50000\"/></a:schemeClr></a:solidFill></c:spPr>"))
            .Replace("<c:chart>", "<c:clrMapOvr bg1=\"dk1\" tx1=\"lt1\"/><c:chart>");
        var chart = Read(xml, scheme: name => name switch { "lt1" => PdfColor.FromRgb(200, 200, 200), "dk1" => PdfColor.Black, _ => null });
        var fill = chart.PlotArea.Groups[0].Series[0].Shape.Fill!.Color!.Value;
        Assert.Equal(100 / 255.0, fill.Red, 2); // lt1 at 50% luminance.
        Assert.Equal(PdfColor.Black, chart.Background);
    }

    [Fact]
    public void Shapes_markers_points_and_labels_are_read()
    {
        var extra = "<c:spPr><a:solidFill><a:srgbClr val=\"FF0000\"><a:alpha val=\"50000\"/></a:srgbClr></a:solidFill><a:ln w=\"25400\"><a:solidFill><a:prstClr val=\"blue\"/></a:solidFill><a:prstDash val=\"dash\"/></a:ln></c:spPr>"
            + "<c:marker><c:symbol val=\"triangle\"/><c:size val=\"9\"/></c:marker>"
            + "<c:dPt><c:idx val=\"2\"/><c:spPr><a:noFill/></c:spPr><c:explosion val=\"30\"/></c:dPt>"
            + "<c:dLbls><c:dLbl><c:idx val=\"1\"/><c:delete val=\"1\"/></c:dLbl><c:numFmt formatCode=\"0.0%\" sourceLinked=\"0\"/><c:dLblPos val=\"t\"/><c:showVal val=\"1\"/><c:separator>; </c:separator></c:dLbls>"
            + "<c:smooth val=\"1\"/>";
        var chart = Read(Space(Group("lineChart", "<c:grouping val=\"standard\"/><c:marker val=\"1\"/>", Series(0, "S", Quarters, [1, 2, 3, 4], extra)) + CatAx(1, 2) + ValAx(2, 1)));
        var series = chart.PlotArea.Groups[0].Series[0];
        Assert.Equal(new PdfColor(1, 0, 0, 0.5), series.Shape.Fill!.Color);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), series.Shape.Line!.Color);
        Assert.Equal(2, series.Shape.LineWidth);
        Assert.Equal([4, 3], series.Shape.Dash!);
        Assert.Equal((MarkerSymbol.Triangle, 9.0), (series.Marker!.Symbol, series.Marker.Size!.Value));
        Assert.True(series.Smooth);
        Assert.Same(Paint.None, series.Points[2].Shape!.Fill);
        Assert.Equal(30, series.Points[2].Explosion);
        var labels = series.Labels!;
        Assert.True(labels.ShowValue && labels.Shows);
        Assert.Equal(("0.0%", "t", "; "), (labels.FormatCode, labels.Position, labels.Separator));
        Assert.True(labels.Points[1].Delete);
    }

    [Fact]
    public void Titles_are_rich_text_references_or_automatic()
    {
        var rich = "<c:title><c:tx><c:rich><a:bodyPr rot=\"-5400000\"/><a:p><a:pPr><a:defRPr sz=\"1400\" b=\"1\"/></a:pPr><a:r><a:rPr i=\"1\"><a:solidFill><a:srgbClr val=\"00FF00\"/></a:solidFill><a:latin typeface=\"Arial\"/></a:rPr><a:t>Sales</a:t></a:r><a:r><a:t> 2026</a:t></a:r></a:p><a:p><a:r><a:t>Second line</a:t></a:r></a:p></c:rich></c:tx></c:title>";
        var chart = Read(Bars(Series(0, "North", Quarters, [1, 2, 3, 4]), title: rich));
        Assert.Equal("Sales 2026\nSecond line", chart.Title!.Text);
        Assert.Equal((14.0, true, true, 90.0, "Arial"), (chart.Title.Format.Size!.Value, chart.Title.Format.Bold!.Value, chart.Title.Format.Italic!.Value, chart.Title.Format.Rotation!.Value, chart.Title.Format.Typeface));
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), chart.Title.Format.Color);

        // An empty title of a single series is its name; of several, "Chart Title"; no title element and a single series: its name unless deleted.
        Assert.Equal("North", Read(Bars(Series(0, "North", Quarters, [1]), title: "<c:title/>")).Title!.Text);
        Assert.Equal("Chart Title", Read(Bars(Series(0, "A", null, [1]) + Series(1, "B", null, [1]), title: "<c:title/>")).Title!.Text);
        Assert.Equal("North", Read(Space(Group("barChart", "<c:barDir val=\"col\"/>", Series(0, "North", null, [1])), chart: "")).Title!.Text);
        Assert.Null(Read(Space(Group("barChart", "<c:barDir val=\"col\"/>", Series(0, "North", null, [1])))).Title);
        Assert.Null(Read(Space(Group("barChart", "<c:barDir val=\"col\"/>", Series(0, "A", null, [1]) + Series(1, "B", null, [1])), chart: "")).Title);
    }

    [Fact]
    public void Axes_scaling_formats_ticks_and_titles_are_read()
    {
        var axis = "<c:valAx><c:axId val=\"2\"/><c:scaling><c:logBase val=\"10\"/><c:orientation val=\"maxMin\"/><c:max val=\"1000\"/><c:min val=\"1\"/></c:scaling>"
            + "<c:delete val=\"1\"/><c:axPos val=\"r\"/><c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>Units</a:t></a:r></a:p></c:rich></c:tx></c:title>"
            + "<c:numFmt formatCode=\"#,##0\" sourceLinked=\"0\"/><c:majorTickMark val=\"in\"/><c:minorTickMark val=\"cross\"/><c:tickLblPos val=\"low\"/>"
            + "<c:crossAx val=\"1\"/><c:crosses val=\"max\"/><c:crossBetween val=\"midCat\"/><c:majorUnit val=\"10\"/><c:minorUnit val=\"2\"/></c:valAx>";
        var chart = Read(Space(Group("barChart", "<c:barDir val=\"col\"/>", Series(0, "S", null, [5])) + CatAx(1, 2) + axis));
        var value = chart.PlotArea.Axes[1];
        Assert.Equal((10.0, true, 1000.0, 1.0, true, AxisPosition.Right), (value.LogBase!.Value, value.Reversed, value.Max!.Value, value.Min!.Value, value.Deleted, value.Position));
        Assert.Equal(("#,##0", false, TickMark.Inside, TickMark.Cross, TickLabelPosition.Low), (value.FormatCode, value.SourceLinked, value.MajorTicks, value.MinorTicks, value.Labels));
        Assert.Equal((AxisCrosses.Max, false, 10.0, 2.0, "Units"), (value.Crosses, value.CrossBetween, value.MajorUnit!.Value, value.MinorUnit!.Value, value.Title!.Text));

        // A minimum not below the maximum is dropped; a missing title text is "Axis Title".
        var odd = Read(Space(Group("barChart", "", Series(0, "S", null, [5])) + CatAx(1, 2, extra: "<c:title/>") + ValAx(2, 1, scaling: "<c:max val=\"1\"/><c:min val=\"5\"/>")));
        Assert.Equal((null, null), (odd.PlotArea.Axes[1].Min, odd.PlotArea.Axes[1].Max));
        Assert.Equal("Axis Title", odd.PlotArea.Axes[0].Title!.Text);
    }

    [Fact]
    public void Legends_and_their_deleted_entries_are_read()
    {
        var chart = Read(Bars(Series(0, "A", null, [1]), legend: Legend("t", "<c:legendEntry><c:idx val=\"0\"/><c:delete val=\"1\"/></c:legendEntry><c:legendEntry><c:idx val=\"1\"/></c:legendEntry>")));
        Assert.Equal(LegendPosition.Top, chart.Legend!.Position);
        Assert.Equal([0], chart.Legend.Deleted);
    }

    [Fact]
    public void Boolean_elements_without_a_value_are_true()
    {
        var chart = Read(Space(Group("lineChart", "<c:varyColors/>", Series(0, "A", null, [1], "<c:smooth/>")) + CatAx(1, 2) + ValAx(2, 1)));
        Assert.True(chart.PlotArea.Groups[0].VaryColors);
        Assert.True(chart.PlotArea.Groups[0].Series[0].Smooth);
        Assert.False(Read(Space(Group("lineChart", "<c:varyColors val=\"0\"/>", Series(0, "A", null, [1])))).PlotArea.Groups[0].VaryColors);
    }

    [Fact]
    public void Text_and_background_aliases_fall_back_to_dark_and_light_colors()
    {
        // A theme that knows dk1 but not tx1 (as a document's theme does).
        var xml = Bars(Series(0, "A", null, [1], extra: "<c:spPr><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill></c:spPr>"));
        var chart = Read(xml, scheme: name => name == "dk1" ? PdfColor.FromRgb(200, 0, 0) : null);
        Assert.Equal(PdfColor.FromRgb(200, 0, 0), chart.PlotArea.Groups[0].Series[0].Shape.Fill!.Color);
    }

    [Fact]
    public void Markup_compatibility_blocks_use_their_fallback()
    {
        var style = "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"c14\"><c14:style xmlns:c14=\"urn:c14\" val=\"102\"/></mc:Choice><mc:Fallback><c:style val=\"5\"/></mc:Fallback></mc:AlternateContent>";
        Assert.Equal(5, Read(Bars(Series(0, "A", null, [1])).Replace("<c:chart>", style + "<c:chart>")).Style);
    }

    [Fact]
    public void Literals_multi_level_categories_and_number_categories_are_read()
    {
        var series = "<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:tx><c:v>Literal</c:v></c:tx>"
            + "<c:cat><c:multiLvlStrRef><c:f>A1</c:f><c:multiLvlStrCache><c:ptCount val=\"2\"/><c:lvl><c:pt idx=\"0\"><c:v>Jan</c:v></c:pt><c:pt idx=\"1\"><c:v>Feb</c:v></c:pt></c:lvl><c:lvl><c:pt idx=\"0\"><c:v>2026</c:v></c:pt></c:lvl></c:multiLvlStrCache></c:multiLvlStrRef></c:cat>"
            + "<c:val><c:numLit><c:formatCode>0%</c:formatCode><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>0.5</c:v></c:pt><c:pt idx=\"1\"><c:v>#N/A</c:v></c:pt></c:numLit></c:val></c:ser>";
        var s = Read(Bars(series)).PlotArea.Groups[0].Series[0];
        Assert.Equal("Literal", s.Name);
        Assert.Equal(["Jan", "Feb"], s.Categories!.Texts!);
        Assert.Equal([0.5, null], s.Values.Numbers);
        Assert.Equal("0%", s.Values.FormatCode);
    }

    [Fact]
    public void Three_d_and_pie_of_pie_charts_are_drawn_flat_and_unknown_types_are_named()
    {
        var unsupported = new List<string>();
        var chart = Read(Space(Group("bar3DChart", "<c:barDir val=\"col\"/>", Series(0, "A", null, [1])) + Group("surfaceChart", "", Series(0, "B", null, [1]), 3, 4)
            + Group("ofPieChart", "", Series(1, "C", null, [1]), 5, 6) + CatAx(1, 2) + ValAx(2, 1)), unsupported: unsupported);
        Assert.Equal([ChartKind.Bar, ChartKind.Pie], chart.PlotArea.Groups.Select(g => g.Kind));
        Assert.True(chart.PlotArea.Groups[0].ThreeD);
        Assert.Equal(["surface"], unsupported);
        Assert.Equal(["A 3-D chart is drawn flat.", "A pie-of-pie or bar-of-pie chart is drawn as a single pie."], chart.Notes);
    }

    [Fact]
    public void Missing_parts_give_no_chart()
    {
        Assert.Null(ChartReader.Read(XElement.Parse("<other/>"), null, ResourceLimits.Default));
        Assert.Null(ChartReader.Read(XElement.Parse($"<c:chartSpace xmlns:c=\"{C}\"/>"), null, ResourceLimits.Default));
        Assert.Null(ChartReader.Read(XElement.Parse($"<c:chartSpace xmlns:c=\"{C}\"><c:chart/></c:chartSpace>"), null, ResourceLimits.Default));
        Assert.Empty(Read(Space(string.Empty)).PlotArea.Groups);
    }

    [Fact]
    public void Point_counts_are_bounded()
    {
        // A stated count far beyond the points present is believed only up to a few thousand points.
        var huge = $"<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:val><c:numRef><c:numCache><c:ptCount val=\"2000000000\"/><c:pt idx=\"3\"><c:v>1</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>";
        Assert.Equal(ChartReader.TrustedPointCount, Read(Bars(huge)).PlotArea.Groups[0].Series[0].Values.Count);

        // Point indexes beyond a worksheet's rows, negative or not numbers are skipped.
        var odd = "<c:ser><c:idx val=\"-5\"/><c:order val=\"x\"/><c:val><c:numRef><c:numCache><c:pt idx=\"99999999\"><c:v>1</c:v></c:pt><c:pt idx=\"-1\"><c:v>1</c:v></c:pt><c:pt idx=\"a\"><c:v>1</c:v></c:pt><c:pt idx=\"1\"><c:v>NaN</c:v></c:pt><c:pt idx=\"0\"><c:v>1e999</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>";
        var series = Read(Bars(odd)).PlotArea.Groups[0].Series[0];
        Assert.Equal([null, null], series.Values.Numbers);
        Assert.Equal(0, series.Index);

        // The points of a chart together are bounded by MaxTableCells.
        var many = string.Concat(Enumerable.Range(0, 3).Select(i => Series(i, "S", null, [.. Enumerable.Repeat((double?)1, 40)])));
        var error = Assert.Throws<ResourceLimitExceededException>(() => Read(Bars(many), limits: ResourceLimits.Default with { MaxTableCells = 100 }));
        Assert.Equal("MaxTableCells", error.LimitName);
    }

    [Fact]
    public void References_without_caches_are_resolved_through_the_caller()
    {
        // As openpyxl writes them: references only.
        var series = "<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:tx><c:strRef><c:f>'Data'!B1</c:f></c:strRef></c:tx>"
            + "<c:cat><c:numRef><c:f>'Data'!$A$2:$A$4</c:f></c:numRef></c:cat><c:val><c:numRef><c:f>'Data'!$B$2:$B$4</c:f></c:numRef></c:val></c:ser>";
        var asked = new List<string>();
        ChartReference? Cells(string formula)
        {
            asked.Add(formula);
            return formula switch
            {
                "'Data'!B1" => new ChartReference(["North"]),
                "'Data'!$A$2:$A$4" => new ChartReference(["Q1", "Q2", "Q3"]),
                "'Data'!$B$2:$B$4" => new ChartReference([10.0, "text", null], "0.0"),
                _ => null,
            };
        }

        var s = Read(Bars(series), references: Cells).PlotArea.Groups[0].Series[0];
        Assert.Equal("North", s.Name);
        Assert.Equal(["Q1", "Q2", "Q3"], s.Categories!.Texts!);
        Assert.Equal([10, null, null], s.Values.Numbers);
        Assert.Equal("0.0", s.Values.FormatCode);
        Assert.Equal(3, asked.Count);

        // Cached values win over the cells.
        var cached = Read(Bars(Series(0, "Cached", null, [7])), references: Cells).PlotArea.Groups[0].Series[0];
        Assert.Equal(("Cached", 7.0), (cached.Name, cached.Values.Numbers[0]!.Value));
    }

    [Fact]
    public void Text_formats_inherit()
    {
        var parent = new ChartTextFormat { Size = 10, Bold = false, Color = PdfColor.Black, Typeface = "Calibri" };
        var own = new ChartTextFormat { Bold = true, Rotation = 45 }.Over(parent);
        Assert.Equal((10.0, true, "Calibri", 45.0), (own.Size!.Value, own.Bold!.Value, own.Typeface, own.Rotation!.Value));
        var shape = new ChartShape { Line = Paint.None }.Over(new ChartShape { Fill = new Paint(PdfColor.White), Line = new Paint(PdfColor.Black), LineWidth = 2 });
        Assert.Equal((PdfColor.White, (PdfColor?)null, 2.0), (shape.Fill!.Color!.Value, shape.Line!.Color, shape.LineWidth!.Value));
    }
}
