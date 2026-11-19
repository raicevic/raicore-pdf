using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RaiCore.Pdf.Slides.Model;

namespace RaiCore.Pdf.Slides.Pptx;

/// <summary>
/// Text frames: body properties inherited from the placeholders, and paragraph and run properties resolved through the
/// cascade of list styles (the presentation's default, the master's title, body or other style, the master's and
/// layout's placeholder, the shape's own) and the paragraph's and run's own properties.
/// </summary>
internal sealed partial class PptxReader
{
    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex Escape();

    /// <summary>Characters of the symbol fonts used for bullets, as Unicode (Wingdings and Symbol).</summary>
    private static readonly Dictionary<char, string> SymbolBullets = new()
    {
        ['l'] = "\u25CF",
        ['n'] = "\u25A0",
        ['o'] = "\u25A1",
        ['q'] = "\u25A1",
        ['u'] = "\u25C6",
        ['v'] = "\u25C6",
        ['w'] = "\u25C6",
        ['\u00A7'] = "\u25AA",
        ['\u00A8'] = "\u25A1",
        ['\u00D8'] = "\u25BA",
        ['\u00E0'] = "\u2192",
        ['\u00E8'] = "\u2192",
        ['\u00FC'] = "\u2713",
        ['\u00FB'] = "\u2717",
        ['\u00B7'] = "\u2022",
        ['\u009F'] = "\u2022",
        ['\uF0B7'] = "\u2022",
        ['\uF0A7'] = "\u25AA",
        ['\uF0D8'] = "\u25BA",
        ['\uF0FC'] = "\u2713",
        ['\uF06C'] = "\u25CF",
        ['\uF06E'] = "\u25A0",
    };

    /// <summary>Reads a text body into a frame.</summary>
    /// <param name="txBody">The text body (<c>p:txBody</c> or <c>a:txBody</c>).</param>
    /// <param name="lists">The list styles, lowest priority first.</param>
    /// <param name="bodyPrs">The body properties, lowest priority first.</param>
    /// <param name="sheet">The part the text belongs to.</param>
    private TextFrame TextFrame(XElement txBody, IReadOnlyList<XElement?> lists, IReadOnlyList<XElement?> bodyPrs, Sheet sheet)
    {
        string? Body(string name)
        {
            for (var i = bodyPrs.Count - 1; i >= 0; i--)
            {
                if (bodyPrs[i].Attr(name) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        double Inset(string name, double fallback) =>
            Body(name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) && double.IsFinite(emu)
                ? Math.Clamp(emu, 0, 1e10) / XmlExtensions.EmuPerPoint
                : fallback;

        var frame = new TextFrame
        {
            LeftInset = Inset("lIns", 7.2),
            TopInset = Inset("tIns", 3.6),
            RightInset = Inset("rIns", 7.2),
            BottomInset = Inset("bIns", 3.6),
            Anchor = Body("anchor") switch { "ctr" => TextAnchor.Middle, "b" => TextAnchor.Bottom, _ => TextAnchor.Top },
            AnchorCenter = Body("anchorCtr") is "1" or "true",
            Wrap = Body("wrap") != "none",
            Flow = Body("vert") switch
            {
                "vert" or "eaVert" or "mongolianVert" or "wordArtVertRtl" => TextFlow.Vertical,
                "vert270" => TextFlow.Vertical270,
                _ => TextFlow.Horizontal,
            },
        };

        for (var i = bodyPrs.Count - 1; i >= 0; i--)
        {
            var bodyPr = bodyPrs[i];
            if (bodyPr.El("normAutofit") is { } normal)
            {
                frame.ShrinkOnOverflow = true;
                frame.FontScale = Math.Clamp((normal.Number("fontScale") ?? 100000) / 100000, 0.01, 1);
                frame.LineSpacingReduction = Math.Clamp((normal.Number("lnSpcReduction") ?? 0) / 100000, 0, 0.9);
                break;
            }

            if (bodyPr.El("spAutoFit") is not null || bodyPr.El("noAutofit") is not null)
            {
                break;
            }
        }

        var counters = new Dictionary<int, (string Scheme, int Number)>();
        foreach (var p in txBody.Els("p"))
        {
            frame.Paragraphs.Add(Paragraph(p, lists, sheet, counters));
        }

        return frame;
    }

    /// <summary>Reads a paragraph with its properties resolved for its level.</summary>
    private TextParagraph Paragraph(XElement p, IReadOnlyList<XElement?> lists, Sheet sheet, Dictionary<int, (string Scheme, int Number)> counters)
    {
        var pPr = p.El("pPr");
        var level = (int)Math.Clamp(pPr.Number("lvl") ?? 0, 0, 8);
        var layers = new List<XElement>();
        foreach (var list in lists)
        {
            if (list.El("defPPr") is { } defaults)
            {
                layers.Add(defaults);
            }

            if (list.El($"lvl{level + 1}pPr") is { } levelProperties)
            {
                layers.Add(levelProperties);
            }
        }

        if (pPr is not null)
        {
            layers.Add(pPr);
        }

        string? Attr(string name)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i].Attr(name) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        XElement? Child(params string[] names)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                foreach (var child in layers[i].Elements())
                {
                    if (Array.IndexOf(names, child.Name.LocalName) >= 0)
                    {
                        return child;
                    }
                }
            }

            return null;
        }

        double Length(string name) =>
            Attr(name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) && double.IsFinite(emu)
                ? Math.Clamp(emu, -1e10, 1e10) / XmlExtensions.EmuPerPoint
                : 0;

        var runLayers = layers.Select(l => l.El("defRPr")).ToList();
        var runs = new List<TextRun>();
        foreach (var child in p.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "r":
                    runs.Add(Run(child.El("t")?.Value ?? string.Empty, child.El("rPr"), runLayers, sheet));
                    break;
                case "br":
                    runs.Add(Run("\n", child.El("rPr"), runLayers, sheet));
                    break;
                case "fld":
                {
                    var text = child.Attr("type") == "slidenum" ? _slideNumber.ToString(CultureInfo.InvariantCulture) : child.El("t")?.Value ?? string.Empty;
                    runs.Add(Run(text, child.El("rPr"), runLayers, sheet));
                    break;
                }
            }
        }

