namespace RaiCore.Pdf.Slides.Model;

/// <summary>How an area is painted.</summary>
internal abstract record Fill;

/// <summary>One color (its alpha included).</summary>
/// <param name="Color">The color.</param>
internal sealed record SolidFill(PdfColor Color) : Fill;

/// <summary>A gradient between color stops.</summary>
/// <param name="Stops">The stops, by position from 0 to 1.</param>
/// <param name="Angle">For a linear gradient, the direction in degrees clockwise from left-to-right.</param>
/// <param name="Radial">Whether the gradient spreads from the center outwards (circle, rectangle and shape paths).</param>
internal sealed record GradientFill(IReadOnlyList<GradientStop> Stops, double Angle, bool Radial) : Fill
{
    /// <summary>The color at a position from 0 to 1, interpolated between the stops.</summary>
    public PdfColor ColorAt(double position)
    {
        if (Stops.Count == 0)
        {
            return PdfColor.White;
        }

        if (position <= Stops[0].Position)
        {
            return Stops[0].Color;
        }

        for (var i = 1; i < Stops.Count; i++)
        {
            var (p1, c1) = (Stops[i].Position, Stops[i].Color);
            if (position <= p1)
            {
                var (p0, c0) = (Stops[i - 1].Position, Stops[i - 1].Color);
                var t = p1 > p0 ? (position - p0) / (p1 - p0) : 1;
                return new PdfColor(
                    c0.Red + ((c1.Red - c0.Red) * t),
                    c0.Green + ((c1.Green - c0.Green) * t),
                    c0.Blue + ((c1.Blue - c0.Blue) * t),
                    c0.Alpha + ((c1.Alpha - c0.Alpha) * t));
            }
        }

        return Stops[^1].Color;
    }
}

/// <summary>A gradient stop.</summary>
/// <param name="Position">The position, from 0 to 1.</param>
/// <param name="Color">The color.</param>
internal readonly record struct GradientStop(double Position, PdfColor Color);

/// <summary>An image stretched over the area (or tiled, drawn stretched).</summary>
/// <param name="Data">The image file's bytes.</param>
/// <param name="Source">The part name of the image, for diagnostics.</param>
internal sealed record PictureFill(byte[] Data, string Source) : Fill;

/// <summary>How a line or an outline is stroked.</summary>
internal sealed record Outline
{
    /// <summary>The width in points.</summary>
    public double Width { get; init; } = 0.75;

    /// <summary>The color (with alpha).</summary>
    public PdfColor Color { get; init; } = PdfColor.Black;

    /// <summary>The dash pattern, in multiples of the line width; empty for a solid line.</summary>
    public IReadOnlyList<double> Dash { get; init; } = [];

    /// <summary>The shape of the line's ends.</summary>
    public LineCap Cap { get; init; } = LineCap.Butt;

    /// <summary>The shape of the corners.</summary>
    public LineJoin Join { get; init; } = LineJoin.Round;

    /// <summary>The decoration at the start of an open line, if any.</summary>
    public LineEnd? Head { get; init; }

    /// <summary>The decoration at the end of an open line, if any.</summary>
    public LineEnd? Tail { get; init; }
}

/// <summary>The kinds of line end decoration.</summary>
internal enum LineEndKind
{
    /// <summary>A filled triangle.</summary>
    Triangle,

    /// <summary>An open arrow (two strokes).</summary>
    Arrow,

    /// <summary>A filled arrow with a notched back.</summary>
    Stealth,

    /// <summary>A filled diamond.</summary>
    Diamond,

    /// <summary>A filled circle.</summary>
    Oval,
}

/// <summary>A line end decoration (an arrowhead).</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Width">Its width in multiples of the line width (2, 3 or 5).</param>
/// <param name="Length">Its length in multiples of the line width (2, 3 or 5).</param>
internal sealed record LineEnd(LineEndKind Kind, double Width, double Length);
