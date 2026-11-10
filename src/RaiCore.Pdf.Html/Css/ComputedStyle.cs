using AngleSharp.Css.Dom;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using FlexDirection = RaiCore.Pdf.Layout.Blocks.FlexDirection;
using FlexWrap = RaiCore.Pdf.Layout.Blocks.FlexWrap;
using TextDecorationStyle = RaiCore.Pdf.Layout.TextDecorationStyle;

namespace RaiCore.Pdf.Html.Css;

internal enum CssDisplay
{
    None,
    Block,
    Inline,
    InlineBlock,
    ListItem,
    Table,
    TableRow,
    TableCell,
    TableRowGroup,
    TableCaption,
    Flex,
    InlineFlex,
    InlineTable,
}

internal enum CssWhiteSpace
{
    Normal,
    NoWrap,
    Pre,
    PreWrap,
    PreLine,
}

/// <summary>
/// The computed style of one element, converted from AngleSharp's strings into typed values. Lengths are in points,
/// except percentages and <c>auto</c>, which depend on the containing block and are resolved during box building.
/// </summary>
internal sealed record ComputedStyle
{
    public CssDisplay Display { get; init; } = CssDisplay.Inline;

    public IReadOnlyList<string> FontFamilies { get; init; } = ["serif"];

    public double FontSize { get; init; } = 12;

    public int FontWeight { get; init; } = 400;

    public bool Italic { get; init; }

    public PdfColor Color { get; init; } = PdfColor.Black;

    public LineHeight LineHeight { get; init; } = LineHeight.Normal;

    public TextAlignment TextAlign { get; init; } = TextAlignment.Left;

    public CssLength TextIndent { get; init; } = CssLength.Zero;

    public CssWhiteSpace WhiteSpace { get; init; }

    public string TextTransform { get; init; } = "none";

    public string FontFeatures { get; init; } = "normal";

    public CssLength[] Margin { get; init; } = [CssLength.Zero, CssLength.Zero, CssLength.Zero, CssLength.Zero];

    public CssLength[] Padding { get; init; } = [CssLength.Zero, CssLength.Zero, CssLength.Zero, CssLength.Zero];

    public double[] BorderWidth { get; init; } = [0, 0, 0, 0];

    public PdfColor[] BorderColor { get; init; } = [PdfColor.Black, PdfColor.Black, PdfColor.Black, PdfColor.Black];

    /// <summary>Per side (top, right, bottom, left); <c>none</c> is solid with width 0, <c>hidden</c> has width 0 too.</summary>
    public BorderStyle[] BorderStyle { get; init; } = [Layout.Blocks.BorderStyle.Solid, Layout.Blocks.BorderStyle.Solid, Layout.Blocks.BorderStyle.Solid, Layout.Blocks.BorderStyle.Solid];

    public PdfColor? Background { get; init; }

    public CssLength Width { get; init; } = CssLength.Auto;

    /// <summary>
    /// Whether the cascade gives <c>width</c>/<c>height</c> a value (including <c>auto</c>, and the presentational
    /// hints AngleSharp makes of <c>width</c> and <c>height</c> attributes), so an image's attributes do not apply.
    /// </summary>
    public bool WidthDeclared { get; init; }

    /// <inheritdoc cref="WidthDeclared"/>
    public bool HeightDeclared { get; init; }

    public CssLength MaxWidth { get; init; } = CssLength.Auto;

    public CssLength Height { get; init; } = CssLength.Auto;

    public bool BorderBox { get; init; }

    public PageBreak BreakBefore { get; init; }

    public PageBreak BreakAfter { get; init; }

    public PageBreakInside BreakInside { get; init; }

    public int Orphans { get; init; } = 2;

    public int Widows { get; init; } = 2;

    public string ListStyleType { get; init; } = "disc";

    public bool ListStyleInside { get; init; }

    public bool Hidden { get; init; }

    public bool BorderCollapse { get; init; }

    public double BorderSpacingX { get; init; }

    public double BorderSpacingY { get; init; }

    public bool TableLayoutFixed { get; init; }

    public string VerticalAlign { get; init; } = "baseline";

    public CssLength MinWidth { get; init; } = CssLength.Auto;

    public CssLength MinHeight { get; init; } = CssLength.Auto;

    public CssLength MaxHeight { get; init; } = CssLength.Auto;

    public FlexDirection FlexDirection { get; init; }

    public FlexWrap FlexWrap { get; init; }

    public JustifyContent JustifyContent { get; init; }

    public FlexAlignment AlignItems { get; init; } = FlexAlignment.Stretch;

    public AlignContent AlignContent { get; init; }

    /// <summary><c>null</c> for <c>align-self: auto</c>.</summary>
    public FlexAlignment? AlignSelf { get; init; }

