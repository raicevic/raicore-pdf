using RaiCore.Pdf.Images;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Svg;

namespace RaiCore.Pdf;

/// <summary>Image file formats <see cref="PdfImage"/> can read.</summary>
public enum ImageFormat
{
    /// <summary>JPEG (baseline or progressive; embedded without re-encoding).</summary>
    Jpeg,

    /// <summary>PNG, including transparency and interlacing.</summary>
    Png,

    /// <summary>GIF (the first frame).</summary>
    Gif,

    /// <summary>SVG, drawn as vector graphics (a static subset of SVG 1.1).</summary>
    Svg,

    /// <summary>BMP (uncompressed, RLE or bit fields, with 32-bit alpha).</summary>
    Bmp,

    /// <summary>WebP (lossy or lossless, with alpha; the first frame of an animation).</summary>
    WebP,
}

/// <summary>
/// An image that can be drawn on pages with <see cref="PdfCanvas.DrawImage"/>. JPEGs are embedded as they are;
/// PNG, GIF, BMP and WebP are decoded and recompressed, with transparency kept. SVG is drawn as vector graphics (a form
/// XObject), not rasterized. An image drawn several times, on any number of pages, is stored in the PDF once.
/// </summary>
/// <remarks>
/// Immutable and thread-safe. Embedded color profiles are ignored; colors are treated as sRGB. For the supported
/// SVG subset, see <see cref="LoadSvg"/>.
/// </remarks>
public sealed class PdfImage
{
    private const double PointsPerPixel = 0.75;

    private readonly DecodedImage? _raster;
    private readonly SvgImage? _svg;

    private PdfImage(DecodedImage image, ImageFormat format)
    {
        _raster = image;
        Format = format;
    }

    private PdfImage(SvgImage svg)
    {
        _svg = svg;
        Format = ImageFormat.Svg;
    }

    /// <summary>The file format.</summary>
    public ImageFormat Format { get; }

    /// <summary>
    /// Width in pixels as displayed (after applying the EXIF orientation of a JPEG). For SVG, the intrinsic width in
    /// CSS pixels, rounded; <see cref="NaturalWidth"/> is exact.
    /// </summary>
    public int PixelWidth => _svg is { } svg ? ToPixels(svg.Width) : SwapsAxes ? Image.Height : Image.Width;

    /// <summary>
    /// Height in pixels as displayed (after applying the EXIF orientation of a JPEG). For SVG, the intrinsic height in
    /// CSS pixels, rounded; <see cref="NaturalHeight"/> is exact.
    /// </summary>
    public int PixelHeight => _svg is { } svg ? ToPixels(svg.Height) : SwapsAxes ? Image.Width : Image.Height;

    /// <summary>
    /// The natural width in points, as CSS sizes an image: 0.75 points (one CSS pixel) per pixel, or an SVG's own
    /// width (from its <c>width</c> attribute or <c>viewBox</c>, else 300 pixels).
    /// </summary>
    public double NaturalWidth => _svg?.Width ?? PixelWidth * PointsPerPixel;

    /// <summary>
    /// The natural height in points: 0.75 points (one CSS pixel) per pixel, or an SVG's own height (from its
    /// <c>height</c> attribute or <c>viewBox</c>, else 150 pixels).
    /// </summary>
    public double NaturalHeight => _svg?.Height ?? PixelHeight * PointsPerPixel;

    /// <summary>Whether the image has transparent or semi-transparent pixels. Always true for SVG, which has no background.</summary>
    public bool HasTransparency => _svg is not null || Image.Alpha is not null || Image.ColorKey is not null;

    /// <summary>Whether drawing the image uses PDF transparency (alpha or groups): only SVG images with opacity do.</summary>
    internal bool UsesTransparency => _svg?.UsesTransparency ?? false;

    /// <summary>The decoded raster image. Not available for SVG.</summary>
    internal DecodedImage Image => _raster ?? throw new InvalidOperationException("An SVG image has no pixels.");

    private bool SwapsAxes => Image.Orientation >= 5;

    /// <summary>Loads an image file (JPEG, PNG, GIF, BMP, WebP or SVG; the format is detected from the content).</summary>
    public static PdfImage Load(string path) => Load(File.ReadAllBytes(path));

