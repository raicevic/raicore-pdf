using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Reading;

/// <summary>Where an object is stored: at an offset in the file, or as the n-th object of an object stream.</summary>
internal readonly record struct XrefEntry(long Offset, int Generation, int StreamNumber, int Index)
{
    public bool IsCompressed => StreamNumber > 0;

    public static XrefEntry InFile(long offset, int generation) => new(offset, generation, 0, 0);

    public static XrefEntry InStream(int streamNumber, int index) => new(0, 0, streamNumber, index);
}

/// <summary>
/// The indirect objects of a PDF file (ISO 32000-2, section 7.5): reads the cross-reference data (classic tables,
/// cross-reference streams and hybrid files, following <c>/Prev</c> through incremental updates, the newest entry
/// winning), parses objects on demand from the file or from object streams, and caches them. When the
/// cross-reference data is missing or wrong, the file is scanned for <c>n g obj</c> headers instead
/// (<see cref="IsRepaired"/>). Reference loops, <c>/Prev</c> loops and objects that need themselves to be parsed
/// (a stream whose <c>/Length</c> is in its own object stream) resolve to null instead of looping.
/// </summary>
internal sealed class PdfObjectStore
{
    /// <summary>The most cross-reference sections (incremental updates) followed through <c>/Prev</c>.</summary>
    private const int MaxSections = 4096;

    /// <summary>How many references in a row (an object that is itself a reference) are followed.</summary>
    private const int MaxReferenceChain = 32;

    private readonly byte[] _data;
    private readonly Dictionary<int, XrefEntry> _entries = [];
    private readonly Dictionary<int, PdfObject> _cache = [];
    private readonly Dictionary<int, ObjectStream?> _objectStreams = [];
    private readonly HashSet<int> _resolving = [];

    /// <summary>Where <c>%PDF-</c> starts; some files have junk before it, and offsets that count from it.</summary>
    private readonly int _headerOffset;

    /// <summary>
    /// Whether the cross-reference data has been read. Until then an object that cannot be found does not trigger a
    /// repair: it may be listed in an older section that has not been read yet.
    /// </summary>
    private bool _loaded;

    public PdfObjectStore(byte[] data)
    {
        _data = data;
        var header = data.AsSpan(0, Math.Min(data.Length, 1024)).IndexOf("%PDF-"u8);
        _headerOffset = Math.Max(0, header);
        HeaderVersion = header >= 0 ? ReadVersion(header + 5) : null;
        if (header < 0 && data.AsSpan().IndexOf(" obj"u8) < 0)
        {
            throw new PdfFormatException("The data is not a PDF file: it has no %PDF- header and no objects.");
        }

        Trailer = new PdfDictionary();
        try
        {
            LoadCrossReferences();
        }
        catch (PdfFormatException)
        {
            Reconstruct();
        }

        _loaded = true;
        if (!IsRepaired && Resolve(Trailer["Root"]) is not PdfDictionary)
        {
            Reconstruct();
        }
    }

    public byte[] Data => _data;

    /// <summary>The version in the file header, such as <c>1.7</c>, or <c>null</c> when there is no header.</summary>
    public string? HeaderVersion { get; }

    /// <summary>The trailer dictionary (merged over incremental updates: the newest value of each key wins).</summary>
    public PdfDictionary Trailer { get; private set; }

    /// <summary>Whether the cross-reference data was missing or wrong, and the objects were found by scanning the file.</summary>
    public bool IsRepaired { get; private set; }

    /// <summary>The numbers of all objects the cross-reference data lists, in ascending order.</summary>
    public IEnumerable<int> ObjectNumbers => _entries.Keys.Order();

    /// <summary>Follows references until a direct object; a reference to a missing object, or a loop, gives <c>null</c>.</summary>
    public PdfObject? Resolve(PdfObject? obj)
    {
        for (var hops = 0; obj is PdfReference reference; hops++)
        {
            if (hops >= MaxReferenceChain)
            {
                return null;
            }

            obj = GetObject(reference.ObjectNumber);
        }

        return obj is PdfNull ? null : obj;
    }

