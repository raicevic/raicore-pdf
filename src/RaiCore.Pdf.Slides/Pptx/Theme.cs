using System.Xml.Linq;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>
/// A theme: its color scheme, font scheme (major for titles, minor for body text) and format scheme (the fills, lines
/// and backgrounds that style references point to).
/// </summary>
internal sealed class Theme
{
    /// <summary>The colors of the Office theme, used when a presentation has no theme.</summary>
    private static readonly Dictionary<string, PdfColor> OfficeColors = new(StringComparer.Ordinal)
    {
        ["dk1"] = PdfColor.FromRgb(0, 0, 0),
        ["lt1"] = PdfColor.FromRgb(255, 255, 255),
        ["dk2"] = PdfColor.FromRgb(0x44, 0x54, 0x6A),
        ["lt2"] = PdfColor.FromRgb(0xE7, 0xE6, 0xE6),
        ["accent1"] = PdfColor.FromRgb(0x44, 0x72, 0xC4),
        ["accent2"] = PdfColor.FromRgb(0xED, 0x7D, 0x31),
        ["accent3"] = PdfColor.FromRgb(0xA5, 0xA5, 0xA5),
        ["accent4"] = PdfColor.FromRgb(0xFF, 0xC0, 0x00),
        ["accent5"] = PdfColor.FromRgb(0x5B, 0x9B, 0xD5),
        ["accent6"] = PdfColor.FromRgb(0x70, 0xAD, 0x47),
        ["hlink"] = PdfColor.FromRgb(0x05, 0x63, 0xC1),
        ["folHlink"] = PdfColor.FromRgb(0x95, 0x4F, 0x72),
    };

    /// <summary>A theme with the Office colors and fonts and no format scheme.</summary>
    public static Theme Default { get; } = new();

    /// <summary>The theme part's name (its pictures are found through its relationships), or empty.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>The scheme colors by name (<c>dk1</c>, <c>accent1</c>, ...).</summary>
    public Dictionary<string, PdfColor> Colors { get; } = new(OfficeColors, StringComparer.Ordinal);

    /// <summary>The major (heading) Latin font.</summary>
    public string MajorLatin { get; private set; } = "Calibri Light";

    /// <summary>The minor (body) Latin font.</summary>
    public string MinorLatin { get; private set; } = "Calibri";

    /// <summary>The major East Asian font, if any.</summary>
    public string? MajorEastAsian { get; private set; }

    /// <summary>The minor East Asian font, if any.</summary>
    public string? MinorEastAsian { get; private set; }

    /// <summary>The major complex script font, if any.</summary>
    public string? MajorComplex { get; private set; }

    /// <summary>The minor complex script font, if any.</summary>
    public string? MinorComplex { get; private set; }

    /// <summary>The fill styles (style matrix fill references 1 to 999).</summary>
    public List<XElement> FillStyles { get; } = [];

    /// <summary>The line styles (style matrix line references).</summary>
    public List<XElement> LineStyles { get; } = [];

    /// <summary>The background fill styles (fill references from 1001).</summary>
    public List<XElement> BackgroundStyles { get; } = [];

    /// <summary>Reads a theme part (<c>a:theme</c>); anything missing keeps the Office theme's value.</summary>
    public static Theme Read(XElement? root, string path)
    {
        var theme = new Theme { Path = path };
        var elements = root.El("themeElements");
        if (elements.El("clrScheme") is { } scheme)
        {
            foreach (var entry in scheme.Elements())
            {
                if (entry.Elements().FirstOrDefault() is { } color && DrawingColors.Base(color, null, null) is { } value)
                {
                    theme.Colors[entry.Name.LocalName] = value;
                }
            }
        }

        if (elements.El("fontScheme") is { } fonts)
        {
            static string? Face(XElement? font, string script) => font.El(script).Attr("typeface") is { Length: > 0 } face ? face : null;
            var major = fonts.El("majorFont");
            var minor = fonts.El("minorFont");
            theme.MajorLatin = Face(major, "latin") ?? theme.MajorLatin;
            theme.MinorLatin = Face(minor, "latin") ?? theme.MinorLatin;
            theme.MajorEastAsian = Face(major, "ea");
            theme.MinorEastAsian = Face(minor, "ea");
            theme.MajorComplex = Face(major, "cs");
            theme.MinorComplex = Face(minor, "cs");
        }

        if (elements.El("fmtScheme") is { } format)
        {
            theme.FillStyles.AddRange(format.El("fillStyleLst")?.Elements() ?? []);
            theme.LineStyles.AddRange(format.El("lnStyleLst")?.Elements() ?? []);
            theme.BackgroundStyles.AddRange(format.El("bgFillStyleLst")?.Elements() ?? []);
        }

        return theme;
    }

    /// <summary>
    /// The font a theme font reference names (<c>+mj-lt</c>, <c>+mn-ea</c>, ...), or the name itself when it is not a
    /// reference; <c>null</c> for a reference to a font the theme does not have.
    /// </summary>
    public string? Font(string name) => name switch
    {
        "+mj-lt" => MajorLatin,
        "+mn-lt" => MinorLatin,
        "+mj-ea" => MajorEastAsian,
        "+mn-ea" => MinorEastAsian,
        "+mj-cs" => MajorComplex,
        "+mn-cs" => MinorComplex,
        _ => name,
    };
}
