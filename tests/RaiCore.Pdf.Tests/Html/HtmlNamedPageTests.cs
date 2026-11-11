using PDFtoImage;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// Named pages (<c>page</c> and <c>@page name</c>), page sizes and margins for <c>:first</c>, <c>:left</c>,
/// <c>:right</c> and <c>:blank</c>, and <c>break-before: left/right</c> with blank pages.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlNamedPageTests
{
    private const double Mm = 72 / 25.4;

    [Fact]
    public void A_wide_table_goes_on_a_named_landscape_page()
    {
        var html = Document(
            "@page { size: A4; margin: 20mm } @page wide { size: A4 landscape } .wide { page: wide } table { width: 100%; border-collapse: collapse }",
            $"<p>Portrait</p><table class='wide'><tr>{string.Concat(Enumerable.Range(1, 12).Select(i => $"<td>Column {i}</td>"))}</tr></table><p>Portrait again</p>");
        var pdf = HtmlToPdf.Convert(html);

        Assert.Equal([PdfPageSize.A4, PdfPageSize.A4.Landscape, PdfPageSize.A4], pdf.Pages.Select(p => p.Size));

        var result = HtmlToPdf.CreateFlowDocument(html).Paginate();
        var table = result.Placements.First(p => p.Block is TableBlock);
        Assert.Equal(1, table.Page);
        Assert.Equal(PdfPageSize.A4.Landscape.Width - (40 * Mm), table.Width, 0.01); // 100% of the landscape page.
        Assert.Equal([null, "wide", null], result.Frames.Select(f => f.Context.Name));
    }

    [Fact]
    public void A_page_name_change_forces_a_page_break()
    {
        var result = Paginate("", "<p>a</p><div style='page: chapter'><p>b</p><p>c</p></div><div style='page: chapter'><p>d</p></div><p>e</p>");

        Assert.Equal([null, "chapter", null], result.Frames.Select(f => f.Context.Name));
        Assert.Equal([0, 1, 1, 1, 2], ParagraphPages(result));
    }

    [Fact]
    public void The_first_page_takes_the_name_of_the_first_content()
    {
        var pdf = HtmlToPdf.Convert(Document("@page { size: A5 } @page cover { size: A4 } .cover { page: cover }", "<div class='cover'>Cover</div><p>Text</p>"));

        Assert.Equal([PdfPageSize.A4, PdfPageSize.A5], pdf.Pages.Select(p => p.Size)); // No blank page before the cover.
    }

    [Fact]
    public void First_pages_get_their_own_size_and_margins()
    {
        var html = Document("@page { size: A5; margin: 10mm } @page :first { size: A4; margin-top: 50mm } p { margin: 0 }", "<p>one</p><p style='break-before: page'>two</p>");
        var result = HtmlToPdf.CreateFlowDocument(html).Paginate();

        Assert.Equal([PdfPageSize.A4, PdfPageSize.A5], result.Frames.Select(f => f.Setup.Size));
        Assert.Equal([50 * Mm, 10 * Mm], result.Placements.Where(p => p.Block is ParagraphBlock).Select(p => p.Top), new Tolerance(0.01));
    }

    [Fact]
    public void Left_and_right_pages_get_mirrored_margins()
    {
        var result = Paginate(
            "@page { margin: 20mm } @page :left { margin-left: 30mm; margin-right: 15mm } @page :right { margin-left: 15mm; margin-right: 30mm }",
            "<p>one</p><p style='break-before: page'>two</p><p style='break-before: page'>three</p>");

        Assert.Equal([15 * Mm, 30 * Mm, 15 * Mm], result.Frames.Select(f => f.Setup.Margins.Left), new Tolerance(0.01));
        Assert.Equal([30 * Mm, 15 * Mm, 30 * Mm], result.Frames.Select(f => f.Setup.Margins.Right), new Tolerance(0.01));
    }

    [Theory]
    [InlineData("break-before: right", 3, true)]
    [InlineData("break-before: recto", 3, true)]
    [InlineData("page-break-before: right", 3, true)]
    [InlineData("break-before: left", 2, false)]
    [InlineData("break-before: verso", 2, false)]
    [InlineData("break-before: page", 2, false)]
    public void Left_and_right_breaks_insert_blank_pages(string declaration, int pages, bool blank)
    {
        var result = Paginate("", $"<h1>One</h1><h1 style='{declaration}'>Two</h1>");

        Assert.Equal(pages, result.Frames.Count);
        Assert.Equal(blank, result.Frames.Any(f => f.Context.IsBlank));
        Assert.Equal(pages - 1, result.Placements.Last(p => p.Block is ParagraphBlock).Page);
    }

    [Fact]
    public void Blank_pages_get_their_own_margin_boxes_and_margins()
    {
        var html = Document(
            "@page { size: A5; margin: 20mm; @bottom-center { content: counter(page); color: #0000ff } } @page :blank { margin-top: 40mm; @top-center { content: \"BLANK PAGE\"; color: #ff0000 } @bottom-center { content: none } }",
            "<p>One</p><p style='break-before: right'>Two</p>");
        var bytes = HtmlToPdf.ConvertToBytes(html);
        var result = HtmlToPdf.CreateFlowDocument(html).Paginate();

        Assert.Equal(3, Conversion.GetPageCount(bytes));
        Assert.Equal(40 * Mm, result.Frames[1].Setup.Margins.Top, 0.01);
        Assert.Equal([false, true, false], [.. Enumerable.Range(0, 3).Select(page => HasPixels(bytes, page, Red))]);
        Assert.Equal([true, false, true], [.. Enumerable.Range(0, 3).Select(page => HasPixels(bytes, page, Blue))]);
    }

    [Fact]
    public void Page_selectors_cascade_by_specificity_then_source_order()
    {
        // A first page named "wide": the name beats :first, :first beats :right, and each beats plain @page, whatever
        // the source order; among equal ones the later rule wins.
        const string css = """
            @page wide:first { margin-top: 70pt }
            @page wide { margin: 10pt }
            @page :first { margin: 30pt; margin-left: 31pt }
            @page :right { margin-left: 40pt; margin-bottom: 41pt }
            @page { margin: 5pt }
            @page :right { margin-bottom: 42pt }
            .wide { page: wide }
            """;
        var result = Paginate(css, "<div class='wide'><p>a</p><p style='break-before: page'>b</p></div><p>c</p><p style='break-before: page'>d</p>");
        var margins = result.Frames.Select(f => f.Setup.Margins).ToList();

        Assert.Equal(new Thickness(70, 10, 10, 10), margins[0]); // wide:first, then wide.
        Assert.Equal(new Thickness(10, 10, 10, 10), margins[1]); // wide (a left page).
        Assert.Equal(new Thickness(5, 5, 42, 40), margins[2]); // :right twice (the later wins), over plain @page.
        Assert.Equal(new Thickness(5, 5, 5, 5), margins[3]); // A left page: plain @page.
    }

    [Fact]
    public void Margin_boxes_use_the_size_and_margins_of_their_page()
    {
        var html = Document(
            "@page { size: A5; margin: 20mm; @bottom-right { content: \"PAGE\"; color: #ff0000 } } @page wide { size: A5 landscape; margin: 10mm } .wide { page: wide }",
            "<p>portrait</p><div class='wide'>landscape</div>");
        var bytes = HtmlToPdf.ConvertToBytes(html);

        var portrait = RedPixels(bytes, 0);
        var landscape = RedPixels(bytes, 1);
        Assert.InRange(portrait.Max(p => p.X), PdfPageSize.A5.Width - (25 * Mm), PdfPageSize.A5.Width - (20 * Mm));
        Assert.InRange(landscape.Max(p => p.X), PdfPageSize.A5.Height - (15 * Mm), PdfPageSize.A5.Height - (10 * Mm));
        Assert.True(landscape.Min(p => p.Y) > PdfPageSize.A5.Width - (10 * Mm), "In the landscape page's bottom margin.");
    }

    [Fact]
    public void The_page_property_cascades_by_specificity_and_style_attributes_win()
    {
        const string css = "#a { page: one } div.x { page: two } div { page: three } div.x.auto { page: auto }";
        var result = Paginate(css, "<div id='a' class='x'><p>1</p></div><div class='x'><p>2</p></div><div class='x' style='page: four'><p>3</p></div><div class='x auto'><p>4</p></div>");

        // The last div's page is auto (div.x.auto beats div.x), so its paragraph goes on an unnamed page.
        Assert.Equal(["one", "two", "four", null], result.Frames.Select(f => f.Context.Name));
    }

    [Fact]
    public void Without_page_specific_rules_the_document_has_one_setup()
    {
        var flow = HtmlToPdf.CreateFlowDocument(Document("@page { size: A5 } @page :first { @top-center { content: 'x' } }", "<p>a</p>"));

        Assert.Null(flow.PageSetupResolver);
        Assert.Equal(PdfPageSize.A5, flow.Setup.Size);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(":left", 1)]
    [InlineData(":first", 100)]
    [InlineData(":blank:right", 101)]
    [InlineData("wide", 10000)]
    [InlineData("wide:first:left", 10101)]
    public void Page_selector_specificity(string selector, int specificity)
    {
        Assert.Equal(specificity, Assert.Single(PageRules.Parse(selector)!).Specificity);
    }

    [Theory]
    [InlineData(":nth(2)")]
    [InlineData("wide :first")]
    [InlineData(":left,")]
    [InlineData("1st")]
    public void Invalid_page_selectors_are_ignored(string selector)
    {
        Assert.Null(PageRules.Parse(selector));
    }

    [Fact]
    public void Page_selector_lists_match_any_of_their_selectors()
    {
        var selectors = PageRules.Parse("wide, :first")!;

        Assert.Contains(selectors, s => s.Matches(new PageContext(1, null, PageSide.Right, IsBlank: false)));
        Assert.Contains(selectors, s => s.Matches(new PageContext(4, "wide", PageSide.Left, IsBlank: false)));
        Assert.DoesNotContain(selectors, s => s.Matches(new PageContext(4, "Wide", PageSide.Left, IsBlank: false))); // Case-sensitive.
    }

    // ---- helpers ----

    private static readonly Func<SkiaSharp.SKColor, bool> Red = c => c is { Red: > 200, Green: < 80, Blue: < 80 };

    private static readonly Func<SkiaSharp.SKColor, bool> Blue = c => c is { Red: < 80, Green: < 80, Blue: > 200 };

    private static string Document(string css, string body) => $$"""
        <html><head><style>
          body { margin: 0; font-family: sans-serif; }
          {{css}}
        </style></head><body>{{body}}</body></html>
        """;

    private static PaginationResult Paginate(string css, string body) => HtmlToPdf.CreateFlowDocument(Document(css, body)).Paginate();

    private static List<int> ParagraphPages(PaginationResult result) => [.. result.Placements.Where(p => p.Block is ParagraphBlock).Select(p => p.Page)];

    private static bool HasPixels(byte[] pdf, int page, Func<SkiaSharp.SKColor, bool> color)
    {
        using var bitmap = Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: 72));
        return Enumerable.Range(0, bitmap.Height).Any(y => Enumerable.Range(0, bitmap.Width).Any(x => color(bitmap.GetPixel(x, y))));
    }

    private static List<(int X, int Y)> RedPixels(byte[] pdf, int page)
    {
        using var bitmap = Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: 72));
        return [.. Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Where(x => Red(bitmap.GetPixel(x, y))).Select(x => (x, y)))];
    }

    private readonly struct Tolerance(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;

        public int GetHashCode(double obj) => 0;
    }
}
