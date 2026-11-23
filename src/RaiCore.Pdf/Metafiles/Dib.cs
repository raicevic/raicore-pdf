using System.Buffers.Binary;
using RaiCore.Pdf.Images;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// Device-independent bitmaps in metafiles ([MS-WMF] DeviceIndependentBitmap): decoded by the BMP decoder (a file
/// header put before them), or as the JPEG or PNG they hold.
/// </summary>
internal static class Dib
{
    private const int BiJpeg = 4, BiPng = 5;

    /// <summary>
    /// The length of the header and color table of a packed DIB (the bits follow them), or null when the header is
    /// damaged.
    /// </summary>
    public static int? InfoLength(ReadOnlySpan<byte> dib, int usage = 0)
    {
        if (dib.Length < 12)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize == 12)
        {
            var coreBits = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            var coreColors = coreBits <= 8 ? 1 << coreBits : 0;
            return 12 + (usage == 2 ? 0 : coreColors * (usage == 1 ? 2 : 3));
        }

        if (headerSize < 40 || headerSize > dib.Length || dib.Length < 40)
        {
            return null;
        }

        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var used = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        long colors = used != 0 ? used : bits <= 8 ? 1 << bits : 0;
        long length = headerSize + (usage == 2 ? 0 : colors * (usage == 1 ? 2 : 4));
        if (headerSize == 40 && compression is 3 or 6)
        {
            length += compression == 6 ? 16 : 12;
        }

