using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Fonts.OpenType.Cff;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Fonts;

/// <summary>
/// Writes an OpenType font as a Type 0 (composite) font: Identity-H encoding where character codes are
/// glyph IDs, a CIDFont with widths, a font descriptor, the embedded font program and a ToUnicode map
/// so text can be copied and searched.
/// </summary>
internal static class CidFontWriter
{
    public static PdfReference Write(PdfFileWriter writer, OpenTypeFont font, FontUsage usage, PdfReference? reference = null)
    {
        if (writer.PdfA != PdfAConformance.None && usage.Glyphs.TryGetValue(0, out var missing))
        {
            throw NotdefException(font, missing);
        }

        var isTrueType = font.Outlines == OutlineFormat.TrueType;
        var glyphIds = font.NoSubsetting ? Enumerable.Range(0, font.NumGlyphs) : usage.Glyphs.Keys;

        PdfStreamObject fontFile;
        var subset = !font.NoSubsetting;
        if (isTrueType)
        {
            var program = TrueTypeSubsetter.Subset(font, glyphIds);
            fontFile = PdfStreamObject.CreateCompressed(program);
            fontFile.Dictionary["Length1"] = new PdfInteger(program.Length);
        }
        else if (subset && TrySubsetCff(font, glyphIds) is { } cff)
        {
            // A bare CID-keyed CFF whose charset maps the kept glyphs to their original glyph IDs as CIDs.
            fontFile = PdfStreamObject.CreateCompressed(cff);
            fontFile.Dictionary["Subtype"] = new PdfName("CIDFontType0C");
        }
        else
        {
            // The font asks not to be subset, or its CFF uses features the subsetter does not handle: embed it whole.
            if (writer.PdfA.Part() == 1)
            {
                throw new InvalidOperationException(
                    $"PDF/A-1 (PDF 1.4) does not allow OpenType font programs, but the CFF font '{font.FullName}' cannot be subset and would be embedded whole as one. "
                    + "Use a TrueType font, or PDF/A-2 or PDF/A-3.");
            }

            subset = false;
            fontFile = PdfStreamObject.CreateCompressed(ExtractFace(font));
            fontFile.Dictionary["Subtype"] = new PdfName("OpenType");
        }

        var baseFont = subset ? $"{SubsetTag(font, usage)}+{font.PostScriptName}" : font.PostScriptName;
        var scale = 1000.0 / font.UnitsPerEm;

        var descriptor = Descriptor(font, baseFont, Flags(font, symbolic: true));
        descriptor[isTrueType ? "FontFile2" : "FontFile3"] = writer.Add(fontFile);
        if (subset && writer.PdfA.Part() == 1)
        {
            // PDF/A-1 requires a subset's CIDSet: a bit per CID (= glyph ID here), most significant first, set for those embedded.
            var cids = glyphIds.Append(0).ToList();
            var cidSet = new byte[(cids.Max() / 8) + 1];
            foreach (var gid in cids)
            {
                cidSet[gid / 8] |= (byte)(0x80 >> (gid % 8));
            }

            descriptor["CIDSet"] = writer.Add(PdfStreamObject.CreateCompressed(cidSet));
        }

        var cidFont = new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName(isTrueType ? "CIDFontType2" : "CIDFontType0"),
            ["BaseFont"] = new PdfName(baseFont),
            ["CIDSystemInfo"] = new PdfDictionary
            {
                ["Registry"] = PdfString.FromText("Adobe"),
                ["Ordering"] = PdfString.FromText("Identity"),
                ["Supplement"] = new PdfInteger(0),
            },
            ["FontDescriptor"] = writer.Add(descriptor),
            ["DW"] = new PdfInteger((long)Math.Round(font.GetAdvanceWidth(0) * scale)),
            ["W"] = Widths(font, usage, scale),
        };
        if (isTrueType)
        {
            cidFont["CIDToGIDMap"] = new PdfName("Identity");
        }

