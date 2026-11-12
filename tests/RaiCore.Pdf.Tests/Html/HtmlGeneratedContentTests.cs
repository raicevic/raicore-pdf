using System.Text.RegularExpressions;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>Generated content: ::before and ::after with content strings, attr(), quotes and images.</summary>
public class HtmlGeneratedContentTests
{
    private const double Px = 0.75;
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void Rules_with_selectors_AngleSharp_cannot_parse_are_ignored()
    {
        // AngleSharp gives ::marker rules no selector text, which crashed the search for elements with pseudo-elements.
        var texts = Texts("<ol><li class='n'>a</li></ol>", "li::marker { color: red } .n::before { content: '> ' }");

        Assert.EndsWith("> a", Assert.Single(texts), StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_before_and_after_wrap_the_content_of_blocks_and_inline_elements()
    {
        var texts = Texts(
            "<p class='n'>middle</p><p>a <span class='s'>b</span> c</p>",
            ".n::before { content: '[ ' } .n::after { content: ' ]' } .s::before { content: '<' } .s::after { content: '>' }");

        Assert.Equal(["[ middle ]", "a <b> c"], texts);
    }

    [Fact]
    public void Block_pseudo_elements_become_blocks_before_and_after_the_children()
    {
        var blocks = Blocks(
            "<div class='card'>Body</div>",
            ".card::before { content: 'Banner'; display: block; background: #eee; padding: 4px } .card::after { content: 'End'; display: block }");

        var card = Assert.IsType<ContainerBlock>(Assert.Single(blocks, b => b is ContainerBlock { Children.Count: 3 }));
        Assert.Equal(["Banner", "Body", "End"], card.Children.Select(b => Text(Assert.IsType<ParagraphBlock>(b).Paragraph)));
        Assert.NotNull(card.Children[0].Style.Background);
        Assert.Equal(new Thickness(4 * Px, 4 * Px, 4 * Px, 4 * Px), card.Children[0].Style.Padding);
    }

    [Fact]
    public void Empty_block_pseudo_elements_draw_separators()
    {
        var blocks = Blocks(
            "<div class='rule'>Text</div>",
            ".rule::after { content: ''; display: block; height: 2px; background: red }");

        var separator = Assert.IsType<ContainerBlock>(Assert.Single(blocks, b => b.Style.Background is not null));
        Assert.Empty(separator.Children);
        Assert.Equal(2 * Px, separator.Style.Height);
    }

    [Fact]
    public void Pseudo_elements_have_their_own_styles_and_inherit_from_their_element()
    {
        var paragraph = Paragraphs(
            "<p class='item'>Text</p>",
            """
            .item { color: blue; font-size: 20px }
            .item::before { content: 'NEW'; color: red; font-weight: bold; background: yellow; padding: 0 2px; margin-right: 4px }
            .item::after { content: '!'; font-size: 0.5em }
            """)[0];

        Assert.Equal("NEWText!", Text(paragraph));
        var label = paragraph.Spans[0];
        Assert.Equal(PdfColor.FromRgb(255, 0, 0), label.Style.Color);
        Assert.Equal(700, label.Style.Fonts[0].Weight);
        Assert.Equal(15, label.Style.FontSize);

        var decoration = Assert.Single(paragraph.Decorations);
        Assert.Equal("NEW", Text(paragraph)[decoration.Start..decoration.End]);
        Assert.Equal(PdfColor.FromRgb(255, 255, 0), decoration.Box.Background);
        Assert.Equal(4 * Px, decoration.Box.Margin.Right);

        var after = paragraph.Spans[^1];
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), after.Style.Color); // Inherited from the element.
        Assert.Equal(7.5, after.Style.FontSize); // 0.5em of the element's 20px.
        Assert.Equal(400, after.Style.Fonts[0].Weight);
    }

