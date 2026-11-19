namespace RaiCore.Pdf.Email.Mime;

/// <summary>
/// Undoes a content transfer encoding (RFC 2045, section 6): base64 and quoted-printable, leniently, as mail clients do
/// (characters outside the alphabet are skipped, broken escapes kept). 7bit, 8bit, binary and unknown encodings are
/// passed through.
/// </summary>
internal static class TransferDecoding
{
    public static byte[] Decode(ReadOnlySpan<byte> data, string encoding) => encoding switch
    {
        "base64" => DecodeBase64(data),
        "quoted-printable" => DecodeQuotedPrintable(data),
        _ => data.ToArray(),
    };

    /// <summary>
    /// Decodes base64, skipping line breaks and any other character outside the alphabet; a group of two or three
    /// characters before padding or at the end gives one or two bytes.
    /// </summary>
    public static byte[] DecodeBase64(ReadOnlySpan<byte> data)
    {
        var output = new byte[(data.Length * 3 / 4) + 3];
        var count = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            var value = b switch
            {
                >= (byte)'A' and <= (byte)'Z' => b - 'A',
                >= (byte)'a' and <= (byte)'z' => b - 'a' + 26,
                >= (byte)'0' and <= (byte)'9' => b - '0' + 52,
                (byte)'+' or (byte)'-' => 62, // '-' and '_': the URL-safe alphabet, which some mailers use.
                (byte)'/' or (byte)'_' => 63,
                _ => -1,
            };
            if (value < 0)
            {
                if (b == '=')
                {
                    // Padding ends a group: its leftover bits are zero. Base64 that goes on after it (blocks put
                    // together) starts a new group.
                    buffer = 0;
                    bits = 0;
                }

                continue;
            }

            buffer = (buffer << 6) | value;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output[count++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        return output.AsSpan(0, count).ToArray();
    }

    /// <summary>
    /// Decodes quoted-printable: <c>=XX</c> is a byte (either case of hex digits), <c>=</c> at the end of a line (after
    /// optional whitespace) joins it to the next, and an <c>=</c> that is neither stays as it is.
    /// </summary>
    public static byte[] DecodeQuotedPrintable(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        var count = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b != '=')
            {
                output[count++] = b;
                continue;
            }

            if (i + 2 < data.Length && IsHex(data[i + 1]) && IsHex(data[i + 2]))
            {
                output[count++] = (byte)((Hex(data[i + 1]) << 4) | Hex(data[i + 2]));
                i += 2;
                continue;
            }

            // A soft line break: '=', optional trailing whitespace, then CRLF or LF (or the end).
            var j = i + 1;
            while (j < data.Length && data[j] is (byte)' ' or (byte)'\t')
            {
                j++;
            }

            if (j == data.Length)
            {
                i = j - 1;
                continue;
            }

            if (data[j] == '\n' || (data[j] == '\r' && j + 1 < data.Length && data[j + 1] == '\n'))
            {
                i = data[j] == '\r' ? j + 1 : j;
                continue;
            }

            output[count++] = b; // A broken escape: kept, as mail clients show it.
        }

        return output.AsSpan(0, count).ToArray();
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static int Hex(byte b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
}
