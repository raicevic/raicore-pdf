using RaiCore.Pdf.Security;

namespace RaiCore.Pdf;

/// <summary>
/// A PDF document under construction.
/// </summary>
/// <remarks>
/// A document, its pages and their canvases are not thread-safe: build and save each document on one thread at a time.
/// Different documents are independent and can be built and saved in parallel, and they can share fonts
/// (<see cref="Fonts.PdfFont"/>, <see cref="Fonts.FontCollection"/>) and images (<see cref="PdfImage"/>), which are
/// thread-safe and are not copied per document.
/// </remarks>
/// <example>
/// <code>
/// var doc = new PdfDocument();
/// var page = doc.AddPage(PdfPageSize.A4);
/// page.Canvas.DrawText("Hello, PDF", StandardFont.Helvetica, 24, 72, page.Height - 96);
/// doc.Save("hello.pdf");
/// </code>
/// </example>
public sealed partial class PdfDocument
{
    private readonly List<PdfPage> _pages = [];

    /// <summary>The writer while the document is written page by page (<see cref="BeginSave"/>).</summary>
    private DocumentWriter? _saving;

    /// <summary>The number of pages <see cref="FlushPages"/> has written.</summary>
    private int _flushed;

    /// <summary>The document was written page by page and finished (<see cref="EndSave"/>).</summary>
    private bool _saved;

    /// <summary>The pages in order.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Document metadata, written to the Info dictionary and the XMP metadata stream.</summary>
    public PdfDocumentInfo Info { get; } = new();

    /// <summary>Whether content streams are Flate-compressed. Turn off to inspect output by eye.</summary>
    public bool CompressContent { get; set; } = true;

    /// <summary>
    /// The document outline: the top-level bookmarks a PDF viewer shows in its navigation panel, each with nested
    /// entries. Empty by default (no outline is written).
    /// </summary>
    public IList<PdfOutlineItem> Outline { get; } = new List<PdfOutlineItem>();

    /// <summary>
    /// How a viewer opens the document. By default (<see cref="PdfPageMode.Automatic"/>) the outline panel opens when
    /// the document has an outline.
    /// </summary>
    public PdfPageMode PageMode { get; set; } = PdfPageMode.Automatic;

    /// <summary>
    /// Named destinations: places in the document that links refer to by name
    /// (<see cref="PdfPage.AddLink(double, double, double, double, string)"/>), written to the catalog's <c>/Names</c>
    /// tree. Names are compared exactly (ordinal). Links may be added before their destinations; links to names
    /// missing here when the document is saved are left out.
    /// </summary>
    public IDictionary<string, PdfDestination> NamedDestinations { get; } = new Dictionary<string, PdfDestination>(StringComparer.Ordinal);

    /// <summary>
    /// Password protection and permissions. <c>null</c> (the default) writes an unencrypted document. See
    /// <see cref="PdfEncryption"/>.
    /// </summary>
    public PdfEncryption? Encryption { get; set; }

    /// <summary>Appends a new, empty page.</summary>
    /// <exception cref="InvalidOperationException">The document has been saved with <see cref="EndSave"/>.</exception>
    public PdfPage AddPage(PdfPageSize size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Page dimensions must be positive.");
        }

