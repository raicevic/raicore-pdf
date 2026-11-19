using System.Globalization;
using System.Xml;
using RaiCore.Pdf.Layout;

namespace RaiCore.Pdf.Sheets.Xlsx;

/// <summary>A cell style of a workbook (an <c>xf</c> of <c>cellXfs</c>), resolved: its format, number format and alignment.</summary>
internal sealed record XlsxCellStyle(CellFormat Format, NumberFormat NumberFormat, CellAlignment Alignment);

/// <summary>
/// The styles part of a workbook (<c>xl/styles.xml</c>): number formats, fonts, fills, borders and the cell styles that
/// combine them. Indexes out of range fall back to the first entry, or to nothing.
/// </summary>
internal sealed class XlsxStyles
{
    private readonly Dictionary<int, string> _numberFormats = [];
    private readonly List<FontInfo> _fonts = [];
    private readonly List<PdfColor?> _fills = [];
    private readonly List<CellFormat> _borders = [];
    private readonly List<XfInfo> _xfs = [];
    private readonly Dictionary<int, XlsxCellStyle> _resolved = [];
    private readonly Dictionary<int, NumberFormat> _formats = [];
    private readonly FormatSettings _settings;

    private XlsxStyles(FormatSettings settings) => _settings = settings;

    /// <summary>The font size of the workbook's default font (its first), in points.</summary>
    public double DefaultFontSize => _fonts.Count > 0 && _fonts[0].Size is { } size ? size : 11;

    /// <summary>The family of the workbook's default font, or <c>null</c>.</summary>
    public string? DefaultFontName => _fonts.Count > 0 ? _fonts[0].Name : null;

    /// <summary>Styles of a workbook without a styles part: General everywhere, in the default font.</summary>
    public static XlsxStyles Empty(FormatSettings settings) => new(settings);

    /// <summary>Reads the styles part.</summary>
    public static XlsxStyles Read(XmlReader reader, FormatSettings settings, IReadOnlyList<uint>? theme)
    {
        var styles = new XlsxStyles(settings);
        var palette = new List<uint>();
        var section = string.Empty;
        var depth = -1;
        var fonts = new List<Action<XlsxColors>>();
        var fills = new List<Action<XlsxColors>>();
        var borders = new List<Action<XlsxColors>>();

        // The palette may come after the fonts that use it: colors are resolved once the whole part is read.
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Depth == 1)
            {
                section = reader.LocalName;
                depth = reader.Depth;
                continue;
            }

            if (reader.Depth != depth + 1)
            {
                if (section == "colors" && reader.LocalName == "rgbColor" && reader.GetAttribute("rgb") is { } rgb
                    && uint.TryParse(rgb, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                {
                    palette.Add(value & 0xFFFFFF);
                }

                continue;
            }

            switch (section, reader.LocalName)
            {
                case ("numFmts", "numFmt"):
                    if (int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && reader.GetAttribute("formatCode") is { } code)
                    {
                        styles._numberFormats[id] = code;
                    }

                    break;
                case ("fonts", "font"):
                    fonts.Add(ReadFont(reader, styles._fonts));
                    break;
                case ("fills", "fill"):
                    fills.Add(ReadFill(reader, styles._fills));
                    break;
                case ("borders", "border"):
                    borders.Add(ReadBorder(reader, styles._borders));
                    break;
                case ("cellXfs", "xf"):
                    styles._xfs.Add(ReadXf(reader));
                    break;
            }
        }

        var colors = new XlsxColors(palette, theme);
        foreach (var resolve in fonts.Concat(fills).Concat(borders))
        {
            resolve(colors);
        }

        return styles;
    }

    /// <summary>The cell style at an index (the first one for an index out of range).</summary>
    public XlsxCellStyle CellStyle(int index)
    {
        if (index < 0 || index >= _xfs.Count)
        {
            index = 0;
        }

        if (_resolved.TryGetValue(index, out var style))
        {
            return style;
        }

        var xf = index < _xfs.Count ? _xfs[index] : new XfInfo();
        var font = xf.FontId >= 0 && xf.FontId < _fonts.Count ? _fonts[xf.FontId] : _fonts.Count > 0 ? _fonts[0] : new FontInfo();
        var border = xf.BorderId >= 0 && xf.BorderId < _borders.Count ? _borders[xf.BorderId] : CellFormat.Default;
        var format = new CellFormat
        {
            FontName = font.Name,
            FontSize = font.Size,
            Bold = font.Bold,
            Italic = font.Italic,
            Underline = font.Underline,
            Strikethrough = font.Strikethrough,
            Color = font.Color,
            Fill = xf.FillId >= 0 && xf.FillId < _fills.Count ? _fills[xf.FillId] : null,
            Top = border.Top,
            Right = border.Right,
            Bottom = border.Bottom,
            Left = border.Left,
            Vertical = xf.Vertical,
            Wrap = xf.Wrap,
            Indent = xf.Indent,
        };
        style = new XlsxCellStyle(format, NumberFormatOf(xf.NumberFormatId), xf.Horizontal);
        _resolved[index] = style;
        return style;
    }