        return length <= dib.Length ? (int)length : null;
    }

    /// <summary>
    /// Decodes a bitmap from its BITMAPINFO and bits; null (reported) when it cannot be read or is larger than
    /// <see cref="ResourceLimits.MaxImagePixels"/>.
    /// </summary>
    /// <param name="context">For reports.</param>
    /// <param name="info">The header and color table.</param>
    /// <param name="bits">The pixels.</param>
    /// <param name="usage">0 when the color table holds colors, 1 when it holds indexes into <paramref name="palette"/>.</param>
    /// <param name="palette">The selected logical palette, if any.</param>
    /// <param name="mono">For a monochrome bitmap drawn in the text and background colors: the colors of 0 and 1 bits.</param>
    /// <param name="keepAlpha">Whether the alpha of 32-bit pixels counts (only for EMR_ALPHABLEND; GDI ignores it elsewhere).</param>
    public static DecodedImage? Decode(MetafileContext context, ReadOnlySpan<byte> info, ReadOnlySpan<byte> bits, int usage, GdiPalette? palette, (PdfColor Zero, PdfColor One)? mono, bool keepAlpha)
    {
        if (info.Length < 12)
        {
            return null;
        }

        try
        {
            if (info.Length >= 40 && BinaryPrimitives.ReadUInt32LittleEndian(info[16..]) is BiJpeg or BiPng)
            {
                var embedded = PdfImage.Load(bits.ToArray());
                return embedded.Format == PdfImageFormat.Jpeg || embedded.Format == PdfImageFormat.Png ? embedded.Image : null;
            }

            var header = FixColorTable(info.ToArray(), usage, palette, mono);
            var file = new byte[14 + header.Length + bits.Length];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), 14 + header.Length);
            header.CopyTo(file, 14);
            bits.CopyTo(file.AsSpan(14 + header.Length));
            if (header.Length >= 40 && BinaryPrimitives.ReadInt32LittleEndian(header) >= 40)
            {
                // The image size may be missing or wrong; the decoder takes the rows from the dimensions.
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14 + 20), 0);
            }

            var image = BmpDecoder.Decode(file);
            return keepAlpha ? image : image with { Alpha = null };
        }
        catch (ImageFormatException ex)
        {
            context.Report($"A bitmap in the picture cannot be read ({ex.Message}); it is left out.", damaged: true);
            return null;
        }
        catch (ResourceLimitExceededException ex) when (ex.LimitName == nameof(ResourceLimits.MaxImagePixels))
        {
            context.Report($"A bitmap in the picture is left out: it has more pixels than ResourceLimits.MaxImagePixels allows ({ex.Limit}).", damaged: true);
            return null;
        }
    }

    /// <summary>Replaces a color table of palette indexes by the palette's colors, or a monochrome table by the given colors.</summary>
    private static byte[] FixColorTable(byte[] header, int usage, GdiPalette? palette, (PdfColor Zero, PdfColor One)? mono)
    {
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (headerSize < 40 || header.Length < headerSize)
        {
            return header;
        }

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        if (bitCount > 8)
        {
            return header;
        }

        var used = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        var colors = (int)Math.Min(used != 0 ? used : 1u << bitCount, 256);
        if (usage == 2 || header.Length < headerSize + (colors * (usage == 1 ? 2 : 4)))
        {
            // No color table (DIB_PAL_INDICES, or cut short): the monochrome colors, the palette, or grays.
            var table = new byte[colors * 4];
            for (var i = 0; i < colors; i++)
            {
                var gray = colors > 1 ? i / (double)(colors - 1) : 0;
                var color = mono is { } two && colors == 2 ? (i == 0 ? two.Zero : two.One)
                    : palette is { } p && i < p.Colors.Length ? p.Colors[i] : new PdfColor(gray, gray, gray);
                Quad(table, i, color);
            }

            var replaced = new byte[headerSize + table.Length];
            header.AsSpan(0, headerSize).CopyTo(replaced);
            table.CopyTo(replaced, headerSize);
            return replaced;
        }

        if (usage == 1)
        {
            // Indexes into the palette (2 bytes each): the table becomes RGB quads, which needs more room.
            var table = new byte[colors * 4];
            for (var i = 0; i < colors && headerSize + (i * 2) + 2 <= header.Length; i++)
            {
                var index = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(headerSize + (i * 2)));
                var color = palette is { } p && index < p.Colors.Length ? p.Colors[index] : new PdfColor(index / 255.0 % 1, index / 255.0 % 1, index / 255.0 % 1);
                Quad(table, i, color);
            }

            // The header and table only are passed, so the table can be replaced by a longer one.
            var replaced = new byte[headerSize + table.Length];
            header.AsSpan(0, headerSize).CopyTo(replaced);
            table.CopyTo(replaced, headerSize);
            return replaced;
        }

        if (mono is { } m && bitCount == 1 && header.Length >= headerSize + 8)
        {
            Quad(header.AsSpan(headerSize), 0, m.Zero);
            Quad(header.AsSpan(headerSize), 1, m.One);
        }

        return header;
    }

    private static void Quad(Span<byte> table, int index, PdfColor color)
    {
        table[(index * 4) + 0] = (byte)Math.Round(color.Blue * 255);
        table[(index * 4) + 1] = (byte)Math.Round(color.Green * 255);
        table[(index * 4) + 2] = (byte)Math.Round(color.Red * 255);
        table[(index * 4) + 3] = 0;
    }

    /// <summary>A copy of the image with a color shown transparent (a color key mask), for raster operations that keep what is under it.</summary>
    public static DecodedImage WithTransparent(DecodedImage image, PdfColor color)
    {
        if (image.Alpha is not null || image.ColorKey is not null || image.Encoding != ImageEncoding.Raw)
        {
            return image;
        }

        var max = (1 << image.BitsPerComponent) - 1;
        int Level(double value) => (int)Math.Round(value * max);
        switch (image.ColorSpace)
        {
            case ImageColorSpace.Rgb:
                return image with { ColorKey = [Level(color.Red), Level(color.Red), Level(color.Green), Level(color.Green), Level(color.Blue), Level(color.Blue)] };
            case ImageColorSpace.Gray:
                var gray = Level((color.Red + color.Green + color.Blue) / 3);
                return image with { ColorKey = [gray, gray] };
            case ImageColorSpace.Indexed when image.Palette is { } palette:
                var (r, g, b) = ((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255));
                for (var i = 0; i + 2 < palette.Length; i += 3)
                {
                    if (palette[i] == r && palette[i + 1] == g && palette[i + 2] == b)
                    {
                        return image with { ColorKey = [i / 3, i / 3] };
                    }
                }

                return image;
            default:
                return image;
        }
    }

    /// <summary>
    /// A pattern brush of a decoded bitmap: its pixels as RGB, top row first (null when its format is not handled), and
    /// its average color.
    /// </summary>
    public static PatternBrush? Pattern(DecodedImage image)
    {
        if (image.Encoding != ImageEncoding.Raw || (long)image.Width * image.Height > 1 << 20)
        {
            return null;
        }

        var (w, h) = (image.Width, image.Height);
        var rgb = new byte[w * h * 3];
        var components = image.ColorSpace switch { ImageColorSpace.Rgb => 3, ImageColorSpace.Cmyk => 4, _ => 1 };
        var bpc = image.BitsPerComponent;
        var stride = ((w * components * bpc) + 7) / 8;
        if (image.Data.Length < stride * h || image.ColorSpace == ImageColorSpace.Cmyk || bpc > 8)
        {
            return null;
        }

        long sumR = 0, sumG = 0, sumB = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                byte r, g, b;
                if (image.ColorSpace == ImageColorSpace.Rgb)
                {
                    var o = (y * stride) + (x * 3);
                    (r, g, b) = (image.Data[o], image.Data[o + 1], image.Data[o + 2]);
                }
                else
                {
                    var bit = x * bpc;
                    var value = (image.Data[(y * stride) + (bit / 8)] >> (8 - bpc - (bit % 8))) & ((1 << bpc) - 1);
                    if (image.ColorSpace == ImageColorSpace.Indexed && image.Palette is { } palette)
                    {
                        var o = value * 3;
                        (r, g, b) = o + 2 < palette.Length ? (palette[o], palette[o + 1], palette[o + 2]) : ((byte)0, (byte)0, (byte)0);
                    }
                    else
                    {
                        var level = (byte)(value * 255 / ((1 << bpc) - 1));
                        (r, g, b) = (level, level, level);
                    }
                }

                var p = ((y * w) + x) * 3;
                (rgb[p], rgb[p + 1], rgb[p + 2]) = (r, g, b);
                sumR += r;
                sumG += g;
                sumB += b;
            }
        }

        var count = Math.Max(1, (long)w * h * 255);
        return new PatternBrush(w, h, rgb, 1, new PdfColor(sumR / (double)count, sumG / (double)count, sumB / (double)count));
    }
}
