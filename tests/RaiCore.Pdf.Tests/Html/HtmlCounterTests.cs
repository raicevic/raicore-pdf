using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Paged;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>
/// CSS counters (counter-reset/increment/set, counter() and counters() in generated content, the list-item
/// counter), page-context counters in margin boxes, and the cross-references target-counter(), target-counters()
/// and target-text(), with leaders for tables of contents.
/// </summary>
public class HtmlCounterTests
{
    private const string Filler = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";

    [Fact]
    public void Counter_increment_counts_elements_in_document_order()
    {
        var texts = Texts(
            "<h2>Alpha</h2><p>text</p><h2>Beta</h2><div><h2>Gamma</h2></div>",
            "h2 { counter-increment: section } h2::before { content: counter(section) '. ' }");

        Assert.Equal(["1. Alpha", "text", "2. Beta", "3. Gamma"], texts);
    }

    [Fact]
    public void Increment_defaults_to_one_and_takes_values_and_set_overrides()
    {
        var texts = Texts(
            "<p class='a'>a</p><p class='b'>b</p><p class='c'>c</p><p class='a'>d</p><p class='d'>e</p>",
            """
            body { counter-reset: n 10 }
            p.a { counter-increment: n }
            p.b { counter-increment: n 5 }
            p.c { counter-set: n 100 }
            p.d { counter-increment: n -3 }
            p::before { content: counter(n) ' ' }
            """);

        Assert.Equal(["11 a", "16 b", "100 c", "101 d", "98 e"], texts);
    }

    [Fact]
    public void Resets_on_siblings_start_over_and_nested_resets_make_nested_counters()
    {
        // h1 and h2 are siblings: the counter h1 resets is in scope for the following h2s, until the next h1 resets it.
        var texts = Texts(
            "<h1>A</h1><h2>a</h2><h2>b</h2><h1>B</h1><h2>c</h2>",
            """
            body { counter-reset: chapter }
            h1 { counter-increment: chapter; counter-reset: section }
            h2 { counter-increment: section }
            h1::before { content: counters(chapter, '.') ' ' }
            h2::before { content: counter(chapter) '.' counters(section, '.') ' ' }
            """);

        Assert.Equal(["1 A", "1.1 a", "1.2 b", "2 B", "2.1 c"], texts);
    }

    [Fact]
    public void Counters_joins_nested_scopes_for_section_numbering()
    {
        var texts = Texts(
            """
            <ol><li>One<ol><li>One.one</li><li>One.two<ol><li>Deep</li></ol></li></ol></li><li>Two</li></ol>
            """,
            """
            ol { counter-reset: item; list-style: none }
            li { counter-increment: item }
            li::before { content: counters(item, '.') ' ' }
            """);

        Assert.Equal(["1 One", "1.1 One.one", "1.2 One.two", "1.2.1 Deep", "2 Two"], texts);
    }

    [Fact]
    public void Counters_in_sections_number_headings()
    {
        var texts = Texts(
            """
            <section><h2>A</h2>
              <div class="sub"><section><h2>B</h2></section><section><h2>C</h2><div class="sub"><section><h2>D</h2></section></div></section></div>
            </section>
            <section><h2>E</h2></section>
            """,
            "body, .sub { counter-reset: s } section { counter-increment: s } h2::before { content: counters(s, '.') '. ' }");

        Assert.Equal(["1. A", "1.1. B", "1.2. C", "1.2.1. D", "2. E"], texts);
    }

    [Theory]
    [InlineData("decimal", "3")]
    [InlineData("decimal-leading-zero", "03")]
    [InlineData("lower-roman", "iii")]
    [InlineData("upper-roman", "III")]
    [InlineData("lower-alpha", "c")]
    [InlineData("upper-latin", "C")]
    [InlineData("lower-greek", "\u03B3")]
    [InlineData("disc", "\u2022")]
    [InlineData("none", "")]
    public void Counters_use_counter_styles(string style, string expected)
    {
        var texts = Texts("<p>x</p>", $"p {{ counter-reset: c 3 }} p::before {{ content: '[' counter(c, {style}) ']' }}");

        Assert.Equal([$"[{expected}]x"], texts);
    }

