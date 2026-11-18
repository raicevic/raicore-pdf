using RaiCore.Pdf.Filters;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Extraction.Images;

/// <summary>
/// An image (an image XObject, or an inline image with its abbreviations expanded) and what exporting it needs:
/// its size, color space, filters and masks (ISO 32000-2, 8.9).
/// </summary>
internal sealed class ImageObject
{
    /// <summary>The image codecs, whose data is exported as it is (after any filters before them).</summary>
    private static readonly Dictionary<string, ExtractedImageFormat> Codecs = new(StringComparer.Ordinal)
    {
        ["DCTDecode"] = ExtractedImageFormat.Jpeg,
        ["JPXDecode"] = ExtractedImageFormat.Jpeg2000,
        ["JBIG2Decode"] = ExtractedImageFormat.Jbig2,
        ["CCITTFaxDecode"] = ExtractedImageFormat.Tiff,
    };

    /// <summary>The filters <see cref="StreamDecoder"/> decodes.</summary>
    private static readonly HashSet<string> Decodable = new(StringComparer.Ordinal)
    {
        "FlateDecode", "LZWDecode", "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode", "Crypt",
    };

    /// <summary>The keys of inline image dictionaries, abbreviated (ISO 32000-2, table 91).</summary>
    private static readonly Dictionary<string, string> InlineKeys = new(StringComparer.Ordinal)
    {
        ["BPC"] = "BitsPerComponent", ["CS"] = "ColorSpace", ["D"] = "Decode", ["DP"] = "DecodeParms", ["F"] = "Filter",
        ["H"] = "Height", ["IM"] = "ImageMask", ["I"] = "Interpolate", ["W"] = "Width", ["L"] = "Length",
    };

    /// <summary>The filter names of inline images, abbreviated (ISO 32000-2, table 92).</summary>
    private static readonly Dictionary<string, string> InlineFilters = new(StringComparer.Ordinal)
    {
        ["AHx"] = "ASCIIHexDecode", ["A85"] = "ASCII85Decode", ["LZW"] = "LZWDecode", ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode", ["CCF"] = "CCITTFaxDecode", ["DCT"] = "DCTDecode",
    };

    private readonly PdfReader _reader;

    private ImageObject(PdfReader reader, PdfDictionary dictionary, byte[] data, bool isInline)
    {
        _reader = reader;
        Dictionary = dictionary;
        Data = data;
        IsInline = isInline;
        Width = Integer("Width");
        Height = Integer("Height");
        IsMask = Resolve(dictionary["ImageMask"]) is PdfBoolean { Value: true };
        BitsPerComponent = IsMask ? 1 : Integer("BitsPerComponent");
        ColorSpace = IsMask ? null : ColorSpace.Parse(dictionary["ColorSpace"], Resolve, DecodeStream);
        Decode = Resolve(dictionary["Decode"]) is PdfArray decode
            ? [.. decode.Items.Select(i => Resolve(i) switch { PdfInteger n => n.Value, PdfReal r => r.Value, _ => double.NaN })]
            : null;

        var filters = Resolve(dictionary["Filter"]) switch
        {
            PdfName name => [name],
            PdfArray array => array.Items.Select(Resolve).ToList(),
            _ => [],
        };
        Filters = [.. filters.Select(f => (f as PdfName)?.Value ?? "?")];
        Format = FormatOf();
    }

    public PdfDictionary Dictionary { get; }

    /// <summary>The image's data as stored (before its filters).</summary>
    public byte[] Data { get; }

