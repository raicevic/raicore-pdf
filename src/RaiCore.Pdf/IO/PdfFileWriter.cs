using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.IO;

/// <summary>
/// Writes a complete PDF file: header, indirect objects, a classic cross-reference table and trailer.
/// Objects are numbered in the order they are added.
/// </summary>
internal sealed class PdfFileWriter
{
    private readonly List<PdfObject> _objects = [];

    /// <summary>Registers an indirect object and returns a reference to it.</summary>
    public PdfReference Add(PdfObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _objects.Add(obj);
        return new PdfReference(_objects.Count);
    }

    public void Write(Stream stream, PdfReference root, PdfReference? info, string version = "1.7")
    {
        using var output = new PdfOutput(stream);

        // Header. The comment with high-bit bytes tells transfer tools the file is binary.
        output.WriteAscii($"%PDF-{version}\n");
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var offsets = new long[_objects.Count];
        for (var i = 0; i < _objects.Count; i++)
        {
            offsets[i] = output.Position;
            output.WriteInteger(i + 1);
            output.WriteAscii(" 0 obj\n");
            _objects[i].WriteTo(output);
            output.WriteAscii("\nendobj\n");
        }

        // The file identifier is derived from the content, so identical input gives identical output.
        var id = new PdfString(output.PeekHash()[..16], hex: true);

        var xrefOffset = output.Position;
        output.WriteAscii($"xref\n0 {_objects.Count + 1}\n");
        output.WriteAscii("0000000000 65535 f\r\n"); // Each entry is exactly 20 bytes.
        foreach (var offset in offsets)
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

        trailer["ID"] = new PdfArray(id, id);

        output.WriteAscii("trailer\n");
        trailer.WriteTo(output);
        output.WriteAscii("\nstartxref\n");
        output.WriteInteger(xrefOffset);
        output.WriteAscii("\n%%EOF\n");
    }
}
