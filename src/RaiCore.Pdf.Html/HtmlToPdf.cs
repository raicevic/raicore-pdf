using System.Text;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html.Boxes;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html;

/// <summary>
/// Converts HTML and CSS to PDF with RaiCore.Pdf's own layout engine (no browser, no JavaScript).
/// </summary>
/// <example>
/// <code>
/// var pdf = HtmlToPdf.Convert("&lt;h1&gt;Invoice&lt;/h1&gt;&lt;p&gt;Thank you!&lt;/p&gt;");
/// pdf.Save("invoice.pdf");
/// </code>
/// </example>
public static class HtmlToPdf
{
    /// <summary>
    /// How many times a document with page cross-references is laid out at most: once with placeholders, then with
    /// the pages found, until they stay the same.
    /// </summary>
    private const int MaxLayoutPasses = 4;

    /// <summary>Converts an HTML document to a PDF document.</summary>
    public static PdfDocument Convert(string html, HtmlConverterOptions? options = null) =>
        ConvertAsync(html, options).GetAwaiter().GetResult();

    /// <summary>Converts an HTML document to PDF bytes.</summary>
    public static byte[] ConvertToBytes(string html, HtmlConverterOptions? options = null) => Convert(html, options).ToArray();

    /// <summary>Converts an HTML document to a PDF document.</summary>
    public static async Task<PdfDocument> ConvertAsync(string html, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (flow, title) = await LayoutAsync(html, options, cancellationToken).ConfigureAwait(false);
        var document = flow.Render();
        document.Info.Title = title;
        document.Info.Creator = "RaiCore.Pdf.Html";
        return document;
    }

    /// <summary>
    /// Lays out an HTML document without rendering it, so you can add a <see cref="FlowDocument.PageDecorator"/>
    /// (headers, footers, page numbers) or render it into an existing <see cref="PdfDocument"/>.
    /// </summary>
    public static FlowDocument CreateFlowDocument(string html, HtmlConverterOptions? options = null) =>
        LayoutAsync(html, options, CancellationToken.None).GetAwaiter().GetResult().Flow;

    private static async Task<(FlowDocument Flow, string? Title)> LayoutAsync(string html, HtmlConverterOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlConverterOptions();
        var fonts = options.Fonts ?? BundledFonts.CreateCollection();

        // A print device; the viewport (needed to compute relative units such as background-position) is the
        // page's content area, as in browsers' print layout.
        var device = new DefaultRenderDevice
        {
            Category = DeviceCategory.Printer,
            DeviceWidth = (int)(options.Page.Size.Width / CssValues.PointsPerPixel),
            DeviceHeight = (int)(options.Page.Size.Height / CssValues.PointsPerPixel),
            ViewPortWidth = (int)(options.Page.ContentWidth / CssValues.PointsPerPixel),
            ViewPortHeight = (int)(options.Page.ContentHeight / CssValues.PointsPerPixel),
        };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        using var document = await context.OpenAsync(
            request =>
            {
                request.Content(html);
                if (options.BaseUri is not null)
                {
                    request.Address(options.BaseUri);
                }
            },
            cancellationToken).ConfigureAwait(false);

        var styleSheets = PrepareStyles(document, options);
        var cssTexts = styleSheets.Select(s => s.Css).ToList();
        var pageRules = PageRules.Load(options.Page, cssTexts);
        var setup = pageRules.Setup;

        // @page can change the page size and margins; styles are computed lazily, so vw/vh still see the final area.
        device.ViewPortWidth = (int)(setup.ContentWidth / CssValues.PointsPerPixel);
        device.ViewPortHeight = (int)(setup.ContentHeight / CssValues.PointsPerPixel);

        var paged = Paged.PagedMedia.Load(document, cssTexts);
        var webFonts = WebFonts.Load(styleSheets, options.ResourceLoader, fonts);

        // Named pages and @page :first/:left/:right/:blank can give pages their own size and margins. Percentages in a
        // named page resolve against its content width (that of a right page that is not the first).
        var builder = new BoxBuilder(document, options, fonts, options.BaseUri, paged, webFonts)
        {
            PageContentWidth = pageRules.VariesPerPage ? name => pageRules.Resolve(new PageContext(3, name, PageSide.Right, IsBlank: false)).ContentWidth : null,
        };
        var flow = Build(builder, setup, pageRules);

        // Cross-references to pages (target-counter(..., page)) are only known after pagination: lay out again with
        // the pages found until they no longer change (a page number that is wider can move what follows).
        for (var pass = 2; pass <= MaxLayoutPasses && builder.UsesPageReferences; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!builder.ResolvePageReferences(flow.MapPages()))
            {
                break;
            }

            flow = Build(builder, setup, pageRules);
        }

