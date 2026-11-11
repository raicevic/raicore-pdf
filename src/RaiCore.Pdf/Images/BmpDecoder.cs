using System.Buffers.Binary;
using System.Numerics;

namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes Windows bitmaps: BITMAPCOREHEADER, BITMAPINFOHEADER and its V2-V5 extensions; 1, 2, 4 and 8-bit
/// palettes, 16, 24 and 32-bit pixels, BI_RLE8 and BI_RLE4, BI_BITFIELDS and BI_ALPHABITFIELDS, bottom-up and
/// top-down rows. 32-bit pixels have alpha if the format says so (an alpha mask, or the high byte of BI_RGB) and
/// some pixel's alpha is non-zero: many writers leave the byte zero, and browsers then show the image opaque.
/// Pixels that run-length data skips are transparent. Embedded JPEG or PNG data and color profiles are not supported.
/// </summary>
internal static class BmpDecoder
{
    private const int BiRgb = 0, BiRle8 = 1, BiRle4 = 2, BiBitfields = 3, BiJpeg = 4, BiPng = 5, BiAlphaBitfields = 6;

    public static bool IsBmp(ReadOnlySpan<byte> data) =>
        data.Length >= 18 && data[0] == 'B' && data[1] == 'M'
        && BinaryPrimitives.ReadUInt32LittleEndian(data[14..]) is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124;

