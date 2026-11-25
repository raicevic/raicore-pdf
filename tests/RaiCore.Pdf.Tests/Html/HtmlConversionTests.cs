using System.Text.RegularExpressions;
using PDFtoImage;
using RaiCore.Pdf.Extraction;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Tests.Html;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlConversionTests
{
    private const double Px = 0.75; // Points per CSS pixel.

    [Fact]
    public void Converts_a_simple_document()
    {
        var pdf = HtmlToPdf.Convert("<html><head><title> Hello PDF </title></head><body><h1>Hello</h1><p>World</p></body></html>");

        Assert.Single(pdf.Pages);
        Assert.Equal("Hello PDF", pdf.Info.Title);
        var text = ExternalTools.ExtractText(pdf.ToArray());
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("World", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Applies_the_default_styles_browsers_use()
    {
        var blocks = Blocks("<h1>Title</h1><p>Text</p>");

        var h1 = Paragraph(blocks, "Title");
        Assert.Equal(32 * Px, h1.Paragraph.Style.FontSize, 3); // 2em of 16px
        Assert.Equal(700, h1.Paragraph.Style.Fonts[0].Weight);
        Assert.Equal(0.67 * 32 * Px, h1.Style.Margin.Top.Points, 2);
        Assert.Equal(PageBreak.Avoid, h1.Style.BreakAfter);
        Assert.Equal(16 * Px, Paragraph(blocks, "Text").Style.Margin.Top.Points, 3); // 1em
        Assert.Equal("LiberationSerif", Paragraph(blocks, "Text").Paragraph.Style.Fonts[0].PostScriptName); // serif default
        Assert.Equal(8 * Px, Find<ContainerBlock>(blocks, b => b.Style.Margin.Left.Points > 0).Style.Margin.Left.Points, 3); // body margin
    }

    [Fact]
    public void Resolves_the_cascade_specificity_inline_styles_and_important()
    {
        const string html = """
            <style>
              p { color: red; }
              .note { color: green; }
              #main { color: blue; }
              .strong { color: purple !important; }
            </style>
            <p>a</p><p class="note">b</p><p class="note" id="main">c</p><p id="main" style="color: orange">d</p>
            <p class="strong" style="color: orange">e</p>
            """;
        var blocks = Blocks(html);

        Assert.Equal(PdfColor.FromRgb(255, 0, 0), Color(blocks, "a"));
        Assert.Equal(PdfColor.FromRgb(0, 128, 0), Color(blocks, "b"));
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), Color(blocks, "c"));
        Assert.Equal(PdfColor.FromRgb(255, 165, 0), Color(blocks, "d"));
        Assert.Equal(PdfColor.FromRgb(128, 0, 128), Color(blocks, "e"));
    }

    [Fact]
    public void Inherits_and_resolves_relative_font_sizes()
    {
        var blocks = Blocks("<div style='font-size: 10px'><div style='font-size: 2em'><p style='font-size: 150%'>x</p><p>y</p></div></div>");

        Assert.Equal(30 * Px, Paragraph(blocks, "x").Paragraph.Style.FontSize, 3);
        Assert.Equal(20 * Px, Paragraph(blocks, "y").Paragraph.Style.FontSize, 3);
    }

    [Theory]
    [InlineData("<p>  Hello   \n\t world  </p>", "Hello world ")]
    [InlineData("<p>a <b> b </b> c</p>", "a b c")]
    [InlineData("<p style='white-space: pre'>a  b\nc</p>", "a  b\nc")] // Spaces stay spaces: TextStyle.NoWrap keeps them from wrapping (#142).
    [InlineData("<p style='white-space: pre-wrap'>a  b\n c</p>", "a  b\n c")]
    [InlineData("<p style='white-space: pre-line'>a   b  \n   c</p>", "a b\nc")]
    [InlineData("<p style='white-space: nowrap'>a b</p>", "a b")]
    [InlineData("<p>one<br>two<br> three</p>", "one\ntwo\nthree")]
    [InlineData("<p style='text-transform: uppercase'>shout</p>", "SHOUT")]
    [InlineData("<p style='text-transform: capitalize'>hello big world</p>", "Hello Big World")]
    public void Processes_white_space_and_line_breaks_like_css(string html, string expected) =>
        Assert.Equal(expected, Text(Assert.Single(Paragraphs(Blocks(html)))));

    [Theory]
    [InlineData("<p>A</p><p>&nbsp;</p><p>B</p>", new[] { "A", "\u00A0", "B" })] // The spacer paragraph of e-mails and word processors.
    [InlineData("<p>A</p><p> &nbsp; </p><p>B</p>", new[] { "A", "\u00A0 ", "B" })]
    [InlineData("<p>A</p><p><span>\u2003</span></p><p>B</p>", new[] { "A", "\u2003", "B" })]
    [InlineData("<p>A</p><p> \t\n </p><p>B</p>", new[] { "A", "B" })] // Collapsible white space alone has no line box.
    public void A_no_break_space_is_not_collapsible(string html, string[] expected) =>
        Assert.Equal(expected, Paragraphs(Blocks(html)).Select(Text));

    [Fact]
    public void A_paragraph_holding_a_no_break_space_is_a_line_tall()
    {
        var pdf = HtmlToPdf.ConvertToBytes("<p style='margin:0'>A</p><p style='margin:0'>&nbsp;</p><p style='margin:0'>B</p><p style='margin:0'>C</p>");
        var lines = new TextExtractor(PdfReader.Open(pdf)).FindOnPage(0, new Regex("[ABC]")).Select(m => m.Bounds.Y).ToList();

        Assert.Equal(2 * (lines[2] - lines[1]), lines[1] - lines[0], 3);
    }

    [Fact]
    public void A_centered_box_in_a_fixed_width_cell_is_centered_in_the_cell()
    {
        // The e-mail layout from the fidelity corpus (leemunroe-email): the box's auto margins resolve against the cell.
        var pdf = HtmlToPdf.ConvertToBytes("""
            <style>@page { size: 400pt 400pt; margin: 0 }</style>
            <body style="margin: 0">
              <table style="width: 100%; border-spacing: 0"><tr>
                <td style="padding: 0">&nbsp;</td>
                <td style="width: 200pt; padding: 0"><div style="max-width: 200pt; margin: 0 auto; background: #ff0000">Box</div></td>
                <td style="padding: 0">&nbsp;</td>
              </tr></table>
            </body>
            """);
        using var bitmap = Conversion.ToImage(pdf, page: 0, options: new RenderOptions(Dpi: 72));

        var red = Enumerable.Range(0, bitmap.Width).Where(x => bitmap.GetPixel(x, 5) is { Red: > 200, Green: < 80 }).ToList();
        Assert.Equal((100, 299), (red[0], red[^1]));
    }

    [Theory]
    [InlineData("overflow-wrap: break-word", true, false)]
    [InlineData("word-wrap: break-word", true, false)] // The legacy name.
    [InlineData("overflow-wrap: anywhere", false, true)]
    [InlineData("WORD-WRAP: anywhere", false, true)]
    [InlineData("overflow-wrap: anywhere; word-wrap: normal", false, false)] // The later of the two wins.
    [InlineData("word-wrap: break-word; overflow-wrap: normal", false, false)]
    [InlineData("", false, false)]
    public void Overflow_wrap_lets_long_words_break(string css, bool breakWord, bool anywhere)
    {
        var paragraph = Assert.Single(Paragraphs(Blocks($"<div style='{css}'><p>long_token</p></div>"))).Paragraph; // Inherited.

        Assert.Equal((breakWord, anywhere), (paragraph.BreakLongWords, paragraph.BreakAnywhere));
    }

    [Fact]
    public void Overflow_wrap_keeps_a_long_token_in_its_box()
    {
        // The case from #306: a 60 pt box and a token far wider.
        var diagnostics = new List<HtmlDiagnostic>();
        var pdf = HtmlToPdf.ConvertToBytes(
            "<style>.word-wrap:after { content: '!' }</style><div class='word-wrap' style='width: 60pt; overflow-wrap: break-word'>Supercalifragilisticexpialidocious_long_token_here</div>",
            new HtmlConverterOptions { Diagnostics = diagnostics.Add });

        Assert.DoesNotContain(diagnostics, d => d.Kind is HtmlDiagnosticKind.Overflow or HtmlDiagnosticKind.UnsupportedProperty);
        var words = new TextExtractor(PdfReader.Open(pdf)).FindOnPage(0, new Regex(@"\S+")).ToList();
        Assert.True(words.Count > 3, $"{words.Count} lines");
        var left = words.Min(w => w.Bounds.X);
        Assert.All(words, w => Assert.True(w.Bounds.X + w.Bounds.Width <= left + 60 + 0.5, $"{w.Text} ends at {w.Bounds.X + w.Bounds.Width - left}"));
        Assert.EndsWith("!", words[^1].Text, StringComparison.Ordinal); // The selector .word-wrap:after is not rewritten.
    }

    [Fact]
    public void Inline_elements_become_styled_spans()
    {
        var paragraph = Assert.Single(Paragraphs(Blocks("<p>plain <b>bold</b> <i>italic</i> <span style='color: #00ff00'>green</span></p>"))).Paragraph;

        var bold = paragraph.Spans.Single(s => s.Text == "bold");
        var italic = paragraph.Spans.Single(s => s.Text == "italic");
        var green = paragraph.Spans.Single(s => s.Text == "green");
        Assert.Equal("LiberationSerif-Bold", bold.Style.Fonts[0].PostScriptName);
        Assert.Equal("LiberationSerif-Italic", italic.Style.Fonts[0].PostScriptName);
        Assert.Equal(PdfColor.FromRgb(0, 255, 0), green.Style.Color);
    }

    [Fact]
    public void Mixed_inline_and_block_content_gets_anonymous_paragraphs()
    {
        var blocks = Blocks("<div>before<p>inside</p>after</div>");
        var texts = Paragraphs(blocks).Select(Text).ToList();

        Assert.Equal(["before", "inside", "after"], texts);
    }

    [Theory]
    [InlineData("<ul><li>a</li><li>b</li></ul>", new[] { "\u2022", "\u2022" })]
    [InlineData("<ol><li>a</li><li>b</li><li>c</li></ol>", new[] { "1.", "2.", "3." })]
    [InlineData("<ol start='9'><li>a</li><li>b</li></ol>", new[] { "9.", "10." })]
    [InlineData("<ol reversed><li>a</li><li>b</li><li>c</li></ol>", new[] { "3.", "2.", "1." })]
    [InlineData("<ol><li>a</li><li value='7'>b</li><li>c</li></ol>", new[] { "1.", "7.", "8." })]
    [InlineData("<ol style='list-style-type: lower-roman'><li>a</li><li>b</li><li>c</li><li>d</li></ol>", new[] { "i.", "ii.", "iii.", "iv." })]
    [InlineData("<ol style='list-style-type: upper-alpha'><li>a</li><li>b</li></ol>", new[] { "A.", "B." })]
    [InlineData("<ul><li>a<ul><li>b<ul><li>c</li></ul></li></ul></li></ul>", new[] { "\u2022", "\u25E6", "\u25AA" })]
    public void List_items_get_markers(string html, string[] markers)
    {
        var paragraphs = Paragraphs(Blocks(html)).ToList();

        Assert.Equal(markers, paragraphs.Select(p => Text(p).Split('\u00A0')[0]));
    }

    [Fact]
    public void Outside_markers_hang_and_inside_markers_do_not()
    {
        var outside = Assert.Single(Paragraphs(Blocks("<ul><li>a</li></ul>"))).Paragraph;
        var inside = Assert.Single(Paragraphs(Blocks("<ul style='list-style-position: inside'><li>a</li></ul>"))).Paragraph;
        var none = Assert.Single(Paragraphs(Blocks("<ul style='list-style: none'><li>a</li></ul>")));

        Assert.True(outside.FirstLineIndent < 0);
        Assert.Equal(0, inside.FirstLineIndent);
        Assert.Equal("a", Text(none));
    }

    [Fact]
    public void Resolves_box_model_widths_percentages_and_auto_margins()
    {
        const string html = """
            <body style="margin: 0">
              <div id="a" style="width: 50%; padding: 10px; border: 2px solid black">a</div>
              <div id="b" style="width: 200px; margin: 0 auto; box-sizing: border-box; padding: 20px">b</div>
              <div id="c" style="max-width: 100px; margin-left: auto">c</div>
            </body>
            """;
        var blocks = Blocks(html, PageWidth(300)); // 300 pt of content; body has no margin

        var a = Paragraph(blocks, "a");
        Assert.Equal((150 + 15 + 3), a.Style.Width!.Value.Resolve(300), 3); // 50% of 300 pt content + padding + border
        var b = Paragraph(blocks, "b");
        Assert.Equal(150, b.Style.Width!.Value.Resolve(300), 3); // border-box: padding included
        Assert.Equal(75, b.Style.Margin.Left.Resolve(300), 3); // (300 - 150) / 2
        var c = Paragraph(blocks, "c");
        Assert.Null(c.Style.Width);
        Assert.Equal(75, c.Style.MaxWidth!.Value.Resolve(300), 3);
        Assert.Equal(225, c.Style.Margin.Left.Resolve(300), 3); // pushed right by the space the max width leaves
    }

    [Fact]
    public void Text_properties_map_to_the_paragraph()
    {
        var p = Assert.Single(Paragraphs(Blocks("<p style='text-align: justify; text-indent: 2em; line-height: 1.5; font-size: 10px'>x</p>"))).Paragraph;

        Assert.Equal(TextAlignment.Justify, p.Alignment);
        Assert.Equal(20 * Px, p.FirstLineIndent, 3);
        Assert.Equal(LineHeight.Multiple(1.5), p.Style.LineHeight);
    }

    [Fact]
    public void Hidden_elements_are_not_rendered()
    {
        var texts = Paragraphs(Blocks("<p>shown</p><p style='display:none'>gone</p><p hidden>gone</p><script>gone()</script><template><p>gone</p></template>")).Select(Text);

        Assert.Equal(["shown"], texts);
    }

    [Fact]
    public void Print_media_rules_apply_and_screen_rules_do_not()
    {
        var texts = Paragraphs(Blocks("<style>@media screen { .s { display: none } } @media print { .p { display: none } }</style><p class='s'>screen-hidden</p><p class='p'>print-hidden</p>")).Select(Text);

        Assert.Equal(["screen-hidden"], texts);
    }

    [Theory]
    [InlineData("@page { size: A5 landscape; margin: 1cm }", 595.28, 419.53, 28.35)]
    [InlineData("@page { size: letter; margin: 0.5in 1in }", 612, 792, 36)]
    [InlineData("@page { size: 100mm 50mm }", 283.46, 141.73, 56.69)]
    [InlineData("@page { size: A6 }", 297.64, 419.53, 56.69)]
    [InlineData("<!-- @page { size: A6 } -->", 297.64, 419.53, 56.69)] // Old pages and e-mails wrap style sheets in comment markers.
    [InlineData("<!--@page { size: A6 }-->", 297.64, 419.53, 56.69)]
    [InlineData("@page { size: a6 landscape }", 419.53, 297.64, 56.69)]
    [InlineData("@page { size: A0 }", 2383.94, 3370.39, 56.69)]
    [InlineData("@page { size: B6 }", 354.33, 498.9, 56.69)]
    [InlineData("@page { size: jis-b5 }", 515.91, 728.5, 56.69)]
    [InlineData("@page { size: 300PX 200Px; margin: 10PT }", 225, 150, 10)]
    public void Page_size_and_margins_come_from_at_page(string css, double width, double height, double marginTop)
    {
        var flow = HtmlToPdf.CreateFlowDocument($"<style>{css}</style><p>x</p>");

        Assert.Equal(width, flow.Setup.Size.Width, tolerance: 0.01);
        Assert.Equal(height, flow.Setup.Size.Height, tolerance: 0.01);
        Assert.Equal(marginTop, flow.Setup.Margins.Top.Points, tolerance: 0.01);
    }

    [Theory]
    [InlineData("<p>a</p><p style='break-before: page'>b</p>", 2)]
    [InlineData("<p style='page-break-after: always'>a</p><p>b</p>", 2)]
    [InlineData("<p>a</p><p>b</p>", 1)]
    [InlineData("<style>p.x { page-break-before: always }</style><p>a</p><p class='x'>b</p>", 2)]
    [InlineData("<h1>Heading</h1><p>b</p>", 1)] // Headings avoid breaks after them; nothing forces one before.
    public void Page_breaks_are_honored(string html, int pages) =>
        Assert.Equal(pages, HtmlToPdf.Convert(html).Pages.Count);

    [Fact]
    public void Images_load_from_data_uris_and_files()
    {
        var png = File.ReadAllBytes(Fixtures.Path(Path.Combine("PngSuite", "basn6a08.png"))); // 32 x 32
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        var folder = new Uri(Fixtures.Path("PngSuite") + Path.DirectorySeparatorChar);
        var html = $"""
            <p>inline <img src="{dataUri}"> natural, <img src="basn6a08.png" width="64"> scaled</p>
            <img src="basn6a08.png" style="display: block; width: 48px; margin: 0 auto">
            <p><img src="missing.png" alt="[logo]"> <img src="https://example.com/remote.png" alt="[remote]"></p>
            """;
        var blocks = Blocks(html, baseUri: folder);

        var inline = Paragraphs(blocks).First();
        var images = inline.Paragraph.Spans.Where(s => s.Image is not null).Select(s => s.Image!).ToList();
        Assert.Equal([(24.0, 24.0), (48.0, 48.0)], images.Select(i => (i.Width, i.Height))); // 32 px natural; width=64 keeps 1:1
        var block = Find<ImageBlock>(blocks, _ => true);
        Assert.Equal((36.0, BlockAlignment.Center), (block.Width!.Value, block.Alignment));
        Assert.Equal("[logo] [remote]", Text(Paragraphs(blocks).Last())); // Not found / network disabled: alt text.
    }

    [Fact]
    public void Linked_and_user_stylesheets_are_applied()
    {
        var css = new Dictionary<string, string> { ["https://example.com/site.css"] = "p { color: #ff0000; }" };
        var options = new HtmlConverterOptions
        {
            BaseUri = new Uri("https://example.com/"),
            ResourceLoader = request => css.TryGetValue(request.Uri.ToString(), out var text) ? System.Text.Encoding.UTF8.GetBytes(text) : null,
            UserStyleSheet = ".override { color: #0000ff }",
        };
        var blocks = HtmlToPdf.CreateFlowDocument("<link rel='stylesheet' href='site.css'><p>red</p><p class='override'>blue</p>", options).Blocks;

        Assert.Equal(PdfColor.FromRgb(255, 0, 0), Color(blocks, "red"));
        Assert.Equal(PdfColor.FromRgb(0, 0, 255), Color(blocks, "blue"));
    }

    [Fact]
    public void The_loader_is_told_what_each_resource_is_for()
    {
        var requests = new List<ResourceRequest>();
        var options = new HtmlConverterOptions
        {
            BaseUri = new Uri("https://example.com/"),
            ResourceLoader = request =>
            {
                requests.Add(request);
                return request.Uri.AbsolutePath == "/site.css"
                    ? "@font-face { font-family: Brand; src: url(brand.ttf) } p { font-family: Brand } div { background-image: url(bg.png); height: 10px }"u8.ToArray()
                    : null;
            },
        };

        HtmlToPdf.ConvertToBytes("<link rel='stylesheet' href='site.css'><p>Text</p><img src='photo.png'><div></div>", options);

        Assert.Equal(ResourceKind.StyleSheet, Assert.Single(requests, r => r.Uri.AbsolutePath == "/site.css").Kind);
        Assert.Equal(ResourceKind.Font, Assert.Single(requests, r => r.Uri.AbsolutePath == "/brand.ttf").Kind);
        Assert.Equal(ResourceKind.Image, Assert.Single(requests, r => r.Uri.AbsolutePath == "/photo.png").Kind);
        Assert.Equal(ResourceKind.Image, Assert.Single(requests, r => r.Uri.AbsolutePath == "/bg.png").Kind);
        Assert.All(requests, r => Assert.True(r.CancellationToken.CanBeCanceled)); // Every conversion has a deadline.
    }

    [Fact]
    public void Unknown_font_families_fall_back_and_generic_families_resolve()
    {
        var blocks = Blocks("<p style=\"font-family: 'No Such Font', Arial\">a</p><p style='font-family: NoSuchFont'>b</p><p style='font-family: monospace'>c</p>");

        Assert.Equal("LiberationSans", Paragraph(blocks, "a").Paragraph.Style.Fonts[0].PostScriptName); // Arial -> Liberation Sans
        Assert.Equal("LiberationSans", Paragraph(blocks, "b").Paragraph.Style.Fonts[0].PostScriptName); // final sans-serif fallback
        Assert.Equal("LiberationMono", Paragraph(blocks, "c").Paragraph.Style.Fonts[0].PostScriptName);
    }

    [Fact]
    public void Malformed_html_still_converts()
    {
        var pdf = HtmlToPdf.Convert("<div><p>unclosed <b>bold <i>both</div><p>next");

        Assert.Single(pdf.Pages);
    }

    [Fact]
    public void Invoice_renders_with_backgrounds_borders_and_extractable_text()
    {
        const string html = """
            <html><head><title>Invoice</title><style>
              @page { size: A4; margin: 20mm; }
              body { font-family: Arial, sans-serif; margin: 0; }
              .address { width: 40%; background: #e0e8ff; padding: 10px; border-left: 6px solid #1d4f91; }
              .total { max-width: 200px; margin: 20px auto; text-align: center; border: 2px solid #c00000; }
            </style></head><body>
              <h1>Invoice 2026-0042</h1>
              <div class="address"><b>Jens Jensen</b><br>8000 Aarhus C</div>
              <ul><li>Consulting</li><li>Support</li></ul>
              <div class="total">Total: 1.234,50 kr.</div>
            </body></html>
            """;
        var bytes = HtmlToPdf.ConvertToBytes(html);

        ExternalTools.AssertQpdfClean(bytes);
        var text = ExternalTools.ExtractText(bytes);
        foreach (var expected in new[] { "Invoice 2026-0042", "Jens Jensen", "8000 Aarhus C", "Consulting", "Support", "Total: 1.234,50 kr." })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        using var bitmap = Conversion.ToImage(bytes, page: 0, options: new RenderOptions(Dpi: 72));
        var pixels = Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width / 2).Select(x => bitmap.GetPixel(x, y))).ToList();
        Assert.Contains(pixels, c => c.Red is >= 215 and <= 230 && c.Green is >= 225 && c.Blue == 255); // address background
        Assert.Contains(pixels, c => c.Red < 40 && c.Green is >= 70 and <= 90 && c.Blue is >= 135 and <= 155); // border-left
    }

    // ---- helpers ----

    private static PageSetup PageWidth(double width) => new(new PdfPageSize(width + 100, 800), new Thickness(50, 50, 50, 50));

    [Theory]
    [InlineData("rgb(1 2 3 / 4px)")]
    [InlineData("rgb(1 2 3 / )")]
    [InlineData("rgba(1 2 3 /)")]
    [InlineData("lab(1 2 3 / 4px)")]
    [InlineData("LCH(1 2 3 / 4PX)")]
    [InlineData("oklab(1 2 3 / /**/ )")]
    [InlineData("oklch(1 2 3 / +4e1px)")]
    [InlineData("rgb(1 2 3 / 4\\70 x)")]
    public void Colors_with_an_unreadable_alpha_are_dropped_wherever_they_come_from(string bad)
    {
        // AngleSharp.Css throws on these while parsing (fuzzing found them); CSS drops the declaration instead.
        var Bad = bad;
        var diagnostics = new List<HtmlDiagnostic>();
        var options = new HtmlConverterOptions
        {
            BaseUri = new Uri("https://example.com/"),
            ResourceLoader = request => request.Uri.AbsolutePath == "/linked.css" ? System.Text.Encoding.UTF8.GetBytes($".linked {{ color: red; color: {Bad} }}") : null,
            UserStyleSheet = $".user {{ color: red; color: {Bad} }}",
            Diagnostics = diagnostics.Add,
        };
        var html = $$"""
            <link rel="stylesheet" href="linked.css">
            <style>.sheet { color: red; color: {{Bad}} } .valid { color: rgb(0 0 255 / 50%) }</style>
            <p style="color: red; color: {{Bad}}">inline</p>
            <p class="sheet">sheet</p><p class="linked">linked</p><p class="user">user</p><p class="valid">valid</p>
            """;
        var blocks = HtmlToPdf.CreateFlowDocument(html, options).Blocks;

        Assert.All(new[] { "inline", "sheet", "linked", "user" }, text => Assert.Equal(PdfColor.FromRgb(255, 0, 0), Color(blocks, text)));
        Assert.Equal(PdfColor.FromRgba(0, 0, 255, 0.5), Color(blocks, "valid"));
        Assert.Contains(diagnostics, d => d.Kind == HtmlDiagnosticKind.UnsupportedValue && d.Subject.Contains("color", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("a { color: inherit }")]
    [InlineData("nav a { color: inherit }")]
    [InlineData("a { color: #800000 }")]
    public void Any_author_rule_overrides_the_default_link_color(string css)
    {
        // The default styles' a:link would outrank these selectors if it were an author rule.
        var blocks = Blocks($"<style>{css}</style><nav><p style='color: #800000'><a href='#x'>link</a></p></nav>");

        Assert.Equal(PdfColor.FromRgb(128, 0, 0), Color(blocks, "link"));
    }

    [Fact]
    public void Links_are_blue_by_default() =>
        Assert.Equal(PdfColor.FromRgb(0, 0, 0xEE), Color(Blocks("<p><a href='#x'>link</a></p>"), "link"));

    [Fact]
    public void An_author_display_shows_a_hidden_element_as_in_browsers()
    {
        Assert.DoesNotContain(Paragraphs(Blocks("<div hidden>hidden</div>")), p => Text(p).Trim() == "hidden");
        Assert.Single(Paragraphs(Blocks("<style>div { display: block }</style><div hidden>shown</div>")), p => Text(p).Trim() == "shown");
    }

    private static IReadOnlyList<Block> Blocks(string html, PageSetup? page = null, Uri? baseUri = null) =>
        HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Page = page ?? new HtmlConverterOptions().Page, BaseUri = baseUri }).Blocks;

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);

    private static IEnumerable<ParagraphBlock> Paragraphs(IEnumerable<Block> blocks) => Flatten(blocks).OfType<ParagraphBlock>();

    private static T Find<T>(IEnumerable<Block> blocks, Func<T, bool> predicate) => Flatten(blocks).OfType<T>().First(predicate);

    private static string Text(ParagraphBlock block) => string.Concat(block.Paragraph.Spans.Select(s => s.Text));

    private static ParagraphBlock Paragraph(IEnumerable<Block> blocks, string text) => Paragraphs(blocks).First(p => Text(p).Trim() == text);

    private static PdfColor Color(IEnumerable<Block> blocks, string text) => Paragraph(blocks, text).Paragraph.Spans[0].Style.Color;
}
