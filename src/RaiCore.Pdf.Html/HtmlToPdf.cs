using System.Text;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html.Boxes;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Html.Diagnostics;
using RaiCore.Pdf.Layout;

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

    /// <summary>Converts an HTML document to PDF bytes (see <see cref="ConvertAsync(string, HtmlConverterOptions?, CancellationToken)"/>).</summary>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<byte[]> ConvertToBytesAsync(string html, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default) =>
        (await ConvertAsync(html, options, cancellationToken).ConfigureAwait(false)).ToArray();

    /// <summary>
    /// Converts an HTML document to PDF and writes it to <paramref name="output"/> page by page (see
    /// <see cref="PdfDocument.BeginSave"/>): each page is written as soon as it is drawn, so long documents need less
    /// memory than with <see cref="Convert"/>. The stream is left open.
    /// </summary>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    public static void ConvertToStream(string html, Stream output, HtmlConverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        ConvertToStream(html, output, options ?? new HtmlConverterOptions(), HtmlMetadata.DefaultCreator, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Converts an HTML document to PDF and writes it to <paramref name="output"/> page by page (see <see cref="ConvertToStream(string, Stream, HtmlConverterOptions?)"/>).</summary>
    /// <remarks>
    /// Like <see cref="ConvertAsync(string, HtmlConverterOptions?, CancellationToken)"/>, the conversion runs on a thread
    /// of its own and applies the options' <see cref="HtmlConverterOptions.Limits"/>. The stream is written only
    /// asynchronously, a page at a time, so it can be an ASP.NET Core response body (which forbids synchronous writes);
    /// while it is slow, the conversion waits. If writing fails, the conversion stops and the write's exception is thrown.
    /// </remarks>
    /// <exception cref="ResourceLimitExceededException">The document exceeds one of the options' <see cref="HtmlConverterOptions.Limits"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task ConvertToStreamAsync(string html, Stream output, HtmlConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        return ConvertToStreamAsync(html, output, options ?? new HtmlConverterOptions(), HtmlMetadata.DefaultCreator, cancellationToken);
    }

    /// <summary>
    /// <see cref="ConvertToStreamAsync(string, Stream, HtmlConverterOptions?, CancellationToken)"/>, naming
    /// <paramref name="defaultCreator"/> as the creator unless the options or the document name one.
    /// </summary>
    internal static async Task ConvertToStreamAsync(string html, Stream output, HtmlConverterOptions options, string defaultCreator, CancellationToken cancellationToken)
    {
        using var pipe = new OutputPipe(bounded: !RunsOnCallerThread);
        // The conversion completes the pipe when it ends. One canceled before it starts never does, but then the same
        // token stops the copy.
        var conversion = ConvertToStream(html, pipe, options, defaultCreator, cancellationToken);
        try
        {
            await pipe.DrainToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Writing failed (or was canceled): stop the conversion at its next write, let it end, and report the write's error.
            pipe.Abort();
            await ((Task)conversion).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        await conversion.ConfigureAwait(false);
    }

    /// <summary>Converts on a conversion thread and writes the PDF to <paramref name="output"/> from there, page by page.</summary>
    private static Task<bool> ConvertToStream(string html, Stream output, HtmlConverterOptions options, string defaultCreator, CancellationToken cancellationToken) =>
        RunLimited(
            options,
            () =>
            {
                var succeeded = false;
                try
                {
                    var (flow, metadata, diagnostics) = Layout(html, options, cancellationToken);

                    // Everything ConvertAsync sets, but before writing begins: page by page, it is read by BeginSave.
                    var document = new PdfDocument { Tagged = options.Tagged, PdfUA = options.PdfUA, StrictTagging = options.StrictTagging, TimeProvider = options.TimeProvider };
                    metadata.ApplyTo(document, options, defaultCreator);
                    document.Encryption = options.Encryption;
                    document.PdfAConformance = options.PdfAConformance;
                    if (options.PdfAConformance != PdfAConformance.None)
                    {
                        document.EmbedStandardFonts();
                    }

                    document.BeginSave(output);
                    flow.RenderTo(document);
                    document.EndSave();
                    diagnostics.Deliver();
                    succeeded = true;
                    return true;
                }
                finally
                {
                    (output as OutputPipe)?.Complete(succeeded);
                }
            },
            cancellationToken);

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
                var (flow, metadata, diagnostics) = Layout(html, options, cancellationToken);
                var document = new PdfDocument { Tagged = options.Tagged, PdfUA = options.PdfUA, StrictTagging = options.StrictTagging, TimeProvider = options.TimeProvider };
                flow.RenderTo(document);
                diagnostics.Deliver();
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
    public static FlowDocument CreateFlowDocument(string html, HtmlConverterOptions? options = null) =>
        CreateFlowDocumentAsync(html, options, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// <see cref="CreateFlowDocument"/> on a conversion thread, observing <paramref name="cancellationToken"/> while
    /// the layout is built.
    /// </summary>
    internal static Task<FlowDocument> CreateFlowDocumentAsync(string html, HtmlConverterOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlConverterOptions();
        return RunLimited(
            options,
            () =>
            {
                var (flow, _, diagnostics) = Layout(html, options, cancellationToken);
                diagnostics.DeliverAndGoLive(); // Layout issues come when the document is rendered, later.
                return flow;
            },
            cancellationToken);
    }

    /// <summary>Whether conversions run on the caller's thread: where there are no threads to start.</summary>
    private static bool RunsOnCallerThread => OperatingSystem.IsBrowser() || OperatingSystem.IsWasi();

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

        if (RunsOnCallerThread)
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

    private static (FlowDocument Flow, HtmlMetadata Metadata, DiagnosticCollector Diagnostics) Layout(string html, HtmlConverterOptions options, CancellationToken cancellationToken)
    {
        var fonts = options.Fonts ?? BundledFonts.CreateCollection();
        var diagnostics = new DiagnosticCollector(options.Diagnostics);

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
        using var document = Parse(context, CssColorGuard.Rewrite(html), options.BaseUri, cancellationToken); // AngleSharp parses CSS here.
        CheckStructure(document, options.Limits);

        var originalInlineStyles = new Dictionary<IElement, string>(ReferenceEqualityComparer.Instance);
        var styleSheets = PrepareStyles(document, options, diagnostics, originalInlineStyles);
        SanitizeStyles(document);
        var cssTexts = styleSheets.Select(s => s.Css).ToList();

        // The CSS as written, not as rewritten for AngleSharp (which drops what is not drawn, such as shadows).
        CssDiagnostics.Check(document, styleSheets.Where(s => s.Original is not null).Select(s => s.Original!), originalInlineStyles, diagnostics);
        var pageRules = PageRules.Load(options.Page, cssTexts);
        var setup = pageRules.Setup;

        // @page can change the page size and margins; styles are computed lazily, so vw/vh still see the final area.
        device.ViewPortWidth = (int)(setup.ContentWidth / CssValues.PointsPerPixel);
        device.ViewPortHeight = (int)(setup.ContentHeight / CssValues.PointsPerPixel);

        var paged = Paged.PagedMedia.Load(document, cssTexts);
        var webFonts = WebFonts.Load(styleSheets, options.ResourceLoader, fonts, diagnostics);
        ResourceLimits.Checkpoint();

        // Named pages and @page :first/:left/:right/:blank can give pages their own size and margins. Percentages in a
        // named page resolve against its content width (that of a right page that is not the first).
        var builder = new BoxBuilder(document, options, fonts, options.BaseUri, paged, webFonts)
        {
            Diagnostics = diagnostics,
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

        if (diagnostics.IsEnabled)
        {
            flow.Diagnostics = d => ReportLayout(diagnostics, d, builder.ElementOf(d.Block));
        }

        return (flow, HtmlMetadata.Read(document), diagnostics);
    }

    /// <summary>Reports content that did not fit where it was laid out, at the element its block was made for.</summary>
    private static void ReportLayout(DiagnosticCollector diagnostics, LayoutDiagnostic diagnostic, IElement? element)
    {
        var (kind, subject) = diagnostic.Kind switch
        {
            LayoutDiagnosticKind.Overflow when diagnostic.Block is ParagraphBlock => (HtmlDiagnosticKind.Overflow, "line wider than its box"),
            LayoutDiagnosticKind.Overflow when diagnostic.Block is TableBlock => (HtmlDiagnosticKind.Overflow, "table wider than its container"),
            LayoutDiagnosticKind.Overflow when diagnostic.Block is ImageBlock => (HtmlDiagnosticKind.Overflow, "image wider than its container"),
            LayoutDiagnosticKind.Overflow => (HtmlDiagnosticKind.Overflow, "box wider than its container"),
            LayoutDiagnosticKind.Clipped => (HtmlDiagnosticKind.Clipped, "content taller than a page"),
            _ => (HtmlDiagnosticKind.ScaledToFit, "image taller than a page"),
        };
        diagnostics.Report(kind, HtmlDiagnosticSeverity.Warning, subject, $"{diagnostic.Message} (page {diagnostic.Page + 1})", element);
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
    private static List<StyleSheetText> PrepareStyles(IDocument document, HtmlConverterOptions options, DiagnosticCollector diagnostics, Dictionary<IElement, string> originalInlineStyles)
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
                var uri = href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null : ResolveUri(options.BaseUri, href);
                sheetUri = uri ?? sheetUri;
                var data = ResourceLoaders.Fetch(options.ResourceLoader, href, uri, ResourceKind.StyleSheet, out var failure);
                css = data is null ? null : Encoding.UTF8.GetString(data);
                if (failure is { } failed)
                {
                    diagnostics.Report(failed.Kind, HtmlDiagnosticSeverity.Warning, ResourceLoaders.Subject(href), failed.Message, link);
                }
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
            var text = style.TextContent;
            if (RewriteCss(text) is var rewritten && rewritten != text)
            {
                style.TextContent = rewritten;
            }

            var media = style.GetAttribute("media");
            if (media is null or "" || media.Contains("print", StringComparison.OrdinalIgnoreCase) || media.Contains("all", StringComparison.OrdinalIgnoreCase))
            {
                cssTexts.Add(new(style.TextContent, linkedBases.GetValueOrDefault(style, options.BaseUri)) { Original = text });
            }
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var inline = element.GetAttribute("style")!;
            if (RewriteCss(inline) is var rewritten && rewritten != inline)
            {
                originalInlineStyles[element] = inline;
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
    /// Removes what AngleSharp throws <see cref="NullReferenceException"/> on while it computes styles (fuzzing found
    /// several kinds of malformed CSS): style rules whose selector it could not read but which have nested rules
    /// (<c>:{&amp;{}}</c>), and declarations whose value it cannot write back as text (<c>width: clamp(1,(,0)</c>).
    /// Neither can apply to an element anyway.
    /// </summary>
    private static void SanitizeStyles(IDocument document)
    {
        foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            Sanitize(sheet.Rules, sheet.RemoveAt);
        }

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            SanitizeDeclarations(element.GetStyle());
        }

        static void Sanitize(ICssRuleList rules, Action<int> removeAt)
        {
            for (var i = rules.Length - 1; i >= 0; i--)
            {
                switch (rules[i])
                {
                    case ICssStyleRule { SelectorText: null }:
                        removeAt(i);
                        break;
                    case ICssStyleRule style:
                        SanitizeDeclarations(style.Style);
                        break;
                    case ICssGroupingRule group:
                        Sanitize(group.Rules, group.RemoveAt);
                        break;
                }
            }
        }

        // Only rewritten when one is broken: removing it would read it too, so the others are written back as text.
        static void SanitizeDeclarations(ICssStyleDeclaration declarations)
        {
            var kept = new List<string>();
            var broken = false;
            foreach (var property in declarations)
            {
                try
                {
                    kept.Add($"{property.Name}: {property.Value}{(property.IsImportant ? " !important" : string.Empty)}");
                }
                catch (NullReferenceException)
                {
                    broken = true;
                }
            }

            if (broken)
            {
                declarations.CssText = string.Join("; ", kept);
            }
        }
    }

    /// <summary>Rewrites CSS that AngleSharp would misread before it is parsed (see LegacyBreaks and LogicalSides).</summary>
    private static string RewriteCss(string css) => CssTextDecoration.RewriteShorthand(CssBackgrounds.RewriteShorthand(LogicalSides.Rewrite(LegacyBreaks.Rewrite(StripShadows(CssColorGuard.Rewrite(css))))));

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
