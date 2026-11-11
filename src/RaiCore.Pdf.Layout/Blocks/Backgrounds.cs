namespace RaiCore.Pdf.Layout.Blocks;

/// <summary>How a background image repeats along one axis (CSS <c>background-repeat</c>).</summary>
public enum BackgroundRepeat
{
    /// <summary>Tiled over the whole painting area (the default).</summary>
    Repeat,

    /// <summary>Drawn once.</summary>
    NoRepeat,

    /// <summary>Tiled as often as it fits whole, with the space left shared between the tiles.</summary>
    Space,

    /// <summary>Tiled a whole number of times, scaled to fill the area exactly.</summary>
    Round,
}

/// <summary>How a background image is sized (CSS <c>background-size</c>).</summary>
public enum BackgroundSizeKind
{
    /// <summary>Its natural size (gradients: the origin box).</summary>
    Auto,

    /// <summary>Scaled to cover the origin box, keeping its aspect ratio.</summary>
    Cover,

    /// <summary>Scaled to fit inside the origin box, keeping its aspect ratio.</summary>
    Contain,

    /// <summary>The given width and height; one left out keeps the aspect ratio.</summary>
    Explicit,
}

/// <summary>The size of a background image (CSS <c>background-size</c>).</summary>
/// <param name="Kind">How it is sized.</param>
/// <param name="Width">For <see cref="BackgroundSizeKind.Explicit"/>: the width (percentages of the origin box), or <c>null</c> for auto.</param>
/// <param name="Height">For <see cref="BackgroundSizeKind.Explicit"/>: the height, or <c>null</c> for auto.</param>
public readonly record struct BackgroundSize(BackgroundSizeKind Kind, Inset? Width = null, Inset? Height = null);

/// <summary>
/// Where a background image goes along one axis (CSS <c>background-position</c>): an offset from the start edge of the
/// origin box (left or top), or from the end edge. A percentage aligns that point of the image with that point of the
/// box (50% centers it).
/// </summary>
/// <param name="Offset">The offset.</param>
/// <param name="FromEnd">Whether the offset is from the right or bottom edge.</param>
public readonly record struct BackgroundPosition(Inset Offset, bool FromEnd = false)
{
    /// <summary>The start edge (the default).</summary>
    public static BackgroundPosition Start { get; } = new(Inset.Percent(0));

    /// <summary>Centered.</summary>
    public static BackgroundPosition Center { get; } = new(Inset.Percent(50));

    /// <summary>Where the image's start edge goes, from the box's start edge, for a box and an image of these sizes.</summary>
    internal double Resolve(double box, double image)
    {
        var offset = Offset.IsPercent ? Offset.Value / 100 * (box - image) : Offset.Value;
        return FromEnd ? box - image - offset : offset;
    }
}

/// <summary>What a background layer paints: an image or a gradient.</summary>
public abstract class BackgroundImage
{
    private protected BackgroundImage()
    {
    }
}

/// <summary>An image (raster or SVG) as a background, at its natural size unless sized.</summary>
public sealed class ImageBackground : BackgroundImage
{
    /// <summary>Creates an image background.</summary>
    public ImageBackground(PdfImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
    }

    /// <summary>The image.</summary>
    public PdfImage Image { get; }
}

/// <summary>A gradient color stop: a color and, optionally, where it is (points or a percentage of the gradient line).</summary>
/// <param name="Color">The color (with alpha).</param>
/// <param name="Position">Where it is; <c>null</c> spaces it evenly between its neighbors (first 0%, last 100%).</param>
public readonly record struct GradientStop(PdfColor Color, Inset? Position = null);

/// <summary>Colors that blend along a line or out from a center (CSS gradients).</summary>
public abstract class Gradient : BackgroundImage
{
    private protected Gradient(IReadOnlyList<GradientStop> stops, bool repeating)
    {
        ArgumentNullException.ThrowIfNull(stops);
        if (stops.Count == 0)
        {
            throw new ArgumentException("A gradient needs at least one stop.", nameof(stops));
        }

        Stops = stops;
        Repeating = repeating;
    }

    /// <summary>The color stops, in order.</summary>
    public IReadOnlyList<GradientStop> Stops { get; }

    /// <summary>The stops repeat (CSS <c>repeating-linear-gradient</c>, <c>repeating-radial-gradient</c>).</summary>
    public bool Repeating { get; }
}