        return writer.Add(new PdfDictionary
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type0"),
            ["BaseFont"] = new PdfName(baseFont),
            ["Encoding"] = new PdfName("Identity-H"),
            ["DescendantFonts"] = new PdfArray(writer.Add(cidFont)),
            ["ToUnicode"] = writer.Add(PdfStreamObject.CreateCompressed(ToUnicodeCMap(usage, mapEveryGlyph: writer.PdfA.IsLevelA()))),
        }, reference);
    }

    /// <summary>Width array in the compact form <c>[first [w1 w2 ...] first [w ...] ...]</c>.</summary>
    private static PdfArray Widths(OpenTypeFont font, FontUsage usage, double scale)
    {
        var result = new PdfArray();
        PdfArray? run = null;
        var previous = -2;
        foreach (var gid in usage.Glyphs.Keys)
        {
            if (gid != previous + 1 || run is null)
            {
                run = new PdfArray();
                result.Items.Add(new PdfInteger(gid));
                result.Items.Add(run);
            }

            run.Items.Add(new PdfReal(Math.Round(font.GetAdvanceWidth(gid) * scale, 2)));
            previous = gid;
        }

        return result;
    }

    /// <summary>The font descriptor, without the font program.</summary>
    internal static PdfDictionary Descriptor(OpenTypeFont font, string baseFont, int flags)
    {
        var scale = 1000.0 / font.UnitsPerEm;
        return new PdfDictionary
        {
            ["Type"] = new PdfName("FontDescriptor"),
            ["FontName"] = new PdfName(baseFont),
            ["FontFamily"] = PdfString.FromText(font.FamilyName),
            ["Flags"] = new PdfInteger(flags),
            ["FontBBox"] = PdfArray.OfNumbers(
                Math.Round(font.XMin * scale), Math.Round(font.YMin * scale), Math.Round(font.XMax * scale), Math.Round(font.YMax * scale)),
            ["ItalicAngle"] = new PdfReal(font.ItalicAngle),
            ["Ascent"] = new PdfReal(Math.Round(font.Ascender * scale)),
            ["Descent"] = new PdfReal(Math.Round(font.Descender * scale)),
            ["CapHeight"] = new PdfReal(Math.Round(font.CapHeight * scale)),
            ["XHeight"] = new PdfReal(Math.Round(font.XHeight * scale)),
            ["StemV"] = new PdfReal(Math.Round(10 + (220 * Math.Pow((font.WeightClass - 50) / 900.0, 2)))),
        };
    }

    /// <summary>
    /// The error for drawing the .notdef glyph (glyph 0, the "missing glyph" box) under PDF/A, which forbids it.
    /// <paramref name="text"/> is the text it stood for, if known.
    /// </summary>
    internal static InvalidOperationException NotdefException(OpenTypeFont font, string? text)
    {
        var what = string.IsNullOrEmpty(text)
            ? "some of the text"
            : $"'{text}' ({string.Join(' ', text.EnumerateRunes().Select(r => string.Create(CultureInfo.InvariantCulture, $"U+{r.Value:X4}")))})";
        return new InvalidOperationException(
            $"PDF/A does not allow the .notdef (missing glyph) glyph, but the font '{font.FullName}' has no glyph for {what}. "
            + "Use a font that covers these characters, for instance as a fallback font.");
    }

    internal static int Flags(OpenTypeFont font, bool symbolic)
    {
        // Symbolic: glyphs are addressed by ID, not by a standard Latin encoding. Nonsymbolic (32) otherwise.
        var flags = symbolic ? 4 : 32;
        if (font.IsFixedPitch)
        {
            flags |= 1;
        }

        if (font.IsItalic || font.ItalicAngle != 0)
        {
            flags |= 64;
        }

        return flags;
    }

    /// <summary>Six uppercase letters identifying the subset, derived from its content so output is deterministic.</summary>
    internal static string SubsetTag(OpenTypeFont font, FontUsage usage)
    {
        var input = new StringBuilder(font.PostScriptName);
        foreach (var gid in usage.Glyphs.Keys)
        {
            input.Append(',').Append(gid);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()));
        return string.Create(6, hash, static (span, h) =>
        {
            for (var i = 0; i < 6; i++)
            {
                span[i] = (char)('A' + (h[i] % 26));
            }
        });
    }

    /// <summary>
    /// The ToUnicode CMap: each glyph to its text. Glyphs without text of their own (the extra glyphs of a cluster,
    /// whose text is the run's actual text) are left out, unless <paramref name="mapEveryGlyph"/> (PDF/A level A, ISO
    /// 19005-2, 6.2.11.7.2) maps them to U+FFFD, which also replaces the values that PDF/A forbids.
    /// </summary>
    private static byte[] ToUnicodeCMap(FontUsage usage, bool mapEveryGlyph)
    {
        var cmap = new StringBuilder();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        cmap.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        cmap.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var entries = usage.Glyphs
            .Select(g => (g.Key, Text: mapEveryGlyph ? MappableText(g.Value) : g.Value))
            .Where(g => g.Key != 0 && !string.IsNullOrEmpty(g.Text))
            .Select(g => (g.Key, Text: g.Text!))
            .ToList();
        for (var start = 0; start < entries.Count; start += 100)
        {
            var chunk = entries.Skip(start).Take(100).ToList();
            cmap.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (gid, text) in chunk)
            {
                cmap.Append('<').Append(gid.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append(">\n");
            }

            cmap.Append("endbfchar\n");
        }

        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(cmap.ToString());
    }

    /// <summary>
    /// A glyph's text as PDF/A level A allows it in a ToUnicode map: without U+0000, U+FEFF and U+FFFE, and U+FFFD
    /// (the replacement character) when nothing is left.
    /// </summary>
    internal static string MappableText(string? text)
    {
        var allowed = text?.Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Replace("\uFFFE", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrEmpty(allowed) ? "\uFFFD" : allowed;
    }

    /// <summary>The subset CFF font program, or null when the CFF cannot be subset (it is then embedded whole).</summary>
    private static byte[]? TrySubsetCff(OpenTypeFont font, IEnumerable<int> glyphIds)
    {
        try
        {
            return CffSubsetter.Subset(font, glyphIds);
        }
        catch (Exception ex) when (ex is FontFormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A standalone SFNT for one face (needed when the font came from a collection).</summary>
    private static byte[] ExtractFace(OpenTypeFont font)
    {
        var tables = font.TableTags.ToDictionary(t => t, t => font.GetTable(t).ToArray(), StringComparer.Ordinal);
        return SfntBuilder.Build(font.SfntVersion, tables);
    }
}
