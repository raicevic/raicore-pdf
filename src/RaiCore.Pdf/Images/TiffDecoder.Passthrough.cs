using System.Buffers.Binary;

namespace RaiCore.Pdf.Images;

/// <summary>TIFF images embedded without decoding: CCITT fax data and JPEG data.</summary>
internal static partial class TiffDecoder
{
    /// <summary>
    /// CCITT fax data (compression 2: modified Huffman; 3: Group 3; 4: Group 4), embedded with /CCITTFaxDecode. It must
    /// be in one strip (or one tile as wide as the image), except for modified Huffman, whose rows are independent and
    /// byte-aligned, so its strips simply follow each other.
    /// </summary>
    private static DecodedImage Ccitt(TiffFile file, TiffDirectory directory, int width, int height, int compression, int samples, long bits, long photometric)
    {
        if (samples != 1 || bits != 1)
        {
            throw new ImageFormatException("A TIFF fax (CCITT) image must have one 1-bit sample per pixel.");
        }

        if (photometric is not (0 or 1))
        {
            throw new ImageFormatException($"TIFF fax (CCITT) images with photometric interpretation {photometric} are not supported.");
        }

        var layout = ReadLayout(directory, width, height, planes: 1);
        if (layout.Tiled && (layout.Across > 1 || layout.Down > 1 || layout.Width != width))
        {
            throw new ImageFormatException("Tiled TIFF fax (CCITT) images are supported only as one tile as wide as the image.");
        }

        var segments = layout.Across * layout.Down;
        if (segments > 1 && compression != 2)
        {
            throw new ImageFormatException(
                $"TIFF fax images (CCITT {(compression == 4 ? "Group 4" : "Group 3")}) in {segments} strips are not supported: their strips cannot be joined "
                + "without decoding them. Save the file with one strip per page.");
        }

        using var data = new MemoryStream();
        for (var i = 0; i < segments; i++)
        {
            data.Write(Segment(file, layout, i));
        }

        if (data.Length == 0)
        {
            throw new ImageFormatException("The TIFF fax image has no data.");
        }

        var coded = directory.Number(FillOrderTag) == 2 ? ReverseBits(data.GetBuffer().AsSpan(0, (int)data.Length)) : data.ToArray();
        var options = directory.Number(compression == 4 ? T6OptionsTag : T4OptionsTag) ?? 0;
        if (compression != 2 && (options & 2) != 0)
        {
            throw new ImageFormatException("TIFF fax images using uncompressed mode are not supported.");
        }

        // The codes give white and black runs, and TIFF decodes black runs to 1 bits: with WhiteIsZero they show black,
        // as with PDF's default (/BlackIs1 false); with BlackIsZero they show white (inverted, as libtiff shows them),
        // which /BlackIs1 true does.
        var blackIs1 = photometric == 1;
        var parameters = compression switch
        {
            2 => new CcittParameters(0, blackIs1, EncodedByteAlign: true),
            // Fill bits before an end-of-line code (option bit 2) only lengthen its run of zeros, which decoders
            // accept as they are; /EncodedByteAlign would instead skip bits after every row.
            3 => new CcittParameters((options & 1) != 0 ? 1 : 0, blackIs1, EncodedByteAlign: false),
            _ => new CcittParameters(-1, blackIs1, EncodedByteAlign: false),
        };

        return new DecodedImage(width, height, ImageColorSpace.Gray, 1, coded, ImageEncoding.CcittFax) { Ccitt = parameters };
    }

    /// <summary>JPEG data (compression 7), embedded with /DCTDecode: the strips merged into one JPEG with the JPEGTables.</summary>
    private static DecodedImage Jpeg(TiffFile file, TiffDirectory directory, int width, int height, int samples, long photometric)
    {
        var expected = photometric switch
        {
            1 => 1,
            2 or 6 => 3,
            5 => 4,
            _ => throw new ImageFormatException($"JPEG-compressed TIFF images with photometric interpretation {photometric} are not supported."),
        };

        if (samples != expected)
        {
            throw new ImageFormatException(
                $"JPEG-compressed TIFF images with {samples} samples per pixel are not supported (extra samples such as alpha cannot be kept without decoding the JPEG data).");
        }

        if (samples > 1 && directory.Number(PlanarConfigurationTag) == 2)
        {
            throw new ImageFormatException("JPEG-compressed TIFF images in separate planes are not supported.");
        }

        var layout = ReadLayout(directory, width, height, planes: 1);
        if (layout.Tiled && layout.Across * layout.Down > 1)
        {
            throw new ImageFormatException("JPEG-compressed TIFF images in several tiles are not supported: the tiles cannot be merged into one JPEG.");
        }

        var strips = new List<byte[]>(layout.Down);
        for (var i = 0; i < layout.Down; i++)
        {
            strips.Add(Segment(file, layout, i).ToArray());
        }

        var jpeg = MergeJpeg(strips, directory.Bytes(JpegTablesTag), width, height, layout.Height);
        var image = JpegReader.Read(jpeg);
        if (image.Width != width || image.Height != height || image.Components != samples)
        {
            throw new ImageFormatException(
                $"The TIFF's JPEG data is {image.Width} x {image.Height} pixels with {image.Components} components, not {width} x {height} with {samples}.");
        }

        // An RGB TIFF's JPEG data is RGB (libtiff writes it without color conversion and without an Adobe marker);
        // YCbCr data needs the default transform.
        return image with { ColorTransform = photometric == 2 ? 0 : null };
    }

