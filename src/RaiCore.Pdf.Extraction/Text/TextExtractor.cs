using RaiCore.Pdf.Extraction.Content;
using RaiCore.Pdf.Extraction.Text;

namespace RaiCore.Pdf.Extraction;

/// <summary>
/// Gets the text of the pages of a PDF file, in reading order (see <see cref="TextExtractionMode"/>).
/// </summary>
/// <remarks>
/// <para>
/// Text is read from the fonts' mappings to Unicode, which almost every producer writes; text without one is taken
/// from the font's encoding or embedded font program. Pages that are images (scans) have no text: see
/// <see cref="PageText.HasText"/>. Invisible text, such as the OCR layer of a scanned PDF, is included unless
/// <see cref="TextExtractionOptions.IncludeInvisibleText"/> says otherwise; text hidden by clipping or outside the
/// page is not.
/// </para>
/// <para>
/// Malformed content never throws: what cannot be read is skipped and reported in <see cref="PageText.Diagnostics"/>.
/// The work per page is bounded; for untrusted files, run extraction inside <see cref="ResourceLimits.Enforce"/> to
/// limit memory and time as well. Like the <see cref="PdfReader"/> it reads, an extractor is not thread-safe; fonts
/// are decoded once per extractor and reused by later pages.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var reader = PdfReader.Open("report.pdf");
/// var extractor = new TextExtractor(reader);
/// foreach (var page in extractor.ExtractPages())
/// {
///     Console.WriteLine(page.HasText ? page.Text : $"Page {page.PageIndex + 1} has no text (a scan?)");
/// }
/// </code>
/// </example>
public sealed class TextExtractor
{
    private readonly ContentInterpreter _interpreter;

    /// <summary>An extractor for the pages of <paramref name="reader"/>.</summary>
    public TextExtractor(PdfReader reader, TextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Reader = reader;
        Options = options ?? TextExtractionOptions.Default;
        _interpreter = new ContentInterpreter(reader, new GlyphCollector(0, 0, true));
    }

    /// <summary>The file read.</summary>
    public PdfReader Reader { get; }

    /// <summary>The options.</summary>
    public TextExtractionOptions Options { get; }

    /// <summary>The text of one page.</summary>
    /// <param name="pageIndex">The page's position in the file, from 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the file.</exception>
    /// <exception cref="ResourceLimitExceededException">A limit of <see cref="ResourceLimits.Enforce"/> was exceeded.</exception>
    public PageText ExtractPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, Reader.Pages.Count);
        var page = Reader.Pages[pageIndex];
        var collector = new GlyphCollector(page.Width, page.Height, Options.IncludeInvisibleText);
        var reported = _interpreter.Diagnostics.Messages.Count;
        _interpreter.Run(page, collector);
        var diagnostics = _interpreter.Diagnostics.Messages.Skip(reported).ToArray();
        return new PageText(pageIndex, TextLayout.Compose(collector.Items, Options.Mode), diagnostics);
    }

    /// <summary>The text of every page, in order. Pages are read as the sequence is enumerated.</summary>
    public IEnumerable<PageText> ExtractPages() => ExtractPages(Enumerable.Range(0, Reader.Pages.Count));

    /// <summary>The text of the pages at <paramref name="pageIndexes"/> (from 0), in the order given.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the file (thrown when it is reached).</exception>
    public IEnumerable<PageText> ExtractPages(IEnumerable<int> pageIndexes)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        return Iterate(pageIndexes);

        IEnumerable<PageText> Iterate(IEnumerable<int> indexes)
        {
            foreach (var index in indexes)
            {
                yield return ExtractPage(index);
            }
        }
    }

    /// <summary>
    /// The text of the whole file: the pages' texts with <see cref="TextExtractionOptions.PageSeparator"/> between them.
    /// </summary>
    public string ExtractText() => string.Join(Options.PageSeparator, ExtractPages().Select(p => p.Text));
}
