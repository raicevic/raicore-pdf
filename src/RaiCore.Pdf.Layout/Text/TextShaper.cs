using System.Runtime.CompilerServices;
using HarfBuzzSharp;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace RaiCore.Pdf.Layout.Text;

/// <summary>
/// Shapes text with HarfBuzz: applies kerning, ligatures, contextual forms (Arabic joining, Indic reordering)
/// and mark positioning, and picks a font per character from a fallback list.
/// </summary>
/// <remarks>Thread-safe. Fonts must be embedded fonts loaded with <see cref="PdfFont.Load(string, int)"/>.</remarks>
public static class TextShaper
{
    private static readonly ConditionalWeakTable<OpenTypeFont, HarfBuzzFace> Faces = [];

    /// <summary>Shapes <paramref name="text"/> in a single font.</summary>
    public static ShapedText Shape(string text, PdfFont font, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        return Shape(text, [font], options);
    }

    /// <summary>
    /// Shapes <paramref name="text"/>, using for each character cluster the first font in <paramref name="fonts"/>
    /// that has all of its glyphs (like a CSS <c>font-family</c> list). Characters no font has are drawn with
    /// the first font's "missing glyph".
    /// </summary>
    public static ShapedText Shape(string text, IReadOnlyList<PdfFont> fonts, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fonts);
        if (fonts.Count == 0)
        {
            throw new ArgumentException("At least one font is required.", nameof(fonts));
        }

        var openTypeFonts = new OpenTypePdfFont[fonts.Count];
        for (var i = 0; i < fonts.Count; i++)
        {
            openTypeFonts[i] = fonts[i] as OpenTypePdfFont
                ?? throw new ArgumentException(
                    $"'{fonts[i].PostScriptName}' is a standard PDF font, which cannot be shaped. Load a font file instead (see RaiCore.Pdf.Fonts for bundled ones).",
                    nameof(fonts));
        }

        options ??= ShapingOptions.Default;
        var features = options.Features.Select(Feature.Parse).ToArray();
        var runs = new List<ShapedRun>();
        foreach (var item in TextItemizer.Itemize(text, openTypeFonts))
        {
            runs.Add(ShapeRun(text, item, features, options.Language));
        }

        return new ShapedText(text, runs);
    }

    private static ShapedRun ShapeRun(string text, TextItem item, Feature[] features, string? language)
    {
        var font = item.Font;
        var face = Faces.GetValue(font.Font, static f => new HarfBuzzFace(f));
        var unitsPerEm = (double)font.Font.UnitsPerEm;

        using var buffer = new HbBuffer();

        // The whole text is passed as context so shaping at run boundaries (e.g. Arabic joining) is correct.
        buffer.AddUtf16(text.AsSpan(), item.Start, item.Length);
        buffer.Direction = item.Direction == TextDirection.RightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = item.Script;
        if (language is not null)
        {
            buffer.Language = new Language(language);
        }

        face.Font.Shape(buffer, features);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var clusters = new int[infos.Length];
        for (var i = 0; i < infos.Length; i++)
        {
            clusters[i] = (int)infos[i].Cluster;
        }

        // Each cluster's text goes on its first glyph so copy/paste and search work, including ligatures.
        var boundaries = clusters.Append(item.Start + item.Length).Distinct().Order().ToArray();
        var seen = new HashSet<int>();
        var glyphs = new PositionedGlyph[infos.Length];
        var oneToOneInOrder = item.Direction == TextDirection.LeftToRight;
        for (var i = 0; i < infos.Length; i++)
        {
            var cluster = clusters[i];
            string? glyphText = null;
            if (seen.Add(cluster))
            {
                var end = boundaries[Array.BinarySearch(boundaries, cluster) + 1];
                glyphText = text[cluster..end];
            }
            else
            {
                oneToOneInOrder = false; // Several glyphs for one cluster: ToUnicode alone cannot express it.
            }

            if (i > 0 && cluster < clusters[i - 1])
            {
                oneToOneInOrder = false; // Reordered (e.g. Indic pre-base vowels).
            }

            glyphs[i] = new PositionedGlyph(
                (int)infos[i].Codepoint,
                positions[i].XAdvance / unitsPerEm,
                positions[i].XOffset / unitsPerEm,
                positions[i].YOffset / unitsPerEm,
                glyphText);
        }

        return new ShapedRun(
            text, item.Start, item.Length, font, item.Script.ToString(), item.Direction, glyphs, clusters, needsActualText: !oneToOneInOrder);
    }

    /// <summary>HarfBuzz objects for one font, created once and shared (HarfBuzz fonts are safe to shape with concurrently).</summary>
    private sealed class HarfBuzzFace
    {
        public HarfBuzzFace(OpenTypeFont font)
        {
            // HarfBuzz reads the font data in place for as long as the blob lives. Blob.FromStream is not safe for
            // that: it pins a managed array only while constructing the blob, so the GC may later move or reuse
            // the memory (HarfBuzzSharp 14.2.1). Copy into unmanaged memory owned by the blob instead.
            Blob = CreateBlob(font.Data);
            Blob.MakeImmutable();
            Face = new Face(Blob, font.FaceIndex);
            Font = new HbFont(Face);
            Font.SetScale(font.UnitsPerEm, font.UnitsPerEm);
            Font.SetFunctionsOpenType();
        }

        public Blob Blob { get; }

        public Face Face { get; }

        public HbFont Font { get; }

        private static unsafe Blob CreateBlob(byte[] data)
        {
            var memory = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)data.Length);
            data.CopyTo(new Span<byte>((void*)memory, data.Length));
            return new Blob(memory, data.Length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.NativeMemory.Free((void*)memory));
        }
    }
}
