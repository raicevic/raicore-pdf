namespace RaiCore.Pdf;

/// <summary>Pages of other PDF files drawn as form XObjects: letterheads, stamps, several pages on one sheet.</summary>
public sealed partial class PdfCanvas
{
    /// <summary>
    /// Draws a page of another PDF file at its own size with its bottom-left corner at (<paramref name="x"/>,
    /// <paramref name="y"/>), e.g. a letterhead under the content of a generated page:
    /// <c>page.Canvas.DrawPage(letterhead.Pages[0], 0, 0)</c> before drawing the rest.
    /// </summary>
    /// <remarks>See <see cref="DrawPage(PdfReaderPage, double, double, double, double)"/>.</remarks>
    public PdfCanvas DrawPage(PdfReaderPage page, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(page);
        return DrawPage(page, x, y, page.Width, page.Height);
    }

    /// <summary>
    /// Draws a page of another PDF file scaled to fill the rectangle with its bottom-left corner at
    /// (<paramref name="x"/>, <paramref name="y"/>). The page is drawn as it is displayed: its crop box, turned by its
    /// <see cref="PdfReaderPage.Rotate"/>, so that <see cref="PdfReaderPage.Width"/> by
    /// <see cref="PdfReaderPage.Height"/> fills the rectangle.
    /// </summary>
    /// <remarks>
    /// The page becomes a form XObject (its content streams joined, with its resources), written once per document
    /// however often it is drawn; its fonts and images are shared with pages imported from the same
    /// <see cref="PdfReader"/>. Only the page's content is drawn: its annotations (links, form fields, comments) are
    /// not. Like images, the page is drawn opaque whatever the alpha of the fill color.
    /// </remarks>
    public PdfCanvas DrawPage(PdfReaderPage page, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            throw new ArgumentException("The rectangle must have finite coordinates.");
        }

        var name = UseResource(_page.UsePageForm(page));
        return Op($"q {OpaqueImagePrefix()}{N(width / page.Width)} 0 0 {N(height / page.Height)} {N(x)} {N(y)} cm /{name} Do Q");
    }
}
