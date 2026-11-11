using System.Buffers.Binary;
using System.IO.Compression;

namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes PNG (ISO/IEC 15948): all color types and bit depths, Adam7 interlacing and all filter types.
/// Transparency becomes a color-key mask (gray/RGB tRNS) or an 8-bit soft mask (alpha channel, palette tRNS).
/// Color management chunks (gAMA, iCCP, sRGB) are ignored: samples are treated as sRGB, as browsers mostly do.
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    public static DecodedImage Decode(byte[] data)
    {
        if (!IsPng(data))
        {
            throw new ImageFormatException("Not a PNG file.");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();
        var pos = 8;
        var seenHeader = false;
        var seenEnd = false;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > int.MaxValue || pos + 12 + (long)length > data.Length)
            {
                throw new ImageFormatException("Truncated PNG chunk.");
            }

            var type = data.AsSpan(pos + 4, 4);
            var body = data.AsSpan(pos + 8, (int)length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8 + (int)length));
            if (crc != Crc(data.AsSpan(pos + 4, 4 + (int)length)))
            {
                throw new ImageFormatException($"PNG chunk '{System.Text.Encoding.ASCII.GetString(type)}' has a bad CRC.");
            }

            if (!seenHeader && !type.SequenceEqual("IHDR"u8))
            {
                throw new ImageFormatException("The first PNG chunk must be IHDR.");
            }

            switch (System.Text.Encoding.ASCII.GetString(type))
            {
                case "IHDR":
                    if (body.Length != 13)
                    {
                        throw new ImageFormatException("Invalid IHDR chunk.");
                    }

                    width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue);
                    height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
                    bitDepth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    if (body[10] != 0 || body[11] != 0 || interlace > 1)
                    {
                        throw new ImageFormatException("Unsupported PNG compression, filter or interlace method.");
                    }

                    seenHeader = true;
                    break;
                case "PLTE":
                    if (body.Length % 3 != 0 || body.Length == 0 || body.Length > 768)
                    {
                        throw new ImageFormatException("Invalid PLTE chunk.");
                    }

                    palette = body.ToArray();
                    break;
                case "tRNS":
                    transparency = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    seenEnd = true;
                    break;
                default:
                    if ((type[0] & 0x20) == 0)
                    {
                        throw new ImageFormatException($"Unknown critical PNG chunk '{System.Text.Encoding.ASCII.GetString(type)}'.");
                    }

                    break; // Ancillary chunk we do not need.
            }

            pos += 12 + (int)length;
            if (seenEnd)
            {
                break;
            }
        }

        if (!seenHeader || idat.Length == 0)
        {
            throw new ImageFormatException("The PNG has no header or no image data.");
        }

        ValidateHeader(width, height, bitDepth, colorType);
        if (colorType == 3 && palette is null)
        {
            throw new ImageFormatException("Palette PNG without a PLTE chunk.");
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        var bitsPerPixel = channels * bitDepth;
        // The filtered rows need at most this much (interlaced passes add a filter byte per pass row); more is ignored,
        // so a small file cannot inflate to gigabytes.
        var rowBytes = (((long)width * bitsPerPixel) + 7) / 8;
        var raw = Inflate(idat.ToArray(), ((rowBytes + 1) * height) + (7L * (height + 8)) + 64);
        var image = interlace == 0
            ? Unfilter(raw, 0, width, height, bitsPerPixel, out _)
            : Deinterlace(raw, width, height, bitsPerPixel);

        return colorType switch
        {
            0 => new DecodedImage(width, height, ImageColorSpace.Gray, bitDepth, image, ImageEncoding.Raw)
            {
                ColorKey = transparency is { Length: >= 2 } t ? Key(t, 1, bitDepth) : null,
            },
            2 => new DecodedImage(width, height, ImageColorSpace.Rgb, bitDepth, image, ImageEncoding.Raw)
            {
                ColorKey = transparency is { Length: >= 6 } t ? Key(t, 3, bitDepth) : null,
            },
            3 => new DecodedImage(width, height, ImageColorSpace.Indexed, bitDepth, image, ImageEncoding.Raw)
            {
                Palette = palette,
                Alpha = transparency is { Length: > 0 } t && t.Any(a => a != 255) ? PaletteAlpha(image, width, height, bitDepth, t) : null,
            },
            _ => SplitAlpha(image, width, height, colorType == 4 ? 1 : 3, bitDepth),
        };
    }

    private static void ValidateHeader(int width, int height, int bitDepth, int colorType)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageFormatException("PNG size must be positive.");
        }

        ResourceLimits.CheckImagePixels(width, height);

        var valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => bitDepth is 8 or 16,
            _ => false,
        };
        if (!valid)
        {
            throw new ImageFormatException($"Invalid PNG color type {colorType} with bit depth {bitDepth}.");
        }
    }

    private static byte[] Inflate(byte[] compressed, long maxLength)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while (output.Length < maxLength && (read = zlib.Read(buffer, 0, (int)Math.Min(buffer.Length, maxLength - output.Length))) > 0)
            {
                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new ImageFormatException("Corrupt PNG image data.", ex);
        }
    }

    /// <summary>Reverses the per-row filters of one (sub)image starting at <paramref name="offset"/>; returns packed rows.</summary>
    private static byte[] Unfilter(byte[] raw, int offset, int width, int height, int bitsPerPixel, out int consumed)
    {
        var rowBytes = (int)(((long)width * bitsPerPixel + 7) / 8);
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var output = new byte[(long)rowBytes * height];
        if ((long)offset + ((long)(rowBytes + 1) * height) > raw.Length)
        {
            throw new ImageFormatException("PNG image data is too short.");
        }

        var src = offset;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[src++];
            var row = output.AsSpan(y * rowBytes, rowBytes);
            var previous = y > 0 ? output.AsSpan((y - 1) * rowBytes, rowBytes) : default;
            raw.AsSpan(src, rowBytes).CopyTo(row);
            src += rowBytes;
            switch (filter)
            {
                case 0:
                    break;
                case 1: // Sub
                    for (var i = bpp; i < rowBytes; i++)
                    {
                        row[i] += row[i - bpp];
                    }

                    break;
                case 2: // Up
                    if (y > 0)
                    {
                        for (var i = 0; i < rowBytes; i++)
                        {
                            row[i] += previous[i];
                        }
                    }

                    break;
                case 3: // Average
                    for (var i = 0; i < rowBytes; i++)
                    {
                        var left = i >= bpp ? row[i - bpp] : 0;
                        var up = y > 0 ? previous[i] : 0;
                        row[i] += (byte)((left + up) >> 1);
                    }

                    break;
                case 4: // Paeth
                    for (var i = 0; i < rowBytes; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = y > 0 ? previous[i] : 0;
                        int c = i >= bpp && y > 0 ? previous[i - bpp] : 0;
                        var p = a + b - c;
                        var pa = Math.Abs(p - a);
                        var pb = Math.Abs(p - b);
                        var pc = Math.Abs(p - c);
                        row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }

                    break;
                default:
                    throw new ImageFormatException($"Invalid PNG filter type {filter}.");
            }
        }

        consumed = src - offset;
        return output;
    }

    /// <summary>Decodes the seven Adam7 passes and places their pixels in a full image (bit-exact for packed depths).</summary>
    private static byte[] Deinterlace(byte[] raw, int width, int height, int bitsPerPixel)
    {
        ReadOnlySpan<int> xStart = [0, 4, 0, 2, 0, 1, 0];
        ReadOnlySpan<int> yStart = [0, 0, 4, 0, 2, 0, 1];
        ReadOnlySpan<int> xStep = [8, 8, 4, 4, 2, 2, 1];
        ReadOnlySpan<int> yStep = [8, 8, 8, 4, 4, 2, 2];
        var rowBytes = (int)(((long)width * bitsPerPixel + 7) / 8);
        var output = new byte[(long)rowBytes * height];
        var offset = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            var passWidth = (width - xStart[pass] + xStep[pass] - 1) / xStep[pass];
            var passHeight = (height - yStart[pass] + yStep[pass] - 1) / yStep[pass];
            if (passWidth <= 0 || passHeight <= 0)
            {
                continue;
            }

            var sub = Unfilter(raw, offset, passWidth, passHeight, bitsPerPixel, out var consumed);
            offset += consumed;
            var subRowBytes = ((passWidth * bitsPerPixel) + 7) / 8;
            for (var y = 0; y < passHeight; y++)
            {
                for (var x = 0; x < passWidth; x++)
                {
                    CopyPixel(sub, (y * subRowBytes * 8L) + ((long)x * bitsPerPixel),
                        output, ((long)(yStart[pass] + (y * yStep[pass])) * rowBytes * 8) + ((long)(xStart[pass] + (x * xStep[pass])) * bitsPerPixel),
                        bitsPerPixel);
                }
            }
        }

        return output;
    }

    private static void CopyPixel(byte[] source, long sourceBit, byte[] target, long targetBit, int bits)
    {
        if (bits % 8 == 0)
        {
            Array.Copy(source, sourceBit / 8, target, targetBit / 8, bits / 8);
            return;
        }

        // 1, 2 or 4 bits: within one byte, most significant bits first.
        var value = (source[sourceBit / 8] >> (8 - (int)(sourceBit % 8) - bits)) & ((1 << bits) - 1);
        var shift = 8 - (int)(targetBit % 8) - bits;
        target[targetBit / 8] = (byte)((target[targetBit / 8] & ~(((1 << bits) - 1) << shift)) | (value << shift));
    }

    /// <summary>Separates interleaved color and alpha samples (gray+alpha or RGBA) into color data and an 8-bit alpha mask.</summary>
    private static DecodedImage SplitAlpha(byte[] image, int width, int height, int colorChannels, int bitDepth)
    {
        var bytesPerSample = bitDepth / 8;
        var pixelBytes = (colorChannels + 1) * bytesPerSample;
        var color = new byte[(long)width * height * colorChannels * bytesPerSample];
        var alpha = new byte[(long)width * height];
        for (long p = 0, c = 0; p < (long)width * height; p++)
        {
            var src = p * pixelBytes;
            for (var i = 0; i < colorChannels * bytesPerSample; i++)
            {
                color[c++] = image[src + i];
            }

            alpha[p] = image[src + (colorChannels * bytesPerSample)]; // For 16-bit, the high byte.
        }

        return new DecodedImage(width, height, colorChannels == 1 ? ImageColorSpace.Gray : ImageColorSpace.Rgb, bitDepth, color, ImageEncoding.Raw)
        {
            Alpha = alpha.All(a => a == 255) ? null : alpha,
        };
    }

    private static byte[] PaletteAlpha(byte[] image, int width, int height, int bitDepth, byte[] paletteAlpha)
    {
        var rowBytes = ((width * bitDepth) + 7) / 8;
        var alpha = new byte[(long)width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bit = (x * bitDepth) % 8;
                var index = (image[(y * rowBytes) + (x * bitDepth / 8)] >> (8 - bit - bitDepth)) & ((1 << bitDepth) - 1);
                alpha[((long)y * width) + x] = index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
            }
        }

        return alpha;
    }

    /// <summary>tRNS for gray/RGB: one exact sample value per component, as a PDF color-key range.</summary>
    private static int[] Key(byte[] trns, int components, int bitDepth)
    {
        var key = new int[components * 2];
        var mask = (1 << bitDepth) - 1;
        for (var i = 0; i < components; i++)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(i * 2)) & mask;
            key[i * 2] = value;
            key[(i * 2) + 1] = value;
        }

        return key;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
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
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
