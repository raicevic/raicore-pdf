using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf;

/// <summary>Links placed in drawing coordinates: the canvas follows the current transformation to find them on the page.</summary>
public sealed partial class PdfCanvas
{
    private readonly Stack<SvgMatrix> _savedTransforms = new();

    /// <summary>The current transformation matrix, as set by <see cref="Transform"/> and restored by <see cref="RestoreState"/>.</summary>
    private SvgMatrix _transform = SvgMatrix.Identity;

    /// <summary>
    /// Makes a rectangle a link that opens <paramref name="uri"/>, like
    /// <see cref="PdfPage.AddLink(double, double, double, double, Uri)"/> but in the canvas's current coordinates: after
    /// <see cref="Transform"/>, the link covers the transformed rectangle (its bounding box, when rotated or skewed).
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="uri">The URI to open.</param>
    public PdfLink AddLink(double x, double y, double width, double height, Uri uri)
    {
        var (px, py, pw, ph) = ToPage(x, y, width, height);
        return _page.AddLink(px, py, pw, ph, uri);
    }

    /// <summary>
    /// Makes a rectangle a link to a named destination, like
    /// <see cref="PdfPage.AddLink(double, double, double, double, string)"/> but in the canvas's current coordinates.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="destinationName">The name of the destination (see <see cref="PdfDocument.NamedDestinations"/>).</param>
    public PdfLink AddLink(double x, double y, double width, double height, string destinationName)
    {
        var (px, py, pw, ph) = ToPage(x, y, width, height);
        return _page.AddLink(px, py, pw, ph, destinationName);
    }

    /// <summary>
    /// Makes a rectangle a link to a place in the document, like
    /// <see cref="PdfPage.AddLink(double, double, double, double, PdfDestination)"/> but in the canvas's current coordinates.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The bottom edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="destination">Where to jump to.</param>
    public PdfLink AddLink(double x, double y, double width, double height, PdfDestination destination)
    {
        var (px, py, pw, ph) = ToPage(x, y, width, height);
        return _page.AddLink(px, py, pw, ph, destination);
    }

    private void PushTransform() => _savedTransforms.Push(_transform);

    private void PopTransform()
    {
        if (_savedTransforms.Count > 0)
        {
            _transform = _savedTransforms.Pop();
        }
    }

    /// <summary>The <c>cm</c> operator: the new matrix applies first, then the current one.</summary>
    private void ConcatTransform(double a, double b, double c, double d, double e, double f) =>
        _transform = new SvgMatrix(a, b, c, d, e, f).Then(_transform);

    /// <summary>The bounding box, in page coordinates, of a rectangle in the current coordinates.</summary>
    private (double X, double Y, double Width, double Height) ToPage(double x, double y, double width, double height)
    {
        if (_transform.IsIdentity)
        {
            return (x, y, width, height);
        }

        Span<(double X, double Y)> corners =
        [
            _transform.Apply(x, y), _transform.Apply(x + width, y), _transform.Apply(x, y + height), _transform.Apply(x + width, y + height),
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (cx, cy) in corners)
        {
            (minX, maxX) = (Math.Min(minX, cx), Math.Max(maxX, cx));
            (minY, maxY) = (Math.Min(minY, cy), Math.Max(maxY, cy));
        }

        return (minX, minY, maxX - minX, maxY - minY);
    }
}
