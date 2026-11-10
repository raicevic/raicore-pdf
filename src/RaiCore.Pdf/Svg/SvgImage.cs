using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Svg;

/// <summary>
/// An SVG document drawn as PDF vector graphics in a form XObject. The drawing is made once, when the SVG is loaded;
/// the form's space is the image's natural size in points with the origin at the bottom left.
/// </summary>
internal sealed class SvgImage
{
    /// <summary>The size browsers give an image without a width, height or view box (CSS pixels).</summary>
    private const double DefaultWidth = 300;

    private const double DefaultHeight = 150;

    private const double PointsPerPixel = 0.75;

    /// <summary>Holds the drawing's content stream and the resources it uses (fonts, images, patterns).</summary>
    private readonly PdfPage _sheet;

    private SvgImage(PdfPage sheet, double width, double height, SvgPreserveAspectRatio? aspect)
    {
        _sheet = sheet;
        Width = width;
        Height = height;
        Aspect = aspect;
    }

    /// <summary>The natural width in points.</summary>
    public double Width { get; }

    /// <summary>The natural height in points.</summary>
    public double Height { get; }

    /// <summary>How the drawing fits a rectangle of another aspect ratio, or null to stretch it (no view box, or <c>none</c>).</summary>
    public SvgPreserveAspectRatio? Aspect { get; }

    public static SvgImage Load(Stream stream) => Draw(SvgDocument.Parse(stream));

    /// <summary>Draws the document: the root's view box is mapped into its viewport, which is flipped to PDF's y-up space.</summary>
    private static SvgImage Draw(SvgDocument document)
    {
        var root = document.Root;
        var viewBox = SvgViewBox.Parse(root["viewBox"]);
        var aspect = SvgPreserveAspectRatio.Parse(root["preserveAspectRatio"]);
        var (width, height) = NaturalSize(root, viewBox);

        var sheet = new PdfPage(new PdfPageSize(width * PointsPerPixel, height * PointsPerPixel));
        var ctm = SvgMatrix.Scale(PointsPerPixel, -PointsPerPixel).Then(SvgMatrix.Translate(0, height * PointsPerPixel));
        var viewport = (width, height);
        if (viewBox is { } box)
        {
            ctm = box.Transform(aspect, width, height).Then(ctm);
            viewport = (box.Width, box.Height);
        }

        if (ctm.IsFinite)
        {
            sheet.Canvas.Transform(ctm.A, ctm.B, ctm.C, ctm.D, ctm.E, ctm.F);
            new SvgRenderer(document, sheet.Canvas).Render(ctm, viewport);
        }

        return new SvgImage(sheet, sheet.Width, sheet.Height, viewBox is null || aspect.None ? null : aspect);
    }

    /// <summary>
    /// The intrinsic size in CSS pixels: <c>width</c> and <c>height</c> (absolute lengths; percentages count as
    /// missing), a missing one following the view box's aspect ratio, the view box's size without either, else 300 x 150.
    /// </summary>
    internal static (double Width, double Height) NaturalSize(SvgElement root, SvgViewBox? viewBox)
    {
        static double? Absolute(string? text) =>
            SvgLength.Parse(text) is { IsPercent: false } length && length.Resolve(SvgStyle.DefaultFontSize, 0) is var value && value > 0 && value < 1e6 ? value : null;

        var width = Absolute(root["width"]);
        var height = Absolute(root["height"]);
        return (width, height, viewBox) switch
        {
            ({ } w, { } h, _) => (w, h),
            ({ } w, null, { } box) => (w, w * box.Height / box.Width),
            (null, { } h, { } box) => (h * box.Width / box.Height, h),
            ({ } w, null, null) => (w, DefaultHeight),
            (null, { } h, null) => (DefaultWidth, h),
            (null, null, { } box) when box.Width < 1e6 && box.Height < 1e6 => (box.Width, box.Height),
            _ => (DefaultWidth, DefaultHeight),
        };
    }

    /// <summary>
    /// The matrix that draws the form into the rectangle. With a view box, the drawing keeps its aspect ratio and is
    /// aligned per <c>preserveAspectRatio</c> (as browsers lay out an SVG image again at the new size); <c>slice</c>
    /// is drawn like <c>meet</c>, as nothing outside the rectangle may be painted.
    /// </summary>
    public (double A, double B, double C, double D, double E, double F) GetMatrix(double x, double y, double width, double height)
    {
        var (sx, sy) = (width / Width, height / Height);
        if (Aspect is not { } aspect)
        {
            return (sx, 0, 0, sy, x, y);
        }

        var scale = Math.Min(sx, sy);
        var dx = (width - (Width * scale)) * aspect.AlignX;
        var dy = (height - (Height * scale)) * (1 - aspect.AlignY); // yMin is the top, which is up in PDF.
        return (scale, 0, 0, scale, x + dx, y + dy);
    }

    /// <summary>Whether the drawing uses transparency (alpha or groups).</summary>
    public bool UsesTransparency => _sheet.UsesTransparency;

    /// <summary>
    /// Writes the drawing as a form XObject with its own resources. It is not a transparency group itself: its
    /// translucent shapes blend with whatever it is drawn over.
    /// </summary>
    public PdfReference WriteTo(PdfFileWriter writer)
    {
        var content = _sheet.Canvas.ToBytes();
        var resources = _sheet.WriteResources(
            writer, compress: true, (font, usage) => font.WriteTo(writer, usage), image => image.WriteTo(writer), alpha => writer.Add(PdfPage.ExtGState(alpha)));
        var form = PdfStreamObject.CreateCompressed(content);
        form.Dictionary["Type"] = new PdfName("XObject");
        form.Dictionary["Subtype"] = new PdfName("Form");
        form.Dictionary["BBox"] = PdfArray.OfNumbers(0, 0, Width, Height);
        form.Dictionary["Resources"] = resources;
        return writer.Add(form);
    }
}
