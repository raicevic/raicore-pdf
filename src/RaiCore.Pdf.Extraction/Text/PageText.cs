namespace RaiCore.Pdf.Extraction;

/// <summary>The text of one page, from <see cref="TextExtractor"/>.</summary>
public sealed class PageText
{
    internal PageText(int pageIndex, string text, IReadOnlyList<string> diagnostics)
    {
        PageIndex = pageIndex;
        Text = text;
        Diagnostics = diagnostics;
    }

    /// <summary>The page's position in the file, from 0.</summary>
    public int PageIndex { get; }

    /// <summary>The page's text: lines separated by <c>\n</c>, without trailing spaces.</summary>
    public string Text { get; }

    /// <summary>
    /// Whether the page has any text other than whitespace. A page without text is often a scan: run OCR on it if
    /// you need its words.
    /// </summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Problems met while reading the page, such as a missing font or a stream that could not be decoded: what they
    /// affect is skipped or guessed, never thrown. Each problem is reported once per <see cref="TextExtractor"/>, on
    /// the first page that has it.
    /// </summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>The page's text.</summary>
    public override string ToString() => Text;
}
