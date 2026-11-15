using System.Globalization;
using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf;

/// <summary>
/// Where content lands on the page, for the bounding boxes (<c>/BBox</c>) of figures, formulas, forms and tables in a
/// tagged document, and the strict tagging check (<see cref="PdfDocument.StrictTagging"/>).
/// </summary>
/// <remarks>
/// Bounds are tracked only in a tagged document. They are conservative: a curve counts with its exact extent, a
/// stroke with three quarters of its line width on each side (enough for round and square caps and for miter joins
/// of right angles), text with the font's ascent and descent over its advance, and an image or imported page with its
/// whole rectangle. Everything is cut to the clip (as a rectangle around the clipping paths).
/// </remarks>
public sealed partial class PdfCanvas
{
    private readonly Stack<(double LineWidth, PdfRectangle? Clip)> _savedBounds = new();

    /// <summary>The bounding box, in page coordinates, of the path being built; <c>null</c> when it has no points yet.</summary>
    private PdfRectangle? _pathBounds;

    /// <summary>The line width in user space units, as set by <see cref="SetLineWidth"/>.</summary>
    private double _lineWidth = 1;

    /// <summary>The bounding box of the clip in page coordinates; <c>null</c> for no clip (the whole page).</summary>
    private PdfRectangle? _clip;

    /// <summary>Whether the bounds of what is drawn are tracked: only in a tagged document.</summary>
    private bool TracksBounds => Tagging;

    private void PushBounds() => _savedBounds.Push((_lineWidth, _clip));

    private void PopBounds()
    {
        if (_savedBounds.TryPop(out var saved))
        {
            (_lineWidth, _clip) = saved;
        }
    }

    /// <summary>The current point of the path being built and the start of its subpath, in page coordinates.</summary>
    private (double X, double Y)? _currentPoint, _subpathStart;

    /// <summary>Adds a point of the current path (in user space) that a straight line, or a move, ends at.</summary>
    private void IncludePathPoint(double x, double y, bool move = false)
    {
        if (!TracksBounds)
        {
            return;
        }

        var point = _transform.Apply(x, y);
        IncludePagePoint(point);
        _currentPoint = point;
        if (move || _subpathStart is null)
        {
            _subpathStart = point;
        }
    }

    /// <summary>
    /// Adds a cubic Bezier curve from the current point (in user space): its exact extent, from the curve's extremes.
    /// An affine transformation maps a Bezier curve to the Bezier curve of the mapped control points, so the extremes are
    /// found in page coordinates.
    /// </summary>
    private void IncludeCurve(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (!TracksBounds)
        {
            return;
        }

        var (p1, p2, p3) = (_transform.Apply(x1, y1), _transform.Apply(x2, y2), _transform.Apply(x3, y3));
        var p0 = _currentPoint ?? p1; // A curve without a current point is an error in PDF; start it at its first control point.
        _subpathStart ??= p0;
        IncludePagePoint(p0);
        IncludePagePoint(p3);
        Span<double> roots = stackalloc double[4];
        var count = Extremes(p0.X, p1.X, p2.X, p3.X, roots);
        count += Extremes(p0.Y, p1.Y, p2.Y, p3.Y, roots[count..]);
        foreach (var t in roots[..count])
        {
            var u = 1 - t;
            var (a, b, c, d) = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t);
            IncludePagePoint(((a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X), (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y)));
        }

