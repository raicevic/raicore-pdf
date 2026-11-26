using System.Globalization;
using System.Text;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html.Boxes;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Html.Diagnostics;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Html;

/// <summary>HTML fragments (see <see cref="HtmlFragment"/>): HTML converted to blocks for a document built elsewhere.</summary>
public static partial class HtmlToPdf
{
    /// <summary>
    /// Builds the blocks of an HTML fragment on a conversion thread, with the options' limits enforced, and returns
    /// them with what was reported.
    /// </summary>
    internal static Task<(List<Block> Blocks, List<HtmlDiagnostic> Diagnostics)> BuildFragmentAsync(string html, HtmlFragmentOptions fragment, CancellationToken cancellationToken)
    {
        // The converter's options for what fragments share with documents.
        var options = new HtmlConverterOptions
        {
            Fonts = fragment.Fonts,
            FallbackFontFamilies = fragment.FallbackFontFamilies ?? [],
            BaseUri = fragment.BaseUri,
            ResourceLoader = fragment.ResourceLoader ?? throw new ArgumentException("The resource loader must not be null.", nameof(fragment)),
            Hyphenators = fragment.Hyphenators,
            CreateBookmarks = fragment.CreateBookmarks,
            Limits = fragment.Limits ?? throw new ArgumentException("The resource limits must not be null.", nameof(fragment)),
        };
        return RunLimited(options, () => BuildFragment(html, fragment, options, cancellationToken), cancellationToken);
    }

    private static (List<Block> Blocks, List<HtmlDiagnostic> Diagnostics) BuildFragment(string html, HtmlFragmentOptions fragment, HtmlConverterOptions options, CancellationToken cancellationToken)
    {
        var fonts = options.Fonts ?? BundledFonts.CreateCollection();
        var reported = new List<HtmlDiagnostic>();
        var diagnostics = new DiagnosticCollector(reported.Add);

        // A print device whose viewport is the width the fragment is placed in (vw), and as tall as the default page's
        // content area (vh): a fragment has no page of its own.
        var device = PrintDevice.For(options.Page, fragment.Width, options.Page.ContentHeight);
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        using var document = Parse(context, CssColorGuard.Rewrite(html), options.BaseUri, cancellationToken);
        CheckStructure(document, options.Limits);

        // The base style's language is the root's, unless the fragment names one for its root itself.
        if (fragment.BaseStyle?.Language is { Length: > 0 } language && document.DocumentElement is { } root
            && !root.HasAttribute("lang") && !root.HasAttribute("xml:lang"))
        {
            root.SetAttribute("lang", language);
        }

        var originalInlineStyles = new Dictionary<IElement, string>(ReferenceEqualityComparer.Instance);
        var styleSheets = PrepareStyles(document, options, diagnostics, originalInlineStyles, FragmentRootCss(fragment.BaseStyle), fragment.Css);
        SanitizeStyles(document);
        var cssTexts = styleSheets.Select(s => s.Css).ToList();
        CssDiagnostics.Check(document, styleSheets.Where(s => s.Original is not null).Select(s => s.Original!), originalInlineStyles, diagnostics);

        var paged = Paged.PagedMedia.Load(document, cssTexts);
        paged.RestrictToFragment(diagnostics);
        var webFonts = WebFonts.Load(styleSheets, options.ResourceLoader, fonts, diagnostics);
        ResourceLimits.Checkpoint();

        var builder = new BoxBuilder(document, options, fonts, options.BaseUri, paged, webFonts)
        {
            Diagnostics = diagnostics,
            BaseFonts = fragment.BaseStyle?.Fonts ?? [],
        };
        var blocks = builder.BuildFragment(fragment.Width);
        if (builder.UsesPageReferences)
        {
            diagnostics.Report(
                HtmlDiagnosticKind.UnsupportedValue,
                HtmlDiagnosticSeverity.Warning,
                "target-counter(..., page)",
                "Page numbers are not known while an HTML fragment is converted, before the document it is placed in is laid out: they show 0.");
        }

        diagnostics.Deliver();
        return (blocks, reported);
    }

    /// <summary>
    /// User-agent CSS for a fragment: the body has no margin (the fragment goes in a box of its own), and the root has
    /// the base text style, which the fragment's elements inherit. It has no specificity, so the fragment's CSS overrides it.
    /// </summary>
    internal static string FragmentRootCss(TextStyle? style)
    {
        var css = new StringBuilder(":where(body) { margin: 0; }");
        if (style is null)
        {
            return css.ToString();
        }

        static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        static string Channel(double value) => Number(Math.Round(Math.Clamp(value, 0, 1) * 255));

        css.Append(" :where(html) { font-family: ");
        css.AppendJoin(", ", style.Fonts.Select(font => font.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).Select(QuoteFamily));
        var first = style.Fonts[0];
        css.Append("; font-weight: ").Append(first.Weight.ToString(CultureInfo.InvariantCulture));
        css.Append("; font-style: ").Append(first.IsItalic ? "italic" : "normal");
        css.Append("; font-size: ").Append(Number(style.FontSize / CssValues.PointsPerPixel)).Append("px");
        var color = style.Color;
        css.Append("; color: rgba(").Append(Channel(color.Red)).Append(", ").Append(Channel(color.Green)).Append(", ")
            .Append(Channel(color.Blue)).Append(", ").Append(Number(Math.Clamp(color.Alpha, 0, 1))).Append(')');
        css.Append("; line-height: ").Append(style.LineHeight.Kind switch
        {
            LineHeightKind.Multiple => Number(style.LineHeight.Value),
            LineHeightKind.Points => Number(style.LineHeight.Value / CssValues.PointsPerPixel) + "px",
            _ => "normal",
        });
        css.Append("; letter-spacing: ").Append(Number(style.LetterSpacing / CssValues.PointsPerPixel)).Append("px");
        css.Append("; word-spacing: ").Append(Number(style.WordSpacing / CssValues.PointsPerPixel)).Append("px");
        css.Append("; hyphens: ").Append(style.Hyphens switch
        {
            Hyphens.Auto => "auto",
            Hyphens.None => "none",
            _ => "manual",
        });
        return css.Append("; }").ToString();

        // A family name as a CSS string.
        static string QuoteFamily(string family) =>
            "\"" + family.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