    /// <summary>
    /// Makes one JPEG of a TIFF's JPEG strips. One strip gets the tables (JPEGTables) inserted after its start of image.
    /// Several are joined as one scan with restart markers between them, which reset the decoder as the start of a new
    /// strip does: sound when every strip has the same headers (but its height), one sequential scan and a height
    /// that is a whole number of block rows. Anything else is rejected rather than embedded wrongly.
    /// </summary>
    private static byte[] MergeJpeg(List<byte[]> strips, ReadOnlySpan<byte> tables, int width, int height, int rowsPerStrip)
    {
        // The tables without their own start and end of image.
        if (tables.Length >= 2 && tables[0] == 0xFF && tables[1] == 0xD8)
        {
            tables = tables[2..];
        }

        if (tables.Length >= 2 && tables[^2] == 0xFF && tables[^1] == 0xD9)
        {
            tables = tables[..^2];
        }

        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        output.Write(tables);
        var first = ParseJpegStrip(strips[0]);
        if (strips.Count == 1)
        {
            output.Write(strips[0].AsSpan(2));
            return output.ToArray();
        }

        if (first.Marker is not (0xC0 or 0xC1))
        {
            throw new ImageFormatException("The strips of a progressive or lossless JPEG-compressed TIFF cannot be merged into one JPEG.");
        }

        if (first.HasRestartInterval)
        {
            throw new ImageFormatException("The strips of a JPEG-compressed TIFF with restart intervals cannot be merged into one JPEG.");
        }

        if (rowsPerStrip % first.McuHeight != 0)
        {
            throw new ImageFormatException(
                $"The {rowsPerStrip}-row strips of the JPEG-compressed TIFF cannot be merged into one JPEG: the rows per strip must be a multiple of {first.McuHeight}.");
        }

        var interval = (long)((width + first.McuWidth - 1) / first.McuWidth) * (rowsPerStrip / first.McuHeight);
        if (interval > ushort.MaxValue)
        {
            throw new ImageFormatException("The strips of the JPEG-compressed TIFF are too large to be merged into one JPEG.");
        }

        var header = strips[0].AsSpan(2, first.ScanStart - 2).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(first.Height - 2), (ushort)height);
        var sos = first.SosStart - 2;
        output.Write(header.AsSpan(0, sos));
        output.Write([0xFF, 0xDD, 0x00, 0x04, (byte)(interval >> 8), (byte)interval]); // DRI
        output.Write(header.AsSpan(sos));
        for (var i = 0; i < strips.Count; i++)
        {
            var strip = strips[i];
            var parsed = i == 0 ? first : ParseJpegStrip(strip);
            var rows = BinaryPrimitives.ReadUInt16BigEndian(strip.AsSpan(parsed.Height));
            var last = i == strips.Count - 1;
            var rowsLeft = height - (i * rowsPerStrip);
            if (BinaryPrimitives.ReadUInt16BigEndian(strip.AsSpan(parsed.Height + 2)) != width
                || (last ? rows < rowsLeft || rows > rowsPerStrip : rows != rowsPerStrip))
            {
                throw new ImageFormatException($"The JPEG data of TIFF strip {i} does not match the strip's size.");
            }

            if (i > 0 && !SameHeaders(strips[0].AsSpan(2, first.ScanStart - 2), strip.AsSpan(2, parsed.ScanStart - 2), first.Height - 2))
            {
                throw new ImageFormatException("The strips of the JPEG-compressed TIFF have different JPEG headers and cannot be merged into one JPEG.");
            }

            if (parsed.HasMoreMarkers)
            {
                throw new ImageFormatException("The strips of the JPEG-compressed TIFF have several scans and cannot be merged into one JPEG.");
            }

            if (i > 0)
            {
                output.Write([0xFF, (byte)(0xD0 + ((i - 1) % 8))]); // RSTn
            }

            output.Write(strip.AsSpan(parsed.ScanStart, parsed.ScanEnd - parsed.ScanStart));
        }

