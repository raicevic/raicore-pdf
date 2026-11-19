using System.Text;
using System.Text.Unicode;

namespace RaiCore.Pdf.Sheets.Csv;

/// <summary>
/// The text of a CSV file from its bytes: a byte order mark decides (UTF-8, UTF-16, UTF-32); else the encoding given;
/// else UTF-16 without a mark if every other byte is zero, UTF-8 if the bytes are valid UTF-8, and Windows-1252 (what
/// Excel writes on Western Windows systems, and a superset of ISO-8859-1 and ASCII) if they are not. The code page
/// tables come with .NET (<see cref="CodePagesEncodingProvider"/>, asked directly so nothing is registered for the
/// whole process) and work with invariant globalization.
/// </summary>
internal static class CsvText
{
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

    /// <summary>How many bytes the UTF-16 test looks at.</summary>
    private const int Utf16Sample = 4096;

    /// <summary>Decodes the bytes; bytes the encoding cannot decode become U+FFFD.</summary>
    public static string Decode(ReadOnlySpan<byte> data, Encoding? encoding)
    {
        var (detected, bomLength) = FromByteOrderMark(data);
        data = data[bomLength..];
        encoding = detected ?? encoding ?? Guess(data);
        var text = encoding.GetString(data);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>The encoding a byte order mark names, and the mark's length; <c>null</c> without one.</summary>
    private static (Encoding? Encoding, int Length) FromByteOrderMark(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (Encoding.UTF8, 3);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return (Encoding.UTF32, 4);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode, 2);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        return (null, 0);
    }

    /// <summary>The encoding of text without a byte order mark.</summary>
    private static Encoding Guess(ReadOnlySpan<byte> data)
    {
        // UTF-16 without a mark: text in Latin script has a zero in every other byte, and the other bytes are not zero.
        var sample = data[..(Math.Min(data.Length, Utf16Sample) & ~1)];
        if (sample.Length >= 4)
        {
            int evenZeros = 0, oddZeros = 0;
            for (var i = 0; i < sample.Length; i += 2)
            {
                evenZeros += sample[i] == 0 ? 1 : 0;
                oddZeros += sample[i + 1] == 0 ? 1 : 0;
            }

            var pairs = sample.Length / 2;
            if (oddZeros * 10 >= pairs * 4 && evenZeros * 20 < pairs)
            {
                return Encoding.Unicode;
            }

            if (evenZeros * 10 >= pairs * 4 && oddZeros * 20 < pairs)
            {
                return Encoding.BigEndianUnicode;
            }
        }

        return Utf8.IsValid(data) ? Encoding.UTF8 : Windows1252;
    }
}
