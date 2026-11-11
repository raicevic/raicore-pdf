using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>Identifies a charstring: a glyph's own (kind 0), a local subroutine of a Font DICT (1) or a global subroutine (2).</summary>
internal readonly record struct CharStringKey(int Kind, int FontDict, int Index)
{
    public const int Glyph = 0;
    public const int Local = 1;
    public const int Global = 2;
}

/// <summary>Receives what <see cref="CharStringInterpreter"/> finds while running a glyph's charstring.</summary>
internal interface ICharStringVisitor
{
    /// <summary>
    /// A drawing, hint or <c>endchar</c> operator with its arguments as 16.16 fixed-point numbers. Subroutine calls
    /// are followed (not reported here), CFF2 <c>blend</c> is resolved to the default instance and <c>vsindex</c> is
    /// consumed. <paramref name="hintMask"/> holds the mask bytes of <c>hintmask</c> and <c>cntrmask</c>.
    /// </summary>
    void Operator(int op, ReadOnlySpan<int> args, ReadOnlySpan<byte> hintMask);

    /// <summary>
    /// A subroutine call. <paramref name="operandStart"/>..<paramref name="operandEnd"/> are the bytes of the number
    /// literal immediately before the call in <paramref name="caller"/>, or -1 when the number came from elsewhere.
    /// </summary>
    void SubroutineCall(bool global, int index, CharStringKey caller, int operandStart, int operandEnd);
}

