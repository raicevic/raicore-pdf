using AngleSharp;
using AngleSharp.Dom;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Html.Diagnostics;

namespace RaiCore.Pdf.Tests.Html;

/// <summary>Diagnostics (#59): resources that did not load, reported once per issue with the element path and a count.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlDiagnosticsTests
{
    private static readonly string Missing = new Uri(Path.Combine(Path.GetTempPath(), "raicore-missing", "logo.png")).AbsoluteUri;

    /// <summary>A 1 x 1 PNG.</summary>
    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static async Task<List<HtmlDiagnostic>> Diagnose(string html, HtmlConverterOptions? options = null)
    {
        var found = new List<HtmlDiagnostic>();
        options = Copy(options ?? new HtmlConverterOptions(), found.Add);
        _ = await HtmlToPdf.ConvertAsync(html, options, TestContext.Current.CancellationToken);
        return found;
    }

    private static HtmlConverterOptions Copy(HtmlConverterOptions options, Action<HtmlDiagnostic> diagnostics) => new()
    {
        BaseUri = options.BaseUri,
        Limits = options.Limits,
        ResourceLoader = options.ResourceLoader,
        Diagnostics = diagnostics,
    };

    [Fact]
    public async Task A_missing_image_is_reported_once_with_its_path_and_how_many_elements_use_it()
    {
        var found = await Diagnose($"""
            <html><body>
              <p><img id="logo" src="{Missing}"></p>
              <div class="card wide extra"><img src="{Missing}"><img src="{Missing}"></div>
              <div style="background-image: url('{Missing}')">x</div>
            </body></html>
            """);

        var diagnostic = Assert.Single(found);
        Assert.Equal(HtmlDiagnosticKind.ResourceNotLoaded, diagnostic.Kind);
        Assert.Equal(HtmlDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(Missing, diagnostic.Subject);
        Assert.Equal("html > body > p > img#logo", diagnostic.ElementPath);
        Assert.Equal(4, diagnostic.Count); // Three images and a background.
        Assert.Contains("resource loader returned nothing", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relative_urls_without_a_base_uri_say_so()
    {
        var diagnostic = Assert.Single(await Diagnose("<img src='images/logo.png'>"));

        Assert.Equal("images/logo.png", diagnostic.Subject);
        Assert.Contains("HtmlConverterOptions.BaseUri", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_and_malformed_images_are_told_apart()
    {
        var found = await Diagnose("""
            <img src="data:image/png;base64,AAAAAAAAAAAAAAAA">
            <img src="data:image/png;base64">
            """);

        Assert.Equal([HtmlDiagnosticKind.ResourceInvalid, HtmlDiagnosticKind.ResourceNotLoaded], found.Select(d => d.Kind));
        Assert.Contains("cannot be read", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("malformed", found[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_over_a_limit_are_too_large()
    {
        var bytes = await Diagnose(
            $"<img src='{Pixel}'>",
            new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxResourceBytes = 10 } });
        var photo = new Uri(Fixtures.Path("Images/rgb.jpg")).AbsoluteUri;
        var pixels = await Diagnose(
            $"<img src='{photo}'>",
            new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxImagePixels = 4 } });

        Assert.Equal(HtmlDiagnosticKind.ResourceTooLarge, Assert.Single(bytes).Kind);
        Assert.Contains("MaxResourceBytes", bytes[0].Message, StringComparison.Ordinal);
        Assert.Equal(Pixel[..48] + "\u2026", bytes[0].Subject); // Long data: URIs are cut.
        Assert.Equal(HtmlDiagnosticKind.ResourceTooLarge, Assert.Single(pixels).Kind);
        Assert.Equal(photo, pixels[0].Subject);
        Assert.Contains("too large", pixels[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inline_svg_that_cannot_be_read_is_reported()
    {
        // SVG elements nested deeper than SVG images allow (the document's own limit is raised to let them through).
        var groups = string.Concat(Enumerable.Repeat("<g>", 300)) + string.Concat(Enumerable.Repeat("</g>", 300));
        var diagnostic = Assert.Single(await Diagnose(
            $"<p><svg width='10' height='10'>{groups}</svg></p>",
            new HtmlConverterOptions { Limits = ResourceLimits.Default with { MaxNestingDepth = 1000 } }));

        Assert.Equal(HtmlDiagnosticKind.ResourceInvalid, diagnostic.Kind);
        Assert.Equal("<svg>", diagnostic.Subject);
        Assert.Equal("html > body > p > svg", diagnostic.ElementPath);
    }

    [Fact]
    public async Task Missing_print_style_sheets_are_reported_and_screen_ones_ignored()
    {
        var found = await Diagnose($"""
            <html><head>
              <link rel="stylesheet" href="{Missing}.css">
              <link rel="stylesheet" media="screen" href="{Missing}.screen.css">
            </head><body>x</body></html>
            """);

        var diagnostic = Assert.Single(found);
        Assert.Equal(Missing + ".css", diagnostic.Subject);
        Assert.Equal("html > head > link:nth-of-type(1)", diagnostic.ElementPath);
    }

    [Fact]
    public async Task A_web_font_is_reported_only_when_none_of_its_sources_load()
    {
        var bundled = new Uri(Fixtures.Path("LiberationSans-Regular.ttf")).AbsoluteUri;
        var found = await Diagnose($$"""
            <style>
              @font-face { font-family: Broken; src: url('{{Missing}}.woff2') format('woff2'), url('data:font/woff2;base64,AAAAAAAA') }
              @font-face { font-family: Fallback; src: url('{{Missing}}.woff2') format('woff2'), url('{{bundled}}') format('truetype') }
            </style>
            <p style="font-family: Broken">One</p><p style="font-family: Fallback">Two</p>
            """);

        var diagnostic = Assert.Single(found);
        Assert.Equal(HtmlDiagnosticKind.ResourceInvalid, diagnostic.Kind); // The last source's reason.
        Assert.Contains("font-family \"Broken\" falls back", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(diagnostic.ElementPath);
    }

    [Fact]
    public async Task A_web_font_whose_license_forbids_embedding_is_reported()
    {
        var data = File.ReadAllBytes(Fixtures.Path("LiberationSans-Regular.ttf"));
        Fonts.FontEmbeddingTests.SetFsType(data, 0x0002); // Restricted License embedding.
        var found = await Diagnose($$"""
            <style>@font-face { font-family: Licensed; src: url('data:font/ttf;base64,{{Convert.ToBase64String(data)}}') }</style>
            <p style="font-family: Licensed">Text</p>
            """);

        var diagnostic = Assert.Single(found);
        Assert.Equal(HtmlDiagnosticKind.FontNotEmbeddable, diagnostic.Kind);
        Assert.Contains("Licensed", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layout_passes_for_page_references_do_not_count_twice()
    {
        var found = await Diagnose($$"""
            <style>a::after { content: " (page " target-counter(attr(href), page) ")" }</style>
            <p><a href="#end">To the end</a> <img src="{{Missing}}"></p>
            <h2 id="end" style="break-before: page">End</h2>
            """);

        Assert.Equal(1, Assert.Single(found).Count);
    }

    [Fact]
    public async Task Diagnostics_arrive_in_order_before_every_kind_of_conversion_completes()
    {
        var html = $"<img src='{Missing}'><img src='data:image/png;base64'>";
        var streamed = new List<HtmlDiagnostic>();
        var flowed = new List<HtmlDiagnostic>();

        using (var output = new MemoryStream())
        {
            await HtmlToPdf.ConvertToStreamAsync(html, output, new HtmlConverterOptions { Diagnostics = streamed.Add }, TestContext.Current.CancellationToken);
        }

        HtmlToPdf.CreateFlowDocument(html, new HtmlConverterOptions { Diagnostics = flowed.Add });
        var converted = await Diagnose(html);

        Assert.All(new[] { streamed, flowed, converted }, list =>
            Assert.Equal([HtmlDiagnosticKind.ResourceNotLoaded, HtmlDiagnosticKind.ResourceNotLoaded], list.Select(d => d.Kind)));
        Assert.Equal(Missing, converted[0].Subject);
    }

    [Fact]
    public async Task Nothing_is_reported_for_a_document_whose_resources_load()
    {
        Assert.Empty(await Diagnose($"<p>Text <img src='{Pixel}'></p>"));
    }

    [Theory]
    [InlineData("<div id='a'><p class='x y z'>t</p></div>", "p", "html > body > div#a > p.x.y")]
    [InlineData("<ul><li>1</li><li>2</li><li id='last'>3</li></ul>", "li:nth-child(2)", "html > body > ul > li:nth-of-type(2)")]
    [InlineData("<ul><li>1</li><li>2</li><li id='last'>3</li></ul>", "#last", "html > body > ul > li#last")]
    public async Task Element_paths_name_ids_classes_and_positions(string html, string selector, string expected)
    {
        var context = BrowsingContext.New(Configuration.Default);
        using var document = await context.OpenAsync(request => request.Content(html), TestContext.Current.CancellationToken);

        Assert.Equal(expected, DiagnosticCollector.ElementPath(document.QuerySelector(selector)!));
    }
}
