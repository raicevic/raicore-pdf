using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Svg;

/// <summary>
/// Draws an SVG document on a canvas as PDF vector graphics. Unsupported elements and attributes (filters, masks,
/// markers, patterns, scripts, animation, <c>foreignObject</c>) are skipped.
/// </summary>
/// <remarks>
/// The canvas is in the SVG's user space when <see cref="Render"/> starts; the renderer also tracks that space's
/// matrix relative to the form, which gradient patterns need.
/// </remarks>
internal sealed partial class SvgRenderer
{
    /// <summary>The deepest nesting of groups and references rendered.</summary>
    private const int MaxDepth = 48;

    /// <summary>The most elements rendered, which bounds the work for <c>use</c> elements that multiply content.</summary>
    private const int MaxElements = 100_000;

    private static readonly HashSet<string> Shapes = new(StringComparer.Ordinal)
    {
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
    };

    private readonly SvgDocument _document;
    private readonly PdfCanvas _canvas;
    private readonly HashSet<SvgElement> _active = new(ReferenceEqualityComparer.Instance);
    private int _count;

    public SvgRenderer(SvgDocument document, PdfCanvas canvas)
    {
        _document = document;
        _canvas = canvas;
    }

    /// <param name="ctm">The matrix from the user space of the root to the form's space (already applied on the canvas).</param>
    /// <param name="viewport">The size that percentages refer to: the view box, or the viewport without one.</param>
    public void Render(SvgMatrix ctm, (double Width, double Height) viewport)
    {
        var root = _document.Root;
        var style = SvgStyle.Compute(SvgStyle.Initial, root, viewport);
        if (!style.Displayed || style.Opacity <= 0)
        {
            return;
        }

        var state = new RenderState(ctm, viewport, style.Opacity, 0);
        _active.Add(root);
        _canvas.SaveState();
        if (ApplyClip(root, style, state))
        {
            RenderChildren(root, style, state);
        }

        _canvas.RestoreState();
    }

    private static bool IsRenderable(string name) =>
        Shapes.Contains(name) || name is "g" or "a" or "switch" or "svg" or "use" or "text" or "image";

    private void RenderChildren(SvgElement element, SvgStyle style, RenderState state)
    {
        foreach (var child in element.Elements)
        {
            RenderElement(child, style, state);
            if (element.Name == "switch" && IsRenderable(child.Name))
            {
                break; // A switch renders its first child; conditional attributes are not evaluated.
            }
        }
    }

    private void RenderElement(SvgElement element, SvgStyle parentStyle, RenderState parent)
    {
        if (!IsRenderable(element.Name) || parent.Depth >= MaxDepth || _count >= MaxElements || !_active.Add(element))
        {
            return;
        }

        _count++;
        try
        {
            var style = SvgStyle.Compute(parentStyle, element, parent.Viewport);
            if (!style.Displayed)
            {
                return;
            }

            var transform = SvgMatrix.Parse(element["transform"]) ?? SvgMatrix.Identity;
            if (element.Name == "use")
            {
                // The use element's x and y translate the referenced content before its transform.
                var x = Length(element, "x", parent.Viewport.Width, style.FontSize);
                var y = Length(element, "y", parent.Viewport.Height, style.FontSize);
                transform = SvgMatrix.Translate(x, y).Then(transform);
            }

            var state = new RenderState(transform.Then(parent.Ctm), parent.Viewport, parent.Opacity * style.Opacity, parent.Depth + 1);
            if (!state.Ctm.IsFinite || state.Opacity <= 0)
            {
                return;
            }

            _canvas.SaveState();
            if (!transform.IsIdentity)
            {
                _canvas.Transform(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F);
            }

            if (ApplyClip(element, style, state))
            {
                switch (element.Name)
                {
                    case "g" or "a" or "switch":
                        RenderChildren(element, style, state);
                        break;
                    case "svg":
                        RenderViewport(element, element, style, state, null, null);
                        break;
                    case "use":
                        RenderUse(element, style, state);
                        break;
                    case "text":
                        RenderText(element, style, state);
                        break;
                    case "image":
                        RenderImage(element, style, state);
                        break;
                    default:
                        RenderShape(element, style, state);
                        break;
                }
            }

            _canvas.RestoreState();
        }
        finally
        {
            _active.Remove(element);
        }
    }

