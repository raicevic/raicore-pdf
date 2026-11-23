using System.Buffers.Binary;
using System.IO.Compression;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// The pictures of OfficeArt metafile BLIPs ([MS-ODRAW] 2.2.24 OfficeArtBlipEMF and 2.2.25 OfficeArtBlipWMF), as the
/// DOC, PPT and XLS formats store them: a 34-byte OfficeArtMetafileHeader and the (usually zlib-compressed) file.
/// </summary>
internal static class MetafileBlip
{
    private const int HeaderSize = 34;
    private const double EmusPerInch = 914400;

    /// <summary>
    /// The metafile of a BLIP's data after its UIDs: decompressed, and for a WMF (stored without its placeable header)
    /// with a placeable header made from the window its records set and the size the BLIP header gives. Null when the
    /// data is damaged.
    /// </summary>
    /// <param name="data">The BLIP record's body after the UID (or two): the metafile header first.</param>
    /// <param name="wmf">Whether it is a WMF BLIP (else EMF).</param>
    /// <param name="maxBytes">The most bytes it may decompress to.</param>
    /// <exception cref="ResourceLimitExceededException">It decompresses to more than <paramref name="maxBytes"/>.</exception>
    public static byte[]? Decode(ReadOnlySpan<byte> data, bool wmf, long maxBytes)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        var header = data[..HeaderSize];
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var (cx, cy) = (BinaryPrimitives.ReadInt32LittleEndian(header[20..]), BinaryPrimitives.ReadInt32LittleEndian(header[24..]));
        var compressed = header[32] == 0;
        var body = data[HeaderSize..];
        var file = compressed ? Inflate(body, size, maxBytes) : body.ToArray();
        if (file is null || file.Length == 0)
        {
            return null;
        }

        if (!wmf)
        {
            return file;
        }

        var bounds = (BinaryPrimitives.ReadInt32LittleEndian(header[4..]), BinaryPrimitives.ReadInt32LittleEndian(header[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]), BinaryPrimitives.ReadInt32LittleEndian(header[16..]));
        return WithPlaceable(file, bounds, cx / EmusPerInch, cy / EmusPerInch);
    }

    /// <summary>
    /// A WMF stored without its placeable header with one: the bounding box is the window its records set (else
    /// <paramref name="bounds"/>), and units per inch follow from the picture's width (or height) in inches.
    /// </summary>
    public static byte[] WithPlaceable(byte[] file, (int Left, int Top, int Right, int Bottom) bounds, double widthInches, double heightInches)
    {
        if (WmfHeader.IsPlaceable(file))
        {
            return file;
        }

        if (file.Length < 18 || WmfHeader.FindWindow(file, 18) is not { } window)
        {
            window = (bounds.Left, bounds.Top, (double)bounds.Right - bounds.Left, (double)bounds.Bottom - bounds.Top);
        }

        var (left, top, width, height) = window;
        if (width == 0 || height == 0 || Math.Abs(left) > short.MaxValue || Math.Abs(top) > short.MaxValue
            || Math.Abs(left + width) > short.MaxValue || Math.Abs(top + height) > short.MaxValue)
        {
            return [.. Placeable(0, 0, 0, 0, 0), .. file];
        }

        var inch = widthInches > 0 ? Math.Abs(width) / widthInches : heightInches > 0 ? Math.Abs(height) / heightInches : 1440;
        return [.. Placeable((short)left, (short)top, (short)(left + width), (short)(top + height), (ushort)Math.Clamp(Math.Round(inch), 1, ushort.MaxValue)), .. file];
    }

    /// <summary>A placeable WMF header ([MS-WMF] 2.3.2.3) with its checksum.</summary>
    internal static byte[] Placeable(short left, short top, short right, short bottom, ushort inch)
    {
        var header = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(6), left);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(8), top);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(10), right);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), bottom);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), inch);
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(i));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), checksum);
        return header;
    }

    private static byte[]? Inflate(ReadOnlySpan<byte> data, uint size, long maxBytes)
    {
        try
        {
            using var input = new MemoryStream(data.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), maxBytes,
                        $"A metafile picture decompresses to more than the limit of {maxBytes} bytes.");
                }

                output.Write(buffer, 0, read);
                if (size > 0 && output.Length >= size)
                {
                    break;
                }
            }

            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
