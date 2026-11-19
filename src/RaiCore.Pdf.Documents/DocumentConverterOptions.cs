using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Documents;

/// <summary>
/// The options the word-processing converters of this package share: fonts, outline, tagging, diagnostics and resource
/// limits. See <see cref="DocxConverterOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Page size, margins and orientation come from the document's own sections, as do its fonts, sizes and colors.
/// </para>
/// <para>Options are immutable once created and can be shared by conversions running at the same time.</para>
/// </remarks>
public abstract class DocumentConverterOptions
{
    private protected DocumentConverterOptions()
    {
    }

    /// <summary>
    /// The fonts the document's font families are found in. Defaults to the bundled Liberation fonts only, so output is
    /// identical on every machine: Liberation Sans, Serif and Mono have the widths of Arial, Times New Roman and Courier
    /// New, so text in those fonts breaks into lines as in a word processor. Add
    /// <see cref="FontCollection.AddSystemFonts"/> or your own font files for the document's other fonts (Calibri,
    /// Cambria, or their metric-compatible substitutes Carlito and Caladea, which are used when present).
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// The font family of text whose font <see cref="Fonts"/> does not have (and that is not known to be a serif or
    /// monospace font), or a generic family: <c>sans-serif</c> (the default), <c>serif</c> or <c>monospace</c>.
    /// </summary>
    public string FontFamily { get; init; } = "sans-serif";

    /// <summary>
    /// Font families tried, in order, for characters the document's fonts lack (e.g. a CJK or Arabic font). The bundled
    /// Liberation Sans comes last. Defaults to none.
    /// </summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>
    /// Whether headings (paragraphs with an outline level, such as the built-in Heading styles) become entries of the
    /// PDF's outline (bookmarks), nested by level. Defaults to <c>true</c>.
    /// </summary>
    public bool HeadingBookmarks { get; init; } = true;

    /// <summary>
    /// The document's title (its metadata), or <c>null</c> (the default) for the title of the document's properties, if
    /// it has one.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Whether the PDF is tagged (accessible): paragraphs, headings by level, tables with their header rows, and images
    /// with their alternative text; headers and footers are artifacts. Defaults to <c>false</c>. For PDF/UA, also set
    /// <see cref="PdfDocument.PdfUA"/> and a title on the document.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>
    /// Receives what the conversion could not show as the document does (<see cref="DocumentDiagnostic"/>): unsupported
    /// content such as charts or text boxes, images in formats that cannot be drawn, and content that does not fit where
    /// it is laid out. Each distinct issue is reported once per conversion. <c>null</c> (the default) collects nothing.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new DocxConverterOptions { Diagnostics = d => logger.LogWarning("{Kind}: {Message}", d.Kind, d.Message) };
    /// </code>
    /// </example>
    public Action<DocumentDiagnostic>? Diagnostics { get; init; }

    /// <summary>
    /// The limits that bound a conversion: <see cref="ResourceLimits.MaxDecompressedBytes"/> (all the parts of a
    /// package together), <see cref="ResourceLimits.MaxElements"/> (the XML elements of all its parts together),
    /// <see cref="ResourceLimits.MaxNestingDepth"/> (XML elements and nested tables), <see cref="ResourceLimits.MaxTableCells"/>,
    /// <see cref="ResourceLimits.MaxImagePixels"/>, <see cref="ResourceLimits.MaxPages"/> and <see cref="ResourceLimits.Timeout"/>.
    /// Defaults to <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;
}

/// <summary>Options for <see cref="DocxToPdf"/>. See <see cref="DocumentConverterOptions"/> for the options all converters share.</summary>
/// <remarks>Options are immutable once created and can be shared by conversions running at the same time.</remarks>
public sealed class DocxConverterOptions : DocumentConverterOptions
{
}