    private void RenderUse(SvgElement use, SvgStyle style, RenderState state)
    {
        var target = _document.GetReferenced(use);
        if (target is null || _active.Contains(target))
        {
            return; // Missing, external or circular reference.
        }

        if (target.Name is "symbol" or "svg")
        {
            // The use element's width and height size the viewport of a symbol or svg.
            if (!_active.Add(target))
            {
                return;
            }

            try
            {
                var targetStyle = SvgStyle.Compute(style, target, state.Viewport);
                if (targetStyle.Displayed && state.Opacity * targetStyle.Opacity > 0)
                {
                    var inner = state with { Opacity = state.Opacity * targetStyle.Opacity };
                    _canvas.SaveState();
                    if (ApplyClip(target, targetStyle, inner))
                    {
                        RenderViewport(target, use, targetStyle, inner, use["width"], use["height"]);
                    }

                    _canvas.RestoreState();
                }
            }
            finally
            {
                _active.Remove(target);
            }

            return;
        }

        RenderElement(target, style, state);
    }

    /// <summary>
    /// Renders the children of a nested <c>svg</c> or a <c>symbol</c> in a new viewport, clipped to it, with its view box
    /// mapped into it. <paramref name="width"/> and <paramref name="height"/> override the element's own (from a <c>use</c>).
    /// </summary>
    private void RenderViewport(SvgElement element, SvgElement positioned, SvgStyle style, RenderState state, string? width, string? height)
    {
        var fontSize = style.FontSize;
        var x = positioned == element ? Length(element, "x", state.Viewport.Width, fontSize) : 0;
        var y = positioned == element ? Length(element, "y", state.Viewport.Height, fontSize) : 0;
        var w = (SvgLength.Parse(width) ?? SvgLength.Parse(element["width"]) ?? new SvgLength(100, SvgUnit.Percent)).Resolve(fontSize, state.Viewport.Width);
        var h = (SvgLength.Parse(height) ?? SvgLength.Parse(element["height"]) ?? new SvgLength(100, SvgUnit.Percent)).Resolve(fontSize, state.Viewport.Height);
        if (w <= 0 || h <= 0)
        {
            return; // A zero-sized viewport disables rendering.
        }

        _canvas.Rectangle(x, y, w, h).Clip();
        var matrix = SvgMatrix.Translate(x, y);
        var viewport = (w, h);
        if (SvgViewBox.Parse(element["viewBox"]) is { } viewBox)
        {
            matrix = viewBox.Transform(SvgPreserveAspectRatio.Parse(element["preserveAspectRatio"]), w, h).Then(matrix);
            viewport = (viewBox.Width, viewBox.Height);
        }

        if (!matrix.IsFinite)
        {
            return;
        }

        _canvas.Transform(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);
        RenderChildren(element, style, state with { Ctm = matrix.Then(state.Ctm), Viewport = viewport });
    }

    private void RenderShape(SvgElement element, SvgStyle style, RenderState state)
    {
        if (!style.Visible || ShapePath(element, style, state.Viewport) is not { } path)
        {
            return;
        }

        SvgBounds? bounds = null;
        var measured = false;
        SvgBounds? Bounds()
        {
            if (!measured)
            {
                bounds = path.Bounds();
                measured = true;
            }

            return bounds;
        }

        // A line has no area to fill.
        var fill = element.Name == "line" ? null : ResolvePaint(style.Fill, style, style.FillOpacity * state.Opacity, Bounds, state);
        var stroke = style.StrokeWidth > 0 ? ResolvePaint(style.Stroke, style, style.StrokeOpacity * state.Opacity, Bounds, state) : null;
        if (fill is null && stroke is null)
        {
            return;
        }

        SetPaints(fill, stroke, style);
        AddPath(path);
        if (fill is not null && stroke is not null)
        {
            _ = style.FillEvenOdd ? _canvas.FillAndStrokeEvenOdd() : _canvas.FillAndStroke();
        }
        else if (fill is not null)
        {
            _ = style.FillEvenOdd ? _canvas.FillEvenOdd() : _canvas.Fill();
        }
        else
        {
            _canvas.Stroke();
        }
    }

    /// <summary>Sets the fill and stroke paints and the stroke style.</summary>
    private void SetPaints(SvgResolvedPaint? fill, SvgResolvedPaint? stroke, SvgStyle style)
    {
        if (fill is { } f)
        {
            _ = f.Pattern is { } pattern ? _canvas.SetFillPattern(pattern) : _canvas.SetFillColor(f.Color!.Value);
        }

        if (stroke is { } s)
        {
            _ = s.Pattern is { } pattern ? _canvas.SetStrokePattern(pattern) : _canvas.SetStrokeColor(s.Color!.Value);
            _canvas.SetLineWidth(style.StrokeWidth).SetLineCap(style.LineCap).SetLineJoin(style.LineJoin).SetMiterLimit(style.MiterLimit);
            if (style.DashArray.Count > 0)
            {
                _canvas.SetDash(style.DashArray, style.DashOffset);
            }
        }
    }

