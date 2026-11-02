using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class PagedMediaTests
{
    private const string Filler = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";

    [Fact]
    public void Scanner_reads_page_rules_with_margin_boxes_and_style_rules()
    {
        const string css = """
            /* a { comment } */
            @import url("x.css");
            h1 { string-set: chapter content(); color: red }
            @page { size: A5; margin: 1cm; @top-center { content: "a } b"; color: blue } @bottom-right { content: counter(page) } }
            @page :first { @top-center { content: none } }
            @media print { h2 { string-set: section content() } }
            @media screen { h3 { string-set: hidden content() } }
            """;
        var (styles, pages) = CssRuleScanner.Scan(css);

        Assert.Equal(["h1", "h2"], styles.Select(s => s.Selector));
        Assert.Equal(["", ":first"], pages.Select(p => p.Selector));
        Assert.Contains(new CssDeclaration("size", "A5"), pages[0].Declarations);
        Assert.Equal("\"a } b\"", pages[0].MarginBoxes["top-center"].Single(d => d.Property == "content").Value);
        Assert.Equal(["top-center", "bottom-right"], pages[0].MarginBoxes.Keys);
    }

    [Fact]
    public void Footer_counts_pages_on_every_page()
    {
        var pages = PageTexts(Document("@bottom-center { content: \"Page \" counter(page) \" of \" counter(pages) }", Chapters(3)));

        Assert.Equal(3, pages.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            Assert.Contains($"Page {i + 1} of 3", pages[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Counters_use_counter_styles_and_strings_decode_css_escapes()
    {
        var pages = PageTexts(Document("@bottom-center { content: \"\\2014  \" counter(page, upper-roman) \" \\2014\" }", Chapters(3)));

        // Extractors rebuild spaces from glyph positions differently (poppler vs pdfminer), so compare without them:
        // the point is that "\2014" decodes to an em dash and the counter is in upper-roman.
        static string NoSpaces(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("\u2014I\u2014", NoSpaces(pages[0]), StringComparison.Ordinal);
        Assert.Contains("\u2014III\u2014", NoSpaces(pages[2]), StringComparison.Ordinal);
    }

    [Fact]
    public void Running_strings_show_the_current_chapter_including_continuation_pages()
    {
        // Chapter 1 is long enough to continue onto a second page; chapter 2 starts on a new page.
        var body = $"<h1>First chapter</h1>{string.Concat(Enumerable.Repeat($"<p>{Filler}{Filler}{Filler}</p>", 14))}<h1 style='break-before: page'>Second chapter</h1><p>Short.</p>";
        var pages = PageTexts(Document("@top-center { content: \"[\" string(chapter) \"]\" }", body, "h1 { string-set: chapter content() }"));

        Assert.True(pages.Count >= 3);
        Assert.Contains("[First chapter]", pages[0], StringComparison.Ordinal);
        Assert.Contains("[First chapter]", pages[1], StringComparison.Ordinal); // Carried over (entry value).
        Assert.Contains("[Second chapter]", pages[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("first", "[A]")]
    [InlineData("last", "[B]")]
    [InlineData("first-except", "[]")]
    public void Running_string_policies(string policy, string expected)
    {
        var body = "<h2>A</h2><p>x</p><h2>B</h2><p>y</p>";
        var pages = PageTexts(Document($"@top-center {{ content: \"[\" string(s, {policy}) \"]\" }}", body, "h2 { string-set: s content() }"));

        Assert.Contains(expected, pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Running_elements_leave_the_flow_and_appear_in_margin_boxes()
    {
        var body = "<div class='head'>ACME Report</div>" + Chapters(2);
        var pages = PageTexts(Document("@top-left { content: element(head) }", body, ".head { position: running(head) }"));

        Assert.All(pages, page => Assert.Equal(1, Occurrences(page, "ACME Report"))); // Once per page: only the header.
    }

    [Fact]
    public void First_left_and_right_pages_get_their_own_boxes()
    {
        const string page = """
            @top-center { content: "all pages" }
            }
            @page :first { @top-center { content: none } }
            @page :left { @bottom-left { content: "left" } }
            @page :right { @bottom-right { content: "right" }
            """;
        var pages = PageTexts(Document(page, Chapters(3)));

        Assert.DoesNotContain("all pages", pages[0], StringComparison.Ordinal);
        Assert.Contains("all pages", pages[1], StringComparison.Ordinal);
        Assert.Contains("right", pages[0], StringComparison.Ordinal); // Page 1 is a right page.
        Assert.Contains("left", pages[1], StringComparison.Ordinal);
        Assert.DoesNotContain("right", pages[1], StringComparison.Ordinal);
        Assert.Contains("right", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Margin_boxes_are_styled_and_positioned_in_the_margin()
    {
        var html = Document("@top-right { content: \"MARKED\"; color: #ff0000; font-size: 20pt; font-weight: bold }", "<p>body</p>");
        using var bitmap = Conversion.ToImage(HtmlToPdf.ConvertToBytes(html), page: 0, options: new RenderOptions(Dpi: 72));

        // A5 with a 20 mm (56.7 pt) top margin: red text in the top margin, in the right third.
        var red = Enumerable.Range(0, 56).SelectMany(y => Enumerable.Range(0, bitmap.Width).Where(x => bitmap.GetPixel(x, y) is { Red: > 200, Green: < 80, Blue: < 80 }).Select(x => (x, y))).ToList();
        Assert.NotEmpty(red);
        Assert.True(red.Min(p => p.x) > bitmap.Width / 2, "The top-right box is on the right.");
    }

    [Fact]
    public void Pages_get_markers_of_the_blocks_that_start_on_them()
    {
        var style = new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10) { LineHeight = LineHeight.Points(10) };
        var flow = new FlowDocument(new PageSetup(new PdfPageSize(200, 200), Thickness.All(20)));
        flow.Add(new ParagraphBlock(new Paragraph(style).Add("a")) { Marker = "A" });
        flow.Add(new ContainerBlock { Marker = "empty" }); // Takes no space.
        flow.Add(new ParagraphBlock(new Paragraph(style).Add(string.Join("\n", Enumerable.Range(0, 20)))) { Marker = "B" });
        flow.Add(new ParagraphBlock(new Paragraph(style).Add("c"), new BoxStyle { BreakBefore = PageBreak.Page }) { Marker = "C" });
        var seen = new List<(int, string)>();
        flow.PageDecorator = (_, page) => seen.AddRange(page.Markers.Select(m => (page.Number, (string)m)));
        flow.Render();

        Assert.Equal([(1, "A"), (1, "empty"), (1, "B"), (3, "C")], seen);
    }

    [Fact]
    public void Empty_marker_blocks_do_not_change_margin_collapsing()
    {
        var style = new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10);
        var a = new ParagraphBlock(new Paragraph(style).Add("a"), new BoxStyle { Margin = new(0, 0, 20, 0) });
        var b = new ParagraphBlock(new Paragraph(style).Add("b"), new BoxStyle { Margin = new(10, 0, 0, 0) });
        var flow = new FlowDocument(PageSetup.A4).Add(a).Add(new ContainerBlock { Marker = "m" }).Add(b);
        var placements = flow.Paginate().Placements;

        Assert.Equal(placements.Single(p => p.Block == a).Bottom + 20, placements.Single(p => p.Block == b).Top, 6);
    }

    [Fact]
    public void Blocks_can_be_measured_and_drawn_outside_the_flow()
    {
        var style = new TextStyle(PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf")), 10) { LineHeight = LineHeight.Points(12) };
        Block[] blocks = [new ParagraphBlock(new Paragraph(style).Add("one\ntwo"))];
        var doc = new PdfDocument();
        var page = doc.AddPage(PdfPageSize.A5);

        Assert.Equal(24, FlowDocument.MeasureBlocks(blocks, 100), 6);
        Assert.Equal(24, FlowDocument.DrawBlocks(page.Canvas, page.Height, blocks, 50, 50, 100), 6);
        Assert.Contains("two", ExternalTools.ExtractText(doc.ToArray()), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static string Document(string pageBody, string body, string css = "") => $$"""
        <html><head><style>
          @page { size: A5; margin: 20mm 15mm; {{pageBody}} }
          body { margin: 0; font-family: sans-serif; }
          {{css}}
        </style></head><body>{{body}}</body></html>
        """;

    private static string Chapters(int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"<h1 style='{(i > 1 ? "break-before: page" : "")}'>Chapter {i}</h1><p>{Filler}</p>"));

    private static List<string> PageTexts(string html) =>
        [.. ExternalTools.ExtractText(HtmlToPdf.ConvertToBytes(html)).Split('\f').Where(p => p.Trim().Length > 0)];

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
