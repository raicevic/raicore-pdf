using System.Text;
using RaiCore.Pdf.IO;

namespace RaiCore.Pdf.Objects;

/// <summary>Base type for the low-level PDF object model (ISO 32000-2, section 7.3).</summary>
public abstract class PdfObject
{
    internal abstract void WriteTo(PdfOutput output);

    /// <summary>Serializes the object to its PDF syntax. Intended for diagnostics and tests.</summary>
    public override string ToString()
    {
        using var buffer = new MemoryStream();
        using (var output = new PdfOutput(buffer))
        {
            WriteTo(output);
        }

        return Encoding.Latin1.GetString(buffer.ToArray());
    }
}

/// <summary>The PDF <c>null</c> object.</summary>
public sealed class PdfNull : PdfObject
{
    /// <summary>The single instance.</summary>
    public static readonly PdfNull Instance = new();

    private PdfNull()
    {
    }

    internal override void WriteTo(PdfOutput output) => output.WriteAscii("null");
}

/// <summary>A PDF boolean.</summary>
public sealed class PdfBoolean : PdfObject
{
    /// <summary><c>true</c>.</summary>
    public static readonly PdfBoolean True = new(true);

    /// <summary><c>false</c>.</summary>
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <summary>Returns the shared instance for <paramref name="value"/>.</summary>
    public static PdfBoolean From(bool value) => value ? True : False;

    internal override void WriteTo(PdfOutput output) => output.WriteAscii(Value ? "true" : "false");
}

/// <summary>A PDF integer.</summary>
public sealed class PdfInteger(long value) : PdfObject
{
    /// <summary>The value.</summary>
    public long Value { get; } = value;

    internal override void WriteTo(PdfOutput output) => output.WriteInteger(Value);
}

/// <summary>A PDF real number.</summary>
public sealed class PdfReal(double value) : PdfObject
{
    /// <summary>The value.</summary>
    public double Value { get; } = value;

    internal override void WriteTo(PdfOutput output) => output.WriteReal(Value);
}

/// <summary>A PDF name, such as <c>/Type</c>. The value is stored without the leading slash.</summary>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    /// <summary>Creates a name. <paramref name="value"/> must not include the leading slash.</summary>
    public PdfName(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Value = value;
    }

    /// <summary>The name without the leading slash.</summary>
    public string Value { get; }

    /// <summary>Converts a string to a name.</summary>
    public static implicit operator PdfName(string value) => new(value);

    /// <summary>Creates a name from a string.</summary>
    public static PdfName FromString(string value) => new(value);

    internal override void WriteTo(PdfOutput output)
    {
        output.WriteByte((byte)'/');
        foreach (var b in Encoding.UTF8.GetBytes(Value))
        {
            if (b is < 0x21 or > 0x7E or (byte)'#' || PdfSyntax.IsDelimiter(b))
            {
                output.WriteAscii($"#{b:X2}");
            }
            else
            {
                output.WriteByte(b);
            }
        }
    }

    /// <inheritdoc />
    public bool Equals(PdfName? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PdfName);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>A PDF string. Holds raw bytes; use <see cref="FromText"/> for human-readable text.</summary>
public sealed class PdfString : PdfObject
{
    private readonly byte[] _bytes;

    /// <summary>Creates a string from raw bytes.</summary>
    public PdfString(byte[] bytes, bool hex = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes;
        IsHex = hex;
    }

    /// <summary>Whether the string is written in hexadecimal form (<c>&lt;...&gt;</c>).</summary>
    public bool IsHex { get; }

    /// <summary>The raw bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>
    /// Creates a text string (ISO 32000-2, 7.9.2.2): plain ASCII is stored as-is,
    /// anything else as UTF-16BE with a byte order mark.
    /// </summary>
    public static PdfString FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Ascii.IsValid(text))
        {
            return new PdfString(Encoding.ASCII.GetBytes(text));
        }

        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var bytes = new byte[utf16.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        utf16.CopyTo(bytes, 2);
        return new PdfString(bytes);
    }

    internal override void WriteTo(PdfOutput output)
    {
        if (IsHex)
        {
            output.WriteByte((byte)'<');
            output.WriteAscii(Convert.ToHexString(_bytes));
            output.WriteByte((byte)'>');
            return;
        }

        output.WriteByte((byte)'(');
        foreach (var b in _bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    output.WriteByte((byte)'\\');
                    output.WriteByte(b);
                    break;
                case < 0x20 or > 0x7E:
                    output.WriteAscii($"\\{Convert.ToString(b, 8).PadLeft(3, '0')}");
                    break;
                default:
                    output.WriteByte(b);
                    break;
            }
        }

        output.WriteByte((byte)')');
    }
}

