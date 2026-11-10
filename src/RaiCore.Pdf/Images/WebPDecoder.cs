using System.Buffers.Binary;

namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes WebP: the RIFF container with simple lossy (VP8) or lossless (VP8L) data, and the extended format (VP8X)
/// with an ALPH chunk (raw or lossless-compressed alpha, with its prediction filters). For an animation, the first
/// frame is decoded onto the canvas; areas outside it are transparent. Metadata chunks (ICCP, EXIF, XMP) are ignored.
/// </summary>
internal static class WebPDecoder
{
    private const int AnimationFlag = 0x02;

    public static bool IsWebP(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    public static DecodedImage Decode(byte[] data)
    {
        if (!IsWebP(data))
        {
            throw new ImageFormatException("Not a WebP file.");
        }

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        if (riffSize < 12)
        {
            throw new ImageFormatException("Invalid WebP file size.");
        }

        if (riffSize > data.Length - 8L)
        {
            throw new ImageFormatException("Truncated WebP file.");
        }

        var end = 8 + (int)riffSize;
        var pos = 12;
        var first = ReadChunk(data, ref pos, end);
        switch (first.Type)
        {
            case "VP8 ":
            case "VP8L":
                return ToImage(DecodeBitstream(data, first, alphaChunk: null));
            case "VP8X":
                return DecodeExtended(data, first, pos, end);
            default:
                throw new ImageFormatException($"Unsupported WebP chunk '{first.Type}'.");
        }
    }

    private static DecodedImage DecodeExtended(byte[] data, Chunk header, int pos, int end)
    {
        if (header.Size != 10)
        {
            throw new ImageFormatException("Invalid VP8X chunk.");
        }

        var flags = data[header.Offset];
        var canvasWidth = ReadUInt24(data, header.Offset + 4) + 1;
        var canvasHeight = ReadUInt24(data, header.Offset + 7) + 1;
        if ((long)canvasWidth * canvasHeight > 200_000_000)
        {
            throw new ImageFormatException("WebP image is too large.");
        }

        if ((flags & AnimationFlag) != 0)
        {
            return DecodeFirstFrame(data, pos, end, canvasWidth, canvasHeight);
        }

        var frame = DecodeFrameChunks(data, pos, end);
        if (frame.Width != canvasWidth || frame.Height != canvasHeight)
        {
            throw new ImageFormatException("The WebP image size does not match its canvas.");
        }

        return ToImage(frame);
    }

    /// <summary>The first ANMF frame of an animation, placed on a transparent canvas.</summary>
    private static DecodedImage DecodeFirstFrame(byte[] data, int pos, int end, int canvasWidth, int canvasHeight)
    {
        while (pos < end)
        {
            var chunk = ReadChunk(data, ref pos, end);
            if (chunk.Type != "ANMF")
            {
                continue;
            }

            if (chunk.Size < 16)
            {
                throw new ImageFormatException("Invalid ANMF chunk.");
            }

            var x = ReadUInt24(data, chunk.Offset) * 2;
            var y = ReadUInt24(data, chunk.Offset + 3) * 2;
            var width = ReadUInt24(data, chunk.Offset + 6) + 1;
            var height = ReadUInt24(data, chunk.Offset + 9) + 1;
            if ((long)x + width > canvasWidth || (long)y + height > canvasHeight)
            {
                throw new ImageFormatException("A WebP animation frame lies outside the canvas.");
            }

            var frame = DecodeFrameChunks(data, chunk.Offset + 16, chunk.Offset + chunk.Size);
            if (frame.Width != width || frame.Height != height)
            {
                throw new ImageFormatException("The WebP frame size does not match its image data.");
            }

            var rgb = new byte[(long)canvasWidth * canvasHeight * 3];
            var alpha = new byte[(long)canvasWidth * canvasHeight];
            for (var row = 0; row < height; row++)
            {
                var target = ((y + row) * canvasWidth) + x;
                Array.Copy(frame.Rgb, row * width * 3, rgb, target * 3, width * 3);
                if (frame.Alpha is { } frameAlpha)
                {
                    Array.Copy(frameAlpha, row * width, alpha, target, width);
                }
                else
                {
                    alpha.AsSpan(target, width).Fill(255);
                }
            }

            return ToImage(new Frame(canvasWidth, canvasHeight, rgb, alpha));
        }

        throw new ImageFormatException("The animated WebP has no frames.");
    }

    /// <summary>Decodes the image data in a sequence of chunks: an optional ALPH chunk, then VP8 or VP8L.</summary>
    private static Frame DecodeFrameChunks(byte[] data, int pos, int end)
    {
        Chunk? alpha = null;
        while (pos < end)
        {
            var chunk = ReadChunk(data, ref pos, end);
            switch (chunk.Type)
            {
                case "ALPH":
                    alpha = chunk;
                    break;
                case "VP8 ":
                case "VP8L":
                    return DecodeBitstream(data, chunk, alpha);
                default:
                    break; // ICCP, EXIF, XMP and unknown chunks.
            }
        }

        throw new ImageFormatException("The WebP file has no image data.");
    }

    private static Frame DecodeBitstream(byte[] data, Chunk chunk, Chunk? alphaChunk)
    {
        if (chunk.Type == "VP8L")
        {
            // Lossless data has its own alpha; an ALPH chunk does not apply.
            var argb = Vp8LDecoder.Decode(data, chunk.Offset, chunk.Size, out var w, out var h);
            var rgb = new byte[(long)w * h * 3];
            var alpha = new byte[(long)w * h];
            for (var i = 0; i < argb.Length; i++)
            {
                var p = argb[i];
                rgb[i * 3] = (byte)(p >> 16);
                rgb[(i * 3) + 1] = (byte)(p >> 8);
                rgb[(i * 3) + 2] = (byte)p;
                alpha[i] = (byte)(p >> 24);
            }

            return new Frame(w, h, rgb, alpha);
        }

        var pixels = Vp8Decoder.Decode(data, chunk.Offset, chunk.Size, out var width, out var height);
        var alphaPlane = alphaChunk is { } a ? DecodeAlpha(data, a, width, height) : null;
        return new Frame(width, height, pixels, alphaPlane);
    }

    /// <summary>Decodes an ALPH chunk: a header byte, then raw or VP8L-compressed values, optionally predicted.</summary>
    private static byte[] DecodeAlpha(byte[] data, Chunk chunk, int width, int height)
    {
        if (chunk.Size <= 1)
        {
            throw new ImageFormatException("Invalid WebP alpha chunk.");
        }

        var header = data[chunk.Offset];
        var method = header & 0x03;
        var filter = (header >> 2) & 0x03;
        var preprocessing = (header >> 4) & 0x03;
        if (method > 1 || preprocessing > 1 || (header >> 6) != 0)
        {
            throw new ImageFormatException("Invalid WebP alpha header.");
        }

        byte[] alpha;
        if (method == 0)
        {
            if (chunk.Size - 1 < (long)width * height)
            {
                throw new ImageFormatException("Truncated WebP alpha data.");
            }

            alpha = data.AsSpan(chunk.Offset + 1, width * height).ToArray();
        }
        else
        {
            alpha = Vp8LDecoder.DecodeAlpha(data, chunk.Offset + 1, chunk.Size - 1, width, height);
        }

        if (filter != 0)
        {
            Unfilter(alpha, width, height, filter);
        }

        return alpha;
    }

    /// <summary>Reverses the alpha prediction: 1 = from the left, 2 = from above, 3 = gradient (left + above - above-left).</summary>
    private static void Unfilter(byte[] alpha, int width, int height, int filter)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            if (y == 0 || filter == 1)
            {
                // The first row is always predicted from the left; other rows start from the pixel above.
                var pred = y == 0 ? 0 : alpha[row - width];
                for (var x = 0; x < width; x++)
                {
                    alpha[row + x] = (byte)(pred + alpha[row + x]);
                    pred = alpha[row + x];
                }
            }
            else if (filter == 2)
            {
                for (var x = 0; x < width; x++)
                {
                    alpha[row + x] = (byte)(alpha[row - width + x] + alpha[row + x]);
                }
            }
            else
            {
                int left = alpha[row - width], topLeft = left;
                for (var x = 0; x < width; x++)
                {
                    int top = alpha[row - width + x];
                    left = (byte)(alpha[row + x] + Math.Clamp(left + top - topLeft, 0, 255));
                    topLeft = top;
                    alpha[row + x] = (byte)left;
                }
            }
        }
    }

    private static DecodedImage ToImage(Frame frame) =>
        new(frame.Width, frame.Height, ImageColorSpace.Rgb, 8, frame.Rgb, ImageEncoding.Raw)
        {
            Alpha = frame.Alpha is { } alpha && alpha.AsSpan().ContainsAnyExcept((byte)255) ? alpha : null,
        };

    private static Chunk ReadChunk(byte[] data, ref int pos, int end)
    {
        if (pos + 8 > end)
        {
            throw new ImageFormatException("Truncated WebP chunk.");
        }

        var type = System.Text.Encoding.ASCII.GetString(data, pos, 4);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
        if (size > end - pos - 8L)
        {
            throw new ImageFormatException($"Truncated WebP chunk '{type}'.");
        }

        var chunk = new Chunk(type, pos + 8, (int)size);
        pos += 8 + (int)size + (int)(size & 1); // Chunks are padded to an even size.
        return chunk;
    }

    private static int ReadUInt24(byte[] data, int at) => data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);

    private readonly record struct Chunk(string Type, int Offset, int Size);

    private sealed record Frame(int Width, int Height, byte[] Rgb, byte[]? Alpha);
}
