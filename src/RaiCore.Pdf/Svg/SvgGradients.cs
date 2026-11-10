using System.Globalization;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Svg;

/// <summary>A resolved paint: a solid color or a pattern (a gradient as a PDF shading pattern).</summary>
internal readonly record struct SvgResolvedPaint(PdfColor? Color, PdfDictionary? Pattern);

/// <summary>
/// Turns <c>linearGradient</c> and <c>radialGradient</c> elements into PDF shading patterns: axial (type 2) and
/// radial (type 3) shadings whose color function stitches one exponential interpolation per pair of stops.
/// </summary>
/// <remarks>
/// <c>spreadMethod</c> <c>reflect</c> and <c>repeat</c> are drawn as <c>pad</c>. Translucent stops are blended with
/// white, like all SVG transparency until PDF transparency is supported.
/// </remarks>
internal static class SvgGradients
{
    private const int MaxReferenceChain = 16;

    /// <summary>
    /// The paint for a gradient applied to an element, or null when it paints nothing (no stops, or a bounding-box
    /// gradient on an element without area).
    /// </summary>
    /// <param name="document">The document, to follow <c>href</c> references.</param>
    /// <param name="gradient">The gradient element.</param>
    /// <param name="bounds">The element's bounding box in its user space, for <c>objectBoundingBox</c> units.</param>
    /// <param name="ctm">The element's user space to form space.</param>
    /// <param name="viewport">The viewport for percentages in <c>userSpaceOnUse</c> units.</param>
    /// <param name="opacity">The fill or stroke opacity, applied to every stop.</param>
    public static SvgResolvedPaint? Resolve(
        SvgDocument document, SvgElement gradient, Func<SvgBounds?> bounds, SvgMatrix ctm, (double Width, double Height) viewport, double opacity)
    {
        var chain = Chain(document, gradient);
        var stops = Stops(chain, viewport, opacity);
        if (stops.Count == 0)
        {
            return null;
        }

        if (stops.Count == 1)
        {
            return new SvgResolvedPaint(stops[0].Color, null);
        }

        var userSpace = Attribute(chain, "gradientUnits") == "userSpaceOnUse";
        var units = SvgMatrix.Identity;
        if (!userSpace)
        {
            if (bounds() is not { } box || box.Width <= 0 || box.Height <= 0)
            {
                return null; // A bounding box without area cannot map the gradient.
            }

            units = new SvgMatrix(box.Width, 0, 0, box.Height, box.MinX, box.MinY);
        }

        double Coordinate(string name, string fallback, double reference)
        {
            var length = SvgLength.Parse(Attribute(chain, name) ?? fallback) ?? SvgLength.Parse(fallback)!.Value;
            return userSpace ? length.Resolve(SvgStyle.DefaultFontSize, reference) : length.IsPercent ? length.Value / 100 : length.Value;
        }

        var lastColor = stops[^1].Color;
        PdfDictionary shading;
        if (gradient.Name == "linearGradient")
        {
            var x1 = Coordinate("x1", "0%", viewport.Width);
            var y1 = Coordinate("y1", "0%", viewport.Height);
            var x2 = Coordinate("x2", "100%", viewport.Width);
            var y2 = Coordinate("y2", "0%", viewport.Height);
            if (x1 == x2 && y1 == y2)
            {
                return new SvgResolvedPaint(lastColor, null); // A zero-length vector paints the last stop color.
            }

            shading = Shading(2, [x1, y1, x2, y2], stops);
        }
        else
        {
            var diagonal = SvgStyle.Diagonal(viewport);
            var cx = Coordinate("cx", "50%", viewport.Width);
            var cy = Coordinate("cy", "50%", viewport.Height);
            var r = Coordinate("r", "50%", diagonal);
            var fx = Attribute(chain, "fx") is null ? cx : Coordinate("fx", "50%", viewport.Width);
            var fy = Attribute(chain, "fy") is null ? cy : Coordinate("fy", "50%", viewport.Height);
            if (r <= 0)
            {
                return new SvgResolvedPaint(lastColor, null);
            }

            // SVG 1.1 moves a focal point outside the circle onto its edge.
            var distance = Math.Sqrt(((fx - cx) * (fx - cx)) + ((fy - cy) * (fy - cy)));
            if (distance > r * 0.999)
            {
                var scale = r * 0.999 / distance;
                (fx, fy) = (cx + ((fx - cx) * scale), cy + ((fy - cy) * scale));
            }

            shading = Shading(3, [fx, fy, 0, cx, cy, r], stops);
        }

        var transform = SvgMatrix.Parse(Attribute(chain, "gradientTransform")) ?? SvgMatrix.Identity;
        var matrix = transform.Then(units).Then(ctm);
        if (!matrix.IsFinite || matrix.IsSingular)
        {
            return new SvgResolvedPaint(lastColor, null);
        }

        var pattern = new PdfDictionary
        {
            ["Type"] = new PdfName("Pattern"),
            ["PatternType"] = new PdfInteger(2),
            ["Shading"] = shading,
            ["Matrix"] = PdfArray.OfNumbers(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F),
        };
        return new SvgResolvedPaint(null, pattern);
    }

