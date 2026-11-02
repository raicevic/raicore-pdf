using RaiCore.Pdf.Images;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;

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
}

/// <summary>
/// A raster image that can be drawn on pages with <see cref="PdfCanvas.DrawImage"/>. JPEGs are embedded as they are;
/// PNG and GIF are decoded and recompressed, with transparency kept. An image drawn several times, on any number of
/// pages, is stored in the PDF once.
/// </summary>
/// <remarks>Immutable and thread-safe. Embedded color profiles are ignored; colors are treated as sRGB.</remarks>
public sealed class PdfImage
{
    private PdfImage(DecodedImage image, ImageFormat format)
    {
        Image = image;
        Format = format;
    }

    /// <summary>The file format.</summary>
    public ImageFormat Format { get; }

    /// <summary>Width in pixels as displayed (after applying the EXIF orientation of a JPEG).</summary>
    public int PixelWidth => SwapsAxes ? Image.Height : Image.Width;

    /// <summary>Height in pixels as displayed (after applying the EXIF orientation of a JPEG).</summary>
    public int PixelHeight => SwapsAxes ? Image.Width : Image.Height;

    /// <summary>Whether the image has transparent or semi-transparent pixels.</summary>
    public bool HasTransparency => Image.Alpha is not null || Image.ColorKey is not null;

    internal DecodedImage Image { get; }

    private bool SwapsAxes => Image.Orientation >= 5;

    /// <summary>Loads an image file (JPEG, PNG or GIF; the format is detected from the content).</summary>
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
        if (PngDecoder.IsPng(data))
        {
            return new PdfImage(PngDecoder.Decode(data), ImageFormat.Png);
        }

        if (GifDecoder.IsGif(data))
        {
            return new PdfImage(GifDecoder.Decode(data), ImageFormat.Gif);
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return new PdfImage(JpegReader.Read(data), ImageFormat.Jpeg);
        }

        throw new ImageFormatException("Unsupported image format; JPEG, PNG and GIF are supported.");
    }

    /// <summary>
    /// The matrix that draws the stored image into the rectangle, applying the EXIF orientation. PDF draws an image
    /// into the unit square; (u, v) is a position in the stored image with v = 1 at its first row.
    /// </summary>
    internal (double A, double B, double C, double D, double E, double F) GetMatrix(double x, double y, double width, double height)
    {
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

    /// <summary>Writes the image (and its soft mask) as XObjects.</summary>
    internal PdfReference WriteTo(PdfFileWriter writer)
    {
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
}
