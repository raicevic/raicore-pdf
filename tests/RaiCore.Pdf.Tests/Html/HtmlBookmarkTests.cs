using System.Text;
using RaiCore.Pdf.Html;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>PDF bookmarks from headings and the CSS GCPM properties bookmark-level, bookmark-label and bookmark-state.</summary>
public class HtmlBookmarkTests
{
    [Fact]
    public void Headings_nest_by_level_and_skipped_levels_nest_under_the_nearest_shallower_one()
    {
        var outline = Outline("<h1>A</h1><h2>B</h2><h3>C</h3><h2>D</h2><h1>E</h1><h3>F</h3><h6>G</h6><h4>H</h4><h5>I</h5>");

        Assert.Equal("A[B[C], D], E[F[G, H[I]]]", Tree(outline));
    }

    [Fact]
    public void Titles_are_the_heading_text_with_white_space_collapsed()
    {
        var outline = Outline("<h1>\n  Annual   <em>report</em>\t2026<br>Summary <script>ignored()</script></h1>");

        Assert.Equal("Annual report 2026 Summary", Assert.Single(outline).Title);
    }

    [Fact]
    public void Bookmark_level_none_disables_and_levels_make_other_elements_bookmarks()
    {
        var outline = Outline(
            "<h1>A</h1><h2>Hidden</h2><p style='bookmark-level: 2'>Paragraph</p><h3>C</h3><div class='box'>Box</div>",
            "h2 { bookmark-level: none } .box { bookmark-level: 1 }");

        Assert.Equal("A[Paragraph[C]], Box", Tree(outline));
    }

    [Fact]
    public void Declarations_cascade_by_specificity_then_source_order()
    {
        var outline = Outline(
            "<h1>A</h1><h2 class='keep'>Kept</h2><h2>Dropped</h2><h3 id='late'>Late</h3><section><h4>Listed</h4></section>",
            "h2.keep { bookmark-level: 2 } h2 { bookmark-level: none } #late { bookmark-level: none } h3 { bookmark-level: 3 } h1, section h4 { bookmark-level: 1 }");

        Assert.Equal("A[Kept], Listed", Tree(outline));
    }

    [Fact]
    public void Labels_combine_strings_attributes_and_content()
    {
        var outline = Outline(
            """
            <h1 id="s">Ignored</h1>
            <h1 id="a" title="From the title">Ignored</h1>
            <h1 id="c">Intro</h1>
            <h1 id="f">Zeta</h1>
            <h1 id="e" data-x="x">Ignored</h1>
            <h1 id="m">No attribute</h1>
            """,
            """
            #s { bookmark-label: "Fixed" }
            #a { bookmark-label: attr(title) }
            #c { bookmark-label: "Ch. " content() }
            #f { bookmark-label: content(first-letter) " - " content(text) }
            #e { bookmark-label: '\2192  ' attr(data-x) }
            #m { bookmark-label: attr(data-missing) content() }
            """);

        Assert.Equal(["Fixed", "From the title", "Ch. Intro", "Z - Zeta", "\u2192 x", "No attribute"], outline.Select(i => i.Title));
    }

    [Fact]
    public void Bookmark_state_closed_collapses_an_entry()
    {
        var outline = Outline("<h1 class='c'>A</h1><h2>B</h2><h1>C</h1>", "h1.c { bookmark-state: closed }");

        Assert.Equal("A[B], C", Tree(outline));
        Assert.Equal([false, true], outline.Select(i => i.IsOpen));
    }

    [Fact]
    public void Invalid_values_are_ignored()
    {
        var outline = Outline(
            "<h1>A</h1><h2>B</h2><h3>C</h3>",
            "h1 { bookmark-level: 0 } h2 { bookmark-level: two; bookmark-label: none } h3 { bookmark-state: shut }");

        Assert.Equal("A[B[C]]", Tree(outline));
        Assert.True(outline[0].Children[0].Children[0].IsOpen);
    }

    [Fact]
    public void Headings_without_text_are_skipped()
    {
        var outline = Outline("<h1>A</h1><h2> </h2><h2>B</h2>");

        Assert.Equal("A[B]", Tree(outline));
    }

    [Fact]
    public void Headings_in_flex_containers_and_table_cells_are_bookmarks()
    {
        var outline = Outline(
            "<h1>A</h1><div style='display: flex'><h2>Flex item</h2></div><h2 style='display: flex'>Flex heading</h2>" +
            "<table><tr><td><h2>Cell</h2></td></tr></table>");

        Assert.Equal("A[Flex item, Flex heading, Cell]", Tree(outline));
    }

    [Fact]
    public void Headings_in_running_elements_are_not_bookmarks()
    {
        var outline = Outline(
            "<header class='letterhead'><h1>Letterhead</h1></header><h1>Body</h1>",
            "@page { @top-center { content: element(letterhead) } } .letterhead { position: running(letterhead) }");

        Assert.Equal("Body", Tree(outline));
    }

    [Fact]
    public void Unicode_titles_are_kept()
    {
        var outline = Outline("<h1>\u00C6bler &amp; \u00E6g \u2013 \u65E5\u672C</h1>");

        Assert.Equal("\u00C6bler & \u00E6g \u2013 \u65E5\u672C", Assert.Single(outline).Title);
    }

    [Fact]
    public void Destinations_point_at_the_page_where_each_heading_starts()
    {
        var doc = HtmlToPdf.Convert("<h1>One</h1><p>Text</p><h1 style='break-before: page'>Two</h1>");
        var pdf = Encoding.Latin1.GetString(doc.ToArray());

        Assert.Equal(2, doc.Pages.Count);
        Assert.Same(doc.Pages[0], doc.Outline[0].Page);
        Assert.Same(doc.Pages[1], doc.Outline[1].Page);

        // Near the top of the page: below the 2 cm margin, by no more than the heading's own top margin.
        var contentTop = doc.Pages[1].Height - (72 / 2.54 * 2);
        Assert.InRange(doc.Outline[1].Top!.Value, contentTop - 30, contentTop);
        Assert.Contains("/PageMode /UseOutlines", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Bookmarks_can_be_turned_off()
    {
        var doc = HtmlToPdf.Convert("<h1>A</h1><h2>B</h2>", new HtmlConverterOptions { CreateBookmarks = false });

        Assert.Empty(doc.Outline);
        Assert.DoesNotContain("/Outlines", Encoding.Latin1.GetString(doc.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_qpdf_structural_check() =>
        ExternalTools.AssertQpdfClean(HtmlToPdf.ConvertToBytes("<h1>\u00C6 A</h1><h2>B</h2><h3 style='bookmark-state: closed'>C</h3><h4>D</h4><h1>E</h1>"));

    private static IList<PdfOutlineItem> Outline(string body, string css = "") =>
        HtmlToPdf.Convert($"<html><head><style>{css}</style></head><body>{body}</body></html>").Outline;

    /// <summary>The outline as text, e.g. <c>A[B, C], D</c>.</summary>
    private static string Tree(IEnumerable<PdfOutlineItem> items) =>
        string.Join(", ", items.Select(i => i.Children.Count == 0 ? i.Title : $"{i.Title}[{Tree(i.Children)}]"));
}