    public double FlexGrow { get; init; }

    public double FlexShrink { get; init; } = 1;

    /// <summary>A length, a percentage or <c>auto</c>; see also <see cref="FlexBasisContent"/>.</summary>
    public CssLength FlexBasis { get; init; } = CssLength.Auto;

    public bool FlexBasisContent { get; init; }

    public int Order { get; init; }

    public TextDecorationLines TextDecorationLine { get; init; }

    public TextDecorationStyle TextDecorationStyle { get; init; }

    /// <summary><c>null</c> for <c>currentColor</c>.</summary>
    public PdfColor? TextDecorationColor { get; init; }

    /// <summary>A length, a percentage of 1em, or auto (the font's).</summary>
    public CssLength TextDecorationThickness { get; init; } = CssLength.Auto;

    /// <summary>A length, a percentage of 1em, or auto (the font's).</summary>
    public CssLength TextUnderlineOffset { get; init; } = CssLength.Auto;

    /// <summary><c>letter-spacing</c> in points (<c>normal</c> is 0).</summary>
    public double LetterSpacing { get; init; }

    /// <summary><c>word-spacing</c> in points (<c>normal</c> is 0).</summary>
    public double WordSpacing { get; init; }

    public CssLength RowGap { get; init; } = CssLength.Zero;

    public CssLength ColumnGap { get; init; } = CssLength.Zero;

