using System.Xml.Linq;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>The theme of a document (<c>word/theme/theme1.xml</c>): its major and minor fonts and its color scheme.</summary>
internal sealed class DocxTheme
{
    private readonly Dictionary<string, PdfColor> _colors = new(StringComparer.Ordinal);

    /// <summary>The font of headings (<c>majorHAnsi</c>), or <c>null</c>.</summary>
    public string? MajorFont { get; private set; }

    /// <summary>The font of body text (<c>minorHAnsi</c>), or <c>null</c>.</summary>
    public string? MinorFont { get; private set; }

    /// <summary>Reads a theme part, or gives an empty theme for <c>null</c>.</summary>
    public static DocxTheme Read(XElement? root)
    {
        var theme = new DocxTheme();
        if (root is null)
        {
            return theme;
        }

        foreach (var element in root.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "majorFont":
                    theme.MajorFont ??= Latin(element);
                    break;
                case "minorFont":
                    theme.MinorFont ??= Latin(element);
                    break;
                case "clrScheme":
                    foreach (var color in element.Elements())
                    {
                        var value = color.Elements().FirstOrDefault() is { } definition
                            ? Ooxml.Attr(definition, definition.Name.LocalName == "sysClr" ? "lastClr" : "val")
                            : null;
                        if (Ooxml.Color(value) is { } rgb)
                        {
                            theme._colors.TryAdd(color.Name.LocalName, rgb);
                        }
                    }

                    break;
            }
        }

        return theme;
    }

    private static string? Latin(XElement font) =>
        font.Elements().FirstOrDefault(e => e.Name.LocalName == "latin") is { } latin && Ooxml.Attr(latin, "typeface") is { Length: > 0 } typeface ? typeface : null;

    /// <summary>The font a theme font reference (<c>minorHAnsi</c>, <c>majorAscii</c>...) names, or <c>null</c>.</summary>
    public string? Font(string? reference) => reference switch
    {
        null => null,
        _ when reference.StartsWith("major", StringComparison.Ordinal) => MajorFont,
        _ when reference.StartsWith("minor", StringComparison.Ordinal) => MinorFont,
        _ => null,
    };

    /// <summary>A theme color by the name WordprocessingML uses (<c>accent1</c>, <c>text1</c>, <c>hyperlink</c>), or <c>null</c>.</summary>
    public PdfColor? Color(string name)
    {
        var key = name switch
        {
            "dark1" or "text1" => "dk1",
            "light1" or "background1" => "lt1",
            "dark2" or "text2" => "dk2",
            "light2" or "background2" => "lt2",
            "hyperlink" => "hlink",
            "followedHyperlink" => "folHlink",
            _ => name,
        };
        return _colors.TryGetValue(key, out var color) ? color : null;
    }
}
