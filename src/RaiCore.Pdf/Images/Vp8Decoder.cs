using System.Numerics;

namespace RaiCore.Pdf.Images;

/// <summary>
/// Decodes a VP8 key frame (RFC 6386), the image data of lossy WebP, to 8-bit RGB: the boolean entropy decoder,
/// intra prediction, the inverse DCT and Walsh-Hadamard transforms, the normal and simple loop filters and the
/// YUV to RGB conversion. The output matches libwebp's exactly, including its "fancy" chroma upsampling.
/// </summary>
internal sealed class Vp8Decoder
{
    // The work area for one macroblock: a 16x16 luma block and two 8x8 chroma blocks, each with the row above and
    // the columns to the left that prediction reads, at a stride of 32 bytes (the layout libwebp uses).
    private const int Bps = 32;
    private const int YOffset = Bps + 8;
    private const int UOffset = YOffset + (Bps * 16) + Bps;
    private const int VOffset = UOffset + 16;
    private const int WorkSize = (Bps * 17) + (Bps * 9);

    // Prediction modes, in libwebp's order. The 16x16 and chroma modes share the first four numbers.
    private const int DcPred = 0, TmPred = 1, VPred = 2, HPred = 3;
    private const int BRdPred = 4, BVrPred = 5, BLdPred = 6, BVlPred = 7, BHdPred = 8, BHuPred = 9;
    private const int DcPredNoTop = 4, DcPredNoLeft = 5, DcPredNoTopLeft = 6;

    private const int NumBands = 8, NumContexts = 3, NumProbas = 11;

    private readonly byte[] _data;
    private readonly BoolDecoder _header;
    private readonly int _width, _height, _mbWidth, _mbHeight;

    private bool _useSegment, _updateMap, _absoluteDelta = true;
    private readonly int[] _segmentQuantizer = new int[4], _segmentFilterLevel = new int[4];
    private readonly int[] _segmentProbas = [255, 255, 255];

    private bool _simpleFilter, _useLfDelta;
    private int _filterLevel, _sharpness, _filterType;
    private readonly int[] _refLfDelta = new int[4], _modeLfDelta = new int[4];

    // Dequantization factors per segment: luma DC/AC, Y2 DC/AC, chroma DC/AC.
    private readonly int[,] _dq = new int[4, 6];
    private readonly byte[] _probas = new byte[4 * NumBands * NumContexts * NumProbas];
    private bool _useSkipProba;
    private int _skipProba;
    private BoolDecoder[] _partitions = [];

    private Vp8Decoder(byte[] data, int offset, int length)
    {
        _data = data;
        if (length < 10)
        {
            throw new ImageFormatException("Truncated VP8 frame header.");
        }

        var bits = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        var keyFrame = (bits & 1) == 0;
        var profile = (bits >> 1) & 7;
        var show = (bits >> 4) & 1;
        var partitionLength = bits >> 5;
        if (!keyFrame)
        {
            throw new ImageFormatException("The VP8 data is not a key frame.");
        }

        if (profile > 3 || show == 0)
        {
            throw new ImageFormatException("Invalid VP8 frame header.");
        }

        if (data[offset + 3] != 0x9D || data[offset + 4] != 0x01 || data[offset + 5] != 0x2A)
        {
            throw new ImageFormatException("Invalid VP8 start code.");
        }

        _width = (data[offset + 6] | (data[offset + 7] << 8)) & 0x3FFF;
        _height = (data[offset + 8] | (data[offset + 9] << 8)) & 0x3FFF;
        if (_width == 0 || _height == 0)
        {
            throw new ImageFormatException("VP8 image has a zero size.");
        }

        ResourceLimits.CheckImagePixels(_width, _height);

        _mbWidth = (_width + 15) >> 4;
        _mbHeight = (_height + 15) >> 4;
        var start = offset + 10;
        var size = length - 10;
        if (partitionLength > size)
        {
            throw new ImageFormatException("Truncated VP8 data.");
        }

        _header = new BoolDecoder(data, start, partitionLength);
        ParseHeader(start + partitionLength, size - partitionLength);
    }

    /// <summary>Decodes the VP8 key frame in <paramref name="data"/> to interleaved RGB samples.</summary>
    public static byte[] Decode(byte[] data, int offset, int length, out int width, out int height)
    {
        var decoder = new Vp8Decoder(data, offset, length);
        width = decoder._width;
        height = decoder._height;
        return decoder.DecodeFrame();
    }

    private void ParseHeader(int partitionsStart, int partitionsSize)
    {
        var br = _header;
        br.GetValue(1); // Color space (always YUV).
        br.GetValue(1); // Clamping type (the decoder always clamps).

        // Segments.
        _useSegment = br.GetValue(1) != 0;
        if (_useSegment)
        {
            _updateMap = br.GetValue(1) != 0;
            if (br.GetValue(1) != 0)
            {
                _absoluteDelta = br.GetValue(1) != 0;
                for (var s = 0; s < 4; s++)
                {
                    _segmentQuantizer[s] = br.GetValue(1) != 0 ? br.GetSignedValue(7) : 0;
                }

                for (var s = 0; s < 4; s++)
                {
                    _segmentFilterLevel[s] = br.GetValue(1) != 0 ? br.GetSignedValue(6) : 0;
                }
            }

            if (_updateMap)
            {
                for (var s = 0; s < 3; s++)
                {
                    _segmentProbas[s] = br.GetValue(1) != 0 ? br.GetValue(8) : 255;
                }
            }
        }

        // Loop filter.
        _simpleFilter = br.GetValue(1) != 0;
        _filterLevel = br.GetValue(6);
        _sharpness = br.GetValue(3);
        _useLfDelta = br.GetValue(1) != 0;
        if (_useLfDelta && br.GetValue(1) != 0)
        {
            for (var i = 0; i < 4; i++)
            {
                if (br.GetValue(1) != 0)
                {
                    _refLfDelta[i] = br.GetSignedValue(6);
                }
            }

            for (var i = 0; i < 4; i++)
            {
                if (br.GetValue(1) != 0)
                {
                    _modeLfDelta[i] = br.GetSignedValue(6);
                }
            }
        }

        _filterType = _filterLevel == 0 ? 0 : _simpleFilter ? 1 : 2;
        if (br.Eof)
        {
            throw new ImageFormatException("Truncated VP8 frame header.");
        }

        // Token partitions: the sizes of all but the last precede them, as 24-bit little-endian numbers.
        var count = 1 << br.GetValue(2);
        var last = count - 1;
        if (partitionsSize < 3 * last)
        {
            throw new ImageFormatException("Truncated VP8 partitions.");
        }

        var end = partitionsStart + partitionsSize;
        var partStart = partitionsStart + (3 * last);
        var sizeLeft = partitionsSize - (3 * last);
        _partitions = new BoolDecoder[count];
        for (var p = 0; p < last; p++)
        {
            var sizeAt = partitionsStart + (3 * p);
            var partSize = _data[sizeAt] | (_data[sizeAt + 1] << 8) | (_data[sizeAt + 2] << 16);
            partSize = Math.Min(partSize, sizeLeft);
            _partitions[p] = new BoolDecoder(_data, partStart, partSize);
            partStart += partSize;
            sizeLeft -= partSize;
        }

        _partitions[last] = new BoolDecoder(_data, partStart, sizeLeft);
        if (partStart >= end)
        {
            throw new ImageFormatException("Truncated VP8 partitions.");
        }

        ParseQuantizers();
        br.GetValue(1); // Whether to keep the probabilities; irrelevant for a single frame.
        ParseProbabilities();
    }