/// <summary>A gradient along a line through the box's center (CSS <c>linear-gradient</c>).</summary>
public sealed class LinearGradient : Gradient
{
    /// <summary>A gradient in a direction: an angle in degrees, 0 pointing up and increasing clockwise (180 is the default, down).</summary>
    public LinearGradient(double angle, IReadOnlyList<GradientStop> stops, bool repeating = false)
        : base(stops, repeating) => Angle = angle;

    /// <summary>
    /// A gradient toward a corner (CSS <c>to top right</c>): <paramref name="right"/> and <paramref name="bottom"/> pick it.
    /// Its angle depends on the box's shape, so that the corners beside it get the middle color.
    /// </summary>
    public LinearGradient(bool right, bool bottom, IReadOnlyList<GradientStop> stops, bool repeating = false)
        : base(stops, repeating) => Corner = (right ? 1 : -1, bottom ? 1 : -1);

    /// <summary>The angle in degrees, when not toward a corner.</summary>
    public double Angle { get; }

    /// <summary>The corner, as (x, y) signs with y down, or <c>null</c> for <see cref="Angle"/>.</summary>
    public (int X, int Y)? Corner { get; }
}

/// <summary>The shape of a radial gradient's ending shape.</summary>
public enum RadialShape
{
    /// <summary>An ellipse (the default), sized on each axis.</summary>
    Ellipse,

    /// <summary>A circle.</summary>
    Circle,
}

/// <summary>How far a radial gradient's ending shape reaches when not given radii (CSS size keywords).</summary>
public enum RadialExtent
{
    /// <summary>Through the box's farthest corner (the default).</summary>
    FarthestCorner,

    /// <summary>Through the box's closest corner.</summary>
    ClosestCorner,

    /// <summary>To the box's farthest side.</summary>
    FarthestSide,

    /// <summary>To the box's closest side.</summary>
    ClosestSide,
}

/// <summary>A gradient out from a center (CSS <c>radial-gradient</c>).</summary>
public sealed class RadialGradient : Gradient
{
    /// <summary>Creates a radial gradient.</summary>
    public RadialGradient(IReadOnlyList<GradientStop> stops, bool repeating = false)
        : base(stops, repeating)
    {
    }

    /// <summary>The ending shape.</summary>
    public RadialShape Shape { get; init; }

    /// <summary>How far the ending shape reaches, unless <see cref="RadiusX"/> gives it.</summary>
    public RadialExtent Extent { get; init; }

    /// <summary>The horizontal radius (for a circle, the radius); percentages of the box's width. <c>null</c> uses <see cref="Extent"/>.</summary>
    public Inset? RadiusX { get; init; }

    /// <summary>The vertical radius of an ellipse; percentages of the box's height.</summary>
    public Inset? RadiusY { get; init; }

    /// <summary>The center's horizontal position (percentages of the box's width); the default is the middle.</summary>
    public BackgroundPosition CenterX { get; init; } = BackgroundPosition.Center;

    /// <summary>The center's vertical position; the default is the middle.</summary>
    public BackgroundPosition CenterY { get; init; } = BackgroundPosition.Center;
}

/// <summary>
/// One background layer (CSS <c>background-image</c> with its <c>-repeat</c>, <c>-position</c>, <c>-size</c>,
/// <c>-origin</c> and <c>-clip</c>): an image or gradient sized and positioned in the origin box and painted, tiled if it
/// repeats, inside the clip box.
/// </summary>
/// <param name="Image">The image or gradient.</param>
public sealed record BackgroundLayer(BackgroundImage Image)
{
    /// <summary>Repetition along the x axis.</summary>
    public BackgroundRepeat RepeatX { get; init; }

    /// <summary>Repetition along the y axis.</summary>
    public BackgroundRepeat RepeatY { get; init; }

    /// <summary>The horizontal position in the origin box.</summary>
    public BackgroundPosition PositionX { get; init; } = BackgroundPosition.Start;

    /// <summary>The vertical position in the origin box.</summary>
    public BackgroundPosition PositionY { get; init; } = BackgroundPosition.Start;

    /// <summary>The size.</summary>
    public BackgroundSize Size { get; init; }

    /// <summary>The box positions and sizes are relative to (CSS <c>background-origin</c>; the padding box by default).</summary>
    public BackgroundClip Origin { get; init; } = BackgroundClip.PaddingBox;

    /// <summary>The box the layer is painted within (CSS <c>background-clip</c>).</summary>
    public BackgroundClip Clip { get; init; }
}