    public static DecodedImage Decode(byte[] data)
    {
        if (!IsBmp(data))
        {
            throw new ImageFormatException("Not a BMP file.");
        }

        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14));
        if (headerSize is 16 or 64)
        {
            throw new ImageFormatException("OS/2 2.x bitmaps are not supported.");
        }

        Require(data, 14, headerSize);
        var header = data.AsSpan(14, headerSize);
        int width, height, bitsPerPixel, compression = BiRgb, colorsUsed = 0;
        var topDown = false;
        uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;
        var paletteOffset = 14 + headerSize;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            compression = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header[16..]), int.MaxValue);
            colorsUsed = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header[32..]), int.MaxValue);
            topDown = rawHeight < 0;
            height = rawHeight == int.MinValue ? 0 : Math.Abs(rawHeight);
            if (compression is BiBitfields or BiAlphaBitfields)
            {
                // The masks come right after the BITMAPINFOHEADER fields: following a plain BITMAPINFOHEADER (then
                // before the palette), or inside later headers (V3 and later with an alpha mask).
                var inHeader = headerSize >= 52;
                var count = inHeader ? (headerSize >= 56 ? 4 : 3) : compression == BiAlphaBitfields ? 4 : 3;
                Require(data, 14 + 40, count * 4);
                var masks = data.AsSpan(14 + 40);
                redMask = BinaryPrimitives.ReadUInt32LittleEndian(masks);
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
                alphaMask = count == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(masks[12..]) : 0;
                if (!inHeader)
                {
                    paletteOffset += count * 4;
                }
            }
        }

        if (compression is BiJpeg or BiPng)
        {
            throw new ImageFormatException("BMP files with embedded JPEG or PNG data are not supported.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ImageFormatException("BMP size must be positive.");
        }

        ResourceLimits.CheckImagePixels(width, height);

        var valid = (bitsPerPixel, compression) switch
        {
            (1 or 2 or 4 or 8 or 24, BiRgb) => true,
            (16 or 32, BiRgb or BiBitfields or BiAlphaBitfields) => true,
            (8, BiRle8) or (4, BiRle4) => true,
            _ => false,
        };
        if (!valid)
        {
            throw new ImageFormatException($"Unsupported BMP format: {bitsPerPixel} bits per pixel with compression {compression}.");
        }

        if (pixelOffset < paletteOffset || pixelOffset > data.Length)
        {
            throw new ImageFormatException("Invalid BMP pixel data offset.");
        }

        if (bitsPerPixel <= 8)
        {
            var palette = ReadPalette(data, paletteOffset, (int)pixelOffset, headerSize == 12 ? 3 : 4, bitsPerPixel, colorsUsed);
            return compression == BiRgb
                ? DecodeIndexed(data, (int)pixelOffset, width, height, bitsPerPixel, topDown, palette)
                : DecodeRle(data, (int)pixelOffset, width, height, compression == BiRle4, topDown, palette);
        }

        if (compression == BiRgb)
        {
            if (bitsPerPixel == 24)
            {
                return DecodeRgb24(data, (int)pixelOffset, width, height, topDown);
            }

            // BI_RGB defaults: 5-5-5 for 16 bits; 8-8-8 with the high byte as possible alpha for 32 bits.
            (redMask, greenMask, blueMask, alphaMask) = bitsPerPixel == 16
                ? (0x7C00u, 0x03E0u, 0x001Fu, 0u)
                : (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
        }

        return DecodeBitfields(data, (int)pixelOffset, width, height, bitsPerPixel, topDown, redMask, greenMask, blueMask, alphaMask);
    }

    /// <summary>The color table as PDF RGB triples, padded with black to all 2^bits indices.</summary>
    private static byte[] ReadPalette(byte[] data, int offset, int end, int entrySize, int bitsPerPixel, int colorsUsed)
    {
        var size = 1 << bitsPerPixel;
        var count = colorsUsed > 0 && colorsUsed < size ? colorsUsed : size;
        count = Math.Min(count, (end - offset) / entrySize); // Some writers store fewer entries than they declare.
        if (count <= 0)
        {
            throw new ImageFormatException("The BMP has no color table.");
        }

        var palette = new byte[size * 3];
        for (var i = 0; i < count; i++)
        {
            var entry = offset + (i * entrySize);
            palette[i * 3] = data[entry + 2];
            palette[(i * 3) + 1] = data[entry + 1];
            palette[(i * 3) + 2] = data[entry];
        }

        return palette;
    }

    /// <summary>Uncompressed palette images: BMP packs indices most significant bit first, as PDF does.</summary>
    private static DecodedImage DecodeIndexed(byte[] data, int offset, int width, int height, int bitsPerPixel, bool topDown, byte[] palette)
    {
        var stride = Stride(width, bitsPerPixel);
        var rowBytes = (int)(((long)width * bitsPerPixel + 7) / 8);
        RequirePixels(data, offset, stride, rowBytes, height);
        var output = new byte[(long)rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(data, offset + (SourceRow(y, height, topDown) * stride), output, (long)y * rowBytes, rowBytes);
        }

        return new DecodedImage(width, height, ImageColorSpace.Indexed, bitsPerPixel, output, ImageEncoding.Raw) { Palette = palette };
    }

    private static DecodedImage DecodeRgb24(byte[] data, int offset, int width, int height, bool topDown)
    {
        var stride = Stride(width, 24);
        RequirePixels(data, offset, stride, width * 3, height);
        var output = new byte[(long)width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var src = offset + (SourceRow(y, height, topDown) * stride);
            var dst = (long)y * width * 3;
            for (var x = 0; x < width; x++, src += 3, dst += 3)
            {
                output[dst] = data[src + 2];
                output[dst + 1] = data[src + 1];
                output[dst + 2] = data[src];
            }
        }

        return new DecodedImage(width, height, ImageColorSpace.Rgb, 8, output, ImageEncoding.Raw);
    }

    /// <summary>16 and 32-bit pixels, each channel given by a contiguous bit mask and scaled to 8 bits.</summary>
    private static DecodedImage DecodeBitfields(byte[] data, int offset, int width, int height, int bitsPerPixel, bool topDown, uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        if (bitsPerPixel == 16)
        {
            (redMask, greenMask, blueMask, alphaMask) = (redMask & 0xFFFF, greenMask & 0xFFFF, blueMask & 0xFFFF, alphaMask & 0xFFFF);
        }

        var red = new Channel(redMask);
        var green = new Channel(greenMask);
        var blue = new Channel(blueMask);
        var alphaChannel = new Channel(alphaMask);
        var bytesPerPixel = bitsPerPixel / 8;
        var stride = Stride(width, bitsPerPixel);
        RequirePixels(data, offset, stride, width * bytesPerPixel, height);
        var output = new byte[(long)width * height * 3];
        var alpha = alphaMask != 0 ? new byte[(long)width * height] : null;
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var src = offset + (SourceRow(y, height, topDown) * stride);
            for (var x = 0; x < width; x++, src += bytesPerPixel)
            {
                var pixel = bytesPerPixel == 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(src))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(src));
                var i = ((long)y * width) + x;
                output[i * 3] = red.Extract(pixel);
                output[(i * 3) + 1] = green.Extract(pixel);
                output[(i * 3) + 2] = blue.Extract(pixel);
                if (alpha is not null)
                {
                    alpha[i] = alphaChannel.Extract(pixel);
                    anyAlpha |= alpha[i] != 0;
                }
            }
        }

        return new DecodedImage(width, height, ImageColorSpace.Rgb, 8, output, ImageEncoding.Raw)
        {
            Alpha = anyAlpha && alpha!.AsSpan().ContainsAnyExcept((byte)255) ? alpha : null,
        };
    }

    /// <summary>
    /// BI_RLE8 and BI_RLE4: runs of one index (two alternating nibbles for RLE4), absolute runs padded to 16 bits,
    /// end-of-line, end-of-bitmap and delta escapes. Data that ends early leaves the rest transparent, as in browsers.
    /// </summary>
    private static DecodedImage DecodeRle(byte[] data, int offset, int width, int height, bool rle4, bool topDown, byte[] palette)
    {
        var indices = new byte[(long)width * height];
        var alpha = new byte[(long)width * height];
        int x = 0, y = 0, pos = offset;

        void Put(int index)
        {
            if (x < width && y < height)
            {
                var row = topDown ? y : height - 1 - y;
                indices[((long)row * width) + x] = (byte)index;
                alpha[((long)row * width) + x] = 255;
            }

            x++;
        }

        while (pos + 2 <= data.Length && y < height)
        {
            int count = data[pos], value = data[pos + 1];
            pos += 2;
            if (count > 0)
            {
                for (var i = 0; i < count && x < width; i++)
                {
                    Put(!rle4 ? value : (i & 1) == 0 ? value >> 4 : value & 0x0F);
                }

                continue;
            }

            if (value == 0)
            {
                x = 0;
                y++;
            }
            else if (value == 1)
            {
                break;
            }
            else if (value == 2)
            {
                if (pos + 2 > data.Length)
                {
                    break;
                }

                x += data[pos];
                y += data[pos + 1];
                pos += 2;
            }
            else
            {
                var bytes = rle4 ? (value + 1) / 2 : value;
                if (pos + bytes > data.Length)
                {
                    break;
                }

                for (var i = 0; i < value && x < width; i++)
                {
                    Put(!rle4 ? data[pos + i] : (i & 1) == 0 ? data[pos + (i / 2)] >> 4 : data[pos + (i / 2)] & 0x0F);
                }

                pos += (bytes + 1) & ~1;
            }
        }

        return new DecodedImage(width, height, ImageColorSpace.Indexed, 8, indices, ImageEncoding.Raw)
        {
            Palette = palette,
            Alpha = alpha.AsSpan().ContainsAnyExcept((byte)255) ? alpha : null,
        };
    }

    private static int Stride(int width, int bitsPerPixel) => (int)((((long)width * bitsPerPixel) + 31) / 32 * 4);

    private static int SourceRow(int y, int height, bool topDown) => topDown ? y : height - 1 - y;

    private static void RequirePixels(byte[] data, int offset, int stride, int rowBytes, int height)
    {
        // The last row's padding may be missing.
        if (offset + (((long)stride * (height - 1)) + rowBytes) > data.Length)
        {
            throw new ImageFormatException("Truncated BMP pixel data.");
        }
    }

    private static void Require(byte[] data, int offset, int count)
    {
        if (count < 0 || offset + (long)count > data.Length)
        {
            throw new ImageFormatException("Truncated BMP header.");
        }
    }

    /// <summary>One channel of a bit-field pixel: a mask of up to 32 bits, scaled to 0-255.</summary>
    private readonly struct Channel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly int _bits;

        public Channel(uint mask)
        {
            _mask = mask;
            _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
            _bits = mask == 0 ? 0 : 32 - BitOperations.LeadingZeroCount(mask >> _shift); // Holes count as bits.
        }

        public byte Extract(uint pixel)
        {
            if (_bits == 0)
            {
                return 0;
            }

            var value = (pixel & _mask) >> _shift;
            return _bits >= 8
                ? (byte)(value >> (_bits - 8))
                : (byte)(((value * 255) + (((1u << _bits) - 1) / 2)) / ((1u << _bits) - 1));
        }
    }
}
