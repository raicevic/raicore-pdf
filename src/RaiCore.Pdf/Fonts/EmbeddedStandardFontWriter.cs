using System.Buffers.Binary;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Fonts;

/// <summary>
/// Embeds a TrueType font in place of a standard font (see <c>PdfDocument.StandardFontSubstitute</c>): a simple,
/// non-symbolic <c>/TrueType</c> font with <c>/WinAnsiEncoding</c>, so the single-byte codes already in the content
/// streams keep working. The subset gets a Microsoft Unicode (3,1) <c>cmap</c>, through which PDF readers (and PDF/A,
/// ISO 32000-1 9.6.6.4) map each code to its glyph: code, WinAnsi glyph name, Unicode, glyph.
/// </summary>
internal static class EmbeddedStandardFontWriter
{
    public static PdfReference Write(PdfFileWriter writer, OpenTypeFont font, FontUsage usage)
    {
        var codes = usage.Glyphs.Keys.Where(code => code is >= 0 and <= 0xFF).ToList();
        if (codes.Count == 0)
        {
            codes.Add(' '); // Nothing drawn (empty strings): still a valid font.
        }

        var glyphs = new Dictionary<int, int>(); // Code -> glyph.
        var cmap = new SortedDictionary<int, int>(); // Unicode -> glyph.
        foreach (var code in codes)
        {
            var unicode = GlyphUnicode((byte)code);
            var gid = unicode == 0 ? 0 : font.GetGlyphId(unicode);
            if (gid == 0 && writer.PdfA != PdfAConformance.None)
            {
                throw CidFontWriter.NotdefException(font, unicode == 0 ? null : char.ConvertFromUtf32(unicode));
            }

            glyphs[code] = gid;
            if (gid != 0)
            {
                cmap.TryAdd(unicode, gid);
            }

            // WinAnsi names codes A0 and AD "space" and "hyphen", like 20 and 2D; readers that take the Unicode name
            // (nbspace, sfthyphen) find the same glyph.
            if (code is 0xA0 or 0xAD && gid != 0)
            {
                cmap.TryAdd(code, gid);
            }
        }

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["cmap"] = UnicodeCmap(cmap) };
        if (font.HasTable("post") && font.GetTable("post").Length >= 32)
        {
            // Version 3: no glyph names (readers use the cmap), but the italic angle and underline metrics.
            var post = font.GetTable("post")[..32].ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
            tables["post"] = post;
        }

        var program = TrueTypeSubsetter.Subset(font, glyphs.Values.Where(gid => gid != 0), tables);
        var fontFile = PdfStreamObject.CreateCompressed(program);
        fontFile.Dictionary["Length1"] = new PdfInteger(program.Length);

        var baseFont = $"{CidFontWriter.SubsetTag(font, usage)}+{font.PostScriptName}";
        var descriptor = CidFontWriter.Descriptor(font, baseFont, CidFontWriter.Flags(font, symbolic: false));
        descriptor["FontFile2"] = writer.Add(fontFile);

        // Widths of the codes in use (in thousandths of the font size, as in the font program); 0 for the others.
        var (first, last) = (codes.Min(), codes.Max());
        var scale = 1000.0 / font.UnitsPerEm;
        var widths = new PdfArray();
        for (var code = first; code <= last; code++)
        {
            widths.Items.Add(new PdfReal(glyphs.TryGetValue(code, out var gid) ? Math.Round(font.GetAdvanceWidth(gid) * scale, 2) : 0));
        }

        return writer.Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("TrueType"),
            ["BaseFont"] = new PdfName(baseFont),
            ["FirstChar"] = new PdfInteger(first),
            ["LastChar"] = new PdfInteger(last),
            ["Widths"] = writer.Add(widths),
            ["FontDescriptor"] = writer.Add(descriptor),
            ["Encoding"] = new PdfName("WinAnsiEncoding"),
        });
    }

    /// <summary>
    /// The Unicode value of the glyph a WinAnsi code names (ISO 32000-1, Annex D): codes A0 and AD are "space" and
    /// "hyphen". 0 for codes without a glyph (the control codes and the five unused codes).
    /// </summary>
    internal static int GlyphUnicode(byte code) => code switch
    {
        < 0x20 or 0x7F => 0,
        0xA0 => ' ',
        0xAD => '-',
        _ => WinAnsiEncoding.Decode(code),
    };

    /// <summary>A <c>cmap</c> table with one subtable, Microsoft Unicode BMP (3,1) in format 4.</summary>
    internal static byte[] UnicodeCmap(IReadOnlyDictionary<int, int> map)
    {
        // Segments of consecutive code points with consecutive glyphs, plus the required final 0xFFFF segment.
        var segments = new List<(int Start, int End, int Delta)>();
        foreach (var (unicode, gid) in map.OrderBy(entry => entry.Key))
        {
            if (segments.Count > 0 && segments[^1] is var last && last.End + 1 == unicode && unicode + last.Delta == gid)
            {
                segments[^1] = last with { End = unicode };
            }
            else
            {
                segments.Add((unicode, unicode, gid - unicode));
            }
        }

        segments.Add((0xFFFF, 0xFFFF, 1));

        var count = segments.Count;
        var subtableLength = 16 + (count * 8);
        var table = new byte[12 + subtableLength];
        var span = table.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 1); // version 0, one encoding record
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 3); // platform: Microsoft
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], 1); // encoding: Unicode BMP
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 12);

        var sub = span[12..];
        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var searchRange = 2 << entrySelector;
        BinaryPrimitives.WriteUInt16BigEndian(sub, 4);
        BinaryPrimitives.WriteUInt16BigEndian(sub[2..], (ushort)subtableLength);
        BinaryPrimitives.WriteUInt16BigEndian(sub[6..], (ushort)(count * 2));
        BinaryPrimitives.WriteUInt16BigEndian(sub[8..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(sub[10..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(sub[12..], (ushort)((count * 2) - searchRange));
        for (var i = 0; i < count; i++)
        {
            var (start, end, delta) = segments[i];
            BinaryPrimitives.WriteUInt16BigEndian(sub[(14 + (i * 2))..], (ushort)end);
            BinaryPrimitives.WriteUInt16BigEndian(sub[(16 + (count * 2) + (i * 2))..], (ushort)start);
            BinaryPrimitives.WriteUInt16BigEndian(sub[(16 + (count * 4) + (i * 2))..], (ushort)delta); // modulo 65536
            // idRangeOffset stays 0: glyph = code point + delta.
        }

        return table;
    }
}