        _currentPoint = p3;
    }

    /// <summary>The parameters in (0, 1) where a cubic Bezier coordinate has a minimum or maximum; returns how many.</summary>
    private static int Extremes(double p0, double p1, double p2, double p3, Span<double> roots)
    {
        // The derivative, divided by 3: a*t^2 + b*t + c.
        var a = -p0 + (3 * p1) - (3 * p2) + p3;
        var b = 2 * (p0 - (2 * p1) + p2);
        var c = p1 - p0;
        var count = 0;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
            {
                count = Add(roots, count, -c / b);
            }

            return count;
        }

        var discriminant = (b * b) - (4 * a * c);
        if (discriminant < 0)
        {
            return 0;
        }

        var root = Math.Sqrt(discriminant);
        count = Add(roots, count, (-b + root) / (2 * a));
        return Add(roots, count, (-b - root) / (2 * a));

        static int Add(Span<double> roots, int count, double t)
        {
            if (t is > 0 and < 1)
            {
                roots[count++] = t;
            }

            return count;
        }
    }

    /// <summary>Closes the subpath: the current point goes back to its start.</summary>
    private void ClosePathBounds() => _currentPoint = _subpathStart;

    /// <summary>Adds a rectangle of the current path, in user space (a closed subpath starting at its corner).</summary>
    private void IncludePathRectangle(double x, double y, double width, double height)
    {
        if (!TracksBounds)
        {
            return;
        }

        IncludeBounds(PageBounds(x, y, width, height));
        _currentPoint = _subpathStart = _transform.Apply(x, y);
    }

    private void IncludePagePoint((double X, double Y) point) => IncludeBounds(new PdfRectangle(point.X, point.Y, 0, 0));

    private void IncludeBounds(PdfRectangle bounds) => _pathBounds = _pathBounds is { } current ? Union(current, bounds) : bounds;

    /// <summary>Forgets the path once it is painted, used as a clip or ended.</summary>
    private PdfRectangle? EndPathBounds()
    {
        var bounds = _pathBounds;
        (_pathBounds, _currentPoint, _subpathStart) = (null, null, null);
        return bounds;
    }

    /// <summary>The bounds of the current path as filled (<paramref name="stroke"/> false) or stroked, and ends the path.</summary>
    private PdfRectangle? TakePathBounds(bool stroke)
    {
        var bounds = EndPathBounds();
        if (!stroke || bounds is not { } path)
        {
            return bounds;
        }

        // Three quarters of the width covers half of it, plus square caps and miters of right angles (sqrt(2)/2). The width
        // scales like the transformed unit vectors.
        var t = _transform;
        var scale = Math.Max(Math.Sqrt((t.A * t.A) + (t.B * t.B)), Math.Sqrt((t.C * t.C) + (t.D * t.D)));
        var pad = 0.75 * _lineWidth * scale;
        return new PdfRectangle(path.X - pad, path.Y - pad, path.Width + (2 * pad), path.Height + (2 * pad));
    }

    /// <summary>Intersects the clip with the current path's bounds, and ends the path.</summary>
    private void ClipToPath()
    {
        var path = EndPathBounds() ?? new PdfRectangle(0, 0, 0, 0);
        _clip = _clip is { } clip ? clip.Intersect(path) ?? new PdfRectangle(path.X, path.Y, 0, 0) : path;
    }

    /// <summary>The bounding box, in page coordinates, of text drawn with its baseline starting at (x, y).</summary>
    private PdfRectangle TextBounds(PdfFont font, double size, double x, double y, double left, double right, double offsetBottom = 0, double offsetTop = 0)
    {
        var (bottom, top) = GlyphHeights(font);
        return PageBounds(x + left, y + offsetBottom + (bottom * size), right - left, (top - bottom) * size + (offsetTop - offsetBottom));
    }

    /// <summary>The advance width of text, or (for a font without widths) an em per character, which is at least as wide.</summary>
    private static double TextWidth(string text, PdfFont font, double size) =>
        font.CanMeasure ? font.MeasureText(text, size) : text.EnumerateRunes().Count() * size;

    /// <summary>How far glyphs reach below and above the baseline, as fractions of the font size.</summary>
    private static (double Bottom, double Top) GlyphHeights(PdfFont font) => font is OpenTypePdfFont openType
        ? (Math.Min(font.Descent, openType.Font.YMin / (double)openType.Font.UnitsPerEm), Math.Max(font.Ascent, openType.Font.YMax / (double)openType.Font.UnitsPerEm))
        : (font.Descent, font.Ascent);

    /// <summary>The bounding box, in page coordinates, of a rectangle in user space.</summary>
    private PdfRectangle PageBounds(double x, double y, double width, double height)
    {
        var (px, py, pw, ph) = ToPage(x, y, width, height);
        return new PdfRectangle(px, py, pw, ph);
    }

    /// <summary>
    /// Before a painting operator: checks strict tagging, and adds the painted area (in page coordinates; <c>null</c>
    /// for nothing visible) to the figures, formulas, forms and tables it is drawn in.
    /// </summary>
    /// <param name="what">What is painted, for the strict tagging error.</param>
    /// <param name="bounds">The area painted, before clipping.</param>
    private void Painting(string what, PdfRectangle? bounds)
    {
        if (!Tagging)
        {
            return;
        }

        if (_page.Document is { StrictTagging: true } document && ReferenceEquals(CurrentTag, Tag.Untagged))
        {
            var number = document.Pages.TakeWhile(page => !ReferenceEquals(page, _page)).Count() + 1;
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Strict tagging: {what} was drawn on page {number} outside any structure element or artifact. Draw it between BeginTag and EndTag, or BeginArtifact and EndTag."));
        }

        if (bounds is not { } painted)
        {
            return;
        }

        var visible = painted;
        if (_clip is { } clip)
        {
            // Zero-width or zero-height content (a hairline, a dot) still counts, so it is grown before clipping.
            var grown = painted with { Width = Math.Max(painted.Width, 1e-9), Height = Math.Max(painted.Height, 1e-9) };
            if (clip.Intersect(grown) is not { } clipped)
            {
                return; // Clipped away.
            }

            visible = clipped;
        }

        var onPage = new PdfRectangle(visible.X + _page.Origin.X, visible.Y + _page.Origin.Y, visible.Width, visible.Height);
        foreach (var tag in _tags)
        {
            for (var element = tag.Element; element is not null; element = element.Parent)
            {
                if (PdfStructureElement.TakesBoundingBox(element.Type))
                {
                    element.IncludeDrawn(_page, onPage);
                }
            }
        }
    }

    internal static PdfRectangle Union(PdfRectangle a, PdfRectangle b)
    {
        var left = Math.Min(a.X, b.X);
        var bottom = Math.Min(a.Y, b.Y);
        return new PdfRectangle(left, bottom, Math.Max(a.Right, b.Right) - left, Math.Max(a.Top, b.Top) - bottom);
    }
}
