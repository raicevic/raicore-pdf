namespace RaiCore.Pdf.Extraction;

/// <summary>The images of one page, from <see cref="ImageExtractor"/>.</summary>
public sealed class PageImages
{
    internal PageImages(int pageIndex, IReadOnlyList<ExtractedImage> images, IReadOnlyList<string> diagnostics)
    {
        PageIndex = pageIndex;
        Images = images;
        Diagnostics = diagnostics;
    }

    /// <summary>The page's position in the file, from 0.</summary>
    public int PageIndex { get; }

    /// <summary>The images drawn on the page, in drawing order (back to front); an image drawn twice is listed twice.</summary>
    public IReadOnlyList<ExtractedImage> Images { get; }

    /// <summary>
    /// Problems met while reading the page, such as a missing XObject or a stream that could not be decoded: what they
    /// affect is skipped, never thrown. Each problem is reported once per <see cref="ImageExtractor"/>, on the first
    /// page that has it.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }
}