    private void ParseQuantizers()
    {
        var br = _header;
        var baseQ = br.GetValue(7);
        var dqY1Dc = br.GetValue(1) != 0 ? br.GetSignedValue(4) : 0;
        var dqY2Dc = br.GetValue(1) != 0 ? br.GetSignedValue(4) : 0;
        var dqY2Ac = br.GetValue(1) != 0 ? br.GetSignedValue(4) : 0;
        var dqUvDc = br.GetValue(1) != 0 ? br.GetSignedValue(4) : 0;
        var dqUvAc = br.GetValue(1) != 0 ? br.GetSignedValue(4) : 0;
        for (var s = 0; s < 4; s++)
        {
            int q;
            if (_useSegment)
            {
                q = _segmentQuantizer[s] + (_absoluteDelta ? 0 : baseQ);
            }
            else if (s > 0)
            {
                for (var i = 0; i < 6; i++)
                {
                    _dq[s, i] = _dq[0, i];
                }

                continue;
            }
            else
            {
                q = baseQ;
            }

            _dq[s, 0] = Vp8Tables.DcTable[Math.Clamp(q + dqY1Dc, 0, 127)];
            _dq[s, 1] = Vp8Tables.AcTable[Math.Clamp(q, 0, 127)];
            _dq[s, 2] = Vp8Tables.DcTable[Math.Clamp(q + dqY2Dc, 0, 127)] * 2;
            _dq[s, 3] = Math.Max(8, (Vp8Tables.AcTable[Math.Clamp(q + dqY2Ac, 0, 127)] * 101581) >> 16); // x * 155 / 100
            _dq[s, 4] = Vp8Tables.DcTable[Math.Clamp(q + dqUvDc, 0, 117)];
            _dq[s, 5] = Vp8Tables.AcTable[Math.Clamp(q + dqUvAc, 0, 127)];
        }
    }

    private void ParseProbabilities()
    {
        var br = _header;
        var update = Vp8Tables.CoeffsUpdateProba;
        var defaults = Vp8Tables.CoeffsProba0;
        for (var i = 0; i < _probas.Length; i++)
        {
            _probas[i] = br.GetBit(update[i]) != 0 ? (byte)br.GetValue(8) : defaults[i];
        }

        _useSkipProba = br.GetValue(1) != 0;
        if (_useSkipProba)
        {
            _skipProba = br.GetValue(8);
        }
    }

    private byte[] DecodeFrame()
    {
        var mbWidth = _mbWidth;
        var yStride = mbWidth * 16;
        var uvStride = mbWidth * 8;
        var yPlane = new byte[(long)yStride * _mbHeight * 16];
        var uPlane = new byte[(long)uvStride * _mbHeight * 8];
        var vPlane = new byte[(long)uvStride * _mbHeight * 8];

        // Per-row parsing state: prediction modes and the non-zero contexts of the blocks above and to the left.
        var intraTop = new byte[4 * mbWidth];
        var intraLeft = new byte[4];
        var isI4x4 = new bool[mbWidth];
        var modes = new byte[16 * mbWidth];
        var uvModes = new byte[mbWidth];
        var segments = new byte[mbWidth];
        var skips = new bool[mbWidth];
        var nzTop = new uint[mbWidth];
        var nzDcTop = new uint[mbWidth];
        var coeffs = new short[384 * mbWidth];

        // Per-macroblock loop filter parameters, applied once the whole frame is reconstructed.
        var filterLimit = new byte[mbWidth * _mbHeight];
        var filterInnerLevel = new byte[mbWidth * _mbHeight];
        var filterInner = new bool[mbWidth * _mbHeight];
        var filterHev = new byte[mbWidth * _mbHeight];
        var strengths = ComputeFilterStrengths();

        var work = new byte[WorkSize];
        var topY = new byte[yStride];
        var topU = new byte[uvStride];
        var topV = new byte[uvStride];

        for (var mbY = 0; mbY < _mbHeight; mbY++)
        {
            var tokens = _partitions[mbY & (_partitions.Length - 1)];
            intraLeft.AsSpan().Clear();
            for (var mbX = 0; mbX < mbWidth; mbX++)
            {
                ParseIntraMode(mbX, intraTop, intraLeft, isI4x4, modes, uvModes, segments, skips);
            }

            if (_header.Eof)
            {
                throw new ImageFormatException("Truncated VP8 data (first partition).");
            }

            uint nzLeft = 0, nzDcLeft = 0;
            coeffs.AsSpan().Clear();
            for (var mbX = 0; mbX < mbWidth; mbX++)
            {
                var block = coeffs.AsSpan(384 * mbX, 384);
                bool hasCoefficients;
                if (_useSkipProba && skips[mbX])
                {
                    nzLeft = nzTop[mbX] = 0;
                    if (!isI4x4[mbX])
                    {
                        nzDcLeft = nzDcTop[mbX] = 0;
                    }

                    hasCoefficients = false;
                }
                else
                {
                    hasCoefficients = ParseResiduals(tokens, block, isI4x4[mbX], segments[mbX], ref nzTop[mbX], ref nzDcTop[mbX], ref nzLeft, ref nzDcLeft);
                }

                if (tokens.Eof)
                {
                    throw new ImageFormatException("Truncated VP8 data.");
                }

                if (_filterType > 0)
                {
                    var index = (mbY * mbWidth) + mbX;
                    var strength = strengths[segments[mbX], isI4x4[mbX] ? 1 : 0];
                    filterLimit[index] = strength.Limit;
                    filterInnerLevel[index] = strength.InnerLevel;
                    filterHev[index] = strength.HevThreshold;
                    filterInner[index] = isI4x4[mbX] || hasCoefficients;
                }
            }

            ReconstructRow(mbY, work, topY, topU, topV, isI4x4, modes, uvModes, coeffs, yPlane, uPlane, vPlane);
        }

        if (_filterType > 0)
        {
            for (var mbY = 0; mbY < _mbHeight; mbY++)
            {
                for (var mbX = 0; mbX < mbWidth; mbX++)
                {
                    var index = (mbY * mbWidth) + mbX;
                    FilterMacroblock(mbX, mbY, filterLimit[index], filterInnerLevel[index], filterHev[index], filterInner[index], yPlane, uPlane, vPlane, yStride, uvStride);
                }
            }
        }

        return ToRgb(yPlane, uPlane, vPlane, yStride, uvStride);
    }

