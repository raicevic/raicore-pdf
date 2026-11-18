using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Extraction.Fonts;

/// <summary>
/// Where the widths of Adobe's core fonts (the AFM files viewers use for non-embedded standard fonts) differ from the
/// metric-compatible Liberation fonts the core measures with: five characters of Helvetica and Times, in 1/1000 em.
/// Courier is the same in both.
/// </summary>
internal static class AdobeCoreWidths
{
    private static readonly Dictionary<char, int> Helvetica = new() { ['\u00AF'] = 333, ['\u00B1'] = 584, ['\u00B5'] = 556, ['\u00B7'] = 278, ['\u00F7'] = 584 };
    private static readonly Dictionary<char, int> HelveticaBold = new() { ['\u00AF'] = 333, ['\u00B1'] = 584, ['\u00B5'] = 611, ['\u00B7'] = 278, ['\u00F7'] = 584 };
    private static readonly Dictionary<char, int> TimesRoman = new() { ['\u00AF'] = 333, ['\u00B1'] = 564, ['\u00B5'] = 500, ['\u00B7'] = 250, ['\u00F7'] = 564 };
    private static readonly Dictionary<char, int> TimesBold = new() { ['\u00AF'] = 333, ['\u00B1'] = 570, ['\u00B5'] = 556, ['\u00B7'] = 250, ['\u00F7'] = 570 };
    private static readonly Dictionary<char, int> TimesItalic = new() { ['\u00AF'] = 333, ['\u00B1'] = 675, ['\u00B5'] = 500, ['\u00F7'] = 675 };
    private static readonly Dictionary<char, int> TimesBoldItalic = new() { ['\u00AF'] = 333, ['\u00B1'] = 570, ['\u00F7'] = 570 };

    /// <summary>Adobe's width of a character where it differs from the core's metrics, or <c>null</c>.</summary>
    public static int? Get(StandardFont font, char c) => font switch
    {
        StandardFont.Helvetica or StandardFont.HelveticaOblique => Helvetica,
        StandardFont.HelveticaBold or StandardFont.HelveticaBoldOblique => HelveticaBold,
        StandardFont.TimesRoman => TimesRoman,
        StandardFont.TimesBold => TimesBold,
        StandardFont.TimesItalic => TimesItalic,
        StandardFont.TimesBoldItalic => TimesBoldItalic,
        _ => null,
    } is { } widths && widths.TryGetValue(c, out var width) ? width : null;
}
