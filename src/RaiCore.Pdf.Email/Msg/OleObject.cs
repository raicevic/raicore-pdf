using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RaiCore.Pdf.Cfb;

namespace RaiCore.Pdf.Email.Msg;

/// <summary>
/// Gets the file inside an attachment that Outlook keeps as an embedded OLE object (attach method 6, [MS-OXCMSG]
/// 2.2.2.9): a packaged file (<c>\x01Ole10Native</c>), an Office 2007+ document (<c>Package</c>), a file kept whole
/// (<c>CONTENTS</c>, such as a PDF), or an Office 97-2003 document (the storage itself, written out as a compound file);
/// else the object's picture (<c>\x02OlePres000</c>: a metafile or bitmap), as Outlook shows it.
/// </summary>
internal static class OleObject
{
    private const string NativeStream = "\u0001Ole10Native";
    private const string CompObjStream = "\u0001CompObj";

    /// <summary>An extracted object: its file name (with a fitting extension), MIME type and data.</summary>
    public sealed record Extracted(string FileName, string MimeType, byte[] Data);

    /// <summary>
    /// The file in an embedded OLE object, or <c>null</c> with <paramref name="problem"/> saying why nothing could be
    /// taken out of it.
    /// </summary>
    public static Extracted? Extract(CompoundFile file, CfbEntry storage, string name, ResourceLimits limits, out string? problem)
    {
        problem = null;
        var baseName = string.IsNullOrWhiteSpace(name) ? "object" : name;
        try
        {
            if (storage[NativeStream] is { Kind: CfbEntryKind.Stream } native && Package(file.Read(native)) is { } packaged)
            {
                var fileName = string.IsNullOrWhiteSpace(packaged.FileName) ? baseName : packaged.FileName;
                return new Extracted(fileName, MimeType(fileName), packaged.Data);
            }

            if (storage["Package"] is { Kind: CfbEntryKind.Stream } package)
            {
                var data = file.Read(package);
                var extension = OpenXmlExtension(data, limits) ?? ".zip";
                return Named(baseName, extension, data);
            }

            if (storage["CONTENTS"] is { Kind: CfbEntryKind.Stream } contents)
            {
                var data = file.Read(contents);
                return Named(baseName, Sniff(data) ?? ".bin", data);
            }

            if (OfficeExtension(storage) is { } office)
            {
                return Named(baseName, office, CompoundFileWriter.Write(file, storage, limits));
            }

            foreach (var entry in storage.Children.Where(c => c.Kind == CfbEntryKind.Stream && c.Name.StartsWith("\u0002OlePres", StringComparison.Ordinal))
                .OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                if (Presentation(file.Read(entry)) is ({ } extension, { } picture))
                {
                    return Named(baseName, extension, picture);
                }
            }

            problem = $"The embedded object ({ClassName(file, storage) ?? "unknown type"}) holds no file or picture that can be taken out.";
            return null;
        }
        catch (ResourceLimitExceededException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            problem = $"The embedded object could not be read: {ex.Message}";
            return null;
        }
    }

    private static Extracted Named(string name, string extension, byte[] data)
    {
        var fileName = string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;
        return new Extracted(fileName, MimeType(fileName), data);
    }

    /// <summary>The media type of an extracted file, by its extension.</summary>
    private static string MimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".emf" => "image/emf",
        ".wmf" => "image/wmf",
        ".txt" => "text/plain",
        ".zip" => "application/zip",
        ".doc" => "application/msword",
        ".xls" => "application/vnd.ms-excel",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// A packaged file ([MS-OLEDS] 2.3.6, the Packager's native data): size, type 2, label, source path, then the
    /// temporary path and the file's bytes. Other native data is kept whole, after its size.
    /// </summary>
    internal static (string FileName, byte[] Data)? Package(byte[] native)
    {
        var span = native.AsSpan();
        if (span.Length < 6)
        {
            return null;
        }

        var position = 4;
        if (BinaryPrimitives.ReadUInt16LittleEndian(span[position..]) == 2
            && ZeroTerminated(span, ref position, 2) is { } label
            && ZeroTerminated(span, ref position, 0) is { } source
            && position + 8 <= span.Length)
        {
            position += 4; // Reserved (0x00030000).
            var tempLength = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
            position += 4;
            if (tempLength <= (uint)(span.Length - position) && position + (int)tempLength + 4 <= span.Length)
            {
                position += (int)tempLength;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
                position += 4;
                if (size <= (uint)(span.Length - position))
                {
                    var fileName = SafeName(label.Length > 0 && label.Contains('.') ? label : FileNameOf(source) ?? label);
                    return (fileName, span.Slice(position, (int)size).ToArray());
                }
            }
        }

        // Not a package: the native data follows its size.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(span);
        return length <= (uint)(span.Length - 4) && length > 0 ? (string.Empty, span.Slice(4, (int)length).ToArray()) : null;
    }

    private static string? ZeroTerminated(ReadOnlySpan<byte> span, ref int position, int skip)
    {
        var start = position + skip;
        if (start > span.Length)
        {
            return null;
        }

        var end = span[start..].IndexOf((byte)0);
        if (end < 0)
        {
            return null;
        }

        position = start + end + 1;
        return Encoding.Latin1.GetString(span.Slice(start, end));
    }

    private static string? FileNameOf(string path)
    {
        var name = path.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        return name.Length > 0 ? name : null;
    }

    /// <summary>A file name without path separators or control characters.</summary>
    private static string SafeName(string name) =>
        new([.. (FileNameOf(name) ?? name).Where(c => !char.IsControl(c) && c is not ('/' or '\\' or ':'))]);

