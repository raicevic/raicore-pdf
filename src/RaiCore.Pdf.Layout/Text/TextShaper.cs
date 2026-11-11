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
/// <remarks>
/// Thread-safe: any number of threads may shape text at the same time, with the same fonts or different ones. Each
/// font's HarfBuzz face and font objects are created once, on first use, and shared by all threads (HarfBuzz allows
/// concurrent shaping with a font that is no longer changed); every call shapes into its own buffer. Fonts must be
/// embedded fonts loaded with <see cref="PdfFont.Load(string, int)"/>.
/// </remarks>
public static class TextShaper
{
    /// <summary>The HarfBuzz objects of each font, for as long as the font is alive.</summary>
    private static readonly ConditionalWeakTable<OpenTypeFont, HarfBuzzFace> Faces = [];

    /// <summary>
    /// The native copy of each font file, shared by the faces of a collection (<c>.ttc</c>) that hold the same data,
    /// so a file is copied into native memory once however many of its faces are used.
    /// </summary>
    private static readonly ConditionalWeakTable<byte[], Blob> Blobs = [];

    private static readonly Lock FacesLock = new();

    /// <summary>Shapes <paramref name="text"/> in a single font.</summary>
    public static ShapedText Shape(string text, PdfFont font, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        return Shape(text, [font], options);
    }

    /// <summary>
    /// Shapes <paramref name="text"/>, using for each character cluster the first font in <paramref name="fonts"/>
    /// that has all of its glyphs (like a CSS <c>font-family</c> list). Characters no font has are drawn with
    /// the first font's "missing glyph". Each run gets the direction of its script.
    /// </summary>
    public static ShapedText Shape(string text, IReadOnlyList<PdfFont> fonts, ShapingOptions? options = null) =>
        Shape(text, fonts, options, direction: null);

    /// <summary>
    /// Shapes <paramref name="text"/> like <see cref="Shape(string, IReadOnlyList{PdfFont}, ShapingOptions?)"/>, but in
    /// one direction whatever the scripts, as for text whose direction the bidi algorithm resolved (a right-to-left
    /// direction also mirrors characters such as parentheses). The runs stay in logical order.
    /// </summary>
    public static ShapedText Shape(string text, IReadOnlyList<PdfFont> fonts, ShapingOptions? options, TextDirection direction) =>
        Shape(text, fonts, options, (TextDirection?)direction);

    private static ShapedText Shape(string text, IReadOnlyList<PdfFont> fonts, ShapingOptions? options, TextDirection? direction)
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
        foreach (var item in TextItemizer.Itemize(text, openTypeFonts, direction))
        {
            runs.Add(ShapeRun(text, item, features, options.Language));
        }

        return new ShapedText(text, runs);
    }

    private static ShapedRun ShapeRun(string text, TextItem item, Feature[] features, string? language)
    {
        var font = item.Font;
        var face = GetFace(font.Font);
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

    /// <summary>
    /// The HarfBuzz objects of a font. They are created under a lock, so that threads shaping with a new font at the
    /// same time get the same objects instead of each making (and leaking) a native copy of the font.
    /// </summary>
    private static HarfBuzzFace GetFace(OpenTypeFont font)
    {
        if (Faces.TryGetValue(font, out var face))
        {
            return face;
        }

        lock (FacesLock)
        {
            return Faces.GetValue(font, static f => new HarfBuzzFace(f, Blobs.GetValue(f.Data, CreateBlob)));
        }
    }

    /// <summary>
    /// Copies font data into native memory owned by a HarfBuzz blob. HarfBuzz reads the font data in place for as long
    /// as the blob lives. Blob.FromStream is not safe for that: it pins a managed array only while constructing the
    /// blob, so the GC may later move or reuse the memory (HarfBuzzSharp 14.2.1).
    /// </summary>
    private static unsafe Blob CreateBlob(byte[] data)
    {
        var memory = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)data.Length);
        data.CopyTo(new Span<byte>((void*)memory, data.Length));
        var blob = new Blob(memory, data.Length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.NativeMemory.Free((void*)memory));
        blob.MakeImmutable();
        return blob;
    }

    /// <summary>
    /// HarfBuzz objects for one font, created once and shared. They are not changed after construction, so shaping
    /// with them on several threads at once is safe (HarfBuzz only reads a font while shaping; its lazily loaded
    /// tables and caches are thread-safe).
    /// </summary>
    private sealed class HarfBuzzFace
    {
        public HarfBuzzFace(OpenTypeFont font, Blob blob)
        {
            Blob = blob;
            Face = new Face(Blob, font.FaceIndex);
            Face.MakeImmutable();
            Font = new HbFont(Face);
            Font.SetScale(font.UnitsPerEm, font.UnitsPerEm);
            Font.SetFunctionsOpenType();
        }

        public Blob Blob { get; }

        public Face Face { get; }

        public HbFont Font { get; }
    }
}
