using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// The widths CSS sees when printing (#148), as in Chrome: width media queries see the paper (the options' page size,
/// whatever <c>@page</c> says), viewport units the page area (as <c>@page</c> sets it up).
/// </summary>
public class HtmlPrintWidthTests
{
    private const double Px = 0.75;

    [Theory]
    [InlineData(767, true)] // A4 is 793.7 px wide: Bootstrap's md rules (min-width: 768px) apply, although the area is 642 px.
    [InlineData(793, true)]
    [InlineData(794, false)]
    public void Width_media_queries_see_the_paper(int minWidth, bool applies)
    {
        var size = FontSize($"<style>p {{ font-size: 10px }} @media (min-width: {minWidth}px) {{ p {{ font-size: 20px }} }}</style><p>x</p>");

        Assert.Equal(applies ? 20 * Px : 10 * Px, size, 3);
    }

    [Fact]
    public void Page_rules_change_the_viewport_units_but_not_the_media_queries()
    {
        // A landscape A4 page box is 1122.5 px wide; the media still see the 793.7 px paper.
        var html = """
            <style>
              @page { size: A4 landscape; margin: 0 }
              p { font-size: 10px; width: 50vw }
              @media (min-width: 1000px) { p { font-size: 20px } }
            </style><p>x</p>
            """;
        var paragraph = Paragraphs(html).Single();

        Assert.Equal(10 * Px, paragraph.Paragraph.Style.FontSize, 3);
        Assert.Equal(PdfPageSize.A4.Height / 2, paragraph.Style.Width!.Value.Points, 1);
    }

    [Fact]
    public void Viewport_units_are_of_the_page_area()
    {
        var paragraph = Paragraphs("<style>@page { size: 500pt 800pt; margin: 50pt 100pt } p { width: 10vw; height: 10vh }</style><p>x</p>").Single();

        Assert.Equal(30, paragraph.Style.Width!.Value.Points, 3);
        Assert.Equal(70, paragraph.Style.Height!.Value, 3);
    }

    [Fact]
    public void Viewport_units_the_converter_resolves_itself_are_of_the_page_area()
    {
        // A right-to-left element's logical margins are read from the CSS text: 10vw of the 300 pt area.
        var paragraph = Paragraphs("<style>@page { size: 500pt 800pt; margin: 50pt 100pt } p { margin: 0; margin-inline-start: 10vw }</style><p dir='rtl'>x</p>").Single();

        Assert.Equal(30, paragraph.Style.Margin.Right.Points, 3);
    }

    [Fact]
    public void Viewport_units_in_a_fragment_are_of_its_exact_width()
    {
        // 200 pt is 266.67 px: 30vw is 60 pt, not 59.85 pt of a whole-pixel width.
        var fragment = HtmlFragment.Parse("<p style='width: 30vw'>x</p>", new HtmlFragmentOptions { Width = 200 });

        Assert.Equal(60, Flatten(fragment.Blocks).OfType<ParagraphBlock>().Single().Style.Width!.Value.Points, 3);
    }

    [Fact]
    public void The_paper_is_the_options_page()
    {
        var html = "<style>p { font-size: 10px } @media (min-width: 1000px) { p { font-size: 20px } }</style><p>x</p>";
        var wide = new PageSetup(new PdfPageSize(800, 600), Thickness.All(300)); // 1066.7 px wide, a 266.7 px area.

        Assert.Equal(20 * Px, Paragraphs(html, wide).Single().Paragraph.Style.FontSize, 3);
    }

    private static double FontSize(string html) => Paragraphs(html).Single().Paragraph.Style.FontSize;

    private static List<ParagraphBlock> Paragraphs(string html, PageSetup? page = null) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Page = page ?? PageSetup.A4 }).Blocks).OfType<ParagraphBlock>()];

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
