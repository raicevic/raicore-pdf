using System.Globalization;

namespace RaiCore.Pdf.Svg;

/// <summary>
/// Reads numbers and flags from SVG attribute syntax (path data, point lists, transforms, view boxes), where
/// separators are optional: <c>1.5.5</c> is 1.5 and .5, <c>-1-2</c> is -1 and -2.
/// </summary>
internal ref struct SvgScanner
{
    private readonly ReadOnlySpan<char> _text;
    private int _position;

    public SvgScanner(ReadOnlySpan<char> text) => _text = text;

    /// <summary>Whether only whitespace is left.</summary>
    public bool AtEnd
    {
        get
        {
            SkipWhitespace();
            return _position >= _text.Length;
        }
    }

    /// <summary>The next character after whitespace, or <c>'\0'</c> at the end.</summary>
    public char Peek()
    {
        SkipWhitespace();
        return _position < _text.Length ? _text[_position] : '\0';
    }

    /// <summary>Consumes the next character (after whitespace).</summary>
    public char Read()
    {
        var c = Peek();
        if (_position < _text.Length)
        {
            _position++;
        }

        return c;
    }

    /// <summary>Skips whitespace and at most one comma.</summary>
    public void SkipSeparator()
    {
        SkipWhitespace();
        if (_position < _text.Length && _text[_position] == ',')
        {
            _position++;
            SkipWhitespace();
        }
    }

    /// <summary>Whether a number starts at the next position (after a separator).</summary>
    public bool NextIsNumber()
    {
        var saved = _position;
        SkipSeparator();
        var result = _position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] is '+' or '-' or '.');
        _position = saved;
        return result;
    }

    /// <summary>Reads a number, skipping a leading separator. Fails (without consuming) when there is none.</summary>
    public bool TryReadNumber(out double value)
    {
        var saved = _position;
        SkipSeparator();
        var start = _position;
        var i = _position;
        if (i < _text.Length && _text[i] is '+' or '-')
        {
            i++;
        }

        var digits = 0;
        while (i < _text.Length && char.IsAsciiDigit(_text[i]))
        {
            i++;
            digits++;
        }

        if (i < _text.Length && _text[i] == '.')
        {
            i++;
            while (i < _text.Length && char.IsAsciiDigit(_text[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            _position = saved;
            value = 0;
            return false;
        }

        // An exponent only counts when digits follow; "1em" is the number 1 and the unit "em".
        if (i < _text.Length && _text[i] is 'e' or 'E')
        {
            var j = i + 1;
            if (j < _text.Length && _text[j] is '+' or '-')
            {
                j++;
            }

            if (j < _text.Length && char.IsAsciiDigit(_text[j]))
            {
                while (j < _text.Length && char.IsAsciiDigit(_text[j]))
                {
                    j++;
                }

                i = j;
            }
        }

        if (!double.TryParse(_text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            _position = saved;
            value = 0;
            return false;
        }

        _position = i;
        return true;
    }

    /// <summary>Reads an arc flag (<c>0</c> or <c>1</c>, which need no separator after them).</summary>
    public bool TryReadFlag(out bool flag)
    {
        var saved = _position;
        SkipSeparator();
        if (_position < _text.Length && _text[_position] is '0' or '1')
        {
            flag = _text[_position] == '1';
            _position++;
            return true;
        }

        _position = saved;
        flag = false;
        return false;
    }

    /// <summary>Reads an identifier made of letters (e.g. a transform function name).</summary>
    public ReadOnlySpan<char> ReadName()
    {
        SkipWhitespace();
        var start = _position;
        while (_position < _text.Length && (char.IsAsciiLetter(_text[_position]) || _text[_position] == '%'))
        {
            _position++;
        }

        return _text[start.._position];
    }

    /// <summary>Reads all numbers in a list such as <c>points</c> or <c>stroke-dasharray</c>; stops at the first non-number.</summary>
    public static List<double> ReadNumbers(string? text)
    {
        var numbers = new List<double>();
        var scanner = new SvgScanner(text);
        while (scanner.TryReadNumber(out var value))
        {
            numbers.Add(value);
        }

        return numbers;
    }

    private void SkipWhitespace()
    {
        while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\n' or '\r' or '\f')
        {
            _position++;
        }
    }
}
