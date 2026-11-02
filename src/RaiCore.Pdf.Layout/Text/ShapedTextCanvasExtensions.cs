namespace RaiCore.Pdf.Layout.Text;

/// <summary>Draws <see cref="ShapedText"/> on a <see cref="PdfCanvas"/>.</summary>
public static class ShapedTextCanvasExtensions
{
    /// <summary>
    /// Draws shaped text with the pen starting at (<paramref name="x"/>, <paramref name="y"/>) on the baseline.
    /// Runs that cannot be extracted glyph by glyph (e.g. Arabic) are tagged with their logical text.
    /// </summary>
    public static PdfCanvas DrawText(this PdfCanvas canvas, ShapedText text, double fontSize, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(text);
        foreach (var run in text.VisualRuns)
        {
            canvas.DrawGlyphs(run.Glyphs, run.Font, fontSize, x, y, run.NeedsActualText ? run.Text : null);
            x += run.Advance * fontSize;
        }

        return canvas;
    }
}
