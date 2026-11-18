namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes TIFF (TIFF 6.0 and BigTIFF, either byte order): bilevel, grayscale, palette, RGB and CMYK images with 1, 2,
/// 4, 8 or 16 bits per sample, in strips or tiles, interleaved or in separate planes, uncompressed or compressed with
/// PackBits, LZW or Deflate, with or without the horizontal differencing predictor. An extra alpha sample (associated
/// or unassociated) becomes a soft mask. JPEG-compressed images (compression 7) are embedded without decoding, their
/// strips merged into one JPEG; CCITT Group 3 and 4 fax images (compressions 2, 3 and 4) are embedded as they are with
/// /CCITTFaxDecode. Each image file directory is a page. The Orientation tag is applied, and the resolution sets the
/// natural size.
/// </summary>
/// <remarks>
/// Not supported (an <see cref="ImageFormatException"/> says why): floating point and signed samples, YCbCr without
/// JPEG, CIE L*a*b*, old-style JPEG (compression 6) and other compressions, fax images in several strips (Group 3 and
/// 4 strips cannot be joined without decoding them) and JPEG data that cannot be merged into one JPEG (several tiles,
/// progressive strips, strips of a height that is not a multiple of the JPEG's block rows).
/// </remarks>
internal static partial class TiffDecoder
{
    private const ushort ImageWidthTag = 256, ImageLengthTag = 257, BitsPerSampleTag = 258, CompressionTag = 259,
        PhotometricTag = 262, FillOrderTag = 266, StripOffsetsTag = 273, OrientationTag = 274, SamplesPerPixelTag = 277,
        RowsPerStripTag = 278, StripByteCountsTag = 279, XResolutionTag = 282, YResolutionTag = 283,
        PlanarConfigurationTag = 284, T4OptionsTag = 292, T6OptionsTag = 293, ResolutionUnitTag = 296,
        PredictorTag = 317, ColorMapTag = 320, TileWidthTag = 322, TileLengthTag = 323, TileOffsetsTag = 324,
        TileByteCountsTag = 325, InkSetTag = 332, ExtraSamplesTag = 338, SampleFormatTag = 339, JpegTablesTag = 347;

    /// <summary>The most samples per pixel (color and extra samples) an image may have.</summary>
    private const int MaxSamples = 8;

