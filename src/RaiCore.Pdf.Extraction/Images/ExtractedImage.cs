using RaiCore.Pdf.Extraction.Images;

namespace RaiCore.Pdf.Extraction;

/// <summary>The kind of file <see cref="ExtractedImage.GetData"/> gives.</summary>
public enum ExtractedImageFormat
{
    /// <summary>
    /// A PNG file: images compressed without loss in the PDF (Flate, LZW, run-length or uncompressed), in gray or RGB
    /// (CMYK and indexed colors are converted; ICC profiles are not applied), with the image's soft mask, stencil mask
    /// or color key mask as alpha.
    /// </summary>
    Png,

    /// <summary>A JPEG file: the image's data as it is in the PDF (<c>/DCTDecode</c>), not decoded or re-encoded.</summary>
    Jpeg,

    /// <summary>A JPEG 2000 file or codestream, as it is in the PDF (<c>/JPXDecode</c>).</summary>
    Jpeg2000,

    /// <summary>
    /// JBIG2 data as it is in the PDF (<c>/JBIG2Decode</c>): embedded-stream segments without file header, and
    /// without the shared segments of <c>/JBIG2Globals</c>; a JBIG2 decoder that reads PDF's embedded format is needed.
    /// </summary>
    Jbig2,

    /// <summary>A TIFF file holding the image's CCITT fax data (<c>/CCITTFaxDecode</c>) as it is.</summary>
    Tiff,

    /// <summary>
    /// The image's samples, uncompressed, as the PDF stores them (rows of <see cref="ExtractedImage.PixelWidth"/>
    /// samples of <see cref="ExtractedImage.BitsPerComponent"/> bits per color component, each row starting on a byte):
    /// for color spaces a PNG cannot show (<c>Separation</c>, <c>DeviceN</c>, <c>Lab</c>). When a filter is unknown,
    /// the data as stored, still encoded.
    /// </summary>
    Raw,
}

/// <summary>An image drawn on a page, from <see cref="ImageExtractor"/>: where it is, what it is, and its data.</summary>
/// <remarks>
/// Positions are in PDF points in the page's user space, as <see cref="PdfRectangle"/> always is: from the bottom-left
/// corner of the page before its rotation (<c>/Rotate</c>). An image drawn several times is listed once per placement;
/// <see cref="ObjectNumber"/> tells the copies apart from different images.
/// </remarks>
public sealed class ExtractedImage
{
    private readonly ImageObject _image;

    internal ExtractedImage(int pageIndex, ImageObject image, PdfRectangle bounds, double width, double height, double rotation, string? resourceName, int? objectNumber)
    {
        _image = image;
        PageIndex = pageIndex;
        Bounds = bounds;
        Width = width;
        Height = height;
        Rotation = rotation;
        ResourceName = resourceName;
        ObjectNumber = objectNumber;
    }

    /// <summary>The page's position in the file, from 0.</summary>
    public int PageIndex { get; }

    /// <summary>The rectangle the image covers on the page (around it, when it is turned or skewed).</summary>
    public PdfRectangle Bounds { get; }

    /// <summary>The displayed width in points: the length of the image's top and bottom edges on the page.</summary>
    public double Width { get; }

    /// <summary>The displayed height in points: the length of the image's left and right edges on the page.</summary>
    public double Height { get; }

    /// <summary>
    /// How far the image is turned, in degrees counterclockwise in user space (from -180 to 180): 0 for an upright
    /// image, 90 for one whose top points to the left of the (unrotated) page.
    /// </summary>
    public double Rotation { get; }

    /// <summary>The image's width in pixels (samples per row).</summary>
    public int PixelWidth => _image.Width;

    /// <summary>The image's height in pixels (rows).</summary>
    public int PixelHeight => _image.Height;

    /// <summary>
    /// The family of the image's color space: <c>DeviceGray</c>, <c>DeviceRGB</c>, <c>DeviceCMYK</c>,
    /// <c>ICCBased</c>, <c>Indexed</c>, <c>CalGray</c>, <c>CalRGB</c>, <c>Lab</c>, <c>Separation</c> or
    /// <c>DeviceN</c>; <c>null</c> for a stencil mask (<see cref="IsStencilMask"/>), for JPEG 2000 images that carry
    /// their own, or when it is missing or not understood.
    /// </summary>
    public string? ColorSpace => _image.ColorSpace?.Family;

    /// <summary>The bits per color component: 1, 2, 4, 8 or 16 (0 for JPEG 2000 images that carry their own).</summary>
    public int BitsPerComponent => _image.BitsPerComponent;

    /// <summary>Whether the image is a stencil mask (<c>/ImageMask</c>): one bit per pixel, painted in the current fill color.</summary>
    public bool IsStencilMask => _image.IsMask;

    /// <summary>Whether the image has a mask: a soft mask (<c>/SMask</c>, alpha) or a <c>/Mask</c> (stencil or color key).</summary>
    public bool HasMask => _image.HasMask;

    /// <summary>Whether the image is inline in the content stream (<c>BI</c> ... <c>EI</c>) rather than an image XObject.</summary>
    public bool IsInline => _image.IsInline;

    /// <summary>The image XObject's name in the page's (or form's) resources, without the slash; <c>null</c> for an inline image.</summary>
    public string? ResourceName { get; }

    /// <summary>The image XObject's object number in the file: the same for every placement of one image. <c>null</c> for an inline image.</summary>
    public int? ObjectNumber { get; }

    /// <summary>What <see cref="GetData"/> gives.</summary>
    public ExtractedImageFormat Format => _image.Format;

    /// <summary>The usual file name extension for <see cref="Format"/>: <c>.png</c>, <c>.jpg</c>, <c>.jp2</c>, <c>.jbig2</c>, <c>.tif</c> or <c>.bin</c>.</summary>
    public string FileExtension => Format switch
    {
        ExtractedImageFormat.Png => ".png",
        ExtractedImageFormat.Jpeg => ".jpg",
        ExtractedImageFormat.Jpeg2000 => ".jp2",
        ExtractedImageFormat.Jbig2 => ".jbig2",
        ExtractedImageFormat.Tiff => ".tif",
        _ => ".bin",
    };

    /// <summary>The image as a file of <see cref="Format"/>. The data is read and converted on each call.</summary>
    /// <exception cref="PdfFormatException">The image's data is damaged beyond reading.</exception>
    /// <exception cref="ResourceLimitExceededException">The image decodes to more than <see cref="ResourceLimits.MaxDecompressedBytes"/>.</exception>
    public byte[] GetData()
    {
        switch (Format)
        {
            case ExtractedImageFormat.Png:
                return PixelConverter.ToPng(_image);
            case ExtractedImageFormat.Tiff:
                var parameters = _image.ParametersOf(_image.Filters.Count - 1);
                var k = _image.Resolve(parameters?["K"]) is Objects.PdfInteger n ? (int)Math.Clamp(n.Value, -1, 1) : 0;
                var blackIs1 = _image.Resolve(parameters?["BlackIs1"]) is Objects.PdfBoolean { Value: true };
                var inverted = _image.Decode is [var low, var high, ..] && low > high;
                return TiffWriter.Ccitt(_image.Width, _image.Height, k, whiteIsZero: blackIs1 == inverted, _image.DecodeData());
            case ExtractedImageFormat.Raw when !_image.CanDecode:
                return _image.Data;
            default:
                return _image.DecodeData();
        }
    }

    /// <summary>The image's place, size and format.</summary>
    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{PixelWidth} x {PixelHeight} {Format} image on page {PageIndex + 1} at {Bounds}");
}