    private void ParseIntraMode(int mbX, byte[] intraTop, byte[] intraLeft, bool[] isI4x4, byte[] modes, byte[] uvModes, byte[] segments, bool[] skips)
    {
        var br = _header;
        segments[mbX] = !_updateMap ? (byte)0
            : br.GetBit(_segmentProbas[0]) == 0 ? (byte)br.GetBit(_segmentProbas[1])
            : (byte)(br.GetBit(_segmentProbas[2]) + 2);
        if (_useSkipProba)
        {
            skips[mbX] = br.GetBit(_skipProba) != 0;
        }

        var top = intraTop.AsSpan(4 * mbX, 4);
        var i4x4 = br.GetBit(145) == 0;
        isI4x4[mbX] = i4x4;
        if (!i4x4)
        {
            var mode = br.GetBit(156) != 0
                ? (br.GetBit(128) != 0 ? TmPred : HPred)
                : (br.GetBit(163) != 0 ? VPred : DcPred);
            modes[16 * mbX] = (byte)mode;
            top.Fill((byte)mode);
            intraLeft.AsSpan().Fill((byte)mode);
        }
        else
        {
            var probas = Vp8Tables.BModesProba;
            for (var y = 0; y < 4; y++)
            {
                int mode = intraLeft[y];
                for (var x = 0; x < 4; x++)
                {
                    var p = probas.Slice(((top[x] * 10) + mode) * 9, 9);
                    mode = br.GetBit(p[0]) == 0 ? DcPred
                        : br.GetBit(p[1]) == 0 ? TmPred
                        : br.GetBit(p[2]) == 0 ? VPred
                        : br.GetBit(p[3]) == 0
                            ? (br.GetBit(p[4]) == 0 ? HPred : br.GetBit(p[5]) == 0 ? BRdPred : BVrPred)
                            : (br.GetBit(p[6]) == 0 ? BLdPred : br.GetBit(p[7]) == 0 ? BVlPred : br.GetBit(p[8]) == 0 ? BHdPred : BHuPred);
                    top[x] = (byte)mode;
                }

                top.CopyTo(modes.AsSpan((16 * mbX) + (4 * y), 4));
                intraLeft[y] = (byte)mode;
            }
        }

        uvModes[mbX] = br.GetBit(142) == 0 ? (byte)DcPred
            : br.GetBit(114) == 0 ? (byte)VPred
            : br.GetBit(183) != 0 ? (byte)TmPred : (byte)HPred;
    }

    /// <summary>
    /// Reads the coefficients of one macroblock, dequantized, into 24 blocks of 16 (16 luma, 4 + 4 chroma). Returns
    /// whether any is non-zero. The context bits record which blocks had non-zero coefficients, as in libwebp.
    /// </summary>
    private bool ParseResiduals(BoolDecoder br, Span<short> dst, bool i4x4, int segment, ref uint nzTop, ref uint nzDcTop, ref uint nzLeft, ref uint nzDcLeft)
    {
        int first, acType;
        if (!i4x4)
        {
            Span<short> dc = stackalloc short[16];
            dc.Clear();
            var ctx = (int)(nzDcTop + nzDcLeft);
            var nz = GetCoeffs(br, 1, ctx, _dq[segment, 2], _dq[segment, 3], 0, dc);
            nzDcTop = nzDcLeft = nz > 0 ? 1u : 0u;
            if (nz > 1)
            {
                TransformWht(dc, dst);
            }
            else
            {
                var dc0 = (short)((dc[0] + 3) >> 3);
                for (var i = 0; i < 256; i += 16)
                {
                    dst[i] = dc0;
                }
            }

            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3;
        }

        var any = false;
        var tnz = nzTop & 0x0F;
        var lnz = nzLeft & 0x0F;
        for (var y = 0; y < 4; y++)
        {
            var l = lnz & 1;
            for (var x = 0; x < 4; x++)
            {
                var ctx = (int)(l + (tnz & 1));
                var block = dst.Slice(((y * 4) + x) * 16, 16);
                var nz = GetCoeffs(br, acType, ctx, _dq[segment, 0], _dq[segment, 1], first, block);
                l = nz > first ? 1u : 0u;
                tnz = (tnz >> 1) | (l << 7);
                any |= nz > 1 || block[0] != 0;
            }

            tnz >>= 4;
            lnz = (lnz >> 1) | (l << 7);
        }

        var outTop = tnz;
        var outLeft = lnz >> 4;
        for (var ch = 0; ch < 4; ch += 2)
        {
            tnz = nzTop >> (4 + ch);
            lnz = nzLeft >> (4 + ch);
            for (var y = 0; y < 2; y++)
            {
                var l = lnz & 1;
                for (var x = 0; x < 2; x++)
                {
                    var ctx = (int)(l + (tnz & 1));
                    var block = dst.Slice((16 + (ch * 2) + (y * 2) + x) * 16, 16);
                    var nz = GetCoeffs(br, 2, ctx, _dq[segment, 4], _dq[segment, 5], 0, block);
                    l = nz > 0 ? 1u : 0u;
                    tnz = (tnz >> 1) | (l << 3);
                    any |= nz > 0;
                }

                tnz >>= 2;
                lnz = (lnz >> 1) | (l << 5);
            }

            outTop |= (tnz << 4) << ch;
            outLeft |= (lnz & 0xF0) << ch;
        }

        nzTop = outTop;
        nzLeft = outLeft;
        return any;
    }

