using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Security;

namespace RaiCore.Pdf.IO;

/// <summary>
/// Writes a complete PDF file: header, indirect objects, a classic cross-reference table and trailer.
/// Objects are numbered in the order they are added (or reserved).
/// </summary>
/// <remarks>
/// A file can be written in one go (<see cref="Write"/>), or incrementally: <see cref="Begin"/> writes the header,
/// each <see cref="Flush()"/> writes the objects added since the last one and lets go of them, and <see cref="Finish"/>
/// writes the rest, the cross-reference table and the trailer. An object must be complete when it is flushed; one that
/// is filled in later is <see cref="Reserve">reserved</see> (it gets its number, so others can refer to it) and
/// <see cref="Set"/> when it is complete. The cross-reference table lists objects by number, so they may be written in
/// any order.
/// <para>
/// With <see cref="UseObjectStreams"/>, objects other than streams are collected and written compressed in object
/// streams at the end, and the cross-reference table is a cross-reference stream (PDF 1.5).
/// </para>
/// </remarks>
internal sealed class PdfFileWriter : IDisposable
{
    /// <summary>The most objects in one object stream.</summary>
    private const int ObjectsPerStream = 200;

    /// <summary>The objects by number - 1: <c>null</c> once written, or while reserved and not set yet.</summary>
    private readonly List<PdfObject?> _objects = [];

    /// <summary>Where each object starts in the file; 0 while it is not written.</summary>
    private readonly List<long> _offsets = [];

    /// <summary>The numbers of the objects added or set since the last flush.</summary>
    private readonly List<int> _pending = [];

    /// <summary>Objects waiting for an object stream (see <see cref="UseObjectStreams"/>): their number and bytes.</summary>
    private readonly List<(int Number, byte[] Bytes)> _packable = [];

    /// <summary>Where objects written in object streams are: the stream's object number and the index in it.</summary>
    private readonly Dictionary<int, (int Stream, int Index)> _packed = [];

    private PdfOutput? _output;
    private StandardSecurityHandler? _security;

    /// <summary>The number of objects added or reserved so far.</summary>
    public int Count => _objects.Count;

    /// <summary>
    /// The PDF/A level the document is written for. Objects that check a PDF/A rule while they are written (fonts,
    /// images) look here, which also covers those nested in SVG images.
    /// </summary>
    public PdfAConformance PdfA { get; init; }

    /// <summary>The font that embeds a standard font in its place (see <c>PdfDocument.StandardFontSubstitute</c>), or <c>null</c>.</summary>
    public Func<Fonts.StandardFont, Fonts.PdfFont?>? StandardFontSubstitute { get; init; }

    /// <summary>
    /// Whether objects other than streams are written in compressed object streams and the cross-reference table as a
    /// stream (PDF 1.5). Not with encryption.
    /// </summary>
    public bool UseObjectStreams { get; init; }

    /// <summary>Registers an indirect object and returns a reference to it.</summary>
    public PdfReference Add(PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _objects.Add(obj);
        _offsets.Add(0);
        _pending.Add(_objects.Count);
        return new PdfReference(_objects.Count);
    }

    /// <summary>
    /// Registers <paramref name="obj"/> as the object <paramref name="reserved"/> refers to (see <see cref="Reserve"/>),
    /// or, when it is <c>null</c>, as a new object. Returns its reference.
    /// </summary>
    public PdfReference Add(PdfObject obj, PdfReference? reserved)
    {
        if (reserved is null)
        {
            return Add(obj);
        }

        Set(reserved, obj);
        return reserved;
    }

    /// <summary>Takes the next object number for an object that is set later (<see cref="Set"/>).</summary>
    public PdfReference Reserve()
    {
        _objects.Add(null);
        _offsets.Add(0);
        return new PdfReference(_objects.Count);
    }

    /// <summary>Sets the object of a reserved number.</summary>
    public void Set(PdfReference reserved, PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(reserved);
        ArgumentNullException.ThrowIfNull(obj);
        var index = reserved.ObjectNumber - 1;
        if (index < 0 || index >= _objects.Count || _objects[index] is not null || _offsets[index] != 0)
        {
            throw new InvalidOperationException($"Object {reserved.ObjectNumber} is not a reserved object that is still unset.");
        }

        _objects[index] = obj;
        _pending.Add(reserved.ObjectNumber);
    }

