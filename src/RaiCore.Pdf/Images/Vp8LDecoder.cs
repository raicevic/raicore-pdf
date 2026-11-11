namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes VP8L, the lossless WebP format: prefix (Huffman) codes with meta codes, LZ77 backward references with the
/// 2D distance map, the color cache, and the predictor, cross-color, subtract-green and color-indexing transforms.
/// Also decodes the headerless VP8L streams that hold lossless-compressed alpha (ALPH chunks).
/// </summary>
internal sealed class Vp8LDecoder
{
    private const int NumLiteralCodes = 256, NumLengthCodes = 24, NumDistanceCodes = 40;
    private const int MaxColorCacheBits = 11;
    private const int Green = 0, Red = 1, Blue = 2, Alpha = 3, Distance = 4;

    private readonly BitReader _br;

    private Vp8LDecoder(byte[] data, int offset, int length) => _br = new BitReader(data, offset, length);

    private enum Transform
    {
        Predictor = 0,
        CrossColor = 1,
        SubtractGreen = 2,
        ColorIndexing = 3,
    }

    /// <summary>Decodes a VP8L bitstream (with its 5-byte header) to ARGB pixels (0xAARRGGBB), row by row.</summary>
    public static uint[] Decode(byte[] data, int offset, int length, out int width, out int height)
    {
        if (length < 5 || data[offset] != 0x2F)
        {
            throw new ImageFormatException("Invalid VP8L signature.");
        }

        var decoder = new Vp8LDecoder(data, offset + 1, length - 1);
        var br = decoder._br;
        width = (int)br.ReadBits(14) + 1;
        height = (int)br.ReadBits(14) + 1;
        br.ReadBits(1); // Alpha hint; the decoded alpha values are used as they are.
        if (br.ReadBits(3) != 0)
        {
            throw new ImageFormatException("Unsupported VP8L version.");
        }

        return decoder.DecodeLevel0(width, height);
    }

    /// <summary>Decodes a headerless VP8L stream of known size, as in an ALPH chunk; returns the green channel.</summary>
    public static byte[] DecodeAlpha(byte[] data, int offset, int length, int width, int height)
    {
        var pixels = new Vp8LDecoder(data, offset, length).DecodeLevel0(width, height);
        var alpha = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            alpha[i] = (byte)(pixels[i] >> 8);
        }