        ThrowIfSaved();
        var page = new PdfPage(size) { Document = this };
        _pages.Add(page);
        return page;
    }

    /// <summary>Appends a new A4 page.</summary>
    public PdfPage AddPage() => AddPage(PdfPageSize.A4);

    /// <summary>Writes the document to a file.</summary>
    public void Save(string path)
    {
        using var file = File.Create(path);
        Save(file);
    }

    /// <summary>
    /// Writes the document to a stream. The stream is left open. Each page's content is written as soon as it is
    /// encoded, so the whole file is never held in memory (unless <paramref name="stream"/> is a memory stream).
    /// </summary>
    /// <remarks>
    /// The document's settings (PDF/A, PDF/UA, attachments, passwords) are checked before anything is written; a rule
    /// that can only be checked while an object is written (such as PDF/A's ban on CMYK images) leaves the stream with
    /// an incomplete file when it fails.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The document has no pages, or it is written page by page (<see cref="BeginSave"/>).
    /// </exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_saving is not null || _saved)
        {
            throw new InvalidOperationException("The document is written page by page (BeginSave and EndSave), not with Save.");
        }

        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("A PDF document must contain at least one page.");
        }

        using var writer = new DocumentWriter(this, stream);
        foreach (var page in _pages)
        {
            writer.WritePage(page, release: false);
            writer.Flush();
        }

        writer.Finish();
    }

    /// <summary>
    /// Starts writing the document to <paramref name="stream"/> page by page, to keep memory low for long documents:
    /// <see cref="FlushPages"/> writes the pages added so far and frees their content, and <see cref="EndSave"/> writes
    /// the rest. Everything that refers across pages is written at the end, so links, named destinations and the
    /// outline may still point at any page, and fonts embed the glyphs of all pages. The stream is left open.
    /// </summary>
    /// <remarks>
    /// <see cref="CompressContent"/>, <see cref="Encryption"/>, <see cref="Tagged"/>, <see cref="PdfUA"/>,
    /// <see cref="PdfAConformance"/> and <see cref="StandardFontSubstitute"/> are read here and cannot be changed
    /// afterwards; the metadata, outline, named destinations, attachments and the structure tree (which may still grow
    /// while pages are written) are read by <see cref="EndSave"/>. Each page is checked against PDF/A and PDF/UA when it
    /// is flushed. A flushed page cannot be drawn on any more, and pages cannot be inserted before it. Pages imported from other files, and pages that draw pages of other files, keep everything they take
    /// from those files until <see cref="EndSave"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var file = File.Create("report.pdf");
    /// var document = new PdfDocument();
    /// document.BeginSave(file);
    /// foreach (var chapter in chapters)
    /// {
    ///     DrawChapter(document, chapter); // Adds and draws pages.
    ///     document.FlushPages();
    /// }
    /// document.EndSave();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">The document is already being written or has been saved.</exception>
    public void BeginSave(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_saving is not null)
        {
            throw new InvalidOperationException("The document is already being written.");
        }

        ThrowIfSaved();
        _saving = new DocumentWriter(this, stream);
        _flushed = 0;
    }

    /// <summary>Whether the document is being written page by page: <see cref="BeginSave"/> was called and <see cref="EndSave"/> was not.</summary>
    public bool IsSaving => _saving is not null;

    /// <summary>
    /// Writes the pages added since <see cref="BeginSave"/> or the last flush (their content, images and other
    /// resources) and frees their content. The pages stay in <see cref="Pages"/>; links may still be added to them
    /// and refer to them, but drawing on them throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not being written page by page.</exception>
    public void FlushPages()
    {
        var writer = _saving ?? throw new InvalidOperationException("The document is not being written page by page (see BeginSave).");
        for (; _flushed < _pages.Count; _flushed++)
        {
            writer.WritePage(_pages[_flushed], release: true);
            writer.Flush(); // As Save does, so that both write the same file.
        }
    }

    /// <summary>
    /// Writes the pages that are left, then the fonts, links, outline, named destinations, metadata and the file's
    /// cross-reference table, and ends writing. The stream is left open. The document cannot be changed or saved again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is not being written page by page, or it has no pages.</exception>
    public void EndSave()
    {
        var writer = _saving ?? throw new InvalidOperationException("The document is not being written page by page (see BeginSave).");
        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("A PDF document must contain at least one page.");
        }

        FlushPages();
        _saving = null;
        _saved = true;
        using (writer)
        {
            writer.Finish();
        }
    }

    /// <summary>Pages cannot be inserted before pages already written page by page (<see cref="FlushPages"/>), nor after <see cref="EndSave"/>.</summary>
    private void CheckInsertion(int index)
    {
        ThrowIfSaved();
        if (index < _flushed)
        {
            throw new InvalidOperationException($"Page {index} has been written (FlushPages); pages can only be inserted after page {_flushed - 1}.");
        }
    }

    private void ThrowIfSaved()
    {
        if (_saved)
        {
            throw new InvalidOperationException("The document has been saved page by page (EndSave) and cannot be changed.");
        }
    }

    /// <summary>Returns the document as a byte array.</summary>
    /// <exception cref="InvalidOperationException">The document has no pages, or it is written page by page (<see cref="BeginSave"/>).</exception>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();
        Save(buffer);
        return buffer.ToArray();
    }
}
