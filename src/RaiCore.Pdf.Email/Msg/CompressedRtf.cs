using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Email.Msg;

/// <summary>
/// Compressed RTF ([MS-OXRTFCP]), the form of <c>PidTagRtfCompressed</c> in Outlook messages: a header, then either the
/// RTF as it is (<c>MELA</c>) or LZ77-style compressed against a 4096-byte dictionary primed with common RTF
/// (<c>LZFu</c>).
/// </summary>
internal static class CompressedRtf
{
    private const uint Compressed = 0x75465A4C; // "LZFu"
    private const uint Uncompressed = 0x414C454D; // "MELA"

    /// <summary>The text the dictionary starts with ([MS-OXRTFCP] 2.1.3.1.1).</summary>
    private static readonly byte[] Prime = Encoding.ASCII.GetBytes(
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

    /// <summary>Decompresses; at most <paramref name="maxBytes"/> bytes come out.</summary>
    /// <exception cref="MessageFormatException">The data is not compressed RTF.</exception>
    /// <exception cref="ResourceLimitExceededException">It would decompress to more than <paramref name="maxBytes"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data, long maxBytes)
    {
        if (data.Length < 16)
        {
            throw new MessageFormatException("The compressed RTF is shorter than its header.");
        }

        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        // The size counts from after itself; one that ends within the header (damage) leaves an empty body.
        var body = data[16..(int)Math.Clamp(4L + compressedSize, 16, data.Length)];
        if (type == Uncompressed)
        {
            return body[..(int)Math.Min(body.Length, rawSize)].ToArray();
        }

        if (type != Compressed)
        {
            throw new MessageFormatException("The data is not compressed RTF (no LZFu or MELA signature).");
        }

        if (rawSize > maxBytes)
        {
            throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), maxBytes,
                $"The compressed RTF says it holds {rawSize} bytes, more than {maxBytes} (ResourceLimits.MaxDecompressedBytes).");
        }

        var dictionary = new byte[4096];
        Prime.CopyTo(dictionary, 0);
        var write = Prime.Length;
        using var output = new MemoryStream((int)Math.Min(rawSize, (uint)body.Length * 8L));
        var position = 0;
        while (position < body.Length)
        {
            var control = body[position++];
            for (var bit = 0; bit < 8 && position < body.Length; bit++)
            {
                if ((control & (1 << bit)) == 0)
                {
                    var literal = body[position++];
                    output.WriteByte(literal);
                    dictionary[write] = literal;
                    write = (write + 1) & 4095;
                }
                else
                {
                    if (position + 1 >= body.Length)
                    {
                        return output.ToArray();
                    }

                    var reference = (body[position] << 8) | body[position + 1];
                    position += 2;
                    var offset = reference >> 4;
                    var length = (reference & 15) + 2;
                    if (offset == write)
                    {
                        return output.ToArray(); // The end marker.
                    }

                    for (var i = 0; i < length; i++)
                    {
                        var b = dictionary[(offset + i) & 4095];
                        output.WriteByte(b);
                        dictionary[write] = b;
                        write = (write + 1) & 4095;
                    }
                }

                if (output.Length > maxBytes)
                {
                    throw new ResourceLimitExceededException(nameof(ResourceLimits.MaxDecompressedBytes), maxBytes,
                        $"The compressed RTF decompresses to more than {maxBytes} bytes (ResourceLimits.MaxDecompressedBytes).");
                }
            }
        }

        return output.ToArray();
    }
}
