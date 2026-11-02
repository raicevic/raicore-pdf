namespace RaiCore.Pdf.Fonts;

/// <summary>
/// Encodes text as WinAnsiEncoding (Windows-1252, ISO 32000-2 Annex D) for the standard fonts.
/// Characters that cannot be represented become <c>?</c>.
/// </summary>
internal static class WinAnsiEncoding
{
    // Bytes 0x80–0x9F map to these characters; '\0' marks an unused code.
    private const string HighControlRange =
        "€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0" +
        "\0‘’“”•–—˜™š›œ\0žŸ";

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

    private static byte Encode(char c) => c switch
    {
        >= ' ' and <= '~' => (byte)c,
        >= ' ' and <= 'ÿ' => (byte)c,
        _ => HighMap.TryGetValue(c, out var b) ? b : (byte)'?',
    };

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