    /// <summary>Reads the tokens of one 4x4 block from position <paramref name="n"/>; returns the index after the last non-zero one.</summary>
    private int GetCoeffs(BoolDecoder br, int type, int ctx, int dcFactor, int acFactor, int n, Span<short> output)
    {
        ReadOnlySpan<byte> bands = [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];
        ReadOnlySpan<byte> zigzag = [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
        var probas = _probas.AsSpan(type * NumBands * NumContexts * NumProbas, NumBands * NumContexts * NumProbas);
        var p = probas.Slice(((bands[n] * NumContexts) + ctx) * NumProbas, NumProbas);
        for (; n < 16; n++)
        {
            if (br.GetBit(p[0]) == 0)
            {
                return n; // End of block.
            }

            while (br.GetBit(p[1]) == 0)
            {
                // A zero coefficient.
                if (++n == 16)
                {
                    return 16;
                }

                p = probas.Slice(bands[n] * NumContexts * NumProbas, NumProbas);
            }

            var next = probas.Slice(bands[n + 1] * NumContexts * NumProbas, NumContexts * NumProbas);
            int v;
            if (br.GetBit(p[2]) == 0)
            {
                v = 1;
                p = next.Slice(NumProbas, NumProbas);
            }
            else
            {
                v = GetLargeValue(br, p);
                p = next.Slice(2 * NumProbas, NumProbas);
            }

            output[zigzag[n]] = (short)(br.GetSigned(v) * (n > 0 ? acFactor : dcFactor));
        }

        return 16;
    }

    private static int GetLargeValue(BoolDecoder br, ReadOnlySpan<byte> p)
    {
        if (br.GetBit(p[3]) == 0)
        {
            return br.GetBit(p[4]) == 0 ? 2 : 3 + br.GetBit(p[5]);
        }

        if (br.GetBit(p[6]) == 0)
        {
            if (br.GetBit(p[7]) == 0)
            {
                return 5 + br.GetBit(159);
            }

            var v = 7 + (2 * br.GetBit(165));
            return v + br.GetBit(145);
        }

        // DCT_CAT3 to DCT_CAT6: extra bits, most significant first, each with its own probability.
        ReadOnlySpan<byte> cat3 = [173, 148, 140];
        ReadOnlySpan<byte> cat4 = [176, 155, 140, 135];
        ReadOnlySpan<byte> cat5 = [180, 157, 141, 134, 130];
        ReadOnlySpan<byte> cat6 = [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];
        var bit1 = br.GetBit(p[8]);
        var bit0 = br.GetBit(p[9 + bit1]);
        var cat = (2 * bit1) + bit0;
        var table = cat switch { 0 => cat3, 1 => cat4, 2 => cat5, _ => cat6 };
        var value = 0;
        foreach (var prob in table)
        {
            value += value + br.GetBit(prob);
        }

        return value + 3 + (8 << cat);
    }

    private readonly record struct FilterStrength(byte Limit, byte InnerLevel, byte HevThreshold);

    private FilterStrength[,] ComputeFilterStrengths()
    {
        var strengths = new FilterStrength[4, 2];
        if (_filterType == 0)
        {
            return strengths;
        }

        for (var s = 0; s < 4; s++)
        {
            var baseLevel = _useSegment ? _segmentFilterLevel[s] + (_absoluteDelta ? 0 : _filterLevel) : _filterLevel;
            for (var i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                var level = baseLevel;
                if (_useLfDelta)
                {
                    level += _refLfDelta[0];
                    if (i4x4 == 1)
                    {
                        level += _modeLfDelta[0];
                    }
                }

                level = Math.Clamp(level, 0, 63);
                if (level == 0)
                {
                    continue; // Limit 0: no filtering.
                }

                var innerLevel = level;
                if (_sharpness > 0)
                {
                    innerLevel >>= _sharpness > 4 ? 2 : 1;
                    innerLevel = Math.Min(innerLevel, 9 - _sharpness);
                }

                innerLevel = Math.Max(innerLevel, 1);
                strengths[s, i4x4] = new FilterStrength(
                    (byte)((2 * level) + innerLevel), (byte)innerLevel, (byte)(level >= 40 ? 2 : level >= 15 ? 1 : 0));
            }
        }

        return strengths;
    }

    private void ReconstructRow(int mbY, byte[] work, byte[] topY, byte[] topU, byte[] topV, bool[] isI4x4, byte[] modes, byte[] uvModes, short[] coeffs, byte[] yPlane, byte[] uPlane, byte[] vPlane)
    {
        // Left samples start at 129; the row above the frame is 127 (also above-left and above-right).
        for (var j = 0; j < 16; j++)
        {
            work[YOffset + (j * Bps) - 1] = 129;
        }

        for (var j = 0; j < 8; j++)
        {
            work[UOffset + (j * Bps) - 1] = 129;
            work[VOffset + (j * Bps) - 1] = 129;
        }

        if (mbY > 0)
        {
            work[YOffset - Bps - 1] = work[UOffset - Bps - 1] = work[VOffset - Bps - 1] = 129;
        }
        else
        {
            work.AsSpan(YOffset - Bps - 1, 16 + 4 + 1).Fill(127);
            work.AsSpan(UOffset - Bps - 1, 8 + 1).Fill(127);
            work.AsSpan(VOffset - Bps - 1, 8 + 1).Fill(127);
        }

        var yStride = _mbWidth * 16;
        var uvStride = _mbWidth * 8;
        for (var mbX = 0; mbX < _mbWidth; mbX++)
        {
            // The rightmost columns of the previous macroblock become the left samples.
            if (mbX > 0)
            {
                for (var j = -1; j < 16; j++)
                {
                    Array.Copy(work, YOffset + (j * Bps) + 12, work, YOffset + (j * Bps) - 4, 4);
                }

                for (var j = -1; j < 8; j++)
                {
                    Array.Copy(work, UOffset + (j * Bps) + 4, work, UOffset + (j * Bps) - 4, 4);
                    Array.Copy(work, VOffset + (j * Bps) + 4, work, VOffset + (j * Bps) - 4, 4);
                }
            }

            var block = coeffs.AsSpan(384 * mbX, 384);
            if (mbY > 0)
            {
                Array.Copy(topY, mbX * 16, work, YOffset - Bps, 16);
                Array.Copy(topU, mbX * 8, work, UOffset - Bps, 8);
                Array.Copy(topV, mbX * 8, work, VOffset - Bps, 8);
            }

            if (isI4x4[mbX])
            {
                // Above-right samples: from the next macroblock's bottom row, repeated for the right column's subblocks.
                var topRight = YOffset - Bps + 16;
                if (mbY > 0)
                {
                    if (mbX >= _mbWidth - 1)
                    {
                        work.AsSpan(topRight, 4).Fill(topY[(mbX * 16) + 15]);
                    }
                    else
                    {
                        Array.Copy(topY, (mbX + 1) * 16, work, topRight, 4);
                    }
                }

                for (var r = 1; r <= 3; r++)
                {
                    Array.Copy(work, topRight, work, topRight + (r * 4 * Bps), 4);
                }

                for (var n = 0; n < 16; n++)
                {
                    var dst = YOffset + ((n & 3) * 4) + ((n >> 2) * 4 * Bps);
                    PredictLuma4(modes[(16 * mbX) + n], work, dst);
                    AddTransform(block.Slice(n * 16, 16), work, dst);
                }
            }
            else
            {
                PredictLuma16(CheckMode(mbX, mbY, modes[16 * mbX]), work, YOffset);
                for (var n = 0; n < 16; n++)
                {
                    AddTransform(block.Slice(n * 16, 16), work, YOffset + ((n & 3) * 4) + ((n >> 2) * 4 * Bps));
                }
            }

            var uvMode = CheckMode(mbX, mbY, uvModes[mbX]);
            PredictChroma8(uvMode, work, UOffset);
            PredictChroma8(uvMode, work, VOffset);
            for (var n = 0; n < 4; n++)
            {
                var at = ((n & 1) * 4) + ((n >> 1) * 4 * Bps);
                AddTransform(block.Slice((16 + n) * 16, 16), work, UOffset + at);
                AddTransform(block.Slice((20 + n) * 16, 16), work, VOffset + at);
            }

            // Keep the unfiltered bottom row for the next row's prediction.
            if (mbY < _mbHeight - 1)
            {
                Array.Copy(work, YOffset + (15 * Bps), topY, mbX * 16, 16);
                Array.Copy(work, UOffset + (7 * Bps), topU, mbX * 8, 8);
                Array.Copy(work, VOffset + (7 * Bps), topV, mbX * 8, 8);
            }

            for (var j = 0; j < 16; j++)
            {
                Array.Copy(work, YOffset + (j * Bps), yPlane, (((mbY * 16) + j) * yStride) + (mbX * 16), 16);
            }

            for (var j = 0; j < 8; j++)
            {
                Array.Copy(work, UOffset + (j * Bps), uPlane, (((mbY * 8) + j) * uvStride) + (mbX * 8), 8);
                Array.Copy(work, VOffset + (j * Bps), vPlane, (((mbY * 8) + j) * uvStride) + (mbX * 8), 8);
            }
        }
    }

    private static int CheckMode(int mbX, int mbY, int mode) =>
        mode != DcPred ? mode
        : mbX == 0 ? (mbY == 0 ? DcPredNoTopLeft : DcPredNoLeft)
        : mbY == 0 ? DcPredNoTop : DcPred;

    // ---- Inverse transforms ----

    private static int Mul1(int a) => ((a * 20091) >> 16) + a;

    private static int Mul2(int a) => (a * 35468) >> 16;

    /// <summary>Adds the inverse DCT of a 4x4 block to the prediction at <paramref name="dst"/>.</summary>
    private static void AddTransform(ReadOnlySpan<short> input, byte[] work, int dst)
    {
        var any = false;
        foreach (var c in input)
        {
            any |= c != 0;
        }

        if (!any)
        {
            return;
        }

        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            // Vertical pass.
            var a = input[i] + input[8 + i];
            var b = input[i] - input[8 + i];
            var c = Mul2(input[4 + i]) - Mul1(input[12 + i]);
            var d = Mul1(input[4 + i]) + Mul2(input[12 + i]);
            tmp[(4 * i) + 0] = a + d;
            tmp[(4 * i) + 1] = b + c;
            tmp[(4 * i) + 2] = b - c;
            tmp[(4 * i) + 3] = a - d;
        }

        for (var i = 0; i < 4; i++)
        {
            // Horizontal pass.
            var dc = tmp[i] + 4;
            var a = dc + tmp[8 + i];
            var b = dc - tmp[8 + i];
            var c = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            var d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            var row = dst + (i * Bps);
            work[row + 0] = Clip8(work[row + 0] + ((a + d) >> 3));
            work[row + 1] = Clip8(work[row + 1] + ((b + c) >> 3));
            work[row + 2] = Clip8(work[row + 2] + ((b - c) >> 3));
            work[row + 3] = Clip8(work[row + 3] + ((a - d) >> 3));
        }
    }

