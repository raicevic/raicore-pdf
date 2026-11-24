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
        var catalog = copier.Copy(reader.Trailer["Root"]) switch
        {
            PdfReference reference => reference,
            PdfDictionary direct => _table.Add(direct),
            _ => null,
        };

        // When /Root no longer gives a dictionary (a repair after opening took another object for it), the catalog as
        // the reader read it is copied.
        _catalog = catalog is not null && _table.Dictionary(catalog) is not null ? catalog : _table.Add(copier.Copy(reader.Catalog)!);
        _info = copier.Copy(reader.Trailer["Info"]) switch
        {
            PdfReference reference => reference,
            PdfDictionary direct => _table.Add(direct),
            _ => null,
        };

        var pages = new List<PdfReference>();
        foreach (var page in reader.Pages)
        {
            // A page is copied by its reference, so that what refers to it keeps doing so; when that gives no page (a
            // damaged file whose repair lost the object), the reader's page dictionary itself is copied.
            var copy = page.Reference is { } reference ? copier.CopyOf(reference.ObjectNumber) ?? copier.Copy(reference) as PdfReference : null;
            if (copy is null || _table.Dictionary(copy) is null)
            {
                copy = _table.Add(copier.Copy(page.Dictionary)!);
            }

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

        // A malformed file can make the catalog (or the information dictionary) a node of its own page tree: it stays,
        // without the page tree's entries, since the editor writes a page tree of its own.
        foreach (var kept in new[] { _catalog, _info }.OfType<PdfReference>())
        {
            if (_pageObjects.Remove(kept.ObjectNumber) && _table.Dictionary(kept) is { } dictionary)
            {
                dictionary["Kids"] = null;
                dictionary["Count"] = null;
                dictionary["Parent"] = null;
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

    /// <summary>
    /// Whether saves write objects in compressed object streams with a cross-reference stream (PDF 1.5), which makes
    /// files with many small objects (fonts, annotations, outlines) noticeably smaller. A file of an older version is
    /// written as PDF 1.5. Off by default; <see cref="Optimize"/> turns it on unless told not to.
    /// </summary>
    public bool UseObjectStreams { get; set; }

    /// <summary>
    /// Reads the document's interactive form as it is now: whether it has an AcroForm and XFA, and each field's name,
    /// type, value, flags and widgets (with their page and rectangle on the current pages).
    /// </summary>
    /// <example>
    /// <code>
    /// var form = PdfEditor.Open("application.pdf").GetForm();
    /// if (form.Xfa == PdfXfaKind.Dynamic) { /* the PDF pages are only a placeholder */ }
    /// foreach (var field in form.Fields)
    /// {
    ///     Console.WriteLine($"{field.FullName} ({field.Type}) = {field.Value}");
    /// }
    /// </code>
    /// </example>
    public PdfForm GetForm() => FormReader.Read(_table, _table.Dictionary(_catalog)!, Pages.References);

    /// <summary>
    /// Flattens the interactive form: draws each field's widgets into their pages as they look, then removes the widgets,
    /// the fields and the form, so the values stay visible but can no longer be changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A widget is drawn with its normal appearance (in its current state, for check boxes and radio buttons). Where it has
    /// none, or the form asks viewers to make new ones (<c>/NeedAppearances</c>), an appearance is made: text fields
    /// (single-line, multiline, comb, password, alignment, automatic size) and choice fields in the font and color of their
    /// default appearance, measured with the standard font of the same family; check boxes and radio buttons with their
    /// check character. Hidden widgets are removed without being drawn.
    /// </para>
    /// <para>
    /// A static XFA form is removed with the form it comes with; a dynamic XFA form cannot be flattened, since its pages
    /// are laid out by the viewer (<see cref="FlattenForm(PdfFormFlattenOptions)"/> can remove its XFA first). Other
    /// annotations are not touched.
    /// </para>
    /// </remarks>
    /// <returns>How many widgets were drawn (and how many of those with an appearance made for them) and removed.</returns>
    /// <exception cref="NotSupportedException">The form is a dynamic XFA form.</exception>
    public PdfFlattenResult FlattenForm() => new FormFlattener(_table, _table.Dictionary(_catalog)!, Pages.References).Flatten();

    /// <summary>
    /// Flattens the interactive form as <see cref="FlattenForm()"/> does, with a choice of what happens to a dynamic XFA
    /// form: throw (the default), or remove the XFA and flatten the AcroForm behind it.
    /// </summary>
    /// <example>
    /// <code>
    /// var editor = PdfEditor.Open("application.pdf");
    /// editor.FlattenForm(new PdfFormFlattenOptions { DynamicXfa = PdfDynamicXfaHandling.RemoveXfa });
    /// editor.Save("application-flat.pdf");
    /// </code>
    /// </example>
    /// <param name="options">What to do with a dynamic XFA form.</param>
    /// <returns>How many widgets were drawn (and how many of those with an appearance made for them) and removed.</returns>
    /// <exception cref="NotSupportedException">The form is a dynamic XFA form and <paramref name="options"/> says to throw.</exception>
    /// <exception cref="InvalidOperationException">
    /// The form is a dynamic XFA form whose pages are only a placeholder (<see cref="PdfForm.IsXfaPlaceholder"/>).
    /// </exception>
    public PdfFlattenResult FlattenForm(PdfFormFlattenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DynamicXfa == PdfDynamicXfaHandling.RemoveXfa && GetForm().Xfa == PdfXfaKind.Dynamic)
        {
            RemoveXfa();
        }

        return FlattenForm();
    }

    /// <summary>
    /// Removes the XFA form and keeps the AcroForm: deletes <c>/XFA</c> from the form and <c>/NeedsRendering</c> from the
    /// catalog, so that every viewer shows and fills the AcroForm fields, with their values and appearances, on the PDF
    /// pages. The fields stay fillable; <see cref="FlattenForm()"/> burns them in afterwards if wanted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a static XFA form this loses nothing that viewers without XFA show. A dynamic XFA form is laid out by the viewer,
    /// so its AcroForm may be partial or out of date; where the PDF pages are only a placeholder
    /// (<see cref="PdfForm.IsXfaPlaceholder"/>), the result would be the "please wait" page alone, so this throws unless
    /// <paramref name="force"/> is set.
    /// </para>
    /// <para>XFA usage rights and scripts go with the XFA. Check <see cref="GetForm"/> first to see what a document has.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var editor = PdfEditor.Open("application.pdf");
    /// if (!editor.GetForm().IsXfaPlaceholder)
    /// {
    ///     editor.RemoveXfa();
    ///     editor.Save("application-acroform.pdf");
    /// }
    /// </code>
    /// </example>
    /// <param name="force">Remove the XFA even when the PDF pages are only a placeholder.</param>
    /// <returns>Whether there was an XFA form to remove.</returns>
    /// <exception cref="InvalidOperationException">
    /// The PDF pages are only a placeholder for a dynamic XFA form and <paramref name="force"/> is not set.
    /// </exception>
    public bool RemoveXfa(bool force = false)
    {
        var form = GetForm();
        if (form.IsXfaPlaceholder && !force)
        {
            throw new InvalidOperationException(
                "The document's pages are only a placeholder for a dynamic XFA form: removing the XFA would leave the placeholder "
                + "page alone. Pass force: true to remove it anyway.");
        }

        var catalog = _table.Dictionary(_catalog)!;
        catalog["NeedsRendering"] = null;
        if (_table.Dictionary(catalog["AcroForm"]) is { } acroForm)
        {
            acroForm["XFA"] = null;
        }

        return form.Xfa != PdfXfaKind.None;
    }

    /// <summary>
    /// Flattens annotations: draws each one into its page as it looks when printed, then removes it, so that highlights,
    /// ink, stamps, shapes and free text stay visible but can no longer be changed or deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An annotation is drawn with its normal appearance (in its current state) when it is shown and printed: hidden ones,
    /// ones marked not to be shown on screen, and ones not printed are removed without being drawn, as is one without an
    /// appearance (viewers make some, such as the icon of a note without one; flattening does not). A pop-up window goes
    /// with the annotation it belongs to.
    /// </para>
    /// <para>
    /// Links are kept unless <see cref="PdfAnnotationFlattenOptions.KeepLinks"/> is off, and
    /// <see cref="PdfAnnotationFlattenOptions.Types"/> picks the types to flatten. Form fields are never touched: use
    /// <see cref="FlattenForm()"/>.
    /// </para>
    /// </remarks>
    /// <param name="options">Which annotations; by default all but links.</param>
    /// <returns>How many annotations were drawn and how many removed without being drawn.</returns>
    public PdfFlattenResult FlattenAnnotations(PdfAnnotationFlattenOptions? options = null) =>
        new AnnotationFlattener(_table, Pages.References, options ?? PdfAnnotationFlattenOptions.Default).Flatten();

    /// <summary>
    /// Makes the document smaller without changing how it looks: removes resources (fonts, images, graphics states and
    /// the like) that the pages' content never uses, merges identical fonts, images and other streams into one object,
    /// and compresses streams that are not compressed (see <see cref="PdfOptimizationOptions"/>). The changes stay in the
    /// document for later saves.
    /// </summary>
    /// <remarks>
    /// Objects that nothing uses any more are not written anyway: every save writes only what is still in use. Images
    /// are not recompressed and fonts are not subset again.
    /// </remarks>
    /// <param name="options">What to do; by default everything.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="InvalidOperationException">The document has no pages.</exception>
    public PdfOptimizationResult Optimize(PdfOptimizationOptions? options = null)
    {
        options ??= PdfOptimizationOptions.Default;
        if (Pages.Count == 0)
        {
            throw new InvalidOperationException("The document has no pages: there is nothing to optimize.");
        }

        var optimizer = new Optimizer(_table, _catalog, _info, Prepare());
        var removed = options.RemoveUnusedResources ? optimizer.RemoveUnusedResources() : 0;
        var merged = options.MergeDuplicates ? optimizer.MergeDuplicates() : 0;
        var compressed = options.CompressStreams ? optimizer.CompressStreams() : 0;
        if (options.UseObjectStreams)
        {
            UseObjectStreams = true;
        }

        return new PdfOptimizationResult(removed, merged, compressed);
    }

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

    private void Write(Stream stream) => WriteObjects(stream, Prepare());

    /// <summary>
    /// Brings the document up to date for writing: the page tree, and the cleanup of what points at deleted pages.
    /// Returns the objects that are no longer pages.
    /// </summary>
    private HashSet<int> Prepare()
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

        return dead;
    }

    /// <summary>
    /// Writes the objects reachable from the catalog and the document information, numbered anew. References to pages
    /// that are no longer in the document, and to page tree nodes other than the editor's, become null.
    /// </summary>
    private void WriteObjects(Stream stream, HashSet<int> dead)
    {
        using var writer = new PdfFileWriter { UseObjectStreams = UseObjectStreams };
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

        var version = UseObjectStreams && string.CompareOrdinal(_version, "1.5") < 0 ? "1.5" : _version;
        writer.Write(stream, root, info, version);
    }
}
