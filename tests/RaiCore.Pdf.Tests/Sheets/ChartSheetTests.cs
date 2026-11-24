using System.Text;
using System.Text.Json;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Sheets;
using RaiCore.Pdf.Sheets.Drawings;
using RaiCore.Pdf.Sheets.Xlsx;
using RaiCore.Pdf.Tests.Fuzzing;

namespace RaiCore.Pdf.Tests.Sheets;

/// <summary>
/// Chart sheets of workbooks: each on a page of its own in the workbook's sheet order, its chart stretched over the
/// page's content area in landscape orientation (unless its page setup asks for portrait), with its headers and footers;
/// the Apache POI corpus against the sheet order and chart sheets openpyxl reads (expected.json).
/// </summary>
public class ChartSheetTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Package = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>The margins of the default page (1.5 cm), in points.</summary>
    private const double Margin = 1.5 / 2.54 * 72;

    private static readonly Lazy<JsonElement> ExpectedJson = new(() =>
        JsonDocument.Parse(File.ReadAllText(Fixtures.Path("Sheets/Drawings/expected.json"))).RootElement);

    public static TheoryData<string> Corpus => [.. ExpectedJson.Value.EnumerateObject().Select(p => p.Name)
        .Where(n => n.EndsWith(".xlsx", StringComparison.Ordinal) && ExpectedJson.Value.GetProperty(n).GetProperty("openpyxl").GetProperty("chartsheets").GetArrayLength() > 0)];

    private static string Picture(string id = "rId1") =>
        $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"{id}\"/><a:stretch/></xdr:blipFill><xdr:spPr/></xdr:pic>";

    private const string Chart = "<xdr:graphicFrame><xdr:nvGraphicFramePr><xdr:cNvPr id=\"4\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm/>"
        + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\"><c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId2\"/></a:graphicData></a:graphic></xdr:graphicFrame>";

    private static string Absolute(string content, long x, long y, long cx, long cy) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{x}\" y=\"{y}\"/><xdr:ext cx=\"{cx}\" cy=\"{cy}\"/>{content}<xdr:clientData/></xdr:absoluteAnchor>";

    /// <summary>
    /// A workbook of the worksheets <c>First</c> and <c>Last</c> with the chart sheet <c>Chart</c> between them, whose
    /// drawing part has <paramref name="anchors"/> (a PNG is <c>rId1</c>, a bar chart <c>rId2</c>) and whose part has
    /// <paramref name="setup"/> before its drawing.
    /// </summary>
    internal static byte[] Workbook(string anchors, string setup = "", bool drawing = true)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] content)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(content);
            }

            void Text(string path, string content) => Add(path, Encoding.UTF8.GetBytes(content));
            Text("_rels/.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Text("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{R}\"><sheets><sheet name=\"First\" sheetId=\"1\" r:id=\"rId1\"/>"
                + "<sheet name=\"Chart\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Last\" sheetId=\"3\" r:id=\"rId3\"/></sheets></workbook>");
            Text("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{R}/chartsheet\" Target=\"chartsheets/sheet1.xml\"/><Relationship Id=\"rId3\" Type=\"{R}/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>");
            Text("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>first sheet</t></is></c></row></sheetData></worksheet>");
            Text("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>last sheet</t></is></c></row></sheetData></worksheet>");
            Text("xl/chartsheets/sheet1.xml", $"<chartsheet xmlns=\"{Main}\" xmlns:r=\"{R}\"><sheetPr/><sheetViews><sheetView workbookViewId=\"0\" zoomToFit=\"1\"/></sheetViews>"
                + $"{setup}{(drawing ? "<drawing r:id=\"rId1\"/>" : string.Empty)}</chartsheet>");
            Text("xl/chartsheets/_rels/sheet1.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/drawing\" Target=\"../drawings/drawing1.xml\"/></Relationships>");
            Text("xl/drawings/drawing1.xml", $"<xdr:wsDr xmlns:xdr=\"{Xdr}\" xmlns:a=\"{A}\" xmlns:r=\"{R}\">{anchors}</xdr:wsDr>");
            Text("xl/drawings/_rels/drawing1.xml.rels", $"<Relationships xmlns=\"{Package}\"><Relationship Id=\"rId1\" Type=\"{R}/image\" Target=\"../media/image1.png\"/>"
                + $"<Relationship Id=\"rId2\" Type=\"{R}/chart\" Target=\"../charts/chart1.xml\"/></Relationships>");
            Add("xl/media/image1.png", FuzzTargets.DrawingInputs.Png);
            Text("xl/charts/chart1.xml", "<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><c:chart>"
                + "<c:title><c:tx><c:rich><a:p><a:r><a:t>Quarterly sales</a:t></a:r></a:p></c:rich></c:tx></c:title><c:plotArea><c:barChart><c:barDir val=\"col\"/>"
                + "<c:ser><c:idx val=\"0\"/><c:val><c:numRef><c:numCache><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>1</c:v></c:pt><c:pt idx=\"1\"><c:v>3</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>"
                + "<c:axId val=\"1\"/><c:axId val=\"2\"/></c:barChart><c:catAx><c:axId val=\"1\"/><c:crossAx val=\"2\"/></c:catAx><c:valAx><c:axId val=\"2\"/><c:crossAx val=\"1\"/></c:valAx></c:plotArea></c:chart></c:chartSpace>");
        }

        return stream.ToArray();
    }

    private static string[] Pages(byte[] pdf) => [.. new TextExtractor(PdfReader.Open(pdf)).ExtractPages().Select(p => p.Text)];

    private static (double Width, double Height) Size(byte[] pdf, int page)
    {
        var p = PdfReader.Open(pdf).Pages[page];
        return (p.Width, p.Height);
    }

    /// <summary>The images on a page with their bounds, top-down (y from the page's top).</summary>
    private static List<(double Left, double Top, double Right, double Bottom)> Images(byte[] pdf, int page)
    {
        var reader = PdfReader.Open(pdf);
        var height = reader.Pages[page].Height;
        return [.. new ImageExtractor(reader).ExtractPage(page).Images
            .Select(i => (i.Bounds.X, height - i.Bounds.Y - i.Bounds.Height, i.Bounds.X + i.Bounds.Width, height - i.Bounds.Y))];
    }

    private static List<SheetDiagnostic> Diagnose(byte[] xlsx)
    {
        var diagnostics = new List<SheetDiagnostic>();
        XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { Sheet = new SheetOptions { Diagnostics = diagnostics.Add } });
        return diagnostics;
    }

    [Fact]
    public void A_chart_sheet_is_a_landscape_page_of_its_own_in_the_sheet_order()
    {
        var xlsx = Workbook(Absolute(Chart, 34018, 34018, 8697232, 6281964));
        var sheets = XlsxConverter.Read(xlsx);
        Assert.Equal(["First", "Chart", "Last"], sheets.Select(s => s.Name));
        var chart = sheets[1];
        Assert.True(chart.Formatting!.ChartSheet && chart.Formatting.Landscape);
        Assert.Empty(chart.Rows);
        Assert.IsType<ChartItem>(Assert.Single(chart.Formatting.Drawings).Item);
        Assert.False(sheets[0].Formatting!.ChartSheet);

        var pdf = XlsxToPdf.ConvertToBytes(xlsx);
        var pages = Pages(pdf);
        Assert.Equal(3, pages.Length);
        Assert.Contains("first sheet", pages[0]);
        Assert.Contains("Quarterly sales", pages[1]);
        Assert.Contains("last sheet", pages[2]);
        Assert.Equal((842, 595), (Math.Round(Size(pdf, 1).Width), Math.Round(Size(pdf, 1).Height)));
        Assert.Equal((595, 842), (Math.Round(Size(pdf, 0).Width), Math.Round(Size(pdf, 0).Height)));

        // Each sheet gets a bookmark, the chart sheet too.
        Assert.Equal(["First", "Chart", "Last"], XlsxToPdf.Convert(xlsx).Outline.Select(o => o.Title));
        Assert.Empty(Diagnose(xlsx));
    }

    [Fact]
    public void Chart_sheets_are_chosen_by_name_and_index_like_worksheets()
    {
        var xlsx = Workbook(Absolute(Chart, 0, 0, 8697232, 6281964));
        Assert.Equal(["Chart"], XlsxConverter.Read(xlsx, new XlsxConverterOptions { SheetNames = ["chart"] }).Select(s => s.Name));
        Assert.Equal(["Chart", "Last"], XlsxConverter.Read(xlsx, new XlsxConverterOptions { SheetIndexes = [2, 1] }).Select(s => s.Name));
        var pdf = XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { SheetIndexes = [1] });
        Assert.Contains("Quarterly sales", Assert.Single(Pages(pdf)));

        // One sheet: no bookmark; a worksheet and a chart sheet: one each.
        Assert.Empty(XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { SheetIndexes = [1] }).Outline);
        Assert.Equal(["First", "Chart"], XlsxToPdf.Convert(xlsx, new XlsxConverterOptions { SheetIndexes = [0, 1] }).Outline.Select(o => o.Title));
    }

    [Fact]
    public void The_drawings_are_stretched_over_the_content_area()
    {
        // A picture anchored at an offset, half as high as wide: it fills the content area of the landscape page.
        var xlsx = Workbook(Absolute(Picture(), 127000, 254000, 6350000, 3175000));
        var pdf = XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { SheetIndexes = [1] });
        var (left, top, right, bottom) = Assert.Single(Images(pdf, 0));
        var (width, height) = Size(pdf, 0);
        Assert.Equal(Margin, left, 0.01);
        Assert.Equal(Margin, top, 0.01);
        Assert.Equal(width - Margin, right, 0.01);
        Assert.Equal(height - Margin, bottom, 0.01);

        // Two drawings: the box they cover together fills it; each keeps its place in that box.
        xlsx = Workbook(Absolute(Picture(), 0, 0, 1270000, 1270000) + Absolute(Picture(), 1270000, 1270000, 1270000, 1270000));
        pdf = XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { SheetIndexes = [1] });
        var images = Images(pdf, 0);
        Assert.Equal(2, images.Count);
        var (midX, midY) = (width / 2, height / 2);
        Assert.Equal((Margin, Margin, midX, midY), (Math.Round(images[0].Left, 2), Math.Round(images[0].Top, 2), Math.Round(images[0].Right, 2), Math.Round(images[0].Bottom, 2)), new Near());
        Assert.Equal((midX, midY, width - Margin, height - Margin), (images[1].Left, images[1].Top, images[1].Right, images[1].Bottom), new Near());

        // Below the sheet's name, when it is shown.
        pdf = XlsxToPdf.ConvertToBytes(Workbook(Absolute(Picture(), 0, 0, 1270000, 1270000)), new XlsxConverterOptions { SheetIndexes = [1], ShowSheetNames = true });
        var named = Assert.Single(Images(pdf, 0));
        Assert.InRange(named.Top, Margin + 14, Margin + 40);
        Assert.Equal(height - Margin, named.Bottom, 0.01);
        Assert.Contains("Chart", Pages(pdf)[0]);
    }

    [Fact]
    public void The_page_setup_gives_the_orientation_headers_and_footers()
    {
        var setup = "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/><pageSetup orientation=\"portrait\"/>"
            + "<headerFooter><oddHeader>&amp;C&amp;A report</oddHeader><oddFooter>&amp;RPage &amp;P of &amp;N</oddFooter></headerFooter>";
        var xlsx = Workbook(Absolute(Chart, 0, 0, 8697232, 6281964), setup);
        var options = new XlsxConverterOptions { SheetNames = ["Chart"] };
        var pdf = XlsxToPdf.ConvertToBytes(xlsx, options);
        Assert.Equal((595, 842), (Math.Round(Size(pdf, 0).Width), Math.Round(Size(pdf, 0).Height)));
        var text = Assert.Single(Pages(pdf));
        Assert.Contains("Chart report", text);
        Assert.Contains("Page 1 of 1", text);

        // Landscape, said in so many words; without the page setup, the options' page and no headers or footers.
        Assert.Equal(842, Math.Round(Size(XlsxToPdf.ConvertToBytes(Workbook(Absolute(Chart, 0, 0, 100, 100), "<pageSetup orientation=\"landscape\"/>"), options), 0).Width));
        var plain = XlsxToPdf.ConvertToBytes(xlsx, new XlsxConverterOptions { SheetNames = ["Chart"], UsePageSetup = false });
        Assert.Equal(595, Math.Round(Size(plain, 0).Width));
        Assert.DoesNotContain("report", Assert.Single(Pages(plain)));
        Assert.Contains("Quarterly sales", Pages(plain)[0]);
    }

    [Fact]
    public void Chart_sheets_without_anything_to_draw_are_reported_and_left_out()
    {
        // No drawing part, an empty one, a damaged one, or one with only what cannot be drawn.
        foreach (var xlsx in new[] { Workbook(string.Empty, drawing: false), Workbook(string.Empty), Workbook("<xdr:absoluteAnchor>"), Workbook(Absolute(Picture("rId9"), 0, 0, 100, 100)) })
        {
            Assert.Equal(["First", "Last"], XlsxConverter.Read(xlsx).Select(s => s.Name));
            Assert.Contains(Diagnose(xlsx), d => d is { SheetName: "Chart", Kind: SheetDiagnosticKind.ChartSheet });
            Assert.Equal(2, Pages(XlsxToPdf.ConvertToBytes(xlsx)).Length);
        }

        // A chart the engine cannot draw is reported as a chart too.
        var frame = Chart.Replace("http://schemas.openxmlformats.org/drawingml/2006/chart\"", "http://example.com/unknown\"", StringComparison.Ordinal);
        var diagnostics = Diagnose(Workbook(Absolute(frame, 0, 0, 100, 100)));
        Assert.Contains(diagnostics, d => d is { SheetName: "Chart", Kind: SheetDiagnosticKind.ChartSheet });
    }

    [Fact]
    public void A_malformed_chart_sheet_part_throws_the_format_exception()
    {
        var xlsx = Workbook(Absolute(Chart, 0, 0, 8697232, 6281964), "<pageSetup orientation=\"portrait\"");
        var malformed = Assert.ThrowsAny<SpreadsheetFormatException>(() => XlsxToPdf.ConvertToBytes(xlsx));
        Assert.Contains("chartsheets/sheet1.xml", malformed.Message, StringComparison.Ordinal);
    }

    // ---- The Apache POI corpus, against expected.json ----

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Corpus_chart_sheets_are_drawn_in_the_sheet_order(string name)
    {
        var data = File.ReadAllBytes(Path.Combine(Fixtures.Path("Sheets/Drawings"), name));
        var openpyxl = ExpectedJson.Value.GetProperty(name).GetProperty("openpyxl");
        var visible = openpyxl.GetProperty("visible").EnumerateArray().Select(v => v.GetString()!).ToList();
        var chartSheets = openpyxl.GetProperty("chartsheets").EnumerateArray().Select(v => v.GetString()!).ToHashSet();
        var sheets = XlsxConverter.Read(data);
        Assert.Equal(visible, sheets.Select(s => s.Name));
        Assert.Equal(chartSheets, sheets.Where(s => s.Formatting!.ChartSheet).Select(s => s.Name!).ToHashSet());

        // Each chart sheet's page (where its bookmark goes) is landscape, has its chart's text, and is not reported.
        var diagnostics = new List<SheetDiagnostic>();
        var document = XlsxToPdf.Convert(data, new XlsxConverterOptions { Sheet = new SheetOptions { Diagnostics = diagnostics.Add } });
        Assert.DoesNotContain(diagnostics, d => chartSheets.Contains(d.SheetName ?? string.Empty));
        Assert.Equal(visible, document.Outline.Select(o => o.Title));
        var pages = Pages(document.ToArray());
        foreach (var item in document.Outline.Where(o => chartSheets.Contains(o.Title)))
        {
            var page = item.Destination.Page;
            Assert.True(page.Width > page.Height, $"{item.Title} is landscape");
            Assert.NotEmpty(pages[document.Pages.ToList().IndexOf(page)].Trim());
        }
    }

    private sealed class Near : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) x, (double, double, double, double) y) =>
            Math.Abs(x.Item1 - y.Item1) < 0.02 && Math.Abs(x.Item2 - y.Item2) < 0.02 && Math.Abs(x.Item3 - y.Item3) < 0.02 && Math.Abs(x.Item4 - y.Item4) < 0.02;

        public int GetHashCode((double, double, double, double) obj) => 0;
    }
}
