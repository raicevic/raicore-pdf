using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Docx;

/// <summary>
/// Applies WordprocessingML property elements (<c>w:rPr</c>, <c>w:pPr</c>) to resolved formats: each property an element
/// sets replaces the inherited one, so applying the elements of the style hierarchy in order resolves a format.
/// </summary>
internal static class DocxProperties
{
    /// <summary>Applies the run properties of <paramref name="rPr"/> (if any) to <paramref name="format"/>.</summary>
    public static RunFormat ApplyRun(RunFormat format, XElement? rPr, DocxTheme theme)
    {
        if (rPr is null)
        {
            return format;
        }

        foreach (var property in rPr.Elements())
        {
            if (!Ooxml.IsW(property))
            {
                continue;
            }

            switch (property.Name.LocalName)
            {
                case "rFonts":
                    var font = theme.Font(Ooxml.Attr(property, "asciiTheme")) ?? Ooxml.Attr(property, "ascii")
                        ?? theme.Font(Ooxml.Attr(property, "hAnsiTheme")) ?? Ooxml.Attr(property, "hAnsi");
                    if (!string.IsNullOrWhiteSpace(font))
                    {
                        format = format with { FontFamily = font.Trim() };
                    }

                    break;
                case "b":
                    format = format with { Bold = Ooxml.OnOff(property) };
                    break;
                case "i":
                    format = format with { Italic = Ooxml.OnOff(property) };
                    break;
                case "caps":
                    format = format with { Caps = Ooxml.OnOff(property) };
                    break;
                case "smallCaps":
                    format = format with { SmallCaps = Ooxml.OnOff(property) };
                    break;
                case "strike":
                case "dstrike":
                    format = format with { Strike = Ooxml.OnOff(property) };
                    break;
                case "vanish":
                    format = format with { Hidden = Ooxml.OnOff(property) };
                    break;
                case "color":
                    format = format with { Color = Ooxml.Color(Ooxml.Attr(property, "val"), Ooxml.Attr(property, "themeColor"), theme) };
                    break;
                case "sz":
                    if (Ooxml.HalfPoints(property, "val") is { } size)
                    {
                        format = format with { FontSize = Math.Clamp(size, 1, 1638) };
                    }

                    break;
                case "highlight":
                    format = format with { Background = Ooxml.Highlight(Ooxml.Attr(property, "val")) };
                    break;
                case "shd":
                    if (Ooxml.Shading(property, theme) is { } shading)
                    {
                        format = format with { Background = shading };
                    }

                    break;
                case "u":
                    format = format with
                    {
                        Underline = Ooxml.Attr(property, "val") switch
                        {
                            null or "single" or "words" or "thick" => UnderlineStyle.Single,
                            "double" => UnderlineStyle.Double,
                            "dotted" or "dottedHeavy" => UnderlineStyle.Dotted,
                            "dash" or "dashedHeavy" or "dashLong" or "dashLongHeavy" or "dotDash" or "dashDotHeavy" or "dotDotDash" or "dashDotDotHeavy" => UnderlineStyle.Dashed,
                            "wave" or "wavyHeavy" or "wavyDouble" => UnderlineStyle.Wavy,
                            "none" => UnderlineStyle.None,
                            _ => UnderlineStyle.Single,
                        },
                    };
                    break;
                case "vertAlign":
                    format = format with
                    {
                        Position = Ooxml.Attr(property, "val") switch
                        {
                            "superscript" => VerticalPosition.Superscript,
                            "subscript" => VerticalPosition.Subscript,
                            _ => VerticalPosition.Baseline,
                        },
                    };
                    break;
                case "position":
                    format = format with { BaselineShift = Ooxml.HalfPoints(property, "val") ?? 0 };
                    break;
                case "spacing":
                    format = format with { LetterSpacing = Math.Clamp(Ooxml.Twips(property, "val") ?? 0, -100, 100) };
                    break;
                case "rtl":
                    format = format with { RightToLeft = Ooxml.OnOff(property) };
                    break;
                case "lang":
                    if (Ooxml.Attr(property, "val") is { Length: > 0 } language)
                    {
                        format = format with { Language = language };
                    }

                    break;
            }
        }

        return format;
    }

