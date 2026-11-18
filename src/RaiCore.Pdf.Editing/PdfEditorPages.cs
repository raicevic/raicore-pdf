using System.Collections;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>The pages of a <see cref="PdfEditor"/>, in order: delete, insert, move and rotate them.</summary>
public sealed class PdfEditorPages : IReadOnlyList<PdfEditorPage>
{
    private readonly PdfEditor _editor;
    private readonly List<PdfReference> _pages;

    internal PdfEditorPages(PdfEditor editor, List<PdfReference> pages)
    {
        _editor = editor;
        _pages = pages;
    }

    /// <summary>The number of pages.</summary>
    public int Count => _pages.Count;

    internal IReadOnlyList<PdfReference> References => _pages;

    /// <summary>The page at <paramref name="index"/>, from 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    public PdfEditorPage this[int index]
    {
        get
        {
            CheckIndex(index);
            return new PdfEditorPage(_editor.Table, _pages[index]);
        }
    }

    /// <summary>Deletes the page at <paramref name="index"/>. The pages after it move up.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a page of the document.</exception>
    public void Delete(int index) => Delete(index, 1);

    /// <summary>Deletes <paramref name="count"/> pages from <paramref name="index"/> on.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the document's pages.</exception>
    public void Delete(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, _pages.Count, nameof(count));
        _pages.RemoveRange(index, count);
    }

    /// <summary>Moves the page at <paramref name="from"/> so that it is at <paramref name="to"/> afterwards.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the document.</exception>
    public void Move(int from, int to)
    {
        CheckIndex(from);
        CheckIndex(to);
        var page = _pages[from];
        _pages.RemoveAt(from);
        _pages.Insert(to, page);
    }

    /// <summary>Appends every page of <paramref name="source"/>, in order.</summary>
    /// <inheritdoc cref="Insert(int, PdfReader, IEnumerable{int})" path="/remarks"/>
    public void Add(PdfReader source) => Insert(_pages.Count, source);

    /// <summary>Appends the pages of <paramref name="source"/> at <paramref name="pageIndexes"/> (from 0), in that order.</summary>
    /// <inheritdoc cref="Insert(int, PdfReader, IEnumerable{int})" path="/remarks"/>
    public void Add(PdfReader source, IEnumerable<int> pageIndexes) => Insert(_pages.Count, source, pageIndexes);

    /// <summary>Inserts every page of <paramref name="source"/> at <paramref name="index"/>, in order.</summary>
    /// <inheritdoc cref="Insert(int, PdfReader, IEnumerable{int})" path="/remarks"/>
    public void Insert(int index, PdfReader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Insert(index, source, Enumerable.Range(0, source.Pages.Count));
    }

    /// <summary>
    /// Inserts pages of another file (those at <paramref name="pageIndexes"/>, from 0, in that order) so that the first
    /// of them is at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// The pages keep their content, resources, annotations, boxes and rotation; fonts and images they share are copied
    /// once. Links between inserted pages keep working; links to pages of the other file that are not inserted are
    /// dropped. The other file's outline, form fields and structure are not copied. To insert pages made with RaiCore,
    /// read the document's bytes: <c>PdfReader.Open(document.ToArray())</c>.
    /// </remarks>
    /// <param name="index">Where to insert the pages, from 0 to <see cref="Count"/> (to append).</param>
    /// <param name="source">The file to take pages from.</param>
    /// <param name="pageIndexes">The pages to insert, from 0; a page may be inserted more than once.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> or a page index is out of range.</exception>
    public void Insert(int index, PdfReader source, IEnumerable<int> pageIndexes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _pages.Count);
        var indexes = pageIndexes.ToList();
        foreach (var page in indexes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(page, nameof(pageIndexes));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, source.Pages.Count, nameof(pageIndexes));
        }

        var inserted = PageCopier.Copy(_editor, source, indexes);
        _pages.InsertRange(index, inserted);
    }

    /// <summary>The pages, in order.</summary>
    public IEnumerator<PdfEditorPage> GetEnumerator()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void CheckIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _pages.Count);
    }
}
