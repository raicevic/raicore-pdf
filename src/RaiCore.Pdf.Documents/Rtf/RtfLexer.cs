namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>The kinds of <see cref="RtfToken"/>.</summary>
internal enum RtfTokenKind
{
    /// <summary>The end of the data.</summary>
    End,

    /// <summary><c>{</c>.</summary>
    GroupStart,

    /// <summary><c>}</c>.</summary>
    GroupEnd,

    /// <summary>A control word (<c>\b0</c>) with its optional parameter.</summary>
    ControlWord,

    /// <summary>A control symbol (<c>\~</c>, <c>\*</c>): a backslash and one character that is not a letter.</summary>
    ControlSymbol,

    /// <summary>A byte in hexadecimal (<c>\'e9</c>).</summary>
    HexByte,

    /// <summary>Plain text: bytes up to the next backslash, brace, line end or tab.</summary>
    Text,

    /// <summary>The data of <c>\binN</c>.</summary>
    Binary,
}

/// <summary>A token of RTF: its kind, and the word, parameter, byte or span of data it carries.</summary>
internal readonly record struct RtfToken(RtfTokenKind Kind, string Word = "", int Parameter = 0, bool HasParameter = false, int Start = 0, int Length = 0)
{
    /// <summary>The symbol of a <see cref="RtfTokenKind.ControlSymbol"/>, or the byte of a <see cref="RtfTokenKind.HexByte"/>.</summary>
    public int Value => Parameter;
}

/// <summary>
/// Splits RTF into tokens ([MS-RTF] 1.9.1 section 2): group braces, control words with their parameters (a word of up
/// to 32 letters, a parameter of up to 10 digits, clamped to <see cref="int"/>), control symbols, hexadecimal bytes,
/// text and the data of <c>\bin</c>. It allocates no strings for control words it has seen before (they are interned
/// in a bounded table). Line ends in text are ignored, as RTF says.
/// </summary>
internal sealed class RtfLexer
{
    /// <summary>The most distinct control words that are interned; others are allocated each time.</summary>
    private const int MaxInterned = 4096;

    private readonly byte[] _data;
    private readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private int _position;

    /// <summary>A lexer for data from a position (after a byte order mark and white space).</summary>
    public RtfLexer(byte[] data, int start = 0)
    {
        _data = data;
        _position = start;
        _lookup = _words.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The data.</summary>
    public byte[] Data => _data;

    /// <summary>The next token, or one of kind <see cref="RtfTokenKind.End"/>.</summary>
    public RtfToken Next()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];
            switch (b)
            {
                case (byte)'{':
                    _position++;
                    return new RtfToken(RtfTokenKind.GroupStart);
                case (byte)'}':
                    _position++;
                    return new RtfToken(RtfTokenKind.GroupEnd);
                case (byte)'\r':
                case (byte)'\n':
                case 0:
                    _position++;
                    continue;
                case (byte)'\t':
                    _position++;
                    return new RtfToken(RtfTokenKind.ControlWord, "tab");
                case (byte)'\\':
                    return Control();
                default:
                    var start = _position;
                    while (_position < _data.Length && _data[_position] is not ((byte)'\\' or (byte)'{' or (byte)'}' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0))
                    {
                        _position++;
                    }

                    return new RtfToken(RtfTokenKind.Text, Start: start, Length: _position - start);
            }
        }

        return new RtfToken(RtfTokenKind.End);
    }

    /// <summary>A control word, symbol or hexadecimal byte, at a backslash.</summary>
    private RtfToken Control()
    {
        _position++; // The backslash.
        if (_position >= _data.Length)
        {
            return new RtfToken(RtfTokenKind.End);
        }

        var c = _data[_position];
        if (!IsLetter(c))
        {
            _position++;
            if (c == '\'')
            {
                var high = _position < _data.Length ? Hex(_data[_position]) : -1;
                var low = _position + 1 < _data.Length ? Hex(_data[_position + 1]) : -1;
                if (high >= 0 && low >= 0)
                {
                    _position += 2;
                    return new RtfToken(RtfTokenKind.HexByte, Parameter: (high << 4) | low);
                }

                return new RtfToken(RtfTokenKind.ControlSymbol, Parameter: c); // A broken escape, which the reader leaves out.
            }

            if (c is (byte)'\r' or (byte)'\n')
            {
                return new RtfToken(RtfTokenKind.ControlWord, "par"); // A backslash before a line end is a paragraph mark.
            }

            return new RtfToken(RtfTokenKind.ControlSymbol, Parameter: c);
        }

        Span<char> name = stackalloc char[32];
        var length = 0;
        while (_position < _data.Length && IsLetter(_data[_position]))
        {
            if (length < name.Length)
            {
                name[length++] = (char)_data[_position];
            }

            _position++;
        }

        var negative = false;
        if (_position + 1 < _data.Length && _data[_position] == '-' && IsDigit(_data[_position + 1]))
        {
            negative = true;
            _position++;
        }

        long value = 0;
        var digits = 0;
        while (_position < _data.Length && IsDigit(_data[_position]))
        {
            if (digits < 10)
            {
                value = (value * 10) + (_data[_position] - '0');
            }

            digits++;
            _position++;
        }

        if (_position < _data.Length && _data[_position] == ' ')
        {
            _position++; // The space that ends a control word is part of it.
        }

        var parameter = (int)Math.Clamp(negative ? -value : value, int.MinValue, int.MaxValue);
        var word = Intern(name[..length]);
        if (word == "bin" && digits > 0)
        {
            // Binary data: the parameter's bytes, whatever they are.
            var count = (int)Math.Min(Math.Max(0, parameter), _data.Length - _position);
            var start = _position;
            _position += count;
            return new RtfToken(RtfTokenKind.Binary, word, parameter, true, start, count);
        }

        return new RtfToken(RtfTokenKind.ControlWord, word, parameter, digits > 0);
    }

    private string Intern(ReadOnlySpan<char> name)
    {
        if (_lookup.TryGetValue(name, out var word))
        {
            return word;
        }

        word = name.ToString();
        if (_words.Count < MaxInterned)
        {
            _words[word] = word;
        }

        return word;
    }

    private static bool IsLetter(byte b) => b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z');

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>The value of a hexadecimal digit, or -1.</summary>
    internal static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