        output.Write([0xFF, 0xD9]);
        return output.ToArray();
    }

    /// <summary>Whether two strips' headers are the same but for the frame height at <paramref name="heightAt"/>.</summary>
    private static bool SameHeaders(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int heightAt) =>
        a.Length == b.Length && a[..heightAt].SequenceEqual(b[..heightAt]) && a[(heightAt + 2)..].SequenceEqual(b[(heightAt + 2)..]);

    /// <summary>The layout of one strip's JPEG data.</summary>
    /// <param name="Marker">The frame header's marker (SOFn).</param>
    /// <param name="Height">The offset of the frame height (the width follows).</param>
    /// <param name="SosStart">The offset of the start of scan marker.</param>
    /// <param name="ScanStart">The offset of the entropy-coded data, after the scan header.</param>
    /// <param name="ScanEnd">The offset of the end of the entropy-coded data (the end of image marker, or the data's end).</param>
    /// <param name="HasRestartInterval">Whether the headers define a restart interval.</param>
    /// <param name="HasMoreMarkers">Whether another marker (another scan) follows the first scan before the end of image.</param>
    /// <param name="McuWidth">The width of a minimum coded unit in pixels.</param>
    /// <param name="McuHeight">Its height.</param>
    private readonly record struct JpegStrip(int Marker, int Height, int SosStart, int ScanStart, int ScanEnd, bool HasRestartInterval, bool HasMoreMarkers, int McuWidth, int McuHeight);

    private static JpegStrip ParseJpegStrip(byte[] strip)
    {
        if (strip.Length < 4 || strip[0] != 0xFF || strip[1] != 0xD8)
        {
            throw new ImageFormatException("The JPEG data of a TIFF strip does not start with a start of image marker.");
        }

        int marker = 0, heightAt = 0, mcuWidth = 8, mcuHeight = 8;
        var restart = false;
        var position = 2;
        while (true)
        {
            if (position + 4 > strip.Length || strip[position] != 0xFF)
            {
                throw new ImageFormatException("The JPEG data of a TIFF strip is truncated or damaged.");
            }

            var code = strip[position + 1];
            if (code is 0xFF or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += code == 0xFF ? 1 : 2;
                continue;
            }

            if (code is 0xD8 or 0xD9)
            {
                throw new ImageFormatException("The JPEG data of a TIFF strip has no image.");
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(strip.AsSpan(position + 2));
            if (length < 2 || position + 2 + length > strip.Length)
            {
                throw new ImageFormatException("The JPEG data of a TIFF strip is truncated.");
            }

            if (code == 0xDA)
            {
                if (marker == 0)
                {
                    throw new ImageFormatException("The JPEG data of a TIFF strip has no frame header.");
                }

                var scanStart = position + 2 + length;
                var (scanEnd, more) = FindScanEnd(strip, scanStart);
                return new JpegStrip(marker, heightAt, position, scanStart, scanEnd, restart, more, mcuWidth, mcuHeight);
            }

            if (code is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                var components = length >= 8 ? strip[position + 9] : 0;
                if (components == 0 || length < 8 + (3 * components))
                {
                    throw new ImageFormatException("The JPEG frame header of a TIFF strip is damaged.");
                }

                int maxH = 1, maxV = 1;
                for (var c = 0; c < components; c++)
                {
                    var sampling = strip[position + 11 + (3 * c)];
                    maxH = Math.Max(maxH, sampling >> 4);
                    maxV = Math.Max(maxV, sampling & 15);
                }

                marker = code;
                heightAt = position + 5;

                // A single component is coded block by block, whatever its sampling factors.
                (mcuWidth, mcuHeight) = components == 1 ? (8, 8) : (8 * maxH, 8 * maxV);
            }
            else if (code == 0xDD)
            {
                restart = true;
            }

            position += 2 + length;
        }
    }

    /// <summary>
    /// Finds where a scan's entropy-coded data ends: at the next marker (other than stuffed bytes and restart markers).
    /// Returns whether that marker is something else than the end of image (another scan follows).
    /// </summary>
    private static (int End, bool MoreMarkers) FindScanEnd(byte[] strip, int start)
    {
        for (var i = start; i + 1 < strip.Length; i++)
        {
            if (strip[i] != 0xFF)
            {
                continue;
            }

            var next = strip[i + 1];
            if (next is 0x00 or (>= 0xD0 and <= 0xD7))
            {
                i++;
            }
            else if (next != 0xFF)
            {
                return (i, next != 0xD9);
            }
        }

        return (strip.Length, false);
    }
}
