using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using RaiCore.Pdf.Charts;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Xlsx;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Tests.Slides;
using static RaiCore.Pdf.Tests.WordDocuments.Docx;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>
/// The newer chart types (chartex): the reader's numbers (waterfall running totals, histogram bins, pareto shares,
/// box plot statistics, funnel values, treemap and sunburst hierarchies) against an independent parse of the chart XML
/// and cells (expected.json, by make-chartex.py), the layouts of treemaps and sunbursts, hostile input, and the charts
/// drawn in workbooks, Word documents and presentations.
/// </summary>
public class ChartExTests
{
    private const string Cx = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    private static readonly Lazy<JsonElement> Expected = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Charts/ChartEx/expected.json"))).RootElement);

    private static string[] PageTexts(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    /// <summary>Every chartex part of the corpus: file, part (or chart name), and its expectations.</summary>
    public static TheoryData<string, string> Parts()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Expected.Value.EnumerateObject())
        {
            foreach (var part in file.Value.EnumerateObject())
            {
                data.Add(file.Name, part.Name);
            }
        }

        return data;
    }

    /// <summary>A chartex part of a fixture read into the chart model (a workbook's with its cells).</summary>
    private static (Chart? Chart, List<string> Unsupported) ReadPartOf(string file, string part)
    {
        var path = Path.Combine(Fixtures.Path("Charts/ChartEx"), file);
        var bytes = File.ReadAllBytes(path);
        var unsupported = new List<string>();
        var limits = ResourceLimits.Default;
        if (file.EndsWith(".xlsx", StringComparison.Ordinal))
        {
            using var package = XlsxPackage.Open(bytes, limits);
            var workbook = XlsxWorkbook.Read(package, CultureInfo.InvariantCulture);
            var cells = new XlsxChartCells(package, workbook, limits);
            var root = package.Parse(part, reader => XElement.Load(reader), null)!;
            return (ChartReader.Read(root, null, limits, unsupported, cells.Resolve), unsupported);
        }

        var name = file switch
        {
            "chartex.docx" => $"word/charts/chartEx_{part}.xml",
            "chartex.pptx" => $"ppt/charts/chartEx_{part}.xml",
            _ => part,
        };
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var stream = zip.GetEntry(name)!.Open();
        return (ChartReader.Read(XElement.Load(stream), null, limits, unsupported), unsupported);
    }

    private static double?[] Numbers(JsonElement array) => [.. array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (double?)null : v.GetDouble())];

    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 9);

    private static void Near(double?[] expected, double?[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i] is null, actual[i] is null);
            if (expected[i] is { } e)
            {
                Near(e, actual[i]!.Value);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Parts))]
    public void The_numbers_match_the_independent_parse(string file, string part)
    {
        var entries = Expected.Value.GetProperty(file).GetProperty(part).EnumerateArray().ToList();
        var (chart, unsupported) = ReadPartOf(file, part);
        Assert.NotNull(chart);
        if (entries.All(e => e.GetProperty("layout").GetString() == "regionMap"))
        {
            Assert.Empty(chart.PlotArea.Groups);
            Assert.Equal(["region map"], unsupported);
            return;
        }

        Assert.Empty(unsupported);
        var series = chart.PlotArea.Groups.SelectMany(g => g.Series).ToDictionary(s => s.Index);
        for (var k = 0; k < entries.Count; k++)
        {
            var entry = entries[k];
            switch (entry.GetProperty("layout").GetString())
            {
                case "waterfall":
                {
                    var s = series[k];
                    Near(Numbers(entry.GetProperty("values")), s.Values.Numbers);
                    Near(Numbers(entry.GetProperty("starts")), s.Starts!);
                    var ends = Numbers(entry.GetProperty("ends"));
                    for (var i = 0; i < ends.Length; i++)
                    {
                        Near(ends[i]!.Value, s.Starts![i]!.Value + (s.Values.Number(i) ?? 0));
                    }

                    break;
                }

                case "clusteredColumn":
                {
                    var s = series[k];
                    if (entry.TryGetProperty("sorted", out var sorted))
                    {
                        Near(Numbers(sorted), s.Values.Numbers);
                        Near(Numbers(entry.GetProperty("shares")), series[k + 1].Values.Numbers);
                        if (entry.GetProperty("categories") is { ValueKind: JsonValueKind.Array } categories)
                        {
                            Assert.Equal(categories.EnumerateArray().Select(c => c.GetString()), s.Categories!.Texts!);
                        }
                    }
                    else if (entry.TryGetProperty("counts", out var counts))
                    {
                        Near(Numbers(counts), s.Values.Numbers);
                    }

                    if (entry.TryGetProperty("counts", out var binned))
                    {
                        Assert.Equal(binned.GetArrayLength(), s.Categories!.Count);
                        Assert.Equal(binned.EnumerateArray().Sum(c => c.GetInt32()), (int)s.Values.Numbers.Sum(v => v ?? 0));
                    }

                    break;
                }

                case "boxWhisker":
                {
                    var s = series[k];
                    var boxes = entry.GetProperty("boxes");
                    Assert.Equal(boxes.EnumerateObject().Count(), s.Boxes!.Count);
                    foreach (var box in s.Boxes)
                    {
                        var e = boxes.GetProperty(s.Categories!.Texts![box.Category]!);
                        Near(e.GetProperty("q1").GetDouble(), box.Q1);
                        Near(e.GetProperty("median").GetDouble(), box.Median);
                        Near(e.GetProperty("q3").GetDouble(), box.Q3);
                        Near(e.GetProperty("mean").GetDouble(), box.Mean);
                        Near(e.GetProperty("low").GetDouble(), box.WhiskerLow);
                        Near(e.GetProperty("high").GetDouble(), box.WhiskerHigh);
                    }

                    break;
                }

                case "funnel":
                    Near([.. Numbers(entry.GetProperty("values")).Select(v => v is { } x ? Math.Abs(x) : (double?)null)], series[k].Values.Numbers);
                    break;
                case "treemap" or "sunburst":
                {
                    var root = series[k].Hierarchy!;
                    var top = entry.GetProperty("top").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
                    Assert.Equal(top.Keys.Order(), root.Children.Select(c => c.Name).Order());
                    foreach (var child in root.Children)
                    {
                        Near(top[child.Name], child.Value);
                    }

                    Near(entry.GetProperty("total").GetDouble(), root.Value);
                    AssertLayouts(root);
                    break;
                }
            }
        }
    }

    /// <summary>A treemap's boxes have areas, and a sunburst's segments sweeps, in proportion to their values.</summary>
    private static void AssertLayouts(ChartNode root)
    {
        var boxes = ChartRenderer.TreemapLayout([.. root.Children.Select(c => c.Value)], 10, 20, 300, 200);
        for (var i = 0; i < boxes.Count; i++)
        {
            Assert.Equal(root.Children[i].Value / root.Value, boxes[i].Width * boxes[i].Height / (300 * 200), 9);
            Assert.InRange(boxes[i].X, 10 - 1e-9, 310 + 1e-9);
            Assert.InRange(boxes[i].Y + boxes[i].Height, 20, 220 + 1e-9);
        }

        var segments = ChartRenderer.SunburstSegments(root);
        foreach (var (node, level, _, sweep, _) in segments.Where(s => s.Level == 0))
        {
            Assert.Equal(360 * node.Value / root.Value, sweep, 9);
        }

        Assert.Equal(360, segments.Where(s => s.Level == 0).Sum(s => s.Sweep), 9);
    }

    // ---- The statistics ----

    [Fact]
    public void Quartiles_follow_the_files_quartile_method()
    {
        double[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        var exclusive = ChartReader.Statistics(0, [.. values], inclusive: false);
        Assert.Equal((2.5, 5, 7.5), (exclusive.Q1, exclusive.Median, exclusive.Q3)); // QUARTILE.EXC
        var inclusive = ChartReader.Statistics(0, [.. values], inclusive: true);
        Assert.Equal((3, 5, 7), (inclusive.Q1, inclusive.Median, inclusive.Q3)); // QUARTILE.INC
        Assert.Equal(5, inclusive.Mean);

        // An outlier is beyond the whisker, which ends at the furthest value within 1.5 interquartile ranges.
        var outlier = ChartReader.Statistics(0, [1, 2, 3, 4, 100], inclusive: true);
        Assert.Equal((1, 4), (outlier.WhiskerLow, outlier.WhiskerHigh));
        Assert.Equal([1, 2, 3, 4, 100], outlier.Values);

        // One value: everything is that value.
        var single = ChartReader.Statistics(0, [7], inclusive: false);
        Assert.Equal((7, 7, 7, 7, 7), (single.Q1, single.Median, single.Q3, single.WhiskerLow, single.WhiskerHigh));
    }

    [Fact]
    public void Histograms_bin_by_count_width_or_scotts_rule_with_under_and_overflow()
    {
        double[] values = [1, 2, 2, 3, 4, 5, 9];
        Assert.Equal([("[1, 3]", 4), ("(3, 5]", 2), ("(5, 7]", 0), ("(7, 9]", 1)], ChartReader.Bins(values, new ChartReader.Bin(Size: 2), null));
        Assert.Equal([("[1, 5)", 5), ("[5, 9]", 2)], ChartReader.Bins(values, new ChartReader.Bin(ClosedRight: false, Count: 2), null));
        Assert.Equal([("\u22642", 3), ("(2, 4]", 2), ("(4, 6]", 1), (">6", 1)], ChartReader.Bins(values, new ChartReader.Bin(Size: 2, Underflow: 2, Overflow: 6), null));

        // Scott's rule: 3.5 standard deviations over the cube root of the count.
        var sd = Math.Sqrt(values.Sum(v => Math.Pow(v - values.Average(), 2)) / (values.Length - 1));
        var width = 3.5 * sd / Math.Cbrt(values.Length);
        var bins = ChartReader.Bins(values, new ChartReader.Bin(), null);
        Assert.Equal((int)Math.Ceiling(8 / width), bins.Count);
        Assert.Equal(values.Length, bins.Sum(b => b.Count));

        // All equal: one bin; none: no bins; a bin count beyond the limit is cut to it.
        Assert.Equal([("[4, 4]", 3)], ChartReader.Bins([4, 4, 4], new ChartReader.Bin(), null));
        Assert.Empty(ChartReader.Bins([], new ChartReader.Bin(), null));
        Assert.Equal(ChartReader.MaxBins, ChartReader.Bins([0, 1e9], new ChartReader.Bin(Size: 1), null).Count);
    }

    [Fact]
    public void Waterfalls_run_on_from_each_bar_and_totals_stand_on_the_axis()
    {
        var values = new ChartData([100, 20, -15, 105, -5], null, null);
        Assert.Equal([0, 100, 120, 0, 105], ChartReader.WaterfallStarts(values, new HashSet<int> { 3 }));
        Assert.Equal([0.75, 1], ChartReader.ParetoShares(new ChartData([3, 1], null, null)));
    }

    // ---- Hand-made parts: what the file asks for, and hostile input ----

    private static string Part(string data, string series, string axes = "", string extra = "") =>
        $"<cx:chartSpace xmlns:cx=\"{Cx}\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><cx:chartData>{data}</cx:chartData><cx:chart>{extra}<cx:plotArea><cx:plotAreaRegion>{series}</cx:plotAreaRegion>{axes}</cx:plotArea></cx:chart></cx:chartSpace>";

    private static string Numbers(params double[] values) =>
        $"<cx:numDim type=\"val\"><cx:lvl ptCount=\"{values.Length}\">{string.Concat(values.Select((v, i) => $"<cx:pt idx=\"{i}\">{v.ToString(CultureInfo.InvariantCulture)}</cx:pt>"))}</cx:lvl></cx:numDim>";

    private static Chart? ReadPart(string xml, ResourceLimits? limits = null, List<string>? unsupported = null) =>
        ChartReader.Read(XElement.Parse(xml), null, limits ?? ResourceLimits.Default, unsupported);

    [Fact]
    public void Waterfalls_have_their_colors_legend_connectors_and_labels()
    {
        var chart = ReadPart(Part($"<cx:data id=\"0\">{Numbers(10, -4, 6)}</cx:data>",
            "<cx:series layoutId=\"waterfall\"><cx:dataLabels pos=\"outEnd\"><cx:visibility value=\"1\"/></cx:dataLabels><cx:dataId val=\"0\"/>"
            + "<cx:layoutPr><cx:visibility connectorLines=\"1\"/><cx:subtotals><cx:idx val=\"2\"/></cx:subtotals></cx:layoutPr></cx:series>",
            "<cx:axis id=\"0\"><cx:catScaling gapWidth=\"0.8\"/><cx:tickLabels/></cx:axis><cx:axis id=\"1\"><cx:valScaling max=\"20\"/><cx:majorGridlines/><cx:tickLabels/></cx:axis>",
            "<cx:title><cx:tx><cx:txData><cx:v>Flow</cx:v></cx:txData></cx:tx></cx:title>") + string.Empty)!;
        var group = Assert.Single(chart.PlotArea.Groups);
        Assert.True(group.Connectors);
        Assert.Equal(80, group.GapWidth);
        var series = Assert.Single(group.Series);
        Assert.Equal(["Increase", "Decrease", "Total"], series.LegendKeys!.Select(k => k.Text));
        Assert.Equal([chart.Accents[0], chart.Accents[1], chart.Accents[2]], [.. Enumerable.Range(0, 3).Select(i => series.Points[i].Shape!.Fill!.Color!.Value)]);
        Assert.True(series.Labels!.ShowValue);
        Assert.Equal("Flow", chart.Title!.Text);
        var valueAxis = chart.PlotArea.Axes.Single(a => a.Id == 1);
        Assert.Equal((20, false), (valueAxis.Max!.Value, valueAxis.Deleted));
        Assert.Equal(TickLabelPosition.Low, chart.PlotArea.Axes.Single(a => a.Id == 0).Labels);
    }

    [Fact]
    public void Waterfall_bars_float_on_the_running_total_and_funnel_bars_are_centered()
    {
        // 10 up, 4 down, a total of 6: the decrease hangs from 10 down to 6, the total stands on the axis up to 6.
        var drawing = ChartXml.Draw(Part($"<cx:data id=\"0\">{Numbers(10, -4, 6)}</cx:data>",
            "<cx:series layoutId=\"waterfall\"><cx:dataId val=\"0\"/><cx:layoutPr><cx:subtotals><cx:idx val=\"2\"/></cx:subtotals></cx:layoutPr></cx:series>"));
        var rects = drawing.Rects();
        var up = rects.Single(r => r.Color == ChartXml.Rgb(0x44, 0x72, 0xC4));
        var down = rects.Single(r => r.Color == ChartXml.Rgb(0xED, 0x7D, 0x31));
        var total = rects.Single(r => r.Color == ChartXml.Rgb(0xA5, 0xA5, 0xA5));
        Assert.Equal(up.Y, total.Y, 3); // Both stand on the axis.
        Assert.Equal(0.4, down.Height / up.Height, 3);
        Assert.Equal(0.6, total.Height / up.Height, 3);
        Assert.Equal(total.Y + total.Height, down.Y, 3); // The decrease ends at the total.
        Assert.Equal(up.Y + up.Height, down.Y + down.Height, 3); // It starts where the increase ends.

        // The scale reaches the highest running total: two increases of 10 are two whole bars, the second up to 20.
        var steps = ChartXml.Draw(Part($"<cx:data id=\"0\">{Numbers(10, 10)}</cx:data>", "<cx:series layoutId=\"waterfall\"><cx:dataId val=\"0\"/></cx:series>")).Rects()
            .Where(r => r.Color == ChartXml.Rgb(0x44, 0x72, 0xC4)).OrderBy(r => r.X).ToList();
        Assert.Equal(steps[0].Height, steps[1].Height, 3);
        Assert.Equal(steps[0].Y + steps[0].Height, steps[1].Y, 3);

        var funnel = ChartXml.Draw(Part($"<cx:data id=\"0\">{Numbers(8, 4, 2)}</cx:data>", "<cx:series layoutId=\"funnel\"><cx:dataId val=\"0\"/></cx:series>")).Rects()
            .Where(r => r.Color == ChartXml.Rgb(0x44, 0x72, 0xC4)).OrderByDescending(r => r.Width).ToList();
        Assert.Equal(3, funnel.Count);
        Assert.All(funnel, r => Assert.Equal(funnel[0].X + (funnel[0].Width / 2), r.X + (r.Width / 2), 3));
        Assert.Equal([1, 0.5, 0.25], funnel.Select(r => Math.Round(r.Width / funnel[0].Width, 3)));
        Assert.True(funnel[0].Y > funnel[2].Y); // The widest at the top.
    }

    [Fact]
    public void Hierarchies_repeat_blank_outer_levels_and_end_at_blank_inner_ones()
    {
        // Levels in the file: the innermost first.
        var data = "<cx:data id=\"0\"><cx:strDim type=\"cat\"><cx:lvl ptCount=\"4\"><cx:pt idx=\"0\">a1</cx:pt><cx:pt idx=\"1\">a2</cx:pt><cx:pt idx=\"2\">b1</cx:pt></cx:lvl>"
            + "<cx:lvl ptCount=\"4\"><cx:pt idx=\"0\">A</cx:pt><cx:pt idx=\"2\">B</cx:pt><cx:pt idx=\"3\">C</cx:pt></cx:lvl></cx:strDim>"
            + "<cx:numDim type=\"size\"><cx:lvl ptCount=\"4\"><cx:pt idx=\"0\">1</cx:pt><cx:pt idx=\"1\">2</cx:pt><cx:pt idx=\"2\">3</cx:pt><cx:pt idx=\"3\">4</cx:pt></cx:lvl></cx:numDim></cx:data>";
        var chart = ReadPart(Part(data, "<cx:series layoutId=\"treemap\"><cx:dataId val=\"0\"/><cx:layoutPr><cx:parentLabelLayout val=\"none\"/></cx:layoutPr></cx:series>"))!;
        var group = Assert.Single(chart.PlotArea.Groups);
        Assert.Equal((ChartKind.Treemap, ParentLabels.None), (group.Kind, group.ParentLabels));
        var root = group.Series[0].Hierarchy!;
        Assert.Equal(["A", "B", "C"], root.Children.Select(c => c.Name));
        Assert.Equal([3, 3, 4], root.Children.Select(c => c.Value));
        Assert.Equal(["a1", "a2"], root.Children[0].Children.Select(c => c.Name));
        Assert.Empty(root.Children[2].Children); // C has no inner level: a leaf of the top level.
        Assert.Equal(3, root.Children[2].Point);
        Assert.Equal(["A", "B", "C"], group.Series[0].LegendKeys!.Select(k => k.Text));
    }

    [Fact]
    public void Region_maps_and_unknown_layouts_are_left_out_and_hidden_series_skipped()
    {
        var unsupported = new List<string>();
        var chart = ReadPart(Part($"<cx:data id=\"0\">{Numbers(1)}</cx:data>",
            "<cx:series layoutId=\"regionMap\"><cx:dataId val=\"0\"/></cx:series><cx:series layoutId=\"spiral\"><cx:dataId val=\"0\"/></cx:series>"
            + "<cx:series layoutId=\"funnel\" hidden=\"1\"><cx:dataId val=\"0\"/></cx:series>"), unsupported: unsupported)!;
        Assert.Empty(chart.PlotArea.Groups);
        Assert.Equal(["region map", "spiral"], unsupported);

        // Without a plot area, or not a chart space: nothing.
        Assert.Null(ReadPart($"<cx:chartSpace xmlns:cx=\"{Cx}\"><cx:chart/></cx:chartSpace>"));
    }

    [Fact]
    public void Hostile_parts_are_bounded_by_the_limits()
    {
        // A point count is believed only so far, then counted against MaxTableCells.
        var huge = $"<cx:data id=\"0\"><cx:numDim type=\"val\"><cx:lvl ptCount=\"2000000000\"><cx:pt idx=\"0\">1</cx:pt></cx:lvl></cx:numDim></cx:data>";
        var chart = ReadPart(Part(huge, "<cx:series layoutId=\"funnel\"><cx:dataId val=\"0\"/></cx:series>"))!;
        Assert.Equal(ChartReader.TrustedPointCount, chart.PlotArea.Groups[0].Series[0].Values.Count);
        var error = Assert.Throws<ResourceLimitExceededException>(() => ReadPart(Part(huge, "<cx:series layoutId=\"funnel\"><cx:dataId val=\"0\"/></cx:series>"),
            ResourceLimits.Default with { MaxTableCells = 100 }));
        Assert.Equal("MaxTableCells", error.LimitName);

        // Points beyond the series' bound, negative and malformed indexes and values are left out.
        var odd = "<cx:data id=\"0\"><cx:numDim type=\"val\"><cx:lvl><cx:pt idx=\"-1\">5</cx:pt><cx:pt idx=\"x\">5</cx:pt><cx:pt idx=\"99999999\">5</cx:pt><cx:pt idx=\"1\">NaN</cx:pt><cx:pt idx=\"2\">1e400</cx:pt><cx:pt idx=\"3\">2</cx:pt></cx:lvl></cx:numDim></cx:data>";
        var values = ReadPart(Part(odd, "<cx:series layoutId=\"waterfall\"><cx:dataId val=\"0\"/></cx:series>"))!.PlotArea.Groups[0].Series[0].Values;
        Assert.Equal([null, null, null, 2], values.Numbers);

        // Deep hierarchies are cut at MaxLevels; a huge bin count at MaxBins.
        var levels = string.Concat(Enumerable.Range(0, 100).Select(i => $"<cx:lvl ptCount=\"1\"><cx:pt idx=\"0\">L{i}</cx:pt></cx:lvl>"));
        var deep = ReadPart(Part($"<cx:data id=\"0\"><cx:strDim type=\"cat\">{levels}</cx:strDim><cx:numDim type=\"size\"><cx:lvl ptCount=\"1\"><cx:pt idx=\"0\">1</cx:pt></cx:lvl></cx:numDim></cx:data>",
            "<cx:series layoutId=\"sunburst\"><cx:dataId val=\"0\"/></cx:series>"))!;
        var depth = 0;
        for (var node = deep.PlotArea.Groups[0].Series[0].Hierarchy!; node.Children.Count > 0; node = node.Children[0])
        {
            depth++;
        }

        Assert.Equal(ChartReader.MaxLevels, depth);
        var bins = ReadPart(Part($"<cx:data id=\"0\">{Numbers(0, 1e12)}</cx:data>",
            "<cx:series layoutId=\"clusteredColumn\"><cx:dataId val=\"0\"/><cx:layoutPr><cx:binning><cx:binCount val=\"1e15\"/></cx:binning></cx:layoutPr></cx:series>"))!;
        Assert.Equal(ChartReader.MaxBins, bins.PlotArea.Groups[0].Series[0].Values.Count);

        // A drawing of every kind with no data draws nothing and does not fail.
        foreach (var layout in new[] { "waterfall", "clusteredColumn", "funnel", "boxWhisker", "treemap", "sunburst" })
        {
            var empty = ReadPart(Part(string.Empty, $"<cx:series layoutId=\"{layout}\"><cx:dataId val=\"7\"/></cx:series>"))!;
            var pdf = new PdfDocument();
            var page = pdf.AddPage();
            ChartRenderer.Draw(page.Canvas, empty, 0, 0, 300, 200, new ChartRenderOptions { Fonts = (_, _, _) => [RaiCore.Pdf.Fonts.BundledFonts.CreateCollection().Find("Liberation Sans", 400)!] });
            Assert.NotEmpty(pdf.ToArray());
        }
    }

    [Fact]
    public void Workbook_cells_are_read_for_charts_without_data_within_the_limits()
    {
        var bytes = Fixtures.Read("Charts/ChartEx/treemap.xlsx");
        using var package = XlsxPackage.Open(bytes, ResourceLimits.Default);
        var workbook = XlsxWorkbook.Read(package, CultureInfo.InvariantCulture);
        var cells = new XlsxChartCells(package, workbook, ResourceLimits.Default);
        var levels = cells.Resolve("_xlchart.v1.0")!;
        Assert.Equal(3, levels.Columns);
        Assert.Equal(["Best", "First", "A"], levels.Values.Take(3));
        Assert.Equal([3.0, 4.0], cells.Resolve("Sheet1!$E$4:$E$5")!.Values);
        Assert.Null(cells.Resolve("_xlchart.v9.9"));
        Assert.Null(cells.Resolve("Missing!$A$1"));
        Assert.Null(cells.Resolve("#REF!"));

        var small = new XlsxChartCells(package, workbook, ResourceLimits.Default with { MaxTableCells = 10 });
        Assert.Equal("MaxTableCells", Assert.Throws<ResourceLimitExceededException>(() => small.Resolve("Sheet1!$A$1:$Z$100")).LimitName);
    }

    // ---- The converters ----

    [Theory]
    [InlineData("waterfall.xlsx", new[] { "Waterfall", "Increase", "Decrease", "Total" })]
    [InlineData("SimpleHistogram.xlsx", new[] { "Chart Title", "[10," })]
    [InlineData("paretoLine.xlsx", new[] { "ParetoLine", "100%" })]
    [InlineData("boxWhisker.xlsx", new[] { "Chart Title" })]
    [InlineData("funnel1.xlsx", new[] { "Chart Title", "12" })]
    [InlineData("treemap.xlsx", new[] { "Best", "Worst", "Fourth" })]
    [InlineData("sunburst.xlsx", new[] { "Best", "Second" })]
    public void Workbook_charts_are_drawn(string file, string[] texts)
    {
        var diagnostics = new List<SheetDiagnostic>();
        var pdf = XlsxToPdf.ConvertToBytes(Fixtures.Read("Charts/ChartEx/" + file), new XlsxConverterOptions { Sheet = new SheetOptions { Diagnostics = diagnostics.Add } });
        Assert.DoesNotContain(diagnostics, d => d.Kind == SheetDiagnosticKind.Chart);
        var text = string.Concat(PageTexts(pdf));
        foreach (var expected in texts)
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Document_charts_are_drawn_and_region_maps_show_their_stored_picture()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var data = Fixtures.Read("Charts/ChartEx/chartex.docx");
        var images = Paragraphs(Read(data, diagnostics: diagnostics)).SelectMany(p => p.Inlines).OfType<ImageInline>().ToList();
        Assert.Equal(9, images.Count);
        Assert.Equal(8, images.Count(i => i.Chart is not null));
        Assert.Null(images[^1].Chart); // The region map: its picture.
        Assert.Equal(["A chart of a newer type (such as a waterfall or treemap chart) is shown as its stored picture."], diagnostics.Select(d => d.Message));
        var text = string.Concat(PageTexts(DocxToPdf.ConvertToBytes(data)));
        foreach (var expected in new[] { "Cash flow", "Increase", "Scores in five bins", "Damaged", "Response times", "Visits", "Renewals", "Harvest by crop", "Vegetables", "Carrots" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Presentation_charts_are_drawn_and_region_maps_show_their_stored_picture()
    {
        var reports = new List<SlideDiagnostic>();
        var presentation = PptxReaderTests.Read(Fixtures.Read("Charts/ChartEx/chartex.pptx"), new PptxConverterOptions { Diagnostics = reports.Add });
        var kinds = presentation.Slides.Take(8).Select(s => Assert.Single(s.Shapes.OfType<ChartFrame>()).Chart.PlotArea.Groups[0].Kind).ToList();
        Assert.Equal([ChartKind.Bar, ChartKind.Bar, ChartKind.Bar, ChartKind.Bar, ChartKind.BoxWhisker, ChartKind.Bar, ChartKind.Treemap, ChartKind.Sunburst], kinds);
        Assert.Empty(presentation.Slides[8].Shapes.OfType<ChartFrame>());
        Assert.Equal([new SlideDiagnostic(9, "chart", "A chart of a newer type (such as a waterfall or treemap chart) is drawn as its stored picture.")], reports);

        // PowerPoint's own funnel (with hidden series) is drawn too.
        var text = string.Concat(PageTexts(PptxToPdf.ConvertToBytes(Fixtures.Read("Charts/ChartEx/funnel-pp1.pptx"))));
        foreach (var expected in new[] { "Thing 1", "Thing 4", "4.3", "Legend Entry 1" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Series 2", text, StringComparison.Ordinal);
    }
}
