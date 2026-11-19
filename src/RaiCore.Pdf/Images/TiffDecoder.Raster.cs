using System.Buffers.Binary;
using System.IO.Compression;
using RaiCore.Pdf.Filters;

namespace RaiCore.Pdf.Images;

/// <summary>TIFF images decoded to samples: uncompressed, PackBits, LZW and Deflate.</summary>
internal static partial class TiffDecoder
{
    private static readonly byte[] ReversedBits = BuildReversedBits();

    private static DecodedImage Raster(TiffFile file, TiffDirectory directory, int width, int height, int compression, int samples, long bits, long photometric)
    {
        var colorSamples = photometric switch
        {
            0 or 1 or 3 => 1,
            2 => 3,
            5 when (directory.Number(InkSetTag) ?? 1) == 1 => 4,
            5 => throw new ImageFormatException("Only CMYK TIFF separations are supported."),
            6 => throw new ImageFormatException("TIFF YCbCr images are supported only with JPEG compression."),
            _ => throw new ImageFormatException($"TIFF photometric interpretation {photometric} is not supported."),
        };

        if (samples < colorSamples)
        {
            throw new ImageFormatException($"TIFF photometric interpretation {photometric} needs {colorSamples} samples per pixel, not {samples}.");
        }

        if (bits is not (1 or 2 or 4 or 8 or 16) || (photometric == 3 && bits > 8))
        {
            throw new ImageFormatException($"{bits}-bit TIFF {(photometric == 3 ? "palette " : string.Empty)}images are not supported.");
        }

        var predictor = directory.Number(PredictorTag) ?? 1;
        if (predictor != 1 && !(predictor == 2 && bits is 8 or 16))
        {
            throw new ImageFormatException($"TIFF predictor {predictor} with {bits}-bit samples is not supported.");
        }

        var depth = (int)bits;
        var planes = directory.Number(PlanarConfigurationTag) == 2 ? samples : 1;
        var layout = ReadLayout(directory, width, height, planes);
        var segmentSamples = planes == 1 ? samples : 1;
        var segmentRowBits = (long)layout.Width * segmentSamples * depth;
        if (layout.Across > 1 && segmentRowBits % 8 != 0)
        {
            throw new ImageFormatException("TIFF tiles whose rows do not end on a byte boundary are not supported.");
        }

        var segmentRowBytes = (int)((segmentRowBits + 7) / 8);
        var rowBytes = (int)((((long)width * samples * depth) + 7) / 8);
        if ((long)rowBytes * height > Array.MaxLength || (long)segmentRowBytes * layout.Height > Array.MaxLength)
        {
            throw new ImageFormatException("The TIFF image is too large to decode.");
        }

        CheckDataSize(file, layout, compression, layout.Across * layout.Down * planes, (long)segmentRowBytes * height * layout.Across * planes);
        var image = new byte[(long)rowBytes * height];
        var buffer = new byte[(long)segmentRowBytes * layout.Height];
        var reverse = directory.Number(FillOrderTag) == 2 && compression is 1 or 32773;
        for (var plane = 0; plane < planes; plane++)
        {
            for (var down = 0; down < layout.Down; down++)
            {
                var top = down * layout.Height;
                var rows = Math.Min(layout.Height, height - top); // Rows of tiles below the image are not needed.
                for (var across = 0; across < layout.Across; across++)
                {
                    var index = (((plane * layout.Down) + down) * layout.Across) + across;
                    var raw = Segment(file, layout, index);
                    if (raw.IsEmpty)
                    {
                        continue; // Sparse: left zero.
                    }

                    var segment = buffer.AsSpan(0, rows * segmentRowBytes);
                    Decompress(raw, compression, reverse, segment, layout.Kind, index);
                    if (depth == 16 || predictor == 2)
                    {
                        Normalize(segment, rows, segmentRowBytes, layout.Width * segmentSamples, segmentSamples, depth, predictor == 2, file.LittleEndian);
                    }

                    var left = across * layout.Width;
                    var columns = Math.Min(layout.Width, width - left);
                    for (var row = 0; row < rows && top + row < height; row++)
                    {
                        var source = segment.Slice(row * segmentRowBytes, segmentRowBytes);
                        var target = image.AsSpan((top + row) * rowBytes, rowBytes);
                        if (planes == 1)
                        {
                            var start = (int)((long)left * samples * depth / 8);
                            source[..Math.Min(segmentRowBytes, rowBytes - start)].CopyTo(target[start..]);
                        }
                        else
                        {
                            for (var x = 0; x < columns; x++)
                            {
                                SetSample(target, ((long)(left + x) * samples) + plane, depth, GetSample(source, x, depth));
                            }
                        }
                    }
                }
            }
        }

        var (color, alpha) = SplitExtraSamples(directory, image, width, height, samples, colorSamples, depth, associatedAlphaApplies: photometric != 3);
        if (photometric == 0)
        {
            // WhiteIsZero: PDF's gray is black at 0.
            foreach (ref var b in color.AsSpan())
            {
                b = (byte)~b;
            }
        }

        var colorSpace = photometric switch
        {
            2 => ImageColorSpace.Rgb,
            3 => ImageColorSpace.Indexed,
            5 => ImageColorSpace.Cmyk,
            _ => ImageColorSpace.Gray,
        };

        return new DecodedImage(width, height, colorSpace, depth, color, ImageEncoding.Raw)
        {
            Palette = photometric == 3 ? Palette(directory, depth) : null,
            Alpha = alpha,
        };
    }

