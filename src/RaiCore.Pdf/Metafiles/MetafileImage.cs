using RaiCore.Pdf.Images;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// A WMF or EMF picture drawn as PDF vector graphics in a form XObject, like <see cref="Svg.SvgImage"/>: the drawing
/// is made once, when the picture is loaded; the form's space is the picture's natural size in points with the
/// origin at the bottom left. A damaged picture is drawn up to the damage (reported through the options).
/// </summary>
internal sealed class MetafileImage
{
    /// <summary>Holds the drawing's content stream and the resources it uses (fonts, images, patterns).</summary>
    private readonly PdfPage _sheet;

    private MetafileImage(PdfPage sheet, PdfImageFormat format)
    {
        _sheet = sheet;
        Format = format;
    }

    /// <summary><see cref="PdfImageFormat.Wmf"/> or <see cref="PdfImageFormat.Emf"/>.</summary>
    public PdfImageFormat Format { get; }

    /// <summary>The natural width in points.</summary>
    public double Width => _sheet.Width;

    /// <summary>The natural height in points.</summary>
    public double Height => _sheet.Height;

    /// <summary>Whether the data starts with a WMF (placeable or standard) or EMF header.</summary>
    public static bool IsMetafile(ReadOnlySpan<byte> data) => EmfHeader.IsEmf(data) || WmfHeader.IsWmf(data);

    /// <summary>Loads and draws a picture.</summary>
    /// <exception cref="ImageFormatException">The data is not a WMF or EMF picture, or its header is damaged.</exception>
    public static MetafileImage Load(byte[] data, MetafileOptions options) => Load(data, new MetafileContext(options, null));

    /// <summary>Loads and draws a picture (nested in another one when the context has a parent).</summary>
    /// <exception cref="ImageFormatException">The data is not a WMF or EMF picture, or its header is damaged.</exception>
    internal static MetafileImage Load(byte[] data, MetafileContext context)
    {
        if (EmfHeader.IsEmf(data))
        {
            var header = EmfHeader.Read(data);
            var frame = header.FrameMillimeters;
            var sheet = Sheet(frame.Width * 72 / 25.4, frame.Height * 72 / 25.4);
            new EmfPlayer(context, new MetaPainter(sheet.Canvas, sheet.Width, sheet.Height), data, header).Play();
            return new MetafileImage(sheet, PdfImageFormat.Emf);
        }

        if (WmfHeader.IsWmf(data))
        {
            var header = WmfHeader.Read(data);
            if (WmfHeader.EmbeddedEmf(data, header.RecordsStart) is { } emf && EmfHeader.IsEmf(emf) && !EmfHeader.HasGlyphIndexText(emf))
            {
                try
                {
                    var embedded = Load(emf, context);
                    return new MetafileImage(embedded._sheet, PdfImageFormat.Wmf);
                }
                catch (ImageFormatException)
                {
                    // The WMF records draw it.
                }
            }

            var sheet = Sheet(header.Size.Width, header.Size.Height);
            new WmfPlayer(context, new MetaPainter(sheet.Canvas, sheet.Width, sheet.Height), data, header).Play();
            return new MetafileImage(sheet, PdfImageFormat.Wmf);
        }

        throw new ImageFormatException("Not a WMF or EMF picture.");
    }

    private static PdfPage Sheet(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            throw new ImageFormatException("The picture has no size.");
        }

        // A sane range: a hundredth of a point to about 14 meters.
        return new PdfPage(new PdfPageSize(Math.Clamp(width, 0.01, 40000), Math.Clamp(height, 0.01, 40000)));
    }

    /// <summary>The matrix that draws the form into the rectangle (stretched to fill it, as Office does).</summary>
    public (double A, double B, double C, double D, double E, double F) GetMatrix(double x, double y, double width, double height) =>
        (width / Width, 0, 0, height / Height, x, y);

    /// <summary>Whether the drawing uses transparency (translucent EMF+ colors).</summary>
    public bool UsesTransparency => _sheet.UsesTransparency;

    /// <summary>What the drawing uses that needs transparency, which PDF/A-1 forbids, or <c>null</c>.</summary>
    public string? Transparency() => _sheet.Transparency();

    /// <summary>Writes the drawing as a form XObject with its own resources.</summary>
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