    /// <summary>Applies the paragraph properties of <paramref name="pPr"/> (if any) to <paramref name="format"/>.</summary>
    public static ParagraphFormat ApplyParagraph(ParagraphFormat format, XElement? pPr, DocxTheme theme)
    {
        if (pPr is null)
        {
            return format;
        }

        foreach (var property in pPr.Elements())
        {
            if (!Ooxml.IsW(property))
            {
                continue;
            }

            switch (property.Name.LocalName)
            {
                case "keepNext":
                    format = format with { KeepWithNext = Ooxml.OnOff(property) };
                    break;
                case "keepLines":
                    format = format with { KeepLines = Ooxml.OnOff(property) };
                    break;
                case "pageBreakBefore":
                    format = format with { PageBreakBefore = Ooxml.OnOff(property) };
                    break;
                case "widowControl":
                    format = format with { WidowControl = Ooxml.OnOff(property) };
                    break;
                case "contextualSpacing":
                    format = format with { ContextualSpacing = Ooxml.OnOff(property) };
                    break;
                case "bidi":
                    format = format with { RightToLeft = Ooxml.OnOff(property) };
                    break;
                case "outlineLvl":
                    var level = Ooxml.Int(property, "val");
                    format = format with { OutlineLevel = level is >= 0 and <= 8 ? level : null };
                    break;
                case "jc":
                    format = format with
                    {
                        Alignment = Ooxml.Attr(property, "val") switch
                        {
                            "center" => WordAlignment.Center,
                            "right" or "end" => WordAlignment.End,
                            "both" or "distribute" or "mediumKashida" or "highKashida" or "lowKashida" or "thaiDistribute" => WordAlignment.Justify,
                            _ => WordAlignment.Start,
                        },
                    };
                    break;
                case "ind":
                    format = ApplyIndent(format, property);
                    break;
                case "spacing":
                    format = ApplySpacing(format, property);
                    break;
                case "shd":
                    format = format with { Shading = Ooxml.Shading(property, theme) };
                    break;
                case "pBdr":
                    var borders = format.Borders;
                    format = format with
                    {
                        Borders = new BoxSides(
                            Ooxml.Border(Ooxml.Child(property, "top"), theme) ?? borders.Top,
                            Ooxml.Border(Ooxml.Child(property, "right") ?? Ooxml.Child(property, "end"), theme) ?? borders.Right,
                            Ooxml.Border(Ooxml.Child(property, "bottom"), theme) ?? borders.Bottom,
                            Ooxml.Border(Ooxml.Child(property, "left") ?? Ooxml.Child(property, "start"), theme) ?? borders.Left),
                        BetweenBorder = Ooxml.Border(Ooxml.Child(property, "between"), theme) ?? format.BetweenBorder,
                    };
                    break;
                case "tabs":
                    format = format with { TabStops = ApplyTabs(format.TabStops, property) };
                    break;
            }
        }

        return format;
    }

    private static ParagraphFormat ApplyIndent(ParagraphFormat format, XElement ind)
    {
        if ((Ooxml.Twips(ind, "start") ?? Ooxml.Twips(ind, "left")) is { } start)
        {
            format = format with { IndentStart = start };
        }

        if ((Ooxml.Twips(ind, "end") ?? Ooxml.Twips(ind, "right")) is { } end)
        {
            format = format with { IndentEnd = end };
        }

        if (Ooxml.Twips(ind, "hanging") is { } hanging)
        {
            format = format with { FirstLineIndent = -hanging };
        }
        else if (Ooxml.Twips(ind, "firstLine") is { } firstLine)
        {
            format = format with { FirstLineIndent = firstLine };
        }

        return format;
    }

    private static ParagraphFormat ApplySpacing(ParagraphFormat format, XElement spacing)
    {
        // Automatic spacing (as HTML paragraphs) is 14 points.
        if (Ooxml.Attr(spacing, "beforeAutospacing") is { } beforeAuto && Ooxml.IsOn(beforeAuto))
        {
            format = format with { SpaceBefore = 14 };
        }
        else if (Ooxml.Twips(spacing, "before") is { } before)
        {
            format = format with { SpaceBefore = Math.Max(0, before) };
        }

        if (Ooxml.Attr(spacing, "afterAutospacing") is { } afterAuto && Ooxml.IsOn(afterAuto))
        {
            format = format with { SpaceAfter = 14 };
        }
        else if (Ooxml.Twips(spacing, "after") is { } after)
        {
            format = format with { SpaceAfter = Math.Max(0, after) };
        }

        if (Ooxml.Measure(Ooxml.Attr(spacing, "line"), 1) is { } line)
        {
            switch (Ooxml.Attr(spacing, "lineRule"))
            {
                case "exact":
                    format = format with { LineRule = LineSpacingRule.Exact, LineSpacing = Math.Clamp(Math.Abs(line) / 20, 1, 1584) };
                    break;
                case "atLeast":
                    format = format with { LineRule = LineSpacingRule.AtLeast, LineSpacing = Math.Clamp(line / 20, 0, 1584) };
                    break;
                default:
                    // In 240ths of a line.
                    format = format with { LineRule = LineSpacingRule.Multiple, LineSpacing = Math.Clamp(line / 240, 0.25, 10) };
                    break;
            }
        }

        return format;
    }

    /// <summary>Adds the tab stops of <c>w:tabs</c> to the inherited ones, and removes those it clears.</summary>
    private static List<TabStop> ApplyTabs(IReadOnlyList<TabStop> inherited, XElement tabs)
    {
        var result = new List<TabStop>(inherited);
        foreach (var tab in Ooxml.Children(tabs, "tab"))
        {
            if (Ooxml.Twips(tab, "pos") is not { } position)
            {
                continue;
            }

            result.RemoveAll(t => Math.Abs(t.Position - position) < 0.05);
            var kind = Ooxml.Attr(tab, "val");
            if (kind is "clear" or "bar" or "num")
            {
                continue;
            }

            var alignment = kind switch
            {
                "center" => TabAlignment.Center,
                "right" or "end" => TabAlignment.Right,
                "decimal" => TabAlignment.Decimal,
                _ => TabAlignment.Left,
            };
            char? leader = Ooxml.Attr(tab, "leader") switch
            {
                "dot" => '.',
                "hyphen" => '-',
                "underscore" or "heavy" => '_',
                "middleDot" => '\u00B7',
                _ => null,
            };
            result.Add(new TabStop(position, alignment, leader));
        }

        if (result.Count > 64)
        {
            result.RemoveRange(64, result.Count - 64);
        }

        result.Sort((a, b) => a.Position.CompareTo(b.Position));
        return result;
    }
}
