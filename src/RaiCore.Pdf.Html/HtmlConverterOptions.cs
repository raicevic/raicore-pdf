using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Html;

/// <summary>Options for <see cref="HtmlToPdf"/>.</summary>
/// <remarks>
/// Options are immutable once created and can be shared by conversions running at the same time. A shared
/// <see cref="Fonts"/> collection is the way to share font data between them; a <see cref="ResourceLoader"/> passed
/// here is then called from several threads at once and must be thread-safe.
/// </remarks>
public sealed class HtmlConverterOptions
{
    /// <summary>
    /// Fonts used for CSS <c>font-family</c>. Defaults to the bundled Liberation fonts only, so output is identical on
    /// every machine; add <see cref="FontCollection.AddSystemFonts"/> or your own font files for more families and scripts.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// Fonts appended to every <c>font-family</c> list, tried for characters the chosen fonts lack (e.g. a CJK or emoji font).
    /// </summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>The page size and margins when the CSS has no <c>@page</c> rule. Defaults to A4 with 2 cm margins.</summary>
    public PageSetup Page { get; init; } = new(PdfPageSize.A4, Thickness.All(72 / 2.54 * 2));

    /// <summary>The URI that relative URLs (images, stylesheets) resolve against, e.g. the folder containing the HTML file.</summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Loads images, stylesheets and web fonts. The default (<see cref="ResourceLoaders.Default"/>) reads
    /// <c>file:</c> URIs; network access is off unless you supply a loader that performs it. <c>data:</c> URIs never
    /// reach the loader. For untrusted HTML use <see cref="ResourceLoaders.None"/>, which loads nothing (so only
    /// <c>data:</c> URIs work), or a loader that allows only what the document should reach.
    /// </summary>
    public ResourceLoader ResourceLoader { get; init; } = ResourceLoaders.Default;

    /// <summary>
    /// Hyphenation patterns for <c>hyphens: auto</c> by language, used in place of the bundled ones (see
    /// <see cref="Hyphenator.BundledLanguages"/>) or for languages without them. The keys are BCP 47 tags matched like
    /// <c>lang</c>: an entry for <c>de</c> is used for <c>de-CH</c> too (see
    /// <see cref="Hyphenator.ForLanguage(string?, IReadOnlyDictionary{string, Hyphenator}?)"/>). Defaults to <c>null</c>:
    /// the bundled patterns only.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new HtmlConverterOptions
    /// {
    ///     Hyphenators = new Dictionary&lt;string, Hyphenator&gt; { ["sv"] = Hyphenator.FromPatterns(File.ReadLines("hyph-sv.pat.txt")) },
    /// };
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, Hyphenator>? Hyphenators { get; init; }

    /// <summary>Extra CSS applied after the document's own styles (as if it were the last stylesheet).</summary>
    public string? UserStyleSheet { get; init; }

    /// <summary>
    /// Whether to create PDF bookmarks (the document outline): from <c>h1</c>-<c>h6</c> by default, customized with the
    /// CSS properties <c>bookmark-level</c>, <c>bookmark-label</c> and <c>bookmark-state</c>. Defaults to <c>true</c>.
    /// </summary>
    public bool CreateBookmarks { get; init; } = true;

    /// <summary>The PDF's title. Overrides the document's <c>&lt;title&gt;</c>.</summary>
    public string? Title { get; init; }

    /// <summary>The PDF's author. Overrides the document's <c>&lt;meta name="author"&gt;</c>.</summary>
    public string? Author { get; init; }

    /// <summary>The PDF's subject. Overrides the document's <c>&lt;meta name="description"&gt;</c>.</summary>
    public string? Subject { get; init; }

    /// <summary>The PDF's keywords, separated by commas. Override the document's <c>&lt;meta name="keywords"&gt;</c>.</summary>
    public string? Keywords { get; init; }

    /// <summary>
    /// The application that created the content. Overrides the document's <c>&lt;meta name="generator"&gt;</c>;
    /// defaults to the converting package (<c>RaiCore.Pdf.Html</c> or <c>RaiCore.Pdf.Razor</c>).
    /// </summary>
    public string? Creator { get; init; }

