using RaiCore.Pdf.Charts;
using RaiCore.Pdf.DrawingML;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets.Drawings;

/// <summary>
/// One edge of a drawing's anchor: in a row (or column), at an offset from its start of a fraction of its size plus a
/// distance in points at a scale of 100% (spreadsheets anchor in EMUs inside a cell, the binary format in fractions of
/// it). The offset is at most the size of its row or column, as the row or column may be smaller in the PDF than in
/// the spreadsheet; outside the rows or columns that are printed it may be negative (before the first) or run on (from
/// the end of the last).
/// </summary>
/// <param name="Index">The row or column, from 0 (in the source while the sheet is read, then in the printed sheet).</param>
/// <param name="Points">The distance from its start, in points at 100%.</param>
/// <param name="Fraction">The distance from its start as a fraction of its size.</param>
internal readonly record struct AnchorEdge(int Index, double Points, double Fraction = 0);

/// <summary>How a drawing is anchored to its sheet.</summary>
internal enum AnchorKind
{
    /// <summary>
    /// Its top-left corner in a cell and its bottom-right corner in another (<c>xdr:twoCellAnchor</c>, and the binary
    /// format's anchors): it moves and stretches with the rows and columns between them.
    /// </summary>
    TwoCell,

    /// <summary>Its top-left corner in a cell and a size of its own (<c>xdr:oneCellAnchor</c>).</summary>
    OneCell,

    /// <summary>
    /// Its top-left corner at a distance from the sheet's top-left corner, and a size of its own
    /// (<c>xdr:absoluteAnchor</c>); the distance is measured over the printed rows and columns.
    /// </summary>
    Absolute,
}

/// <summary>
/// A drawing object of a worksheet: a picture, shape, chart or group, where it is anchored and how big it is. For
/// <see cref="AnchorKind.Absolute"/>, <see cref="Left"/> and <see cref="Top"/> have index 0 and the distance in points.
/// </summary>
internal sealed record SheetDrawing(AnchorKind Kind, AnchorEdge Left, AnchorEdge Top, DrawingItem Item)
{
    /// <summary>The right edge (<see cref="AnchorKind.TwoCell"/>).</summary>
    public AnchorEdge Right { get; init; }

    /// <summary>The bottom edge (<see cref="AnchorKind.TwoCell"/>).</summary>
    public AnchorEdge Bottom { get; init; }

    /// <summary>The width in points at 100% (one-cell and absolute anchors).</summary>
    public double Width { get; init; }

    /// <summary>The height in points at 100% (one-cell and absolute anchors).</summary>
    public double Height { get; init; }
}

/// <summary>
/// What a drawing shows, drawn into a box (its anchor's, or its place in a group): turned by <see cref="Rotation"/>
/// degrees clockwise about the box's center and mirrored as the flips say.
/// </summary>
internal abstract class DrawingItem
{
    /// <summary>The rotation in degrees, clockwise.</summary>
    public double Rotation { get; init; }

    public bool FlipH { get; init; }

    public bool FlipV { get; init; }

    /// <summary>The name the file gives it (in messages), or <c>null</c>.</summary>
    public string? Name { get; init; }
}

/// <summary>A picture: the image stretched over the box, after the crop, with an optional border.</summary>
internal sealed class PictureItem(PdfImage image) : DrawingItem
{
    public PdfImage Image { get; } = image;

    /// <summary>The fractions cut off each side of the image (negative to pad it), left, top, right, bottom.</summary>
    public (double Left, double Top, double Right, double Bottom) Crop { get; init; }

    /// <summary>The border, or <c>null</c>.</summary>
    public ShapeLine? Line { get; init; }

    /// <summary>The geometry the picture is clipped to, or <c>null</c> for its box.</summary>
    public Geometry? Geometry { get; init; }
}

/// <summary>A shape, text box or connector: its geometry filled and outlined, and its text.</summary>
internal sealed class ShapeItem(Geometry geometry) : DrawingItem
{
    public Geometry Geometry { get; } = geometry;

    /// <summary>The fill, or <c>null</c> for none.</summary>
    public PdfColor? Fill { get; init; }

    /// <summary>The outline, or <c>null</c> for none.</summary>
    public ShapeLine? Line { get; init; }

    /// <summary>The text, or <c>null</c>.</summary>
    public ShapeText? Text { get; init; }
}

/// <summary>A chart, drawn by the chart engine.</summary>
internal sealed class ChartItem(Chart chart) : DrawingItem
{
    public Chart Chart { get; } = chart;
}

/// <summary>
/// A group: its items placed in a coordinate space of its own (<see cref="ChildBounds"/>) that is mapped onto the box.
/// </summary>
internal sealed class GroupItem(IReadOnlyList<(DrawingItem Item, PdfRectangle Box)> children, PdfRectangle childBounds) : DrawingItem
{
    /// <summary>The items in z-order with their boxes in the group's coordinates (y downwards).</summary>
    public IReadOnlyList<(DrawingItem Item, PdfRectangle Box)> Children { get; } = children;

    /// <summary>The rectangle of the group's coordinates that its box shows (y downwards).</summary>
    public PdfRectangle ChildBounds { get; } = childBounds;
}

/// <summary>An outline: its width in points (0 for the thinnest line), color and dash pattern (in multiples of the width).</summary>
internal sealed record ShapeLine(double Width, PdfColor Color, IReadOnlyList<double> Dash);

/// <summary>The text of a shape: paragraphs in a box inside the shape, with insets, vertical anchoring and wrapping.</summary>
internal sealed class ShapeText(IReadOnlyList<ShapeParagraph> paragraphs)
{
    public IReadOnlyList<ShapeParagraph> Paragraphs { get; } = paragraphs;

    /// <summary>The insets from the shape's text rectangle, in points.</summary>
    public (double Left, double Top, double Right, double Bottom) Insets { get; init; } = (7.2, 3.6, 7.2, 3.6);

    /// <summary>Where the text sits vertically.</summary>
    public VerticalAlignment Anchor { get; init; } = VerticalAlignment.Top;

    /// <summary>Whether lines wrap at the box's width.</summary>
    public bool Wrap { get; init; } = true;
}

/// <summary>A paragraph of a shape's text.</summary>
internal sealed record ShapeParagraph(IReadOnlyList<ShapeRun> Runs, TextAlignment Alignment);

/// <summary>A run of a shape's text in one format.</summary>
internal sealed record ShapeRun(string Text, double Size, bool Bold, bool Italic, bool Underline, PdfColor Color, string? Font);