    /// <summary>
    /// The paint for a fill or stroke, or null when nothing is painted. A paint server that is missing or not a
    /// gradient (patterns are not supported) falls back to the paint after it, if any.
    /// </summary>
    private SvgResolvedPaint? ResolvePaint(SvgPaint paint, SvgStyle style, double opacity, Func<SvgBounds?> bounds, RenderState state)
    {
        switch (paint.Kind)
        {
            case SvgPaintKind.Color:
                return paint.Color.OnWhite(opacity) is { } color ? new SvgResolvedPaint(color, null) : null;
            case SvgPaintKind.CurrentColor:
                return style.Color.OnWhite(opacity) is { } current ? new SvgResolvedPaint(current, null) : null;
            case SvgPaintKind.Server:
                if (_document.GetElementById(paint.ServerId) is { Name: "linearGradient" or "radialGradient" } gradient)
                {
                    return SvgGradients.Resolve(_document, gradient, bounds, state.Ctm, state.Viewport, opacity);
                }

                return paint.Fallback is { } fallback ? ResolvePaint(fallback, style, opacity, bounds, state) : null;
            default:
                return null;
        }
    }

    private void AddPath(SvgPath path)
    {
        foreach (var s in path.Segments)
        {
            _ = s.Op switch
            {
                PathOp.MoveTo => _canvas.MoveTo(s.X, s.Y),
                PathOp.LineTo => _canvas.LineTo(s.X, s.Y),
                PathOp.CubicTo => _canvas.CurveTo(s.X1, s.Y1, s.X2, s.Y2, s.X, s.Y),
                _ => _canvas.ClosePath(),
            };
        }
    }

    /// <summary>The geometry of a basic shape or path in its user space, or null when it draws nothing.</summary>
    private static SvgPath? ShapePath(SvgElement element, SvgStyle style, (double Width, double Height) viewport)
    {
        var (vw, vh) = viewport;
        var diagonal = SvgStyle.Diagonal(viewport);
        var fontSize = style.FontSize;
        double L(string name, double reference) => Length(element, name, reference, fontSize);
        var path = new SvgPath();
        switch (element.Name)
        {
            case "path":
                path = SvgPathData.Parse(element["d"]);
                break;
            case "rect":
                {
                    var (width, height) = (L("width", vw), L("height", vh));
                    if (width <= 0 || height <= 0)
                    {
                        return null;
                    }

                    // A missing (or negative) radius takes the other one's value; both are capped at half the size.
                    double? Radius(string name, double reference) =>
                        SvgLength.Parse(element[name]) is { } r && r.Value >= 0 ? r.Resolve(fontSize, reference) : null;
                    var (rx, ry) = (Radius("rx", vw), Radius("ry", vh));
                    var radiusX = Math.Min(rx ?? ry ?? 0, width / 2);
                    var radiusY = Math.Min(ry ?? rx ?? 0, height / 2);
                    path.Rectangle(L("x", vw), L("y", vh), width, height, radiusX, radiusY);
                    break;
                }

            case "circle":
                {
                    var r = L("r", diagonal);
                    if (r <= 0)
                    {
                        return null;
                    }

                    path.Ellipse(L("cx", vw), L("cy", vh), r, r);
                    break;
                }

            case "ellipse":
                {
                    var rx = element["rx"] is null ? L("ry", vh) : L("rx", vw);
                    var ry = element["ry"] is null ? rx : L("ry", vh);
                    if (rx <= 0 || ry <= 0)
                    {
                        return null;
                    }

                    path.Ellipse(L("cx", vw), L("cy", vh), rx, ry);
                    break;
                }

            case "line":
                path.MoveTo(L("x1", vw), L("y1", vh));
                path.LineTo(L("x2", vw), L("y2", vh));
                break;
            case "polyline" or "polygon":
                {
                    var points = SvgScanner.ReadNumbers(element["points"]);
                    if (points.Count < 4)
                    {
                        return null;
                    }

                    path.MoveTo(points[0], points[1]);
                    for (var i = 2; i + 1 < points.Count; i += 2)
                    {
                        path.LineTo(points[i], points[i + 1]);
                    }

                    if (element.Name == "polygon")
                    {
                        path.Close();
                    }

                    break;
                }

            default:
                return null;
        }

        return path.HasDrawing && path.IsFinite() ? path : null;
    }

    /// <summary>A length attribute in user units; percentages refer to <paramref name="reference"/>; invalid or missing is 0.</summary>
    private static double Length(SvgElement element, string name, double reference, double fontSize) =>
        SvgLength.Parse(element[name])?.Resolve(fontSize, reference) ?? 0;

