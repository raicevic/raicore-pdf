namespace RaiCore.Pdf;

/// <summary>
/// A glyph with explicit positioning, as produced by a text shaper such as HarfBuzz.
/// Distances are fractions of the font size (multiply by the size in points).
/// </summary>
/// <param name="GlyphId">The glyph index in the font.</param>
/// <param name="Advance">How far the pen moves after this glyph.</param>
/// <param name="OffsetX">Horizontal displacement of this glyph only, e.g. for an accent.</param>
/// <param name="OffsetY">Vertical displacement of this glyph only (positive is up).</param>
/// <param name="Text">
/// The text this glyph represents, used for copy/paste and search. A ligature such as "ﬁ" maps to "fi";
/// extra glyphs in a cluster can use <c>null</c>.
/// </param>
public readonly record struct PositionedGlyph(int GlyphId, double Advance, double OffsetX = 0, double OffsetY = 0, string? Text = null);
