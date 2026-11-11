using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>A DICT operand: its encoded bytes (copied as-is when a DICT is rewritten) and its value.</summary>
internal readonly record struct CffOperand(byte[] Encoded, double Value);

/// <summary>
/// A parsed CFF or CFF2 DICT: operators with their operands, in order. Two-byte operators are stored as
/// <c>0x0C00 | second byte</c>. In CFF2 Private DICTs, <c>blend</c> is resolved to the default values and
/// <c>vsindex</c> is kept only as <see cref="VsIndex"/>.
/// </summary>
internal sealed class CffDict
{
    public const int Charset = 15;
    public const int Encoding = 16;
    public const int CharStrings = 17;
    public const int Private = 18;
    public const int Subrs = 19;
    public const int VsIndexOp = 22;
    public const int Blend = 23;
    public const int VStore = 24;
    public const int FontBBox = 5;
    public const int IsFixedPitch = 0x0C01;
    public const int ItalicAngle = 0x0C02;
    public const int UnderlinePosition = 0x0C03;
    public const int UnderlineThickness = 0x0C04;
    public const int CharstringType = 0x0C06;
    public const int FontMatrix = 0x0C07;
    public const int Ros = 0x0C1E;
    public const int CidCount = 0x0C22;
    public const int FdArray = 0x0C24;
    public const int FdSelect = 0x0C25;

    private CffDict(List<(int Op, CffOperand[] Operands)> entries, int vsIndex)
    {
        Entries = entries;
        VsIndex = vsIndex;
    }

    public List<(int Op, CffOperand[] Operands)> Entries { get; }

    /// <summary>CFF2 Private DICT: the item variation data used by charstrings that do not set <c>vsindex</c>.</summary>
    public int VsIndex { get; }

    public bool Has(int op) => Entries.Exists(e => e.Op == op);

    public CffOperand[]? Get(int op) => Entries.Find(e => e.Op == op).Operands;

    public int GetInt(int op, int defaultValue)
    {
        var operands = Get(op);
        return operands is { Length: > 0 } ? (int)operands[^1].Value : defaultValue;
    }

    /// <summary>
    /// Parses a DICT. <paramref name="regionCounts"/> (CFF2 Private DICTs) gives the number of variation regions
    /// per <c>vsindex</c>, needed to drop the deltas of <c>blend</c>.
    /// </summary>
    public static CffDict Parse(ReadOnlySpan<byte> data, int[]? regionCounts = null)
    {
        var entries = new List<(int, CffOperand[])>();
        var operands = new List<CffOperand>();
        var vsIndex = 0;
        var pos = 0;
        while (pos < data.Length)
        {
            var start = pos;
            int b0 = data[pos++];
            if (b0 < 28)
            {
                var op = b0;
                if (b0 == 12)
                {
                    Require(data, pos, 1);
                    op = 0x0C00 | data[pos++];
                }

                if (op == VsIndexOp && regionCounts is not null)
                {
                    vsIndex = operands.Count == 1 ? (int)operands[0].Value : throw new FontFormatException("Invalid vsindex in a CFF2 DICT.");
                    operands.Clear();
                }
                else if (op == Blend && regionCounts is not null)
                {
                    if (operands.Count == 0 || vsIndex < 0 || vsIndex >= regionCounts.Length)
                    {
                        throw new FontFormatException("Invalid blend in a CFF2 DICT.");
                    }

                    // n default values, n * k deltas, n: keep the defaults.
                    var n = (int)operands[^1].Value;
                    var deltas = n * regionCounts[vsIndex];
                    if (n < 0 || operands.Count < 1 + n + deltas)
                    {
                        throw new FontFormatException("Invalid blend in a CFF2 DICT.");
                    }

                    operands.RemoveRange(operands.Count - 1 - deltas, deltas + 1);
                }
                else
                {
                    entries.Add((op, [.. operands]));
                    operands.Clear();
                }

                continue;
            }

            double value;
            switch (b0)
            {
                case 28:
                    Require(data, pos, 2);
                    value = BinaryPrimitives.ReadInt16BigEndian(data[pos..]);
                    pos += 2;
                    break;
                case 29:
                    Require(data, pos, 4);
                    value = BinaryPrimitives.ReadInt32BigEndian(data[pos..]);
                    pos += 4;
                    break;
                case 30:
                    value = ReadReal(data, ref pos);
                    break;
                case >= 32 and <= 246:
                    value = b0 - 139;
                    break;
                case >= 247 and <= 250:
                    Require(data, pos, 1);
                    value = ((b0 - 247) * 256) + data[pos++] + 108;
                    break;
                case >= 251 and <= 254:
                    Require(data, pos, 1);
                    value = (-(b0 - 251) * 256) - data[pos++] - 108;
                    break;
                default:
                    throw new FontFormatException($"Invalid CFF DICT byte {b0}.");
            }

            operands.Add(new CffOperand(data[start..pos].ToArray(), value));
        }

        return new CffDict(entries, vsIndex);
    }