        return alpha;
    }

    /// <summary>The main image: transforms, then the entropy-coded pixels, then the transforms are undone.</summary>
    private uint[] DecodeLevel0(int width, int height)
    {
        ResourceLimits.CheckImagePixels(width, height);

        var transforms = new List<(Transform Type, int Bits, int XSize, uint[]? Data)>();
        var seen = 0;
        var xsize = width;
        while (_br.ReadBits(1) != 0)
        {
            var type = (Transform)_br.ReadBits(2);
            if ((seen & (1 << (int)type)) != 0)
            {
                throw new ImageFormatException("Repeated VP8L transform.");
            }

            seen |= 1 << (int)type;
            switch (type)
            {
                case Transform.Predictor:
                case Transform.CrossColor:
                    {
                        var bits = (int)_br.ReadBits(3) + 2;
                        var data = DecodeImageStream(SubSampleSize(xsize, bits), SubSampleSize(height, bits));
                        transforms.Add((type, bits, xsize, data));
                        break;
                    }

                case Transform.ColorIndexing:
                    {
                        var colors = (int)_br.ReadBits(8) + 1;
                        var bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                        var palette = DecodeImageStream(colors, 1);

                        // Entries are coded as differences from the previous one; indices past the end are transparent black.
                        var map = new uint[1 << (8 >> bits)];
                        map[0] = palette[0];
                        for (var i = 1; i < colors; i++)
                        {
                            map[i] = AddPixels(palette[i], map[i - 1]);
                        }

                        transforms.Add((type, bits, xsize, map));
                        xsize = SubSampleSize(xsize, bits);
                        break;
                    }

                default:
                    transforms.Add((type, 0, xsize, null));
                    break;
            }
        }

        var pixels = DecodeEntropyImage(xsize, height, allowMetaCodes: true);
        for (var t = transforms.Count - 1; t >= 0; t--)
        {
            var (type, bits, tw, data) = transforms[t];
            switch (type)
            {
                case Transform.Predictor:
                    InversePredictor(pixels, tw, height, bits, data!);
                    break;
                case Transform.CrossColor:
                    InverseCrossColor(pixels, tw, height, bits, data!);
                    break;
                case Transform.SubtractGreen:
                    for (var i = 0; i < pixels.Length; i++)
                    {
                        var argb = pixels[i];
                        var green = (argb >> 8) & 0xFF;
                        var redBlue = ((argb & 0x00FF00FF) + ((green << 16) | green)) & 0x00FF00FF;
                        pixels[i] = (argb & 0xFF00FF00) | redBlue;
                    }

                    break;
                default:
                    pixels = InverseColorIndexing(pixels, tw, height, bits, data!);
                    break;
            }
        }

        return pixels;
    }

    /// <summary>A sub-image (transform data, meta codes or palette): no transforms and a single set of codes.</summary>
    private uint[] DecodeImageStream(int xsize, int ysize) => DecodeEntropyImage(xsize, ysize, allowMetaCodes: false);

    private uint[] DecodeEntropyImage(int xsize, int ysize, bool allowMetaCodes)
    {
        var cacheBits = 0;
        if (_br.ReadBits(1) != 0)
        {
            cacheBits = (int)_br.ReadBits(4);
            if (cacheBits is < 1 or > MaxColorCacheBits)
            {
                throw new ImageFormatException("Invalid VP8L color cache size.");
            }
        }

        // Meta prefix codes: an entropy image selects the group of five codes for each block of pixels.
        uint[]? groupImage = null;
        var groupBits = 0;
        var groupCount = 1;
        if (allowMetaCodes && _br.ReadBits(1) != 0)
        {
            groupBits = (int)_br.ReadBits(3) + 2;
            groupImage = DecodeImageStream(SubSampleSize(xsize, groupBits), SubSampleSize(ysize, groupBits));
            for (var i = 0; i < groupImage.Length; i++)
            {
                groupImage[i] = (groupImage[i] >> 8) & 0xFFFF;
                groupCount = Math.Max(groupCount, (int)groupImage[i] + 1);
            }
        }

        CheckEnd();
        var used = new bool[groupCount];
        if (groupImage is null)
        {
            used[0] = true;
        }
        else
        {
            foreach (var g in groupImage)
            {
                used[g] = true;
            }
        }

        // Codes of groups no block uses must still be read (and be valid), but need not be kept.
        var groups = new HuffmanCode[groupCount][];
        for (var g = 0; g < groupCount; g++)
        {
            var codes = new HuffmanCode[5];
            for (var j = 0; j < 5; j++)
            {
                var alphabetSize = j switch
                {
                    Green => NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0),
                    Distance => NumDistanceCodes,
                    _ => NumLiteralCodes,
                };
                codes[j] = ReadHuffmanCode(alphabetSize);
            }

            groups[g] = used[g] ? codes : [];
        }

        var pixels = DecodePixels(xsize, ysize, groups, groupImage, groupBits, cacheBits);
        CheckEnd();
        return pixels;
    }

    private uint[] DecodePixels(int width, int height, HuffmanCode[][] groups, uint[]? groupImage, int groupBits, int cacheBits)
    {
        var pixels = new uint[(long)width * height];
        var cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        var groupWidth = SubSampleSize(width, groupBits);
        var end = pixels.Length;
        int pos = 0, x = 0, y = 0;
        var br = _br;
        while (pos < end)
        {
            var codes = groupImage is null ? groups[0] : groups[groupImage[((y >> groupBits) * groupWidth) + (x >> groupBits)]];
            var code = codes[Green].Decode(br);
            if (code < NumLiteralCodes)
            {
                var red = codes[Red].Decode(br);
                var blue = codes[Blue].Decode(br);
                var alpha = codes[Alpha].Decode(br);
                pixels[pos] = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;
                Insert(cache, cacheBits, pixels[pos]);
                pos++;
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else if (code < NumLiteralCodes + NumLengthCodes)
            {
                var length = CopyDistance(code - NumLiteralCodes);
                var distanceSymbol = codes[Distance].Decode(br);
                var distance = PlaneCodeToDistance(width, CopyDistance(distanceSymbol));
                if (br.Eos)
                {
                    break;
                }

                if (pos < distance || end - pos < length)
                {
                    throw new ImageFormatException("Invalid VP8L backward reference.");
                }

                for (var i = 0; i < length; i++, pos++)
                {
                    pixels[pos] = pixels[pos - distance];
                    Insert(cache, cacheBits, pixels[pos]);
                }

                x += length;
                while (x >= width)
                {
                    x -= width;
                    y++;
                }
            }
            else
            {
                var key = code - NumLiteralCodes - NumLengthCodes;
                if (cache is null || key >= cache.Length)
                {
                    throw new ImageFormatException("Invalid VP8L color cache code.");
                }

                pixels[pos] = cache[key];
                Insert(cache, cacheBits, pixels[pos]);
                pos++;
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }

            if (br.Eos)
            {
                break;
            }
        }

        CheckEnd();
        return pixels;
    }

    private static void Insert(uint[]? cache, int bits, uint argb)
    {
        if (cache is not null)
        {
            cache[(int)((0x1E35A7BDu * argb) >> (32 - bits))] = argb;
        }
    }

    /// <summary>Prefix-coded length or distance: values 1-4 directly, then a power-of-two range with extra bits.</summary>
    private int CopyDistance(int symbol)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        var extraBits = (symbol - 2) >> 1;
        var offset = (2 + (symbol & 1)) << extraBits;
        return offset + (int)_br.ReadBits(extraBits) + 1;
    }

    /// <summary>Maps a distance code to a distance in pixels: the first 120 codes are a 2D neighborhood.</summary>
    private static int PlaneCodeToDistance(int width, int planeCode)
    {
        if (planeCode > 120)
        {
            return planeCode - 120;
        }

        ReadOnlySpan<byte> codeToPlane =
        [
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1A, 0x26, 0x2A, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1B, 0x36, 0x3A,
            0x25, 0x2B, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1C, 0x35, 0x3B, 0x46, 0x4A, 0x24, 0x2C, 0x58, 0x45, 0x4B, 0x34, 0x3C, 0x03,
            0x57, 0x59, 0x13, 0x1D, 0x56, 0x5A, 0x23, 0x2D, 0x44, 0x4C, 0x55, 0x5B, 0x33, 0x3D, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1E,
            0x66, 0x6A, 0x22, 0x2E, 0x54, 0x5C, 0x43, 0x4D, 0x65, 0x6B, 0x32, 0x3E, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5D, 0x11, 0x1F,
            0x64, 0x6C, 0x42, 0x4E, 0x76, 0x7A, 0x21, 0x2F, 0x75, 0x7B, 0x31, 0x3F, 0x63, 0x6D, 0x52, 0x5E, 0x00, 0x74, 0x7C, 0x41,
            0x4F, 0x10, 0x20, 0x62, 0x6E, 0x30, 0x73, 0x7D, 0x51, 0x5F, 0x40, 0x72, 0x7E, 0x61, 0x6F, 0x50, 0x71, 0x7F, 0x60, 0x70,
        ];
        int code = codeToPlane[planeCode - 1];
        var distance = ((code >> 4) * width) + (8 - (code & 0xF));
        return Math.Max(distance, 1);
    }

    private HuffmanCode ReadHuffmanCode(int alphabetSize)
    {
        var lengths = new int[alphabetSize];
        if (_br.ReadBits(1) != 0)
        {
            // Simple code: one or two symbols of length 1 (a single symbol takes no bits). Like libwebp, a symbol
            // outside the alphabet is ignored.
            var count = (int)_br.ReadBits(1) + 1;
            var first = (int)_br.ReadBits(_br.ReadBits(1) == 0 ? 1 : 8);
            if (first < alphabetSize)
            {
                lengths[first] = 1;
            }

            if (count == 2)
            {
                var second = (int)_br.ReadBits(8);
                if (second < alphabetSize)
                {
                    lengths[second] = 1;
                }
            }
        }
        else
        {
            // Normal code: the code lengths are themselves prefix-coded, with run-length codes 16-18.
            ReadOnlySpan<byte> order = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
            var codeLengthLengths = new int[19];
            var codes = (int)_br.ReadBits(4) + 4;
            for (var i = 0; i < codes; i++)
            {
                codeLengthLengths[order[i]] = (int)_br.ReadBits(3);
            }

            var lengthCode = HuffmanCode.Build(codeLengthLengths);
            var maxSymbol = alphabetSize;
            if (_br.ReadBits(1) != 0)
            {
                var lengthBits = 2 + (2 * (int)_br.ReadBits(3));
                maxSymbol = 2 + (int)_br.ReadBits(lengthBits);
                if (maxSymbol > alphabetSize)
                {
                    throw new ImageFormatException("Invalid VP8L prefix code.");
                }
            }

            var previous = 8;
            var symbol = 0;
            while (symbol < alphabetSize && maxSymbol-- > 0)
            {
                var length = lengthCode.Decode(_br);
                if (length < 16)
                {
                    lengths[symbol++] = length;
                    if (length != 0)
                    {
                        previous = length;
                    }

                    continue;
                }

                var repeat = length switch
                {
                    16 => (int)_br.ReadBits(2) + 3,
                    17 => (int)_br.ReadBits(3) + 3,
                    _ => (int)_br.ReadBits(7) + 11,
                };
                if (symbol + repeat > alphabetSize)
                {
                    throw new ImageFormatException("Invalid VP8L prefix code.");
                }

                var value = length == 16 ? previous : 0;
                lengths.AsSpan(symbol, repeat).Fill(value);
                symbol += repeat;
            }
        }

        CheckEnd();
        return HuffmanCode.Build(lengths);
    }

    private void CheckEnd()
    {
        if (_br.Eos)
        {
            throw new ImageFormatException("Truncated VP8L data.");
        }
    }

    private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private static uint AddPixels(uint a, uint b) =>
        (((a & 0xFF00FF00) + (b & 0xFF00FF00)) & 0xFF00FF00) | (((a & 0x00FF00FF) + (b & 0x00FF00FF)) & 0x00FF00FF);

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    private static void InversePredictor(uint[] pixels, int width, int height, int bits, uint[] modes)
    {
        // First row: black for the first pixel, then the left pixel. First column: the pixel above.
        pixels[0] = AddPixels(pixels[0], 0xFF000000);
        for (var x = 1; x < width; x++)
        {
            pixels[x] = AddPixels(pixels[x], pixels[x - 1]);
        }

        var tilesPerRow = SubSampleSize(width, bits);
        for (var y = 1; y < height; y++)
        {
            var row = y * width;
            pixels[row] = AddPixels(pixels[row], pixels[row - width]);
            var modeRow = (y >> bits) * tilesPerRow;
            for (var x = 1; x < width; x++)
            {
                var mode = (int)((modes[modeRow + (x >> bits)] >> 8) & 0xF);
                var i = row + x;

                // The top-right pixel of the last column is the first pixel of the current row, as in libwebp.
                pixels[i] = AddPixels(pixels[i], Predict(mode, pixels[i - 1], pixels[i - width], pixels[i - width + 1], pixels[i - width - 1]));
            }
        }
    }

    private static uint Predict(int mode, uint left, uint top, uint topRight, uint topLeft) => mode switch
    {
        0 => 0xFF000000,
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(top, left, topLeft),
        12 => ClampedAddSubtractFull(left, top, topLeft),
        13 => ClampedAddSubtractHalf(left, top, topLeft),
        _ => 0xFF000000, // Modes 14 and 15 are unused; libwebp predicts black.
    };

    private static uint Select(uint a, uint b, uint c)
    {
        static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);
        var paMinusPb = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            paMinusPb += Sub3((int)((a >> shift) & 0xFF), (int)((b >> shift) & 0xFF), (int)((c >> shift) & 0xFF));
        }

        return paMinusPb <= 0 ? a : b;
    }

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var v = (int)((c0 >> shift) & 0xFF) + (int)((c1 >> shift) & 0xFF) - (int)((c2 >> shift) & 0xFF);
            result |= (uint)Math.Clamp(v, 0, 255) << shift;
        }

        return result;
    }

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        var average = Average2(c0, c1);
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            var a = (int)((average >> shift) & 0xFF);
            var b = (int)((c2 >> shift) & 0xFF);
            result |= (uint)Math.Clamp(a + ((a - b) / 2), 0, 255) << shift;
        }

        return result;
    }

    private static void InverseCrossColor(uint[] pixels, int width, int height, int bits, uint[] multipliers)
    {
        static int Delta(sbyte predictor, sbyte color) => (predictor * color) >> 5;
        var tilesPerRow = SubSampleSize(width, bits);
        for (var y = 0; y < height; y++)
        {
            var modeRow = (y >> bits) * tilesPerRow;
            for (var x = 0; x < width; x++)
            {
                var m = multipliers[modeRow + (x >> bits)];
                var greenToRed = (sbyte)(m & 0xFF);
                var greenToBlue = (sbyte)((m >> 8) & 0xFF);
                var redToBlue = (sbyte)((m >> 16) & 0xFF);
                var i = (y * width) + x;
                var argb = pixels[i];
                var green = (sbyte)(argb >> 8);
                var red = (int)((argb >> 16) & 0xFF);
                var blue = (int)(argb & 0xFF);
                red = (red + Delta(greenToRed, green)) & 0xFF;
                blue = (blue + Delta(greenToBlue, green) + Delta(redToBlue, (sbyte)red)) & 0xFF;
                pixels[i] = (argb & 0xFF00FF00) | ((uint)red << 16) | (uint)blue;
            }
        }
    }

    private static uint[] InverseColorIndexing(uint[] packed, int width, int height, int bits, uint[] map)
    {
        var output = new uint[(long)width * height];
        var packedWidth = SubSampleSize(width, bits);
        var bitsPerPixel = 8 >> bits;
        var mask = (1u << bitsPerPixel) - 1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (packed[(y * packedWidth) + (x >> bits)] >> 8) & 0xFF;
                index = (index >> ((x & ((1 << bits) - 1)) * bitsPerPixel)) & mask;
                output[(y * width) + x] = map[index];
            }
        }

        return output;
    }

    /// <summary>A canonical prefix code: an 8-bit lookup table, then bit-by-bit decoding of longer codes.</summary>
    private sealed class HuffmanCode
    {
        private const int TableBits = 8;

        private readonly int[] _counts = new int[16];
        private readonly int[] _symbols;
        private readonly short[] _table = new short[1 << TableBits]; // Symbol << 4 | length; 0 = longer code.
        private readonly int _single = -1;

        private HuffmanCode(int[] lengths)
        {
            var used = 0;
            foreach (var length in lengths)
            {
                if (length > 15)
                {
                    throw new ImageFormatException("Invalid VP8L prefix code.");
                }

                _counts[length]++;
                used += length > 0 ? 1 : 0;
            }

            _counts[0] = 0;
            _symbols = new int[used];
            if (used == 0)
            {
                throw new ImageFormatException("Invalid VP8L prefix code (no symbols).");
            }

            var offsets = new int[16];
            for (var len = 1; len < 15; len++)
            {
                offsets[len + 1] = offsets[len] + _counts[len];
            }

            for (var s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] > 0)
                {
                    _symbols[offsets[lengths[s]]++] = s;
                }
            }

            if (used == 1)
            {
                _single = _symbols[0]; // A lone symbol is coded with zero bits, whatever its length.
                return;
            }

            // The code must be complete: neither over-subscribed nor with unused branches.
            var left = 1;
            for (var len = 1; len <= 15; len++)
            {
                left = (left << 1) - _counts[len];
                if (left < 0)
                {
                    throw new ImageFormatException("Invalid VP8L prefix code (over-subscribed).");
                }
            }

            if (left != 0)
            {
                throw new ImageFormatException("Invalid VP8L prefix code (incomplete).");
            }

            // Fill the lookup table with the codes of up to 8 bits, indexed by their bits in stream (reversed) order.
            var code = 0;
            var index = 0;
            for (var len = 1; len <= TableBits; len++)
            {
                for (var i = 0; i < _counts[len]; i++, code++, index++)
                {
                    var reversed = 0;
                    for (var b = 0; b < len; b++)
                    {
                        reversed |= ((code >> b) & 1) << (len - 1 - b);
                    }

                    for (var fill = reversed; fill < _table.Length; fill += 1 << len)
                    {
                        _table[fill] = (short)((_symbols[index] << 4) | len);
                    }
                }

                code <<= 1;
            }
        }

        public static HuffmanCode Build(int[] lengths) => new(lengths);

        public int Decode(BitReader br)
        {
            if (_single >= 0)
            {
                return _single;
            }

            int entry = _table[br.PeekBits(TableBits)];
            if (entry != 0)
            {
                br.Skip(entry & 0xF);
                return entry >> 4;
            }

            // Canonical decoding, one bit at a time, starting with the first (most significant) code bit.
            int code = 0, first = 0, index = 0;
            for (var len = 1; len <= 15; len++)
            {
                code |= (int)br.ReadBits(1);
                var count = _counts[len];
                if (code - first < count)
                {
                    return _symbols[index + (code - first)];
                }

                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }

            return 0; // Not reached for a complete code.
        }
    }

    /// <summary>Reads bits least significant first. Reading past the end yields zeros and sets <see cref="Eos"/>.</summary>
    private sealed class BitReader(byte[] data, int offset, int length)
    {
        private readonly int _end = offset + length;
        private int _pos = offset;
        private ulong _buffer;
        private int _count;

        public bool Eos { get; private set; }

        public uint ReadBits(int bits)
        {
            var value = PeekBits(bits);
            Skip(bits);
            return value;
        }

        public uint PeekBits(int bits)
        {
            if (_count < bits)
            {
                Fill();
            }

            return (uint)(_buffer & ((1UL << bits) - 1));
        }

        public void Skip(int bits)
        {
            if (_count < bits)
            {
                Fill();
                if (_count < bits)
                {
                    Eos = true;
                    _buffer = 0;
                    _count = 0;
                    return;
                }
            }

            _buffer >>= bits;
            _count -= bits;
        }

        private void Fill()
        {
            while (_count <= 56 && _pos < _end)
            {
                _buffer |= (ulong)data[_pos++] << _count;
                _count += 8;
            }
        }
    }
}
