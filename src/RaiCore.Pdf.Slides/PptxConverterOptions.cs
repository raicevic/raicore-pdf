using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Slides;

/// <summary>Options for <see cref="PptxToPdf"/> and <see cref="PptToPdf"/> (PowerPoint 97-2003 presentations).</summary>
/// <remarks>
/// Options are immutable once created and can be shared by conversions running at the same time.
/// </remarks>
public sealed class PptxConverterOptions
{
    /// <summary>
    /// The fonts the presentation's fonts are found in. Defaults to the bundled Liberation fonts only, so output is
    /// identical on every machine; add <see cref="FontCollection.AddSystemFonts"/> or your own font files for the
    /// presentation's own fonts (Calibri, Aptos, ...) and for more scripts. A font the collection lacks is replaced by
    /// a metric-compatible one where there is one (Carlito for Calibri, Caladea for Cambria, Liberation Sans for Arial),
    /// else by the generic family it belongs to (serif, monospace or sans-serif), then by
    /// <see cref="FallbackFontFamilies"/>.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// Font families tried, in order, for characters the presentation's fonts lack (e.g. a CJK or Arabic font). The
    /// bundled Liberation Sans comes last. Defaults to none.
    /// </summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>
    /// Whether hidden slides are converted too. Defaults to <c>false</c>: hidden slides are left out, as in a slide show.
    /// </summary>
    public bool IncludeHiddenSlides { get; init; }

    /// <summary>
    /// The numbers of the slides to convert, from 1 in the presentation's order (hidden slides counted), or <c>null</c>
    /// (the default) for all of them. Slides are converted in the presentation's order; named hidden slides are
    /// converted too. Numbers the presentation does not have are ignored.
    /// </summary>
    public IReadOnlyList<int>? SlideNumbers { get; init; }

    /// <summary>
    /// The document's title (its metadata), or <c>null</c> (the default) for the title of the presentation's document
    /// properties, if it has one.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Whether the PDF is tagged (accessible): each slide's text in the order of its shapes (titles as headings,
    /// paragraphs, tables with their cells, pictures as figures with their alternative text); lines and fills without
    /// text, and the master's and layout's shapes, are artifacts. Defaults to <c>false</c>. For PDF/UA, also set <see cref="PdfDocument.PdfUA"/>, a title and
    /// <see cref="PdfDocument.Language"/> on the document.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>
    /// The limits that bound a conversion: <see cref="ResourceLimits.MaxDecompressedBytes"/> (all the package's parts
    /// together), <see cref="ResourceLimits.MaxElements"/> (the XML elements of all parts together),
    /// <see cref="ResourceLimits.MaxNestingDepth"/> (the nesting of their XML, and so of groups),
    /// <see cref="ResourceLimits.MaxPages"/> (the slides converted), <see cref="ResourceLimits.MaxTableCells"/> (the
    /// cells of each table), <see cref="ResourceLimits.MaxImagePixels"/> (a larger picture is left out and reported) and
    /// <see cref="ResourceLimits.Timeout"/>. Defaults to <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// Receives what the converter did not draw as a presentation program would: charts it cannot draw, SmartArt
    /// without a stored drawing, pictures in formats it cannot read (such as EMF and WMF), preset shapes drawn as
    /// rectangles, and so on. Each distinct issue is reported once per slide, on the conversion's thread, while the conversion runs.
    /// <c>null</c> (the default) collects nothing.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new PptxConverterOptions { Diagnostics = d => logger.LogWarning("Slide {Slide}: {Subject}: {Message}", d.SlideNumber, d.Subject, d.Message) };
    /// </code>
    /// </example>
    public Action<SlideDiagnostic>? Diagnostics { get; init; }
}
