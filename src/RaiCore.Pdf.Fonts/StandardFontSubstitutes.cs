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
    /// <summary>
    /// The bundled font with the widths of <paramref name="font"/>, or <c>null</c> for Symbol and ZapfDingbats, which
    /// have no bundled counterpart. These are the same instances <see cref="BundledFonts"/> collections use, loaded once
    /// per process.
    /// </summary>
    public static PdfFont? Get(StandardFont font) => font switch
    {
        // StandardFont lists Helvetica, Times and Courier as regular, bold, italic (oblique), bold italic: the order of
        // the bundled faces (Sans, Serif, Mono).
        >= StandardFont.Helvetica and <= StandardFont.CourierBoldOblique => BundledFonts.Face((int)font),
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
}
