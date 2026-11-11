using System.Globalization;
using System.Text;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf.Reading;

/// <summary>The kinds of token in PDF syntax (ISO 32000-2, section 7.2).</summary>
internal enum TokenKind
{
    /// <summary>The end of the data.</summary>
    End,

    /// <summary>An integer, such as <c>42</c> or <c>-7</c>.</summary>
    Integer,

    /// <summary>A real number, such as <c>3.14</c> or <c>-.5</c>.</summary>
    Real,

    /// <summary>A name, such as <c>/Type</c>.</summary>
    Name,

    /// <summary>A literal string in parentheses.</summary>
    LiteralString,

    /// <summary>A hexadecimal string in angle brackets.</summary>
    HexString,

    /// <summary><c>[</c>.</summary>
    ArrayStart,

    /// <summary><c>]</c>.</summary>
    ArrayEnd,

    /// <summary><c>&lt;&lt;</c>.</summary>
    DictionaryStart,

    /// <summary><c>&gt;&gt;</c>.</summary>
    DictionaryEnd,

    /// <summary>Any other run of regular characters: <c>obj</c>, <c>R</c>, <c>true</c>, <c>stream</c>, ...</summary>
    Keyword,

    /// <summary>A stray delimiter, such as an unmatched <c>)</c>, <c>&gt;</c> or a brace.</summary>
    Other,
}

