using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Extraction.Fonts;

/// <summary>
/// The base encodings of simple fonts (ISO 32000-2, Annex D) as code → character tables, <c>\0</c> where a code is
/// undefined. MacExpertEncoding (small capitals and old-style figures of expert fonts) is not included.
/// </summary>
internal static partial class SimpleEncodings
{
    /// <summary>WinAnsiEncoding: Windows-1252 for the printable codes.</summary>
    public static string WinAnsi { get; } = BuildWinAnsi();

    public static string Standard => StandardTable;

    public static string MacRoman => MacRomanTable;

    /// <summary>The table of an encoding name, or <c>null</c> for an unknown one.</summary>
    public static string? ByName(string name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "StandardEncoding" => Standard,
        "MacRomanEncoding" => MacRoman,
        _ => null,
    };

    private static string BuildWinAnsi()
    {
        var table = new char[256];
        for (var code = 0x20; code < 256; code++)
        {
            table[code] = code == 0x7F ? '\0' : WinAnsiEncoding.Decode((byte)code);
        }

        return new string(table);
    }
}
