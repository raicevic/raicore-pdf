namespace RaiCore.Pdf.Extraction.Images;

/// <summary>
/// Turns an image's samples into PNG pixels: gray or RGB (CMYK and indexed colors converted, <c>/Decode</c>
/// applied), with an alpha channel from its soft mask, stencil mask or color key mask. Data that ends early is padded
/// with zeros, as viewers draw it.
/// </summary>
internal static class PixelConverter
{
    /// <summary>The PNG file of an image whose <see cref="ImageObject.Format"/> is <see cref="ExtractedImageFormat.Png"/>.</summary>
    public static byte[] ToPng(ImageObject image)
    {
        var (width, height) = (image.Width, image.Height);
        ResourceLimits.CheckDecompressedBytes((long)width * height * 4);
        if (image.IsMask)
        {
            // A stencil mask: black where it paints, transparent elsewhere.
            var paints = Stencil(image, width, height);
            var pixels = new byte[width * height * 2];
            for (var i = 0; i < paints.Length; i++)
            {
                pixels[(2 * i) + 1] = paints[i] ? (byte)255 : (byte)0;
            }

            return PngWriter.Write(width, height, 2, pixels);
        }

        var (channels, colors) = Colors(image);
        var alpha = Alpha(image);
        if (alpha is null)
        {
            return PngWriter.Write(width, height, channels, colors);
        }

        var withAlpha = new byte[width * height * (channels + 1)];
        for (int i = 0, j = 0; i < alpha.Length; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                withAlpha[j++] = colors[(i * channels) + c];
            }

            withAlpha[j++] = alpha[i];
        }