/// <summary>A token: its kind and where it is in the data (<see cref="End"/> is exclusive).</summary>
internal readonly record struct Token(TokenKind Kind, int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// Splits PDF bytes into tokens. Tokens are positions in the data; their values are decoded on demand. Malformed
/// syntax never throws: an unterminated string runs to the end of the data, and stray delimiters are
/// <see cref="TokenKind.Other"/> tokens.
/// </summary>
internal sealed class PdfLexer(byte[] data)
{
    public byte[] Data => data;

    /// <summary>The position of the next byte to read.</summary>
    public int Position { get; set; }

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) => PdfSyntax.IsDelimiter(b);

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    /// <summary>Skips whitespace and comments.</summary>
    public void SkipWhitespace()
    {
        while (Position < data.Length)
        {
            var b = data[Position];
            if (IsWhitespace(b))
            {
                Position++;
            }
            else if (b == '%')
            {
                while (Position < data.Length && data[Position] is not ((byte)'\r' or (byte)'\n'))
                {
                    Position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Reads the next token.</summary>
    public Token Next()
    {
        SkipWhitespace();
        var start = Position;
        if (start >= data.Length)
        {
            return new Token(TokenKind.End, start, start);
        }

        var b = data[start];
        switch (b)
        {
            case (byte)'[':
                Position++;
                return new Token(TokenKind.ArrayStart, start, Position);
            case (byte)']':
                Position++;
                return new Token(TokenKind.ArrayEnd, start, Position);
            case (byte)'<' when start + 1 < data.Length && data[start + 1] == '<':
                Position += 2;
                return new Token(TokenKind.DictionaryStart, start, Position);
            case (byte)'>' when start + 1 < data.Length && data[start + 1] == '>':
                Position += 2;
                return new Token(TokenKind.DictionaryEnd, start, Position);
            case (byte)'<':
                var close = data.AsSpan(start + 1).IndexOf((byte)'>');
                Position = close < 0 ? data.Length : start + 1 + close + 1;
                return new Token(TokenKind.HexString, start, Position);
            case (byte)'(':
                Position = FindLiteralEnd(start + 1);
                return new Token(TokenKind.LiteralString, start, Position);
            case (byte)'/':
                Position++;
                while (Position < data.Length && IsRegular(data[Position]))
                {
                    Position++;
                }

                return new Token(TokenKind.Name, start, Position);
            case (byte)')' or (byte)'>' or (byte)'{' or (byte)'}' or (byte)'%':
                Position++;
                return new Token(TokenKind.Other, start, Position);
        }

        while (Position < data.Length && IsRegular(data[Position]))
        {
            Position++;
        }

        return new Token(ClassifyRegular(data.AsSpan(start, Position - start)), start, Position);
    }

    /// <summary>Reads the next token without consuming it.</summary>
    public Token Peek()
    {
        var position = Position;
        var token = Next();
        Position = position;
        return token;
    }

    /// <summary>Whether the token is the keyword <paramref name="keyword"/>.</summary>
    public bool IsKeyword(Token token, ReadOnlySpan<byte> keyword) =>
        token.Kind == TokenKind.Keyword && data.AsSpan(token.Start, token.Length).SequenceEqual(keyword);

    /// <summary>The value of an integer token, or <c>null</c> when it is out of range.</summary>
    public long? GetInteger(Token token) =>
        long.TryParse(data.AsSpan(token.Start, token.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>The value of a number token (integer or real); malformed numbers such as <c>1.2.3</c> are 0.</summary>
    public double GetReal(Token token)
    {
        var span = data.AsSpan(token.Start, token.Length);

        // PDF numbers have no exponent; tolerate the odd doubled sign ("--5") that some writers produce.
        while (span.Length > 1 && span[0] is (byte)'+' or (byte)'-' && span[1] is (byte)'+' or (byte)'-')
        {
            span = span[1..];
        }

        return double.TryParse(span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
                ? value
                : 0;
    }

    /// <summary>Whether a number token is in plain PDF number syntax (and so may be written back as it is).</summary>
    public bool IsWellFormedNumber(Token token)
    {
        var span = data.AsSpan(token.Start, token.Length);
        if (span.Length > 32)
        {
            return false;
        }

        if (span.Length > 0 && span[0] is (byte)'+' or (byte)'-')
        {
            span = span[1..];
        }

        var digits = 0;
        var points = 0;
        foreach (var b in span)
        {
            if (b == '.')
            {
                points++;
            }
            else if (b is >= (byte)'0' and <= (byte)'9')
            {
                digits++;
            }
            else
            {
                return false;
            }
        }

        return digits > 0 && points <= 1;
    }

    /// <summary>The bytes of a name token, without the slash and with <c>#xx</c> escapes decoded.</summary>
    public byte[] GetName(Token token)
    {
        var span = data.AsSpan(token.Start + 1, token.Length - 1);
        if (!span.Contains((byte)'#'))
        {
            return span.ToArray();
        }

        var bytes = new List<byte>(span.Length);
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == '#' && i + 2 < span.Length && TryHex(span[i + 1], out var high) && TryHex(span[i + 2], out var low))
            {
                bytes.Add((byte)((high << 4) | low));
                i += 2;
            }
            else
            {
                bytes.Add(span[i]);
            }
        }

        return [.. bytes];
    }

    /// <summary>The bytes of a literal string token, with escapes and line ends decoded (ISO 32000-2, 7.3.4.2).</summary>
    public byte[] GetLiteralString(Token token)
    {
        var end = token.End;
        if (end > token.Start + 1 && data[end - 1] == ')')
        {
            end--; // A string cut off by the end of the data has no closing parenthesis.
        }

        var bytes = new List<byte>(end - token.Start);
        for (var i = token.Start + 1; i < end; i++)
        {
            var b = data[i];
            if (b == '\r')
            {
                // An end-of-line marker in a string is a line feed, whatever it was in the file.
                bytes.Add((byte)'\n');
                if (i + 1 < end && data[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            if (b != '\\')
            {
                bytes.Add(b);
                continue;
            }

            if (++i >= end)
            {
                break;
            }

            b = data[i];
            switch (b)
            {
                case (byte)'n': bytes.Add((byte)'\n'); break;
                case (byte)'r': bytes.Add((byte)'\r'); break;
                case (byte)'t': bytes.Add((byte)'\t'); break;
                case (byte)'b': bytes.Add((byte)'\b'); break;
                case (byte)'f': bytes.Add((byte)'\f'); break;
                case (byte)'\r':
                    // A backslash at the end of a line continues the string on the next line.
                    if (i + 1 < end && data[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case (byte)'\n':
                    break;
                case >= (byte)'0' and <= (byte)'7':
                    var value = b - '0';
                    for (var digits = 1; digits < 3 && i + 1 < end && data[i + 1] is >= (byte)'0' and <= (byte)'7'; digits++)
                    {
                        value = (value * 8) + (data[++i] - '0');
                    }

                    bytes.Add((byte)value);
                    break;
                default:
                    bytes.Add(b); // \( \) \\ and unknown escapes stand for the character itself.
                    break;
            }
        }

        return [.. bytes];
    }

    /// <summary>The bytes of a hexadecimal string token; whitespace and invalid characters are ignored, and an odd final digit is padded with 0.</summary>
    public byte[] GetHexString(Token token)
    {
        var bytes = new List<byte>(token.Length / 2);
        var high = -1;
        for (var i = token.Start + 1; i < token.End; i++)
        {
            if (!TryHex(data[i], out var digit))
            {
                continue;
            }

            if (high < 0)
            {
                high = digit;
            }
            else
            {
                bytes.Add((byte)((high << 4) | digit));
                high = -1;
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return [.. bytes];
    }

    /// <summary>The text of a token, for messages.</summary>
    public string GetText(Token token) => Encoding.Latin1.GetString(data, token.Start, Math.Min(token.Length, 40));

    private static TokenKind ClassifyRegular(ReadOnlySpan<byte> run)
    {
        var digits = false;
        var point = false;
        for (var i = 0; i < run.Length; i++)
        {
            var b = run[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                digits = true;
            }
            else if (b == '.')
            {
                point = true;
            }
            else if (b is not ((byte)'+' or (byte)'-') || (i > 0 && run[i - 1] is not ((byte)'+' or (byte)'-')))
            {
                return TokenKind.Keyword; // Signs only at the start (one or two of them).
            }
        }

        return !digits ? TokenKind.Keyword : point ? TokenKind.Real : TokenKind.Integer;
    }

    /// <summary>The position after the parenthesis that closes a literal string starting at <paramref name="position"/>.</summary>
    private int FindLiteralEnd(int position)
    {
        var depth = 1;
        while (position < data.Length)
        {
            switch (data[position++])
            {
                case (byte)'\\':
                    position++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return position;
                    }

                    break;
            }
        }

        return data.Length;
    }

    internal static bool TryHex(byte b, out int value)
    {
        value = b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => -1,
        };
        return value >= 0;
    }
}
