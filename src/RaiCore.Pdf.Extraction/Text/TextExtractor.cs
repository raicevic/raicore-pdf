using System.Text.RegularExpressions;
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
        var (items, diagnostics) = Collect(pageIndex);
        return new PageText(pageIndex, TextLayout.Compose(items, Options.Mode), diagnostics);
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

    /// <summary>
    /// Finds text on every page, in order: each match with its page and its rectangles on the page. Pages are
    /// searched as the sequence is enumerated.
    /// </summary>
    /// <param name="text">
    /// The text to find. Whitespace in it matches any whitespace on the page, a line break included, so a phrase is
    /// found where it wraps.
    /// </param>
    /// <param name="ignoreCase">Whether upper and lower case letters match each other.</param>
    /// <remarks>
    /// The pages' text in reading order is searched (see <see cref="TextExtractionMode.Reading"/>, whatever
    /// <see cref="TextExtractionOptions.Mode"/> says): text matches across font changes and the pieces a line is drawn
    /// in, but not across columns or table cells that are read one after the other. Ligatures on the page match the
    /// letters they stand for.
    /// </remarks>
    /// <example>
    /// <code>
    /// foreach (var match in new TextExtractor(reader).Find("{{customer}}"))
    /// {
    ///     Console.WriteLine($"page {match.PageIndex + 1}: {match.Bounds}");
    /// }
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty or only whitespace.</exception>
    public IEnumerable<TextMatch> Find(string text, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Find(TextSearch.PlainText(text, ignoreCase));
    }

    /// <summary>
    /// Finds the matches of a regular expression on every page, in order (see <see cref="Find(string, bool)"/>). The
    /// expression is matched against each page's text, whose lines are separated by <c>\n</c> and paragraphs by an
    /// empty line; a match never spans two pages.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">The expression's match timeout elapsed.</exception>
    public IEnumerable<TextMatch> Find(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Iterate(pattern);

        IEnumerable<TextMatch> Iterate(Regex pattern)
        {
            for (var i = 0; i < Reader.Pages.Count; i++)
            {
                foreach (var match in FindOnPage(i, pattern))
                {
                    yield return match;
                }
            }
        }
    }

    /// <summary>Finds text on one page (see <see cref="Find(string, bool)"/>).</summary>
    /// <param name="pageIndex">The page's position in the file, from 0.</param>
    /// <param name="text">The text to find.</param>
    /// <param name="ignoreCase">Whether upper and lower case letters match each other.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the file.</exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty or only whitespace.</exception>
    public IReadOnlyList<TextMatch> FindOnPage(int pageIndex, string text, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FindOnPage(pageIndex, TextSearch.PlainText(text, ignoreCase));
    }

    /// <summary>Finds the matches of a regular expression on one page (see <see cref="Find(Regex)"/>).</summary>
    /// <param name="pageIndex">The page's position in the file, from 0.</param>
    /// <param name="pattern">The expression.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the file.</exception>
    /// <exception cref="RegexMatchTimeoutException">The expression's match timeout elapsed.</exception>
    public IReadOnlyList<TextMatch> FindOnPage(int pageIndex, Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var (items, _) = Collect(pageIndex);
        var text = TextLayout.ComposeReading(items);
        return TextSearch.Find(text, pattern, pageIndex, ContentInterpreter.DisplayMatrix(Reader.Pages[pageIndex]));
    }

    /// <summary>Runs a page's content: its text items, and the problems first met on it.</summary>
    private (List<TextItem> Items, string[] Diagnostics) Collect(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, Reader.Pages.Count);
        var page = Reader.Pages[pageIndex];
        var collector = new GlyphCollector(page.Width, page.Height, Options.IncludeInvisibleText);
        var reported = _interpreter.Diagnostics.Messages.Count;
        _interpreter.Run(page, collector);
        return (collector.Items, _interpreter.Diagnostics.Messages.Skip(reported).ToArray());
    }
}
