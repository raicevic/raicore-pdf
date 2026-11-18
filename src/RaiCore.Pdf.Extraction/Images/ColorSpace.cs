using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Extraction.Images;

/// <summary>An image's color space (ISO 32000-2, 8.6), as far as exporting its pixels needs it.</summary>
/// <param name="Family">The family name, such as <c>DeviceRGB</c>, <c>ICCBased</c> or <c>Indexed</c>.</param>
/// <param name="Components">The number of color components of a sample (1 for <c>Indexed</c>: the index).</param>
/// <param name="Base">The base space of an <c>Indexed</c> space, or the device space with as many components as an <c>ICCBased</c> one.</param>
/// <param name="HighValue">The highest index of an <c>Indexed</c> space.</param>
/// <param name="Lookup">The color table of an <c>Indexed</c> space: <see cref="HighValue"/> + 1 colors of the base space, a byte per component.</param>
internal sealed record ColorSpace(string Family, int Components, ColorSpace? Base = null, int HighValue = 0, byte[]? Lookup = null)
{
    /// <summary>The most levels of nesting followed (an indexed space over an ICC space, and so on).</summary>
    private const int MaxDepth = 4;

    /// <summary>The most components a space may have (DeviceN allows 32).</summary>
    private const int MaxComponents = 32;

    public static ColorSpace Gray { get; } = new("DeviceGray", 1);

    public static ColorSpace Rgb { get; } = new("DeviceRGB", 3);

    public static ColorSpace Cmyk { get; } = new("DeviceCMYK", 4);

    /// <summary>How the pixels of this space become PNG colors.</summary>
    public PixelModel Model => Family switch
    {
        "DeviceGray" or "CalGray" => PixelModel.Gray,
        "DeviceRGB" or "CalRGB" => PixelModel.Rgb,
        "DeviceCMYK" => PixelModel.Cmyk,
        "ICCBased" => Base?.Model ?? PixelModel.None,
        "Indexed" when Lookup is not null && Base?.Model is PixelModel.Gray or PixelModel.Rgb or PixelModel.Cmyk => PixelModel.Indexed,
        _ => PixelModel.None,
    };

    /// <summary>The color space an object names or describes, or <c>null</c> when it is not one.</summary>
    public static ColorSpace? Parse(PdfObject? value, Func<PdfObject?, PdfObject?> resolve, Func<PdfStreamObject, byte[]> decode, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        switch (resolve(value))
        {
            case PdfName name:
                return OfName(name.Value);
            case PdfArray { Items.Count: > 0 } array when resolve(array.Items[0]) is PdfName family:
                return family.Value switch
                {
                    "CalGray" => new ColorSpace("CalGray", 1),
                    "CalRGB" => new ColorSpace("CalRGB", 3),
                    "Lab" => new ColorSpace("Lab", 3),
                    "ICCBased" => IccBased(array, resolve, decode, depth),
                    "Indexed" or "I" => Indexed(array, resolve, decode, depth),
                    "Separation" => new ColorSpace("Separation", 1),
                    "DeviceN" when array.Items.Count > 1 && resolve(array.Items[1]) is PdfArray { Items.Count: > 0 and <= MaxComponents } names =>
                        new ColorSpace("DeviceN", names.Items.Count),
                    _ when array.Items.Count == 1 => OfName(family.Value),
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static ColorSpace? OfName(string name) => name switch
    {
        "DeviceGray" or "G" => Gray,
        "DeviceRGB" or "RGB" => Rgb,
        "DeviceCMYK" or "CMYK" => Cmyk,
        _ => null,
    };

    private static ColorSpace? IccBased(PdfArray array, Func<PdfObject?, PdfObject?> resolve, Func<PdfStreamObject, byte[]> decode, int depth)
    {
        if (array.Items.Count < 2 || resolve(array.Items[1]) is not PdfStreamObject profile)
        {
            return null;
        }

        var components = resolve(profile.Dictionary["N"]) is PdfInteger { Value: 1 or 3 or 4 } n
            ? (int)n.Value
            : Parse(profile.Dictionary["Alternate"], resolve, decode, depth + 1)?.Components ?? 0;

        // The profile itself is not applied: the device space of as many components is close enough for exported pixels.
        var device = components switch { 1 => Gray, 3 => Rgb, 4 => Cmyk, _ => null };
        return device is null ? null : new ColorSpace("ICCBased", components, device);
    }

    private static ColorSpace? Indexed(PdfArray array, Func<PdfObject?, PdfObject?> resolve, Func<PdfStreamObject, byte[]> decode, int depth)
    {
        if (array.Items.Count < 4
            || Parse(array.Items[1], resolve, decode, depth + 1) is not { } baseSpace
            || resolve(array.Items[2]) is not PdfInteger { Value: >= 0 and <= 255 } high)
        {
            return null;
        }

        byte[]? lookup = resolve(array.Items[3]) switch
        {
            PdfString text => text.Bytes.ToArray(),
            PdfStreamObject stream => decode(stream),
            _ => null,
        };

        // A short table is padded with zeros (black in most spaces), as viewers do.
        var length = ((int)high.Value + 1) * baseSpace.Components;
        if (lookup is not null && lookup.Length < length)
        {
            Array.Resize(ref lookup, length);
        }

        return new ColorSpace("Indexed", 1, baseSpace, (int)high.Value, lookup);
    }
}

/// <summary>How an image's samples become PNG pixels.</summary>
internal enum PixelModel
{
    /// <summary>Not convertible: the samples are exported raw.</summary>
    None,
    Gray,
    Rgb,
    Cmyk,
    Indexed,
}