    /// <summary>The inverse Walsh-Hadamard transform of the Y2 block; writes the DC coefficient of each luma block.</summary>
    private static void TransformWht(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> tmp = stackalloc int[16];
        for (var i = 0; i < 4; i++)
        {
            var a0 = input[i] + input[12 + i];
            var a1 = input[4 + i] + input[8 + i];
            var a2 = input[4 + i] - input[8 + i];
            var a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }

        for (var i = 0; i < 4; i++)
        {
            var dc = tmp[i * 4] + 3;
            var a0 = dc + tmp[(i * 4) + 3];
            var a1 = tmp[(i * 4) + 1] + tmp[(i * 4) + 2];
            var a2 = tmp[(i * 4) + 1] - tmp[(i * 4) + 2];
            var a3 = dc - tmp[(i * 4) + 3];
            output[(i * 64) + 0] = (short)((a0 + a1) >> 3);
            output[(i * 64) + 16] = (short)((a3 + a2) >> 3);
            output[(i * 64) + 32] = (short)((a0 - a1) >> 3);
            output[(i * 64) + 48] = (short)((a3 - a2) >> 3);
        }
    }

    // ---- Intra prediction ----

    private static void PredictLuma16(int mode, byte[] b, int dst)
    {
        switch (mode)
        {
            case DcPred:
                {
                    var dc = 16;
                    for (var j = 0; j < 16; j++)
                    {
                        dc += b[dst - 1 + (j * Bps)] + b[dst + j - Bps];
                    }

                    Fill(b, dst, 16, dc >> 5);
                    break;
                }

            case TmPred:
                TrueMotion(b, dst, 16);
                break;
            case VPred:
                for (var j = 0; j < 16; j++)
                {
                    Array.Copy(b, dst - Bps, b, dst + (j * Bps), 16);
                }

                break;
            case HPred:
                for (var j = 0; j < 16; j++)
                {
                    b.AsSpan(dst + (j * Bps), 16).Fill(b[dst + (j * Bps) - 1]);
                }

                break;
            case DcPredNoTop:
                {
                    var dc = 8;
                    for (var j = 0; j < 16; j++)
                    {
                        dc += b[dst - 1 + (j * Bps)];
                    }

                    Fill(b, dst, 16, dc >> 4);
                    break;
                }

            case DcPredNoLeft:
                {
                    var dc = 8;
                    for (var i = 0; i < 16; i++)
                    {
                        dc += b[dst + i - Bps];
                    }

                    Fill(b, dst, 16, dc >> 4);
                    break;
                }

            default:
                Fill(b, dst, 16, 0x80);
                break;
        }
    }

    private static void PredictChroma8(int mode, byte[] b, int dst)
    {
        switch (mode)
        {
            case DcPred:
                {
                    var dc = 8;
                    for (var i = 0; i < 8; i++)
                    {
                        dc += b[dst + i - Bps] + b[dst - 1 + (i * Bps)];
                    }

                    Fill(b, dst, 8, dc >> 4);
                    break;
                }

            case TmPred:
                TrueMotion(b, dst, 8);
                break;
            case VPred:
                for (var j = 0; j < 8; j++)
                {
                    Array.Copy(b, dst - Bps, b, dst + (j * Bps), 8);
                }

                break;
            case HPred:
                for (var j = 0; j < 8; j++)
                {
                    b.AsSpan(dst + (j * Bps), 8).Fill(b[dst + (j * Bps) - 1]);
                }

                break;
            case DcPredNoTop:
                {
                    var dc = 4;
                    for (var i = 0; i < 8; i++)
                    {
                        dc += b[dst - 1 + (i * Bps)];
                    }

                    Fill(b, dst, 8, dc >> 3);
                    break;
                }

            case DcPredNoLeft:
                {
                    var dc = 4;
                    for (var i = 0; i < 8; i++)
                    {
                        dc += b[dst + i - Bps];
                    }

                    Fill(b, dst, 8, dc >> 3);
                    break;
                }

            default:
                Fill(b, dst, 8, 0x80);
                break;
        }
    }

    private static void Fill(byte[] b, int dst, int size, int value)
    {
        for (var j = 0; j < size; j++)
        {
            b.AsSpan(dst + (j * Bps), size).Fill((byte)value);
        }
    }

    private static void TrueMotion(byte[] b, int dst, int size)
    {
        var top = dst - Bps;
        int topLeft = b[top - 1];
        for (var y = 0; y < size; y++)
        {
            var left = b[dst + (y * Bps) - 1];
            for (var x = 0; x < size; x++)
            {
                b[dst + (y * Bps) + x] = Clip8(b[top + x] + left - topLeft);
            }
        }
    }

