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
    public async Task Nesting_selectors_are_reported_as_unsupported()
    {
        // AngleSharp threw NullReferenceException matching "&" outside a nested rule (fuzzing found ",&{").
        var found = await Diagnose("<style>p, & { color: red }</style><p>text</p>");

        var diagnostic = Assert.Single(found, d => d.Kind == HtmlDiagnosticKind.UnsupportedSelector);
        Assert.Equal("&", diagnostic.Subject);
        Assert.Contains("nesting", diagnostic.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Unsupported_properties_are_reported_where_their_rule_applies()
    {
        var found = await Diagnose("""
            <style>
              .cols { column-count: 2 }
              .unused { grid-area: main }
              .raised { box-shadow: 0 1px 2px gray }
            </style>
            <p class="cols">One</p><p class="cols">Two</p><p style="text-shadow: 1px 1px red">Three</p><p class="raised">Four</p>
            """);

        // .unused matches nothing: no effect, no report. Shadows are checked as written, though they are removed before layout.
        Assert.Equal(["column-count", "box-shadow", "text-shadow"], found.Select(d => d.Subject));
        Assert.All(found, d => Assert.Equal((HtmlDiagnosticKind.UnsupportedProperty, HtmlDiagnosticSeverity.Warning), (d.Kind, d.Severity)));
        Assert.Equal("html > body > p.cols:nth-of-type(1)", found[0].ElementPath);
        Assert.Equal(2, found[0].Count);
        Assert.Equal("html > body > p:nth-of-type(3)", found[2].ElementPath);
    }

    [Fact]
    public async Task Screen_only_and_vendor_prefixed_properties_are_information()
    {
        var found = await Diagnose("<p style='cursor: pointer; transition: color 1s; -webkit-box-shadow: 0 0 2px red'>x</p>");

        Assert.Equal(["cursor", "transition", "-webkit-box-shadow"], found.Select(d => d.Subject));
        Assert.All(found, d => Assert.Equal(HtmlDiagnosticSeverity.Info, d.Severity));
        Assert.Contains("vendor-prefixed", found[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_values_and_values_laid_out_differently_are_reported()
    {
        var found = await Diagnose("""
            <style>.grid { display: grid !important } .stick { position: sticky }</style>
            <p style="color: notacolor">a</p><div class="grid">b</div><div class="stick">c</div>
            """);

        Assert.Equal(["display: grid", "position: sticky", "color: notacolor"], found.Select(d => d.Subject));
        Assert.All(found, d => Assert.Equal(HtmlDiagnosticKind.UnsupportedValue, d.Kind));
        Assert.Contains("Grid layout is not supported", found[0].Message, StringComparison.Ordinal);
        Assert.Contains("ignored", found[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_selectors_and_pseudo_elements_are_reported()
    {
        var found = await Diagnose("""
            <style>
              p::first-letter { font-size: 2em }
              ::selection { color: red }
              p::before { content: "> "; column-count: 2 }
              p:nonsense-pseudo-class { color: red }
            </style>
            <p>Text</p>
            """);

        Assert.Equal(["::first-letter", "::selection", "column-count", "p:nonsense-pseudo-class"], found.Select(d => d.Subject));
        Assert.Equal([HtmlDiagnosticSeverity.Warning, HtmlDiagnosticSeverity.Info, HtmlDiagnosticSeverity.Warning, HtmlDiagnosticSeverity.Warning], found.Select(d => d.Severity));
        Assert.Equal("html > body > p", found[2].ElementPath); // A ::before rule's declarations are checked at the element.
    }

    [Fact]
    public async Task At_rules_that_are_skipped_are_reported()
    {
        var found = await Diagnose("""
            <style>
              @import url("other.css");
              @keyframes spin { to { transform: rotate(1turn) } }
              @layer base { p { color: red } }
              @media screen { p { grid-area: a } }
              @media print { p { column-count: 3 } }
              @page { @top-center { content: "Header" } }
            </style>
            <p>Text</p>
            """);

        Assert.Equal(["@import", "@keyframes", "@layer", "column-count"], found.Select(d => d.Subject)); // Screen rules are not checked.
        Assert.Equal([HtmlDiagnosticSeverity.Warning, HtmlDiagnosticSeverity.Info, HtmlDiagnosticSeverity.Warning], found.Take(3).Select(d => d.Severity));
        Assert.All(found.Take(3), d => Assert.Null(d.ElementPath));
    }

    [Fact]
    public async Task A_document_using_supported_css_reports_nothing()
    {
        var found = await Diagnose("""
            <!doctype html><html lang="da"><head><title>T</title><style>
            @page { size: A4; margin: 2cm; @top-center { content: string(chapter) } @bottom-right { content: counter(page) " / " counter(pages) } }
            @page wide { size: A4 landscape }
            :root { --accent: #036; --gap: 8px }
            body { margin: 0; font: 11pt/1.4 "Liberation Sans", sans-serif; color: var(--accent) }
            h1 { string-set: chapter content(); bookmark-level: 1; break-before: page; page-break-after: avoid }
            h2 { counter-increment: sec } h2::before { content: counter(sec) ". " }
            a::after { content: " (" target-counter(attr(href), page) ")" }
            .card { background: linear-gradient(90deg, #fde, #def), #fff; border: 1px solid #ccc; border-radius: 6px; padding: calc(var(--gap) * 2); margin-inline-start: 1em; box-sizing: border-box }
            .row { display: flex; flex-flow: row wrap; gap: var(--gap); justify-content: space-between; align-items: center }
            .row > * { flex: 1 1 0; min-width: 0 }
            table { border-collapse: collapse; width: 100%; table-layout: fixed }
            tr:nth-child(even) td { background-color: rgba(0,0,0,.04); text-align: end; vertical-align: top }
            p { hyphens: auto; -webkit-hyphens: auto; orphans: 3; widows: 3; text-indent: 1em; letter-spacing: .01em }
            .wide { page: wide } .clip { overflow: hidden; text-overflow: ellipsis; white-space: nowrap }
            .abs { position: absolute; inset: 0 auto auto 0; z-index: 2 } .float { float: inline-start; clear: both }
            .big { font-size: clamp(10pt, 2vw, 14pt); text-transform: uppercase; text-decoration: underline wavy red; text-underline-offset: 2px }
            ul { list-style: square inside } q { quotes: "\201C" "\201D" }
            @media print { .noprint { display: none } }
            @supports (display: flex) { .s { display: flex } }
            @font-face { font-family: X; src: local("Liberation Sans") }
            </style></head><body>
            <h1 id="c1">Kapitel</h1><h2>Sek</h2><p class="big">Tekst <q>citat</q> <a href="#c1">link</a></p>
            <div class="card row"><div class="clip">a</div><div class="float">b</div><div class="abs">c</div></div>
            <table><tr><td>1</td></tr><tr><td>2</td></tr></table><ul><li>x</li></ul><div class="wide noprint s">w</div>
            <div style="margin: 0 auto; max-width: 300px; opacity: .8; border-top-left-radius: 4px 8px">inline</div>
            </body></html>
            """);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Content_that_does_not_fit_is_reported_at_its_element()
    {
        var found = await Diagnose($"""
            <div style="width: 100px"><p id="word">Short Donaudampfschifffahrtsgesellschaftskapitaenswitwenrente end</p></div>
            <div style="width: 100px; overflow: hidden"><p>Clipped Donaudampfschifffahrtsgesellschaftskapitaenswitwenrente</p></div>
            <table id="wide" style="width: 100px"><tr><td>Unbreakablewordnumberone</td><td>Unbreakablewordnumbertwo</td></tr></table>
            <img id="tall" src="{Tall}" style="display: block">
            <p id="inline">A line made taller than a page by an inline image <img src="{Tall}"></p>
            """);

        Assert.Equal(
            [(HtmlDiagnosticKind.Overflow, "line wider than its box", "html > body > div:nth-of-type(1) > p#word"),
             (HtmlDiagnosticKind.Overflow, "table wider than its container", "html > body > table#wide"),
             (HtmlDiagnosticKind.ScaledToFit, "image taller than a page", "html > body > img#tall"),
             (HtmlDiagnosticKind.Clipped, "content taller than a page", "html > body > p#inline")],
            found.Select(d => (d.Kind, d.Subject, d.ElementPath)));
        Assert.All(found.Take(2), d => Assert.Contains("(page 1)", d.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_laid_out_document_reports_layout_issues_when_it_is_rendered()
    {
        var found = new List<HtmlDiagnostic>();
        var flow = HtmlToPdf.CreateFlowDocument($"""<img src="{Missing}"><img src="{Tall}" style="display: block">""", new HtmlConverterOptions { Diagnostics = found.Add });
        Assert.Equal([HtmlDiagnosticKind.ResourceNotLoaded], found.Select(d => d.Kind));

        flow.Render();

        Assert.Equal([HtmlDiagnosticKind.ResourceNotLoaded, HtmlDiagnosticKind.ScaledToFit], found.Select(d => d.Kind));
    }

    /// <summary>A 30 x 1500 CSS pixel SVG image: taller than a page.</summary>
    private const string Tall = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='30' height='1500'%3E%3Crect width='30' height='1500' fill='teal'/%3E%3C/svg%3E";

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
