namespace RaiCore.Pdf.Fonts;

/// <summary>
/// Encodes text as WinAnsiEncoding (Windows-1252, ISO 32000-2 Annex D) for the standard fonts.
/// Characters that cannot be represented become <c>?</c>.
/// </summary>
internal static class WinAnsiEncoding
{
    // Bytes 0x80–0x9F map to these characters; '\0' marks an unused code.
    private const string HighControlRange =
        "\u20AC\0\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\0\u017D\0" +
        "\0\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\0\u017E\u0178";

    private static readonly Dictionary<char, byte> HighMap = BuildHighMap();

    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i] = Encode(text[i]);
        }

        return bytes;
    }

    /// <summary>Whether the character exists in WinAnsiEncoding (rather than becoming <c>?</c>).</summary>
    public static bool CanEncode(char c) => c == '?' || Encode(c) != (byte)'?';

    public static byte Encode(char c) => c switch
    {
        >= '\u0020' and <= '\u007E' => (byte)c,
        >= '\u00A0' and <= '\u00FF' => (byte)c,
        _ => HighMap.TryGetValue(c, out var b) ? b : (byte)'?',
    };

    /// <summary>The character a code stands for, or <c>'\0'</c> for the five codes WinAnsiEncoding leaves unused.</summary>
    public static char Decode(byte code) => code is >= 0x80 and <= 0x9F ? HighControlRange[code - 0x80] : (char)code;

    private static Dictionary<char, byte> BuildHighMap()
    {
        var map = new Dictionary<char, byte>();
        for (var i = 0; i < HighControlRange.Length; i++)
        {
            if (HighControlRange[i] != '\0')
            {
                map[HighControlRange[i]] = (byte)(0x80 + i);
            }
        }

        return map;
    }
}
