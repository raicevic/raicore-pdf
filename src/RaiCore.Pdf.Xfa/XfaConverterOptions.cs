using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Xfa;

/// <summary>Options for <see cref="XfaToPdf"/>.</summary>
/// <remarks>Options are immutable once created and can be shared by conversions running at the same time.</remarks>
public sealed class XfaConverterOptions
{
    /// <summary>
    /// The fonts the form's typefaces are found in. Defaults to the bundled Liberation fonts only, so output is identical
    /// on every machine: Liberation Sans, Serif and Mono have the widths of Arial, Times New Roman and Courier New. Fonts
    /// embedded in the PDF file for the form (<see cref="UseEmbeddedFonts"/>) come first. A typeface neither has is
    /// replaced by a font of its generic family (serif, monospace, or sans-serif for the rest, such as Myriad Pro), then
    /// by <see cref="FallbackFontFamilies"/>.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// Font families tried, in order, for characters the form's fonts lack (e.g. a CJK or Arabic font). The bundled
    /// Liberation Sans comes last. Defaults to none.
    /// </summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>
    /// Whether the TrueType and OpenType fonts the PDF file embeds for its form (in the AcroForm's default resources) are
    /// used for the typefaces they are. Defaults to <c>true</c>.
    /// </summary>
    public bool UseEmbeddedFonts { get; init; } = true;

    /// <summary>
    /// XML data merged into the form instead of the data the form carries, or <c>null</c> (the default) for the form's own
    /// data (its <c>datasets</c> packet). Either an <c>xfa:datasets</c> element, an <c>xfa:data</c> element, or the data's
    /// root element itself (for example the XML an XFA form exports). DTDs are not allowed.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>The password of an encrypted PDF file, or <c>null</c> (the default) to try the empty user password.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// The document's title (its metadata), or <c>null</c> (the default) for the title of the PDF file's document
    /// information, if it has one.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The limits that bound a conversion: <see cref="ResourceLimits.MaxDecompressedBytes"/> (the XFA packets together,
    /// and each PDF stream), <see cref="ResourceLimits.MaxElements"/> (the XML elements of the packets together, and the
    /// objects of the merged form, so that repeated subforms cannot multiply without bound),
    /// <see cref="ResourceLimits.MaxNestingDepth"/> (the nesting of the XML, and so of subforms),
    /// <see cref="ResourceLimits.MaxPages"/> (the pages laid out), <see cref="ResourceLimits.MaxTableCells"/> (the cells of
    /// each table), <see cref="ResourceLimits.MaxImagePixels"/> (a larger image is left out and reported),
    /// <see cref="ResourceLimits.MaxFontBytes"/> (an embedded font) and <see cref="ResourceLimits.Timeout"/>. Defaults to
    /// <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// Receives what the converter did not render as a viewer with XFA support would: scripts that were not run,
    /// unsupported content, images that cannot be drawn, content that does not fit, data no field takes and substituted
    /// typefaces. Each distinct issue is reported once, on the conversion's thread, while the conversion runs.
    /// <c>null</c> (the default) collects nothing.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new XfaConverterOptions { Diagnostics = d => logger.LogWarning("{Kind}: {Message}", d.Kind, d.Message) };
    /// </code>
    /// </example>
    public Action<XfaDiagnostic>? Diagnostics { get; init; }
}
