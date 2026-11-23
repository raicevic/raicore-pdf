namespace RaiCore.Pdf.Layout.Text;

/// <summary>
/// Replaces surrogates that lack their other half (damaged or cut text) with U+FFFD, so the code that walks text by
/// code point (itemizer, bidi, line breaking) never meets one. Each is replaced by one character, so offsets into the
/// text stay valid.
/// </summary>
internal static class LoneSurrogates
{
    /// <summary><paramref name="text"/> itself when it has no lone surrogate, else a copy with each one replaced.</summary>
    public static string Replace(string text)
    {
        var first = IndexOfLone(text, 0);
        if (first < 0)
        {
            return text;
        }

        return string.Create(text.Length, (text, first), static (chars, state) =>
        {
            state.text.AsSpan().CopyTo(chars);
            for (var i = state.first; i >= 0; i = IndexOfLone(state.text, i + 1))
            {
                chars[i] = '�';
            }
        });
    }

    /// <summary>The index of the first lone surrogate at or after <paramref name="start"/>, or -1.</summary>
    private static int IndexOfLone(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i]))
            {
                continue;
            }

            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }
}