    /// <summary>
    /// Reads an element's computed style (AngleSharp has already applied the cascade, inheritance and em units, and
    /// evaluated math functions without percentages). Invalid values fall back to the property's initial value.
    /// </summary>
    /// <param name="css">The computed style declaration.</param>
    /// <param name="rootFontSize">The root element's font size in points, for <c>rem</c>.</param>
    /// <param name="viewport">The page content area, for viewport units AngleSharp leaves in a value.</param>
    public static ComputedStyle From(ICssStyleDeclaration css, double rootFontSize, CssViewport viewport = default)
    {
        string Get(string name) => css.GetPropertyValue(name) ?? string.Empty;

        var fontSize = CssValues.ParseLength(Get("font-size"), 12, rootFontSize, viewport) is { Kind: CssLengthKind.Points } size && size.Value > 0 ? size.Value : 12;
        CssLength? Parse(string value) => CssValues.TryParseLength(value, fontSize, rootFontSize, viewport);

        // Margins, padding and the like are 0 when unset or invalid; sizes are auto (AngleSharp returns an empty string when unset).
        CssLength Length(string name) => Parse(Get(name)) ?? CssLength.Zero;
        CssLength Size(string name) => Parse(Get(name))?.NonNegative() ?? CssLength.Auto;
        CssLength[] Sides(string prefix, string suffix = "") =>
            [Length($"{prefix}-top{suffix}"), Length($"{prefix}-right{suffix}"), Length($"{prefix}-bottom{suffix}"), Length($"{prefix}-left{suffix}")];

        var color = CssValues.ParseColor(Get("color")) ?? PdfColor.Black;
        string[] sides = ["top", "right", "bottom", "left"];
        var borderWidth = sides.Select(side =>
            Get($"border-{side}-style") is "" or "none" or "hidden" ? 0 : Math.Max(0, Length($"border-{side}-width").Resolve(0))).ToArray();
        var borderColor = sides.Select(side => CssValues.ParseColor(Get($"border-{side}-color")) ?? color).ToArray();
        var borderStyle = sides.Select(side => ParseBorderStyle(Get($"border-{side}-style"))).ToArray();

        return new ComputedStyle
        {
            Display = ParseDisplay(Get("display")),
            FontFamilies = CssValues.ParseFontFamilies(Get("font-family")),
            FontSize = fontSize,
            FontWeight = CssValues.ParseFontWeight(Get("font-weight")),
            Italic = Get("font-style") is "italic" or "oblique" || Get("font-style").StartsWith("oblique", StringComparison.Ordinal),
            Color = color,
            LineHeight = ParseLineHeight(Get("line-height"), fontSize, rootFontSize, viewport),
            TextAlign = Get("text-align") switch
            {
                "right" or "end" => TextAlignment.Right,
                "center" or "-webkit-center" => TextAlignment.Center,
                "justify" => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            TextIndent = Length("text-indent"),
            LetterSpacing = Length("letter-spacing").Resolve(fontSize),
            WordSpacing = Length("word-spacing").Resolve(fontSize),
            WhiteSpace = Get("white-space") switch
            {
                "nowrap" => CssWhiteSpace.NoWrap,
                "pre" => CssWhiteSpace.Pre,
                "pre-wrap" or "break-spaces" => CssWhiteSpace.PreWrap,
                "pre-line" => CssWhiteSpace.PreLine,
                _ => CssWhiteSpace.Normal,
            },
            TextTransform = Get("text-transform") is { Length: > 0 } transform ? transform : "none",
            FontFeatures = Get("font-feature-settings") is { Length: > 0 } features ? features : "normal",
            Margin = Sides("margin"),
            Padding = [.. Sides("padding").Select(p => p.NonNegative())],
            BorderWidth = borderWidth,
            BorderColor = borderColor,
            BorderStyle = borderStyle,
            Background = CssValues.ParseColor(Get("background-color")),
            Width = Size("width"),
            WidthDeclared = Get("width").Length > 0,
            HeightDeclared = Get("height").Length > 0,
            MaxWidth = Size("max-width"),
            Height = Size("height"),
            BorderBox = Get("box-sizing") == "border-box",
            // Legacy page-break-* properties are rewritten to break-* before parsing (see LegacyBreaks).
            BreakBefore = ParseBreak(Get("break-before")),
            BreakAfter = ParseBreak(Get("break-after")),
            BreakInside = Get("break-inside") is "avoid" or "avoid-page" ? PageBreakInside.Avoid : PageBreakInside.Auto,
            Orphans = Math.Max(1, CssValues.ParseInteger(Get("orphans"), 2)),
            Widows = Math.Max(1, CssValues.ParseInteger(Get("widows"), 2)),
            ListStyleType = Get("list-style-type") is { Length: > 0 } list ? list : "disc",
            ListStyleInside = Get("list-style-position") == "inside",
            Hidden = Get("visibility") is "hidden" or "collapse",
            BorderCollapse = Get("border-collapse") == "collapse",
            BorderSpacingX = Spacing(0),
            BorderSpacingY = Spacing(1),
            TableLayoutFixed = Get("table-layout") == "fixed",
            VerticalAlign = Get("vertical-align") is { Length: > 0 } valign ? valign : "baseline",
            MinWidth = Size("min-width"),
            MinHeight = Size("min-height"),
            MaxHeight = Size("max-height"),
            FlexDirection = Get("flex-direction") switch
            {
                "row-reverse" => FlexDirection.RowReverse,
                "column" => FlexDirection.Column,
                "column-reverse" => FlexDirection.ColumnReverse,
                _ => FlexDirection.Row,
            },
            FlexWrap = Get("flex-wrap") switch
            {
                "wrap" => FlexWrap.Wrap,
                "wrap-reverse" => FlexWrap.WrapReverse,
                _ => FlexWrap.NoWrap,
            },
            JustifyContent = ParseJustify(Get("justify-content") is { Length: > 0 } justify ? justify : Get("place-content")),
            AlignItems = ParseAlignment(Get("align-items")) ?? FlexAlignment.Stretch,
            AlignContent = Get("align-content").Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() switch
            {
                "flex-start" or "start" => AlignContent.FlexStart,
                "flex-end" or "end" => AlignContent.FlexEnd,
                "center" => AlignContent.Center,
                "space-between" => AlignContent.SpaceBetween,
                "space-around" => AlignContent.SpaceAround,
                "space-evenly" => AlignContent.SpaceEvenly,
                _ => AlignContent.Stretch,
            },
            AlignSelf = ParseAlignment(Get("align-self")),
            FlexGrow = Math.Max(0, ParseNumber(Get("flex-grow"), 0)),
            FlexShrink = Math.Max(0, ParseNumber(Get("flex-shrink"), 1)),
            FlexBasis = Get("flex-basis") is "" or "auto" or "content" ? CssLength.Auto : Size("flex-basis"),
            FlexBasisContent = Get("flex-basis") == "content",
            Order = CssValues.ParseInteger(Get("order"), 0),
            TextDecorationLine = ParseDecorationLines(Get("text-decoration-line")),
            TextDecorationStyle = Get("text-decoration-style") switch
            {
                "double" => TextDecorationStyle.Double,
                "dotted" => TextDecorationStyle.Dotted,
                "dashed" => TextDecorationStyle.Dashed,
                "wavy" => TextDecorationStyle.Wavy,
                _ => TextDecorationStyle.Solid,
            },
            TextDecorationColor = Get("text-decoration-color") is { Length: > 0 } decorationColor and not "currentcolor" and not "currentColor"
                ? CssValues.ParseColor(decorationColor)
                : null,
            // Invalid values are auto (the initial value); percentages, also inside calc(), are of 1em.
            TextDecorationThickness = Parse(Get("text-decoration-thickness"))?.NonNegative() ?? CssLength.Auto, // from-font is auto too.
            TextUnderlineOffset = Parse(Get("text-underline-offset")) ?? CssLength.Auto,
            RowGap = Gap("row-gap"),
            ColumnGap = Gap("column-gap"),
        };

        // Gaps are normal (0 outside grids and multi-column) when unset or invalid.
        CssLength Gap(string name) => Parse(Get(name)) is { IsAuto: false } gap ? gap.NonNegative() : CssLength.Zero;

        // border-spacing: one length for both directions, or horizontal then vertical.
        double Spacing(int index)
        {
            var parts = CssValues.SplitComponents(Get("border-spacing"));
            var part = parts.Count == 0 ? null : parts[Math.Min(index, parts.Count - 1)];
            return Math.Max(0, CssValues.ParseLength(part, fontSize, rootFontSize, viewport).Resolve(0));
        }
    }

    private static BorderStyle ParseBorderStyle(string value) => value switch
    {
        "dashed" => Layout.Blocks.BorderStyle.Dashed,
        "dotted" => Layout.Blocks.BorderStyle.Dotted,
        "double" => Layout.Blocks.BorderStyle.Double,
        "groove" => Layout.Blocks.BorderStyle.Groove,
        "ridge" => Layout.Blocks.BorderStyle.Ridge,
        "inset" => Layout.Blocks.BorderStyle.Inset,
        "outset" => Layout.Blocks.BorderStyle.Outset,
        "hidden" => Layout.Blocks.BorderStyle.Hidden,
        _ => Layout.Blocks.BorderStyle.Solid,
    };

    private static CssDisplay ParseDisplay(string value) => value switch
    {
        "none" => CssDisplay.None,
        "flex" => CssDisplay.Flex,
        "inline-flex" => CssDisplay.InlineFlex,
        "block" or "grid" or "flow-root" or "flow" => CssDisplay.Block,
        "inline-block" or "inline-grid" => CssDisplay.InlineBlock,
        "inline-table" => CssDisplay.InlineTable,
        "list-item" => CssDisplay.ListItem,
        "table" => CssDisplay.Table,
        "table-row" => CssDisplay.TableRow,
        "table-cell" => CssDisplay.TableCell,
        "table-row-group" or "table-header-group" or "table-footer-group" => CssDisplay.TableRowGroup,
        "table-caption" => CssDisplay.TableCaption,
        "" or "inline" or "contents" => CssDisplay.Inline,
        _ => CssDisplay.Block,
    };

    private static TextDecorationLines ParseDecorationLines(string value)
    {
        var lines = TextDecorationLines.None;
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            lines |= token switch
            {
                "underline" => TextDecorationLines.Underline,
                "overline" => TextDecorationLines.Overline,
                "line-through" => TextDecorationLines.LineThrough,
                _ => TextDecorationLines.None,
            };
        }

        return lines;
    }