/// <summary>A PDF array.</summary>
public sealed class PdfArray : PdfObject
{
    /// <summary>Creates an array with the given items.</summary>
    public PdfArray(params IEnumerable<PdfObject> items) => Items = [.. items];

    /// <summary>The items in the array.</summary>
    public IList<PdfObject> Items { get; }

    /// <summary>Creates an array of real numbers, e.g. for a rectangle.</summary>
    public static PdfArray OfNumbers(params ReadOnlySpan<double> values)
    {
        var array = new PdfArray();
        foreach (var value in values)
        {
            array.Items.Add(value == Math.Floor(value) && Math.Abs(value) < long.MaxValue
                ? new PdfInteger((long)value)
                : new PdfReal(value));
        }

        return array;
    }

    internal override void WriteTo(PdfOutput output)
    {
        output.WriteByte((byte)'[');
        for (var i = 0; i < Items.Count; i++)
        {
            if (i > 0)
            {
                output.WriteByte((byte)' ');
            }

            Items[i].WriteTo(output);
        }

        output.WriteByte((byte)']');
    }
}

/// <summary>A PDF dictionary. Keys keep their insertion order so output is deterministic.</summary>
public class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = [];

    /// <summary>Number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>The entries in insertion order.</summary>
    public IEnumerable<KeyValuePair<PdfName, PdfObject>> Entries => _entries;

    /// <summary>Gets or sets an entry. Setting a value replaces an existing entry with the same key.</summary>
    public PdfObject? this[PdfName key]
    {
        get => _entries.Find(e => e.Key.Equals(key)).Value;
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            var index = _entries.FindIndex(e => e.Key.Equals(key));
            if (value is null)
            {
                if (index >= 0)
                {
                    _entries.RemoveAt(index);
                }
            }
            else if (index >= 0)
            {
                _entries[index] = new(key, value);
            }
            else
            {
                _entries.Add(new(key, value));
            }
        }
    }

    internal override void WriteTo(PdfOutput output)
    {
        output.WriteAscii("<<");
        foreach (var (key, value) in _entries)
        {
            key.WriteTo(output);
            output.WriteByte((byte)' ');
            value.WriteTo(output);
        }

        output.WriteAscii(">>");
    }
}

/// <summary>A reference to an indirect object, such as <c>12 0 R</c>.</summary>
public sealed class PdfReference : PdfObject
{
    internal PdfReference(int objectNumber, int generation = 0)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    /// <summary>The object number.</summary>
    public int ObjectNumber { get; }

    /// <summary>The generation number.</summary>
    public int Generation { get; }

    internal override void WriteTo(PdfOutput output)
    {
        output.WriteInteger(ObjectNumber);
        output.WriteByte((byte)' ');
        output.WriteInteger(Generation);
        output.WriteAscii(" R");
    }
}

/// <summary>A PDF stream: a dictionary followed by binary data. Must be written as an indirect object.</summary>
public sealed class PdfStreamObject : PdfObject
{
    /// <summary>Creates a stream holding <paramref name="data"/> as-is (no filter).</summary>
    public PdfStreamObject(byte[] data, PdfDictionary? dictionary = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
        Dictionary = dictionary ?? new PdfDictionary();
    }

    /// <summary>The stream dictionary. <c>/Length</c> is written automatically.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The (possibly encoded) stream data.</summary>
    public byte[] Data { get; }

    /// <summary>Creates a stream with <paramref name="data"/> compressed using <c>/FlateDecode</c>.</summary>
    public static PdfStreamObject CreateCompressed(ReadOnlySpan<byte> data, PdfDictionary? dictionary = null)
    {
        var stream = new PdfStreamObject(Filters.Flate.Encode(data), dictionary);
        stream.Dictionary["Filter"] = new PdfName("FlateDecode");
        return stream;
    }

    internal override void WriteTo(PdfOutput output)
    {
        Dictionary["Length"] = new PdfInteger(Data.Length);
        Dictionary.WriteTo(output);
        output.WriteAscii("\nstream\n");
        output.Write(Data);
        output.WriteAscii("\nendstream");
    }
}

internal static class PdfSyntax
{
    public static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
