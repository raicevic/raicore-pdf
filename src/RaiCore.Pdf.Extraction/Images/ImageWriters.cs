using System.Buffers.Binary;
using System.IO.Compression;

namespace RaiCore.Pdf.Extraction.Images;

/// <summary>Writes PNG files (ISO/IEC 15948) of 8-bit pixels: gray, gray with alpha, RGB or RGB with alpha.</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>A PNG of <paramref name="pixels"/>: rows of <paramref name="width"/> pixels of <paramref name="channels"/> bytes (1 gray, 2 gray and alpha, 3 RGB, 4 RGBA).</summary>
    public static byte[] Write(int width, int height, int channels, byte[] pixels)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = channels switch { 1 => 0, 2 => 4, 3 => 2, _ => 6 };
        Chunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var stride = width * channels;
                for (var row = 0; row < height; row++)
                {
                    zlib.WriteByte(0); // No filter.
                    zlib.Write(pixels, row * stride, stride);
                }
            }

            Chunk(output, "IDAT", compressed.ToArray());
        }

        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        data.CopyTo(typed, 4);
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(typed));
        output.Write(number);
    }

    private static uint Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>
/// Wraps CCITT fax data (<c>/CCITTFaxDecode</c>) in a one-strip TIFF file (TIFF 6.0, compression 3 or 4), which image
/// viewers and libraries open.
/// </summary>
internal static class TiffWriter
{
    /// <param name="width">The image's width in pixels.</param>
    /// <param name="height">The image's height in pixels.</param>
    /// <param name="k">The PDF <c>/K</c>: negative for Group 4, 0 for one-dimensional Group 3, positive for two-dimensional Group 3.</param>
    /// <param name="whiteIsZero">Whether decoded white is 0 (TIFF's usual photometric interpretation) or 1.</param>
    /// <param name="data">The encoded data.</param>
    public static byte[] Ccitt(int width, int height, int k, bool whiteIsZero, byte[] data)
    {
        var entries = new List<(ushort Tag, ushort Type, uint Value)>
        {
            (256, 4, (uint)width), // ImageWidth
            (257, 4, (uint)height), // ImageLength
            (258, 3, 1), // BitsPerSample
            (259, 3, k < 0 ? 4u : 3u), // Compression: T6 (Group 4) or T4 (Group 3)
            (262, 3, whiteIsZero ? 0u : 1u), // PhotometricInterpretation
            (273, 4, 0), // StripOffsets, set below
            (277, 3, 1), // SamplesPerPixel
            (278, 4, (uint)height), // RowsPerStrip
            (279, 4, (uint)data.Length), // StripByteCounts
            (k < 0 ? (ushort)293 : (ushort)292, 4, k > 0 ? 1u : 0u), // T6Options or T4Options (bit 0: two-dimensional)
        };

        var ifdLength = 2 + (entries.Count * 12) + 4;
        var dataOffset = (uint)(8 + ifdLength);
        var file = new byte[dataOffset + data.Length];
        file[0] = (byte)'I';
        file[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), (ushort)entries.Count);
        var position = 10;
        foreach (var (tag, type, value) in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(position), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(position + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(position + 4), 1);
            if (type == 3)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(position + 8), (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(position + 8), tag == 273 ? dataOffset : value);
            }

            position += 12;
        }

        // The next IFD offset (0: none) is already zero.
        data.CopyTo(file, (int)dataOffset);
        return file;
    }
}