/// <summary>
/// Runs Type 2 (CFF) and CFF2 charstrings far enough to follow subroutine calls and count stem hints (which
/// determine the length of <c>hintmask</c> data). Arithmetic and storage operators and <c>seac</c>-style
/// <c>endchar</c> are not supported and throw <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class CharStringInterpreter(CffFont font)
{
    public const int HStem = 1;
    public const int VStem = 3;
    public const int VMoveTo = 4;
    public const int RLineTo = 5;
    public const int HLineTo = 6;
    public const int VLineTo = 7;
    public const int RRCurveTo = 8;
    public const int CallSubr = 10;
    public const int Return = 11;
    public const int EndChar = 14;
    public const int VsIndex = 15;
    public const int Blend = 16;
    public const int HStemHm = 18;
    public const int HintMask = 19;
    public const int CntrMask = 20;
    public const int RMoveTo = 21;
    public const int HMoveTo = 22;
    public const int VStemHm = 23;
    public const int RCurveLine = 24;
    public const int RLineCurve = 25;
    public const int VVCurveTo = 26;
    public const int HHCurveTo = 27;
    public const int CallGSubr = 29;
    public const int VHCurveTo = 30;
    public const int HVCurveTo = 31;
    public const int DotSection = 0x0C00;
    public const int HFlex = 0x0C22;
    public const int Flex = 0x0C23;
    public const int HFlex1 = 0x0C24;
    public const int Flex1 = 0x0C25;

    /// <summary>CFF2's argument stack limit (CFF's is 48, which CFF2 fonts may exceed).</summary>
    private const int MaxStack = 513;
    private const int MaxCallDepth = 10;

    private readonly StackEntry[] _stack = new StackEntry[MaxStack];
    private readonly int[] _args = new int[MaxStack];
    private int _count;
    private int _stems;
    private int _vsIndex;
    private int _fontDict;
    private ICharStringVisitor _visitor = null!;

    /// <summary>Runs the charstring of <paramref name="glyphId"/>, reporting operators and subroutine calls.</summary>
    public void Run(int glyphId, ICharStringVisitor visitor)
    {
        _visitor = visitor;
        _count = 0;
        _stems = 0;
        _fontDict = font.FontDictIndex(glyphId);
        _vsIndex = font.FontDicts[_fontDict].Private.VsIndex;
        Execute(font.CharStrings[glyphId], new CharStringKey(CharStringKey.Glyph, _fontDict, glyphId), 0);
    }

    /// <summary>Returns true when the glyph ended (<c>endchar</c>, or the end of a glyph charstring).</summary>
    private bool Execute(ReadOnlySpan<byte> code, CharStringKey key, int depth)
    {
        var pos = 0;
        while (pos < code.Length)
        {
            var start = pos;
            int b0 = code[pos++];
            if (b0 >= 32 || b0 == 28)
            {
                int value;
                if (b0 <= 246 && b0 >= 32)
                {
                    value = (b0 - 139) << 16;
                }
                else if (b0 <= 250 && b0 >= 247)
                {
                    value = (((b0 - 247) * 256) + Byte(code, ref pos) + 108) << 16;
                }
                else if (b0 <= 254 && b0 >= 251)
                {
                    value = ((-(b0 - 251) * 256) - Byte(code, ref pos) - 108) << 16;
                }
                else if (b0 == 28)
                {
                    Require(code, pos, 2);
                    value = BinaryPrimitives.ReadInt16BigEndian(code[pos..]) << 16;
                    pos += 2;
                }
                else
                {
                    Require(code, pos, 4);
                    value = BinaryPrimitives.ReadInt32BigEndian(code[pos..]); // 16.16 fixed
                    pos += 4;
                }

                if (_count >= MaxStack)
                {
                    throw new FontFormatException("Charstring argument stack overflow.");
                }

                _stack[_count++] = new StackEntry(value, key, start, pos);
                continue;
            }

            var op = b0 == 12 ? 0x0C00 | Byte(code, ref pos) : b0;
            switch (op)
            {
                case CallSubr:
                case CallGSubr:
                {
                    var global = op == CallGSubr;
                    var subrs = global ? font.GlobalSubrs : font.FontDicts[_fontDict].LocalSubrs;
                    var entry = Pop();
                    if ((entry.Value & 0xFFFF) != 0)
                    {
                        throw new FontFormatException("Non-integer subroutine number.");
                    }

                    var index = (entry.Value >> 16) + CffIndex.Bias(subrs.Count);
                    if (index < 0 || index >= subrs.Count || depth >= MaxCallDepth)
                    {
                        throw new FontFormatException("Invalid subroutine call in a charstring.");
                    }

                    var literal = entry.Source == key && entry.End == start;
                    _visitor.SubroutineCall(global, index, key, literal ? entry.Start : -1, literal ? entry.End : -1);
                    var callee = new CharStringKey(global ? CharStringKey.Global : CharStringKey.Local, global ? -1 : _fontDict, index);
                    if (Execute(subrs[index], callee, depth + 1))
                    {
                        return true;
                    }

                    break;
                }

                case Return when !font.IsCff2:
                    if (depth == 0)
                    {
                        throw new FontFormatException("return outside a subroutine.");
                    }

                    return false;
                case EndChar when !font.IsCff2:
                    if (_count >= 4)
                    {
                        throw new NotSupportedException("endchar with accent arguments (seac) is not supported.");
                    }

                    Emit(op, []);
                    return true;
                case VsIndex when font.IsCff2:
                    _vsIndex = Pop().Value >> 16;
                    if (_vsIndex < 0 || _vsIndex >= font.RegionCounts.Length)
                    {
                        throw new FontFormatException("Invalid vsindex in a charstring.");
                    }

                    break;
                case Blend when font.IsCff2:
                {
                    // n default values, n * k deltas, n: keep the defaults (the default instance).
                    var n = Pop().Value >> 16;
                    var deltas = _vsIndex < font.RegionCounts.Length ? n * font.RegionCounts[_vsIndex] : -1;
                    if (n < 0 || deltas < 0 || _count < n + deltas)
                    {
                        throw new FontFormatException("Invalid blend in a charstring.");
                    }

                    _count -= deltas;
                    break;
                }

                case HStem:
                case VStem:
                case HStemHm:
                case VStemHm:
                    _stems += _count / 2;
                    Emit(op, []);
                    break;
                case HintMask:
                case CntrMask:
                {
                    _stems += _count / 2; // Arguments here are an implied vstemhm.
                    var maskLength = (_stems + 7) / 8;
                    Require(code, pos, maskLength);
                    Emit(op, code.Slice(pos, maskLength));
                    pos += maskLength;
                    break;
                }

                case VMoveTo:
                case RLineTo:
                case HLineTo:
                case VLineTo:
                case RRCurveTo:
                case RMoveTo:
                case HMoveTo:
                case RCurveLine:
                case RLineCurve:
                case VVCurveTo:
                case HHCurveTo:
                case VHCurveTo:
                case HVCurveTo:
                case HFlex:
                case Flex:
                case HFlex1:
                case Flex1:
                case DotSection:
                    Emit(op, []);
                    break;
                default:
                    throw new NotSupportedException($"Charstring operator {(op >= 0x0C00 ? $"12 {op & 0xFF}" : op)} is not supported.");
            }
        }

        // CFF2 charstrings end without endchar; a CFF subroutine may end without return.
        return key.Kind == CharStringKey.Glyph;
    }

    private void Emit(int op, ReadOnlySpan<byte> hintMask)
    {
        for (var i = 0; i < _count; i++)
        {
            _args[i] = _stack[i].Value;
        }

        _visitor.Operator(op, _args.AsSpan(0, _count), hintMask);
        _count = 0;
    }

    private StackEntry Pop() => _count > 0 ? _stack[--_count] : throw new FontFormatException("Charstring argument stack underflow.");

    private static byte Byte(ReadOnlySpan<byte> code, ref int pos)
    {
        Require(code, pos, 1);
        return code[pos++];
    }

    private static void Require(ReadOnlySpan<byte> code, int pos, int count)
    {
        if (pos + count > code.Length)
        {
            throw new FontFormatException("A charstring is truncated.");
        }
    }

    /// <summary>A number on the stack and where its literal is (to rewrite subroutine numbers in place).</summary>
    private readonly record struct StackEntry(int Value, CharStringKey Source, int Start, int End);
}