        var endStyle = Style([.. runLayers, p.El("endParaRPr")], sheet);
        var hasText = runs.Any(r => r.Text.Trim().Length > 0);
        if (hasText)
        {
            // Deeper levels restart their numbering after a paragraph of this level.
            foreach (var deeper in counters.Keys.Where(k => k > level).ToList())
            {
                counters.Remove(deeper);
            }
        }

        var bullet = hasText ? Bullet(Child("buNone", "buChar", "buAutoNum", "buBlip"), Child("buFont", "buFontTx"), Child("buClr", "buClrTx"),
            Child("buSzPct", "buSzPts", "buSzTx"), level, sheet, counters) : null;
        if (hasText && bullet is null)
        {
            // A paragraph without a number ends the numbered list of its level.
            counters.Remove(level);
        }

        var paragraph = new TextParagraph
        {
            Alignment = Attr("algn") switch
            {
                "ctr" => ParagraphAlignment.Center,
                "r" => ParagraphAlignment.Right,
                "just" or "dist" or "justLow" or "thaiDist" => ParagraphAlignment.Justify,
                _ => ParagraphAlignment.Left,
            },
            Level = level,
            MarginLeft = Math.Max(Length("marL"), 0),
            MarginRight = Math.Max(Length("marR"), 0),
            Indent = Length("indent"),
            LineSpacing = Spacing(Child("lnSpc"), Model.Spacing.Single),
            SpaceBefore = Spacing(Child("spcBef"), Model.Spacing.None),
            SpaceAfter = Spacing(Child("spcAft"), Model.Spacing.None),
            Bullet = bullet,
            EndStyle = endStyle,
        };
        paragraph.Runs.AddRange(runs);
        return paragraph;
    }

    /// <summary>A spacing element (<c>a:lnSpc</c>, <c>a:spcBef</c>, <c>a:spcAft</c>): a percentage or points.</summary>
    private static Spacing Spacing(XElement? element, Spacing fallback)
    {
        if (element.El("spcPct")?.Number("val") is { } percent)
        {
            return new Spacing(Math.Clamp(percent / 100000, 0, 100), false);
        }

        if (element.El("spcPts")?.Number("val") is { } points)
        {
            return new Spacing(Math.Clamp(points / 100, 0, 10000), true);
        }

        return fallback;
    }

    /// <summary>Reads a run: its text and its style from the run layers and its own properties.</summary>
    private TextRun Run(string text, XElement? rPr, List<XElement?> runLayers, Sheet sheet)
    {
        var link = Hyperlink(rPr.El("hlinkClick"), sheet);
        var style = Style([.. runLayers, rPr], sheet);
        if (link is not null)
        {
            // Hyperlinks show in the theme's hyperlink color, underlined.
            style = style with { Color = sheet.Colors.Color("hlink") ?? style.Color, Underline = true };
        }

        return new TextRun { Text = Clean(text), Style = style, Link = link };
    }

    /// <summary>
    /// Text with the format's escapes decoded (<c>_x000B_</c> for characters XML cannot hold, <c>_x005F_</c> for an
    /// underscore), without control characters other than line feeds and tabs (vertical tabs are line breaks).
    /// </summary>
    internal static string Clean(string text)
    {
        if (text.Contains("_x", StringComparison.Ordinal))
        {
            text = Escape().Replace(text, m => ((char)int.Parse(m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
        }

        if (!text.Any(c => c < ' ' || (c >= '\u007F' && c <= '\u009F')))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n' or '\t')
            {
                builder.Append(c);
            }
            else if (c == '\v')
            {
                builder.Append('\n');
            }
            else if (c >= ' ' && (c < '\u007F' || c > '\u009F'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Resolves run properties from layers (<c>a:defRPr</c>, <c>a:rPr</c>), lowest priority first.</summary>
    private static RunStyle Style(IReadOnlyList<XElement?> layers, Sheet sheet)
    {
        string? Attr(string name)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i].Attr(name) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        double Number(string name, double fallback) =>
            Attr(name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

        string? Face(string script)
        {
            for (var i = layers.Count - 1; i >= 0; i--)
            {
                if (layers[i].El(script)?.Attr("typeface") is { Length: > 0 } face)
                {
                    return sheet.Colors.Theme.Font(face);
                }
            }

            return null;
        }

        var color = PdfColor.Black;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            if (layers[i] is not { } layer)
            {
                continue;
            }

            var fill = layer.Elements().FirstOrDefault(e => e.Name.LocalName is "solidFill" or "gradFill" or "noFill" or "pattFill");
            if (fill is not null)
            {
                color = fill.Name.LocalName switch
                {
                    "solidFill" => DrawingColors.Child(fill, sheet.Colors, null) ?? PdfColor.Black,
                    "gradFill" => DrawingColors.Child(fill.El("gsLst")?.Elements().FirstOrDefault(), sheet.Colors, null) ?? PdfColor.Black,
                    "pattFill" => DrawingColors.Child(fill.El("fgClr"), sheet.Colors, null) ?? PdfColor.Black,
                    _ => PdfColor.Black.WithAlpha(0),
                };
                break;
            }
        }

        var underline = Attr("u");
        var size = Math.Clamp(Number("sz", 1800) / 100, 0.5, 4000);
        return new RunStyle
        {
            Font = Face("latin") ?? sheet.Colors.Theme.MinorLatin,
            EastAsianFont = Face("ea"),
            ComplexFont = Face("cs"),
            Size = size,
            Bold = Attr("b") is "1" or "true",
            Italic = Attr("i") is "1" or "true",
            Underline = underline is not null and not "none",
            DoubleUnderline = underline is "dbl" or "dblHeavy",
            Strike = Attr("strike") is "sngStrike" or "dblStrike",
            Color = color,
            Baseline = Math.Clamp(Number("baseline", 0) / 100000, -1, 1),
            AllCaps = Attr("cap") is "all" or "small",
            CharacterSpacing = Math.Clamp(Number("spc", 0) / 100, -size, 1000),
        };
    }

    /// <summary>The bullet of a paragraph from its resolved bullet properties, or <c>null</c> for none.</summary>
    private static Bullet? Bullet(XElement? kind, XElement? font, XElement? color, XElement? size, int level, Sheet sheet,
        Dictionary<int, (string Scheme, int Number)> counters)
    {
        if (kind is null || kind.Name.LocalName == "buNone")
        {
            return null;
        }

        var face = font?.Name.LocalName == "buFont" && font.Attr("typeface") is { Length: > 0 } typeface ? sheet.Colors.Theme.Font(typeface) : null;
        string text;
        switch (kind.Name.LocalName)
        {
            case "buChar":
            {
                text = kind.Attr("char") is { Length: > 0 } character ? character : "\u2022";
                if (face is not null && IsSymbolFont(face))
                {
                    text = text.Length == 1 && SymbolBullets.TryGetValue(text[0], out var unicode) ? unicode : "\u2022";
                    face = null;
                }

                break;
            }

            case "buAutoNum":
            {
                var scheme = kind.Attr("type") ?? "arabicPeriod";
                var start = (int)Math.Clamp(kind.Number("startAt") ?? 1, 1, 32767);
                var number = counters.TryGetValue(level, out var counter) && counter.Scheme == scheme ? counter.Number + 1 : start;
                counters[level] = (scheme, number);
                text = Number(scheme, number);
                if (face is not null && IsSymbolFont(face))
                {
                    face = null;
                }

                break;
            }

            default:
                text = "\u2022";
                face = null;
                break;
        }

        return new Bullet
        {
            Text = text,
            Font = face,
            Color = color?.Name.LocalName == "buClr" ? DrawingColors.Child(color, sheet.Colors, null) : null,
            SizeFactor = size?.Name.LocalName == "buSzPct" ? Math.Clamp((size.Number("val") ?? 100000) / 100000, 0.25, 4) : 1,
            SizePoints = size?.Name.LocalName == "buSzPts" ? Math.Clamp((size.Number("val") ?? 1200) / 100, 1, 4000) : null,
        };
    }

    /// <summary>Whether a font maps its characters to symbols (bullets are written in it as letters).</summary>
    private static bool IsSymbolFont(string face) =>
        face.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || face.Equals("Symbol", StringComparison.OrdinalIgnoreCase)
        || face.Equals("Webdings", StringComparison.OrdinalIgnoreCase) || face.StartsWith("Marlett", StringComparison.OrdinalIgnoreCase);

    /// <summary>A number in an automatic numbering scheme (<c>arabicPeriod</c>: 1., <c>alphaLcParenR</c>: a), ...).</summary>
    internal static string Number(string scheme, int number)
    {
        var digits = scheme switch
        {
            _ when scheme.StartsWith("romanUc", StringComparison.Ordinal) => Roman(number),
            _ when scheme.StartsWith("romanLc", StringComparison.Ordinal) => Roman(number).ToLowerInvariant(),
            _ when scheme.StartsWith("alphaUc", StringComparison.Ordinal) => Alpha(number),
            _ when scheme.StartsWith("alphaLc", StringComparison.Ordinal) => Alpha(number).ToLowerInvariant(),
            _ => number.ToString(CultureInfo.InvariantCulture),
        };

        if (scheme.EndsWith("ParenBoth", StringComparison.Ordinal))
        {
            return "(" + digits + ")";
        }

        if (scheme.EndsWith("ParenR", StringComparison.Ordinal))
        {
            return digits + ")";
        }

        if (scheme.EndsWith("Period", StringComparison.Ordinal))
        {
            return digits + ".";
        }

        return scheme.EndsWith("Minus", StringComparison.Ordinal) ? "- " + digits + " -" : digits;
    }

    /// <summary>A number in capital Roman numerals.</summary>
    private static string Roman(int number)
    {
        ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var builder = new StringBuilder();
        for (var i = 0; i < values.Length && number > 0; i++)
        {
            while (number >= values[i])
            {
                builder.Append(symbols[i]);
                number -= values[i];
            }
        }

        return builder.ToString();
    }

    /// <summary>A number as capital letters (A, B, ..., Z, AA, BB, ... as PowerPoint counts).</summary>
    private static string Alpha(int number)
    {
        var letter = (char)('A' + ((number - 1) % 26));
        return new string(letter, ((number - 1) / 26) + 1);
    }
}