    private NumberFormat NumberFormatOf(int id)
    {
        if (_formats.TryGetValue(id, out var format))
        {
            return format;
        }

        var code = _numberFormats.TryGetValue(id, out var custom) ? custom : NumberFormat.BuiltInCode(id, _settings);
        format = code is null ? NumberFormat.General : NumberFormat.Parse(code);
        _formats[id] = format;
        return format;
    }

    /// <summary>Whether a boolean element (<c>&lt;b/&gt;</c>) is on: without <c>val</c>, or with a true one.</summary>
    private static bool IsOn(XmlReader reader) => reader.GetAttribute("val") is not ("0" or "false");

    /// <summary>Reads the children of the current element, calling <paramref name="child"/> for each element.</summary>
    private static void Children(XmlReader reader, Action<XmlReader> child)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                child(reader);
            }
        }
    }

    private static (string? Rgb, string? Indexed, string? Theme, string? Tint, string? Auto) ColorAttributes(XmlReader reader) =>
        (reader.GetAttribute("rgb"), reader.GetAttribute("indexed"), reader.GetAttribute("theme"), reader.GetAttribute("tint"), reader.GetAttribute("auto"));

    private static Action<XlsxColors> ReadFont(XmlReader reader, List<FontInfo> fonts)
    {
        var font = new FontInfo();
        var index = fonts.Count;
        fonts.Add(font);
        (string?, string?, string?, string?, string?)? color = null;
        Children(reader, child =>
        {
            switch (child.LocalName)
            {
                case "b":
                    font = font with { Bold = IsOn(child) };
                    break;
                case "i":
                    font = font with { Italic = IsOn(child) };
                    break;
                case "strike":
                    font = font with { Strikethrough = IsOn(child) };
                    break;
                case "u":
                    font = font with
                    {
                        Underline = child.GetAttribute("val") switch
                        {
                            "none" => CellUnderline.None,
                            "double" or "doubleAccounting" => CellUnderline.Double,
                            _ => CellUnderline.Single,
                        },
                    };
                    break;
                case "sz":
                    if (double.TryParse(child.GetAttribute("val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size is >= 1 and <= 409)
                    {
                        font = font with { Size = size };
                    }

                    break;
                case "name" or "rFont":
                    font = font with { Name = child.GetAttribute("val") is { Length: > 0 and <= 64 } name ? name : null };
                    break;
                case "color":
                    color = ColorAttributes(child);
                    break;
            }
        });
        fonts[index] = font;
        return colors =>
        {
            if (color is var (rgb, indexed, theme, tint, auto))
            {
                fonts[index] = fonts[index] with { Color = colors.Resolve(rgb, indexed, theme, tint, auto) };
            }
        };
    }

    private static Action<XlsxColors> ReadFill(XmlReader reader, List<PdfColor?> fills)
    {
        var index = fills.Count;
        fills.Add(null);
        string? pattern = null;
        (string?, string?, string?, string?, string?)? foreground = null, background = null, stop = null;
        Children(reader, child =>
        {
            switch (child.LocalName)
            {
                case "patternFill":
                    pattern = child.GetAttribute("patternType") ?? "none";
                    break;
                case "fgColor":
                    foreground = ColorAttributes(child);
                    break;
                case "bgColor":
                    background = ColorAttributes(child);
                    break;
                case "color" when stop is null:
                    stop = ColorAttributes(child); // The first stop of a gradient.
                    break;
            }
        });
        return colors =>
        {
            PdfColor? Resolve((string?, string?, string?, string?, string?)? c) =>
                c is var (rgb, indexed, theme, tint, auto) ? colors.Resolve(rgb, indexed, theme, tint, auto) : null;

            if (pattern is null)
            {
                fills[index] = Resolve(stop);
                return;
            }

            // Patterns are shown as their average color: the share of the foreground over the background.
            var coverage = pattern switch
            {
                "solid" => 1,
                "darkGray" => 0.75,
                "mediumGray" => 0.5,
                "lightGray" => 0.25,
                "gray125" => 0.125,
                "gray0625" => 0.0625,
                "none" => 0,
                _ => pattern.StartsWith("dark", StringComparison.Ordinal) ? 0.5 : 0.25,
            };
            if (coverage == 0)
            {
                return;
            }

            var fg = Resolve(foreground);
            var bg = Resolve(background);
            if (coverage == 1)
            {
                fills[index] = fg ?? bg;
                return;
            }

            var front = fg ?? PdfColor.Black;
            var back = bg ?? PdfColor.FromRgb(255, 255, 255);
            byte Mix(double f, double b) => (byte)Math.Round(((f * coverage) + (b * (1 - coverage))) * 255);
            fills[index] = PdfColor.FromRgb(Mix(front.Red, back.Red), Mix(front.Green, back.Green), Mix(front.Blue, back.Blue));
        };
    }

    private static Action<XlsxColors> ReadBorder(XmlReader reader, List<CellFormat> borders)
    {
        var index = borders.Count;
        borders.Add(CellFormat.Default);
        var sides = new Dictionary<string, (string Style, (string?, string?, string?, string?, string?)? Color)>();
        Children(reader, child =>
        {
            var side = child.LocalName switch
            {
                "left" or "start" => "left",
                "right" or "end" => "right",
                "top" => "top",
                "bottom" => "bottom",
                _ => null,
            };
            if (side is not null && child.GetAttribute("style") is { } style && style != "none")
            {
                (string?, string?, string?, string?, string?)? color = null;
                Children(child, c =>
                {
                    if (c.LocalName == "color")
                    {
                        color = ColorAttributes(c);
                    }
                });
                sides[side] = (style, color);
            }
        });
        return colors =>
        {
            CellBorder Side(string name)
            {
                if (!sides.TryGetValue(name, out var side))
                {
                    return CellBorder.None;
                }

                var color = side.Color is var (rgb, indexed, theme, tint, auto) ? colors.Resolve(rgb, indexed, theme, tint, auto) : null;
                var (width, style) = side.Style switch
                {
                    "hair" => (0.6, BorderStyle.Solid),
                    "medium" => (1.5, BorderStyle.Solid),
                    "thick" => (2.25, BorderStyle.Solid),
                    "double" => (2.25, BorderStyle.Double),
                    "dotted" => (0.75, BorderStyle.Dotted),
                    "dashed" or "dashDot" or "dashDotDot" => (0.75, BorderStyle.Dashed),
                    "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => (1.5, BorderStyle.Dashed),
                    _ => (0.75, BorderStyle.Solid),
                };
                return new CellBorder(width, color ?? PdfColor.Black, style);
            }

            borders[index] = CellFormat.Default with { Top = Side("top"), Right = Side("right"), Bottom = Side("bottom"), Left = Side("left") };
        };
    }

    private static XfInfo ReadXf(XmlReader reader)
    {
        static int Id(XmlReader reader, string name) =>
            int.TryParse(reader.GetAttribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

        var xf = new XfInfo
        {
            NumberFormatId = Id(reader, "numFmtId"),
            FontId = Id(reader, "fontId"),
            FillId = Id(reader, "fillId"),
            BorderId = Id(reader, "borderId"),
        };
        Children(reader, child =>
        {
            if (child.LocalName != "alignment")
            {
                return;
            }

            xf = xf with
            {
                Horizontal = child.GetAttribute("horizontal") switch
                {
                    "left" or "fill" or "justify" or "distributed" => CellAlignment.Left,
                    "center" or "centerContinuous" => CellAlignment.Center,
                    "right" => CellAlignment.Right,
                    _ => CellAlignment.General,
                },
                Vertical = child.GetAttribute("vertical") switch
                {
                    "top" => VerticalAlignment.Top,
                    "center" or "justify" or "distributed" => VerticalAlignment.Middle,
                    _ => VerticalAlignment.Bottom,
                },
                Wrap = child.GetAttribute("wrapText") is "1" or "true",
                Indent = int.TryParse(child.GetAttribute("indent"), NumberStyles.None, CultureInfo.InvariantCulture, out var indent) ? Math.Min(indent, 250) : 0,
            };
        });
        return xf;
    }

    private sealed record FontInfo
    {
        public string? Name { get; init; }

        public double? Size { get; init; }

        public bool Bold { get; init; }

        public bool Italic { get; init; }

        public CellUnderline Underline { get; init; }

        public bool Strikethrough { get; init; }

        public PdfColor? Color { get; init; }
    }

    private sealed record XfInfo
    {
        public int NumberFormatId { get; init; }

        public int FontId { get; init; }

        public int FillId { get; init; }

        public int BorderId { get; init; }

        public CellAlignment Horizontal { get; init; }

        public VerticalAlignment Vertical { get; init; } = VerticalAlignment.Bottom;

        public bool Wrap { get; init; }

        public int Indent { get; init; }
    }
}
