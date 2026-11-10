using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Tests.Html;

public class HtmlWebFontTests
{
    private static readonly Uri FixturesUri = new(Fixtures.Path("x"));

    [Fact]
    public void A_declared_font_is_loaded_from_a_relative_url()
    {
        var fonts = FontsOf("""
            <style>
              @font-face { font-family: Brand; src: url(LiberationMono-Regular.ttf) format("truetype"); }
              p { font-family: Brand, serif; }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal("LiberationMono", fonts[0].PostScriptName);
    }

    [Theory]
    [InlineData("font-weight: 400", "LiberationMono")]
    [InlineData("font-weight: bold", "LiberationMono-Bold")]
    [InlineData("font-style: italic", "LiberationMono-Italic")]
    [InlineData("font-weight: 600", "LiberationMono-Bold")]   // Above 500: heavier faces first.
    [InlineData("font-weight: 300", "LiberationMono")]        // Below 400: lighter first, then heavier.
    [InlineData("font-weight: 450", "LiberationMono")]        // 400-500: up to 500, then lighter.
    public void Faces_are_matched_by_weight_and_style(string css, string expected)
    {
        var fonts = FontsOf($$"""
            <style>
              @font-face { font-family: Brand; src: url(LiberationMono-Regular.ttf); }
              @font-face { font-family: Brand; src: url(LiberationMono-Bold.ttf); font-weight: 700; }
              @font-face { font-family: Brand; src: url(LiberationMono-Italic.ttf); font-style: italic; }
              p { font-family: Brand; {{css}} }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal(expected, fonts[0].PostScriptName);
    }

    [Theory]
    [InlineData(300, "LiberationMono")]   // Below 400: the lighter 100 face beats the heavier 400 one.
    [InlineData(450, "LiberationSans")]   // 400-500: 500 (heavier, up to 500) beats 400 (lighter).
    [InlineData(480, "LiberationSans")]
    [InlineData(600, "LiberationSerif")]  // Above 500: 900 (heavier) beats 500 (lighter)...
    [InlineData(950, "LiberationSerif")]  // ...and with nothing heavier, the heaviest lighter face wins.
    public void Missing_weights_follow_the_css_matching_order(int weight, string expected)
    {
        // Each weight maps to a different file so the chosen face is visible: 100 Mono, 400 Serif-Italic (as a
        // normal face), 500 Sans, 900 Serif.
        var fonts = FontsOf($$"""
            <style>
              @font-face { font-family: Weights; src: url(LiberationMono-Regular.ttf); font-weight: 100; }
              @font-face { font-family: Weights; src: url(LiberationSerif-Italic.ttf); font-weight: 400; }
              @font-face { font-family: Weights; src: url(LiberationSans-Regular.ttf); font-weight: 500; }
              @font-face { font-family: Weights; src: url(LiberationSerif-Regular.ttf); font-weight: 900; }
              p { font-family: Weights; font-weight: {{weight}}; }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal(expected, fonts[0].PostScriptName);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(800)]
    public void A_weight_range_matches_every_weight_in_it(int weight)
    {
        var fonts = FontsOf($$"""
            <style>
              @font-face { font-family: Variable; src: url(LiberationSerif-Regular.ttf); font-weight: 100 900; }
              @font-face { font-family: Variable; src: url(LiberationMono-Regular.ttf); font-weight: 950; }
              p { font-family: Variable; font-weight: {{weight}}; }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal("LiberationSerif", fonts[0].PostScriptName);
    }

    [Fact]
    public void A_declared_family_hides_the_installed_font_of_that_name()
    {
        // Without the rule, Arial resolves to Liberation Sans through the bundled substitutes.
        var fonts = FontsOf("""
            <style>
              @font-face { font-family: "Arial"; src: url(LiberationSerif-Regular.ttf); }
              p { font-family: Arial; }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal("LiberationSerif", fonts[0].PostScriptName);
    }

    [Fact]
    public void Sources_are_tried_in_order_and_unsupported_formats_are_not_downloaded()
    {
        var requested = new List<string>();
        var fonts = FontsOf("""
            <style>
              @font-face {
                font-family: Brand;
                src: url(brand.eot) format("embedded-opentype"), url("missing.woff2") format("woff2"),
                     url('LiberationMono-Regular.ttf') format("truetype");
              }
              p { font-family: Brand; }
            </style>
            <p>Hello</p>
            """, uri => { requested.Add(Path.GetFileName(uri.LocalPath)); return Resources.Default(uri); });

        Assert.Equal("LiberationMono", fonts[0].PostScriptName);
        Assert.Equal(["missing.woff2", "LiberationMono-Regular.ttf"], requested);
    }

    [Fact]
    public void A_family_whose_fonts_cannot_load_falls_back_to_the_next_family()
    {
        var fonts = FontsOf("""
            <style>
              @font-face { font-family: Brand; src: url(missing.ttf); }
              @font-face { font-family: Broken; src: url(data:font/ttf;base64,AAAA); }
              p { font-family: Brand, Broken, "Liberation Mono"; }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal("LiberationMono", fonts[0].PostScriptName);
    }

    [Fact]
    public void Local_sources_use_installed_or_configured_fonts()
    {
        var fonts = FontsOf("""
            <style>
              @font-face { font-family: Brand; src: local("Liberation Mono"), url(missing.ttf); }
              p { font-family: Brand; }
            </style>
            <p>Hello</p>
            """);

        Assert.Equal("LiberationMono", fonts[0].PostScriptName);
    }

    [Fact]
    public void Fonts_can_be_embedded_as_data_uris_in_woff_and_woff2()
    {
        foreach (var file in new[] { "LiberationSans-Regular.woff", "LiberationSans-Regular.woff2" })
        {
            var data = Convert.ToBase64String(Fixtures.Read(file));
            var fonts = FontsOf($$"""
                <style>
                  @font-face { font-family: Inline; src: url(data:font/woff2;base64,{{data}}); }
                  p { font-family: Inline, serif; }
                </style>
                <p>Hello</p>
                """);

            Assert.Equal("LiberationSans", fonts[0].PostScriptName);
        }
    }

    [Fact]
    public void Urls_in_linked_stylesheets_resolve_against_the_stylesheet()
    {
        var requested = new List<Uri>();
        var mono = Fixtures.Read("LiberationMono-Regular.ttf");
        byte[]? Loader(Uri uri)
        {
            requested.Add(uri);
            return uri.AbsolutePath switch
            {
                "/assets/css/site.css" => Encoding.UTF8.GetBytes("@font-face { font-family: Brand; src: url(../fonts/brand.ttf); } p { font-family: Brand; }"),
                "/assets/fonts/brand.ttf" => mono,
                _ => null,
            };
        }

        var flow = HtmlToPdf.CreateFlowDocument(
            """<link rel="stylesheet" href="../assets/css/site.css"><p>Hello</p>""",
            new HtmlConverterOptions { BaseUri = new Uri("https://example.test/pages/invoice.html"), ResourceLoader = Loader });

        Assert.Equal("LiberationMono", Paragraphs(flow)[0].Paragraph.Spans[0].Style.Fonts[0].PostScriptName);
        Assert.Contains(new Uri("https://example.test/assets/fonts/brand.ttf"), requested);
    }

    [Fact]
    public void Unicode_ranges_split_a_family_across_files()
    {
        // Like Google Fonts: one file per script range. Here "Latin" comes from Mono and "Latin-1" from Serif.
        var fonts = FontsOf("""
            <style>
              @font-face { font-family: Split; src: url(LiberationMono-Regular.ttf); unicode-range: U+0000-007F; }
              @font-face { font-family: Split; src: url(LiberationSerif-Regular.ttf); unicode-range: U+0080-00FF, U+0131; }
              p { font-family: Split; }
            </style>
            <p>Hello</p>
            """);

        var runs = TextShaper.Shape("Aé", fonts).Runs;
        Assert.Equal(["LiberationMono", "LiberationSerif"], runs.Select(r => r.Font.PostScriptName));
        Assert.False(fonts.First(f => f.PostScriptName == "LiberationSerif").HasGlyph('A')); // Outside its range.
    }

    [Fact]
    public void Wildcard_unicode_ranges_are_understood()
    {
        var fonts = FontsOf("""
            <style>
              @font-face { font-family: Split; src: url(LiberationMono-Regular.ttf); unicode-range: U+00??; }
              p { font-family: Split, serif; }
            </style>
            <p>Hello</p>
            """);

        var mono = fonts.First(f => f.PostScriptName == "LiberationMono");
        Assert.True(mono.HasGlyph(0xE9));
        Assert.False(mono.HasGlyph(0x0141)); // Ł is in the font but outside U+0000-00FF.
    }

    [Fact]
    public void Fonts_are_only_loaded_when_text_uses_them()
    {
        var requested = new List<string>();
        FontsOf("""
            <style>
              @font-face { font-family: Used; src: url(LiberationMono-Regular.ttf); }
              @font-face { font-family: Unused; src: url(LiberationSerif-Regular.ttf); }
              p { font-family: Used; }
            </style>
            <p>Hello</p>
            """, uri => { requested.Add(Path.GetFileName(uri.LocalPath)); return Resources.Default(uri); });

        Assert.Equal(["LiberationMono-Regular.ttf"], requested);
    }

    [Fact]
    public void Web_fonts_are_embedded_and_rules_in_print_media_count()
    {
        var pdf = HtmlToPdf.Convert("""
            <style>
              @media print { @font-face { font-family: Brand; src: url(LiberationMono-Regular.ttf); } }
              @media screen { @font-face { font-family: Screen; src: url(LiberationSerif-Regular.ttf); } }
              p { font-family: Brand; }
            </style>
            <p>Hello web font</p>
            """, new HtmlConverterOptions { BaseUri = FixturesUri });

        var text = Encoding.Latin1.GetString(pdf.ToArray());
        Assert.Contains("+LiberationMono", text, StringComparison.Ordinal);
        Assert.Contains("Hello web font", ExternalTools.ExtractText(pdf.ToArray()), StringComparison.Ordinal);
    }

    private static List<PdfFont> FontsOf(string html, ResourceLoader? loader = null)
    {
        var flow = HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { BaseUri = FixturesUri, ResourceLoader = loader ?? Resources.Default });
        return [.. Paragraphs(flow)[0].Paragraph.Spans[0].Style.Fonts];
    }

    private static List<ParagraphBlock> Paragraphs(FlowDocument flow) => [.. Flatten(flow.Blocks).OfType<ParagraphBlock>()];

    private static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ContainerBlock c ? Flatten(c.Children).Prepend(b) : [b]);
}
