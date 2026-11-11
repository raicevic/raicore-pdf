using System.IO.Compression;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Filters;

/// <summary>
/// Decodes the data of streams read from a file (ISO 32000-2, section 7.4): <c>/FlateDecode</c> and <c>/LZWDecode</c>
/// (with PNG and TIFF predictors), <c>/ASCIIHexDecode</c>, <c>/ASCII85Decode</c> and <c>/RunLengthDecode</c>. Image
/// codecs (<c>/DCTDecode</c>, <c>/JPXDecode</c>, <c>/JBIG2Decode</c>, <c>/CCITTFaxDecode</c>) are not decoded:
/// imported images keep their data as it is. Damaged data decodes as far as it goes, as viewers do. Output larger than
/// <see cref="MaxDecodedLength"/> is an error, so a small hostile stream cannot exhaust memory.
/// </summary>
internal static class StreamDecoder
{
    /// <summary>The largest decoded stream accepted (256 MB).</summary>
    public const int MaxDecodedLength = 256 * 1024 * 1024;

    /// <summary>The most filters one stream may chain.</summary>
    private const int MaxFilters = 16;

    /// <summary>Decodes a stream's data, applying its filters in order.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="resolve">Resolves indirect references in the stream dictionary.</param>
    /// <exception cref="PdfFormatException">A filter is unknown or not decodable, or the data is too large.</exception>
    public static byte[] Decode(PdfStreamObject stream, Func<PdfObject?, PdfObject?> resolve)
    {
        var filters = resolve(stream.Dictionary["Filter"]) switch
        {
            PdfName name => [name],
            PdfArray array => array.Items.Select(resolve).ToList(),
            _ => [],
        };

        if (filters.Count > MaxFilters)
        {
            throw new PdfFormatException($"A stream has {filters.Count} filters; at most {MaxFilters} are supported.");
        }

        var parameters = resolve(stream.Dictionary["DecodeParms"] ?? stream.Dictionary["DP"]);
        var data = stream.Data;
        for (var i = 0; i < filters.Count; i++)
        {
            var parms = parameters switch
            {
                PdfDictionary dictionary => dictionary,
                PdfArray array when i < array.Items.Count => resolve(array.Items[i]) as PdfDictionary,
                _ => null,
            };

            var name = (filters[i] as PdfName)?.Value;
            data = name switch
            {
                "FlateDecode" or "Fl" => Predict(Inflate(data), parms, resolve),
                "LZWDecode" or "LZW" => Predict(Lzw(data, Integer(parms, "EarlyChange", resolve) ?? 1), parms, resolve),
                "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                "ASCII85Decode" or "A85" => Ascii85(data),
                "RunLengthDecode" or "RL" => RunLength(data),
                "Crypt" when resolve(parms?["Name"]) is null or PdfName { Value: "Identity" } => data,
                _ => throw new PdfFormatException($"The stream filter /{name ?? "?"} cannot be decoded."),
            };
        }

        return data;
    }

    /// <summary>
    /// Inflates zlib data. Damaged or truncated data gives what was decoded before the damage; data without the zlib
    /// header (raw deflate, written by some producers) is accepted too.
    /// </summary>
    internal static byte[] Inflate(byte[] data)
    {
        var result = TryInflate(data, raw: false);
        if (result.Length == 0 && data.Length > 0)
        {
            result = TryInflate(data, raw: true);
        }

        return result;
    }