    [Fact]
    public void Counters_not_in_scope_are_zero_and_hidden_elements_do_not_count()
    {
        var texts = Texts(
            "<p class='i'>a</p><p class='i' style='display: none'>hidden</p><p class='i'>b</p><p class='u'>c</p>",
            "p.i { counter-increment: n } p::before { content: counter(n) ' ' } p.u::before { content: counter(missing) ' ' }");

        Assert.Equal(["1 a", "2 b", "0 c"], texts);
    }

    [Fact]
    public void Pseudo_elements_can_change_counters()
    {
        var texts = Texts(
            "<p>a</p><p>b</p>",
            "body { counter-reset: n } p::before { counter-increment: n 2; content: counter(n) ' ' } p::after { content: ' ' counter(n) }");

        Assert.Equal(["2 a 2", "4 b 4"], texts);
    }

    [Fact]
    public void List_items_count_with_the_list_item_counter()
    {
        var texts = Texts(
            "<ol start='4'><li>a</li><li>b<ol><li>c</li></ol></li></ol>",
            "li::before { content: '(' counters(list-item, '.') ') ' }");

        Assert.Equal(["4.\u00A0(4) a", "5.\u00A0(5) b", "1.\u00A0(5.1) c"], texts);
    }

    [Fact]
    public void List_markers_follow_the_list_item_counter()
    {
        var texts = Texts(
            "<ol><li>a</li><li>b</li><li>c</li></ol><ol class='r'><li>d</li><li>e</li></ol>",
            "li { counter-increment: list-item 2 } ol.r { counter-reset: list-item 10 }");

        Assert.Equal(["2.\u00A0a", "4.\u00A0b", "6.\u00A0c", "12.\u00A0d", "14.\u00A0e"], texts);
    }

    [Fact]
    public void Target_counter_and_target_text_resolve_forward_references()
    {
        var texts = Texts(
            """
            <p><a href="#c">See</a> and <a href="#b">see</a></p>
            <h2 id="a">Alpha</h2><h2 id="b">Beta</h2><h2 id="c">Gamma <em>rays</em></h2>
            <p><a class="t" href="#b">x</a> <a class="f" href="#c">y</a> <a class="n" href="#nowhere">z</a></p>
            """,
            """
            h2 { counter-increment: h }
            h2::before { content: 'Section ' counter(h) ': ' }
            p a::after { content: ' section ' target-counter(attr(href), h, upper-roman) }
            p a.t::after { content: ' [' target-text(attr(href)) '|' target-text(attr(href), before) ']' }
            p a.f::after { content: ' [' target-text(attr(href url), first-letter) '|' target-counters(attr(href), h, '-') ']' }
            p a.n::after { content: ' [' target-counter(attr(href), h) target-text(attr(href)) ']' }
            """);

        Assert.Equal(
            ["See section III and see section II", "Section 1: Alpha", "Section 2: Beta", "Section 3: Gamma rays", "x [Beta|Section 2: ] y [G|3] z []"],
            texts);
    }

    [Fact]
    public void Cross_reference_content_takes_part_in_the_cascade()
    {
        // AngleSharp drops content values with target-counter() and leader(), so they are cascaded separately; a more
        // specific plain content still wins over them, and they win over less specific ones.
        var texts = Texts(
            """
            <nav><a class="plain" href="#t">A</a> <a href="#t">B</a> <a class="strong" href="#t">C</a></nav><h1 id="t">T</h1>
            """,
            """
            nav a.plain::after { content: ' -' }
            nav a::after { content: ' p' target-counter(attr(href), page) }
            a::after { content: ' weak' }
            nav a.strong::after { content: ' [' target-text(attr(href)) ']' }
            nav > a.strong::after { color: red }
            """);

        Assert.Equal(["A - B p1 C [T]", "T"], texts);
    }

