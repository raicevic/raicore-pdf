using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Extraction.Fonts;

/// <summary>Recognizes the 14 standard fonts, and the common names of their metric-compatible equivalents, in <c>/BaseFont</c> names.</summary>
internal static class StandardFontNames
{
    /// <summary>The name without a subset tag (six uppercase letters and a plus sign, ISO 32000-2 9.6.4).</summary>
    public static string WithoutSubsetTag(string name) =>
        name.Length > 7 && name[6] == '+' && !name.AsSpan(0, 6).ContainsAnyExceptInRange('A', 'Z') ? name[7..] : name;

    /// <summary>
    /// The standard font a base font name means or is metric-compatible with (Arial is Helvetica, Times New Roman is
    /// Times, Courier New is Courier), or <c>null</c>.
    /// </summary>
    public static StandardFont? Find(string baseFont)
    {
        var name = WithoutSubsetTag(baseFont).ToUpperInvariant();
        if (name.StartsWith("SYMBOL", StringComparison.Ordinal))
        {
            return StandardFont.Symbol;
        }

        if (name.Contains("DINGBATS", StringComparison.Ordinal))
        {
            return StandardFont.ZapfDingbats;
        }

        var bold = name.Contains("BOLD", StringComparison.Ordinal) || name.Contains("BLACK", StringComparison.Ordinal);
        var italic = name.Contains("ITALIC", StringComparison.Ordinal) || name.Contains("OBLIQUE", StringComparison.Ordinal);
        if (name.Contains("COURIER", StringComparison.Ordinal))
        {
            return (bold, italic) switch
            {
                (true, true) => StandardFont.CourierBoldOblique,
                (true, false) => StandardFont.CourierBold,
                (false, true) => StandardFont.CourierOblique,
                _ => StandardFont.Courier,
            };
        }

        if (name.Contains("TIMES", StringComparison.Ordinal))
        {
            return (bold, italic) switch
            {
                (true, true) => StandardFont.TimesBoldItalic,
                (true, false) => StandardFont.TimesBold,
                (false, true) => StandardFont.TimesItalic,
                _ => StandardFont.TimesRoman,
            };
        }

        if (name.Contains("HELVETICA", StringComparison.Ordinal) || name.Contains("ARIAL", StringComparison.Ordinal))
        {
            return (bold, italic) switch
            {
                (true, true) => StandardFont.HelveticaBoldOblique,
                (true, false) => StandardFont.HelveticaBold,
                (false, true) => StandardFont.HelveticaOblique,
                _ => StandardFont.Helvetica,
            };
        }

        return null;
    }
}
