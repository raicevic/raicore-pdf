using static RaiCore.Pdf.Fonts.OpenType.Cff.CharStringInterpreter;

namespace RaiCore.Pdf.Fonts.OpenType.Cff;

/// <summary>One charstring operator with its arguments (16.16 fixed-point) and, for masks, the mask bytes.</summary>
internal sealed record CharStringCommand(int Op, int[] Args, byte[] HintMask);

/// <summary>
/// Collects a glyph's operators with every subroutine call inlined and CFF2 <c>blend</c>s resolved to the default
/// instance (desubroutinization), and encodes them back as a standalone Type 2 charstring.
/// </summary>
internal sealed class CharStringFlattener : ICharStringVisitor
{
    /// <summary>The CFF argument stack limit; longer CFF2 argument lists are split.</summary>
    private const int MaxArgs = 48;

    public List<CharStringCommand> Commands { get; } = [];

    public static List<CharStringCommand> Flatten(CharStringInterpreter interpreter, int glyphId)
    {
        var flattener = new CharStringFlattener();
        interpreter.Run(glyphId, flattener);
        return flattener.Commands;
    }

    public void Operator(int op, ReadOnlySpan<int> args, ReadOnlySpan<byte> hintMask) =>
        Commands.Add(new CharStringCommand(op, args.ToArray(), hintMask.ToArray()));

    public void SubroutineCall(bool global, int index, CharStringKey caller, int operandStart, int operandEnd)
    {
    }

    /// <summary>
    /// Encodes <paramref name="commands"/> as a Type 2 charstring ending in <c>endchar</c>. A non-null
    /// <paramref name="width"/> (in font units, for CFF2 sources which have none) is written as the width argument.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<CharStringCommand> commands, int? width)
    {
        using var output = new MemoryStream();
        var first = true;
        foreach (var command in commands)
        {
            if (command.Op == DotSection)
            {
                continue;
            }

            var args = command.Args;
            if (first && width is { } w && w != 0)
            {
                args = [w << 16, .. args];
            }

            first = false;
            WriteCommand(output, command.Op, args, command.HintMask);
            if (command.Op == EndChar)
            {
                return output.ToArray();
            }
        }

        if (first && width is { } emptyWidth && emptyWidth != 0)
        {
            WriteNumber(output, emptyWidth << 16);
        }

        output.WriteByte(EndChar);
        return output.ToArray();
    }

    private static void WriteCommand(Stream output, int op, ReadOnlySpan<int> args, byte[] hintMask)
    {
        if (args.Length <= MaxArgs)
        {
            WriteOperator(output, op, args, hintMask);
            return;
        }

        // Only CFF2 charstrings (stack limit 513) get here. Split into operators of at most 48 arguments,
        // keeping each operator's argument grouping (and the alternation of h/v operators).
        switch (op)
        {
            case RLineTo:
            case HLineTo:
            case VLineTo:
            case RRCurveTo:
                WriteChunks(output, op, args, MaxArgs);
                break;
            case RCurveLine:
                WriteChunks(output, RRCurveTo, args[..^2], MaxArgs);
                WriteOperator(output, RLineTo, args[^2..], []);
                break;
            case RLineCurve:
                WriteChunks(output, RLineTo, args[..^6], MaxArgs);
                WriteOperator(output, RRCurveTo, args[^6..], []);
                break;
            case HHCurveTo:
            case VVCurveTo:
                // An odd first argument belongs to the first curve only.
                var head = args.Length % 4 == 1 ? 45 : MaxArgs;
                WriteOperator(output, op, args[..head], []);
                WriteChunks(output, op, args[head..], MaxArgs);
                break;
            case HVCurveTo:
            case VHCurveTo:
                // Chunks of whole pairs of curves keep the start direction; the rest may end with one extra argument.
                var pos = 0;
                while (args.Length - pos > MaxArgs)
                {
                    WriteOperator(output, op, args[pos..(pos + 40)], []);
                    pos += 40;
                }

                WriteOperator(output, op, args[pos..], []);
                break;
            default:
                throw new NotSupportedException($"Charstring operator {op} with {args.Length} arguments cannot be converted to CFF.");
        }
    }

    private static void WriteChunks(Stream output, int op, ReadOnlySpan<int> args, int size)
    {
        for (var pos = 0; pos < args.Length; pos += size)
        {
            WriteOperator(output, op, args[pos..Math.Min(args.Length, pos + size)], []);
        }
    }

    private static void WriteOperator(Stream output, int op, ReadOnlySpan<int> args, byte[] hintMask)
    {
        foreach (var arg in args)
        {
            WriteNumber(output, arg);
        }

        if (op >= 0x0C00)
        {
            output.WriteByte(12);
        }

        output.WriteByte((byte)op);
        output.Write(hintMask);
    }

    /// <summary>Writes a 16.16 fixed-point number in the shortest Type 2 encoding.</summary>
    public static void WriteNumber(Stream output, int value)
    {
        if ((value & 0xFFFF) != 0)
        {
            output.WriteByte(255);
            output.WriteByte((byte)(value >> 24));
            output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 8));
            output.WriteByte((byte)value);
            return;
        }

        WriteInteger(output, value >> 16);
    }

    /// <summary>Writes an integer in the shortest Type 2 encoding.</summary>
    public static void WriteInteger(Stream output, int value)
    {
        switch (value)
        {
            case >= -107 and <= 107:
                output.WriteByte((byte)(value + 139));
                break;
            case >= 108 and <= 1131:
                value -= 108;
                output.WriteByte((byte)((value >> 8) + 247));
                output.WriteByte((byte)value);
                break;
            case >= -1131 and <= -108:
                value = -value - 108;
                output.WriteByte((byte)((value >> 8) + 251));
                output.WriteByte((byte)value);
                break;
            default:
                output.WriteByte(28);
                output.WriteByte((byte)(value >> 8));
                output.WriteByte((byte)value);
                break;
        }
    }
}
