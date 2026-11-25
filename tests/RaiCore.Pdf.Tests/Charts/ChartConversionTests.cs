using System.Text.Json;
using RaiCore.Pdf.Documents;
using RaiCore.Pdf.Documents.Model;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Slides;
using RaiCore.Pdf.Slides.Model;
using RaiCore.Pdf.Tests.Slides;
using static RaiCore.Pdf.Tests.WordDocuments.Docx;

namespace RaiCore.Pdf.Tests.Charts;

/// <summary>
/// Charts in presentations and documents: the corpus written by python-pptx and python-docx (Fixtures/Charts, with
/// what each chart shows in expected.json) and Apache POI's sample charts, drawn with their titles, categories, legend
/// entries and labels as text, and the diagnostics for what is not drawn.
/// </summary>
public class ChartConversionTests
{
    private static readonly JsonElement Expected = JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Charts/expected.json"))).RootElement;

    private static string[] PageTexts(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    [Fact]
    public void Presentation_charts_are_drawn_with_their_text()
    {
        var reports = new List<SlideDiagnostic>();
        var pdf = PptxToPdf.ConvertToBytes(Fixtures.Read("Charts/charts.pptx"), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Empty(reports);
        var pages = PageTexts(pdf);
        var charts = Expected.GetProperty("pptx").EnumerateArray().ToList();
        Assert.Equal(charts.Count, pages.Length);
        for (var i = 0; i < charts.Count; i++)
        {
            var chart = charts[i];
            var text = pages[i];
            Assert.Contains(chart.GetProperty("title").GetString()!, text, StringComparison.Ordinal);
            foreach (var category in chart.GetProperty("categories").EnumerateArray())
            {
                Assert.Contains(category.GetString()!, text, StringComparison.Ordinal);
            }

            // Series names are in the legend, except for pies and doughnuts, whose legend lists their categories.
            if (!chart.GetProperty("type").GetString()!.Contains("PIE", StringComparison.Ordinal) && chart.GetProperty("type").GetString() != "DOUGHNUT")
            {
                foreach (var series in chart.GetProperty("series").EnumerateArray())
                {
                    Assert.Contains(series.GetProperty("name").GetString()!, text, StringComparison.Ordinal);
                }
            }
        }

        // Data labels: the clustered columns show their values, the pie its percentages.
        Assert.Contains("40", pages[0], StringComparison.Ordinal);
        Assert.Contains("50%", pages[6], StringComparison.Ordinal);
        Assert.Contains("10%", pages[6], StringComparison.Ordinal);

        // Axes: 10 to 40 on a large chart is 0 to 45 by 5, as in Excel.
        Assert.Contains("45", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("50", pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Presentation_charts_are_read_into_chart_frames()
    {
        var presentation = PptxReaderTests.Read(Fixtures.Read("Charts/charts.pptx"));
        var frame = Assert.IsType<ChartFrame>(Assert.Single(presentation.Slides[0].Shapes));
        Assert.Equal((36, 36, 648, 468), (frame.Box.X, frame.Box.Y, frame.Box.Width, frame.Box.Height));
        Assert.Equal("Column Clustered", frame.Chart.Title!.Text);
        Assert.Equal(12, frame.Chart.Text.Size);
        Assert.Equal("Calibri", frame.MinorFont);
        var kinds = presentation.Slides.Select(s => ((ChartFrame)s.Shapes[0]).Chart.PlotArea.Groups[0].Kind).ToList();
        Assert.Equal(
            [Pdf.Charts.ChartKind.Bar, Pdf.Charts.ChartKind.Bar, Pdf.Charts.ChartKind.Bar, Pdf.Charts.ChartKind.Bar, Pdf.Charts.ChartKind.Line, Pdf.Charts.ChartKind.Line,
                Pdf.Charts.ChartKind.Pie, Pdf.Charts.ChartKind.Pie, Pdf.Charts.ChartKind.Doughnut, Pdf.Charts.ChartKind.Area, Pdf.Charts.ChartKind.Area,
                Pdf.Charts.ChartKind.Radar, Pdf.Charts.ChartKind.Scatter, Pdf.Charts.ChartKind.Bubble],
            kinds);
    }

    [Theory]
    [InlineData("bar-chart.pptx", "My Bar Chart", new[] { "Sales", "1st Qtr", "4th Qtr", "9" })]
    [InlineData("line-chart.pptx", "My Line Chart", new[] { "Sales", "2nd Qtr", "10" })]
    [InlineData("pie-chart.pptx", "My Pie Chart", new[] { "Sales", "3rd Qtr", "4th Qtr" })]
    [InlineData("radar-chart.pptx", "My Radar Chart", new[] { "Sales", "1st Qtr" })]
    [InlineData("scatter-chart.pptx", "My Scatter Chart", new[] { "Sales", "8" })]
    public void Apache_POI_sample_charts_are_drawn(string file, string slideTitle, string[] texts)
    {
        var reports = new List<SlideDiagnostic>();
        var pdf = PptxToPdf.ConvertToBytes(Fixtures.Read("Poi/" + file), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.DoesNotContain(reports, r => r.Subject == "chart");
        var text = Assert.Single(PageTexts(pdf));
        Assert.Contains(slideTitle, text, StringComparison.Ordinal);
        foreach (var expected in texts)
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tagged_presentations_tag_charts_as_figures()
    {
        var pdf = PptxToPdf.ConvertToBytes(Fixtures.Read("Poi/bar-chart.pptx"), new PptxConverterOptions { Tagged = true });
        var reader = PdfReader.Open(pdf);
        Assert.Contains("Chart: Sales", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        Assert.NotEmpty(new TextExtractor(reader).ExtractPages().First().Text);
    }

    private static string Frame(string inner) =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"Chart\" descr=\"Sales chart\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>"
        + $"<p:xfrm><a:off x=\"{Deck.Emu(50)}\" y=\"{Deck.Emu(50)}\"/><a:ext cx=\"{Deck.Emu(400)}\" cy=\"{Deck.Emu(300)}\"/></p:xfrm>"
        + $"<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">{inner}</a:graphicData></a:graphic></p:graphicFrame>";

    private static readonly string ChartReference = $"<c:chart xmlns:c=\"{ChartXml.C}\" r:id=\"rIdChart\"/>";

    private static Deck DeckWithChart(string chartXml) => new Deck
    {
        SlideRelationships = $"<Relationship Id=\"rIdChart\" Type=\"{Deck.R}/chart\" Target=\"../charts/chart1.xml\"/>",
        Parts = { ("ppt/charts/chart1.xml", System.Text.Encoding.UTF8.GetBytes(chartXml)) },
    }.With(Frame(ChartReference));

    [Fact]
    public void Presentation_charts_use_the_slide_theme_and_report_what_is_not_drawn()
    {
        var themed = DeckWithChart(ChartXml.Bars(ChartXml.Series(0, "North", ["a", "b"], [1, 2]), title: ChartXml.Title("Themed")));
        themed = new Deck
        {
            Theme = "<a:clrScheme name=\"T\"><a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:accent1><a:srgbClr val=\"FF0000\"/></a:accent1></a:clrScheme>",
            SlideRelationships = themed.SlideRelationships,
            Parts = { themed.Parts[0] },
        }.With(Frame(ChartReference));
        var slide = Assert.Single(PptxReaderTests.Read(themed.Build()).Slides);
        var frame = Assert.IsType<ChartFrame>(Assert.Single(slide.Shapes));
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), frame.Chart.PlotArea.Groups[0].Series[0].AutoColor);
        Assert.Equal("Sales chart", frame.AltText);

        // A missing part, a damaged part, a chart of a type the engine cannot draw: reported, not drawn, no exception.
        var reports = new List<SlideDiagnostic>();
        var missing = Deck.Of(Frame(ChartReference)).Build();
        PptxToPdf.ConvertToBytes(missing, new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Contains(new SlideDiagnostic(1, "chart", "A chart without its chart part is not drawn."), reports);

        reports.Clear();
        PptxToPdf.ConvertToBytes(DeckWithChart("<c:chartSpace><unclosed>").Build(), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Contains(reports, r => r.Subject == "chart" && r.Message.StartsWith("The chart part 'ppt/charts/chart1.xml' is damaged", StringComparison.Ordinal));

        reports.Clear();
        var surface = ChartXml.Space(ChartXml.Group("surfaceChart", string.Empty, ChartXml.Series(0, "S", null, [1])));
        PptxToPdf.ConvertToBytes(DeckWithChart(surface).Build(), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Equal([new SlideDiagnostic(1, "chart", "A surface chart is not drawn.")], reports);

        reports.Clear();
        var flat = ChartXml.Space(ChartXml.Group("bar3DChart", "<c:barDir val=\"col\"/>", ChartXml.Series(0, "S", null, [1])));
        PptxToPdf.ConvertToBytes(DeckWithChart(flat).Build(), new PptxConverterOptions { Diagnostics = reports.Add });
        Assert.Equal([new SlideDiagnostic(1, "chart", "A 3-D chart is drawn flat.")], reports);
    }

    [Fact]
    public void Newer_presentation_chart_types_show_their_stored_picture_and_are_reported()
    {
        var chartEx = "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"cx1\">"
            + "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"3\" name=\"C\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/></p:xfrm>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/drawing/2014/chartex\"/></a:graphic></p:graphicFrame></mc:Choice>"
            + "<mc:Fallback>" + Deck.Shape(0, 0, 100, 100, Deck.Para("Stored picture")) + "</mc:Fallback></mc:AlternateContent>";
        var reports = new List<SlideDiagnostic>();
        var slide = Assert.Single(PptxReaderTests.Read(Deck.Of(chartEx).Build(), new PptxConverterOptions { Diagnostics = reports.Add }).Slides);
        Assert.IsType<AutoShape>(Assert.Single(slide.Shapes));
        Assert.Equal([new SlideDiagnostic(1, "chart", "A chart of a newer type (such as a waterfall or treemap chart) is drawn as its stored picture.")], reports);
    }

    [Fact]
    public void Presentation_chart_data_is_bounded_by_the_limits()
    {
        var many = string.Concat(Enumerable.Range(0, 4).Select(i => ChartXml.Series(i, "S", null, [.. Enumerable.Repeat((double?)1, 30)])));
        var error = Assert.Throws<ResourceLimitExceededException>(() => PptxToPdf.ConvertToBytes(DeckWithChart(ChartXml.Bars(many)).Build(),
            new PptxConverterOptions { Limits = ResourceLimits.Default with { MaxTableCells = 100 } }));
        Assert.Equal("MaxTableCells", error.LimitName);
    }

    [Fact]
    public void Document_charts_are_drawn_as_images_of_their_size()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var data = Fixtures.Read("Charts/charts.docx");
        var document = Read(data, diagnostics: diagnostics);
        var charts = Paragraphs(document).SelectMany(p => p.Inlines).OfType<ImageInline>().Where(i => i.Chart is not null).ToList();
        Assert.Equal(3, charts.Count);
        Assert.All(charts, c => Assert.Equal((432, 252), (c.Width, c.Height)));
        Assert.Equal(["Quarterly sales", "Share of fruit", "Monthly visitors"], charts.Select(c => c.Chart!.Chart.Title!.Text));
        Assert.Equal("Quarterly sales", charts[0].AlternativeText);
        Assert.Empty(diagnostics);

        var text = AllText(DocxToPdf.ConvertToBytes(data));
        foreach (var chart in Expected.GetProperty("docx").EnumerateArray())
        {
            Assert.Contains(chart.GetProperty("title").GetString()!, text, StringComparison.Ordinal);

            // A pie without a legend or labels shows no category names.
            foreach (var category in chart.GetProperty("type").GetString() == "PIE" ? [] : chart.GetProperty("categories").EnumerateArray().ToArray())
            {
                Assert.Contains(category.GetString()!, text, StringComparison.Ordinal);
            }
        }

        // The charts are vector images: no raster image in the PDF.
        var images = new ImageExtractor(PdfReader.Open(DocxToPdf.ConvertToBytes(data))).ExtractPages().SelectMany(p => p.Images).ToList();
        Assert.Empty(images);
    }

    [Fact]
    public void Newer_document_chart_types_are_drawn()
    {
        var diagnostics = new List<DocumentDiagnostic>();
        var data = Fixtures.Read("Poi/chartex.docx");
        var document = Read(data, diagnostics: diagnostics);
        var images = Paragraphs(document).SelectMany(p => p.Inlines).OfType<ImageInline>().ToList();
        Assert.Equal(6, images.Count(i => i.Chart is not null)); // Columns, lines, radar, stock, and Word's box and whisker and sunburst charts.
        Assert.Empty(diagnostics);
        var kinds = images.Where(i => i.Chart is not null).Select(i => i.Chart!.Chart.PlotArea.Groups[0].Kind).ToList();
        Assert.Equal([Pdf.Charts.ChartKind.Bar, Pdf.Charts.ChartKind.Line, Pdf.Charts.ChartKind.Radar, Pdf.Charts.ChartKind.BoxWhisker, Pdf.Charts.ChartKind.Stock, Pdf.Charts.ChartKind.Sunburst], kinds);

        var text = AllText(DocxToPdf.ConvertToBytes(data));
        foreach (var expected in new[] { "my chart looks nice", "This is a line chart I just made", "This is a radar chart", "This is a stock chart", "Category 4", "1/9/2002", "this is a box and whisker chart", "this is a sunburst chart", "Branch 1" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Document_charts_report_missing_and_damaged_parts()
    {
        static string Drawing(string id) =>
            $"<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"457200\"/><wp:docPr id=\"1\" name=\"c\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">"
            + $"<c:chart xmlns:c=\"{ChartXml.C}\" r:id=\"{id}\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
        var diagnostics = new List<DocumentDiagnostic>();
        var docx = Document(Drawing("rMissing") + Drawing("rDamaged") + Drawing("rSurface") + Drawing("rGood"),
            parts:
            [
                ("word/charts/damaged.xml", "<c:chartSpace><oops>"),
                ("word/charts/surface.xml", ChartXml.Space(ChartXml.Group("surfaceChart", string.Empty, ChartXml.Series(0, "S", null, [1])))),
                ("word/charts/good.xml", ChartXml.Bars(ChartXml.Series(0, "Good", ["a"], [1]), title: ChartXml.Title("Good chart"))),
            ],
            relationships: [("rDamaged", "chart", "charts/damaged.xml", false), ("rSurface", "chart", "charts/surface.xml", false), ("rGood", "chart", "charts/good.xml", false)]);
        var document = Read(docx, diagnostics: diagnostics);
        var chart = Assert.Single(Paragraphs(document).SelectMany(p => p.Inlines).OfType<ImageInline>());
        Assert.Equal(("Chart: Good chart", 72.0, 36.0), (chart.AlternativeText, chart.Width, chart.Height));
        Assert.Contains(diagnostics, d => d.Message == "A chart without its chart part is left out.");
        Assert.Contains(diagnostics, d => d.Message.StartsWith("The chart part 'word/charts/damaged.xml' is damaged", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Message == "A surface chart is not drawn.");
        Assert.Contains("Good", AllText(DocxToPdf.ConvertToBytes(docx)), StringComparison.Ordinal); // Wrapped in the small chart.
    }
}
