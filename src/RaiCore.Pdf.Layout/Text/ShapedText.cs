using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Layout.Text;

/// <summary>Writing direction of a run.</summary>
public enum TextDirection
{
    /// <summary>Left to right (Latin, Greek, Cyrillic, CJK, ...).</summary>
    LeftToRight,

    /// <summary>Right to left (Arabic, Hebrew, ...).</summary>
    RightToLeft,
}

/// <summary>Options for <see cref="TextShaper"/>.</summary>
public sealed class ShapingOptions
{
    /// <summary>Default options: the font's standard features (kerning, standard ligatures, contextual forms).</summary>
    public static ShapingOptions Default { get; } = new();

    /// <summary>
    /// OpenType features in HarfBuzz syntax, e.g. <c>"dlig"</c> (enable), <c>"-kern"</c> (disable),
    /// <c>"ss01"</c> or <c>"aalt=2"</c>. These map to CSS <c>font-feature-settings</c>.
    /// </summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>BCP 47 language, e.g. <c>"tr"</c> or <c>"sr"</c>, which selects language-specific glyph forms.</summary>
    public string? Language { get; init; }
}

/// <summary>
/// Text shaped into glyphs: one or more runs, each in a single font, script and direction.
/// Positions are fractions of the font size, so one shaped text can be drawn at any size.
/// </summary>
public sealed class ShapedText
{
    internal ShapedText(string text, IReadOnlyList<ShapedRun> runs)
    {
        Text = text;
        Runs = runs;
        Advance = runs.Sum(r => r.Advance);
    }

    /// <summary>The source text.</summary>
    public string Text { get; }

    /// <summary>Runs in logical (reading) order.</summary>
    public IReadOnlyList<ShapedRun> Runs { get; }

    /// <summary>Total advance width as a fraction of the font size.</summary>
    public double Advance { get; }

    /// <summary>Width in points at <paramref name="fontSize"/>, including kerning.</summary>
    public double GetWidth(double fontSize) => Advance * fontSize;

    /// <summary>
    /// Runs in the order they appear on the line, left to right. Text that is entirely right-to-left is
    /// reversed; mixed-direction text keeps logical order until the bidi algorithm lands.
    /// </summary>
    public IEnumerable<ShapedRun> VisualRuns =>
        Runs.Count > 1 && Runs.All(r => r.Direction == TextDirection.RightToLeft) ? Runs.Reverse() : Runs;
}

/// <summary>A sequence of glyphs in one font, script and direction.</summary>
public sealed class ShapedRun
{
    internal ShapedRun(
        string source, int start, int length, PdfFont font, string script, TextDirection direction,
        IReadOnlyList<PositionedGlyph> glyphs, IReadOnlyList<int> clusters, bool needsActualText)
    {
        Source = source;
        Start = start;
        Length = length;
        Font = font;
        Script = script;
        Direction = direction;
        Glyphs = glyphs;
        Clusters = clusters;
        NeedsActualText = needsActualText;
        Advance = glyphs.Sum(g => g.Advance);
    }

    /// <summary>Start of the run in the source text (UTF-16 index).</summary>
    public int Start { get; }

    /// <summary>Length of the run in the source text (UTF-16 code units).</summary>
    public int Length { get; }

    /// <summary>The run's text.</summary>
    public string Text => Source.Substring(Start, Length);

    /// <summary>The font, chosen from the fallback list.</summary>
    public PdfFont Font { get; }

    /// <summary>ISO 15924 script tag, e.g. <c>Latn</c>, <c>Arab</c>, <c>Hebr</c>.</summary>
    public string Script { get; }

    /// <summary>Writing direction.</summary>
    public TextDirection Direction { get; }

    /// <summary>Glyphs in visual (left-to-right) order.</summary>
    public IReadOnlyList<PositionedGlyph> Glyphs { get; }

    /// <summary>For each glyph, the index in the source text of the first character it came from.</summary>
    public IReadOnlyList<int> Clusters { get; }

    /// <summary>Advance width as a fraction of the font size.</summary>
    public double Advance { get; }

    /// <summary>
    /// Whether glyphs do not map to characters one-to-one in order (right-to-left text, reordering or one
    /// character becoming several glyphs). The run is then written with <c>/ActualText</c> so it extracts correctly.
    /// </summary>
    public bool NeedsActualText { get; }

    private string Source { get; }
}
