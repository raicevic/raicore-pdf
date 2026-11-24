using System.Globalization;
using System.Text;
using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>
/// Turns text into paragraphs: plain text (a paragraph per line) and XFA rich text (XFA 3.3, "Rich Text Reference"), an
/// XHTML subset: <c>p</c>, <c>div</c>, <c>span</c>, <c>b</c>, <c>i</c>, <c>u</c>, <c>sup</c>, <c>sub</c>, <c>br</c>,
/// <c>a</c>, <c>ol</c>/<c>ul</c>/<c>li</c>, with the CSS properties XFA uses (font, color, text decoration, alignment,
/// margins, indent, line height, letter spacing, vertical alignment and <c>xfa-spacerun</c>).
/// </summary>
internal static class RichText
{
    /// <summary>The namespace of the floating field attributes (<c>xfa:embed</c>).</summary>
    private const string EmbedNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

    /// <summary>The deepest XHTML nesting followed.</summary>
    private const int MaxDepth = 64;

    /// <summary>Plain text as paragraphs: one per line, the para's space above the first and below the last.</summary>
    public static List<ParagraphSpec> Plain(string text, FontSpec font, ParaSpec para)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
        var paragraphs = new List<ParagraphSpec>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var spec = new ParagraphSpec
            {
                Font = font,
                Para = para with
                {
                    SpaceAbove = i == 0 ? para.SpaceAbove : 0,
                    SpaceBelow = i == lines.Length - 1 ? para.SpaceBelow : 0,
                    TextIndent = i == 0 ? para.TextIndent : 0,
                },
            };
            spec.Runs.Add(new TextRunSpec(lines[i], font));
            paragraphs.Add(spec);
        }

        return paragraphs;
    }

    /// <summary>Rich text (an XHTML <c>body</c>, or any XHTML element) as paragraphs, on a base font and para.</summary>
    /// <param name="body">The XHTML.</param>
    /// <param name="font">The base font.</param>
    /// <param name="para">The base paragraph format.</param>
    /// <param name="embed">The text of a floating field (<c>xfa:embed="#id"</c>), by its reference, or <c>null</c>.</param>
    public static List<ParagraphSpec> Rich(XElement body, FontSpec font, ParaSpec para, Func<string, string?>? embed = null)
    {
        var builder = new Builder(embed);
        builder.Block(body, font, para, false, 0);
        builder.Finish();
        if (builder.Paragraphs.Count == 0)
        {
            builder.Paragraphs.Add(new ParagraphSpec { Font = font, Para = para });
        }

        return builder.Paragraphs;
    }

    private sealed class Builder(Func<string, string?>? embed)
    {
        private ParagraphSpec? _current;
        private bool _lastSpace = true;

        public List<ParagraphSpec> Paragraphs { get; } = [];

        public void Finish() => Close();

        private void Close()
        {
            if (_current is not null)
            {
                // Collapsed whitespace at the end of a paragraph is not drawn.
                if (_current.Runs.Count > 0 && _current.Runs[^1] is { } last && last.Text.EndsWith(' ') && !last.Text.EndsWith("  ", StringComparison.Ordinal))
                {
                    _current.Runs[^1] = last with { Text = last.Text.TrimEnd(' ') };
                }

                Paragraphs.Add(_current);
            }

            _current = null;
            _lastSpace = true;
        }

        private ParagraphSpec Open(FontSpec font, ParaSpec para)
        {
            if (_current is null)
            {
                _current = new ParagraphSpec { Font = font, Para = para };
                _lastSpace = true;
            }

            return _current;
        }

        /// <summary>A block's content: text and inline elements go into paragraphs, block elements start their own.</summary>
        public void Block(XElement element, FontSpec font, ParaSpec para, bool spacerun, int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }

            foreach (var node in element.Nodes())
            {
                if (node is XText text)
                {
                    Text(text.Value, font, para, spacerun);
                    continue;
                }

                if (node is not XElement child)
                {
                    continue;
                }

                var name = child.Name.LocalName.ToLowerInvariant();
                var style = Css(child.Attr("style"));
                var childFont = Font(name, style, font);
                var childSpacerun = spacerun || (style.TryGetValue("xfa-spacerun", out var run) && run == "yes");
                switch (name)
                {
                    case "p" or "div" or "body" or "html" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "ol" or "ul":
                    {
                        Close();
                        var childPara = Para(style, para, childFont, name);
                        if (name is "ol" or "ul")
                        {
                            var number = 0;
                            foreach (var item in child.Elements())
                            {
                                number++;
                                Close();
                                var itemStyle = Css(item.Attr("style"));
                                var itemFont = Font("li", itemStyle, childFont);
                                var itemPara = Para(itemStyle, childPara, itemFont, "li");
                                Open(itemFont, itemPara).Label = name == "ol" ? $"{number}." : "\u2022";
                                Block(item, itemFont, itemPara, childSpacerun, depth + 1);
                                Close();
                            }

                            break;
                        }

                        Open(childFont, childPara);
                        Block(child, childFont, childPara, childSpacerun, depth + 1);
                        Close();
                        break;
                    }

                    case "span" when child.Attribute(XName.Get("embed", EmbedNamespace))?.Value is { } reference:
                        if (embed?.Invoke(reference) is { } embedded)
                        {
                            Text(embedded, childFont, para, true);
                        }

                        break;
                    case "br":
                        Open(font, para).Runs.Add(new TextRunSpec("\n", font));
                        _lastSpace = true;
                        break;
                    default:
                        Block(child, childFont, para, childSpacerun, depth + 1);
                        break;
                }
            }
        }

        private void Text(string text, FontSpec font, ParaSpec para, bool spacerun)
        {
            if (spacerun)
            {
                Open(font, para).Runs.Add(new TextRunSpec(text.Replace('\n', ' ').Replace('\t', ' '), font));
                _lastSpace = text.EndsWith(' ');
                return;
            }

            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c) && c != '\u00A0')
                {
                    if (!_lastSpace)
                    {
                        builder.Append(' ');
                        _lastSpace = true;
                    }
                }
                else
                {
                    builder.Append(c);
                    _lastSpace = false;
                }
            }

            if (builder.Length == 0 && _current is null)
            {
                return; // Whitespace between blocks.
            }

            Open(font, para).Runs.Add(new TextRunSpec(builder.ToString(), font));
        }

        private static FontSpec Font(string element, Dictionary<string, string> style, FontSpec font)
        {
            font = element switch
            {
                "b" or "strong" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => font with { Bold = true },
                "i" or "em" => font with { Italic = true },
                "u" => font with { Underline = 1 },
                "s" or "strike" or "del" => font with { LineThrough = true },
                "sup" => font with { BaselineShift = font.Size * 0.33, Size = font.Size * 0.66 },
                "sub" => font with { BaselineShift = -font.Size * 0.2, Size = font.Size * 0.66 },
                _ => font,
            };
            foreach (var (property, value) in style)
            {
                switch (property)
                {
                    case "font-family":
                        var family = value.Split(',')[0].Trim().Trim('\'', '"');
                        if (family.Length > 0)
                        {
                            font = font with { Typeface = family };
                        }

                        break;
                    case "font-size" when Length(value, font.Size) is { } size and > 0:
                        font = font with { Size = Math.Min(size, 1000) };
                        break;
                    case "font-weight":
                        font = font with { Bold = value is "bold" or "bolder" || (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight) && weight >= 600) };
                        break;
                    case "font-style":
                        font = font with { Italic = value is "italic" or "oblique" };
                        break;
                    case "color" when CssColor(value) is { } color:
                        font = font with { Color = color };
                        break;
                    case "text-decoration":
                        font = font with
                        {
                            Underline = value.Contains("underline", StringComparison.Ordinal) ? (value.Contains("double", StringComparison.Ordinal) ? 2 : 1) : value.Contains("none", StringComparison.Ordinal) ? 0 : font.Underline,
                            LineThrough = value.Contains("line-through", StringComparison.Ordinal) || (!value.Contains("none", StringComparison.Ordinal) && font.LineThrough),
                        };
                        break;
                    case "letter-spacing" when Length(value, font.Size) is { } spacing:
                        font = font with { LetterSpacing = spacing };
                        break;
                    case "vertical-align":
                        font = value switch
                        {
                            "super" => font with { BaselineShift = font.Size * 0.33 },
                            "sub" => font with { BaselineShift = -font.Size * 0.2 },
                            _ when Length(value, font.Size) is { } shift => font with { BaselineShift = -shift },
                            _ => font,
                        };
                        break;
                }
            }

            return font;
        }

        private static ParaSpec Para(Dictionary<string, string> style, ParaSpec para, FontSpec font, string element)
        {
            if (element is "li")
            {
                para = para with { MarginLeft = para.MarginLeft + (font.Size * 1.5) };
            }

            foreach (var (property, value) in style)
            {
                switch (property)
                {
                    case "text-align":
                        para = para with { HAlign = value is "justify" ? "justify" : value is "center" or "right" ? value : "left" };
                        break;
                    case "margin-top" or "space-before" when Length(value, font.Size) is { } above:
                        para = para with { SpaceAbove = above };
                        break;
                    case "margin-bottom" or "space-after" when Length(value, font.Size) is { } below:
                        para = para with { SpaceBelow = below };
                        break;
                    case "margin-left" when Length(value, font.Size) is { } left:
                        para = para with { MarginLeft = left };
                        break;
                    case "margin-right" when Length(value, font.Size) is { } right:
                        para = para with { MarginRight = right };
                        break;
                    case "text-indent" when Length(value, font.Size) is { } indent:
                        para = para with { TextIndent = indent };
                        break;
                    case "line-height" when Length(value, font.Size) is { } height:
                        para = para with { LineHeight = height };
                        break;
                }
            }

            return para;
        }
    }

    /// <summary>A CSS declaration list (<c>style</c> attribute) by property.</summary>
    internal static Dictionary<string, string> Css(string? style)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(style))
        {
            return properties;
        }

        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var property = declaration[..colon].Trim().ToLowerInvariant();
                var value = declaration[(colon + 1)..].Trim();
                properties[property] = property == "font-family" ? value : value.ToLowerInvariant();
            }
        }

        return properties;
    }

    /// <summary>A CSS length in points (<c>pt</c>, <c>px</c>, <c>in</c>, <c>mm</c>, <c>cm</c>, <c>em</c>, <c>%</c> of the font size).</summary>
    internal static double? Length(string value, double fontSize)
    {
        value = value.Trim();
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return fontSize * percent / 100;
        }

        if (value.EndsWith("em", StringComparison.Ordinal) && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var em))
        {
            return fontSize * em;
        }

        return Measurement.TryParse(value, "pt", out var points) ? points : null;
    }

    /// <summary>A CSS color: <c>#rgb</c>, <c>#rrggbb</c>, <c>rgb(r,g,b)</c> or a basic name.</summary>
    internal static PdfColor? CssColor(string value)
    {
        value = value.Trim();
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            }

            return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
                ? PdfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
                : null;
        }

        if (value.StartsWith("rgb(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            var parts = value[4..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            {
                var c = parts.Select(p => (byte)Math.Clamp(int.Parse(p, CultureInfo.InvariantCulture), 0, 255)).ToArray();
                return PdfColor.FromRgb(c[0], c[1], c[2]);
            }

            return null;
        }

        return value switch
        {
            "black" => PdfColor.Black,
            "white" => PdfColor.White,
            "red" => PdfColor.FromRgb(255, 0, 0),
            "green" => PdfColor.FromRgb(0, 128, 0),
            "blue" => PdfColor.FromRgb(0, 0, 255),
            "gray" or "grey" => PdfColor.FromRgb(128, 128, 128),
            "navy" => PdfColor.FromRgb(0, 0, 128),
            "maroon" => PdfColor.FromRgb(128, 0, 0),
            _ => null,
        };
    }
}