    /// <summary>
    /// Writes the file. With <paramref name="security"/>, the <c>/Encrypt</c> dictionary is added as the last object, the
    /// strings and streams of every other object are encrypted, and the trailer gets <c>/Encrypt</c> and the handler's
    /// document ID.
    /// </summary>
    public void Write(Stream stream, PdfReference root, PdfReference? info, string version = "1.7", StandardSecurityHandler? security = null)
    {
        Begin(stream, version, security);
        Finish(root, info);
    }

    /// <summary>Starts writing the file to <paramref name="stream"/>: the header. Objects are written by <see cref="Flush()"/> and <see cref="Finish"/>.</summary>
    public void Begin(Stream stream, string version = "1.7", StandardSecurityHandler? security = null)
    {
        if (_output is not null)
        {
            throw new InvalidOperationException("The file is already being written.");
        }

        if (UseObjectStreams && security is not null)
        {
            throw new NotSupportedException("Object streams are not written in encrypted files.");
        }

        _output = new PdfOutput(stream);
        _security = security;

        // Header. The comment with high-bit bytes tells transfer tools the file is binary.
        _output.WriteAscii($"%PDF-{version}\n");
        _output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
    }

    /// <summary>Writes the objects added or set since the last flush, in order of their numbers, and lets go of them.</summary>
    public void Flush() => Flush(encryptNumber: 0);

    /// <summary>
    /// Writes the objects that are left, the cross-reference table and the trailer. With a security handler (see
    /// <see cref="Begin"/>), the <c>/Encrypt</c> dictionary is added as the last object.
    /// </summary>
    public void Finish(PdfReference root, PdfReference? info)
    {
        var output = _output ?? throw new InvalidOperationException("The file is not being written.");
        var encryptRef = _security is null ? null : Add(_security.CreateEncryptDictionary());
        Flush(encryptRef?.ObjectNumber ?? 0);
        if (UseObjectStreams)
        {
            WriteObjectStreams();
        }

        var unset = Enumerable.Range(0, _offsets.Count).FirstOrDefault(i => _offsets[i] == 0 && !_packed.ContainsKey(i + 1), -1);
        if (unset >= 0)
        {
            throw new InvalidOperationException($"Object {unset + 1} was reserved but never set.");
        }

        if (UseObjectStreams)
        {
            FinishWithStream(output, root, info);
            return;
        }

        // The file identifier is derived from the content, so identical input gives identical output. An encrypted
        // file uses the handler's random ID, which revision 4 key derivation depends on.
        var id = new PdfString(_security?.Parameters.DocumentId ?? output.PeekHash()[..16], hex: true);

        var xrefOffset = output.Position;
        output.WriteAscii($"xref\n0 {_objects.Count + 1}\n");
        output.WriteAscii("0000000000 65535 f\r\n"); // Each entry is exactly 20 bytes.
        foreach (var offset in _offsets)
        {
            output.WriteAscii($"{offset:D10} 00000 n\r\n");
        }

        var trailer = new PdfDictionary
        {
            ["Size"] = new PdfInteger(_objects.Count + 1),
            ["Root"] = root,
        };
        if (info is not null)
        {
            trailer["Info"] = info;
        }

        if (encryptRef is not null)
        {
            trailer["Encrypt"] = encryptRef;
        }

        trailer["ID"] = new PdfArray(id, id);

        output.WriteAscii("trailer\n");
        trailer.WriteTo(output);
        output.WriteAscii("\nstartxref\n");
        output.WriteInteger(xrefOffset);
        output.WriteAscii("\n%%EOF\n");
        Dispose();
    }

    public void Dispose()
    {
        _output?.Dispose();
        _output = null;
    }

    private void Flush(int encryptNumber)
    {
        var output = _output ?? throw new InvalidOperationException("The file is not being written.");
        _pending.Sort();
        foreach (var number in _pending)
        {
            var obj = _objects[number - 1]!;
            if (UseObjectStreams && obj is not PdfStreamObject)
            {
                // Kept for an object stream: written there (unencrypted, generation 0) at the end.
                using var buffer = new MemoryStream();
                using (var bytes = new PdfOutput(buffer))
                {
                    bytes.BeginObject(number, 0, null);
                    obj.WriteTo(bytes);
                    bytes.EndObject();
                }

                _packable.Add((number, buffer.ToArray()));
                _objects[number - 1] = null;
                continue;
            }

            _offsets[number - 1] = output.Position;
            output.WriteInteger(number);
            output.WriteAscii(" 0 obj\n");
            output.BeginObject(number, 0, number == encryptNumber ? null : _security);
            obj.WriteTo(output);
            output.EndObject();
            output.WriteAscii("\nendobj\n");
            _objects[number - 1] = null;
        }

        _pending.Clear();
    }