    /// <summary>justify-content (or the last value of place-content); the last keyword wins over safe/unsafe.</summary>
    private static JustifyContent ParseJustify(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() switch
    {
        "flex-end" or "end" or "right" => JustifyContent.FlexEnd,
        "center" => JustifyContent.Center,
        "space-between" => JustifyContent.SpaceBetween,
        "space-around" => JustifyContent.SpaceAround,
        "space-evenly" => JustifyContent.SpaceEvenly,
        _ => JustifyContent.FlexStart,
    };

    /// <summary>align-items/align-self; <c>null</c> for auto (and unset).</summary>
    private static FlexAlignment? ParseAlignment(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() switch
    {
        "flex-start" or "start" or "self-start" => FlexAlignment.FlexStart,
        "flex-end" or "end" or "self-end" => FlexAlignment.FlexEnd,
        "center" => FlexAlignment.Center,
        "baseline" => FlexAlignment.Baseline,
        "stretch" or "normal" => FlexAlignment.Stretch,
        _ => null,
    };

    private static double ParseNumber(string value, double fallback) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static LineHeight ParseLineHeight(string value, double fontSize, double rootFontSize, CssViewport viewport)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "normal")
        {
            return LineHeight.Normal;
        }

        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var factor))
        {
            return LineHeight.Multiple(Math.Max(0, factor));
        }

        // Percentages refer to the font size; invalid values are normal (the initial value).
        var length = CssValues.TryParseLength(value, fontSize, rootFontSize, viewport);
        return length?.Kind switch
        {
            CssLengthKind.Percent => LineHeight.Multiple(Math.Max(0, length.Value.Value / 100)),
            CssLengthKind.Points or CssLengthKind.Calc => LineHeight.Points(Math.Max(0, length.Value.Resolve(fontSize))),
            _ => LineHeight.Normal,
        };
    }

    private static PageBreak ParseBreak(string value) => value switch
    {
        "page" or "left" or "right" or "recto" or "verso" or "always" => PageBreak.Page,
        "avoid" or "avoid-page" => PageBreak.Avoid,
        _ => PageBreak.Auto,
    };
}