    [Fact]
    public void Bookmark_labels_and_running_strings_can_use_counters()
    {
        var doc = HtmlToPdf.Convert(Html(
            "<h1>Alpha</h1><h1>Beta</h1>",
            "h1 { counter-increment: chapter; bookmark-label: counter(chapter, upper-roman) '. ' content() }"));

        Assert.Equal(["I. Alpha", "II. Beta"], doc.Outline.Select(i => i.Title));

        var flow = HtmlToPdf.CreateFlowDocument(Html("<h1>Alpha</h1>", "h1 { counter-increment: chapter; string-set: title counter(chapter) ' ' content() }"));
        var marker = Assert.IsType<StringSetMarker>(Assert.Single(Flatten(flow.Blocks), b => b.Marker is not null).Marker);
        Assert.Equal([("title", "1 Alpha")], marker.Assignments);
    }

    [Fact]
    public void Target_counter_gives_the_page_of_forward_references()
    {
        var html = Html(
            """
            <nav><a href="#one">One</a><a href="#two">Two</a><a href="#three">Three</a><a href="#missing">Missing</a></nav>
            <h1 id="one">One</h1><p>first</p>
            <h1 id="two" style="break-before: page">Two</h1><p>second</p><div style="break-before: page"></div><p>more</p>
            <h1 id="three" style="break-before: page">Three</h1>
            """,
            "nav a { display: block } nav a::after { content: ' p. ' target-counter(attr(href), page) }");

        var flow = HtmlToPdf.CreateFlowDocument(html);
        var pages = flow.MapPages().Destinations;

        Assert.Equal(1, pages["one"]);
        Assert.Equal(2, pages["two"]);
        Assert.Equal(4, pages["three"]);
        Assert.Equal(["One p. 1", "Two p. 2", "Three p. 4", "Missing p. "], Paragraphs(flow.Blocks).Take(4).Select(Text));
    }

    [Fact]
    public void Page_references_are_laid_out_again_until_they_settle()
    {
        // Pages are numbered from 10. The first layout has a placeholder ("0"); the real page number ("10") is wider
        // and wraps the entry, which pushes the target to the second page; the third layout ("11") keeps it there.
        // Only then is the reference right.
        var html = $$"""
            <html><head><style>
              @page { size: A5; margin: 0 }
              body { margin: 0; font: 12pt/15pt sans-serif }
              p { margin: 0; width: {{EntryWidth}}pt }
              h1 { margin: 0; font: 12pt/15pt sans-serif }
              @page :first { counter-set: page 10 }
              a::after { content: target-counter(attr(href), page) }
            </style></head><body>
              <p>See <a href="#t"></a></p>
              <div style="height: 560pt"></div>
              <h1 id="t">Target</h1>
            </body></html>
            """;

        var flow = HtmlToPdf.CreateFlowDocument(html);

        Assert.Equal(2, flow.MapPages().Destinations["t"]);
        Assert.Equal("See 11", Text(Paragraphs(flow.Blocks).First()));
    }

    [Fact]
    public void A_table_of_contents_shows_titles_and_pages_with_leaders()
    {
        var flow = HtmlToPdf.CreateFlowDocument(TableOfContents());
        var pages = flow.MapPages().Destinations;
        var entries = Paragraphs(flow.Blocks).Where(p => p.Spans.Any(s => s.IsLeader)).ToList();

        Assert.Equal(5, entries.Count);
        string[] ids = ["intro", "scope", "method", "data", "results"];
        string[] titles = ["1. Introduction", "1.1. Scope", "2. Method", "2.1. Data", "3. Results"];
        for (var i = 0; i < entries.Count; i++)
        {
            var spans = entries[i].Spans;
            var leader = spans.Single(s => s.IsLeader);
            Assert.Equal(". ", leader.Text);
            Assert.Equal(titles[i], string.Concat(spans.TakeWhile(s => !s.IsLeader).Select(s => s.Text)));
            Assert.Equal(pages[ids[i]].ToString(System.Globalization.CultureInfo.InvariantCulture), string.Concat(spans.SkipWhile(s => !s.IsLeader).Skip(1).Select(s => s.Text)));

            // The leader fills the line, so the page number ends at the right edge.
            var width = flow.Setup.ContentWidth;
            var line = Assert.Single(entries[i].Layout(width).Lines);
            Assert.Equal(width, line.X + line.Width, 3);
        }

        Assert.Equal([2, 2, 3, 4, 5], ids.Select(id => pages[id]));
    }

