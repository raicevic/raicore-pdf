using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>
/// An existing PDF file opened for changes: delete, insert, move and rotate pages (see <see cref="Pages"/>), then save,
/// as often as needed.
/// </summary>
/// <remarks>
/// <para>
/// Everything the file has is kept: its outline (bookmarks), named destinations, links, form fields, tagged structure,
/// metadata, attachments and the rest. Each save writes the objects still in use, so the resources of deleted pages
/// (fonts, images) leave the file, along with outline entries, named destinations and links that pointed at them, and
/// the form fields on them. Page labels are dropped once pages are deleted, moved or inserted before the original ones,
/// since they would no longer fit.
/// </para>
/// <para>
/// A saved file is a complete new file, not an incremental update: digital signatures become invalid. Encrypted files
/// are opened with their password and saved without encryption. The editor does not enforce
/// <see cref="PdfReader.Permissions"/>: check them before changing a file you received with restrictions.
/// </para>
/// <para>An editor is not thread-safe.</para>
/// </remarks>
/// <example>
/// <code>
/// var editor = PdfEditor.Open("contract.pdf");
/// editor.Pages.Delete(0);                                        // drop the cover page
/// editor.Pages.Add(PdfReader.Open("appendix.pdf"));               // append another file
/// editor.Pages.Move(editor.Pages.Count - 1, 0);                   // its last page goes first
/// editor.Pages[0].Rotation = 90;
/// editor.Save("contract-edited.pdf");
/// </code>
/// </example>
public sealed class PdfEditor
{
    private readonly ObjectTable _table;
    private readonly PdfReference _catalog;
    private readonly PdfReference? _info;

    /// <summary>The page tree root the editor writes: one node with every page as a kid.</summary>
    private readonly PdfReference _pageTree;

    /// <summary>The objects that are or were pages or page tree nodes: written only while they are pages in <see cref="Pages"/>.</summary>
    private readonly HashSet<int> _pageObjects = [];

    /// <summary>Outline items that lost their destination with a deleted page (see <see cref="DocumentCleanup"/>).</summary>
    private readonly HashSet<int> _strippedOutlineItems = [];

    /// <summary>The pages as they were opened, to tell whether page labels still fit.</summary>
    private readonly List<PdfReference> _openedPages;

    private string _version;

    private PdfEditor(PdfReader reader)
    {
        _table = new ObjectTable();
        var copier = new GraphCopier(reader, _table);
        _catalog = copier.Copy(reader.Trailer["Root"]) switch
        {
            PdfReference reference => reference,
            PdfDictionary direct => _table.Add(direct),
            _ => throw new PdfFormatException("The file has no document catalog."),
        };
        _info = copier.Copy(reader.Trailer["Info"]) switch
        {
            PdfReference reference => reference,
            PdfDictionary direct => _table.Add(direct),
            _ => null,
        };

        var pages = new List<PdfReference>();
        foreach (var page in reader.Pages)
        {
            var copy = page.Reference is { } reference
                ? copier.CopyOf(reference.ObjectNumber) ?? (PdfReference)copier.Copy(reference)!
                : _table.Add(copier.Copy(page.Dictionary)!);
            InheritAttributes(_table.Dictionary(copy)!, page, copier);
            pages.Add(copy);
            _pageObjects.Add(copy.ObjectNumber);
        }

        foreach (var node in reader.PageTreeNodes)
        {
            if (copier.CopyOf(node) is { } copy)
            {
                _pageObjects.Add(copy.ObjectNumber);
            }
        }

        _pageTree = _table.Add(new PdfDictionary { ["Type"] = new PdfName("Pages") });
        _openedPages = [.. pages];
        _version = reader.Version;
        Pages = new PdfEditorPages(this, pages);
    }

    /// <summary>A copy of an editor, sharing nothing it could change: saving the copy leaves the original as it was.</summary>
    private PdfEditor(PdfEditor original)
    {
        _table = original._table.Clone();
        _catalog = original._catalog;
        _info = original._info;
        _pageTree = original._pageTree;
        _pageObjects = [.. original._pageObjects];
        _strippedOutlineItems = [.. original._strippedOutlineItems];
        _openedPages = original._openedPages;
        _version = original._version;
        Pages = new PdfEditorPages(this, [.. original.Pages.References]);
    }

    /// <summary>The pages, in order: delete, insert, move and rotate them here.</summary>
    public PdfEditorPages Pages { get; }

    internal ObjectTable Table => _table;

