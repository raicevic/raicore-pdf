using RaiCore.Pdf.Extraction.Fonts;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Extraction.Content;

/// <summary>
/// A glyph shown on a page. Positions are in display space: points from the top-left corner of the page as it is
/// displayed (crop box, after <c>/Rotate</c>), with y growing downwards.
/// </summary>
/// <param name="Text">The characters of the glyph (several for a ligature); empty when nothing maps the code to text.</param>
/// <param name="IsGuess">Whether <see cref="Text"/> is a guess (the code taken as Latin-1) rather than a mapping from the file.</param>
/// <param name="Code">The character code, as read from the string.</param>
/// <param name="CodeLength">The code's length in bytes.</param>
/// <param name="Origin">The glyph's origin on the baseline (where the pen was).</param>
/// <param name="End">Where the pen is after the glyph, without character or word spacing.</param>
/// <param name="Bounds">The glyph's box (advance width by the font's ascent and descent), axis-aligned.</param>
/// <param name="FontSize">The size the glyph appears at: its em height in points on the page.</param>
/// <param name="Font">The font.</param>
/// <param name="RenderMode">The text rendering mode (<c>Tr</c>): 3 is invisible (as in OCR text layers), 7 is a clip only.</param>
/// <param name="IsWordSpace">Whether the code is the single-byte space to which word spacing applies.</param>
/// <param name="Direction">
/// The direction the text runs in, as a unit vector in display space: (1, 0) for ordinary text, (0, 1) for vertical
/// writing (top to bottom), (0, -1) for text turned to read upwards.
/// </param>
/// <param name="IsClipped">
/// Whether the glyph is hidden by clipping: its center is outside the clip (the clipping paths' and forms' bounding
/// boxes; a clipping path that is not a rectangle is taken as its bounding box, so clipped text may be missed but
/// visible text is not marked).
/// </param>
internal readonly record struct TextGlyph(
    string Text,
    bool IsGuess,
    uint Code,
    int CodeLength,
    PdfPoint Origin,
    PdfPoint End,
    Box Bounds,
    double FontSize,
    FontDecoder Font,
    int RenderMode,
    bool IsWordSpace,
    PdfPoint Direction,
    bool IsClipped);

/// <summary>An image drawn on a page.</summary>
/// <param name="Transform">Maps the image's unit square (its pixels span 0 to 1 in x and y, row 0 at the top at y = 1) to display space.</param>
/// <param name="Bounds">The image's box in display space.</param>
/// <param name="XObject">The image XObject, or <c>null</c> for an inline image.</param>
/// <param name="Reference">The reference to the image XObject, when it is an indirect object (images drawn several times share it).</param>
/// <param name="ResourceName">The image's name in the resources (<c>/Im1</c> without the slash), or <c>null</c> for an inline image.</param>
/// <param name="Inline">The inline image, or <c>null</c> for an XObject.</param>
/// <param name="IsClipped">Whether the image is hidden by clipping: it does not touch the clip at all (see <see cref="TextGlyph.IsClipped"/>).</param>
/// <param name="Resources">The resources the image was drawn with: an inline image's named color space is looked up in them.</param>
internal readonly record struct ImagePlacement(
    Matrix Transform,
    Box Bounds,
    PdfStreamObject? XObject,
    PdfReference? Reference,
    string? ResourceName,
    InlineImage? Inline,
    bool IsClipped,
    PdfDictionary Resources);

/// <summary>Receives what a content stream draws, in order. The default implementations ignore everything.</summary>
internal abstract class ContentSink
{
    /// <summary>A text object begins (<c>BT</c>).</summary>
    public virtual void BeginText()
    {
    }

    /// <summary>A text object ends (<c>ET</c>, or the end of the content when it is missing).</summary>
    public virtual void EndText()
    {
    }

    public virtual void Glyph(in TextGlyph glyph)
    {
    }

    public virtual void Image(in ImagePlacement image)
    {
    }

    /// <summary>A marked-content sequence begins (<c>BMC</c> or <c>BDC</c>); its properties hold <c>/MCID</c>, <c>/ActualText</c>, <c>/Lang</c> and so on.</summary>
    public virtual void BeginMarkedContent(string tag, PdfDictionary? properties)
    {
    }

    /// <summary>The innermost marked-content sequence ends (<c>EMC</c>, or the end of its content stream).</summary>
    public virtual void EndMarkedContent()
    {
    }
}
