using System.Buffers.Binary;

namespace RaiCore.Pdf.Images;

/// <summary>
/// Reads what PDF needs from a JPEG without decoding it (the data is embedded as-is with <c>/DCTDecode</c>):
/// size, components, bit depth, the Adobe color transform and the EXIF orientation.
/// </summary>
internal static class JpegReader
{
    public static DecodedImage Read(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            throw new ImageFormatException("Not a JPEG file.");
        }

        int? width = null, height = null, components = null, precision = null;
        var adobe = false;
        var orientation = 1;
        var pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF)
            {
                throw new ImageFormatException($"Invalid JPEG marker at offset {pos}.");
            }

            var marker = data[pos + 1];
            if (marker == 0xFF)
            {
                pos++; // Fill byte.
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                pos += 2; // Markers without a length.
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                break; // End of image / start of scan: all headers have been seen.
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > data.Length)
            {
                throw new ImageFormatException("Truncated JPEG segment.");
            }

            var segment = data.AsSpan(pos + 4, length - 2);
            switch (marker)
            {
                // SOF0-SOF15 except DHT (C4), JPG (C8) and DAC (CC).
                case >= 0xC0 and <= 0xCF when marker is not (0xC4 or 0xC8 or 0xCC):
                    if (segment.Length < 6)
                    {
                        throw new ImageFormatException("Truncated JPEG frame header.");
                    }

                    if (marker is 0xC3 or 0xC7 or 0xCB or 0xCF)
                    {
                        throw new ImageFormatException("Lossless JPEG is not supported by PDF.");
                    }

                    precision = segment[0];
                    height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                    components = segment[5];
                    break;
                case 0xEE when segment.Length >= 5 && segment[..5].SequenceEqual("Adobe"u8):
                    adobe = true;
                    break;
                case 0xE1 when segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8):
                    orientation = ReadExifOrientation(segment[6..]);
                    break;
            }

            pos += 2 + length;
        }

        if (width is null or 0 || height is null or 0 || components is null)
        {
            throw new ImageFormatException("The JPEG has no frame header or a zero size.");
        }

        ResourceLimits.CheckImagePixels(width.Value, height.Value); // Embedded as it is, but viewers decode it.

        if (precision != 8)
        {
            throw new ImageFormatException($"{precision}-bit JPEG is not supported by PDF viewers.");
        }

        var colorSpace = components switch
        {
            1 => ImageColorSpace.Gray,
            3 => ImageColorSpace.Rgb,
            4 => ImageColorSpace.Cmyk,
            _ => throw new ImageFormatException($"JPEG with {components} components is not supported."),
        };

        return new DecodedImage(width.Value, height.Value, colorSpace, 8, data, ImageEncoding.Jpeg)
        {
            // Photoshop and most other tools write Adobe CMYK JPEGs with inverted values.
            InvertCmyk = colorSpace == ImageColorSpace.Cmyk && adobe,
            Orientation = orientation,
        };
    }

    /// <summary>Finds tag 0x0112 (Orientation) in the first image file directory of an EXIF (TIFF) block.</summary>
    private static int ReadExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return 1;
        }

        var littleEndian = tiff[0] == 'I' && tiff[1] == 'I';
        if (!littleEndian && !(tiff[0] == 'M' && tiff[1] == 'M'))
        {
            return 1;
        }

        ushort U16(ReadOnlySpan<byte> s) => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifd = U32(tiff[4..]);
        if (ifd > (uint)(tiff.Length - 2))
        {
            return 1;
        }

        int count = U16(tiff[(int)ifd..]);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            if (U16(tiff[entry..]) == 0x0112)
            {
                int value = U16(tiff[(entry + 8)..]);
                return value is >= 1 and <= 8 ? value : 1;
            }
        }

        return 1;
    }
}