    private static byte Avg3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    private static void PredictLuma4(int mode, byte[] b, int dst)
    {
        // Neighbors: I-L to the left (top to bottom), X above-left, A-H above (E-H above-right).
        int I = b[dst - 1], J = b[dst - 1 + Bps], K = b[dst - 1 + (2 * Bps)], L = b[dst - 1 + (3 * Bps)];
        int X = b[dst - 1 - Bps];
        int A = b[dst - Bps], B = b[dst + 1 - Bps], C = b[dst + 2 - Bps], D = b[dst + 3 - Bps];
        int E = b[dst + 4 - Bps], F = b[dst + 5 - Bps], G = b[dst + 6 - Bps], H = b[dst + 7 - Bps];
        void Set(int x, int y, byte value) => b[dst + x + (y * Bps)] = value;

        switch (mode)
        {
            case DcPred:
                {
                    var dc = 4 + A + B + C + D + I + J + K + L;
                    for (var y = 0; y < 4; y++)
                    {
                        b.AsSpan(dst + (y * Bps), 4).Fill((byte)(dc >> 3));
                    }

                    break;
                }

            case TmPred:
                TrueMotion(b, dst, 4);
                break;
            case VPred:
                {
                    byte v0 = Avg3(X, A, B), v1 = Avg3(A, B, C), v2 = Avg3(B, C, D), v3 = Avg3(C, D, E);
                    for (var y = 0; y < 4; y++)
                    {
                        Set(0, y, v0);
                        Set(1, y, v1);
                        Set(2, y, v2);
                        Set(3, y, v3);
                    }

                    break;
                }

            case HPred:
                b.AsSpan(dst, 4).Fill(Avg3(X, I, J));
                b.AsSpan(dst + Bps, 4).Fill(Avg3(I, J, K));
                b.AsSpan(dst + (2 * Bps), 4).Fill(Avg3(J, K, L));
                b.AsSpan(dst + (3 * Bps), 4).Fill(Avg3(K, L, L));
                break;
            case BRdPred:
                Set(0, 3, Avg3(J, K, L));
                Set(1, 3, Avg3(I, J, K)); Set(0, 2, Avg3(I, J, K));
                Set(2, 3, Avg3(X, I, J)); Set(1, 2, Avg3(X, I, J)); Set(0, 1, Avg3(X, I, J));
                Set(3, 3, Avg3(A, X, I)); Set(2, 2, Avg3(A, X, I)); Set(1, 1, Avg3(A, X, I)); Set(0, 0, Avg3(A, X, I));
                Set(3, 2, Avg3(B, A, X)); Set(2, 1, Avg3(B, A, X)); Set(1, 0, Avg3(B, A, X));
                Set(3, 1, Avg3(C, B, A)); Set(2, 0, Avg3(C, B, A));
                Set(3, 0, Avg3(D, C, B));
                break;
            case BLdPred:
                Set(0, 0, Avg3(A, B, C));
                Set(1, 0, Avg3(B, C, D)); Set(0, 1, Avg3(B, C, D));
                Set(2, 0, Avg3(C, D, E)); Set(1, 1, Avg3(C, D, E)); Set(0, 2, Avg3(C, D, E));
                Set(3, 0, Avg3(D, E, F)); Set(2, 1, Avg3(D, E, F)); Set(1, 2, Avg3(D, E, F)); Set(0, 3, Avg3(D, E, F));
                Set(3, 1, Avg3(E, F, G)); Set(2, 2, Avg3(E, F, G)); Set(1, 3, Avg3(E, F, G));
                Set(3, 2, Avg3(F, G, H)); Set(2, 3, Avg3(F, G, H));
                Set(3, 3, Avg3(G, H, H));
                break;
            case BVrPred:
                Set(0, 0, Avg2(X, A)); Set(1, 2, Avg2(X, A));
                Set(1, 0, Avg2(A, B)); Set(2, 2, Avg2(A, B));
                Set(2, 0, Avg2(B, C)); Set(3, 2, Avg2(B, C));
                Set(3, 0, Avg2(C, D));
                Set(0, 3, Avg3(K, J, I));
                Set(0, 2, Avg3(J, I, X));
                Set(0, 1, Avg3(I, X, A)); Set(1, 3, Avg3(I, X, A));
                Set(1, 1, Avg3(X, A, B)); Set(2, 3, Avg3(X, A, B));
                Set(2, 1, Avg3(A, B, C)); Set(3, 3, Avg3(A, B, C));
                Set(3, 1, Avg3(B, C, D));
                break;
            case BVlPred:
                Set(0, 0, Avg2(A, B));
                Set(1, 0, Avg2(B, C)); Set(0, 2, Avg2(B, C));
                Set(2, 0, Avg2(C, D)); Set(1, 2, Avg2(C, D));
                Set(3, 0, Avg2(D, E)); Set(2, 2, Avg2(D, E));
                Set(0, 1, Avg3(A, B, C));
                Set(1, 1, Avg3(B, C, D)); Set(0, 3, Avg3(B, C, D));
                Set(2, 1, Avg3(C, D, E)); Set(1, 3, Avg3(C, D, E));
                Set(3, 1, Avg3(D, E, F)); Set(2, 3, Avg3(D, E, F));
                Set(3, 2, Avg3(E, F, G));
                Set(3, 3, Avg3(F, G, H));
                break;
            case BHdPred:
                Set(0, 0, Avg2(I, X)); Set(2, 1, Avg2(I, X));
                Set(0, 1, Avg2(J, I)); Set(2, 2, Avg2(J, I));
                Set(0, 2, Avg2(K, J)); Set(2, 3, Avg2(K, J));
                Set(0, 3, Avg2(L, K));
                Set(3, 0, Avg3(A, B, C));
                Set(2, 0, Avg3(X, A, B));
                Set(1, 0, Avg3(I, X, A)); Set(3, 1, Avg3(I, X, A));
                Set(1, 1, Avg3(J, I, X)); Set(3, 2, Avg3(J, I, X));
                Set(1, 2, Avg3(K, J, I)); Set(3, 3, Avg3(K, J, I));
                Set(1, 3, Avg3(L, K, J));
                break;
            default: // BHuPred
                Set(0, 0, Avg2(I, J));
                Set(2, 0, Avg2(J, K)); Set(0, 1, Avg2(J, K));
                Set(2, 1, Avg2(K, L)); Set(0, 2, Avg2(K, L));
                Set(1, 0, Avg3(I, J, K));
                Set(3, 0, Avg3(J, K, L)); Set(1, 1, Avg3(J, K, L));
                Set(3, 1, Avg3(K, L, L)); Set(1, 2, Avg3(K, L, L));
                Set(3, 2, (byte)L); Set(2, 2, (byte)L);
                Set(0, 3, (byte)L); Set(1, 3, (byte)L); Set(2, 3, (byte)L); Set(3, 3, (byte)L);
                break;
        }
    }

    // ---- Loop filter ----