    /// <summary>
    /// The document language as a BCP 47 tag, such as <c>en-US</c>, written as the PDF's <c>/Lang</c> and XMP
    /// <c>dc:language</c>. Overrides the document's <c>&lt;html lang&gt;</c> (or <c>xml:lang</c>).
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Password protection and permissions for the PDF (see <see cref="PdfEncryption"/>). <c>null</c> (the default)
    /// writes an unencrypted document. Sets <see cref="PdfDocument.Encryption"/> on the converted document.
    /// </summary>
    public PdfEncryption? Encryption { get; init; }

    /// <summary>
    /// Whether to write a tagged PDF (<see cref="PdfDocument.Tagged"/>): the document's logical structure from the HTML
    /// semantics (headings, paragraphs, lists, tables with header cells, figures with their <c>alt</c> text, links), so
    /// that screen readers and reflowing viewers can use it, with decorations, page headers and footers and repeated
    /// table headers marked as artifacts.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>
    /// Whether to write PDF/UA-1 (<see cref="PdfDocument.PdfUA"/>), the accessible PDF standard: a tagged PDF that also
    /// needs a title (<c>&lt;title&gt;</c> or <see cref="Title"/>) and a language (<c>&lt;html lang&gt;</c> or
    /// <see cref="Language"/>), and whose images need <c>alt</c> text.
    /// </summary>
    public bool PdfUA { get; init; }

    /// <summary>
    /// Whether the converter checks its own tagging (<see cref="PdfDocument.StrictTagging"/>): anything it paints outside a
    /// structure element or an artifact throws. For the engine's tests; HTML gives users nothing to fix there.
    /// </summary>
    internal bool StrictTagging { get; init; }

    /// <summary>
    /// Writes the PDF as PDF/A-2b, PDF/A-3b, PDF/A-2a or PDF/A-3a for archiving (see
    /// <see cref="PdfDocument.PdfAConformance"/>). The bundled Liberation fonts are then embedded in place of the
    /// standard fonts (used for SVG text), and converting throws <see cref="InvalidOperationException"/> when the
    /// document breaks a rule, such as text in characters no font has, a CMYK image, or <see cref="Encryption"/>. Level A
    /// (<see cref="PdfAConformance.PdfA2A"/>, <see cref="PdfAConformance.PdfA3A"/>) also tags the document, as
    /// <see cref="Tagged"/> does, and needs a language (<c>&lt;html lang&gt;</c> or <see cref="Language"/>) and
    /// <c>alt</c> text on images (<c>alt=""</c> for decorative ones). Defaults to <see cref="PdfAConformance.None"/>.
    /// </summary>
    public PdfAConformance PdfAConformance { get; init; }

    /// <summary>
    /// Limits on what the document may make the conversion do (nesting depth, element and table cell counts, image
    /// pixels, resource and font sizes, pages and time), for converting untrusted HTML safely. Defaults to
    /// <see cref="ResourceLimits.Default"/>. Exceeding a structural limit (depth, elements, table cells, pages, time)
    /// throws <see cref="ResourceLimitExceededException"/>; an image, stylesheet or web font over a size limit is
    /// skipped like one that cannot be loaded.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// The clock that dates the PDF (<see cref="PdfDocument.TimeProvider"/>): the creation and modification dates are
    /// its local time when the document is written. Set a fixed clock for reproducible output: the same HTML and
    /// options then give the same bytes. Defaults to <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Receives what the converter could not render as a browser would: resources that did not load (images, style
    /// sheets, fonts) and why, and CSS it does not support (properties, values, selectors, at-rules; what has no effect
    /// in print anyway is reported as <see cref="HtmlDiagnosticSeverity.Info"/>). Each distinct issue is reported once per conversion, with the path of the first element
    /// it concerns and how many elements it concerns, in the order the issues first occurred, on the conversion's own
    /// thread just before the conversion completes. <c>null</c> (the default) collects nothing.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new HtmlConverterOptions { Diagnostics = d => logger.LogWarning("{Subject}: {Message} ({Path})", d.Subject, d.Message, d.ElementPath) };
    /// </code>
    /// </example>
    public Action<HtmlDiagnostic>? Diagnostics { get; init; }
}