        return PngWriter.Write(width, height, channels + 1, withAlpha);
    }

    /// <summary>An image's colors: 1 (gray) or 3 (RGB) bytes per pixel.</summary>
    private static (int Channels, byte[] Pixels) Colors(ImageObject image)
    {
        var space = image.ColorSpace!;
        var model = space.Model;
        var device = model == PixelModel.Indexed ? space.Base!.Model : model;
        var channels = device == PixelModel.Gray ? 1 : 3;
        var (width, height, components) = (image.Width, image.Height, space.Components);
        var samples = new Samples(image.DecodeData(), width, components, image.BitsPerComponent);
        var max = (1 << image.BitsPerComponent) - 1;
        var decode = DecodeRanges(image.Decode, components, model == PixelModel.Indexed ? max : 1);
        var pixels = new byte[width * height * channels];
        var color = new double[4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (model == PixelModel.Indexed)
                {
                    var index = (int)Math.Round(Map(samples.Get(y, x), max, decode[0], decode[1]));
                    index = Math.Clamp(index, 0, space.HighValue);
                    var baseComponents = space.Base!.Components;
                    for (var c = 0; c < baseComponents; c++)
                    {
                        color[c] = space.Lookup![(index * baseComponents) + c] / 255.0;
                    }
                }
                else
                {
                    for (var c = 0; c < components; c++)
                    {
                        color[c] = Math.Clamp(Map(samples.Get(y, (x * components) + c), max, decode[2 * c], decode[(2 * c) + 1]), 0, 1);
                    }
                }

                var at = ((y * width) + x) * channels;
                switch (device)
                {
                    case PixelModel.Gray:
                        pixels[at] = Byte(color[0]);
                        break;
                    case PixelModel.Rgb:
                        pixels[at] = Byte(color[0]);
                        pixels[at + 1] = Byte(color[1]);
                        pixels[at + 2] = Byte(color[2]);
                        break;
                    default: // CMYK, without a profile: the usual naive conversion.
                        var white = 1 - color[3];
                        pixels[at] = Byte((1 - color[0]) * white);
                        pixels[at + 1] = Byte((1 - color[1]) * white);
                        pixels[at + 2] = Byte((1 - color[2]) * white);
                        break;
                }
            }
        }

        return (channels, pixels);
    }

    /// <summary>The alpha of an image from its soft mask or mask, at the image's size; <c>null</c> when it has none that can be read.</summary>
    private static byte[]? Alpha(ImageObject image)
    {
        if (image.SoftMask is { } softMask)
        {
            var mask = image.Related(softMask);
            if (mask.Format != ExtractedImageFormat.Png || mask.IsMask)
            {
                return null; // A JPEG soft mask, say: its samples cannot be read here.
            }

            var (channels, gray) = Colors(mask);
            return Resample(mask.Width, mask.Height, image.Width, image.Height, i => gray[i * channels]);
        }

        switch (image.Mask)
        {
            case Objects.PdfStreamObject stream when image.Related(stream) is { Width: > 0, Height: > 0 } stencil:
                var paints = Stencil(stencil, stencil.Width, stencil.Height);
                return Resample(stencil.Width, stencil.Height, image.Width, image.Height, i => paints[i] ? (byte)255 : (byte)0);
            case Objects.PdfArray ranges:
                return ColorKey(image, ranges);
            default:
                return null;
        }
    }

    /// <summary>
    /// Where a stencil mask paints (ISO 32000-2, 8.9.6.2): samples of 0 paint, unless <c>/Decode [1 0]</c> turns that
    /// around. A mask used as an image's <c>/Mask</c> works the same way: the image shows where the mask would paint.
    /// </summary>
    private static bool[] Stencil(ImageObject mask, int width, int height)
    {
        var samples = new Samples(mask.DecodeData(), width, 1, 1);
        var inverted = mask.Decode is [var low, var high, ..] && low > high;
        var paints = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                paints[(y * width) + x] = (samples.Get(y, x) == 0) != inverted;
            }
        }

        return paints;
    }

    /// <summary>Color key masking (ISO 32000-2, 8.9.6.4): pixels whose samples are all within the ranges are transparent.</summary>
    private static byte[]? ColorKey(ImageObject image, Objects.PdfArray ranges)
    {
        var components = image.ColorSpace!.Components;
        var limits = ranges.Items.Select(i => image.Resolve(i) is Objects.PdfInteger n ? n.Value : -1).ToArray();
        if (limits.Length < 2 * components)
        {
            return null;
        }

        var samples = new Samples(image.DecodeData(), image.Width, components, image.BitsPerComponent);
        var alpha = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var masked = true;
                for (var c = 0; c < components && masked; c++)
                {
                    var sample = samples.Get(y, (x * components) + c);
                    masked = sample >= limits[2 * c] && sample <= limits[(2 * c) + 1];
                }

                alpha[(y * image.Width) + x] = masked ? (byte)0 : (byte)255;
            }
        }

        return alpha;
    }

    /// <summary>A mask's values (by pixel index in the source) at another size, nearest neighbor.</summary>
    private static byte[] Resample(int sourceWidth, int sourceHeight, int width, int height, Func<int, byte> value)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sy = (int)((long)y * sourceHeight / height);
            for (var x = 0; x < width; x++)
            {
                var sx = (int)((long)x * sourceWidth / width);
                result[(y * width) + x] = value((sy * sourceWidth) + sx);
            }
        }

        return result;
    }

    /// <summary>The <c>/Decode</c> ranges (two numbers per component), or the default ranges from 0 to <paramref name="high"/>.</summary>
    private static double[] DecodeRanges(double[]? decode, int components, double high)
    {
        if (decode is not null && decode.Length >= 2 * components && decode.Take(2 * components).All(double.IsFinite))
        {
            return decode;
        }

        var ranges = new double[2 * components];
        for (var c = 0; c < components; c++)
        {
            ranges[(2 * c) + 1] = high;
        }

        return ranges;
    }

    private static double Map(int sample, int max, double low, double high) => low + (sample * (high - low) / max);

    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>The samples of an image's data: rows start on byte boundaries; samples past the end of the data are 0.</summary>
    private sealed class Samples(byte[] data, int width, int components, int bits)
    {
        private readonly long _rowBytes = (((long)width * components * bits) + 7) / 8;

        /// <summary>The sample at <paramref name="index"/> (pixel * components + component) of row <paramref name="row"/>.</summary>
        public int Get(int row, int index)
        {
            var bit = (long)index * bits;
            var offset = (row * _rowBytes) + (bit / 8);
            if (offset >= data.Length)
            {
                return 0;
            }

            return bits switch
            {
                8 => data[offset],
                16 => (data[offset] << 8) | (offset + 1 < data.Length ? data[offset + 1] : 0),
                _ => (data[offset] >> (8 - bits - (int)(bit % 8))) & ((1 << bits) - 1),
            };
        }
    }
}
