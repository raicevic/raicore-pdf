using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Editing;

/// <summary>
/// The indirect objects of a document being edited, by number: the editor's own copies, which it changes freely (the
/// reader's objects are never changed). Stream data is shared with the files it came from, never copied.
/// </summary>
internal sealed class ObjectTable
{
    /// <summary>The most references followed to reach an object (a reference to a reference, and so on).</summary>
    private const int MaxReferenceChain = 32;

    private readonly Dictionary<int, PdfObject> _objects = [];
    private int _last;

    /// <summary>Adds an object and returns a reference to it.</summary>
    public PdfReference Add(PdfObject obj)
    {
        var reference = Reserve();
        _objects[reference.ObjectNumber] = obj;
        return reference;
    }

    /// <summary>Takes a number for an object that is set later (<see cref="Set"/>).</summary>
    public PdfReference Reserve() => new(++_last);

    public void Set(PdfReference reference, PdfObject obj) => _objects[reference.ObjectNumber] = obj;

    /// <summary>The object a reference points to (following chains), the object itself when it is direct, or <c>null</c>.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        for (var hops = 0; obj is PdfReference reference; hops++)
        {
            if (hops >= MaxReferenceChain)
            {
                return null;
            }

            obj = _objects.GetValueOrDefault(reference.ObjectNumber);
        }