    [Fact]
    public void Table_of_contents_page_numbers_render()
    {
        var pages = PageTexts(TableOfContents());

        Assert.Equal(5, pages.Count);
        static string NoSpaces(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        var toc = NoSpaces(pages[0]);
        Assert.Contains("1.Introduction....", toc, StringComparison.Ordinal);
        Assert.Matches(@"1\.Introduction(\.)+2", toc);
        Assert.Matches(@"2\.1\.Data(\.)+4", toc);
        Assert.Matches(@"3\.Results(\.)+5", toc);
        Assert.Contains("2.1. Data", pages[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Margin_boxes_show_page_context_counters()
    {
        var pages = PageTexts(Html(
            Chapters(3),
            """
            @page { size: A5; margin: 20mm 10mm; counter-increment: sheet 2 leaf;
                    @bottom-center { content: 'p' counter(page) ' s' counter(sheet) '/' counter(pages) ' l' counter(leaf, lower-alpha) }
                    @bottom-right { content: 'c' counter(chapter) } }
            @page :first { counter-set: page 7 }
            h1 { counter-increment: chapter }
            """));

        Assert.Equal(3, pages.Count);
        // The page counter starts at 7 (counter-set on the first page) and goes on from there; sheet counts by 2 and
        // leaf by 1 (the default increment);
        // document counters (chapter) are not in the page context's scope.
        Assert.Contains("p7 s2/3 la", pages[0], StringComparison.Ordinal);
        Assert.Contains("p8 s4/3 lb", pages[1], StringComparison.Ordinal);
        Assert.Contains("p9 s6/3 lc", pages[2], StringComparison.Ordinal);
        Assert.All(pages, page => Assert.Contains("c0", page, StringComparison.Ordinal));
    }

    /// <summary>
    /// Front matter on a named page that does not advance the page counter (and counts its own pages), then a
    /// right-hand break that inserts a blank page, which counts by 10.
    /// </summary>
    private static string NamedPagesDocument() => Html(
        """
        <section class="front"><h1>Title</h1><p><a href="#one">one</a> <a href="#end">end</a></p><h1 style="break-before: page">Preface</h1></section>
        <h1 id="one">One</h1>
        <h1 id="end" style="break-before: right">End</h1>
        """,
        """
        @page { size: A5; @bottom-center { content: 'p' counter(page) ' f' counter(front, upper-alpha) } }
        @page front { counter-increment: page 0 front }
        @page :blank { counter-increment: page 10 }
        .front { page: front }
        a::after { content: ' [' target-counter(attr(href), page) ']' }
        """);

    [Fact]
    public void Named_and_blank_page_rules_change_the_page_counters()
    {
        var pages = PageTexts(NamedPagesDocument());

        // Pages 1-2 are front pages; 3 starts the unnamed pages (on the right); 4 is blank (End goes on a right page).
        Assert.Equal(5, pages.Count);
        string[] footers = ["p0 fA", "p0 fB", "p1 fB", "p11 fB", "p12 fB"];
        for (var i = 0; i < footers.Length; i++)
        {
            Assert.Contains(footers[i], pages[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Page_references_follow_named_and_blank_page_counters()
    {
        var flow = HtmlToPdf.CreateFlowDocument(NamedPagesDocument());
        var map = flow.MapPages();

        Assert.Equal(["front", "front", null, null, null], map.Pages.Select(p => p.Name));
        Assert.True(map.Pages[3].IsBlank);
        Assert.Equal(3, map.Destinations["one"]);
        Assert.Equal(5, map.Destinations["end"]);
        Assert.Equal("one [1] end [12]", Text(Paragraphs(flow.Blocks).Single(p => Text(p).StartsWith("one", StringComparison.Ordinal))));
    }

    [Fact]
    public void Page_references_use_the_page_counter()
    {
        var flow = HtmlToPdf.CreateFlowDocument(Html(
            "<p><a href='#last'>Last</a></p>" + Chapters(3).Replace("<h1 style='break-before: page'>Chapter 3", "<h1 id='last' style='break-before: page'>Chapter 3", StringComparison.Ordinal),
            "@page { size: A5 } @page :first { counter-set: page 10 } a::after { content: ' ' target-counter(attr(href), page, lower-roman) }"));

        Assert.Equal("Last xii", Text(Paragraphs(flow.Blocks).First()));
    }

    [Fact]
    public void Running_strings_carry_document_counters_to_margin_boxes()
    {
        var pages = PageTexts(Html(
            Chapters(2),
            "@page { size: A5; @top-center { content: string(chapter) } } h1 { counter-increment: c; string-set: chapter 'Part ' counter(c, upper-roman) }"));

        Assert.Contains("Part I", pages[0], StringComparison.Ordinal);
        Assert.Contains("Part II", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Content_values_parse_nested_functions()
    {
        var items = ContentValue.Parse("leader('.') target-counter(attr(href url), page, lower-roman) counters(a, '.', upper-alpha) target-text(url('#x'), content(before)) leader(solid) leader('dotted')");

        Assert.Equal(
            [
                new LeaderItem("."),
                new TargetCounterItem(new TargetReference("href", null), "page", "lower-roman"),
                new CountersItem("a", ".", "upper-alpha"),
                new TargetTextItem(new TargetReference(null, "#x"), "before"),
                new LeaderItem("_"),
                new LeaderItem("dotted"),
            ],
            items!);
    }

    /// <summary>A table of contents on page 1 for numbered sections on pages 2 to 5.</summary>
    private static string TableOfContents() => Html(
        """
        <nav>
          <h1 class="toc">Contents</h1>
          <ul>
            <li><a href="#intro">Introduction</a></li>
            <li class="sub"><a href="#scope">Scope</a></li>
            <li><a href="#method">Method</a></li>
            <li class="sub"><a href="#data">Data</a></li>
            <li><a href="#results">Results</a></li>
          </ul>
        </nav>
        <h1 id="intro">Introduction</h1><p>{F}</p>
        <h2 id="scope">Scope</h2><p>{F}</p>
        <h1 id="method">Method</h1><p>{F}</p><p>{LONG}</p>
        <h2 id="data">Data</h2><p>{F}</p>
        <h1 id="results">Results</h1><p>{F}</p>
        """.Replace("{LONG}", string.Concat(Enumerable.Repeat(Filler, 25)), StringComparison.Ordinal).Replace("{F}", Filler, StringComparison.Ordinal),
        """
        @page { size: A5; margin: 15mm 12mm }
        body { counter-reset: chapter }
        h1:not(.toc) { counter-increment: chapter; counter-reset: section; break-before: page }
        h2 { counter-increment: section }
        h1:not(.toc)::before { content: counter(chapter) '. ' }
        h2::before { content: counter(chapter) '.' counter(section) '. ' }
        nav ul { list-style: none; padding: 0 }
        nav li.sub { padding-left: 0 }
        nav a { color: black; text-decoration: none }
        nav a::before { content: target-text(attr(href), before) }
        nav a::after { content: leader(dotted) target-counter(attr(href), page) }
        """);

    /// <summary>A width for "See 0" to fit on one line but not "See 10" (12pt Liberation Sans).</summary>
    private const int EntryWidth = 32;

    private static string Html(string body, string css) =>
        $"<html><head><style>body {{ font-family: sans-serif }} {css}</style></head><body>{body}</body></html>";

    private static string Chapters(int count) =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"<h1 style='{(i > 1 ? "break-before: page" : "")}'>Chapter {i}</h1><p>{Filler}</p>"));

    private static List<string> PageTexts(string html) =>
        [.. ExternalTools.ExtractText(HtmlToPdf.ConvertToBytes(html)).Split('\f').Where(p => p.Trim().Length > 0)];

    private static List<string> Texts(string body, string css) =>
        [.. Paragraphs(HtmlToPdf.CreateFlowDocument(Html(body, css)).Blocks).Select(Text)];

    private static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks) =>
        Flatten(blocks).OfType<ParagraphBlock>().Select(p => p.Paragraph);

    private static string Text(Paragraph paragraph) => string.Concat(paragraph.Spans.Select(s => s.Text));

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b switch
        {
            ContainerBlock c => Flatten(c.Children).Prepend(b),
            TableBlock t => Flatten(t.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Content)).Prepend(b),
            FlexBlock f => Flatten(f.Items.Select(i => i.Content)).Prepend(b),
            _ => [b],
        });
}
