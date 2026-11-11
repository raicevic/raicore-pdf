using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Import;

/// <summary>
/// Copies objects of one read file into a file being written, deeply: every indirect object reachable from what is
/// copied becomes an object of the new file, once, however often and from wherever it is reached (fonts and images
/// shared by several pages are written once). Indirect objects are copied through a queue, so long chains and
/// reference loops cannot exhaust the stack; direct objects nest at most as deep as the parser allows.
/// </summary>
/// <remarks>
/// References to the source's pages and page tree never pull them in: a reference to a page that was imported becomes a
/// reference to its imported copy (see <see cref="MapPage"/>), anything else in the page tree (other pages, the
/// <c>/Parent</c> nodes) and the catalog become nothing. Stream data is shared, not re-encoded: the copy keeps the
/// source's filters, and its <c>/Length</c> is written anew.
/// </remarks>
internal sealed class PdfObjectCopier(PdfReader reader, PdfFileWriter writer)
{
    private readonly Dictionary<int, PdfReference> _copies = [];
    private readonly Dictionary<PdfReference, PdfObject> _copied = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, PdfReference> _pages = [];
    private readonly Queue<(PdfObject Source, PdfObject Copy)> _pending = new();
    private readonly int _catalog = (reader.Trailer["Root"] as PdfReference)?.ObjectNumber ?? 0;

    public PdfReader Reader => reader;

    /// <summary>How many objects of the source have been copied: a test hook for "resources are copied once".</summary>
    public int CopiedCount => _copies.Count;

    /// <summary>Records that the source page <paramref name="page"/> was imported as <paramref name="target"/>. The first import of a page wins.</summary>
    public void MapPage(PdfReaderPage page, PdfReference target)
    {
        if (page.Reference is { } reference)
        {
            _pages.TryAdd(reference.ObjectNumber, target);
        }
    }

    /// <summary>The imported copy of the source page <paramref name="reference"/> points to, or <c>null</c> when it was not imported.</summary>
    public PdfReference? PageReference(PdfObject? reference) => reader.PageOf(reference) is { Reference: { } page }
        ? _pages.GetValueOrDefault(page.ObjectNumber)
        : null;

    /// <summary>
    /// Copies an object (direct, or a reference) and everything it refers to. Returns the copy (a reference for an
    /// indirect object), or <c>null</c> when it copies to nothing (null, a missing object, a page not imported).
    /// </summary>
    public PdfObject? Copy(PdfObject? obj)
    {
        var copy = CopyValue(obj);
        while (_pending.TryDequeue(out var item))
        {
            switch (item)
            {
                case (PdfStreamObject source, PdfStreamObject target):
                    Fill(source.Dictionary, target.Dictionary, isStream: true);
                    break;
                case (PdfDictionary source, PdfDictionary target):
                    Fill(source, target, isStream: false);
                    break;
                case (PdfArray source, PdfArray target):
                    Fill(source, target);
                    break;
            }
        }

        return copy;
    }

    /// <summary>The object a copy stands for: the copied indirect object for a reference this copier made, else the copy itself.</summary>
    public PdfObject? Target(PdfObject? copy) =>
        copy is PdfReference reference && _copied.TryGetValue(reference, out var target) ? target : copy;

    private PdfObject? CopyValue(PdfObject? obj)
    {
        switch (obj)
        {
            case null or PdfNull:
                return null;
            case PdfReference reference:
                return CopyReference(reference);
            case PdfDictionary dictionary:
                var copy = new PdfDictionary();
                Fill(dictionary, copy, isStream: false);
                return copy;
            case PdfArray array:
                var items = new PdfArray();
                Fill(array, items);
                return items;
            case PdfStreamObject stream:
                // Streams are always indirect in a file; one reached directly is written as an object of its own.
                var target = new PdfStreamObject(stream.Data, new PdfDictionary());
                _pending.Enqueue((stream, target));
                return writer.Add(target);
            default:
                return obj; // Numbers, names, strings and booleans are immutable: the copy shares them.
        }
    }

    private PdfReference? CopyReference(PdfReference reference)
    {
        var number = reference.ObjectNumber;
        if (number == _catalog || reader.IsPageTreeObject(number))
        {
            return _pages.GetValueOrDefault(number);
        }

        if (_copies.TryGetValue(number, out var existing))
        {
            return existing;
        }

        var source = reader.Resolve(reference);
        if (source is null)
        {
            return null;
        }

        var target = source switch
        {
            PdfStreamObject stream => new PdfStreamObject(stream.Data, new PdfDictionary()),
            PdfDictionary => new PdfDictionary(),
            PdfArray => new PdfArray(),
            PdfReference => null, // A chain that Resolve could not follow.
            _ => source,
        };

        if (target is null)
        {
            return null;
        }

        var copy = writer.Add(target);
        _copies[number] = copy;
        _copied[copy] = target;
        if (!ReferenceEquals(target, source))
        {
            _pending.Enqueue((source, target));
        }

        return copy;
    }

    private void Fill(PdfDictionary source, PdfDictionary target, bool isStream)
    {
        foreach (var (key, value) in source.Entries)
        {
            // The writer writes a stream's /Length itself (and an indirect length would be an extra object).
            if (isStream && key.Value == "Length")
            {
                continue;
            }

            if (CopyValue(value) is { } copy)
            {
                target.AddUnchecked(key, copy);
            }
        }
    }

    private void Fill(PdfArray source, PdfArray target)
    {
        foreach (var item in source.Items)
        {
            target.Items.Add(CopyValue(item) ?? PdfNull.Instance); // Keep positions: arrays are often positional.
        }
    }
}