    /// <summary>The gradient and the gradients it inherits from through <c>href</c>, nearest first.</summary>
    private static List<SvgElement> Chain(SvgDocument document, SvgElement gradient)
    {
        var chain = new List<SvgElement> { gradient };
        var current = gradient;
        while (chain.Count < MaxReferenceChain
            && document.GetReferenced(current) is { Name: "linearGradient" or "radialGradient" } next
            && !chain.Contains(next))
        {
            chain.Add(next);
            current = next;
        }

        return chain;
    }

    private static string? Attribute(List<SvgElement> chain, string name) =>
        chain.Select(g => g[name]).FirstOrDefault(v => v is not null)?.Trim();

    /// <summary>The stops of the first gradient in the chain that has any, with offsets clamped and made non-decreasing.</summary>
    private static List<(double Offset, PdfColor Color)> Stops(List<SvgElement> chain, (double Width, double Height) viewport, double opacity)
    {
        var result = new List<(double, PdfColor)>();
        var owner = chain.FirstOrDefault(g => g.Elements.Any(e => e.Name == "stop"));
        if (owner is null)
        {
            return result;
        }

        var ownerStyle = SvgStyle.Compute(SvgStyle.Initial, owner, viewport);
        var previous = 0.0;
        foreach (var stop in owner.Elements.Where(e => e.Name == "stop"))
        {
            var style = SvgStyle.Compute(ownerStyle, stop, viewport);
            var offset = ParseOffset(stop["offset"]);
            offset = Math.Max(previous, Math.Clamp(offset, 0, 1));
            previous = offset;
            var color = style.StopColor.OnWhite(style.StopOpacity * opacity) ?? PdfColor.White;
            result.Add((offset, color));
        }

        return result;
    }

    private static double ParseOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        text = text.Trim();
        var percent = text.EndsWith('%');
        return double.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? percent ? value / 100 : value
            : 0;
    }

    private static PdfDictionary Shading(int type, double[] coords, List<(double Offset, PdfColor Color)> stops) => new()
    {
        ["ShadingType"] = new PdfInteger(type),
        ["ColorSpace"] = new PdfName("DeviceRGB"),
        ["Coords"] = PdfArray.OfNumbers(coords),
        ["Function"] = ColorFunction(stops),
        ["Extend"] = new PdfArray(PdfBoolean.True, PdfBoolean.True),
    };

    /// <summary>
    /// A function from [0, 1] to RGB through the stops: one linear interpolation (type 2) per pair of stops,
    /// stitched (type 3). Stops at equal offsets make a hard edge; colors before the first and after the last stop
    /// are constant.
    /// </summary>
    internal static PdfDictionary ColorFunction(List<(double Offset, PdfColor Color)> stops)
    {
        var points = new List<(double Offset, PdfColor Color)>(stops);
        if (points[0].Offset > 0)
        {
            points.Insert(0, (0, points[0].Color));
        }

        if (points[^1].Offset < 1)
        {
            points.Add((1, points[^1].Color));
        }

        var functions = new List<PdfObject>();
        var bounds = new List<double>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (from, to) = (points[i], points[i + 1]);
            if (to.Offset <= from.Offset)
            {
                continue; // A zero-width interval: the color jumps at this offset.
            }

            if (functions.Count > 0)
            {
                bounds.Add(from.Offset);
            }

            functions.Add(Interpolation(from.Color, to.Color));
        }

        if (functions.Count == 1)
        {
            return (PdfDictionary)functions[0];
        }

        return new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Domain"] = PdfArray.OfNumbers(0, 1),
            ["Functions"] = new PdfArray(functions),
            ["Bounds"] = PdfArray.OfNumbers([.. bounds]),
            ["Encode"] = PdfArray.OfNumbers([.. functions.SelectMany(_ => new double[] { 0, 1 })]),
        };
    }

    private static PdfDictionary Interpolation(PdfColor from, PdfColor to) => new()
    {
        ["FunctionType"] = new PdfInteger(2),
        ["Domain"] = PdfArray.OfNumbers(0, 1),
        ["C0"] = PdfArray.OfNumbers(from.Red, from.Green, from.Blue),
        ["C1"] = PdfArray.OfNumbers(to.Red, to.Green, to.Blue),
        ["N"] = new PdfInteger(1),
    };
}
