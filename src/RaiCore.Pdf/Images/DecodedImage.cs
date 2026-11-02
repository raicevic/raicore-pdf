namespace RaiCore.Pdf.Images;

/// <summary>Color space of image samples.</summary>
internal enum ImageColorSpace
{
    Gray,
    Rgb,
    Cmyk,

    /// <summary>Samples are indices into <see cref="DecodedImage.Palette"/> (RGB triples).</summary>
    Indexed,
}

/// <summary>How <see cref="DecodedImage.Data"/> is encoded.</summary>
internal enum ImageEncoding
{
    /// <summary>Uncompressed samples, rows padded to whole bytes (PDF and PNG use the same packing).</summary>
    Raw,

    /// <summary>A complete JPEG file, embedded as-is with /DCTDecode.</summary>
    Jpeg,
}

/// <summary>An image in the form PDF needs, independent of the file format it came from.</summary>
internal sealed record DecodedImage(int Width, int Height, ImageColorSpace ColorSpace, int BitsPerComponent, byte[] Data, ImageEncoding Encoding)
{
    /// <summary>RGB palette for <see cref="ImageColorSpace.Indexed"/>.</summary>
    public byte[]? Palette { get; init; }

    /// <summary>8-bit alpha per pixel (row-major, Width x Height), written as a soft mask.</summary>
    public byte[]? Alpha { get; init; }

    /// <summary>Color-key transparency: min/max sample values per component (PDF /Mask array).</summary>
    public int[]? ColorKey { get; init; }

    /// <summary>Adobe CMYK JPEGs store inverted values.</summary>
    public bool InvertCmyk { get; init; }

    /// <summary>EXIF orientation (1-8); 1 is upright.</summary>
    public int Orientation { get; init; } = 1;

    public int Components => ColorSpace switch
    {
        ImageColorSpace.Rgb => 3,
        ImageColorSpace.Cmyk => 4,
        _ => 1,
    };
}

/// <summary>Thrown when image data is malformed or uses an unsupported format.</summary>
public sealed class ImageFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ImageFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
