using System.Buffers.Binary;

namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes the first frame of a GIF (87a/89a) onto its logical screen: LZW decompression, interlacing,
/// local/global palettes and the transparent color. Areas outside the frame are transparent.
/// </summary>
internal static class GifDecoder
{
    public static bool IsGif(ReadOnlySpan<byte> data) => data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8);

    public static DecodedImage Decode(byte[] data)
    {
        if (!IsGif(data) || data.Length < 13)
        {
            throw new ImageFormatException("Not a GIF file.");
        }

        int screenWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        int screenHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var flags = data[10];
        var pos = 13;
        byte[]? globalPalette = null;
        if ((flags & 0x80) != 0)
        {
            globalPalette = ReadPalette(data, ref pos, flags);
        }

        var transparentIndex = -1;
        while (pos < data.Length)
        {
            var block = data[pos++];
            switch (block)
            {
                case 0x21: // Extension
                    Require(data, pos, 1);
                    var label = data[pos++];
                    if (label == 0xF9 && pos + 5 <= data.Length && data[pos] >= 4)
                    {
                        // Graphic control extension: transparency flag and index.
                        if ((data[pos + 1] & 0x01) != 0)
                        {
                            transparentIndex = data[pos + 4];
                        }
                    }

                    SkipSubBlocks(data, ref pos);
                    break;
                case 0x2C: // Image descriptor
                    return DecodeFrame(data, ref pos, screenWidth, screenHeight, globalPalette, transparentIndex);
                case 0x3B: // Trailer
                    throw new ImageFormatException("The GIF has no image.");
                default:
                    throw new ImageFormatException($"Invalid GIF block 0x{block:X2}.");
            }
        }

        throw new ImageFormatException("Truncated GIF.");
    }

    private static DecodedImage DecodeFrame(byte[] data, ref int pos, int screenWidth, int screenHeight, byte[]? globalPalette, int transparentIndex)
    {
        Require(data, pos, 9);
        int left = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        int top = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 4));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 6));
        var flags = data[pos + 8];
        pos += 9;
        var palette = (flags & 0x80) != 0 ? ReadPalette(data, ref pos, flags) : globalPalette
            ?? throw new ImageFormatException("GIF frame without a color table.");

        // Some encoders write a zero-sized logical screen; use the frame then.
        if (screenWidth == 0 || screenHeight == 0)
        {
            screenWidth = left + width;
            screenHeight = top + height;
        }

        if (width == 0 || height == 0 || screenWidth == 0 || screenHeight == 0)
        {
            throw new ImageFormatException("GIF image has a zero size.");
        }

        ResourceLimits.CheckImagePixels(screenWidth, screenHeight);
        ResourceLimits.CheckImagePixels(width, height);

        Require(data, pos, 1);
        int minCodeSize = data[pos++];
        if (minCodeSize is < 2 or > 11)
        {
            throw new ImageFormatException("Invalid GIF LZW code size.");
        }

        var compressed = ReadSubBlocks(data, ref pos);
        var indices = Lzw(compressed, minCodeSize, width * height);
        if ((flags & 0x40) != 0)
        {
            indices = Deinterlace(indices, width, height);
        }

        // Place the frame on the logical screen; uncovered pixels are transparent.
        var pixels = new byte[screenWidth * screenHeight];
        var alpha = new byte[screenWidth * screenHeight];
        var anyTransparent = false;
        for (var y = 0; y < screenHeight; y++)
        {
            for (var x = 0; x < screenWidth; x++)
            {
                var fx = x - left;
                var fy = y - top;
                var inside = fx >= 0 && fy >= 0 && fx < width && fy < height;
                var index = inside ? indices[(fy * width) + fx] : -1;
                var opaque = inside && index != transparentIndex;
                pixels[(y * screenWidth) + x] = (byte)Math.Max(index, 0);
                alpha[(y * screenWidth) + x] = opaque ? (byte)255 : (byte)0;
                anyTransparent |= !opaque;
            }
        }

        return new DecodedImage(screenWidth, screenHeight, ImageColorSpace.Indexed, 8, pixels, ImageEncoding.Raw)
        {
            Palette = palette,
            Alpha = anyTransparent ? alpha : null,
        };
    }

    /// <summary>GIF's variant of LZW: variable code width from minCodeSize + 1 up to 12 bits, LSB-first.</summary>
    private static byte[] Lzw(byte[] data, int minCodeSize, int pixelCount)
    {
        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var firstByte = new byte[4096];
        var lengths = new int[4096];
        for (var i = 0; i < clear; i++)
        {
            suffix[i] = (byte)i;
            firstByte[i] = (byte)i;
            lengths[i] = 1;
        }

        var output = new byte[pixelCount];
        var outPos = 0;
        var codeSize = minCodeSize + 1;
        var next = clear + 2;
        var previous = -1;
        long bitPos = 0;
        var stack = new byte[4096];

        while (outPos < pixelCount)
        {
            if (bitPos + codeSize > (long)data.Length * 8)
            {
                break; // Truncated data: keep what was decoded, like browsers do.
            }

            var code = 0;
            for (var i = 0; i < codeSize; i++, bitPos++)
            {
                code |= ((data[bitPos >> 3] >> (int)(bitPos & 7)) & 1) << i;
            }

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = clear + 2;
                previous = -1;
                continue;
            }

            if (code == end)
            {
                break;
            }

            int entry;
            if (previous < 0)
            {
                if (code >= clear)
                {
                    throw new ImageFormatException("Invalid GIF LZW data.");
                }

                entry = code;
            }
            else
            {
                if (code > next || (code == next && next >= 4096))
                {
                    throw new ImageFormatException("Invalid GIF LZW code.");
                }

                // The KwKwK case: the code being defined right now.
                var first = code == next ? firstByte[previous] : firstByte[code];
                if (next < 4096)
                {
                    prefix[next] = (short)previous;
                    suffix[next] = first;
                    firstByte[next] = firstByte[previous];
                    lengths[next] = lengths[previous] + 1;
                    next++;
                    if (next == 1 << codeSize && codeSize < 12)
                    {
                        codeSize++;
                    }
                }

                entry = code;
            }

            // Emit the string for entry (built backwards through the prefix chain).
            var length = lengths[entry];
            var c = entry;
            for (var i = length - 1; i >= 0; i--)
            {
                stack[i] = suffix[c];
                c = prefix[c];
            }

            var count = Math.Min(length, pixelCount - outPos);
            stack.AsSpan(0, count).CopyTo(output.AsSpan(outPos));
            outPos += count;
            previous = entry;
        }

        return output;
    }

    private static byte[] Deinterlace(byte[] indices, int width, int height)
    {
        var output = new byte[indices.Length];
        var row = 0;
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            for (var y = start; y < height; y += step)
            {
                Array.Copy(indices, row * width, output, y * width, width);
                row++;
            }
        }

        return output;
    }

    private static byte[] ReadPalette(byte[] data, ref int pos, byte flags)
    {
        var size = 3 * (2 << (flags & 0x07));
        Require(data, pos, size);
        var palette = data.AsSpan(pos, size).ToArray();
        pos += size;
        return palette;
    }

    private static byte[] ReadSubBlocks(byte[] data, ref int pos)
    {
        using var output = new MemoryStream();
        while (true)
        {
            Require(data, pos, 1);
            var size = data[pos++];
            if (size == 0)
            {
                return output.ToArray();
            }

            Require(data, pos, size);
            output.Write(data, pos, size);
            pos += size;
        }
    }

    private static void SkipSubBlocks(byte[] data, ref int pos) => _ = ReadSubBlocks(data, ref pos);

    private static void Require(byte[] data, int pos, int count)
    {
        if (pos < 0 || pos + count > data.Length)
        {
            throw new ImageFormatException("Truncated GIF.");
        }
    }
}