        if (paged.HasMarginBoxes && document.DocumentElement is { } root)
        {
            flow.PageDecorator = paged.CreateDecorator(builder, builder.Style(root));
        }

        var title = document.Title;
        return (flow, string.IsNullOrWhiteSpace(title) ? null : title.Trim());
    }

    /// <summary>Builds the boxes of the document into a flow with the page setups of the @page rules.</summary>
    private static FlowDocument Build(BoxBuilder builder, PageSetup setup, PageRules pageRules)
    {
        var flow = new FlowDocument(setup) { PageSetupResolver = pageRules.VariesPerPage ? pageRules.Resolve : null };
        foreach (var block in builder.Build(setup.ContentWidth, setup.ContentHeight))
        {
            flow.Add(block);
        }

        return flow;
    }

    /// <summary>
    /// Puts the user-agent stylesheet first, replaces linked stylesheets with their content (loaded through the
    /// resource loader) and appends the user stylesheet. Returns all CSS text with the URI its relative URLs resolve
    /// against (a linked stylesheet's own address), for reading the rules AngleSharp does not keep.
    /// </summary>
    private static List<StyleSheetText> PrepareStyles(IDocument document, HtmlConverterOptions options)
    {
        var head = document.Head ?? document.DocumentElement.AppendElement(document.CreateElement("head"));
        var userAgentCss = RewriteCss(UserAgentStyleSheet.Css);
        var cssTexts = new List<StyleSheetText> { new(userAgentCss, null) };
        var linkedBases = new Dictionary<IElement, Uri?>();

        var userAgent = document.CreateElement("style");
        userAgent.TextContent = userAgentCss;
        head.Prepend(userAgent);

        foreach (var link in document.QuerySelectorAll("link[rel~=stylesheet][href]").ToList())
        {
            var media = link.GetAttribute("media");
            var href = link.GetAttribute("href")!;
            string? css = null;
            var sheetUri = options.BaseUri;
            if (media is null or "" || media.Contains("print", StringComparison.OrdinalIgnoreCase) || media.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                byte[]? data = null;
                if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    data = Resources.DecodeDataUri(href);
                }
                else if (ResolveUri(options.BaseUri, href) is { } uri)
                {
                    data = options.ResourceLoader(uri);
                    sheetUri = uri;
                }

                css = data is null ? null : Encoding.UTF8.GetString(data);
            }

            if (css is null)
            {
                link.Remove();
                continue;
            }

            var style = document.CreateElement("style");
            style.TextContent = sheetUri is not null ? CssBackgrounds.AbsolutizeUrls(css, sheetUri) : css; // url() is relative to the sheet.
            link.Replace(style);
            linkedBases[style] = sheetUri;
        }

        foreach (var style in document.QuerySelectorAll("style").Skip(1))
        {
            if (style.TextContent is var text && RewriteCss(text) is var rewritten && rewritten != text)
            {
                style.TextContent = rewritten;
            }

            var media = style.GetAttribute("media");
            if (media is null or "" || media.Contains("print", StringComparison.OrdinalIgnoreCase) || media.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                cssTexts.Add(new(style.TextContent, linkedBases.GetValueOrDefault(style, options.BaseUri)));
            }
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var inline = element.GetAttribute("style")!;
            if (RewriteCss(inline) is var rewritten && rewritten != inline)
            {
                element.SetAttribute("style", rewritten);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.UserStyleSheet))
        {
            var user = document.CreateElement("style");
            user.TextContent = RewriteCss(options.UserStyleSheet);
            head.AppendChild(user);
            cssTexts.Add(new(options.UserStyleSheet, options.BaseUri));
        }

        return cssTexts;
    }

    /// <summary>Rewrites CSS that AngleSharp would misread before it is parsed (see LegacyBreaks and LogicalSides).</summary>
    private static string RewriteCss(string css) => CssBackgrounds.RewriteShorthand(LogicalSides.Rewrite(LegacyBreaks.Rewrite(css)));

    private static Uri? ResolveUri(Uri? baseUri, string reference) =>
        Uri.TryCreate(reference, UriKind.Absolute, out var absolute) ? absolute
        : baseUri is not null && Uri.TryCreate(baseUri, reference, out var relative) ? relative
        : null;
}
