using RaiCore.Pdf.DrawingML;

namespace RaiCore.Pdf.Slides.Model;

/// <summary>
/// A presentation ready to draw: the slide size and the slides in order, with everything their masters, layouts and
/// themes contribute already resolved (inherited positions and text styles, theme colors and fonts). Readers of the
/// file formats fill it (<see cref="Pptx.PptxReader"/>, and later the PowerPoint 97-2003 reader); the renderer
/// (<see cref="Rendering.SlideRenderer"/>) draws it. All lengths are in points, y downwards from the slide's top-left
/// corner.
/// </summary>
internal sealed class Presentation
{
    /// <summary>The slide width in points.</summary>
    public double Width { get; init; }

    /// <summary>The slide height in points.</summary>
    public double Height { get; init; }

    /// <summary>The title of the document properties, if any.</summary>
    public string? Title { get; init; }

    /// <summary>The author of the document properties, if any.</summary>
    public string? Author { get; init; }

    /// <summary>The slides in presentation order (hidden ones included, flagged).</summary>
    public List<Slide> Slides { get; } = [];
}

/// <summary>One slide: its background, and its shapes in drawing order (the master's and layout's first).</summary>
internal sealed class Slide
{
    /// <summary>The slide's number, from 1, in presentation order (hidden slides counted).</summary>
    public int Number { get; init; }

    /// <summary>Whether the slide is hidden in a slide show.</summary>
    public bool Hidden { get; init; }

    /// <summary>The background, or <c>null</c> for white.</summary>
    public Fill? Background { get; set; }

    /// <summary>The shapes in z-order, back to front.</summary>
    public List<Shape> Shapes { get; } = [];
}

/// <summary>Where a shape is: its box in its parent's coordinates, turned and mirrored about its center.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Rotation">The clockwise rotation in degrees, about the box's center.</param>
/// <param name="FlipH">Whether the shape is mirrored left to right.</param>
/// <param name="FlipV">Whether the shape is mirrored top to bottom.</param>
internal readonly record struct ShapeBox(double X, double Y, double Width, double Height, double Rotation = 0, bool FlipH = false, bool FlipV = false);

/// <summary>What all shapes have.</summary>
internal abstract class Shape
{
    /// <summary>The shape's box.</summary>
    public ShapeBox Box { get; set; }

    /// <summary>The shape's name, for diagnostics.</summary>
    public string? Name { get; init; }

    /// <summary>The alternative text (description) of the shape, for tagged PDF.</summary>
    public string? AltText { get; init; }

    /// <summary>
    /// The kind of placeholder the shape is, or <see cref="PlaceholderKind.None"/>: a title is tagged as a heading.
    /// </summary>
    public PlaceholderKind Placeholder { get; init; }

    /// <summary>A link the whole shape opens, if any (a click action with an external target).</summary>
    public Uri? Link { get; init; }

    /// <summary>
    /// Whether the shape comes from the slide's master or layout (a logo, a band): background graphics, which a tagged
    /// PDF marks as artifacts rather than content.
    /// </summary>
    public bool Background { get; set; }
}

/// <summary>The kinds of placeholder that matter when drawing.</summary>
internal enum PlaceholderKind
{
    /// <summary>Not a placeholder.</summary>
    None,

    /// <summary>A title or centered title.</summary>
    Title,

    /// <summary>Any other placeholder.</summary>
    Other,
}

/// <summary>A shape drawn from a geometry: a rectangle, an ellipse, an arrow, a line, a free form, a text box.</summary>
internal sealed class AutoShape : Shape
{
    /// <summary>The outline of the shape.</summary>
    public Geometry Geometry { get; set; } = Geometry.Rectangle;

    /// <summary>The fill, or <c>null</c> for none.</summary>
    public Fill? Fill { get; set; }

    /// <summary>The outline's stroke, or <c>null</c> for none.</summary>
    public Outline? Line { get; set; }

    /// <summary>The text, or <c>null</c> for none.</summary>
    public TextFrame? Text { get; set; }
}

/// <summary>A picture: image data placed in its box, optionally cropped and clipped to a geometry.</summary>
internal sealed class PictureShape : Shape
{
    /// <summary>The image file's bytes (PNG, JPEG, GIF, BMP, TIFF, WebP or SVG), or <c>null</c> when it is missing.</summary>
    public byte[]? Data { get; init; }

    /// <summary>The part name of the image, for diagnostics.</summary>
    public string? Source { get; init; }

    /// <summary>The fractions cut off each side of the image (negative values add space).</summary>
    public Crop Crop { get; init; }

    /// <summary>The shape the picture is clipped to (a rectangle by default).</summary>
    public Geometry Geometry { get; init; } = Geometry.Rectangle;

    /// <summary>The picture's border, or <c>null</c> for none.</summary>
    public Outline? Line { get; init; }

    /// <summary>The fill behind the picture (seen through its transparent parts), or <c>null</c> for none.</summary>
    public Fill? Fill { get; init; }

    /// <summary>The opacity, from 0 to 1.</summary>
    public double Opacity { get; init; } = 1;
}

/// <summary>The fractions of an image cut off its sides.</summary>
internal readonly record struct Crop(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Whether nothing is cut off.</summary>
    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>
/// Shapes grouped together: the children's boxes are in the group's own coordinate space (<see cref="ChildX"/> and so on),
/// which is mapped onto the group's box.
/// </summary>
internal sealed class GroupShape : Shape
{
    /// <summary>The left edge of the children's coordinate space.</summary>
    public double ChildX { get; init; }

    /// <summary>The top edge of the children's coordinate space.</summary>
    public double ChildY { get; init; }

    /// <summary>The width of the children's coordinate space.</summary>
    public double ChildWidth { get; init; }

    /// <summary>The height of the children's coordinate space.</summary>
    public double ChildHeight { get; init; }

    /// <summary>The children in z-order.</summary>
    public List<Shape> Children { get; } = [];
}

/// <summary>A table: a grid of columns and rows whose cells hold text, fills and borders.</summary>
internal sealed class TableShape : Shape
{
    /// <summary>The column widths.</summary>
    public List<double> Columns { get; } = [];

    /// <summary>The rows.</summary>
    public List<TableRow> Rows { get; } = [];
}

/// <summary>A table row: its least height and its cells, one per column (merged ones flagged).</summary>
internal sealed class TableRow
{
    /// <summary>The least height; the row grows to fit its text.</summary>
    public double Height { get; init; }

    /// <summary>The cells.</summary>
    public List<TableCell> Cells { get; } = [];
}

/// <summary>A table cell.</summary>
internal sealed class TableCell
{
    /// <summary>The text.</summary>
    public TextFrame Text { get; init; } = new();

    /// <summary>The fill, or <c>null</c> for none.</summary>
    public Fill? Fill { get; init; }

    /// <summary>The left border, or <c>null</c>.</summary>
    public Outline? Left { get; init; }

    /// <summary>The top border, or <c>null</c>.</summary>
    public Outline? Top { get; init; }

    /// <summary>The right border, or <c>null</c>.</summary>
    public Outline? Right { get; init; }

    /// <summary>The bottom border, or <c>null</c>.</summary>
    public Outline? Bottom { get; init; }

    /// <summary>How many columns the cell spans.</summary>
    public int ColumnSpan { get; init; } = 1;

    /// <summary>How many rows the cell spans.</summary>
    public int RowSpan { get; init; } = 1;

    /// <summary>Whether the cell is covered by a cell that spans over it (it is not drawn).</summary>
    public bool Merged { get; init; }
}
