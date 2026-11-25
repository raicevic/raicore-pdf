using System.Text.RegularExpressions;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Tests.Tagging;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>HTML fragments in documents built with the layout API (#197).</summary>
public class HtmlFragmentTests
{
    /// <summary>A 1 x 1 PNG.</summary>
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    /// <summary>Hebrew "shalom olam", for bidirectional text.</summary>
    private const string Hebrew = "\u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD";

    /// <summary>Rich text as it might come from a database: headings, inline styles, a link, lists, a table, an image, bidi text.</summary>
    private const string RichText = """
        <h2>Customer notes</h2>
        <p>The order was <b>shipped</b> on <i>Monday</i> with <u>tracking</u> and <a href="https://example.com/track">a link</a>.
        An inline image <img src="PIXEL" width="16" height="16" alt="dot"> sits in the text, which wraps onto a second line
        because it is long enough to need one at this width.</p>
        <ul><li>First item</li><li>Second item with <code>code</code></li></ul>
        <ol start="3"><li>Three</li><li>Four</li></ol>
        <table border="1" style="border-collapse: collapse; width: 100%"><tr><th>Item</th><th>Qty</th></tr><tr><td>Widget</td><td>3</td></tr></table>
        <p dir="rtl">HEBREW and English</p>
        <p style="font-size: 0.8em; color: #a00">Small red text at the end.</p>
        """;

    private static readonly FontCollection Fonts = BundledFonts.CreateCollection();

    private static readonly PageSetup Page = new(PdfPageSize.A4, Thickness.All(50));

    private static readonly TextStyle Body = new(Fonts.Find("serif")!, 11) { LineHeight = LineHeight.Multiple(1.3), Color = PdfColor.FromRgb(20, 20, 60) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Rich => RichText.Replace("PIXEL", Pixel, StringComparison.Ordinal).Replace("HEBREW", Hebrew, StringComparison.Ordinal);

    private static HtmlFragmentOptions Options(TextStyle? baseStyle = null, string? css = null) =>
        new() { BaseStyle = baseStyle, Css = css, Fonts = Fonts, Width = Page.ContentWidth };

    /// <summary>The words of a page with where they start (user space).</summary>
    private static List<(string Text, double X, double Y)> Words(byte[] pdf, int page = 0) =>
        [.. new TextExtractor(PdfReader.Open(pdf)).FindOnPage(page, new Regex(@"\S+")).Select(m => (m.Text, m.Bounds.X, m.Bounds.Y))];

    /// <summary>The text styles of a paragraph block's laid-out text, in order.</summary>
    private static List<(string Text, TextStyle Style)> Runs(Block block, double width = 400) =>
        [.. Assert.IsType<ParagraphBlock>(block).Paragraph.Layout(width).Lines.SelectMany(l => l.Fragments).Select(f => (f.Run.Text, f.Style))];

    /// <summary>The fragment's first paragraph block, depth first.</summary>
    private static ParagraphBlock FirstParagraph(IEnumerable<Block> blocks) =>
        Descendants(blocks).OfType<ParagraphBlock>().First();

    private static IEnumerable<Block> Descendants(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Descendants(c.Children).Prepend(b) : [b]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_fragment_between_code_built_paragraphs_renders_as_in_a_converted_document(bool withBaseStyle)
    {
        // The same HTML converted as a whole document whose body has the base style, on a page of the same content width.
        const string bodyCss = "body { margin: 0; font-family: 'Liberation Serif'; font-size: 11pt; line-height: 1.3; color: rgb(20, 20, 60) }";
        var converted = HtmlToPdf.Convert(
            $"<html><head><style>{(withBaseStyle ? bodyCss : "body { margin: 0 }")}</style></head><body>{Rich}</body></html>",
            new HtmlConverterOptions { Page = Page, Fonts = Fonts }).ToArray();

        var flow = new FlowDocument(Page);
        flow.Add(new ParagraphBlock(new Paragraph(Body).Add("Before the fragment.")));
        var fragment = HtmlFragment.Parse(Rich, Options(withBaseStyle ? Body : null));
        Assert.Same(flow, fragment.AddTo(flow));
        flow.Add(new ParagraphBlock(new Paragraph(Body).Add("After the fragment.")));
        var built = flow.Render().ToArray();

        Assert.Empty(fragment.Diagnostics);
        var expected = Words(converted);
        var actual = Words(built);
        Assert.Equal(["Before", "the", "fragment."], actual.Take(3).Select(w => w.Text));
        Assert.Equal(["After", "the", "fragment."], actual.TakeLast(3).Select(w => w.Text));
        var words = actual.Skip(3).SkipLast(3).ToList();
        Assert.Equal(expected.Select(w => w.Text), words.Select(w => w.Text));
        Assert.True(expected.Count > 40);

        // Every word at the same place, the whole fragment moved down by the paragraph before it.
        var dy = words[0].Y - expected[0].Y;
        Assert.InRange(dy, -60, -10);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(Math.Abs(words[i].X - expected[i].X) < 0.01 && Math.Abs(words[i].Y - dy - expected[i].Y) < 0.01, $"{words[i]} vs {expected[i]}");
        }

        // The text wrapped (two lines) and the right-to-left paragraph is right-aligned.
        Assert.Equal(2, words.Where(w => w.Text is "The" or "because").Select(w => Math.Round(w.Y)).Distinct().Count());
        Assert.True(words.Single(w => w.Text == "English").X > Page.ContentWidth / 2);
    }

    [Fact]
    public void The_base_style_is_inherited_and_relative_sizes_follow_it()
    {
        var fragment = HtmlFragment.Parse("<h1>Title</h1><p>Plain <b>bold</b> <i>italic</i> <small>small</small></p>", Options(Body with { LetterSpacing = 0.5, WordSpacing = 1 }));

        var heading = Runs(fragment.Blocks[0]).Single().Style;
        Assert.Equal(22, heading.FontSize, 6); // h1 is 2em.
        Assert.Equal("Liberation Serif", heading.Fonts[0].FamilyName);
        Assert.Equal(700, heading.Fonts[0].Weight);

        var runs = Runs(fragment.Blocks[1]);
        var plain = runs.First(r => r.Text.StartsWith("Plain", StringComparison.Ordinal)).Style;
        Assert.Same(Body.Fonts[0], plain.Fonts[0]);
        Assert.Equal(11, plain.FontSize, 6);
        Assert.Equal(Body.Color, plain.Color);
        Assert.Equal(LineHeight.Multiple(1.3), plain.LineHeight);
        Assert.Equal(0.5, plain.LetterSpacing, 6);
        Assert.Equal(1, plain.WordSpacing, 6);
        Assert.Equal(Fonts.Find("Liberation Serif", 700), runs.Single(r => r.Text == "bold").Style.Fonts[0]);
        Assert.Equal(Fonts.Find("Liberation Serif", 400, italic: true), runs.Single(r => r.Text == "italic").Style.Fonts[0]);
        Assert.Equal(8.8, runs.Single(r => r.Text == "small").Style.FontSize, 3); // smaller.
    }

    [Fact]
    public void The_fragments_css_overrides_the_base_style_and_the_css_option_comes_before_the_fragments_own()
    {
        const string html = """
            <style>.own { color: #00ff00 }</style>
            <p class="note">note</p><p class="own note">own</p><p class="note" style="font-size: 8pt">inline</p><p>plain</p>
            """;
        var fragment = HtmlFragment.Parse(html, Options(Body, css: ".note { color: #ff0000; font-size: 15pt } p { line-height: 2 }"));

        var styles = fragment.Blocks.Select(b => Runs(b).Single().Style).ToList();
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), styles[0].Color);
        Assert.Equal(15, styles[0].FontSize, 6);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), styles[1].Color); // The fragment's <style> wins over the option.
        Assert.Equal(8, styles[2].FontSize, 6); // So does its style attribute.
        Assert.Equal(Body.Color, styles[3].Color);
        Assert.Equal(11, styles[3].FontSize, 6);
        Assert.All(styles, s => Assert.Equal(LineHeight.Multiple(2), s.LineHeight));

        // A root rule of the fragment overrides the base style for everything.
        var root = HtmlFragment.Parse("<style>html { font-size: 20px; color: blue }</style><p>x</p>", Options(Body));
        var style = Runs(root.Blocks[0]).Single().Style;
        Assert.Equal(15, style.FontSize, 6);
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), style.Color);
    }

    [Fact]
    public void Base_fonts_outside_the_collection_are_used_for_their_family()
    {
        // The collection has no Liberation Mono: the base fonts give its faces.
        var courier = Fonts.Find("Liberation Mono")!;
        var courierBold = Fonts.Find("Liberation Mono", 700)!;
        var sansOnly = new FontCollection().AddFont(Fonts.Find("Liberation Sans")!);
        var fragment = HtmlFragment.Parse(
            "<p>plain <b>bold</b> <i>italic</i> <span style=\"font-family: sans-serif\">sans</span></p>",
            new HtmlFragmentOptions { Fonts = sansOnly, BaseStyle = new TextStyle([courier, courierBold], 10) });

        var runs = Runs(fragment.Blocks[0]);
        Assert.Same(courier, runs.Single(r => r.Text.StartsWith("plain", StringComparison.Ordinal)).Style.Fonts[0]);
        Assert.Same(courierBold, runs.Single(r => r.Text == "bold").Style.Fonts[0]);
        Assert.Same(courier, runs.Single(r => r.Text == "italic").Style.Fonts[0]); // The closest face there is.
        Assert.Equal("Liberation Sans", runs.Single(r => r.Text == "sans").Style.Fonts[0].FamilyName);

        // The closest face, whatever the order of the base fonts.
        var reversed = HtmlFragment.Parse("<p><span style=\"font-weight: normal; font-style: italic\">italic</span></p>", new HtmlFragmentOptions { Fonts = sansOnly, BaseStyle = new TextStyle([courierBold, courier], 10) });
        Assert.Same(courier, Runs(reversed.Blocks[0]).Single().Style.Fonts[0]);

        // Every family of the base fonts is named, in order: the others are fallbacks for characters the first lacks.
        var arabic = PdfFont.Load(Fixtures.Path("NotoSansArabic-Subset.ttf"));
        var fallback = HtmlFragment.Parse("<p>text</p>", Options(new TextStyle([Body.Fonts[0], arabic], 10)));
        var fonts = Runs(fallback.Blocks[0]).Single().Style.Fonts;
        Assert.Same(Body.Fonts[0], fonts[0]);
        Assert.Same(arabic, fonts[1]);

        // A bold base font makes the fragment's text bold, and its language is the fragment's.
        var boldBase = HtmlFragment.Parse("<p>text</p>", Options(new TextStyle(Fonts.Find("sans-serif", 700)!, 10) { Language = "de", Hyphens = Hyphens.Auto }));
        var style = Runs(boldBase.Blocks[0]).Single().Style;
        Assert.Equal(700, style.Fonts[0].Weight);
        Assert.Equal("de", style.Language);
        Assert.Equal(Hyphens.Auto, style.Hyphens);
    }

    [Fact]
    public void Base_style_colors_line_heights_and_italic_fonts_become_css()
    {
        var italic = Fonts.Find("serif", 400, italic: true)!;
        var css = HtmlToPdf.FragmentRootCss(new TextStyle(italic, 9) { Color = new PdfColor(1, 0.5, 0, 0.5), LineHeight = LineHeight.Points(12), Hyphens = Hyphens.None });
        Assert.Contains("font-family: \"Liberation Serif\"", css, StringComparison.Ordinal);
        Assert.Contains("font-style: italic", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 12px", css, StringComparison.Ordinal);
        Assert.Contains("color: rgba(255, 128, 0, 0.5)", css, StringComparison.Ordinal);
        Assert.Contains("line-height: 16px", css, StringComparison.Ordinal);
        Assert.Contains("hyphens: none", css, StringComparison.Ordinal);
        Assert.Equal(":where(body) { margin: 0; }", HtmlToPdf.FragmentRootCss(null));

        var style = Runs(HtmlFragment.Parse("<p>x</p>", Options(new TextStyle(italic, 9) { LineHeight = LineHeight.Points(12) })).Blocks[0]).Single().Style;
        Assert.Same(italic, style.Fonts[0]);
        Assert.Equal(LineHeight.Points(12), style.LineHeight);
        Assert.Equal(Hyphens.Manual, style.Hyphens);
    }

    [Fact]
    public void Without_a_base_style_a_fragment_starts_from_the_html_defaults()
    {
        var fragment = HtmlFragment.Parse("Just text, with <b>bold</b>.");

        var block = Assert.Single(fragment.Blocks);
        var plain = Runs(block)[0].Style;
        Assert.Equal(12, plain.FontSize, 6); // 16px.
        Assert.Equal("Liberation Serif", plain.Fonts[0].FamilyName);
        Assert.Equal(PdfColor.Black, plain.Color);
        Assert.Equal(BoxStyle.Default.Margin, block.Style.Margin); // No body margin.
    }

    [Fact]
    public void A_fragment_goes_in_a_table_cell()
    {
        var cell = HtmlFragment.Parse("<p style=\"margin: 0\">Cell with <b>bold</b>:</p><ul style=\"margin: 0\"><li>alpha</li><li>beta</li></ul>", Options(Body));
        var cellStyle = new BoxStyle { Border = Borders.All(0.5, PdfColor.Black), Padding = Thickness.All(4) };
        var table = new TableBlock { ColumnWidths = [Length.FromPoints(100), Length.FromPoints(150)] }
            .Add(new TableRow(new TableCell(new Paragraph(Body).Add("Plain")) { Style = cellStyle }, new TableCell(cell.Blocks) { Style = cellStyle }));
        var pdf = new FlowDocument(Page).Add(table).Render().ToArray();

        var words = Words(pdf);
        Assert.Equal(["Plain", "Cell", "with", "bold:", "\u2022", "alpha", "\u2022", "beta"], words.Select(w => w.Text));
        var left = Page.Margins.Left.Points + 100;
        Assert.All(words.Skip(1), w => Assert.InRange(w.X, left, left + 150));
        Assert.Equal(words[0].Y, words[1].Y, 0.5); // Both cells start at the top (HTML text has whole-pixel font metrics).
        Assert.True(words[5].Y < words[1].Y - 10);
    }

    [Fact]
    public void A_fragment_goes_in_a_container_block()
    {
        var fragment = HtmlFragment.Parse("<p>one</p><p>two</p>", Options(Body));
        var container = new ContainerBlock(new BoxStyle { Padding = Thickness.All(5) });

        Assert.Same(container, fragment.AddTo(container));
        Assert.Equal(fragment.Blocks, container.Children);
        Assert.Equal(2, container.Children.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("<!-- a comment -->")]
    [InlineData("<style>p { color: red }</style>")]
    public void An_empty_fragment_has_no_blocks(string html)
    {
        var fragment = HtmlFragment.Parse(html, Options(Body));
        var flow = new FlowDocument(Page);

        Assert.Empty(fragment.Blocks);
        Assert.Empty(fragment.Diagnostics);
        Assert.Empty(fragment.AddTo(flow).Blocks);
    }

    [Fact]
    public void A_whole_document_is_a_fragment_too()
    {
        var fragment = HtmlFragment.Parse("<!DOCTYPE html><html><head><title>T</title><style>p { color: #0000ff }</style></head><body><p>Body text</p></body></html>", Options(Body));

        var style = Runs(Assert.Single(fragment.Blocks)).Single().Style;
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), style.Color);
    }

    [Fact]
    public void A_styled_body_keeps_its_box()
    {
        var fragment = HtmlFragment.Parse("<p>one</p><p>two</p>", Options(Body, css: "body { background: #eee; padding: 4px }"));

        var body = Assert.IsType<ContainerBlock>(Assert.Single(fragment.Blocks));
        Assert.Equal(3, body.Style.Padding.Top.Points, 6);
        Assert.Equal(2, body.Children.Count);
    }

    [Theory]
    [InlineData("border: 1px solid")]
    [InlineData("background-image: linear-gradient(red, blue)")]
    [InlineData("margin-top: 3px")]
    public void A_body_that_draws_something_keeps_its_box(string css)
    {
        var fragment = HtmlFragment.Parse("<p>one</p><p>two</p>", Options(Body, css: $"body {{ {css} }}"));

        Assert.Equal(2, Assert.IsType<ContainerBlock>(Assert.Single(fragment.Blocks)).Children.Count);
    }

    [Fact]
    public void A_body_with_an_id_keeps_its_box_as_the_links_destination()
    {
        var fragment = HtmlFragment.Parse("<body id=\"notes\"><p>one</p><p>two</p></body>", Options(Body));

        var body = Assert.IsType<ContainerBlock>(Assert.Single(fragment.Blocks));
        Assert.Equal("notes", body.Id);
        Assert.Equal(2, body.Children.Count);
    }

    [Fact]
    public void The_base_styles_own_fonts_win_over_the_collections_faces_of_their_family()
    {
        var path = Fixtures.Path("RaiCoreTestTT-Regular.ttf");
        var own = PdfFont.Load(path);
        var collection = new FontCollection().AddFile(path);
        var fragment = HtmlFragment.Parse("<p>abc</p>", new HtmlFragmentOptions { Fonts = collection, BaseStyle = new TextStyle(own, 10) });

        var font = Runs(fragment.Blocks[0]).Single().Style.Fonts[0];
        Assert.Same(own, font);
        Assert.NotSame(collection.Find(own.FamilyName), font);
    }

    [Fact]
    public void Malformed_html_is_repaired_as_browsers_do()
    {
        var fragment = HtmlFragment.Parse("<p>unclosed <b>bold <i>both</p><td>stray</td></table><div>open <span>span", Options(Body));
        var pdf = new FlowDocument(Page).Add(new ContainerBlock(null, fragment.Blocks)).Render().ToArray();

        Assert.Equal(["unclosed", "bold", "both", "stray", "open", "span"], Words(pdf).Select(w => w.Text));
    }

    [Fact]
    public void Images_are_loaded_through_the_resource_loader_relative_to_the_base_uri()
    {
        var requests = new List<ResourceRequest>();
        var options = new HtmlFragmentOptions
        {
            Fonts = Fonts,
            BaseUri = new Uri("https://cdn.example.com/notes/"),
            ResourceLoader = request =>
            {
                requests.Add(request);
                return request.Uri.AbsolutePath.EndsWith("dot.png", StringComparison.Ordinal) ? ResourceLoaders.DecodeDataUri(Pixel) : null;
            },
        };
        var fragment = HtmlFragment.Parse("<p>An <img src=\"images/dot.png\" width=\"10\" height=\"10\" alt=\"dot\"> inline image.</p><img src=\"missing.png\" alt=\"gone\" style=\"display: block\"><img src=\"images/dot.png\" style=\"display: block; width: 50px\">", options);

        Assert.Equal(["https://cdn.example.com/notes/images/dot.png", "https://cdn.example.com/notes/missing.png"], requests.Select(r => r.Uri.AbsoluteUri).Distinct());
        Assert.All(requests, r => Assert.Equal(ResourceKind.Image, r.Kind));
        Assert.NotNull(FirstParagraph(fragment.Blocks).Paragraph.Layout(300).Lines.Single().Fragments);
        var image = Descendants(fragment.Blocks).OfType<ImageBlock>().Single();
        Assert.Equal(1, image.Image.PixelWidth);

        var missing = Assert.Single(fragment.Diagnostics);
        Assert.Equal(HtmlDiagnosticKind.ResourceNotLoaded, missing.Kind);
        Assert.Equal("missing.png", missing.Subject);
        Assert.Equal("html > body > img:nth-of-type(1)", missing.ElementPath);

        // The inline image is drawn in the document the fragment is placed in.
        var document = new FlowDocument(Page);
        fragment.AddTo(document);
        Assert.Contains("/Image", System.Text.Encoding.Latin1.GetString(document.Render().ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void What_only_pages_have_is_left_out_and_reported()
    {
        const string html = """
            <style>
              @page { size: A5; @top-center { content: element(header) } }
              .header { position: running(header) }
              h1 { string-set: chapter content() }
              .wide { page: wide }
              a::after { content: " (page " target-counter(attr(href), page) ")" }
              p { box-decoration-break: clone }
            </style>
            <div class="header">Running header</div>
            <h1>Chapter</h1>
            <div class="wide">Wide content</div>
            <p><a href="#target">see</a></p>
            <p id="target">Target</p>
            """;
        var fragment = HtmlFragment.Parse(html, Options(Body));

        var reported = fragment.Diagnostics.ToDictionary(d => d.Subject);
        Assert.Equal(HtmlDiagnosticKind.UnsupportedRule, reported["@page"].Kind);
        Assert.Equal(HtmlDiagnosticKind.UnsupportedValue, reported["position: running()"].Kind);
        Assert.Equal("html > body > div.header:nth-of-type(1)", reported["position: running()"].ElementPath);
        Assert.Equal(HtmlDiagnosticKind.UnsupportedProperty, reported["string-set"].Kind);
        Assert.Equal("html > body > h1", reported["string-set"].ElementPath);
        Assert.Equal(HtmlDiagnosticKind.UnsupportedProperty, reported["page"].Kind);
        Assert.Equal(HtmlDiagnosticKind.UnsupportedValue, reported["target-counter(..., page)"].Kind);
        Assert.Contains(fragment.Diagnostics, d => d.Subject.Contains("box-decoration-break", StringComparison.Ordinal)); // The usual CSS diagnostics too.
        Assert.All(fragment.Diagnostics.Where(d => d.Subject != "box-decoration-break"), d => Assert.Equal(HtmlDiagnosticSeverity.Warning, d.Severity));

        // The running element stays in the flow, no block has a page name or a running-string marker, and the page
        // reference shows the placeholder.
        var pdf = new FlowDocument(Page).Add(new ContainerBlock(null, fragment.Blocks)).Render().ToArray();
        Assert.Equal(["Running", "header", "Chapter", "Wide", "content", "see", "(page", "0)", "Target"], Words(pdf).Select(w => w.Text));
        Assert.All(Descendants(fragment.Blocks), b => Assert.True(b.PageName is null && b.Marker is null));
        Assert.Null(fragment.Blocks.OfType<ParagraphBlock>().First().Bookmark); // No bookmarks by default.
    }

    [Fact]
    public void Headings_become_bookmarks_only_when_asked()
    {
        const string html = "<h1>Chapter</h1><p>text</p><h2>Section</h2>";
        var none = new FlowDocument(Page);
        HtmlFragment.Parse(html, Options(Body)).AddTo(none);
        Assert.Empty(none.Render().Outline);

        var flow = new FlowDocument(Page);
        HtmlFragment.Parse(html, Options(Body)).AddTo(flow);
        HtmlFragment.Parse(html, new HtmlFragmentOptions { Fonts = Fonts, CreateBookmarks = true }).AddTo(flow);
        var outline = flow.Render().Outline;
        var chapter = Assert.Single(outline);
        Assert.Equal("Chapter", chapter.Title);
        Assert.Equal("Section", Assert.Single(chapter.Children).Title);
    }

    [Fact]
    public void Ids_and_internal_links_work_in_the_document_and_page_breaks_apply()
    {
        const string html = """<p><a href="#later">Jump</a></p><div style="break-before: page"><p id="later">Later</p></div>""";
        var flow = new FlowDocument(Page);
        flow.Add(new ParagraphBlock(new Paragraph(Body).Add("Code")));
        HtmlFragment.Parse(html, Options(Body)).AddTo(flow);
        var document = flow.Render();

        Assert.Equal(2, document.Pages.Count);
        Assert.True(document.NamedDestinations.ContainsKey("later"));
        Assert.Equal(["Later"], Words(document.ToArray(), page: 1).Select(w => w.Text));
    }

    [Fact]
    public void Percentages_and_viewport_units_resolve_against_the_width()
    {
        var fragment = HtmlFragment.Parse("<div style=\"margin-left: 10%; padding: 5%\">x</div><div style=\"width: 50vw\">y</div><p style=\"text-indent: 10%\">z</p>", new HtmlFragmentOptions { Fonts = Fonts, Width = 300 });

        Assert.Equal(30, fragment.Blocks[0].Style.Margin.Left.Resolve(300), 6);
        Assert.Equal(15, fragment.Blocks[0].Style.Padding.Left.Resolve(300), 6);
        Assert.Equal(150, fragment.Blocks[1].Style.Width!.Value.Points, 6); // vw: of the width.
        Assert.Equal(30, Assert.IsType<ParagraphBlock>(fragment.Blocks[2]).Paragraph.FirstLineIndent, 6); // Resolved while the blocks are built.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void The_width_must_be_positive(double width)
    {
        Assert.Throws<ArgumentException>(() => HtmlFragment.Parse("<p>x</p>", new HtmlFragmentOptions { Width = width }));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlFragment.Parse(null!));
        var fragment = HtmlFragment.Parse("<p>x</p>");
        Assert.Throws<ArgumentNullException>(() => fragment.AddTo((FlowDocument)null!));
        Assert.Throws<ArgumentNullException>(() => fragment.AddTo((ContainerBlock)null!));
        Assert.Throws<ArgumentException>(() => HtmlFragment.Parse("<p>x</p>", new HtmlFragmentOptions { Limits = null! }));
        Assert.Throws<ArgumentException>(() => HtmlFragment.Parse("<p>x</p>", new HtmlFragmentOptions { ResourceLoader = null! }));
    }

    [Fact]
    public void Resource_limits_apply()
    {
        var nested = string.Concat(Enumerable.Repeat("<div>", 30)) + "deep";
        var limited = new HtmlFragmentOptions { Fonts = Fonts, Limits = ResourceLimits.Default with { MaxNestingDepth = 20 } };
        var ex = Assert.Throws<ResourceLimitExceededException>(() => HtmlFragment.Parse(nested, limited));
        Assert.Equal(nameof(ResourceLimits.MaxNestingDepth), ex.LimitName);

        var elements = new HtmlFragmentOptions { Fonts = Fonts, Limits = ResourceLimits.Default with { MaxElements = 10 } };
        Assert.Throws<ResourceLimitExceededException>(() => HtmlFragment.Parse(string.Concat(Enumerable.Repeat("<p>x</p>", 10)), elements));

        // An image over a size limit is skipped and reported.
        var small = new HtmlFragmentOptions { Fonts = Fonts, Limits = ResourceLimits.Default with { MaxResourceBytes = 10 } };
        var fragment = HtmlFragment.Parse($"<p><img src=\"{Pixel}\" alt=\"dot\">text</p>", small);
        Assert.Equal(HtmlDiagnosticKind.ResourceTooLarge, Assert.Single(fragment.Diagnostics).Kind);
    }

    [Fact]
    public async Task Parsing_asynchronously_gives_the_same_blocks_and_observes_cancellation()
    {
        var fragment = await HtmlFragment.ParseAsync(Rich, Options(Body), Ct);
        var flow = new FlowDocument(Page);
        fragment.AddTo(flow);
        Assert.Equal(Words(HtmlFragment.Parse(Rich, Options(Body)).AddTo(new FlowDocument(Page)).Render().ToArray()).Select(w => w.Text), Words(flow.Render().ToArray()).Select(w => w.Text));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlFragment.ParseAsync("<p>x</p>", cancellationToken: new CancellationToken(canceled: true)));

        // Canceled while it runs: a loader that cancels.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var options = new HtmlFragmentOptions
        {
            Fonts = Fonts,
            BaseUri = new Uri("https://example.com/"),
            ResourceLoader = request =>
            {
                cancel.Cancel();
                return null;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HtmlFragment.ParseAsync("<p><img src=\"a.png\"><img src=\"b.png\"></p>" + string.Concat(Enumerable.Repeat("<p>more</p>", 200)), options, cancel.Token));
    }

    [Fact]
    public void Fragment_blocks_are_tagged_as_in_a_converted_document()
    {
        const string html = """
            <h1>Quarterly report</h1>
            <p>Read the <a href="https://example.com/q3">full figures</a> online.</p>
            <ol><li>Revenue grew</li><li>Costs fell</li></ol>
            <p><img src="PIXEL" alt="Company logo" width="20" height="20"> Logo</p>
            <table><thead><tr><th>Region</th><th>Sales</th></tr></thead><tbody><tr><th>North</th><td>10</td></tr></tbody></table>
            <p lang="da">Tak for i dag.</p>
            """;
        var body = html.Replace("PIXEL", Pixel, StringComparison.Ordinal);
        var converted = HtmlToPdf.Convert($"<html lang=\"en\"><body>{body}</body></html>", new HtmlConverterOptions { Tagged = true, StrictTagging = true, Fonts = Fonts }).ToArray();

        var document = new PdfDocument { Tagged = true, StrictTagging = true, Language = "en" };
        var flow = new FlowDocument(Page);
        flow.Add(new ParagraphBlock(new Paragraph(Body).Add("Code-built introduction.")));
        HtmlFragment.Parse(body, Options(Body)).AddTo(flow);
        flow.RenderTo(document);

        var expected = TaggedPdfInspector.Inspect(converted).ToString();
        var actual = TaggedPdfInspector.Inspect(document.ToArray()).ToString();
        Assert.StartsWith("Document(H1 P(Link) L(", expected, StringComparison.Ordinal);
        Assert.Equal("Document(P " + expected["Document(".Length..], actual);
    }
}