    private static byte[] TryInflate(byte[] data, bool raw)
    {
        using var output = new MemoryStream();
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using Stream inflater = raw
                ? new DeflateStream(input, CompressionMode.Decompress)
                : new ZLibStream(input, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int read;
            while ((read = inflater.Read(buffer)) > 0)
            {
                CheckLength(output.Length + read);
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            // Keep what was decoded before the damage.
        }

        return output.ToArray();
    }

    /// <summary>Undoes a PNG (10-15) or TIFF (2) predictor (ISO 32000-2, 7.4.4.4).</summary>
    private static byte[] Predict(byte[] data, PdfDictionary? parms, Func<PdfObject?, PdfObject?> resolve)
    {
        var predictor = Integer(parms, "Predictor", resolve) ?? 1;
        if (predictor <= 1)
        {
            return data;
        }

        var colors = Integer(parms, "Colors", resolve) ?? 1;
        var bits = Integer(parms, "BitsPerComponent", resolve) ?? 8;
        var columns = Integer(parms, "Columns", resolve) ?? 1;
        if (colors is < 1 or > 32 || bits is not (1 or 2 or 4 or 8 or 16) || columns is < 1 or > (1 << 24))
        {
            throw new PdfFormatException("A stream has invalid predictor parameters.");
        }

        var bytesPerPixel = Math.Max(1, (int)(colors * bits / 8));
        var rowLength = (int)(((colors * bits * columns) + 7) / 8);
        if (predictor == 2)
        {
            if (bits != 8)
            {
                throw new PdfFormatException("TIFF predictors are only supported for 8 bits per component.");
            }

            var output = (byte[])data.Clone();
            for (var row = 0; row < output.Length; row += rowLength)
            {
                var end = Math.Min(row + rowLength, output.Length);
                for (var i = row + bytesPerPixel; i < end; i++)
                {
                    output[i] = (byte)(output[i] + output[i - bytesPerPixel]);
                }
            }

            return output;
        }

        // PNG: each row starts with its own filter type byte.
        var rows = data.Length / (rowLength + 1);
        if (rows == 0)
        {
            return [];
        }

        var result = new byte[rows * rowLength];
        var previous = new byte[rowLength];
        for (var row = 0; row < rows; row++)
        {
            var source = data.AsSpan(row * (rowLength + 1), rowLength + 1);
            var filter = source[0];
            var line = result.AsSpan(row * rowLength, rowLength);
            source[1..].CopyTo(line);
            for (var i = 0; i < rowLength; i++)
            {
                var left = i >= bytesPerPixel ? line[i - bytesPerPixel] : 0;
                var up = previous[i];
                var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                line[i] = filter switch
                {
                    1 => (byte)(line[i] + left),
                    2 => (byte)(line[i] + up),
                    3 => (byte)(line[i] + ((left + up) / 2)),
                    4 => (byte)(line[i] + Paeth(left, up, upLeft)),
                    _ => line[i],
                };
            }

            line.CopyTo(previous);
        }

        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Decodes LZW data (9 to 12-bit codes; <paramref name="earlyChange"/> 1 widens codes one entry early, as TIFF does).</summary>
    internal static byte[] Lzw(byte[] data, long earlyChange)
    {
        const int Clear = 256;
        const int EndOfData = 257;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var lengths = new int[4096];
        for (var i = 0; i < 256; i++)
        {
            suffix[i] = (byte)i;
            lengths[i] = 1;
            prefix[i] = -1;
        }

        var early = earlyChange == 0 ? 0 : 1;
        var output = new List<byte>(data.Length * 3);
        var next = 258;
        var width = 9;
        var previous = -1;
        var bitBuffer = 0L;
        var bitCount = 0;
        var position = 0;
        var entry = new byte[4096];
        while (true)
        {
            while (bitCount < width && position < data.Length)
            {
                bitBuffer = (bitBuffer << 8) | data[position++];
                bitCount += 8;
            }

            if (bitCount < width)
            {
                break;
            }

            var code = (int)((bitBuffer >> (bitCount - width)) & ((1 << width) - 1));
            bitCount -= width;
            if (code == Clear)
            {
                next = 258;
                width = 9;
                previous = -1;
                continue;
            }

            if (code == EndOfData)
            {
                break;
            }

            int length;
            if (code < next && (code < 256 || lengths[code] > 0))
            {
                length = Write(code, entry);
                if (previous >= 0 && next < 4096)
                {
                    Add(previous, entry[0]);
                }
            }
            else if (code == next && previous >= 0)
            {
                // The code being defined: the previous entry plus its own first byte.
                var previousLength = Write(previous, entry);
                entry[previousLength] = entry[0];
                length = previousLength + 1;
                if (next < 4096)
                {
                    Add(previous, entry[0]);
                }
            }
            else
            {
                break; // An invalid code: the data is damaged.
            }

            CheckLength(output.Count + length);
            output.AddRange(entry.AsSpan(0, length));
            previous = code;
            if (next + early >= (1 << width) && width < 12)
            {
                width++;
            }
        }

        return [.. output];

        int Write(int code, byte[] target)
        {
            var length = lengths[code];
            for (var i = length - 1; i >= 0; i--)
            {
                target[i] = suffix[code];
                code = prefix[code];
            }

            return length;
        }

        void Add(int previousCode, byte first)
        {
            if (lengths[previousCode] + 1 > 4095)
            {
                return;
            }

            prefix[next] = previousCode;
            suffix[next] = first;
            lengths[next] = lengths[previousCode] + 1;
            next++;
        }
    }

    internal static byte[] AsciiHex(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var b in data)
        {
            if (b == '>')
            {
                break;
            }

            if (!Reading.PdfLexer.TryHex(b, out var digit))
            {
                continue; // Whitespace (and anything else) is ignored.
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                output.Add((byte)((high << 4) | digit));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return [.. output];
    }

    internal static byte[] Ascii85(byte[] data)
    {
        var output = new List<byte>(data.Length);
        Span<int> group = stackalloc int[5];
        var count = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '~')
            {
                break;
            }

            if (Reading.PdfLexer.IsWhitespace(b))
            {
                continue;
            }

            if (b == 'z' && count == 0)
            {
                output.AddRange([0, 0, 0, 0]);
                continue;
            }

            if (b is < (byte)'!' or > (byte)'u')
            {
                break; // Not ASCII85: the data is damaged.
            }

            group[count++] = b - '!';
            if (count == 5)
            {
                Emit(group, 4);
                count = 0;
            }
        }

        if (count > 1)
        {
            // A final partial group is padded with 'u' (84), and gives one byte fewer than its characters.
            for (var i = count; i < 5; i++)
            {
                group[i] = 84;
            }

            Emit(group, count - 1);
        }

        return [.. output];

        void Emit(Span<int> digits, int bytes)
        {
            long value = 0;
            foreach (var digit in digits)
            {
                value = (value * 85) + digit;
            }

            for (var i = 0; i < bytes; i++)
            {
                output.Add((byte)(value >> (24 - (8 * i))));
            }
        }
    }

    internal static byte[] RunLength(byte[] data)
    {
        var output = new List<byte>(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                var count = Math.Min(length + 1, data.Length - i);
                CheckLength(output.Count + count);
                output.AddRange(data.AsSpan(i, count));
                i += count;
            }
            else if (i < data.Length)
            {
                CheckLength(output.Count + 257 - length);
                for (var n = 0; n < 257 - length; n++)
                {
                    output.Add(data[i]);
                }

                i++;
            }
        }

        return [.. output];
    }

    private static long? Integer(PdfDictionary? dictionary, string key, Func<PdfObject?, PdfObject?> resolve) =>
        resolve(dictionary?[key]) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => (long)real.Value,
            _ => null,
        };

    private static void CheckLength(long length)
    {
        if (length > MaxDecodedLength)
        {
            throw new PdfFormatException($"A stream decodes to more than {MaxDecodedLength / (1024 * 1024)} MB.");
        }
    }
}