    /// <summary>
    /// Separates the color samples from extra samples: the first extra sample, if it is alpha, becomes an 8-bit mask
    /// (null if fully opaque), and associated (premultiplied) alpha is divided out of the color. Other extra samples are dropped.
    /// </summary>
    private static (byte[] Color, byte[]? Alpha) SplitExtraSamples(TiffDirectory directory, byte[] image, int width, int height, int samples, int colorSamples, int depth, bool associatedAlphaApplies)
    {
        if (samples == colorSamples)
        {
            return (image, null);
        }

        var kind = directory.Numbers(ExtraSamplesTag, 1) is [var first] ? first : 0;
        var hasAlpha = kind is 1 or 2;
        var premultiplied = kind == 1 && associatedAlphaApplies;
        var rowBytes = (int)((((long)width * samples * depth) + 7) / 8);
        var colorRowBytes = (int)((((long)width * colorSamples * depth) + 7) / 8);
        var color = new byte[(long)colorRowBytes * height];
        var alpha = hasAlpha ? new byte[(long)width * height] : null;
        var max = (1 << depth) - 1;
        var opaque = true;
        for (var y = 0; y < height; y++)
        {
            var source = image.AsSpan(y * rowBytes, rowBytes);
            var target = color.AsSpan(y * colorRowBytes, colorRowBytes);
            for (var x = 0; x < width; x++)
            {
                var pixel = (long)x * samples;
                var a = hasAlpha ? GetSample(source, pixel + colorSamples, depth) : max;
                for (var c = 0; c < colorSamples; c++)
                {
                    var value = GetSample(source, pixel + c, depth);
                    if (premultiplied)
                    {
                        value = a == 0 ? 0 : (int)Math.Min(max, (((long)value * max) + (a / 2)) / a);
                    }

                    SetSample(target, ((long)x * colorSamples) + c, depth, value);
                }

                if (alpha is not null)
                {
                    var mask = depth == 16 ? a >> 8 : a * 255 / max;
                    alpha[((long)y * width) + x] = (byte)mask;
                    opaque &= mask == 255;
                }
            }
        }

        return (color, opaque ? null : alpha);
    }

    /// <summary>The RGB palette of a palette image, from its ColorMap (16-bit values; 8-bit ones are accepted, as libtiff's tools do).</summary>
    private static byte[] Palette(TiffDirectory directory, int depth)
    {
        var entries = 1 << depth;
        var map = directory.Numbers(ColorMapTag, 3L * entries);
        if (map is null || map.Length < 3 * entries)
        {
            throw new ImageFormatException("The palette TIFF has no complete color map.");
        }

        var eightBit = map.All(v => v is >= 0 and < 256);
        var palette = new byte[3 * entries];
        for (var i = 0; i < entries; i++)
        {
            for (var c = 0; c < 3; c++)
            {
                var value = Math.Clamp(map[(c * entries) + i], 0, 65535);
                palette[(i * 3) + c] = (byte)(eightBit ? value : value >> 8);
            }
        }

        return palette;
    }

    /// <summary>
    /// Throws unless the strips or tiles hold enough data to decompress to <paramref name="needed"/> bytes, at the
    /// highest ratio the compression can reach: so that a small file cannot make the decoder allocate a huge image
    /// before its data turns out to be missing.
    /// </summary>
    private static void CheckDataSize(TiffFile file, Layout layout, int compression, int segments, long needed)
    {
        var available = 0L;
        for (var i = 0; i < segments; i++)
        {
            var offset = layout.Offsets[i];
            if (offset >= 0 && offset < file.Data.Length)
            {
                available += Math.Clamp(layout.Counts?[i] ?? file.Data.Length - offset, 0, file.Data.Length - offset);
            }
        }

        // PackBits: 2 bytes give at most 128; Deflate at most about 1032 to 1; LZW at most 4096 bytes per 12-bit code.
        var ratio = compression switch { 1 => 1, 32773 => 64, 5 => 2731, _ => 1032 };
        if (needed > (available * ratio) + 4096)
        {
            throw new ImageFormatException($"The TIFF's image data ({available} bytes) is too short for its {needed} bytes of samples.");
        }
    }

