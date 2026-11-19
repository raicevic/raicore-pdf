using System.Buffers.Binary;
using System.Text;

namespace RaiCore.Pdf.Email.Cfb;

/// <summary>
/// A property set stream ([MS-OLEPS]), such as <c>\u0005SummaryInformation</c> (title, author, dates) and
/// <c>\u0005DocumentSummaryInformation</c> (company, category): one or two property sets of typed values by id.
/// </summary>
/// <remarks>
/// Values are read as strings (<c>VT_LPSTR</c> in the set's code page, <c>VT_LPWSTR</c>), integers (<c>VT_I2</c>,
/// <c>VT_I4</c>, <c>VT_UI2</c>, <c>VT_UI4</c>, <c>VT_I8</c>, <c>VT_UI8</c>), floating point (<c>VT_R4</c>,
/// <c>VT_R8</c>), <see cref="bool"/> (<c>VT_BOOL</c>), <see cref="DateTime"/> in UTC (<c>VT_FILETIME</c>), bytes
/// (<c>VT_BLOB</c>) and <see cref="Guid"/> (<c>VT_CLSID</c>); other types (vectors, variants, the dictionary) are left
/// out. Values that point outside their set are left out; a stream that is not a property set throws
/// <see cref="PdfFormatException"/>.
/// </remarks>
internal static class PropertySetStream
{
    /// <summary>The format id of the summary information property set.</summary>
    public static readonly Guid SummaryInformation = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    /// <summary>The format id of the document summary information property set.</summary>
    public static readonly Guid DocumentSummaryInformation = new("D5CDD502-2E9C-101B-9397-08002B2CF9AE");

    // Property ids of the summary information ([MS-OLEPS] 2.25.1).
    public const uint CodePage = 1;
    public const uint Title = 2;
    public const uint Subject = 3;
    public const uint Author = 4;
    public const uint Keywords = 5;
    public const uint Comments = 6;
    public const uint Created = 12;
    public const uint LastSaved = 13;

    /// <summary>The largest number of properties read from one set; more is damage, not a document.</summary>
    private const int MaxProperties = 4096;

