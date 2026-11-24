using System.Buffers.Binary;
using System.Text;
using RaiCore.Pdf.Sheets.Xlsx;

namespace RaiCore.Pdf.Sheets.Xls;

/// <summary>
/// Reads the records of a BIFF stream ([MS-XLS] 2.1.4): a type, a length and that many bytes each, from an offset to
/// the end of the stream. The CONTINUE records that follow a record are appended to its data, with the offsets where
/// they begin kept in <see cref="Breaks"/> (a string split there starts again with its flags). A record that runs past
/// the end of the stream is cut there, and is the last.
/// </summary>
internal sealed class BiffReader
{
    /// <summary>The type of a CONTINUE record.</summary>
    public const ushort Continue = 0x003C;

    private readonly byte[] _stream;
    private readonly List<int> _breaks = [];
    private byte[] _joined = [];
    private int _next;
    private int _start;
    private int _length;
    private bool _isJoined;

    public BiffReader(byte[] stream, int offset = 0)
    {
        _stream = stream;
        _next = Math.Clamp(offset, 0, stream.Length);
    }

    /// <summary>The offset of the current record in the stream.</summary>
    public int Position { get; private set; }

    /// <summary>The offset of the record after the current one (and its CONTINUE records).</summary>
    public int NextPosition => _next;

    /// <summary>The current record's type.</summary>
    public ushort Id { get; private set; }

    /// <summary>The current record's data, with its CONTINUE records' data appended.</summary>
    public ReadOnlySpan<byte> Data => _isJoined ? _joined.AsSpan(0, _length) : _stream.AsSpan(_start, _length);

    /// <summary>The offsets in <see cref="Data"/> where a CONTINUE record's data begins, in order.</summary>
    public IReadOnlyList<int> Breaks => _breaks;

    /// <summary>Moves to the next record; <c>false</c> at the end of the stream.</summary>
    public bool Next()
    {
        _breaks.Clear();
        _isJoined = false;
        if (_stream.Length - _next < 4)
        {
            _next = _stream.Length;
            return false;
        }

        Position = _next;
        Id = BinaryPrimitives.ReadUInt16LittleEndian(_stream.AsSpan(_next));
        var length = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(_stream.AsSpan(_next + 2)), _stream.Length - _next - 4);
        _start = _next + 4;
        _length = length;
        _next = _start + length;
        while (_stream.Length - _next >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(_stream.AsSpan(_next)) == Continue)
        {
            var more = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(_stream.AsSpan(_next + 2)), _stream.Length - _next - 4);
            if (!_isJoined)
            {
                if (_joined.Length < _length + more)
                {
                    _joined = new byte[Math.Max(_length + more, Math.Min(_joined.Length * 2, int.MaxValue / 2))];
                }

                _stream.AsSpan(_start, _length).CopyTo(_joined);
                _isJoined = true;
            }
            else if (_joined.Length < _length + more)
            {
                Array.Resize(ref _joined, Math.Max(_length + more, _joined.Length * 2));
            }

            _breaks.Add(_length);
            _stream.AsSpan(_next + 4, more).CopyTo(_joined.AsSpan(_length));
            _length += more;
            _next += 4 + more;
        }

        return true;
    }

    /// <summary>A 16-bit value at an offset of the data, or 0 past its end.</summary>
    public static int U16(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : 0;

    /// <summary>A 32-bit value at an offset of the data, or 0 past its end.</summary>
    public static uint U32(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : 0;

    /// <summary>
    /// Reads a string with its length before it (one or two bytes, <paramref name="lengthSize"/>): in BIFF8 a Unicode
    /// string with its flags (<see cref="ReadUnicode"/>), before it characters in the workbook's code page.
    /// </summary>
    public static string ReadString(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks, ref int position, int lengthSize, Encoding? codePage, bool extended = false,
        List<(int First, int Font)>? runs = null)
    {
        if (position < 0 || position + lengthSize > data.Length)
        {
            position = data.Length;
            return string.Empty;
        }

        var count = lengthSize == 1 ? data[position] : U16(data, position);
        position += lengthSize;
        if (codePage is not null)
        {
            var bytes = Math.Min(count, data.Length - position);
            var text = codePage.GetString(data.Slice(position, bytes));
            position += bytes;
            return Clean(text);
        }

        return ReadUnicode(data, breaks, ref position, count, extended, runs);
    }

    /// <summary>
    /// Reads <paramref name="count"/> characters of a BIFF8 Unicode string from its flags byte on ([MS-XLS] 2.5.293 and
    /// 2.5.294): one byte a character (the low bytes of UTF-16) or two. Where the characters run on into a CONTINUE
    /// record, it starts with a new flags byte. An extended string's phonetic data are skipped, and its formatting runs
    /// (the first character and the font index of each, [MS-XLS] 2.5.129) added to <paramref name="runs"/> if given. A
    /// string cut short by the end of the data is read as far as it goes.
    /// </summary>
    public static string ReadUnicode(ReadOnlySpan<byte> data, IReadOnlyList<int> breaks, ref int position, int count, bool extended = false,
        List<(int First, int Font)>? runs = null)
    {
        if (position < 0 || position >= data.Length)
        {
            position = data.Length;
            return string.Empty;
        }

        var flags = data[position++];
        var high = (flags & 0x01) != 0;
        long skip = 0;
        var runCount = 0;
        if (extended && (flags & 0x08) != 0)
        {
            runCount = U16(data, position);
            skip += 4L * runCount;
            position += 2;
        }

        if (extended && (flags & 0x04) != 0)
        {
            skip += U32(data, position);
            position += 4;
        }

        position = Math.Min(position, data.Length);
        var chars = count <= 256 ? stackalloc char[count] : new char[count];
        var read = 0;
        var next = 0;
        while (next < breaks.Count && breaks[next] < position)
        {
            next++;
        }

        while (read < count && position < data.Length)
        {
            var end = next < breaks.Count ? breaks[next] : data.Length;
            if (position == end)
            {
                // The characters go on in a CONTINUE record, which starts with their flags again.
                high = (data[position++] & 0x01) != 0;
                next++;
                continue;
            }

            var available = high ? (end - position) / 2 : end - position;
            if (available == 0)
            {
                position = end; // Half a character before a break: damaged.
                continue;
            }

            var take = Math.Min(available, count - read);
            for (var i = 0; i < take; i++)
            {
                chars[read++] = high ? (char)(data[position] | (data[position + 1] << 8)) : (char)data[position];
                position += high ? 2 : 1;
            }
        }

        if (runs is not null)
        {
            // The runs follow the characters (without flags where they run on into a CONTINUE record).
            for (var i = 0; i < runCount && position + (4 * i) + 4 <= data.Length; i++)
            {
                runs.Add((U16(data, position + (4 * i)), U16(data, position + (4 * i) + 2)));
            }
        }

        position = (int)Math.Min(position + skip, data.Length);
        return Clean(new string(chars[..read]));
    }

    /// <summary>
    /// The text without the NUL characters some writers leave in strings, and with the halves of surrogate pairs that
    /// are alone (a string cut short, damage) replaced.
    /// </summary>
    private static string Clean(string text) => XlsxWorkbook.WithoutLoneSurrogates(
        text.Contains('\0', StringComparison.Ordinal) ? text.Replace("\0", string.Empty, StringComparison.Ordinal) : text);
}
