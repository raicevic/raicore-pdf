using System.Globalization;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>The geometry of a gradient: an axis (PDF axial shading) or two circles (radial shading), in user space.</summary>
internal abstract record ShadingGeometry;

/// <summary>A gradient along the line from (<paramref name="X0"/>, <paramref name="Y0"/>) to (<paramref name="X1"/>, <paramref name="Y1"/>).</summary>
internal sealed record AxialGeometry(double X0, double Y0, double X1, double Y1) : ShadingGeometry;

/// <summary>A gradient between two circles (offset 0 on the first, 1 on the second).</summary>
internal sealed record RadialGeometry(double X0, double Y0, double R0, double X1, double Y1, double R1) : ShadingGeometry;

/// <summary>
/// Gradients painted with the <c>sh</c> operator: the shading fills the current clip in the current user space, so it
/// follows every transformation (unlike a pattern, whose matrix is fixed to the page). Colors with alpha get a soft mask
/// made of a second shading with the same geometry that carries the alpha as gray.
/// </summary>
public sealed partial class PdfCanvas
{
    private readonly List<(PdfDictionary Shading, string Name)> _shadings = [];
    private readonly List<(PdfDictionary AlphaShading, string Name)> _softMasks = [];

    /// <summary>The shading resources used by this canvas.</summary>
    internal IReadOnlyList<(PdfDictionary Shading, string Name)> Shadings => _shadings;

    /// <summary>The soft-mask graphics states used by this canvas: each masks with the luminosity of its alpha shading.</summary>
    internal IReadOnlyList<(PdfDictionary AlphaShading, string Name)> SoftMasks => _softMasks;

    /// <summary>
    /// Fills the current clip with a gradient through <paramref name="stops"/> (offsets from 0 to 1, in order; the first and
    /// last colors continue beyond the ends). Translucent stops are painted through a soft mask.
    /// </summary>
    internal PdfCanvas PaintShading(ShadingGeometry geometry, IReadOnlyList<(double Offset, PdfColor Color)> stops)
    {
        if (stops.Count == 0)
        {
            return this;
        }

        var colors = Shading(geometry, Function(stops, color => [color.Red, color.Green, color.Blue]), "DeviceRGB");
        var name = string.Create(CultureInfo.InvariantCulture, $"Sh{_shadings.Count + 1}");
        _shadings.Add((colors, name));
        UseResource(name);

        if (stops.All(stop => stop.Color.Alpha >= 1))
        {
            return Op($"/{name} sh");
        }

        var alpha = Shading(geometry, Function(stops, color => [color.Alpha]), "DeviceGray");
        var mask = string.Create(CultureInfo.InvariantCulture, $"SM{_softMasks.Count + 1}");
        _softMasks.Add((alpha, mask));
        UseResource(mask);
        return Op($"q /{mask} gs /{name} sh Q");
    }

    private static PdfDictionary Shading(ShadingGeometry geometry, PdfDictionary function, string colorSpace) => new()
    {
        ["ShadingType"] = new PdfInteger(geometry is AxialGeometry ? 2 : 3),
        ["ColorSpace"] = new PdfName(colorSpace),
        ["Coords"] = geometry switch
        {
            AxialGeometry a => PdfArray.OfNumbers(a.X0, a.Y0, a.X1, a.Y1),
            RadialGeometry r => PdfArray.OfNumbers(r.X0, r.Y0, r.R0, r.X1, r.Y1, r.R1),
            _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
        },
        ["Function"] = function,
        ["Extend"] = new PdfArray([PdfBoolean.True, PdfBoolean.True]),
    };

    /// <summary>
    /// A function from [0, 1] through the stops' components: a linear interpolation (type 2) per pair of stops, stitched
    /// (type 3). Stops at equal offsets make a hard edge.
    /// </summary>
    private static PdfDictionary Function(IReadOnlyList<(double Offset, PdfColor Color)> stops, Func<PdfColor, double[]> components)
    {
        var points = stops.Select(stop => (Offset: Math.Clamp(stop.Offset, 0, 1), stop.Color)).ToList();
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

            functions.Add(new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(2),
                ["Domain"] = PdfArray.OfNumbers(0, 1),
                ["C0"] = PdfArray.OfNumbers(components(from.Color)),
                ["C1"] = PdfArray.OfNumbers(components(to.Color)),
                ["N"] = new PdfInteger(1),
            });
        }

        if (functions.Count == 0)
        {
            // All stops at one offset: the last color everywhere.
            var last = components(points[^1].Color);
            return new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(2),
                ["Domain"] = PdfArray.OfNumbers(0, 1),
                ["C0"] = PdfArray.OfNumbers(last),
                ["C1"] = PdfArray.OfNumbers(last),
                ["N"] = new PdfInteger(1),
            };
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
}
