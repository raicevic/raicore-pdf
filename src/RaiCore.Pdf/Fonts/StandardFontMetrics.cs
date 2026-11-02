namespace RaiCore.Pdf.Fonts;

/// <summary>Widths and vertical metrics of the standard fonts (see StandardFontMetrics.g.cs).</summary>
internal static partial class StandardFontMetrics
{
    public static Metrics? Get(StandardFont font) => All[(int)font];

    /// <summary>Metrics in 1/1000 em. <see cref="Widths"/> is indexed by WinAnsiEncoding code.</summary>
    internal sealed record Metrics(int Ascent, int Descent, int LineGap, int CapHeight, int XHeight, int[] Widths);
}