    /// <summary>Opens a file.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="password">The user or owner password of an encrypted file; <c>null</c> for none.</param>
    /// <exception cref="PdfFormatException">The file is not a PDF file, or too damaged to read.</exception>
    /// <exception cref="PdfPasswordException">The file is encrypted and the password is missing or wrong.</exception>
    public static PdfEditor Open(string path, string? password = null) => new(PdfReader.Open(path, password));

    /// <summary>Opens a file from a stream, which is read to its end.</summary>
    /// <inheritdoc cref="Open(string, string?)"/>
    public static PdfEditor Open(Stream stream, string? password = null) => new(PdfReader.Open(stream, password));

    /// <summary>Opens a file from its bytes.</summary>
    /// <inheritdoc cref="Open(string, string?)"/>
    public static PdfEditor Open(byte[] data, string? password = null) => new(PdfReader.Open(data, password));

    /// <summary>Opens the file a reader read. The reader is not changed, and can still be used.</summary>
    public static PdfEditor Open(PdfReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new PdfEditor(reader);
    }

    /// <summary>Writes the document to a file. The file may be the one that was opened.</summary>
    /// <exception cref="InvalidOperationException">The document has no pages.</exception>
    public void Save(string path)
    {
        var data = ToArray(); // Written whole before the file is replaced, which may be the file that was opened.
        File.WriteAllBytes(path, data);
    }

