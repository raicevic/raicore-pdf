namespace RaiCore.Pdf.Extraction;

/// <summary>A place where <see cref="TextExtractor.Find(string, bool)"/> found the text it searched for.</summary>
/// <remarks>
/// Rectangles are in PDF points in the page's user space, as <see cref="PdfRectangle"/> always is: from the
/// bottom-left corner of the page before its rotation (<c>/Rotate</c>), so they can be drawn on the page or turned into
/// annotations as they are. Each covers the matched characters' glyphs (their advance width by the font's ascent and
/// descent); on a rotated page, or for text that is turned, it is the axis-aligned rectangle around them.
/// </remarks>
public sealed class TextMatch
{
    internal TextMatch(int pageIndex, string text, IReadOnlyList<PdfRectangle> rectangles)
    {
        PageIndex = pageIndex;
        Text = text;
        Rectangles = rectangles;
        var (left, bottom) = (rectangles.Min(r => r.X), rectangles.Min(r => r.Y));
        Bounds = new PdfRectangle(left, bottom, rectangles.Max(r => r.Right) - left, rectangles.Max(r => r.Top) - bottom);
    }

    /// <summary>The page's position in the file, from 0.</summary>
    public int PageIndex { get; }

    /// <summary>The text matched, as it is in the page's text: a match that wraps has a line break (<c>\n</c>) where the line ends.</summary>
    public string Text { get; }

    /// <summary>The rectangles of the match on the page: one per line it is on (in reading order), never empty.</summary>
    public IReadOnlyList<PdfRectangle> Rectangles { get; }

    /// <summary>The rectangle around all of <see cref="Rectangles"/>.</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>The text matched, with its page and bounds.</summary>
    public override string ToString() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"{Text}\" on page {PageIndex + 1} at {Bounds}");
}