    /// <summary>Whether the data starts like a TIFF or BigTIFF file, in either byte order.</summary>
    public static bool IsTiff(ReadOnlySpan<byte> data) => data.Length >= 8
        && ((data[0] == 'I' && data[1] == 'I' && (data[2] is 42 or 43) && data[3] == 0)
            || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && (data[3] is 42 or 43)));

    /// <summary>The number of pages (image file directories) in the file.</summary>
    public static int CountPages(byte[] data) => new TiffFile(data).CountDirectories();

    /// <summary>Decodes the page at <paramref name="pageIndex"/> (zero-based).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The file has fewer pages.</exception>
    public static DecodedImage Decode(byte[] data, int pageIndex = 0)
    {
        var file = new TiffFile(data);
        var directory = file.Directory(pageIndex);
        var width = directory.Required(ImageWidthTag);
        var height = directory.Required(ImageLengthTag);
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw new ImageFormatException("TIFF size must be positive.");
        }

        ResourceLimits.CheckImagePixels(width, height);

        var compression = directory.Number(CompressionTag) ?? 1;
        var samples = directory.Number(SamplesPerPixelTag) ?? 1;
        if (samples is < 1 or > MaxSamples)
        {
            throw new ImageFormatException($"TIFF images with {samples} samples per pixel are not supported.");
        }

        var bitDepths = directory.Numbers(BitsPerSampleTag, samples) ?? [1];
        var bits = bitDepths[0];
        if (bitDepths.Any(b => b != bits))
        {
            throw new ImageFormatException("TIFF images whose samples have different bit depths are not supported.");
        }

        if (directory.Numbers(SampleFormatTag, samples) is { } formats && formats.Any(f => f is not (1 or 4)))
        {
            throw new ImageFormatException("Only unsigned integer TIFF samples are supported, not floating point or signed ones.");
        }

        var photometric = directory.Number(PhotometricTag) ?? (compression is 2 or 3 or 4 ? 0 : samples >= 3 ? 2 : 1);
        var image = compression switch
        {
            2 or 3 or 4 => Ccitt(file, directory, (int)width, (int)height, (int)compression, (int)samples, bits, photometric),
            7 => Jpeg(file, directory, (int)width, (int)height, (int)samples, photometric),
            1 or 5 or 8 or 32946 or 32773 => Raster(file, directory, (int)width, (int)height, (int)compression, (int)samples, bits, photometric),
            6 => throw new ImageFormatException("Old-style JPEG compression in TIFF (compression 6) is not supported."),
            _ => throw new ImageFormatException($"TIFF compression {compression} is not supported."),
        };

        return image with
        {
            Orientation = directory.OptionalNumber(OrientationTag) is { } orientation and >= 1 and <= 8 ? (int)orientation : 1,
            Resolution = Resolution(directory),
        };
    }

    /// <summary>The resolution in pixels per inch, if the file gives one in inches or centimeters within reason.</summary>
    private static (double X, double Y)? Resolution(TiffDirectory directory)
    {
        var unit = directory.OptionalNumber(ResolutionUnitTag) ?? 2;
        if (unit is not (2 or 3))
        {
            return null; // No absolute unit: only an aspect ratio, which CSS sizing ignores as for other formats.
        }

        var x = directory.OptionalRational(XResolutionTag);
        var y = directory.OptionalRational(YResolutionTag);
        x ??= y;
        y ??= x;
        if (x is not { } horizontal || y is not { } vertical)
        {
            return null;
        }

        var scale = unit == 3 ? 2.54 : 1;
        (horizontal, vertical) = (horizontal * scale, vertical * scale);
        return horizontal is >= 1 and <= 100_000 && vertical is >= 1 and <= 100_000 ? (horizontal, vertical) : null;
    }

    /// <summary>Where an image's strips or tiles are, and their size.</summary>
    /// <param name="Width">The width of a strip or tile in pixels.</param>
    /// <param name="Height">The height of a strip or tile in rows (the last strip may have fewer).</param>
    /// <param name="Across">Tiles per row of tiles (1 for strips).</param>
    /// <param name="Down">Rows of tiles, or strips.</param>
    /// <param name="Offsets">The file offset of each strip or tile, plane after plane.</param>
    /// <param name="Counts">The byte count of each, if the file gives them.</param>
    /// <param name="Tiled">Whether the image is tiled.</param>
    private sealed record Layout(int Width, int Height, int Across, int Down, long[] Offsets, long[]? Counts, bool Tiled)
    {
        public string Kind => Tiled ? "tile" : "strip";
    }

    private static Layout ReadLayout(TiffDirectory directory, int width, int height, int planes)
    {
        long segmentWidth, segmentHeight;
        bool tiled = directory.Has(TileWidthTag) || directory.Has(TileLengthTag);
        if (tiled)
        {
            segmentWidth = directory.Required(TileWidthTag);
            segmentHeight = directory.Required(TileLengthTag);
            if (segmentWidth <= 0 || segmentHeight <= 0 || segmentWidth > int.MaxValue || segmentHeight > int.MaxValue)
            {
                throw new ImageFormatException("TIFF tile size must be positive.");
            }

            ResourceLimits.CheckImagePixels(segmentWidth, segmentHeight);
        }
        else
        {
            segmentWidth = width;
            segmentHeight = directory.Number(RowsPerStripTag) is { } rows and > 0 and < int.MaxValue ? Math.Min(rows, height) : height;
        }

        var across = (width + segmentWidth - 1) / segmentWidth;
        var down = (height + segmentHeight - 1) / segmentHeight;
        var total = across * down * planes;
        var offsets = directory.Numbers(tiled ? TileOffsetsTag : StripOffsetsTag)
            ?? throw new ImageFormatException($"The TIFF has no {(tiled ? "tile" : "strip")} offsets.");
        if (offsets.Length < total)
        {
            throw new ImageFormatException($"The TIFF has {offsets.Length} {(tiled ? "tile" : "strip")} offsets for {total} {(tiled ? "tiles" : "strips")}.");
        }

        var counts = directory.Numbers(tiled ? TileByteCountsTag : StripByteCountsTag);
        return new Layout((int)segmentWidth, (int)segmentHeight, (int)across, (int)down, offsets, counts is null || counts.Length < total ? null : counts, tiled);
    }

    /// <summary>The bytes of strip or tile <paramref name="index"/>; without byte counts, up to the end of the file.</summary>
    private static ReadOnlySpan<byte> Segment(TiffFile file, Layout layout, int index)
    {
        var data = file.Data;
        var offset = layout.Offsets[index];
        var count = layout.Counts?[index] ?? data.Length - offset;
        if (count == 0)
        {
            return []; // An empty (sparse) strip or tile.
        }

        if (offset < 0 || offset >= data.Length)
        {
            throw new ImageFormatException($"TIFF {layout.Kind} {index} is outside the file.");
        }

        return data.AsSpan((int)offset, (int)Math.Clamp(count, 0, data.Length - offset));
    }

    /// <summary>A TIFF or BigTIFF file: its byte order and its chain of image file directories.</summary>
    private sealed class TiffFile
    {
        private readonly long _firstDirectory;

        public TiffFile(byte[] data)
        {
            if (!IsTiff(data))
            {
                throw new ImageFormatException("Not a TIFF file.");
            }

            Data = data;
            LittleEndian = data[0] == 'I';
            Big = U16(2) == 43;
            if (Big)
            {
                if (data.Length < 16 || U16(4) != 8 || U16(6) != 0)
                {
                    throw new ImageFormatException("Invalid BigTIFF header.");
                }

                _firstDirectory = U64(8);
            }
            else
            {
                _firstDirectory = U32(4);
            }
        }

        public byte[] Data { get; }

        public bool LittleEndian { get; }

        /// <summary>Whether the file is a BigTIFF (64-bit offsets).</summary>
        public bool Big { get; }

        public ushort U16(long position) => LittleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan((int)position))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)position));

        public uint U32(long position) => LittleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan((int)position))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan((int)position));

        /// <summary>A 64-bit value; one beyond <see cref="long.MaxValue"/> reads as <see cref="long.MaxValue"/>, which no file reaches.</summary>
        public long U64(long position)
        {
            var value = RawU64(position);
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }

        public ulong RawU64(long position) => LittleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan((int)position))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(Data.AsSpan((int)position));

        /// <summary>Counts the directories, following the chain until it ends, leaves the file or loops.</summary>
        public int CountDirectories()
        {
            var count = 0;
            foreach (var _ in Directories())
            {
                count++;
            }

            return count;
        }

        /// <summary>The directory of page <paramref name="pageIndex"/>.</summary>
        public TiffDirectory Directory(int pageIndex)
        {
            var count = 0;
            foreach (var directory in Directories())
            {
                if (count++ == pageIndex)
                {
                    return directory;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"The TIFF has {count} {(count == 1 ? "page" : "pages")}.");
        }

        private IEnumerable<TiffDirectory> Directories()
        {
            var seen = new HashSet<long>();
            var offset = _firstDirectory;
            while (offset != 0 && seen.Add(offset))
            {
                var directory = ReadDirectory(offset);
                if (directory is null)
                {
                    if (seen.Count == 1)
                    {
                        throw new ImageFormatException("The TIFF's first image file directory is outside the file.");
                    }

                    yield break; // A damaged link: the pages before it are still read, as libtiff does.
                }

                yield return directory;
                offset = directory.Next;
            }

            if (seen.Count == 0)
            {
                throw new ImageFormatException("The TIFF has no images.");
            }
        }

        /// <summary>Reads the directory at <paramref name="offset"/>, or returns null if it does not fit in the file.</summary>
        private TiffDirectory? ReadDirectory(long offset)
        {
            var (countSize, entrySize, offsetSize) = Big ? (8, 20, 8) : (2, 12, 4);
            if (offset < 8 || offset > Data.Length - countSize)
            {
                return null;
            }

            var count = Big ? U64(offset) : U16(offset);
            var entries = offset + countSize;
            if (count > (Data.Length - entries) / entrySize)
            {
                return null;
            }

            var nextPosition = entries + (count * entrySize);
            var next = nextPosition + offsetSize <= Data.Length ? (Big ? U64(nextPosition) : U32(nextPosition)) : 0;
            var fields = new Dictionary<ushort, TiffField>();
            for (var i = 0L; i < count; i++)
            {
                var entry = entries + (i * entrySize);
                var tag = U16(entry);
                var type = U16(entry + 2);
                var size = TypeSize(type);
                if (size == 0)
                {
                    continue; // An unknown field type: the field is ignored, as TIFF 6.0 says.
                }

                var values = Big ? U64(entry + 4) : U32(entry + 4);
                long position;
                if (values > Data.Length / size)
                {
                    position = -1; // More values than the file has bytes.
                }
                else if (values * size <= offsetSize)
                {
                    position = entry + (Big ? 12 : 8);
                }
                else
                {
                    position = Big ? U64(entry + 12) : U32(entry + 8);
                    if (position > Data.Length - (values * size))
                    {
                        position = -1;
                    }
                }

                fields.TryAdd(tag, new TiffField(tag, type, values, position));
            }

            return new TiffDirectory(this, fields, next);
        }

        /// <summary>The size of one value of a TIFF field type; 0 for unknown types.</summary>
        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1, // BYTE, ASCII, SBYTE, UNDEFINED
            3 or 8 => 2, // SHORT, SSHORT
            4 or 9 or 11 or 13 => 4, // LONG, SLONG, FLOAT, IFD
            5 or 10 or 12 or 16 or 17 or 18 => 8, // RATIONAL, SRATIONAL, DOUBLE, LONG8, SLONG8, IFD8
            _ => 0,
        };
    }

    /// <summary>A field of a directory: <paramref name="Position"/> is where its values are, or -1 if outside the file.</summary>
    private readonly record struct TiffField(ushort Tag, ushort Type, long Count, long Position);

    /// <summary>An image file directory: one page's fields.</summary>
    private sealed class TiffDirectory(TiffFile file, Dictionary<ushort, TiffField> fields, long next)
    {
        /// <summary>The offset of the next directory; 0 if none.</summary>
        public long Next => next;

        public bool Has(ushort tag) => fields.ContainsKey(tag);

        /// <summary>The first value of an integer field, or null if it is absent.</summary>
        public long? Number(ushort tag) => Numbers(tag, 1) is [var value, ..] ? value : null;

        public long Required(ushort tag) => Number(tag) ?? throw new ImageFormatException($"The TIFF has no tag {tag}.");

        /// <summary>The first value of an integer field, or null if it is absent or unreadable (for tags that may be ignored).</summary>
        public long? OptionalNumber(ushort tag)
        {
            try
            {
                return Number(tag);
            }
            catch (ImageFormatException)
            {
                return null;
            }
        }

        /// <summary>The values of an integer field (at most <paramref name="max"/>), or null if it is absent or has none.</summary>
        public long[]? Numbers(ushort tag, long max = long.MaxValue)
        {
            if (!fields.TryGetValue(tag, out var field) || field.Count == 0)
            {
                return null;
            }

            var size = field.Type switch
            {
                1 or 6 => 1,
                3 or 8 => 2,
                4 or 9 or 13 => 4,
                16 or 17 or 18 => 8,
                _ => throw new ImageFormatException($"TIFF tag {tag} has type {field.Type}, not an integer type."),
            };

            CheckPosition(field);
            var values = new long[Math.Min(field.Count, max)];
            for (var i = 0; i < values.Length; i++)
            {
                var position = field.Position + ((long)i * size);
                values[i] = field.Type switch
                {
                    1 => file.Data[position],
                    6 => (sbyte)file.Data[position],
                    3 => file.U16(position),
                    8 => (short)file.U16(position),
                    4 or 13 => file.U32(position),
                    9 => (int)file.U32(position),
                    _ => file.U64(position),
                };
            }

            return values;
        }

        /// <summary>The first value of a rational (or integer or floating point) field, or null if absent or unreadable.</summary>
        public double? OptionalRational(ushort tag)
        {
            if (!fields.TryGetValue(tag, out var field) || field.Position < 0 || field.Count == 0)
            {
                return null;
            }

            var position = field.Position;
            return field.Type switch
            {
                5 => (double)file.U32(position) / file.U32(position + 4),
                10 => (double)(int)file.U32(position) / (int)file.U32(position + 4),
                11 => BitConverter.Int32BitsToSingle((int)file.U32(position)),
                12 => BitConverter.UInt64BitsToDouble(file.RawU64(position)),
                3 or 4 => OptionalNumber(tag),
                _ => null,
            };
        }

        /// <summary>The raw bytes of a field, or empty if it is absent.</summary>
        public ReadOnlySpan<byte> Bytes(ushort tag)
        {
            if (!fields.TryGetValue(tag, out var field))
            {
                return [];
            }

            if (field.Type is not (1 or 7))
            {
                throw new ImageFormatException($"TIFF tag {tag} has type {field.Type}, not bytes.");
            }

            CheckPosition(field);
            return file.Data.AsSpan((int)field.Position, (int)field.Count);
        }

        private static void CheckPosition(TiffField field)
        {
            if (field.Position < 0)
            {
                throw new ImageFormatException($"The values of TIFF tag {field.Tag} are outside the file.");
            }
        }
    }
}
