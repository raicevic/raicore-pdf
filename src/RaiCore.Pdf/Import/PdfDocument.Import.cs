using RaiCore.Pdf.Import;

namespace RaiCore.Pdf;

/// <summary>Pages imported from existing PDF files: merging documents, appending attachments, adding cover pages.</summary>
public sealed partial class PdfDocument
{
    /// <summary>Appends every page of <paramref name="source"/>, in order.</summary>
    /// <returns>The imported pages, in order.</returns>
    /// <inheritdoc cref="InsertPages(int, PdfReader, IEnumerable{int}, PdfImportOptions?)" path="/remarks"/>
    public IReadOnlyList<PdfPage> ImportPages(PdfReader source, PdfImportOptions? options = null) =>
        InsertPages(_pages.Count, source, AllPages(source), options);

    /// <summary>Appends pages of <paramref name="source"/>: those at <paramref name="pageIndexes"/> (from 0), in that order.</summary>
    /// <returns>The imported pages, in order.</returns>
    /// <inheritdoc cref="InsertPages(int, PdfReader, IEnumerable{int}, PdfImportOptions?)" path="/remarks"/>
    public IReadOnlyList<PdfPage> ImportPages(PdfReader source, IEnumerable<int> pageIndexes, PdfImportOptions? options = null) =>
        InsertPages(_pages.Count, source, pageIndexes, options);

    /// <summary>Inserts every page of <paramref name="source"/> at <paramref name="index"/>, in order.</summary>
    /// <returns>The imported pages, in order.</returns>
    /// <inheritdoc cref="InsertPages(int, PdfReader, IEnumerable{int}, PdfImportOptions?)" path="/remarks"/>
    public IReadOnlyList<PdfPage> InsertPages(int index, PdfReader source, PdfImportOptions? options = null) =>
        InsertPages(index, source, AllPages(source), options);

    /// <summary>
    /// Inserts pages of <paramref name="source"/> (those at <paramref name="pageIndexes"/>, from 0, in that order) so
    /// that the first of them is at <paramref name="index"/> in <see cref="Pages"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An imported page keeps its content, resources, boxes and rotation; its content streams, fonts and images are
    /// copied as they are, without decoding or re-encoding them. Objects the pages share (a font used on every page) are
    /// written once, also across several imports from the same <see cref="PdfReader"/> and pages drawn from it with
    /// <see cref="PdfCanvas.DrawPage(PdfReaderPage, double, double)"/>. The source file's structure (tagged PDF), forms,
    /// named destinations, JavaScript and other document-level parts are not copied.
    /// </para>
    /// <para>
    /// More can be drawn on an imported page: its <see cref="PdfPage.Canvas"/> draws over the original content, e.g.
    /// page numbers on merged documents. Its <see cref="PdfPage.Size"/> is the source page's media box, and every
    /// position on it (drawing, links, destinations) is measured from the media box's bottom-left corner, in the page's
    /// own orientation (<see cref="PdfReaderPage.Rotate"/> turns the drawing along with the page when displayed).
    /// </para>
    /// </remarks>
    /// <param name="index">Where to insert the pages, from 0 to the number of pages (to append).</param>
    /// <param name="source">The file to import from.</param>
    /// <param name="pageIndexes">The pages to import, from 0; a page may be imported more than once.</param>
    /// <param name="options">What to keep of the pages: annotations (by default) and the outline.</param>
    /// <returns>The imported pages, in order.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> or a page index is out of range.</exception>
    public IReadOnlyList<PdfPage> InsertPages(int index, PdfReader source, IEnumerable<int> pageIndexes, PdfImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _pages.Count);
        options ??= PdfImportOptions.Default;

        var imported = new List<PdfPage>();
        var bySource = new Dictionary<PdfReaderPage, PdfPage>(ReferenceEqualityComparer.Instance);
        foreach (var pageIndex in pageIndexes)
        {
            if (pageIndex < 0 || pageIndex >= source.Pages.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pageIndexes), pageIndex, $"The file has {source.Pages.Count} page(s); page indexes start at 0.");
            }

            var sourcePage = source.Pages[pageIndex];
            var page = new PdfPage(sourcePage, options);
            imported.Add(page);
            bySource.TryAdd(sourcePage, page);
        }

        _pages.InsertRange(index, imported);
        if (options.KeepOutline)
        {
            foreach (var item in PdfOutlineImporter.Read(source, bySource))
            {
                Outline.Add(item);
            }
        }

        return imported;
    }

    /// <summary>Inserts a new, empty page at <paramref name="index"/>, e.g. a cover page before imported pages.</summary>
    /// <param name="index">Where to insert the page, from 0 to the number of pages (to append).</param>
    /// <param name="size">The page size.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range, or the size is not positive.</exception>
    public PdfPage InsertPage(int index, PdfPageSize size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _pages.Count);
        var page = AddPage(size);
        _pages.RemoveAt(_pages.Count - 1);
        _pages.Insert(index, page);
        return page;
    }

    private static IEnumerable<int> AllPages(PdfReader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Enumerable.Range(0, source.Pages.Count);
    }
}
