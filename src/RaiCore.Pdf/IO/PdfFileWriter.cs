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
/// </remarks>
internal sealed class PdfFileWriter : IDisposable
{
    /// <summary>The objects by number - 1: <c>null</c> once written, or while reserved and not set yet.</summary>
    private readonly List<PdfObject?> _objects = [];

    /// <summary>Where each object starts in the file; 0 while it is not written.</summary>
    private readonly List<long> _offsets = [];

    /// <summary>The numbers of the objects added or set since the last flush.</summary>
    private readonly List<int> _pending = [];

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
        var unset = _offsets.FindIndex(offset => offset == 0);
        if (unset >= 0)
        {
            throw new InvalidOperationException($"Object {unset + 1} was reserved but never set.");
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
}