    [Fact]
    public void Inline_block_pseudo_elements_are_atomic_inline_boxes()
    {
        var paragraph = Paragraphs(
            "<p class='status'>Paid</p>",
            ".status::before { content: ''; display: inline-block; width: 8px; height: 8px; background: green; vertical-align: middle }")[0];

        var dot = Assert.IsType<InlineBox>(paragraph.Spans[0].Box);
        Assert.Equal(8 * Px, dot.Content.Style.Width);
        Assert.Equal(InlineAlignmentKind.Middle, dot.Alignment.Kind);
        Assert.Equal("\uFFFCPaid", Text(paragraph));
    }

    [Fact]
    public void Attr_inserts_attribute_values()
    {
        var texts = Texts(
            "<p data-label='Due'>Friday</p><p data-label=''>Empty</p><p class='missing'>None</p>",
            "[data-label]::before { content: attr(data-label) ': ' } .missing::before { content: '(' attr(data-label) ')' }");

        Assert.Equal(["Due: Friday", ": Empty", "()None"], texts);
    }

    [Fact]
    public void Strings_decode_css_escapes_and_line_breaks_need_preserved_white_space()
    {
        var texts = Texts(
            "<p class='quote'>Text</p><p class='pre'>B</p><p class='normal'>D</p>",
            """
            .quote::before { content: "\201C" } .quote::after { content: '\201D  \'' }
            .pre::before { content: "A\A"; white-space: pre }
            .normal::before { content: "C\A" }
            """);

        Assert.Equal(["\u201CText\u201D '", "A\nB", "C D"], texts);
    }

    [Fact]
    public void Quotes_nest_and_alternate_between_double_and_single()
    {
        var texts = Texts(
            "<p><q>a <q>b <q>c</q></q> d</q></p><p class='custom'><q>x <q>y <q>z</q></q></q></p><p class='none'><q>n</q></p>",
            """.custom { quotes: "\AB" "\BB" "<" ">" } .none { quotes: none }""");

        Assert.Equal(["\u201Ca \u2018b \u2018c\u2019\u2019 d\u201D", "\u00ABx <y <z>>\u00BB", "n"], texts);
    }

    [Fact]
    public void No_open_quote_and_no_close_quote_change_the_depth_without_text()
    {
        var texts = Texts(
            "<p><q class='silent'>a <q>b</q></q> <q>c</q></p><p class='stray'>d</p>",
            ".silent::before { content: no-open-quote } .silent::after { content: no-close-quote } .stray::after { content: close-quote }");

        // The silent quote still counts as a level, so the inner quote is single; a stray close quote shows nothing.
        Assert.Equal(["a \u2018b\u2019 \u201Cc\u201D", "d"], texts);
    }

    [Fact]
    public void Url_inserts_an_image_with_text()
    {
        var paragraph = Paragraphs(
            "<p class='icon'>Warning</p>",
            $".icon::before {{ content: url({Pixel}) ' ' }} .icon::after {{ content: url('{Pixel}') }}")[0];

        Assert.Equal("\uFFFC Warning\uFFFC", Text(paragraph));
        Assert.NotNull(paragraph.Spans[0].Image);
        Assert.NotNull(paragraph.Spans[^1].Image);
    }

    [Fact]
    public void Content_none_normal_or_missing_and_display_none_generate_nothing()
    {
        var texts = Texts(
            "<p class='a'>A</p><p class='b'>B</p><p class='c'>C</p><p class='d'>D</p><p class='e'>E</p>",
            """
            p::before { content: 'x' }
            .a::before { content: none } .b::before { content: normal } .c::before { display: none }
            .d::after { color: red } .e::before { content: initial }
            """);

        Assert.Equal(["A", "B", "C", "xD", "E"], texts);
    }

    [Fact]
    public void Later_and_more_specific_rules_win_and_the_legacy_single_colon_works()
    {
        var texts = Texts(
            "<p>One</p><p class='x'>Two</p><p id='y' class='x'>Three</p>",
            "#y:BEFORE { content: 'E' } .x:before { content: 'C' } p::before { content: 'A' } p::before { content: 'B' }");

        Assert.Equal(["BOne", "CTwo", "EThree"], texts);
    }