    /// <summary>Decompresses one strip or tile into <paramref name="output"/>, which it must fill.</summary>
    private static void Decompress(ReadOnlySpan<byte> raw, int compression, bool reverseBits, Span<byte> output, string kind, int index)
    {
        if (reverseBits)
        {
            raw = ReverseBits(raw);
        }

        var written = compression switch
        {
            1 => Copy(raw, output),
            32773 => PackBits(raw, output),
            5 => Lzw(raw, output),
            _ => Inflate(raw, output),
        };

        if (written < output.Length)
        {
            throw new ImageFormatException($"TIFF {kind} {index} has {written} bytes of image data; its rows need {output.Length}.");
        }
    }

    private static int Copy(ReadOnlySpan<byte> raw, Span<byte> output)
    {
        var count = Math.Min(raw.Length, output.Length);
        raw[..count].CopyTo(output);
        return count;
    }

    /// <summary>PackBits (Apple's run-length coding): a count byte n, then n + 1 literal bytes (n &gt;= 0) or one byte repeated 1 - n times (n &lt; 0, but not -128).</summary>
    private static int PackBits(ReadOnlySpan<byte> raw, Span<byte> output)
    {
        int i = 0, written = 0;
        while (written < output.Length && i < raw.Length)
        {
            int n = (sbyte)raw[i++];
            if (n >= 0)
            {
                var count = Math.Min(Math.Min(n + 1, raw.Length - i), output.Length - written);
                raw.Slice(i, count).CopyTo(output[written..]);
                i += n + 1;
                written += count;
            }
            else if (n != -128 && i < raw.Length)
            {
                var count = Math.Min(1 - n, output.Length - written);
                output.Slice(written, count).Fill(raw[i++]);
                written += count;
            }
        }

        return written;
    }

    private static int Lzw(ReadOnlySpan<byte> raw, Span<byte> output)
    {
        if (raw.Length >= 2 && raw[0] == 0 && (raw[1] & 1) != 0)
        {
            // Codes stored least significant bit first, written by libtiff before 1991 (version 4).
            throw new ImageFormatException("Old-style TIFF LZW data is not supported.");
        }

        var decoded = StreamDecoder.Lzw(raw.ToArray(), earlyChange: 1, maxLength: output.Length);
        return Copy(decoded, output);
    }

    private static int Inflate(ReadOnlySpan<byte> raw, Span<byte> output)
    {
        using var input = new MemoryStream(raw.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var written = 0;
        try
        {
            int read;
            while (written < output.Length && (read = zlib.Read(output[written..])) > 0)
            {
                written += read;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) // ZLibException is an IOException.
        {
            throw new ImageFormatException("Corrupt TIFF Deflate data.", ex);
        }

        return written;
    }

    /// <summary>
    /// Undoes the horizontal differencing predictor (each sample stored as the difference from the same sample of
    /// the pixel to its left) and puts 16-bit samples in PDF's big-endian order.
    /// </summary>
    private static void Normalize(Span<byte> segment, int rows, int rowBytes, int rowSamples, int stride, int depth, bool predictor, bool littleEndian)
    {
        for (var y = 0; y < rows; y++)
        {
            var row = segment.Slice(y * rowBytes, rowBytes);
            if (depth == 8)
            {
                for (var i = stride; i < rowSamples; i++)
                {
                    row[i] += row[i - stride];
                }

                continue;
            }

            for (var i = 0; i < rowSamples; i++)
            {
                var at = row.Slice(i * 2, 2);
                var value = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at);
                if (predictor && i >= stride)
                {
                    value += BinaryPrimitives.ReadUInt16BigEndian(row[((i - stride) * 2)..]);
                }

                BinaryPrimitives.WriteUInt16BigEndian(at, value);
            }
        }
    }

    /// <summary>Sample <paramref name="index"/> of a row of packed samples (most significant bits first; 16-bit big-endian).</summary>
    private static int GetSample(ReadOnlySpan<byte> row, long index, int depth) => depth switch
    {
        8 => row[(int)index],
        16 => BinaryPrimitives.ReadUInt16BigEndian(row[(int)(index * 2)..]),
        _ => (row[(int)(index * depth / 8)] >> (8 - (int)(index * depth % 8) - depth)) & ((1 << depth) - 1),
    };

    private static void SetSample(Span<byte> row, long index, int depth, int value)
    {
        switch (depth)
        {
            case 8:
                row[(int)index] = (byte)value;
                break;
            case 16:
                BinaryPrimitives.WriteUInt16BigEndian(row[(int)(index * 2)..], (ushort)value);
                break;
            default:
                var shift = 8 - (int)(index * depth % 8) - depth;
                ref var b = ref row[(int)(index * depth / 8)];
                b = (byte)((b & ~(((1 << depth) - 1) << shift)) | (value << shift));
                break;
        }
    }

    /// <summary>The data with the bits of each byte reversed (FillOrder 2: least significant bit first).</summary>
    private static byte[] ReverseBits(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = ReversedBits[data[i]];
        }

        return result;
    }

    private static byte[] BuildReversedBits()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var reversed = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                reversed |= ((i >> bit) & 1) << (7 - bit);
            }

            table[i] = (byte)reversed;
        }

        return table;
    }
}