    /// <summary>Reads the property sets of a stream, by format id.</summary>
    /// <exception cref="PdfFormatException">The data is not a property set stream.</exception>
    public static IReadOnlyList<(Guid FormatId, IReadOnlyDictionary<uint, object> Properties)> Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0xFFFE)
        {
            throw new PdfFormatException("The stream is not a property set stream (no byte order mark).");
        }

        var setCount = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        if (setCount is 0 or > 2 || data.Length < 28 + (20 * setCount))
        {
            throw new PdfFormatException($"The property set stream holds {setCount} sets; it can hold one or two.");
        }

        var sets = new List<(Guid, IReadOnlyDictionary<uint, object>)>();
        for (var i = 0; i < setCount; i++)
        {
            var entry = data.Slice(28 + (20 * i), 20);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            if (offset > data.Length - 8)
            {
                throw new PdfFormatException("A property set lies outside its stream.");
            }

            sets.Add((new Guid(entry[..16]), ReadSet(data[(int)offset..])));
        }

        return sets;
    }

    /// <summary>The summary information of a stream (the first set with its format id), or an empty dictionary.</summary>
    public static IReadOnlyDictionary<uint, object> Summary(ReadOnlySpan<byte> data) =>
        Read(data).FirstOrDefault(s => s.FormatId == SummaryInformation).Properties ?? new Dictionary<uint, object>();

    private static Dictionary<uint, object> ReadSet(ReadOnlySpan<byte> set)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(set);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(set[4..]);
        set = set[..(int)Math.Min(size is >= 8 ? size : (uint)set.Length, (uint)set.Length)];
        if (count > MaxProperties || 8 + (8L * count) > set.Length)
        {
            throw new PdfFormatException($"The property set claims {count} properties, more than it holds.");
        }

        var entries = new (uint Id, uint Offset)[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = (BinaryPrimitives.ReadUInt32LittleEndian(set[(8 + (8 * i))..]), BinaryPrimitives.ReadUInt32LittleEndian(set[(12 + (8 * i))..]));
        }

        // The code page first: it says how the 8-bit strings are encoded.
        var codePage = 1252;
        foreach (var (id, offset) in entries)
        {
            if (id == CodePage && Value(set, offset, 1252) is short or int)
            {
                codePage = Convert.ToInt32(Value(set, offset, 1252), System.Globalization.CultureInfo.InvariantCulture) & 0xFFFF;
            }
        }

        var properties = new Dictionary<uint, object>();
        foreach (var (id, offset) in entries)
        {
            if (id != 0 && Value(set, offset, codePage) is { } value)
            {
                properties[id] = value;
            }
        }

        return properties;
    }

    /// <summary>A typed value ([MS-OLEPS] 2.15), or <c>null</c> when it is of another type or does not fit in the set.</summary>
    private static object? Value(ReadOnlySpan<byte> set, uint offset, int codePage)
    {
        if (offset > set.Length - 4)
        {
            return null;
        }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(set[(int)offset..]);
        var value = set[((int)offset + 4)..];
        return type switch
        {
            0x02 when value.Length >= 2 => BinaryPrimitives.ReadInt16LittleEndian(value),
            0x03 when value.Length >= 4 => BinaryPrimitives.ReadInt32LittleEndian(value),
            0x04 when value.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(value),
            0x05 when value.Length >= 8 => BinaryPrimitives.ReadDoubleLittleEndian(value),
            0x0B when value.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(value) != 0,
            0x12 when value.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(value),
            0x13 when value.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(value),
            0x14 when value.Length >= 8 => BinaryPrimitives.ReadInt64LittleEndian(value),
            0x15 when value.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(value),
            0x1E => String8(value, codePage),
            0x1F => String16(value),
            0x40 when value.Length >= 8 => FileTime(BinaryPrimitives.ReadInt64LittleEndian(value)),
            0x41 => Blob(value),
            0x48 when value.Length >= 16 => new Guid(value[..16]),
            _ => null,
        };
    }

    /// <summary>A <c>VT_LPSTR</c>: a byte count, then text in the code page (UTF-16 for code page 1200), ending at a null.</summary>
    private static string? String8(ReadOnlySpan<byte> value, int codePage)
    {
        if (value.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(value) is var length && length > value.Length - 4)
        {
            return null;
        }

        var bytes = value.Slice(4, (int)length);
        var text = codePage == 1200 ? System.Text.Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)])
            : (codePage == 65001 ? System.Text.Encoding.UTF8 : CodePageEncoding(codePage)).GetString(bytes);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    /// <summary>A <c>VT_LPWSTR</c>: a character count, then UTF-16 text, ending at a null.</summary>
    private static string? String16(ReadOnlySpan<byte> value)
    {
        if (value.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(value) is var length && length > (value.Length - 4) / 2)
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(value.Slice(4, (int)length * 2));
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : text;
    }

    private static byte[]? Blob(ReadOnlySpan<byte> value) =>
        value.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(value) is var length && length <= value.Length - 4
            ? value.Slice(4, (int)length).ToArray()
            : null;

    /// <summary>A FILETIME (100-nanosecond intervals since 1601) as a UTC time; zero and out-of-range values are left out.</summary>
    private static DateTime? FileTime(long ticks) =>
        ticks > 0 && ticks <= DateTime.MaxValue.Ticks - new DateTime(1601, 1, 1).Ticks ? DateTime.FromFileTimeUtc(ticks) : null;

    private static System.Text.Encoding CodePageEncoding(int codePage) =>
        (codePage is > 0 and < 65535 ? CodePagesEncodingProvider.Instance.GetEncoding(codePage) : null)
        ?? CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? System.Text.Encoding.Latin1;
}