    /// <summary>The extension of an Office 2007+ document by its main part: <c>.docx</c>, <c>.xlsx</c> or <c>.pptx</c>.</summary>
    private static string? OpenXmlExtension(byte[] data, ResourceLimits limits)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var count = 0;
            foreach (var entry in zip.Entries)
            {
                if (++count > limits.MaxElements)
                {
                    break;
                }

                var path = entry.FullName.TrimStart('/');
                if (path.StartsWith("word/", StringComparison.OrdinalIgnoreCase))
                {
                    return ".docx";
                }

                if (path.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                {
                    return ".xlsx";
                }

                if (path.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase))
                {
                    return ".pptx";
                }
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The extension of an Office 97-2003 document kept as the object's own storage.</summary>
    private static string? OfficeExtension(CfbEntry storage) =>
        storage["WordDocument"] is { Kind: CfbEntryKind.Stream } ? ".doc"
        : storage["Workbook"] is { Kind: CfbEntryKind.Stream } || storage["Book"] is { Kind: CfbEntryKind.Stream } ? ".xls"
        : storage["PowerPoint Document"] is { Kind: CfbEntryKind.Stream } ? ".ppt"
        : null;

    /// <summary>The extension of a file kept whole, from its first bytes.</summary>
    private static string? Sniff(ReadOnlySpan<byte> data) =>
        data.StartsWith("%PDF-"u8) ? ".pdf"
        : data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) ? ".png"
        : data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg"
        : data.StartsWith("GIF8"u8) ? ".gif"
        : data.StartsWith("PK\u0003\u0004"u8) ? ".zip"
        : data.StartsWith(CompoundFile.Signature) ? ".bin"
        : null;

    /// <summary>
    /// The picture of an OLE presentation stream ([MS-OLEDS] 2.3.4): a Windows metafile (given the placeable header that
    /// a <c>.wmf</c> file starts with), an enhanced metafile, or a device-independent bitmap (given a BMP file header).
    /// </summary>
    internal static (string? Extension, byte[]? Data) Presentation(byte[] stream)
    {
        var span = stream.AsSpan();
        if (span.Length < 8)
        {
            return (null, null);
        }

        var marker = BinaryPrimitives.ReadUInt32LittleEndian(span);
        if (marker is not (0xFFFFFFFF or 0xFFFFFFFE))
        {
            return (null, null); // No standard clipboard format (a registered format name).
        }

        var format = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var position = 8;
        if (position + 4 > span.Length)
        {
            return (null, null);
        }

        var targetDeviceSize = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
        if (targetDeviceSize < 4 || targetDeviceSize - 4 > (uint)(span.Length - position - 4))
        {
            return (null, null);
        }

        position += 4 + (int)(targetDeviceSize - 4);
        if (position + 28 > span.Length)
        {
            return (null, null);
        }

        var width = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 16)..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 20)..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 24)..]);
        position += 28;
        if (size == 0 || size > (uint)(span.Length - position))
        {
            return (null, null);
        }

        var data = span.Slice(position, (int)size);
        return format switch
        {
            3 => (".wmf", Placeable(data, width, height)), // CF_METAFILEPICT.
            14 => (".emf", data.ToArray()), // CF_ENHMETAFILE.
            8 => (".bmp", Bitmap(data)), // CF_DIB.
            _ => (null, null),
        };
    }

    /// <summary>A Windows metafile with the placeable header (Aldus) in front: its size in HIMETRIC units, 2540 a inch.</summary>
    private static byte[] Placeable(ReadOnlySpan<byte> metafile, uint width, uint height)
    {
        var scale = Math.Max(1.0, Math.Max(width, height) / 32767.0);
        var header = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), (short)Math.Max(1, width / scale));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), (short)Math.Max(1, height / scale));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)Math.Max(1, 2540 / scale));
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(i));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), checksum);
        return [.. header, .. metafile];
    }

    /// <summary>A device-independent bitmap with the BMP file header in front, which points at its pixels.</summary>
    private static byte[]? Bitmap(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 16)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var bitCount = dib.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]) : 0;
        var colors = headerSize >= 40 && dib.Length >= 36 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]) : 0;
        var compression = headerSize >= 40 && dib.Length >= 20 ? BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]) : 0;
        var paletteEntries = colors != 0 ? colors : bitCount <= 8 ? 1u << bitCount : 0;
        var masks = compression == 3 && headerSize == 40 ? 12u : 0;
        var offset = 14 + headerSize + masks + (paletteEntries * 4);
        if (headerSize < 12 || offset > 14 + (uint)dib.Length)
        {
            return null;
        }

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), offset);
        dib.CopyTo(file.AsSpan(14));
        return file;
    }

    /// <summary>The object's class name from its <c>\x01CompObj</c> stream ([MS-OLEDS] 2.3.8), for reports.</summary>
    private static string? ClassName(CompoundFile file, CfbEntry storage)
    {
        if (storage[CompObjStream] is not { Kind: CfbEntryKind.Stream } compObj)
        {
            return null;
        }

        var bytes = file.Read(compObj);
        var position = 28; // Header (4 + 4 + 4 + 16).
        string? Next()
        {
            var span = bytes.AsSpan();
            if (position + 4 > span.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
            position += 4;
            if (length == 0 || length > (uint)(span.Length - position) || length > 256)
            {
                return null;
            }

            var text = Encoding.Latin1.GetString(span.Slice(position, (int)length)).TrimEnd('\0');
            position += (int)length;
            return text;
        }

        var userType = Next();
        return string.IsNullOrWhiteSpace(userType) ? null : userType;
    }
}