    public bool IsInline { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Whether the image is a stencil mask (<c>/ImageMask true</c>): one bit per pixel, painted in the fill color.</summary>
    public bool IsMask { get; }

    public int BitsPerComponent { get; }

    /// <summary>The color space; <c>null</c> for a stencil mask, or when there is none (JPEG 2000 images carry their own) or it is not understood.</summary>
    public ColorSpace? ColorSpace { get; }

    /// <summary>The <c>/Decode</c> array, or <c>null</c> for the default.</summary>
    public double[]? Decode { get; }

    /// <summary>The filter names, in order.</summary>
    public IReadOnlyList<string> Filters { get; }

    public ExtractedImageFormat Format { get; }

    /// <summary>Whether every filter can be decoded (none is an image codec or unknown).</summary>
    public bool CanDecode => Filters.All(Decodable.Contains);

    /// <summary>The soft mask (<c>/SMask</c>): a grayscale image whose samples are the alpha.</summary>
    public PdfStreamObject? SoftMask => Resolve(Dictionary["SMask"]) as PdfStreamObject;

    /// <summary>The mask (<c>/Mask</c>): a stencil mask image, or an array of color key ranges.</summary>
    public PdfObject? Mask => Resolve(Dictionary["Mask"]) switch
    {
        PdfStreamObject stream => stream,
        PdfArray ranges => ranges,
        _ => null,
    };

    public bool HasMask => SoftMask is not null || Mask is not null;

    /// <summary>The <c>/DecodeParms</c> of the filter at <paramref name="index"/>.</summary>
    public PdfDictionary? ParametersOf(int index) => Resolve(Dictionary["DecodeParms"]) switch
    {
        PdfDictionary parameters when index == 0 || Filters.Count == 1 => parameters,
        PdfArray array when index < array.Items.Count => Resolve(array.Items[index]) as PdfDictionary,
        _ => null,
    };

    public static ImageObject FromXObject(PdfReader reader, PdfStreamObject stream) => new(reader, stream.Dictionary, stream.Data, false);

    /// <summary>An inline image, its abbreviated keys and names expanded and a named color space looked up in <paramref name="resources"/>.</summary>
    public static ImageObject FromInline(PdfReader reader, PdfDictionary inline, byte[] data, PdfDictionary resources)
    {
        var dictionary = new PdfDictionary();
        foreach (var (key, value) in inline.Entries)
        {
            var name = InlineKeys.GetValueOrDefault(key.Value, key.Value);
            dictionary[name] = name switch
            {
                "Filter" => ExpandFilters(value),
                "ColorSpace" when value is PdfName named && named.Value is not ("G" or "RGB" or "CMYK" or "DeviceGray" or "DeviceRGB" or "DeviceCMYK")
                    => (reader.Resolve(resources["ColorSpace"]) as PdfDictionary)?[named] ?? value,
                _ => value,
            };
        }

        return new ImageObject(reader, dictionary, data, true);

        static PdfObject ExpandFilters(PdfObject value) => value switch
        {
            PdfName name => new PdfName(InlineFilters.GetValueOrDefault(name.Value, name.Value)),
            PdfArray array => new PdfArray(array.Items.Select(ExpandFilters)),
            _ => value,
        };
    }

    /// <summary>
    /// The data after the filters before an image codec (the codec's own data, for <see cref="ExtractedImageFormat.Jpeg"/>
    /// and the other codecs), or after all filters (the samples).
    /// </summary>
    /// <exception cref="PdfFormatException">A filter's data or parameters are damaged beyond decoding.</exception>
    public byte[] DecodeData()
    {
        var count = Codecs.ContainsKey(Filters.Count > 0 ? Filters[^1] : string.Empty) ? Filters.Count - 1 : Filters.Count;
        if (count == 0)
        {
            return Data;
        }

        var dictionary = new PdfDictionary
        {
            ["Filter"] = new PdfArray(Filters.Take(count).Select(f => (PdfObject)new PdfName(f))),
            ["DecodeParms"] = new PdfArray(Enumerable.Range(0, count).Select(i => (PdfObject?)ParametersOf(i) ?? PdfNull.Instance)),
        };
        return StreamDecoder.Decode(new PdfStreamObject(Data, dictionary), Resolve);
    }

    /// <summary>Another image of the same file, such as this one's mask.</summary>
    public ImageObject Related(PdfStreamObject stream) => FromXObject(_reader, stream);

    public PdfObject? Resolve(PdfObject? value) => _reader.Resolve(value);

    private byte[] DecodeStream(PdfStreamObject stream) => _reader.Store.Decode(stream);

    private int Integer(string key) => Resolve(Dictionary[key]) switch
    {
        PdfInteger n when n.Value is > 0 and <= int.MaxValue => (int)n.Value,
        PdfReal r when r.Value is >= 1 and <= int.MaxValue => (int)r.Value,
        _ => 0,
    };

    private ExtractedImageFormat FormatOf()
    {
        if (Filters.Count > 0 && Codecs.TryGetValue(Filters[^1], out var codec) && Filters.Take(Filters.Count - 1).All(Decodable.Contains))
        {
            return codec;
        }

        var pixels = Width > 0 && Height > 0 && BitsPerComponent is 1 or 2 or 4 or 8 or 16
            && (IsMask || (ColorSpace?.Model ?? PixelModel.None) != PixelModel.None);
        return Filters.All(Decodable.Contains) && pixels ? ExtractedImageFormat.Png : ExtractedImageFormat.Raw;
    }
}
