using System.Text;

namespace RaiCore.Pdf.Extraction.Text;

/// <summary>
/// Text that remembers, for each character, the text item it came from (<c>null</c> for spaces and line breaks put
/// in by the layout), so that a match in a page's text can be found on the page.
/// </summary>
internal sealed class MappedText
{
    private readonly StringBuilder _text = new();
    private readonly List<TextItem?> _sources = [];

    public int Length => _text.Length;

    public char this[int index] => _text[index];

    /// <summary>The item character <paramref name="index"/> came from, or <c>null</c>.</summary>
    public TextItem? SourceAt(int index) => _sources[index];

    public MappedText Append(string text, TextItem? source)
    {
        _text.Append(text);
        for (var i = 0; i < text.Length; i++)
        {
            _sources.Add(source);
        }

        return this;
    }

    public MappedText Append(char c, TextItem? source = null)
    {
        _text.Append(c);
        _sources.Add(source);
        return this;
    }

    public MappedText Append(MappedText other)
    {
        _text.Append(other._text);
        _sources.AddRange(other._sources);
        return this;
    }

    /// <summary>The parts with <paramref name="separator"/> between them.</summary>
    public static MappedText Join(string separator, IEnumerable<MappedText> parts)
    {
        var joined = new MappedText();
        var first = true;
        foreach (var part in parts)
        {
            if (!first)
            {
                joined.Append(separator, null);
            }

            joined.Append(part);
            first = false;
        }

        return joined;
    }

    /// <summary>A copy with runs of spaces made single and spaces trimmed from both ends.</summary>
    public MappedText CollapseSpaces()
    {
        var start = 0;
        var end = _text.Length;
        while (start < end && _text[start] == ' ')
        {
            start++;
        }

        while (end > start && _text[end - 1] == ' ')
        {
            end--;
        }

        var result = new MappedText();
        for (var i = start; i < end; i++)
        {
            if (_text[i] != ' ' || _text[i - 1] != ' ')
            {
                result.Append(_text[i], _sources[i]);
            }
        }

        return result;
    }

    public override string ToString() => _text.ToString();
}