    private void FilterMacroblock(int mbX, int mbY, int limit, int innerLevel, int hevThreshold, bool inner, byte[] yPlane, byte[] uPlane, byte[] vPlane, int yStride, int uvStride)
    {
        if (limit == 0)
        {
            return;
        }

        var y = (mbY * 16 * yStride) + (mbX * 16);
        if (_filterType == 1)
        {
            // Simple filter: luma only.
            if (mbX > 0)
            {
                SimpleFilter16(yPlane, y, 1, yStride, limit + 4);
            }

            if (inner)
            {
                for (var k = 1; k <= 3; k++)
                {
                    SimpleFilter16(yPlane, y + (4 * k), 1, yStride, limit);
                }
            }

            if (mbY > 0)
            {
                SimpleFilter16(yPlane, y, yStride, 1, limit + 4);
            }

            if (inner)
            {
                for (var k = 1; k <= 3; k++)
                {
                    SimpleFilter16(yPlane, y + (4 * k * yStride), yStride, 1, limit);
                }
            }

            return;
        }

        var uv = (mbY * 8 * uvStride) + (mbX * 8);
        if (mbX > 0)
        {
            FilterLoop(yPlane, y, 1, yStride, 16, limit + 4, innerLevel, hevThreshold, edge: true);
            FilterLoop(uPlane, uv, 1, uvStride, 8, limit + 4, innerLevel, hevThreshold, edge: true);
            FilterLoop(vPlane, uv, 1, uvStride, 8, limit + 4, innerLevel, hevThreshold, edge: true);
        }

        if (inner)
        {
            for (var k = 1; k <= 3; k++)
            {
                FilterLoop(yPlane, y + (4 * k), 1, yStride, 16, limit, innerLevel, hevThreshold, edge: false);
            }

            FilterLoop(uPlane, uv + 4, 1, uvStride, 8, limit, innerLevel, hevThreshold, edge: false);
            FilterLoop(vPlane, uv + 4, 1, uvStride, 8, limit, innerLevel, hevThreshold, edge: false);
        }

        if (mbY > 0)
        {
            FilterLoop(yPlane, y, yStride, 1, 16, limit + 4, innerLevel, hevThreshold, edge: true);
            FilterLoop(uPlane, uv, uvStride, 1, 8, limit + 4, innerLevel, hevThreshold, edge: true);
            FilterLoop(vPlane, uv, uvStride, 1, 8, limit + 4, innerLevel, hevThreshold, edge: true);
        }

        if (inner)
        {
            for (var k = 1; k <= 3; k++)
            {
                FilterLoop(yPlane, y + (4 * k * yStride), yStride, 1, 16, limit, innerLevel, hevThreshold, edge: false);
            }

            FilterLoop(uPlane, uv + (4 * uvStride), uvStride, 1, 8, limit, innerLevel, hevThreshold, edge: false);
            FilterLoop(vPlane, uv + (4 * uvStride), uvStride, 1, 8, limit, innerLevel, hevThreshold, edge: false);
        }
    }

    /// <summary>Filters 16 pixels across an edge: <paramref name="step"/> crosses the edge, <paramref name="along"/> moves along it.</summary>
    private static void SimpleFilter16(byte[] b, int p, int step, int along, int threshold)
    {
        var t = (2 * threshold) + 1;
        for (var i = 0; i < 16; i++, p += along)
        {
            if (NeedsFilter(b, p, step, t))
            {
                DoFilter2(b, p, step);
            }
        }
    }

    private static void FilterLoop(byte[] b, int p, int step, int along, int size, int threshold, int innerThreshold, int hevThreshold, bool edge)
    {
        var t = (2 * threshold) + 1;
        for (var i = 0; i < size; i++, p += along)
        {
            if (!NeedsFilter2(b, p, step, t, innerThreshold))
            {
                continue;
            }

            if (Hev(b, p, step, hevThreshold))
            {
                DoFilter2(b, p, step);
            }
            else if (edge)
            {
                DoFilter6(b, p, step);
            }
            else
            {
                DoFilter4(b, p, step);
            }
        }
    }

    private static int SignedClip1(int v) => Math.Clamp(v, -128, 127);

    private static int SignedClip2(int v) => Math.Clamp(v, -16, 15);

    private static void DoFilter2(byte[] b, int p, int step)
    {
        int p1 = b[p - (2 * step)], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        var a = (3 * (q0 - p0)) + SignedClip1(p1 - q1);
        var a1 = SignedClip2((a + 4) >> 3);
        var a2 = SignedClip2((a + 3) >> 3);
        b[p - step] = Clip8(p0 + a2);
        b[p] = Clip8(q0 - a1);
    }

    private static void DoFilter4(byte[] b, int p, int step)
    {
        int p1 = b[p - (2 * step)], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        var a = 3 * (q0 - p0);
        var a1 = SignedClip2((a + 4) >> 3);
        var a2 = SignedClip2((a + 3) >> 3);
        var a3 = (a1 + 1) >> 1;
        b[p - (2 * step)] = Clip8(p1 + a3);
        b[p - step] = Clip8(p0 + a2);
        b[p] = Clip8(q0 - a1);
        b[p + step] = Clip8(q1 - a3);
    }

    private static void DoFilter6(byte[] b, int p, int step)
    {
        int p2 = b[p - (3 * step)], p1 = b[p - (2 * step)], p0 = b[p - step];
        int q0 = b[p], q1 = b[p + step], q2 = b[p + (2 * step)];
        var a = SignedClip1((3 * (q0 - p0)) + SignedClip1(p1 - q1));
        var a1 = ((27 * a) + 63) >> 7;
        var a2 = ((18 * a) + 63) >> 7;
        var a3 = ((9 * a) + 63) >> 7;
        b[p - (3 * step)] = Clip8(p2 + a3);
        b[p - (2 * step)] = Clip8(p1 + a2);
        b[p - step] = Clip8(p0 + a1);
        b[p] = Clip8(q0 - a1);
        b[p + step] = Clip8(q1 - a2);
        b[p + (2 * step)] = Clip8(q2 - a3);
    }

    private static bool Hev(byte[] b, int p, int step, int threshold) =>
        Math.Abs(b[p - (2 * step)] - b[p - step]) > threshold || Math.Abs(b[p + step] - b[p]) > threshold;

    private static bool NeedsFilter(byte[] b, int p, int step, int t) =>
        (4 * Math.Abs(b[p - step] - b[p])) + Math.Abs(b[p - (2 * step)] - b[p + step]) <= t;

    private static bool NeedsFilter2(byte[] b, int p, int step, int t, int it)
    {
        int p3 = b[p - (4 * step)], p2 = b[p - (3 * step)], p1 = b[p - (2 * step)], p0 = b[p - step];
        int q0 = b[p], q1 = b[p + step], q2 = b[p + (2 * step)], q3 = b[p + (3 * step)];
        if ((4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1) > t)
        {
            return false;
        }

        return Math.Abs(p3 - p2) <= it && Math.Abs(p2 - p1) <= it && Math.Abs(p1 - p0) <= it
            && Math.Abs(q3 - q2) <= it && Math.Abs(q2 - q1) <= it && Math.Abs(q1 - q0) <= it;
    }

