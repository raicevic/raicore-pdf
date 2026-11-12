using System.Text;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html.Boxes;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html;

/// <summary>
/// Converts HTML and CSS to PDF with RaiCore.Pdf's own layout engine (no browser, no JavaScript).
/// </summary>
/// <remarks>
/// Thread-safe: conversions can run in parallel, and each one is independent (its own DOM, styles, layout and
/// document), so the same input and options give the same PDF however many conversions run at once. They share the
/// fonts of <see cref="HtmlConverterOptions.Fonts"/> (or the bundled fonts, loaded once per process) and the bundled
/// hyphenation patterns without copying them. Fonts declared with <c>@font-face</c> are loaded per conversion.
/// </remarks>
/// <example>
/// <code>
/// var pdf = HtmlToPdf.Convert("&lt;h1&gt;Invoice&lt;/h1&gt;&lt;p&gt;Thank you!&lt;/p&gt;");
/// pdf.Save("invoice.pdf");
/// </code>
/// </example>
public static partial class HtmlToPdf
{
    /// <summary>
    /// How many times a document with page cross-references is laid out at most: once with placeholders, then with
    /// the pages found, until they stay the same.
    /// </summary>
    private const int MaxLayoutPasses = 4;

    /// <summary>
    /// The stack size of the thread a conversion runs on. Layout recurses once or more per nesting level of the HTML,
    /// which a default 1 MB stack cannot take for deeply nested documents; the space is reserved, not committed.
    /// </summary>
    private const int ConversionStackSize = 64 * 1024 * 1024;

    /// <summary>Converts an HTML document to a PDF document.</summary>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    public static PdfDocument Convert(string html, HtmlConverterOptions? options = null) =>
        ConvertAsync(html, options).GetAwaiter().GetResult();

    /// <summary>Converts an HTML document to PDF bytes.</summary>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    public static byte[] ConvertToBytes(string html, HtmlConverterOptions? options = null) => Convert(html, options).ToArray();

    /// <summary>
    /// Converts an HTML document to PDF and writes it to <paramref name="output"/> page by page (see
    /// <see cref="PdfDocument.BeginSave"/>): each page is written as soon as it is drawn, so long documents need less
    /// memory than with <see cref="Convert"/>. The stream is left open.
    /// </summary>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    public static void ConvertToStream(string html, Stream output, HtmlConverterOptions? options = null) =>
        ConvertToStreamAsync(html, output, options).GetAwaiter().GetResult();

