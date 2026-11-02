using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>Names read from the OpenType <c>name</c> table.</summary>
/// <param name="Family">Typographic family (ID 16), or the legacy family (ID 1).</param>
/// <param name="LegacyFamily">Legacy family (ID 1), which may include the weight, e.g. "Arial Black".</param>
/// <param name="Subfamily">Typographic subfamily (ID 17), or the legacy subfamily (ID 2).</param>
/// <param name="FullName">Full name (ID 4).</param>
/// <param name="PostScriptName">PostScript name (ID 6).</param>
internal sealed record FontNames(string Family, string LegacyFamily, string Subfamily, string FullName, string PostScriptName);

internal static class NameTable
{
    public static FontNames Read(ReadOnlySpan<byte> table)
    {
        var best = new Dictionary<int, (int Score, string Value)>();
        if (table.Length >= 6)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
            int storage = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
            for (var i = 0; i < count && 6 + ((i + 1) * 12) <= table.Length; i++)
            {
                var record = table[(6 + (i * 12))..];
                int platform = BinaryPrimitives.ReadUInt16BigEndian(record);
                int encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
                int language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
                int nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
                int offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
                if (nameId is not (1 or 2 or 4 or 6 or 16 or 17) || storage + offset + length > table.Length)
                {
                    continue;
                }

                var bytes = table.Slice(storage + offset, length);

                // Prefer Windows English, then any Windows, then Unicode, then Macintosh Roman.
                (int score, string? value) = platform switch
                {
                    3 when encoding is 1 or 10 => (language == 0x0409 ? 4 : 3, Encoding.BigEndianUnicode.GetString(bytes)),
                    0 => (2, Encoding.BigEndianUnicode.GetString(bytes)),
                    1 when encoding == 0 => (1, Encoding.Latin1.GetString(bytes)),
                    _ => (0, null),
                };

                if (value is not null && (!best.TryGetValue(nameId, out var existing) || score > existing.Score))
                {
                    best[nameId] = (score, value.Trim('\0', ' '));
                }
            }
        }

        string Get(int id) => best.TryGetValue(id, out var v) ? v.Value : string.Empty;

        var legacyFamily = Get(1);
        return new FontNames(
            Get(16) is { Length: > 0 } family ? family : legacyFamily,
            legacyFamily,
            Get(17) is { Length: > 0 } sub ? sub : Get(2),
            Get(4),
            Get(6));
    }
}
