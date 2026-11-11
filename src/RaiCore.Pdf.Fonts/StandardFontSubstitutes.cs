namespace RaiCore.Pdf.Fonts;

/// <summary>
/// The bundled Liberation fonts as embeddable substitutes for the standard fonts, which have the same character widths:
/// Liberation Sans for Helvetica, Liberation Serif for Times and Liberation Mono for Courier. PDF/A requires every font
/// to be embedded, so text drawn in a standard font (including SVG text) needs one.
/// </summary>
/// <example>
/// <code>
/// var doc = new PdfDocument { PdfAConformance = PdfAConformance.PdfA2B }.EmbedStandardFonts();
/// doc.AddPage().Canvas.DrawText("Archived", StandardFont.Helvetica, 12, 72, 720); // embeds Liberation Sans
/// </code>
/// </example>
public static class StandardFontSubstitutes
{
    private static readonly Lazy<PdfFont>[] Faces =
    [
        .. new[] { "LiberationSans", "LiberationSerif", "LiberationMono" }.SelectMany(family =>
            new[] { "Regular", "Bold", "Italic", "BoldItalic" }.Select(style => new Lazy<PdfFont>(() => Load($"{family}-{style}")))),
    ];

    /// <summary>
    /// The bundled font with the widths of <paramref name="font"/>, or <c>null</c> for Symbol and ZapfDingbats, which
    /// have no bundled counterpart. Fonts are loaded once and shared.
    /// </summary>
    public static PdfFont? Get(StandardFont font) => font switch
    {
        // StandardFont lists each family as regular, bold, italic (oblique), bold italic: the same order as Faces.
        >= StandardFont.Helvetica and <= StandardFont.CourierBoldOblique => Faces[(int)font].Value,
        _ => null,
    };

    /// <summary>
    /// Embeds the bundled Liberation fonts in place of the standard fonts when <paramref name="document"/> is saved (sets
    /// <see cref="PdfDocument.StandardFontSubstitute"/>). Returns the document.
    /// </summary>
    public static PdfDocument EmbedStandardFonts(this PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.StandardFontSubstitute = Get;
        return document;
    }

    private static PdfFont Load(string file)
    {
        var name = $"RaiCore.Pdf.Fonts.Liberation.{file}.ttf";
        using var stream = typeof(StandardFontSubstitutes).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded font '{name}' is missing from {typeof(StandardFontSubstitutes).Assembly.GetName().Name}.");
        return PdfFont.Load(stream);
    }
}
