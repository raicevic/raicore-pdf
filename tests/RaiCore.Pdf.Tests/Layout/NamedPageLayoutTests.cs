using PDFtoImage;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Layout;

/// <summary>
/// Page setups per page (<see cref="FlowDocument.PageSetupResolver"/>), page names (<see cref="Block.PageName"/>) and
/// left/right page breaks. The default page is 200 x 200 pt with 20 pt margins (16 lines of 10 pt per page); the
/// "wide" page is 300 x 150 pt with 10 pt margins.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class NamedPageLayoutTests
{
    private static readonly PdfFont Sans = PdfFont.Load(Fixtures.Path("LiberationSans-Regular.ttf"));
    private static readonly TextStyle Text = new(Sans, 8) { LineHeight = LineHeight.Points(10) };
    private static readonly PageSetup Small = new(new PdfPageSize(200, 200), Thickness.All(20));
    private static readonly PageSetup Wide = new(new PdfPageSize(300, 150), Thickness.All(10));

    [Fact]
    public void Without_a_resolver_every_page_gets_the_setup()
    {
        var flow = Flow(Para(20), Para(1, new BoxStyle { BreakBefore = PageBreak.Page }));
        var result = flow.Paginate();

        Assert.All(result.Frames, frame => Assert.Same(Small, frame.Setup));
        Assert.Equal([PageSide.Right, PageSide.Left, PageSide.Right], result.Frames.Select(f => f.Context.Side));
        Assert.Equal([1, 2, 3], result.Frames.Select(f => f.Context.Number));
    }

    [Fact]
    public void A_page_name_change_forces_a_break_and_the_page_gets_its_setup()
    {
        var before = Para(2);
        var wide = Para(2, name: "wide");
        var after = Para(2);
        var flow = Flow(before, wide, after);
        flow.PageSetupResolver = page => page.Name == "wide" ? Wide : Small;
        var result = flow.Paginate();

        Assert.Equal([null, "wide", null], result.Frames.Select(f => f.Context.Name));
        Assert.Equal([0, 1, 2], new[] { before, wide, after }.Select(b => result.Placements.Single(p => p.Block == b).Page));
        Assert.Equal(Wide.Margins.Top, result.Placements.Single(p => p.Block == wide).Top, 6);

        var pdf = flow.Render();
        Assert.Equal([Small.Size, Wide.Size, Small.Size], pdf.Pages.Select(p => p.Size));
    }

    [Fact]
    public void Siblings_with_the_same_page_name_share_pages()
    {
        var flow = Flow(Para(2, name: "wide"), Para(2, name: "wide"));
        flow.PageSetupResolver = page => page.Name == "wide" ? Wide : Small;
        var result = flow.Paginate();

        var frame = Assert.Single(result.Frames); // The first page takes the name of the content it starts with.
        Assert.Equal("wide", frame.Context.Name);
        Assert.Same(Wide, frame.Setup);
    }

    [Fact]
    public void A_container_starts_with_the_page_name_of_its_first_child()
    {
        // The break goes before the section (whose first child is on a "wide" page), so its top is on that page too.
        var inner = Para(1, name: "wide");
        var section = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0) }, inner);
        var result = Flow(Para(1), section).Paginate();

        Assert.Equal([null, "wide"], result.Frames.Select(f => f.Context.Name));
        Assert.Equal(1, result.Placements.Single(p => p.Block == inner).Page);
        Assert.Equal(1, Assert.Single(result.Placements, p => p.Block == section).Page);
    }

    [Fact]
    public void Children_inherit_the_page_name_and_a_sibling_without_it_goes_back_to_unnamed_pages()
    {
        var named = Para(1);
        var inherited = Para(1);
        var unnamed = Para(1);
        var section = new ContainerBlock(null, new ContainerBlock(null, named, new ContainerBlock(null, inherited)) { PageName = "wide" }, unnamed);
        var result = Flow(section).Paginate();

        Assert.Equal(["wide", null], result.Frames.Select(f => f.Context.Name));
        Assert.Equal([0, 0, 1], new[] { named, inherited, unnamed }.Select(b => result.Placements.Single(p => p.Block == b).Page));
    }

    [Fact]
    public void A_named_container_breaks_before_and_after_itself_but_not_inside()
    {
        var a = Para(1);
        var b = Para(1);
        var c = Para(1);
        var d = Para(1);
        var result = Flow(a, new ContainerBlock(null, b, c) { PageName = "x" }, d).Paginate();

        Assert.Equal([0, 1, 1, 2], new[] { a, b, c, d }.Select(block => result.Placements.Single(p => p.Block == block).Page));
    }

    [Fact]
    public void Content_continuing_on_an_automatic_break_keeps_the_page_name()
    {
        var flow = Flow(Para(1), Para(40, name: "wide"));
        flow.PageSetupResolver = page => page.Name == "wide" ? Wide : Small;
        var result = flow.Paginate();

        // 13 lines fit on a 130 pt tall wide page: 40 lines take 4 of them.
        Assert.Equal([null, "wide", "wide", "wide", "wide"], result.Frames.Select(f => f.Context.Name));
        Assert.All(result.Frames.Skip(1), f => Assert.Same(Wide, f.Setup));
    }

    [Fact]
    public void Break_before_right_inserts_a_blank_page_when_the_next_page_is_a_left_page()
    {
        var heading = Para(1, new BoxStyle { BreakBefore = PageBreak.Right });
        var flow = Flow(Para(1), heading);
        var blank = new PageSetup(new PdfPageSize(200, 200), Thickness.All(50));
        flow.PageSetupResolver = page => page.IsBlank ? blank : Small;
        var seen = new List<PageInfo>();
        flow.PageDecorator = (_, page) => seen.Add(page);
        var result = flow.Paginate();

        Assert.Equal([false, true, false], result.Frames.Select(f => f.Context.IsBlank));
        Assert.Equal(2, result.Placements.Single(p => p.Block == heading).Page);
        Assert.Same(blank, result.Frames[1].Setup);
        Assert.Equal(PageSide.Right, result.Frames[2].Context.Side);

        flow.Render();
        Assert.Equal([false, true, false], seen.Select(p => p.IsBlank));
        Assert.Equal([PageSide.Right, PageSide.Left, PageSide.Right], seen.Select(p => p.Side));
        Assert.Same(blank, seen[1].Setup);
    }

    [Theory]
    [InlineData(PageBreak.Right, 3)] // Page 2 would be a left page: a blank page 2, the content on page 3.
    [InlineData(PageBreak.Left, 2)]
    [InlineData(PageBreak.Page, 2)]
    public void Left_and_right_breaks_go_to_a_page_of_that_side(PageBreak breakBefore, int expectedPage)
    {
        var block = Para(1, new BoxStyle { BreakBefore = breakBefore });
        var result = Flow(Para(1), block).Paginate();

        Assert.Equal(expectedPage, result.Placements.Single(p => p.Block == block).Page + 1);
        Assert.Equal(expectedPage, result.Frames.Count);
    }

    [Fact]
    public void Break_after_left_and_right_insert_blank_pages_too()
    {
        var first = Para(1, new BoxStyle { BreakAfter = PageBreak.Left });
        var second = Para(1, new BoxStyle { BreakAfter = PageBreak.Left });
        var third = Para(1);
        var result = Flow(first, second, third).Paginate();

        // Page 1 (right), page 2 (left), blank page 3 (right), page 4 (left).
        Assert.Equal([0, 1, 3], new[] { first, second, third }.Select(block => result.Placements.Single(p => p.Block == block).Page));
        Assert.Equal([false, false, true, false], result.Frames.Select(f => f.Context.IsBlank));
    }

    [Fact]
    public void A_side_break_on_an_empty_page_makes_that_page_the_blank_one()
    {
        // The section's break-before: page starts page 2 (a left page), still empty when its first child's
        // break-before: right needs a right page: page 2 becomes the blank page.
        var heading = Para(1, new BoxStyle { BreakBefore = PageBreak.Right });
        var result = Flow(Para(1), new ContainerBlock(new BoxStyle { BreakBefore = PageBreak.Page }, heading)).Paginate();

        Assert.Equal(3, result.Frames.Count);
        Assert.Equal([false, true, false], result.Frames.Select(f => f.Context.IsBlank));
        Assert.Equal(2, result.Placements.Single(p => p.Block == heading).Page);
    }

    [Fact]
    public void A_left_break_before_the_first_content_makes_the_first_page_a_left_page()
    {
        var result = Flow(Para(1, new BoxStyle { BreakBefore = PageBreak.Left }), Para(1, new BoxStyle { BreakBefore = PageBreak.Page })).Paginate();

        Assert.Equal([PageSide.Left, PageSide.Right], result.Frames.Select(f => f.Context.Side));
        Assert.DoesNotContain(result.Frames, f => f.Context.IsBlank);
    }

    [Fact]
    public void The_first_page_and_left_and_right_pages_get_their_own_margins()
    {
        var first = Small with { Margins = new Thickness(60, 20, 20, 20) };
        var left = Small with { Margins = new Thickness(20, 10, 20, 40) };
        var paragraph = Para(40);
        var flow = Flow(paragraph);
        flow.PageSetupResolver = page => page.IsFirst ? first : page.Side == PageSide.Left ? left : Small;
        var result = flow.Paginate();

        var pieces = result.Placements.Where(p => p.Block == paragraph).ToList();
        Assert.Equal(60, pieces[0].Top, 6);
        Assert.Equal(12, pieces[0].EndLine); // 120 pt below the top margin of 60.
        Assert.Equal(20, pieces[1].Top, 6);
        Assert.Equal([first, left, Small], result.Frames.Take(3).Select(f => f.Setup));
    }

    [Fact]
    public void Blocks_starting_on_a_wider_page_get_its_content_width_through_auto_width_containers()
    {
        var narrow = Para(1);
        var wide = Para(1);
        var fixedWidth = Para(1);
        var flow = Flow(
            new ContainerBlock(new BoxStyle { Padding = new Thickness(0, 5, 0, 5) }, narrow),
            new ContainerBlock(new BoxStyle { Padding = new Thickness(0, 5, 0, 5) }, wide, new ContainerBlock(new BoxStyle { Width = 100 }, fixedWidth)) { PageName = "wide" });
        flow.PageSetupResolver = page => page.Name == "wide" ? Wide : Small;
        var placements = flow.Paginate().Placements;

        Assert.Equal(Small.ContentWidth - 10, placements.Single(p => p.Block == narrow).Width, 6);
        Assert.Equal(Wide.ContentWidth - 10, placements.Single(p => p.Block == wide).Width, 6);
        Assert.Equal(100, placements.Single(p => p.Block == fixedWidth).Width, 6);
    }

    [Fact]
    public void Backgrounds_of_boxes_that_follow_the_page_width_are_as_wide_as_each_page()
    {
        var box = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0) }, Para(1), Para(1, name: "wide"));
        var flow = Flow(box);
        flow.PageSetupResolver = page => page.Name == "wide" ? Wide : Small;
        var fragments = flow.Paginate().Placements.Where(p => p.Block == box).ToList();

        Assert.Equal([Small.ContentWidth, Wide.ContentWidth], fragments.Select(f => f.Width));
    }

    [Fact]
    public void Pages_with_another_left_margin_shift_their_content()
    {
        // A right page with a 20 pt left margin, then a left page with a 60 pt one; the band fills the content width.
        var left = Small with { Margins = new Thickness(20, 20, 20, 60) };
        var band = new ContainerBlock(new BoxStyle { Background = PdfColor.FromRgb(255, 0, 0) }, Para(30));
        var flow = Flow(band);
        flow.PageSetupResolver = page => page.Side == PageSide.Left ? left : Small;
        var pdf = flow.Render().ToArray();

        Assert.Equal((20, 180), RedSpan(pdf, page: 0));
        Assert.Equal((60, 180), RedSpan(pdf, page: 1)); // 160 - 40: as much narrower as the page's content area.
    }

    [Fact]
    public void Links_move_with_shifted_content()
    {
        var left = Small with { Margins = new Thickness(20, 20, 20, 60) };
        var flow = Flow(Para(1, new BoxStyle { BreakAfter = PageBreak.Page }), new ParagraphBlock(new Paragraph(Text).AddLink("Home", LinkTarget.ToUri(new Uri("https://example.com/")))));
        flow.PageSetupResolver = page => page.Side == PageSide.Left ? left : Small;
        var pdf = flow.Render();

        var link = Assert.Single(pdf.Pages[1].Links);
        Assert.Equal(60, link.X, 6);
    }

    [Fact]
    public void A_resolver_leaving_no_content_area_is_reported()
    {
        var flow = Flow(Para(1));
        flow.PageSetupResolver = _ => new PageSetup(new PdfPageSize(100, 100), Thickness.All(60));

        Assert.Throws<InvalidOperationException>(() => flow.CountPages());
    }

    [Fact]
    public void The_decorator_gets_each_pages_setup_and_name()
    {
        var flow = Flow(Para(1), Para(1, name: "wide"));
        flow.PageSetupResolver = page => page.Name == "wide" ? Wide : Small;
        var seen = new List<PageInfo>();
        flow.PageDecorator = (_, page) => seen.Add(page);
        flow.Render();

        Assert.Equal([Small, Wide], seen.Select(p => p.Setup));
        Assert.Equal([null, "wide"], seen.Select(p => p.Name));
        Assert.Equal(new PageContext(2, "wide", PageSide.Left, IsBlank: false), seen[1].Context);
    }

    // ---- helpers ----

    private static (int Left, int Right) RedSpan(byte[] pdf, int page)
    {
        using var bitmap = Conversion.ToImage(pdf, page: page, options: new RenderOptions(Dpi: 72));
        var row = Enumerable.Range(0, bitmap.Width).Where(x => bitmap.GetPixel(x, 100) is { Red: > 200, Green: < 80, Blue: < 80 }).ToList();
        return (row.Min(), row.Max() + 1);
    }

    private static ParagraphBlock Para(int lines, BoxStyle? style = null, string? name = null) =>
        new(new Paragraph(Text).Add(string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Line {i}"))), style) { PageName = name };

    private static FlowDocument Flow(params Block[] blocks)
    {
        var flow = new FlowDocument(Small);
        foreach (var block in blocks)
        {
            flow.Add(block);
        }

        return flow;
    }
}