    /// <summary>The indirect object with this number, or <c>null</c> when it is missing or cannot be parsed.</summary>
    public PdfObject? GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached))
        {
            return cached;
        }

        if (!_resolving.Add(number))
        {
            return null; // Needed to parse itself.
        }

        try
        {
            var obj = _entries.TryGetValue(number, out var entry) ? Load(number, entry) : null;
            if (obj is null && _loaded && !IsRepaired)
            {
                // The cross-reference data is incomplete or points at the wrong place: rebuild it from the file, and
                // try again. (A reference to an object that really does not exist costs one scan of the file.)
                Reconstruct();
                obj = _entries.TryGetValue(number, out entry) ? Load(number, entry) : null;
            }

            if (obj is not null)
            {
                _cache[number] = obj;
            }

            return obj;
        }
        finally
        {
            _resolving.Remove(number);
        }
    }

    /// <summary>Parses an object where the entry says it is; an object too damaged to parse (nested too deeply) is missing.</summary>
    private PdfObject? Load(int number, XrefEntry entry)
    {
        try
        {
            return entry.IsCompressed ? LoadFromObjectStream(number, entry) : ParseIndirectObject(entry.Offset, number);
        }
        catch (PdfFormatException)
        {
            return PdfNull.Instance; // Found, but unusable: the null object (and no repair, which would not help).
        }
    }

    /// <summary>Parses the indirect object at <paramref name="offset"/>, or returns <c>null</c> when its header is not <c>number g obj</c>.</summary>
    private PdfObject? ParseIndirectObject(long offset, int number)
    {
        if (TryParseIndirectObject(offset, out var found, out var obj) && found == number)
        {
            return obj;
        }

        // Some files count offsets from the header, after junk at the start of the file.
        if (_headerOffset > 0 && TryParseIndirectObject(offset + _headerOffset, out found, out obj) && found == number)
        {
            return obj;
        }

        return null;
    }

    /// <summary>Parses <c>n g obj ... endobj</c> at <paramref name="offset"/>, including the data of a stream.</summary>
    private bool TryParseIndirectObject(long offset, out int number, out PdfObject? obj) =>
        TryParseIndirectObject(offset, out number, out obj, out _);

    private bool TryParseIndirectObject(long offset, out int number, out PdfObject? obj, out int end)
    {
        number = 0;
        obj = null;
        end = 0;
        if (offset < 0 || offset >= _data.Length)
        {
            return false;
        }

        var lexer = new PdfLexer(_data) { Position = (int)offset };
        var first = lexer.Next();
        var second = lexer.Next();
        var keyword = lexer.Next();
        if (first.Kind != TokenKind.Integer || second.Kind != TokenKind.Integer || !lexer.IsKeyword(keyword, "obj"u8)
            || lexer.GetInteger(first) is not (> 0 and <= int.MaxValue) || lexer.GetInteger(second) is not (>= 0 and <= 65535))
        {
            return false;
        }

        number = (int)lexer.GetInteger(first)!.Value;
        var parser = new PdfParser(lexer);
        obj = parser.ParseObject() ?? PdfNull.Instance;
        end = lexer.Position;
        if (obj is PdfDictionary dictionary)
        {
            var position = lexer.Position;
            var next = lexer.Next();
            if (lexer.IsKeyword(next, "stream"u8))
            {
                var (data, streamEnd) = ReadStreamData(dictionary, next.End);
                obj = new PdfStreamObject(data, dictionary);
                end = streamEnd;
            }
            else
            {
                lexer.Position = position;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the data of a stream whose <c>stream</c> keyword ends at <paramref name="position"/>. <c>/Length</c> is
    /// trusted when <c>endstream</c> follows it; otherwise the data runs to the next <c>endstream</c>.
    /// </summary>
    private (byte[] Data, int End) ReadStreamData(PdfDictionary dictionary, int position)
    {
        // The keyword is followed by CRLF or LF (a lone CR is tolerated).
        if (position < _data.Length && _data[position] == '\r')
        {
            position++;
        }

        if (position < _data.Length && _data[position] == '\n')
        {
            position++;
        }

        var start = Math.Min(position, _data.Length);
        if (Resolve(dictionary["Length"]) is PdfInteger { Value: >= 0 } length && length.Value <= _data.Length - start)
        {
            var end = start + (int)length.Value;
            var after = end;
            while (after < _data.Length && PdfLexer.IsWhitespace(_data[after]))
            {
                after++;
            }

            if (_data.AsSpan(after).StartsWith("endstream"u8))
            {
                return (_data.AsSpan(start, end - start).ToArray(), after + 9);
            }
        }

        var found = _data.AsSpan(start).IndexOf("endstream"u8);
        var stop = found < 0 ? _data.Length : start + found;
        var dataEnd = stop;

        // The end-of-line marker before endstream is not part of the data.
        if (dataEnd > start && _data[dataEnd - 1] == '\n')
        {
            dataEnd--;
        }

        if (dataEnd > start && _data[dataEnd - 1] == '\r')
        {
            dataEnd--;
        }

        return (_data.AsSpan(start, dataEnd - start).ToArray(), found < 0 ? _data.Length : stop + 9);
    }

    private PdfObject? LoadFromObjectStream(int number, XrefEntry entry)
    {
        if (!_objectStreams.TryGetValue(entry.StreamNumber, out var stream))
        {
            stream = ObjectStream.Load(this, Resolve(new PdfReference(entry.StreamNumber)) as PdfStreamObject);
            _objectStreams[entry.StreamNumber] = stream;
        }

        return stream?.Parse(number, entry.Index);
    }

    /// <summary>Decodes a stream's data, resolving references in its dictionary in this file.</summary>
    public byte[] Decode(PdfStreamObject stream) => StreamDecoder.Decode(stream, Resolve);

    private string? ReadVersion(int position)
    {
        var end = position;
        while (end < _data.Length && end - position < 8 && (char.IsAsciiDigit((char)_data[end]) || _data[end] == '.'))
        {
            end++;
        }

        return end > position ? System.Text.Encoding.ASCII.GetString(_data, position, end - position) : null;
    }

    // --- Cross-reference data ---------------------------------------------------------------------------------------

    private void LoadCrossReferences()
    {
        var startxref = FindStartXref() ?? throw new PdfFormatException("The file has no startxref.");
        var visited = new HashSet<long>();
        long? offset = startxref;
        while (offset is { } at)
        {
            if (!visited.Add(at) || visited.Count > MaxSections)
            {
                break; // A /Prev loop.
            }

            var trailer = ReadSection(at) ?? (_headerOffset > 0 ? ReadSection(at + _headerOffset) : null)
                ?? throw new PdfFormatException("startxref does not point at cross-reference data.");
            MergeTrailer(trailer, newer: false);
            offset = Resolve(trailer["Prev"]) is PdfInteger prev ? prev.Value : null;
        }

        if (_entries.Count == 0)
        {
            throw new PdfFormatException("The cross-reference data lists no objects.");
        }
    }

    /// <summary>The offset after the last <c>startxref</c> keyword.</summary>
    private long? FindStartXref()
    {
        var tail = Math.Max(0, _data.Length - 4096);
        var index = _data.AsSpan(tail).LastIndexOf("startxref"u8);
        if (index < 0)
        {
            return null;
        }

        var lexer = new PdfLexer(_data) { Position = tail + index + 9 };
        var token = lexer.Next();
        return token.Kind == TokenKind.Integer ? lexer.GetInteger(token) : null;
    }

    /// <summary>Reads one cross-reference section (table or stream) at <paramref name="offset"/> and returns its trailer.</summary>
    private PdfDictionary? ReadSection(long offset)
    {
        if (offset < 0 || offset >= _data.Length)
        {
            return null;
        }

        var lexer = new PdfLexer(_data) { Position = (int)offset };
        var token = lexer.Next();
        if (lexer.IsKeyword(token, "xref"u8))
        {
            var trailer = ReadTable(lexer);

            // A hybrid file: objects in object streams are listed in a cross-reference stream, read after the table.
            if (Resolve(trailer["XRefStm"]) is PdfInteger stream)
            {
                _ = ReadStream(stream.Value);
            }

            return trailer;
        }

        return ReadStream(offset);
    }

    /// <summary>Reads a classic cross-reference table after the <c>xref</c> keyword, and the trailer after it.</summary>
    private PdfDictionary ReadTable(PdfLexer lexer)
    {
        while (true)
        {
            var token = lexer.Next();
            if (lexer.IsKeyword(token, "trailer"u8))
            {
                return new PdfParser(lexer).ParseObject() as PdfDictionary
                    ?? throw new PdfFormatException("The trailer is not a dictionary.");
            }

            var countToken = lexer.Next();
            if (token.Kind != TokenKind.Integer || countToken.Kind != TokenKind.Integer
                || lexer.GetInteger(token) is not (>= 0 and < int.MaxValue) || lexer.GetInteger(countToken) is not (>= 0 and < int.MaxValue))
            {
                throw new PdfFormatException("A cross-reference subsection header is malformed.");
            }

            var first = lexer.GetInteger(token)!.Value;
            var count = lexer.GetInteger(countToken)!.Value;

            // Each entry takes at least 6 bytes ("0 0 n "), which bounds a lying count.
            if (count > (_data.Length - lexer.Position) / 6)
            {
                throw new PdfFormatException("A cross-reference subsection is longer than the file.");
            }

            for (var i = 0L; i < count; i++)
            {
                var offsetToken = lexer.Next();
                var generationToken = lexer.Next();
                var type = lexer.Next();
                if (offsetToken.Kind != TokenKind.Integer || generationToken.Kind != TokenKind.Integer || type.Kind != TokenKind.Keyword)
                {
                    throw new PdfFormatException("A cross-reference entry is malformed.");
                }

                var number = first + i;
                if (lexer.IsKeyword(type, "n"u8) && number is > 0 and <= int.MaxValue
                    && lexer.GetInteger(offsetToken) is { } entryOffset and > 0
                    && lexer.GetInteger(generationToken) is { } generation and >= 0 and <= 65535)
                {
                    _entries.TryAdd((int)number, XrefEntry.InFile(entryOffset, (int)generation)); // Newer sections were read first.
                }
            }
        }
    }

    /// <summary>Reads a cross-reference stream at <paramref name="offset"/> and returns its dictionary (the trailer).</summary>
    private PdfDictionary? ReadStream(long offset)
    {
        if (!TryParseIndirectObject(offset, out _, out var obj) || obj is not PdfStreamObject stream
            || Resolve(stream.Dictionary["Type"]) is not PdfName { Value: "XRef" })
        {
            return null;
        }

        var dictionary = stream.Dictionary;
        if (Resolve(dictionary["W"]) is not PdfArray { Items.Count: >= 3 } w
            || w.Items.Take(3).Any(item => Resolve(item) is not PdfInteger { Value: >= 0 and <= 8 }))
        {
            throw new PdfFormatException("A cross-reference stream has an invalid /W.");
        }

        var widths = w.Items.Take(3).Select(item => (int)((PdfInteger)Resolve(item)!).Value).ToArray();
        var entryLength = widths.Sum();
        if (entryLength == 0)
        {
            throw new PdfFormatException("A cross-reference stream has an invalid /W.");
        }

        var data = Decode(stream);
        var size = Resolve(dictionary["Size"]) is PdfInteger s ? s.Value : 0;
        var index = Resolve(dictionary["Index"]) is PdfArray array
            ? array.Items.Select(item => Resolve(item) is PdfInteger i ? i.Value : -1).ToList()
            : [0, size];

        var position = 0;
        for (var pair = 0; pair + 1 < index.Count; pair += 2)
        {
            var first = index[pair];
            var count = index[pair + 1];
            if (first < 0 || count < 0)
            {
                throw new PdfFormatException("A cross-reference stream has an invalid /Index.");
            }

            for (var i = 0L; i < count && position + entryLength <= data.Length; i++, position += entryLength)
            {
                var type = widths[0] == 0 ? 1 : Field(data, position, widths[0]);
                var field2 = Field(data, position + widths[0], widths[1]);
                var field3 = Field(data, position + widths[0] + widths[1], widths[2]);
                var number = first + i;
                if (number is <= 0 or > int.MaxValue)
                {
                    continue;
                }

                if (type == 1 && field2 > 0 && field3 <= 65535)
                {
                    _entries.TryAdd((int)number, XrefEntry.InFile(field2, (int)field3));
                }
                else if (type == 2 && field2 is > 0 and <= int.MaxValue && field3 <= int.MaxValue)
                {
                    _entries.TryAdd((int)number, XrefEntry.InStream((int)field2, (int)field3));
                }
            }
        }

        return dictionary;
    }

    /// <summary>A big-endian unsigned field of 0 to 8 bytes.</summary>
    private static long Field(byte[] data, int position, int width)
    {
        var value = 0L;
        for (var i = 0; i < width; i++)
        {
            value = (value << 8) | data[position + i];
        }

        return value;
    }

    /// <summary>Merges a section's trailer into <see cref="Trailer"/>: the newer one's keys win.</summary>
    private void MergeTrailer(PdfDictionary trailer, bool newer)
    {
        var merged = new PdfDictionary();
        var (first, second) = newer ? (trailer, Trailer) : (Trailer, trailer);
        foreach (var (key, value) in first.Entries)
        {
            merged.AddUnchecked(key, value);
        }

        foreach (var (key, value) in second.Entries)
        {
            if (merged[key] is null)
            {
                merged.AddUnchecked(key, value);
            }
        }

        Trailer = merged;
    }

    // --- Reconstruction ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the cross-reference data by scanning the whole file for <c>n g obj</c> headers and trailers. Later
    /// definitions win, as in incremental updates. Objects in object streams are added unless a later object in the file
    /// defines them. A missing <c>/Root</c> is taken from the last object of type <c>/Catalog</c>.
    /// </summary>
    private void Reconstruct()
    {
        IsRepaired = true;
        _entries.Clear();
        _cache.Clear();
        _objectStreams.Clear();
        Trailer = new PdfDictionary();

        var positions = new Dictionary<int, long>();
        var objectStreams = new List<(int Number, long Position)>();
        var trailers = new List<(long Position, PdfDictionary Trailer)>();
        int? catalog = null;

        var position = 0;
        while (position < _data.Length)
        {
            var found = _data.AsSpan(position).IndexOf("obj"u8);
            if (found < 0)
            {
                break;
            }

            var keyword = position + found;
            position = keyword + 3;
            if (FindHeaderStart(keyword) is not { } start)
            {
                continue;
            }

            int number;
            PdfObject? obj;
            int end;
            try
            {
                if (!TryParseIndirectObject(start, out number, out obj, out end))
                {
                    continue;
                }
            }
            catch (PdfFormatException)
            {
                continue; // Too deeply nested: skip the object.
            }

            _entries[number] = XrefEntry.InFile(start, 0);
            positions[number] = start;
            position = Math.Max(position, end);
            var dictionary = obj switch
            {
                PdfStreamObject stream => stream.Dictionary,
                PdfDictionary d => d,
                _ => null,
            };

            switch ((dictionary?["Type"] as PdfName)?.Value)
            {
                case "XRef":
                    trailers.Add((start, dictionary!));
                    break;
                case "ObjStm":
                    objectStreams.Add((number, start));
                    break;
                case "Catalog":
                    catalog = number;
                    break;
            }
        }

        // Trailers of classic sections.
        for (var at = _data.AsSpan().IndexOf("trailer"u8); at >= 0;)
        {
            var lexer = new PdfLexer(_data) { Position = at + 7 };
            try
            {
                if (new PdfParser(lexer).ParseObject() is PdfDictionary trailer)
                {
                    trailers.Add((at, trailer));
                }
            }
            catch (PdfFormatException)
            {
                // A damaged trailer: ignore it.
            }

            var next = _data.AsSpan(at + 7).IndexOf("trailer"u8);
            at = next < 0 ? -1 : at + 7 + next;
        }

        foreach (var (_, trailer) in trailers.OrderBy(t => t.Position))
        {
            MergeTrailer(trailer, newer: true);
        }

        Trailer["Prev"] = null;
        Trailer["XRefStm"] = null;

        // Objects in object streams, unless a later top-level object replaces them.
        foreach (var (streamNumber, streamPosition) in objectStreams.OrderBy(s => s.Position))
        {
            var stream = ObjectStream.Load(this, GetObject(streamNumber) as PdfStreamObject);
            if (stream is null)
            {
                continue;
            }

            _objectStreams[streamNumber] = stream;
            for (var i = 0; i < stream.Numbers.Length; i++)
            {
                var number = stream.Numbers[i];
                if (!positions.TryGetValue(number, out var at) || at < streamPosition)
                {
                    _entries[number] = XrefEntry.InStream(streamNumber, i);
                    positions[number] = streamPosition;
                    _cache.Remove(number);
                }
            }
        }

        if (Resolve(Trailer["Root"]) is not PdfDictionary)
        {
            Trailer["Root"] = null;
            catalog ??= FindCatalog();
            if (catalog is { } root)
            {
                Trailer["Root"] = new PdfReference(root);
            }
        }
    }

    /// <summary>The last object of type <c>/Catalog</c>, also inside object streams.</summary>
    private int? FindCatalog()
    {
        int? found = null;
        foreach (var number in ObjectNumbers)
        {
            if (Resolve(new PdfReference(number)) is PdfDictionary dictionary && dictionary["Type"] is PdfName { Value: "Catalog" })
            {
                found = number;
            }
        }

        return found;
    }

    /// <summary>
    /// The start of <c>n g</c> before the <c>obj</c> keyword at <paramref name="keyword"/>, or <c>null</c> when the
    /// keyword is not an object header.
    /// </summary>
    private int? FindHeaderStart(int keyword)
    {
        if (keyword + 3 < _data.Length && PdfLexer.IsRegular(_data[keyword + 3]))
        {
            return null; // Part of a longer word, such as "endobj" (checked below) or "objects".
        }

        var i = keyword - 1;
        if (i < 0 || !PdfLexer.IsWhitespace(_data[i]))
        {
            return null;
        }

        i = SkipBack(i, PdfLexer.IsWhitespace);
        var generationEnd = i;
        i = SkipBack(i, IsAsciiDigit);
        if (i == generationEnd || i < 0 || !PdfLexer.IsWhitespace(_data[i]))
        {
            return null;
        }

        i = SkipBack(i, PdfLexer.IsWhitespace);
        var numberEnd = i;
        i = SkipBack(i, IsAsciiDigit);
        if (i == numberEnd || (i >= 0 && PdfLexer.IsRegular(_data[i])))
        {
            return null;
        }

        return i + 1;

        int SkipBack(int from, Func<byte, bool> predicate)
        {
            var steps = 0;
            while (from >= 0 && predicate(_data[from]) && steps++ < 32)
            {
                from--;
            }

            return from;
        }
    }

    private static bool IsAsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>An object stream (ISO 32000-2, 7.5.7): its decoded data, and the number and offset of each object in it.</summary>
    private sealed class ObjectStream(byte[] data, int first, int[] numbers, int[] offsets)
    {
        public int[] Numbers => numbers;

        public static ObjectStream? Load(PdfObjectStore store, PdfStreamObject? stream)
        {
            if (stream is null || store.Resolve(stream.Dictionary["N"]) is not PdfInteger { Value: >= 0 } n
                || store.Resolve(stream.Dictionary["First"]) is not PdfInteger { Value: >= 0 } firstOffset)
            {
                return null;
            }

            byte[] data;
            try
            {
                data = store.Decode(stream);
            }
            catch (PdfFormatException)
            {
                return null;
            }

            if (firstOffset.Value > data.Length || n.Value > data.Length / 2)
            {
                return null; // Each pair of integers takes at least 4 bytes, so a larger /N is a lie.
            }

            var count = (int)n.Value;
            var numbers = new int[count];
            var offsets = new int[count];
            var lexer = new PdfLexer(data);
            for (var i = 0; i < count; i++)
            {
                var number = lexer.Next();
                var offset = lexer.Next();
                if (number.Kind != TokenKind.Integer || offset.Kind != TokenKind.Integer
                    || lexer.GetInteger(number) is not (> 0 and <= int.MaxValue) || lexer.GetInteger(offset) is not (>= 0 and <= int.MaxValue))
                {
                    Array.Resize(ref numbers, i);
                    Array.Resize(ref offsets, i);
                    break;
                }

                numbers[i] = (int)lexer.GetInteger(number)!.Value;
                offsets[i] = (int)lexer.GetInteger(offset)!.Value;
            }

            return new ObjectStream(data, (int)firstOffset.Value, numbers, offsets);
        }

        /// <summary>Parses object <paramref name="number"/>, expected at <paramref name="index"/> but looked up by number if not there.</summary>
        public PdfObject? Parse(int number, int index)
        {
            if (index < 0 || index >= numbers.Length || numbers[index] != number)
            {
                index = Array.IndexOf(numbers, number);
                if (index < 0)
                {
                    return null;
                }
            }

            var position = (long)first + offsets[index];
            if (position >= data.Length)
            {
                return null;
            }

            var lexer = new PdfLexer(data) { Position = (int)position };
            return new PdfParser(lexer).ParseObject() ?? PdfNull.Instance;
        }
    }
}
