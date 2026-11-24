namespace RaiCore.Pdf;

/// <summary>
/// A note on a page (a text annotation, a "sticky note"): an icon over a rectangle of the page that viewers open to
/// show the note's text. Added with <see cref="PdfPage.AddNote"/> or <see cref="PdfCanvas.AddNote"/>.
/// </summary>
/// <remarks>
/// Notes are printed and have an appearance of their own (a small yellow note), as PDF/A requires; in a tagged
/// document each is an <c>Annot</c> structure element, its text the annotation's description (PDF/UA).
/// </remarks>
public sealed class PdfNote
{
    internal PdfNote(double x, double y, double width, double height, string text)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Text = text;
    }

    /// <summary>The left edge of the note's icon, in PDF points from the left of the page.</summary>
    public double X { get; }

    /// <summary>The bottom edge of the note's icon, in PDF points from the bottom of the page.</summary>
    public double Y { get; }

    /// <summary>The width of the note's icon.</summary>
    public double Width { get; }

    /// <summary>The height of the note's icon.</summary>
    public double Height { get; }

    /// <summary>The note's text.</summary>
    public string Text { get; }

    /// <summary>Who wrote the note (shown as its title by viewers), or <c>null</c>.</summary>
    public string? Author { get; set; }

    /// <summary>The structure element the note belongs to, in a tagged document.</summary>
    internal PdfStructureElement? StructureElement { get; set; }
}