    /// <summary>
    /// Clips to the element's <c>clip-path</c>, if it refers to a <c>clipPath</c>. Returns false when the clip leaves
    /// nothing visible (an empty clip path), so the element need not be drawn.
    /// </summary>
    private bool ApplyClip(SvgElement element, SvgStyle style, RenderState state)
    {
        if (_document.GetElementById(style.ClipPath) is not { Name: "clipPath" } clip || !_active.Add(clip))
        {
            return true; // No clip, a reference to something else (ignored, as in browsers), or a cycle.
        }

        try
        {
            var matrix = SvgMatrix.Parse(clip["transform"]) ?? SvgMatrix.Identity;
            if (clip["clipPathUnits"]?.Trim() == "objectBoundingBox")
            {
                if (GeometryBounds(element, style, state.Viewport, 0) is not { } box || box.Width <= 0 || box.Height <= 0)
                {
                    return false;
                }

                matrix = matrix.Then(new SvgMatrix(box.Width, 0, 0, box.Height, box.MinX, box.MinY));
            }

            var clipStyle = SvgStyle.Compute(SvgStyle.Initial, clip, state.Viewport);

            // A clip path can itself be clipped; the clips intersect.
            if (clipStyle.ClipPath is not null && !ApplyClip(clip, clipStyle, state))
            {
                return false;
            }

            var path = new SvgPath();
            bool? evenOdd = null;
            foreach (var child in clip.Elements)
            {
                AddClipGeometry(child, clipStyle, matrix, state.Viewport, path, ref evenOdd, 0);
            }

            if (!path.HasDrawing || !path.IsFinite())
            {
                return false;
            }

            AddPath(path);
            _canvas.Clip(evenOdd ?? false);
            return true;
        }
        finally
        {
            _active.Remove(clip);
        }
    }

    /// <summary>
    /// Adds a clip path child's geometry to <paramref name="path"/>, transformed into the clipped element's user space.
    /// The children's union is approximated by one path with the first child's clip rule. Text is not supported.
    /// </summary>
    private void AddClipGeometry(SvgElement element, SvgStyle parentStyle, SvgMatrix matrix, (double Width, double Height) viewport, SvgPath path, ref bool? evenOdd, int depth)
    {
        var style = SvgStyle.Compute(parentStyle, element, viewport);
        if (!style.Displayed || !style.Visible || depth > MaxDepth)
        {
            return;
        }

        var transform = (SvgMatrix.Parse(element["transform"]) ?? SvgMatrix.Identity).Then(matrix);
        if (element.Name == "use")
        {
            if (_document.GetReferenced(element) is { } target && Shapes.Contains(target.Name))
            {
                var offset = SvgMatrix.Translate(Length(element, "x", viewport.Width, style.FontSize), Length(element, "y", viewport.Height, style.FontSize));
                AddClipGeometry(target, style, offset.Then(transform), viewport, path, ref evenOdd, depth + 1);
            }

            return;
        }

        if (Shapes.Contains(element.Name) && ShapePath(element, style, viewport) is { } shape)
        {
            path.Append(shape, transform);
            evenOdd ??= style.ClipEvenOdd;
        }
    }

    /// <summary>
    /// The bounding box of an element's geometry in its user space (before its own transform), for
    /// <c>objectBoundingBox</c> units; null when it has none.
    /// </summary>
    private SvgBounds? GeometryBounds(SvgElement element, SvgStyle style, (double Width, double Height) viewport, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        if (Shapes.Contains(element.Name))
        {
            return ShapePath(element, style, viewport)?.Bounds();
        }

        if (element.Name == "text")
        {
            return TextBounds(element, style, viewport);
        }

        if (element.Name == "use")
        {
            if (_document.GetReferenced(element) is not { } target || target == element)
            {
                return null;
            }

            var targetStyle = SvgStyle.Compute(style, target, viewport);
            var offset = SvgMatrix.Translate(Length(element, "x", viewport.Width, style.FontSize), Length(element, "y", viewport.Height, style.FontSize));
            var targetTransform = SvgMatrix.Parse(target["transform"]) ?? SvgMatrix.Identity;
            return GeometryBounds(target, targetStyle, viewport, depth + 1)?.Transform(targetTransform.Then(offset));
        }

        SvgBounds? result = null;
        if (element.Name is "g" or "a" or "switch" or "svg" or "symbol")
        {
            foreach (var child in element.Elements.Where(e => IsRenderable(e.Name)))
            {
                var childStyle = SvgStyle.Compute(style, child, viewport);
                if (!childStyle.Displayed || GeometryBounds(child, childStyle, viewport, depth + 1) is not { } box)
                {
                    continue;
                }

                var transform = SvgMatrix.Parse(child["transform"]) ?? SvgMatrix.Identity;
                box = box.Transform(transform);
                result = result is { } r ? r.Union(box) : box;
            }
        }

        return result;
    }

    /// <summary>The current transformation (user space to form space), viewport, group opacity and nesting depth.</summary>
    private readonly record struct RenderState(SvgMatrix Ctm, (double Width, double Height) Viewport, double Opacity, int Depth);
}