    [Fact]
    public void Pseudo_elements_in_list_items_follow_the_marker()
    {
        var paragraphs = Paragraphs(
            "<ul><li>Default</li></ul><ul class='checks'><li>Done</li></ul>",
            ".checks { list-style: none } li::before { content: '\\2192  ' } .checks li::before { content: '\\2713  ' }");

        Assert.Equal("\u2022\u00A0\u2192 Default", Text(paragraphs[0]));
        Assert.Equal("\u2713 Done", Text(paragraphs[1]));
    }

    [Fact]
    public void Pseudo_elements_in_table_cells_and_flex_containers()
    {
        var blocks = Blocks(
            "<table><tr><td class='pct'>50</td></tr></table><div class='row'><span>Item</span></div>",
            ".pct::after { content: ' %' } .row { display: flex } .row::before { content: 'Start' } .row::after { content: 'End' }");

        var cell = Assert.Single(blocks.OfType<TableBlock>()).Rows[0].Cells[0];
        Assert.Equal("50 %", Text(Assert.IsType<ParagraphBlock>(Assert.Single(cell.Content)).Paragraph));

        var flex = Assert.Single(blocks.OfType<FlexBlock>());
        Assert.Equal(["Start", "Item", "End"], flex.Items.Select(i => Text(Assert.IsType<ParagraphBlock>(i.Content).Paragraph)));
    }

    [Fact]
    public void A_bare_pseudo_element_selector_applies_to_every_element_once()
    {
        var texts = Texts("<p>a</p>", "::before { content: '-' } head::before, style::before { content: 'hidden' }");

        // html::before, body::before and p::before; generated boxes have no pseudo-elements of their own.
        Assert.Equal(["-", "-", "-a"], texts);
    }

    [Fact]
    public void Replaced_elements_have_no_pseudo_elements()
    {
        var texts = Texts($"<p>a<img src='{Pixel}'>b<br>c</p>", "img::before, br::after { content: 'x' }");

        Assert.Equal(["a\uFFFCb\nc"], texts);
    }

    [Fact]
    public void Bookmark_labels_can_use_the_generated_text()
    {
        var doc = HtmlToPdf.Convert(
            """
            <html><head><style>
            h1::before { content: 'Chapter ' attr(data-n) ': ' }
            h1 { bookmark-label: content(before) content(text) content(after) }
            </style></head><body><h1 data-n='1'>Intro</h1><h1 data-n='2'>End</h1></body></html>
            """);

        Assert.Equal(["Chapter 1: Intro", "Chapter 2: End"], doc.Outline.Select(i => i.Title));
    }

    [Fact]
    public void Text_extraction_follows_document_order()
    {
        var pdf = HtmlToPdf.ConvertToBytes(
            """
            <html><head><style>
            .total::before { content: 'Total: ' } .total::after { content: ' EUR' }
            .banner::before { content: 'Banner'; display: block }
            </style></head><body><p class='total'>42</p><div class='banner'>Body <q>quoted</q></div></body></html>
            """);

        var text = Regex.Replace(ExternalTools.ExtractText(pdf), @"\s+", " ").Trim();

        Assert.Equal("Total: 42 EUR Banner Body \u201Cquoted\u201D", text);
    }

    private static List<string> Texts(string body, string css) => [.. Paragraphs(body, css).Select(Text)];

    private static List<Paragraph> Paragraphs(string body, string css) =>
        [.. Blocks(body, css).OfType<ParagraphBlock>().Select(p => p.Paragraph)];

    private static IReadOnlyList<Block> Blocks(string body, string css) =>
        [.. Flatten(HtmlToPdf.CreateFlowDocument($"<html><head><style>{css}</style></head><body>{body}</body></html>").Blocks)];

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