    private static byte Clip8(int v) => (byte)Math.Clamp(v, 0, 255);

    // ---- YUV to RGB ----

    /// <summary>Converts to RGB with libwebp's "fancy" upsampling: chroma is interpolated bilinearly (9-3-3-1 weights).</summary>
    private byte[] ToRgb(byte[] yPlane, byte[] uPlane, byte[] vPlane, int yStride, int uvStride)
    {
        var width = _width;
        var height = _height;
        var rgb = new byte[(long)width * height * 3];
        var rowBytes = width * 3;

        // The first row uses the first chroma row only; then pairs of rows lie between two chroma rows.
        UpsampleLinePair(yPlane, 0, -1, uPlane, vPlane, 0, 0, rgb, 0, -1, width);
        var y = 0;
        for (; y + 2 < height; y += 2)
        {
            var k = (y / 2) + 1;
            UpsampleLinePair(yPlane, (y + 1) * yStride, (y + 2) * yStride, uPlane, vPlane, (k - 1) * uvStride, k * uvStride, rgb, (y + 1) * rowBytes, (y + 2) * rowBytes, width);
        }

        if ((height & 1) == 0)
        {
            var lastUv = ((height / 2) - 1) * uvStride;
            UpsampleLinePair(yPlane, (height - 1) * yStride, -1, uPlane, vPlane, lastUv, lastUv, rgb, (height - 1) * rowBytes, -1, width);
        }

        return rgb;
    }

    private static void UpsampleLinePair(byte[] yPlane, int topY, int bottomY, byte[] uPlane, byte[] vPlane, int topUv, int curUv, byte[] rgb, int topDst, int bottomDst, int length)
    {
        var lastPair = (length - 1) >> 1;
        int tlU = uPlane[topUv], tlV = vPlane[topUv];
        int lU = uPlane[curUv], lV = vPlane[curUv];
        StorePixel(yPlane[topY], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2, rgb, topDst);
        if (bottomY >= 0)
        {
            StorePixel(yPlane[bottomY], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2, rgb, bottomDst);
        }

        for (var x = 1; x <= lastPair; x++)
        {
            int tU = uPlane[topUv + x], tV = vPlane[topUv + x];
            int u = uPlane[curUv + x], v = vPlane[curUv + x];
            var avgU = tlU + tU + lU + u + 8;
            var avgV = tlV + tV + lV + v + 8;
            var diag12U = (avgU + (2 * (tU + lU))) >> 3;
            var diag12V = (avgV + (2 * (tV + lV))) >> 3;
            var diag03U = (avgU + (2 * (tlU + u))) >> 3;
            var diag03V = (avgV + (2 * (tlV + v))) >> 3;
            StorePixel(yPlane[topY + (2 * x) - 1], (diag12U + tlU) >> 1, (diag12V + tlV) >> 1, rgb, topDst + (((2 * x) - 1) * 3));
            StorePixel(yPlane[topY + (2 * x)], (diag03U + tU) >> 1, (diag03V + tV) >> 1, rgb, topDst + (2 * x * 3));
            if (bottomY >= 0)
            {
                StorePixel(yPlane[bottomY + (2 * x) - 1], (diag03U + lU) >> 1, (diag03V + lV) >> 1, rgb, bottomDst + (((2 * x) - 1) * 3));
                StorePixel(yPlane[bottomY + (2 * x)], (diag12U + u) >> 1, (diag12V + v) >> 1, rgb, bottomDst + (2 * x * 3));
            }

            (tlU, tlV, lU, lV) = (tU, tV, u, v);
        }

        if ((length & 1) == 0)
        {
            StorePixel(yPlane[topY + length - 1], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2, rgb, topDst + ((length - 1) * 3));
            if (bottomY >= 0)
            {
                StorePixel(yPlane[bottomY + length - 1], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2, rgb, bottomDst + ((length - 1) * 3));
            }
        }
    }

    /// <summary>BT.601 YUV to RGB in libwebp's 14-bit fixed point.</summary>
    private static void StorePixel(int y, int u, int v, byte[] rgb, int at)
    {
        var luma = (y * 19077) >> 8;
        rgb[at] = YuvClip(luma + ((v * 26149) >> 8) - 14234);
        rgb[at + 1] = YuvClip(luma - ((u * 6419) >> 8) - ((v * 13320) >> 8) + 8708);
        rgb[at + 2] = YuvClip(luma + ((u * 33050) >> 8) - 17685);
    }

    private static byte YuvClip(int v) => (v & ~0x3FFF) == 0 ? (byte)(v >> 6) : v < 0 ? (byte)0 : (byte)255;

    /// <summary>The VP8 boolean entropy decoder, reading one byte at a time (as libwebp does near the end of data).</summary>
    private sealed class BoolDecoder
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;
        private ulong _value;
        private int _bits = -8;
        private uint _range = 255 - 1;

        public BoolDecoder(byte[] data, int start, int length)
        {
            _data = data;
            _pos = start;
            _end = start + length;
            LoadNewBytes();
        }

        /// <summary>Whether reading went past the end of the data.</summary>
        public bool Eof { get; private set; }

        public int GetBit(int prob)
        {
            var range = _range;
            if (_bits < 0)
            {
                LoadNewBytes();
            }

            var pos = _bits;
            var split = (range * (uint)prob) >> 8;
            var value = (uint)(_value >> pos);
            int bit;
            if (value > split)
            {
                range -= split;
                _value -= (ulong)(split + 1) << pos;
                bit = 1;
            }
            else
            {
                range = split + 1;
                bit = 0;
            }

            var shift = 7 ^ BitOperations.Log2(range);
            range <<= shift;
            _bits -= shift;
            _range = range - 1;
            return bit;
        }

        /// <summary>Returns <paramref name="v"/> or -<paramref name="v"/>, reading the sign at probability 1/2.</summary>
        public int GetSigned(int v)
        {
            if (_bits < 0)
            {
                LoadNewBytes();
            }

            var pos = _bits;
            var split = _range >> 1;
            var value = (uint)(_value >> pos);
            var mask = (int)(split - value) >> 31; // -1 when the bit is 1.
            _bits -= 1;
            _range = (uint)((int)_range + mask) | 1;
            _value -= (ulong)((split + 1) & (uint)mask) << pos;
            return (v ^ mask) - mask;
        }

        /// <summary>An unsigned literal, most significant bit first.</summary>
        public int GetValue(int bits)
        {
            var v = 0;
            while (bits-- > 0)
            {
                v |= GetBit(0x80) << bits;
            }

            return v;
        }

        public int GetSignedValue(int bits)
        {
            var value = GetValue(bits);
            return GetValue(1) != 0 ? -value : value;
        }

        private void LoadNewBytes()
        {
            if (_pos < _end)
            {
                _bits += 8;
                _value = _data[_pos++] | (_value << 8);
            }
            else if (!Eof)
            {
                _value <<= 8;
                _bits += 8;
                Eof = true;
            }
            else
            {
                _bits = 0; // Past the end; the caller reports the error.
            }
        }
    }
}