    /// <summary>Writes the collected objects in object streams (ISO 32000-2, 7.5.7) of up to <see cref="ObjectsPerStream"/> objects.</summary>
    private void WriteObjectStreams()
    {
        for (var start = 0; start < _packable.Count; start += ObjectsPerStream)
        {
            var group = _packable.Skip(start).Take(ObjectsPerStream).ToList();
            var header = new System.Text.StringBuilder();
            using var body = new MemoryStream();
            foreach (var (number, bytes) in group)
            {
                header.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} {body.Length} ");
                body.Write(bytes);
                body.WriteByte((byte)'\n');
            }

            var headerBytes = System.Text.Encoding.ASCII.GetBytes(header.ToString());
            var data = new byte[headerBytes.Length + body.Length];
            headerBytes.CopyTo(data, 0);
            body.ToArray().CopyTo(data, headerBytes.Length);
            var stream = PdfStreamObject.CreateCompressed(data, new PdfDictionary
            {
                ["Type"] = new PdfName("ObjStm"),
                ["N"] = new PdfInteger(group.Count),
                ["First"] = new PdfInteger(headerBytes.Length),
            });
            var reference = Add(stream);
            for (var i = 0; i < group.Count; i++)
            {
                _packed[group[i].Number] = (reference.ObjectNumber, i);
            }

            Flush(encryptNumber: 0);
        }

        _packable.Clear();
    }

    /// <summary>Writes the cross-reference stream (ISO 32000-2, 7.5.8), which holds the trailer, and the end of the file.</summary>
    private void FinishWithStream(PdfOutput output, PdfReference root, PdfReference? info)
    {
        var id = new PdfString(output.PeekHash()[..16], hex: true);
        var xrefNumber = _objects.Count + 1;
        var xrefOffset = output.Position;
        var offsetBytes = Math.Max(4, (int)Math.Ceiling(Math.Log2(xrefOffset + 1) / 8));
        var rowLength = 1 + offsetBytes + 2;
        var rows = new byte[(xrefNumber + 1) * rowLength];
        void Row(int number, int type, long field2, int field3)
        {
            var at = number * rowLength;
            rows[at] = (byte)type;
            for (var b = 0; b < offsetBytes; b++)
            {
                rows[at + offsetBytes - b] = (byte)(field2 >> (8 * b));
            }

            rows[at + offsetBytes + 1] = (byte)(field3 >> 8);
            rows[at + offsetBytes + 2] = (byte)field3;
        }

        Row(0, 0, 0, 0xFFFF);
        for (var number = 1; number < xrefNumber; number++)
        {
            if (_packed.TryGetValue(number, out var location))
            {
                Row(number, 2, location.Stream, location.Index);
            }
            else
            {
                Row(number, 1, _offsets[number - 1], 0);
            }
        }

        Row(xrefNumber, 1, xrefOffset, 0);
        var dictionary = new PdfDictionary
        {
            ["Type"] = new PdfName("XRef"),
            ["Size"] = new PdfInteger(xrefNumber + 1),
            ["W"] = PdfArray.OfNumbers(1, offsetBytes, 2),
            ["Root"] = root,
        };
        if (info is not null)
        {
            dictionary["Info"] = info;
        }

        dictionary["ID"] = new PdfArray(id, id);
        var xref = PdfStreamObject.CreateCompressed(rows, dictionary);
        output.WriteInteger(xrefNumber);
        output.WriteAscii(" 0 obj\n");
        output.BeginObject(xrefNumber, 0, null);
        xref.WriteTo(output);
        output.EndObject();
        output.WriteAscii("\nendobj\nstartxref\n");
        output.WriteInteger(xrefOffset);
        output.WriteAscii("\n%%EOF\n");
        Dispose();
    }
}
