using System.Buffers.Binary;

namespace RaiCore.Pdf.Fonts.OpenType;

/// <summary>Sequential big-endian reader over a byte array, as used by every OpenType structure.</summary>
internal sealed class BigEndianReader(byte[] data, int position = 0)
{
    public byte[] Data { get; } = data;

    public int Position { get; set; } = position;

    public int Remaining => Data.Length - Position;

    public byte ReadByte()
    {
        Require(1);
        return Data[Position++];
    }

    public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

    public ushort ReadUInt16()
    {
        Require(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(Position));
        Position += 2;
        return value;
    }

    public short ReadInt16() => unchecked((short)ReadUInt16());

    public uint ReadUInt32()
    {
        Require(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(Position));
        Position += 4;
        return value;
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    /// <summary>Reads a 16.16 fixed-point number.</summary>
    public double ReadFixed() => ReadInt32() / 65536.0;

    public string ReadTag()
    {
        Require(4);
        var tag = string.Create(4, (Data, Position), static (span, s) =>
        {
            for (var i = 0; i < 4; i++)
            {
                span[i] = (char)s.Data[s.Position + i];
            }
        });
        Position += 4;
        return tag;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Require(count);
        var span = Data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count)
    {
        Require(count);
        Position += count;
    }

    /// <summary>WOFF2 variable-length unsigned integer (UIntBase128).</summary>
    public uint ReadUIntBase128()
    {
        uint result = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = ReadByte();
            if (i == 0 && b == 0x80)
            {
                throw new FontFormatException("Invalid UIntBase128: leading zero byte.");
            }

            if ((result & 0xFE000000) != 0)
            {
                throw new FontFormatException("Invalid UIntBase128: value overflows 32 bits.");
            }

            result = (result << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new FontFormatException("Invalid UIntBase128: more than 5 bytes.");
    }

    /// <summary>WOFF2 variable-length 16-bit integer (255UInt16).</summary>
    public ushort Read255UInt16()
    {
        var code = ReadByte();
        return code switch
        {
            253 => ReadUInt16(),
            255 => (ushort)(ReadByte() + 253),
            254 => (ushort)(ReadByte() + (253 * 2)),
            _ => code,
        };
    }

    private void Require(int count)
    {
        if (count < 0 || Position < 0 || Position > Data.Length - count)
        {
            throw new FontFormatException("Unexpected end of font data.");
        }
    }
}

/// <summary>Thrown when font data is malformed or uses an unsupported format.</summary>
public sealed class FontFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public FontFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public FontFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public FontFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