    /// <summary>Converts an HTML document to PDF and writes it to <paramref name="output"/> page by page (see <see cref="ConvertToStream"/>).</summary>
    /// <remarks>
    /// Like <see cref="ConvertAsync(string, HtmlConverterOptions?, CancellationToken)"/>, it runs on a thread of its own
    /// and applies the options' <see cref="HtmlConverterOptions.Limits"/>; the stream is written from that thread.
    /// </remarks>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task ConvertToStreamAsync(string html, Stream output, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new HtmlConverterOptions();
        return RunLimited(
            options,
            () =>
            {
                var (flow, metadata) = Layout(html, options, cancellationToken);

                // Everything ConvertAsync sets, but before writing begins: page by page, it is read by BeginSave.
                var document = new PdfDocument { Tagged = options.Tagged, PdfUA = options.PdfUA };
                metadata.ApplyTo(document, options, HtmlMetadata.DefaultCreator);
                document.Encryption = options.Encryption;
                document.PdfAConformance = options.PdfAConformance;
                if (options.PdfAConformance != PdfAConformance.None)
                {
                    document.EmbedStandardFonts();
                }

                document.BeginSave(output);
                flow.RenderTo(document);
                document.EndSave();
                return true;
            },
            cancellationToken);
    }

    /// <summary>Converts an HTML document to a PDF document.</summary>
    /// <remarks>
    /// <para>
    /// The document's metadata comes from the HTML: the title from <c>&lt;title&gt;</c>, the language from
    /// <c>&lt;html lang&gt;</c> (or <c>xml:lang</c>), and the author, subject, keywords and creator from
    /// <c>&lt;meta name="author|description|keywords|generator"&gt;</c>. The options' <see cref="HtmlConverterOptions.Title"/>,
    /// <see cref="HtmlConverterOptions.Language"/> and the like override them.
    /// </para>
    /// <para>
    /// The conversion runs on a thread of its own (with a large stack, for deeply nested documents) and checks
    /// <paramref name="cancellationToken"/> and the <see cref="ResourceLimits.Timeout"/> while parsing, laying out and
    /// rendering.
    /// </para>
    /// </remarks>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<PdfDocument> ConvertAsync(string html, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default) =>
        ConvertAsync(html, options, HtmlMetadata.DefaultCreator, cancellationToken);

    /// <summary>Converts an HTML document, naming <paramref name="defaultCreator"/> as the creator unless the options or the document name one.</summary>
    internal static Task<PdfDocument> ConvertAsync(string html, HtmlConverterOptions? options, string defaultCreator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlConverterOptions();
        return RunLimited(
            options,
            () =>
            {
                var (flow, metadata) = Layout(html, options, cancellationToken);
                var document = new PdfDocument { Tagged = options.Tagged, PdfUA = options.PdfUA };
                flow.RenderTo(document);
                metadata.ApplyTo(document, options, defaultCreator);
                document.Encryption = options.Encryption;
                document.PdfAConformance = options.PdfAConformance;
                if (options.PdfAConformance != PdfAConformance.None)
                {
                    document.EmbedStandardFonts();
                }

                return document;
            },
            cancellationToken);
    }

    /// <summary>
    /// Lays out an HTML document without rendering it, so you can add a <see cref="FlowDocument.PageDecorator"/>
    /// (headers, footers, page numbers) or render it into an existing <see cref="PdfDocument"/>.
    /// </summary>
    /// <remarks>
    /// The options' <see cref="HtmlConverterOptions.Limits"/> apply to building the layout; rendering the returned
    /// document later applies <see cref="ResourceLimits.Default"/> (or the limits of an enclosing
    /// <see cref="ResourceLimits.Enforce"/> scope), on the calling thread's stack.
    /// </remarks>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    public static FlowDocument CreateFlowDocument(string html, HtmlConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlConverterOptions();
        return RunLimited(options, () => Layout(html, options, CancellationToken.None).Flow, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread with a large stack, with the options' limits enforced and the
    /// cancellation token observed.
    /// </summary>
    private static Task<T> RunLimited<T>(HtmlConverterOptions options, Func<T> work, CancellationToken cancellationToken)
    {
        var limits = options.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(options));
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        T Run()
        {
            using (limits.Enforce(cancellationToken))
            {
                return work();
            }
        }

        if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
        {
            return Task.FromResult(Run()); // No threads to start: the caller's stack it is.
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    completion.SetResult(Run());
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
                {
                    completion.SetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            ConversionStackSize)
        {
            IsBackground = true,
            Name = "RaiCore.Pdf HTML conversion",
        };
        thread.Start();
        return completion.Task;
    }

    private static (FlowDocument Flow, HtmlMetadata Metadata) Layout(string html, HtmlConverterOptions options, CancellationToken cancellationToken)
    {
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
        using var document = Parse(context, html, options.BaseUri, cancellationToken);
        CheckStructure(document, options.Limits);

        var styleSheets = PrepareStyles(document, options);
        RemoveUnmatchableRules(document);
        var cssTexts = styleSheets.Select(s => s.Css).ToList();
        var pageRules = PageRules.Load(options.Page, cssTexts);
        var setup = pageRules.Setup;

        // @page can change the page size and margins; styles are computed lazily, so vw/vh still see the final area.
        device.ViewPortWidth = (int)(setup.ContentWidth / CssValues.PointsPerPixel);
        device.ViewPortHeight = (int)(setup.ContentHeight / CssValues.PointsPerPixel);

        var paged = Paged.PagedMedia.Load(document, cssTexts);
        var webFonts = WebFonts.Load(styleSheets, options.ResourceLoader, fonts);
        ResourceLimits.Checkpoint();

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
            ResourceLimits.Checkpoint();
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

        return (flow, HtmlMetadata.Read(document));
    }

    /// <summary>
    /// Parses the HTML, synchronously so the rest of the conversion stays on this thread. Parsing stops when
    /// <paramref name="cancellationToken"/> is canceled or the time limit is reached (pathological markup can make the
    /// HTML parser slow).
    /// </summary>
    private static IDocument Parse(IBrowsingContext context, string html, Uri? baseUri, CancellationToken cancellationToken)
    {
        var scope = ResourceLimits.CurrentScope;
        using var parsing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (scope?.Remaining is { } remaining && remaining != Timeout.InfiniteTimeSpan)
        {
            parsing.CancelAfter(remaining);
        }

        try
        {
            return context.OpenAsync(
                request =>
                {
                    request.Content(html);
                    if (baseUri is not null)
                    {
                        request.Address(baseUri);
                    }
                },
                parsing.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && scope is not null)
        {
            throw scope.TimeoutExceeded();
        }
    }

    /// <summary>
    /// Checks the document against <see cref="ResourceLimits.MaxElements"/> and <see cref="ResourceLimits.MaxNestingDepth"/>
    /// before anything recurses over it.
    /// </summary>
    private static void CheckStructure(IDocument document, ResourceLimits limits)
    {
        if (document.DocumentElement is not { } root)
        {
            return;
        }

        var count = 0;
        var pending = new Stack<(IElement Element, int Depth)>();
        pending.Push((root, 1));
        while (pending.Count > 0)
        {
            var (element, depth) = pending.Pop();
            if (++count > limits.MaxElements)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxElements), limits.MaxElements, $"The document has more than the limit of {limits.MaxElements} elements.");
            }

            if (depth > limits.MaxNestingDepth)
            {
                throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxNestingDepth), limits.MaxNestingDepth, $"The document's elements are nested more than the limit of {limits.MaxNestingDepth} deep.");
            }

            for (var child = element.FirstElementChild; child is not null; child = child.NextElementSibling)
            {
                pending.Push((child, depth + 1));
            }
        }
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
                    data = Resources.Limit(Resources.DecodeDataUri(href));
                }
                else if (ResolveUri(options.BaseUri, href) is { } uri)
                {
                    data = Resources.Load(options.ResourceLoader, uri);
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

    /// <summary>
    /// Removes style rules whose selector AngleSharp could not read: it keeps them, and throws
    /// <see cref="NullReferenceException"/> matching the rules nested in them (fuzzing found <c>:{&amp;{}}</c>). They
    /// cannot match an element anyway.
    /// </summary>
    private static void RemoveUnmatchableRules(IDocument document)
    {
        foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            Remove(sheet.Rules, sheet.RemoveAt);
        }

        static void Remove(ICssRuleList rules, Action<int> removeAt)
        {
            for (var i = rules.Length - 1; i >= 0; i--)
            {
                switch (rules[i])
                {
                    case ICssStyleRule { SelectorText: null }:
                        removeAt(i);
                        break;
                    case ICssGroupingRule group:
                        Remove(group.Rules, group.RemoveAt);
                        break;
                }
            }
        }
    }

    /// <summary>Rewrites CSS that AngleSharp would misread before it is parsed (see LegacyBreaks and LogicalSides).</summary>
    private static string RewriteCss(string css) => CssBackgrounds.RewriteShorthand(LogicalSides.Rewrite(LegacyBreaks.Rewrite(StripShadows(css))));

    /// <summary>
    /// Removes <c>box-shadow</c> and <c>text-shadow</c>, which are not drawn: AngleSharp throws a
    /// <see cref="NullReferenceException"/> computing some malformed ones (fuzzing found <c>box-shadow: 1px1px</c>).
    /// </summary>
    private static string StripShadows(string css) =>
        css.Contains("shadow", StringComparison.OrdinalIgnoreCase) ? ShadowRegex().Replace(css, string.Empty) : css;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w-])(?:box|text)-shadow\s*:[^;}]*;?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ShadowRegex();

    private static Uri? ResolveUri(Uri? baseUri, string reference) =>
        Uri.TryCreate(reference, UriKind.Absolute, out var absolute) ? absolute
        : baseUri is not null && Uri.TryCreate(baseUri, reference, out var relative) ? relative
        : null;
}