        return obj is PdfNull ? null : obj;
    }

    public PdfDictionary? Dictionary(PdfObject? obj) => Resolve(obj) as PdfDictionary;

    /// <summary>A stream's decoded data, or nothing when its filters cannot be decoded.</summary>
    public byte[] DecodeOrEmpty(PdfStreamObject stream)
    {
        try
        {
            return Filters.StreamDecoder.Decode(stream, Resolve);
        }
        catch (PdfFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// A copy of the table whose dictionaries and arrays are copies too, so that changing it leaves this one as it is.
    /// Stream data and immutable values (numbers, names, strings) are shared.
    /// </summary>
    public ObjectTable Clone()
    {
        var clone = new ObjectTable { _last = _last };
        foreach (var (number, obj) in _objects)
        {
            clone._objects[number] = Copy(obj);
        }

        return clone;

        static PdfObject Copy(PdfObject obj)
        {
            switch (obj)
            {
                case PdfStreamObject stream:
                    var streamCopy = new PdfStreamObject(stream.Data, new PdfDictionary());
                    Fill(stream.Dictionary, streamCopy.Dictionary);
                    return streamCopy;
                case PdfDictionary dictionary:
                    var copy = new PdfDictionary();
                    Fill(dictionary, copy);
                    return copy;
                case PdfArray array:
                    return new PdfArray(array.Items.Select(Copy));
                default:
                    return obj;
            }
        }

        static void Fill(PdfDictionary from, PdfDictionary to)
        {
            foreach (var (key, value) in from.Entries)
            {
                to.AddUnchecked(key, Copy(value));
            }
        }
    }

    public PdfArray? Array(PdfObject? obj) => Resolve(obj) as PdfArray;
}

/// <summary>
/// Copies objects of a read file into an <see cref="ObjectTable"/>, deeply: every indirect object reachable from what is
/// copied becomes an object of the table, once (shared fonts and images stay shared). Indirect objects are copied
/// through a queue, so long chains and reference loops cannot exhaust the stack.
/// </summary>
/// <param name="source">The file copied from.</param>
/// <param name="target">The table copied into.</param>
/// <param name="redirect">
/// Decides where references to some source objects go instead of being copied: returns <c>true</c> with the reference to
/// use (or <c>null</c> for none). Used to keep another file's catalog and page tree out of a copy of its pages.
/// </param>
internal sealed class GraphCopier(PdfReader source, ObjectTable target, Func<int, (bool Redirected, PdfReference? To)>? redirect = null)
{
    private readonly Dictionary<int, PdfReference> _copies = [];
    private readonly Queue<(PdfObject Source, PdfObject Copy)> _pending = new();

    /// <summary>Records that the source object <paramref name="number"/> is copied as <paramref name="copy"/> (made by the caller).</summary>
    public void Map(int number, PdfReference copy) => _copies[number] = copy;

    /// <summary>The copy of the source object <paramref name="number"/>, when it was copied.</summary>
    public PdfReference? CopyOf(int number) => _copies.GetValueOrDefault(number);

    /// <summary>
    /// Copies an object (direct, or a reference) and everything it refers to. Returns the copy (a reference for an
    /// indirect object), or <c>null</c> when it copies to nothing (null, or a missing object).
    /// </summary>
    public PdfObject? Copy(PdfObject? obj)
    {
        var copy = CopyValue(obj);
        Drain();
        return copy;
    }

    /// <summary>Copies the source object <paramref name="number"/> into the object <paramref name="into"/> already reserved for it.</summary>
    public void CopyInto(int number, PdfReference into)
    {
        _copies[number] = into;
        if (source.Resolve(new PdfReference(number)) is { } obj)
        {
            target.Set(into, Shell(obj));
        }

        Drain();
    }

    private void Drain()
    {
        while (_pending.TryDequeue(out var item))
        {
            switch (item)
            {
                case (PdfStreamObject from, PdfStreamObject to):
                    Fill(from.Dictionary, to.Dictionary, isStream: true);
                    break;
                case (PdfDictionary from, PdfDictionary to):
                    Fill(from, to, isStream: false);
                    break;
                case (PdfArray from, PdfArray to):
                    Fill(from, to);
                    break;
            }
        }
    }

    private PdfObject? CopyValue(PdfObject? obj)
    {
        switch (obj)
        {
            case null or PdfNull:
                return null;
            case PdfReference reference:
                return CopyReference(reference.ObjectNumber);
            case PdfDictionary dictionary:
                var copy = new PdfDictionary();
                Fill(dictionary, copy, isStream: false);
                return copy;
            case PdfArray array:
                var items = new PdfArray();
                Fill(array, items);
                return items;
            case PdfStreamObject stream:
                // Streams are always indirect in a file; one reached directly becomes an object of its own.
                var own = target.Reserve();
                target.Set(own, Shell(stream));
                return own;
            default:
                return obj; // Numbers, names, strings and booleans are immutable: the copy shares them.
        }
    }

    private PdfReference? CopyReference(int number)
    {
        if (redirect?.Invoke(number) is (true, var to))
        {
            return to;
        }

        if (_copies.TryGetValue(number, out var existing))
        {
            return existing;
        }

        var obj = source.Resolve(new PdfReference(number));
        if (obj is null or PdfReference)
        {
            return null;
        }

        var reference = target.Reserve();
        _copies[number] = reference;
        target.Set(reference, Shell(obj));
        return reference;
    }

    /// <summary>An empty copy of a container, filled later from the queue; immutable values are shared.</summary>
    private PdfObject Shell(PdfObject obj)
    {
        PdfObject copy = obj switch
        {
            PdfStreamObject stream => new PdfStreamObject(stream.Data, new PdfDictionary()),
            PdfDictionary => new PdfDictionary(),
            PdfArray => new PdfArray(),
            _ => obj,
        };

        if (!ReferenceEquals(copy, obj))
        {
            _pending.Enqueue((obj, copy));
        }

        return copy;
    }

    private void Fill(PdfDictionary from, PdfDictionary to, bool isStream)
    {
        foreach (var (key, value) in from.Entries)
        {
            // The writer writes a stream's /Length itself.
            if (isStream && key.Value == "Length")
            {
                continue;
            }

            if (CopyValue(value) is { } copy)
            {
                to.AddUnchecked(key, copy);
            }
        }
    }

    private void Fill(PdfArray from, PdfArray to)
    {
        foreach (var item in from.Items)
        {
            to.Items.Add(CopyValue(item) ?? PdfNull.Instance); // Keep positions: arrays are often positional.
        }
    }
}