    /// <summary>Writes the document to a stream, which is left open. Editing can go on afterwards, and the document can be saved again.</summary>
    /// <exception cref="InvalidOperationException">The document has no pages.</exception>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Write(stream);
    }

    /// <summary>
    /// Writes the document to a stream asynchronously: it is written to memory first, then to <paramref name="stream"/>
    /// with asynchronous writes only. The stream is left open.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document has no pages.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var data = ToArray();
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The document as the bytes of a PDF file.</summary>
    /// <exception cref="InvalidOperationException">The document has no pages.</exception>
    public byte[] ToArray()
    {
        using var buffer = new MemoryStream();
        Write(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Splits the document into consecutive parts whose files are each at most <paramref name="maxBytes"/> long, each
    /// with as many pages as fit. The document itself is not changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each part is a complete PDF file, written as <see cref="ToArray"/> writes the document with only the part's pages:
    /// fonts and images its pages share are in it once, and bookmarks, named destinations and links that lead to pages of
    /// other parts are left out. A page that is larger than the limit on its own is a part by itself, marked with
    /// <see cref="PdfSplitPart.ExceedsLimit"/>. Page labels are kept in the first part only.
    /// </para>
    /// <para>
    /// The size of a part is measured by writing it, so splitting writes each page a few times (about twice the
    /// logarithm of a part's page count); the parts are held in memory.
    /// </para>
    /// </remarks>
    /// <param name="maxBytes">The largest size of a part's file in bytes.</param>
    /// <returns>The parts, in page order; together they have every page once.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The document has no pages.</exception>
    public IReadOnlyList<PdfSplitPart> SplitBySize(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        var total = Pages.Count;
        if (total == 0)
        {
            throw new InvalidOperationException("The document has no pages: there is nothing to split.");
        }

        var parts = new List<PdfSplitPart>();
        for (var start = 0; start < total;)
        {
            var first = start;
            var remaining = total - start;
            byte[] Write(int count)
            {
                var part = new PdfEditor(this);
                part.Pages.Delete(first + count, total - first - count);
                part.Pages.Delete(0, first);
                return part.ToArray();
            }

            var best = Write(1);
            if (best.Length > maxBytes)
            {
                parts.Add(new PdfSplitPart(start, 1, best, exceedsLimit: true));
                start++;
                continue;
            }

            // Double the page count while the part fits, then search between the last count that fits and the first
            // that does not. Parts grow with their pages, so the search finds the largest count that fits.
            var (fits, tooMany) = (1, remaining + 1);
            for (var count = 2; fits < remaining; count *= 2)
            {
                count = Math.Min(count, remaining);
                var data = Write(count);
                if (data.Length > maxBytes)
                {
                    tooMany = count;
                    break;
                }

                (fits, best) = (count, data);
            }

            while (tooMany - fits > 1 && fits < remaining)
            {
                var count = fits + ((tooMany - fits) / 2);
                var data = Write(count);
                if (data.Length > maxBytes)
                {
                    tooMany = count;
                }
                else
                {
                    (fits, best) = (count, data);
                }
            }

            parts.Add(new PdfSplitPart(start, fits, best, exceedsLimit: false));
            start += fits;
        }

        return parts;
    }

    /// <summary>Notes that pages of another file were inserted: the file is written in the higher of the two versions.</summary>
    internal void NoteVersion(string version)
    {
        if (string.CompareOrdinal(version, _version) > 0)
        {
            _version = version;
        }
    }

    internal void AddPageObject(PdfReference page) => _pageObjects.Add(page.ObjectNumber);

    /// <summary>
    /// Writes the attributes a page inherits from its page tree nodes into the page itself (ISO 32000-2, 7.7.3.4), since
    /// the editor writes a new, flat page tree.
    /// </summary>
    internal static void InheritAttributes(PdfDictionary page, PdfReaderPage source, GraphCopier copier)
    {
        if (page["MediaBox"] is null)
        {
            page["MediaBox"] = Box(source.MediaBox);
        }

        if (page["CropBox"] is null && source.CropBox != source.MediaBox)
        {
            page["CropBox"] = Box(source.CropBox);
        }

        if (page["Rotate"] is null && source.Rotate != 0)
        {
            page["Rotate"] = new PdfInteger(source.Rotate);
        }

        if (page["Resources"] is null)
        {
            // Required (ISO 32000-2, table 31): a page that has none, not even inherited, gets an empty one.
            page["Resources"] = copier.Copy(source.ResourcesObject) ?? new PdfDictionary();
        }

        static PdfArray Box(PdfRectangle box) => PdfArray.OfNumbers(box.X, box.Y, box.Right, box.Top);
    }

    private void Write(Stream stream)
    {
        var pages = Pages.References;
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("The document has no pages: a PDF file needs at least one.");
        }

        var live = pages.Select(p => p.ObjectNumber).ToHashSet();
        var dead = _pageObjects.Where(n => !live.Contains(n)).ToHashSet();
        var catalog = _table.Dictionary(_catalog)!;

        var tree = _table.Dictionary(_pageTree)!;
        tree["Kids"] = new PdfArray(pages);
        tree["Count"] = new PdfInteger(pages.Count);
        foreach (var page in pages)
        {
            _table.Dictionary(page)!["Parent"] = _pageTree;
        }

        catalog["Pages"] = _pageTree;
        new DocumentCleanup(_table, catalog, pages, dead, _strippedOutlineItems).Run();
        if (!pages.Take(_openedPages.Count).SequenceEqual(_openedPages))
        {
            catalog["PageLabels"] = null;
        }

        WriteObjects(stream, dead);
    }

    /// <summary>
    /// Writes the objects reachable from the catalog and the document information, numbered anew. References to pages
    /// that are no longer in the document, and to page tree nodes other than the editor's, become null.
    /// </summary>
    private void WriteObjects(Stream stream, HashSet<int> dead)
    {
        using var writer = new PdfFileWriter();
        var written = new Dictionary<int, PdfReference>();
        var pending = new Queue<(PdfObject Source, PdfReference Target)>();

        PdfObject? Out(PdfObject? obj)
        {
            switch (obj)
            {
                case null or PdfNull:
                    return null;
                case PdfReference reference:
                    var number = reference.ObjectNumber;
                    if (dead.Contains(number))
                    {
                        return null;
                    }

                    if (written.TryGetValue(number, out var existing))
                    {
                        return existing;
                    }

                    if (_table.Resolve(reference) is not { } target)
                    {
                        return null;
                    }

                    var reserved = writer.Reserve();
                    written[number] = reserved;
                    pending.Enqueue((target, reserved));
                    return reserved;
                case PdfStreamObject stream:
                    var own = writer.Reserve();
                    pending.Enqueue((stream, own));
                    return own;
                case PdfDictionary dictionary:
                    return OutDictionary(dictionary, isStream: false);
                case PdfArray array:
                    var items = new PdfArray();
                    foreach (var item in array.Items)
                    {
                        items.Items.Add(Out(item) ?? PdfNull.Instance);
                    }

                    return items;
                default:
                    return obj;
            }
        }

        PdfDictionary OutDictionary(PdfDictionary dictionary, bool isStream)
        {
            var copy = new PdfDictionary();
            foreach (var (key, value) in dictionary.Entries)
            {
                if (!(isStream && key.Value == "Length") && Out(value) is { } item)
                {
                    copy.AddUnchecked(key, item);
                }
            }

            return copy;
        }

        var root = (PdfReference)Out(_catalog)!;
        var info = Out(_info) as PdfReference;
        while (pending.TryDequeue(out var item))
        {
            writer.Set(item.Target, item.Source switch
            {
                PdfStreamObject s => new PdfStreamObject(s.Data, OutDictionary(s.Dictionary, isStream: true)),
                PdfDictionary d => OutDictionary(d, isStream: false),
                PdfArray a => (PdfArray)Out(a)!,
                var other => other,
            });
        }

        writer.Write(stream, root, info, _version);
    }
}