    /// <summary>Loads an image from a stream. The stream is read to the end and left open.</summary>
    public static PdfImage Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Load(buffer.ToArray());
    }

    /// <summary>Loads an image from bytes. The array must not be modified afterwards.</summary>
    public static PdfImage Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (SvgDocument.IsSvg(data))
        {
            using var stream = new MemoryStream(data, writable: false);
            return new PdfImage(SvgImage.Load(stream));
        }

        if (PngDecoder.IsPng(data))
        {
            return new PdfImage(PngDecoder.Decode(data), ImageFormat.Png);
        }

        if (GifDecoder.IsGif(data))
        {
            return new PdfImage(GifDecoder.Decode(data), ImageFormat.Gif);
        }

        if (WebPDecoder.IsWebP(data))
        {
            return new PdfImage(WebPDecoder.Decode(data), ImageFormat.WebP);
        }

        if (BmpDecoder.IsBmp(data))
        {
            return new PdfImage(BmpDecoder.Decode(data), ImageFormat.Bmp);
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return new PdfImage(JpegReader.Read(data), ImageFormat.Jpeg);
        }

        throw new ImageFormatException("Unsupported image format; JPEG, PNG, GIF, BMP, WebP and SVG are supported.");
    }

    /// <summary>
    /// Loads an SVG image from its markup. It is drawn as PDF vector graphics, sized by its <c>width</c> and
    /// <c>height</c> or its <c>viewBox</c>.
    /// </summary>
    /// <remarks>
    /// Supported: shapes and paths, groups, transforms, nested <c>svg</c>, <c>use</c> with <c>symbol</c>, fills and
    /// strokes (dashes, caps, joins, fill rules), presentation attributes, the <c>style</c> attribute and
    /// <c>&lt;style&gt;</c> sheets with type, class and id selectors, linear and radial gradients, clip paths, basic
    /// text in the standard PDF fonts and <c>data:</c> images. Opacity (<c>opacity</c>, <c>fill-opacity</c>,
    /// <c>stroke-opacity</c>, translucent colors) uses PDF transparency, with group opacity as transparency groups.
    /// Filters, masks, markers, patterns, animation, scripts and <c>foreignObject</c> are skipped, and
    /// nothing external is loaded.
    /// </remarks>
    /// <exception cref="ImageFormatException">The markup is not well-formed XML or its root is not <c>svg</c>.</exception>
    public static PdfImage LoadSvg(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markup), writable: false);
        return new PdfImage(SvgImage.Load(stream));
    }

    /// <summary>
    /// The matrix that draws the stored image into the rectangle, applying the EXIF orientation. PDF draws an image
    /// into the unit square; (u, v) is a position in the stored image with v = 1 at its first row.
    /// </summary>
    internal (double A, double B, double C, double D, double E, double F) GetMatrix(double x, double y, double width, double height)
    {
        if (_svg is { } svg)
        {
            return svg.GetMatrix(x, y, width, height);
        }

        // Displayed position (X', Y') in [0, 1] for stored (u, v), per EXIF orientation 1-8.
        var (xu, xv, x0, yu, yv, y0) = Image.Orientation switch
        {
            2 => (-1, 0, 1, 0, 1, 0),   // X' = 1 - u, Y' = v
            3 => (-1, 0, 1, 0, -1, 1),  // X' = 1 - u, Y' = 1 - v
            4 => (1, 0, 0, 0, -1, 1),   // X' = u,     Y' = 1 - v
            5 => (0, -1, 1, -1, 0, 1),  // X' = 1 - v, Y' = 1 - u
            6 => (0, 1, 0, -1, 0, 1),   // X' = v,     Y' = 1 - u
            7 => (0, 1, 0, 1, 0, 0),    // X' = v,     Y' = u
            8 => (0, -1, 1, 1, 0, 0),   // X' = 1 - v, Y' = u
            _ => (1, 0, 0, 0, 1, 0),    // X' = u,     Y' = v
        };

        // X = x + width * X', Y = y + height * Y'; the matrix maps (u, v) to (X, Y) = (a u + c v + e, b u + d v + f).
        return (width * xu, height * yu, width * xv, height * yv, x + (width * x0), y + (height * y0));
    }

    /// <summary>Writes the image (and its soft mask) as XObjects; an SVG becomes a form XObject.</summary>
    internal PdfReference WriteTo(PdfFileWriter writer)
    {
        if (_svg is { } svg)
        {
            return svg.WriteTo(writer);
        }

        var image = Image;
        PdfStreamObject stream;
        if (image.Encoding == ImageEncoding.Jpeg)
        {
            stream = new PdfStreamObject(image.Data);
            stream.Dictionary["Filter"] = new PdfName("DCTDecode");
        }
        else
        {
            stream = PdfStreamObject.CreateCompressed(image.Data);
        }

        var dict = stream.Dictionary;
        dict["Type"] = new PdfName("XObject");
        dict["Subtype"] = new PdfName("Image");
        dict["Width"] = new PdfInteger(image.Width);
        dict["Height"] = new PdfInteger(image.Height);
        dict["BitsPerComponent"] = new PdfInteger(image.BitsPerComponent);
        dict["ColorSpace"] = image.ColorSpace switch
        {
            ImageColorSpace.Gray => new PdfName("DeviceGray"),
            ImageColorSpace.Rgb => new PdfName("DeviceRGB"),
            ImageColorSpace.Cmyk => new PdfName("DeviceCMYK"),
            _ => new PdfArray(
                new PdfName("Indexed"), new PdfName("DeviceRGB"),
                new PdfInteger((image.Palette!.Length / 3) - 1), new PdfString(image.Palette, hex: true)),
        };

        if (image.InvertCmyk)
        {
            dict["Decode"] = PdfArray.OfNumbers(1, 0, 1, 0, 1, 0, 1, 0);
        }

        if (image.ColorKey is { } key)
        {
            dict["Mask"] = PdfArray.OfNumbers([.. key.Select(k => (double)k)]);
        }

        if (image.Alpha is { } alpha)
        {
            var mask = PdfStreamObject.CreateCompressed(alpha);
            mask.Dictionary["Type"] = new PdfName("XObject");
            mask.Dictionary["Subtype"] = new PdfName("Image");
            mask.Dictionary["Width"] = new PdfInteger(image.Width);
            mask.Dictionary["Height"] = new PdfInteger(image.Height);
            mask.Dictionary["ColorSpace"] = new PdfName("DeviceGray");
            mask.Dictionary["BitsPerComponent"] = new PdfInteger(8);
            dict["SMask"] = writer.Add(mask);
        }

        return writer.Add(stream);
    }

    private static int ToPixels(double points) => (int)Math.Max(1, Math.Round(points / PointsPerPixel));
}