    private static double ReadReal(ReadOnlySpan<byte> data, ref int pos)
    {
        var text = new StringBuilder();
        while (true)
        {
            Require(data, pos, 1);
            var b = data[pos++];
            for (var shift = 4; shift >= 0; shift -= 4)
            {
                var nibble = (b >> shift) & 0xF;
                switch (nibble)
                {
                    case <= 9:
                        text.Append((char)('0' + nibble));
                        break;
                    case 0xA:
                        text.Append('.');
                        break;
                    case 0xB:
                        text.Append('E');
                        break;
                    case 0xC:
                        text.Append("E-");
                        break;
                    case 0xE:
                        text.Append('-');
                        break;
                    case 0xF:
                        return double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
                    default:
                        throw new FontFormatException("Invalid real number in a CFF DICT.");
                }
            }
        }
    }

    private static void Require(ReadOnlySpan<byte> data, int pos, int count)
    {
        if (pos + count > data.Length)
        {
            throw new FontFormatException("A CFF DICT is truncated.");
        }
    }
}

/// <summary>Builds DICT data.</summary>
internal sealed class CffDictWriter
{
    private readonly List<byte> _output = [];

    public int Length => _output.Count;

    /// <summary>Writes an integer in its shortest encoding.</summary>
    public CffDictWriter Int(int value)
    {
        switch (value)
        {
            case >= -107 and <= 107:
                _output.Add((byte)(value + 139));
                break;
            case >= 108 and <= 1131:
                value -= 108;
                _output.Add((byte)((value >> 8) + 247));
                _output.Add((byte)value);
                break;
            case >= -1131 and <= -108:
                value = -value - 108;
                _output.Add((byte)((value >> 8) + 251));
                _output.Add((byte)value);
                break;
            case >= short.MinValue and <= short.MaxValue:
                _output.Add(28);
                _output.Add((byte)(value >> 8));
                _output.Add((byte)value);
                break;
            default:
                Int32(value);
                break;
        }

        return this;
    }

    /// <summary>Writes an integer in the fixed 5-byte encoding, so offsets can be filled in without changing sizes.</summary>
    public CffDictWriter Int32(int value)
    {
        _output.Add(29);
        _output.Add((byte)(value >> 24));
        _output.Add((byte)(value >> 16));
        _output.Add((byte)(value >> 8));
        _output.Add((byte)value);
        return this;
    }

    public CffDictWriter Operands(IEnumerable<CffOperand> operands)
    {
        foreach (var operand in operands)
        {
            _output.AddRange(operand.Encoded);
        }

        return this;
    }

    public CffDictWriter Op(int op)
    {
        if (op >= 0x0C00)
        {
            _output.Add(12);
        }

        _output.Add((byte)op);
        return this;
    }

    public byte[] ToArray() => _output.ToArray();
}
