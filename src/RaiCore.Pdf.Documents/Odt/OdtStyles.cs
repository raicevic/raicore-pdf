using System.Xml.Linq;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Odt;

/// <summary>Where a style name is looked up: the content's automatic styles, or those of the master pages (styles.xml).</summary>
internal enum StyleScope
{
    /// <summary>The body: <c>content.xml</c>'s automatic styles, then the common styles.</summary>
    Content,

    /// <summary>Headers and footers: <c>styles.xml</c>'s automatic styles, then the common styles.</summary>
    Master,
}

/// <summary>Letter case changes of <c>fo:text-transform</c> that a <see cref="RunFormat"/> cannot express.</summary>
internal enum Casing
{
    /// <summary>The text as it is (capitals are <see cref="RunFormat.Caps"/>).</summary>
    None,

    /// <summary>Lowercase.</summary>
    Lower,

    /// <summary>The first letter of each word in capitals.</summary>
    Capitalize,
}

/// <summary>A run format with the letter case change to apply to its text.</summary>
internal readonly record struct TextFormat(RunFormat Run, Casing Casing = Casing.None);

/// <summary>A paragraph style resolved through its parents and the default style.</summary>
/// <param name="Format">The paragraph format (tab stop positions still relative to the indent).</param>
/// <param name="Text">The text format of the paragraph.</param>
/// <param name="BreakAfter">Whether a page break follows the paragraph.</param>
/// <param name="MasterPage">The master page the paragraph starts (a page break with new page styles), or <c>null</c>.</param>
/// <param name="PageNumber">The page number the paragraph's page restarts at, or <c>null</c>.</param>
/// <param name="ListStyle">The list style the paragraph style names, or <c>null</c>.</param>
/// <param name="OutlineLevel">The default outline level (1-based), or <c>null</c>.</param>
/// <param name="OwnProperties">The paragraph properties of the paragraph's own automatic style, which override a list's indents.</param>
internal sealed record ParagraphStyle(
    ParagraphFormat Format, TextFormat Text, bool BreakAfter, string? MasterPage, int? PageNumber, string? ListStyle, int? OutlineLevel, XElement? OwnProperties);

/// <summary>
/// The styles of an OpenDocument document: common styles (<c>office:styles</c>), the automatic styles of the content
/// and of the master pages, default styles, font faces, list and outline styles, page layouts, master pages and the
/// notes configuration. Paragraph and text styles are resolved through their <c>style:parent-style-name</c> chains
/// (with the family's default style first) and cached.
/// </summary>
internal sealed class OdtStyles
{
    /// <summary>The longest parent chain followed; longer (or circular) chains are cut.</summary>
    private const int MaxChain = 32;

    private readonly Dictionary<(string, string), XElement> _common = [];
    private readonly Dictionary<(string, string), XElement> _contentAutomatic = [];
    private readonly Dictionary<(string, string), XElement> _masterAutomatic = [];
    private readonly Dictionary<string, XElement> _defaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<(StyleScope, string), XElement> _listStyles = [];
    private readonly Dictionary<string, XElement> _pageLayouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _masterPages = new(StringComparer.Ordinal);
    private readonly Dictionary<(StyleScope, string?), ParagraphStyle> _paragraphs = [];
    private readonly Dictionary<(TextFormat, StyleScope, string), TextFormat> _texts = [];
    private readonly XElement? _commonRoot;

    public OdtStyles(XElement? stylesRoot, XElement contentRoot)
    {
        _commonRoot = Odf.Child(stylesRoot, Odf.Office, "styles") ?? Odf.Child(contentRoot, Odf.Office, "styles");
        foreach (var root in new[] { stylesRoot, contentRoot })
        {
            foreach (var face in Odf.Children(Odf.Child(root, Odf.Office, "font-face-decls"), Odf.Style, "font-face"))
            {
                if (Odf.Attr(face, Odf.Style, "name") is { } name && Odf.FamilyName(Odf.Attr(face, Odf.Svg, "font-family")) is { } family)
                {
                    _fonts.TryAdd(name, family);
                }
            }
        }

        Collect(_commonRoot, _common, StyleScope.Master, common: true);
        Collect(Odf.Child(stylesRoot, Odf.Office, "automatic-styles"), _masterAutomatic, StyleScope.Master, common: false);
        Collect(Odf.Child(contentRoot, Odf.Office, "automatic-styles"), _contentAutomatic, StyleScope.Content, common: false);
        foreach (var master in Odf.Children(Odf.Child(stylesRoot ?? contentRoot, Odf.Office, "master-styles"), Odf.Style, "master-page"))
        {
            if (Odf.Attr(master, Odf.Style, "name") is { } name && _masterPages.TryAdd(name, master))
            {
                MasterPages.Add(master);
            }
        }

        OutlineStyle = Odf.Child(_commonRoot, Odf.Text, "outline-style");
        var defaultParagraph = _defaults.GetValueOrDefault("paragraph");
        var tabDistance = Odf.Length(Odf.Child(defaultParagraph, Odf.Style, "paragraph-properties"), Odf.Style, "tab-stop-distance");
        DefaultTabStop = tabDistance is >= 1 ? Math.Min(tabDistance.Value, 1584) : 36;
        Language = Paragraph(null, StyleScope.Content).Text.Run.Language;
    }

    /// <summary>The master pages, in document order.</summary>
    public List<XElement> MasterPages { get; } = [];

    /// <summary>The outline style (the numbering of headings), or <c>null</c>.</summary>
    public XElement? OutlineStyle { get; }

    /// <summary>The distance between default tab stops.</summary>
    public double DefaultTabStop { get; }

    /// <summary>The default language, or <c>null</c>.</summary>
    public string? Language { get; }

    private void Collect(XElement? container, Dictionary<(string, string), XElement> styles, StyleScope scope, bool common)
    {
        foreach (var element in container?.Elements() ?? [])
        {
            var name = Odf.Attr(element, Odf.Style, "name");
            if (Odf.Is(element, Odf.Style, "style"))
            {
                if (name is not null)
                {
                    styles.TryAdd((Odf.Attr(element, Odf.Style, "family") ?? string.Empty, name), element);
                }
            }
            else if (Odf.Is(element, Odf.Style, "default-style") && common)
            {
                _defaults.TryAdd(Odf.Attr(element, Odf.Style, "family") ?? string.Empty, element);
            }
            else if (Odf.Is(element, Odf.Text, "list-style") && name is not null)
            {
                _listStyles.TryAdd((scope, name), element);
                if (common)
                {
                    _listStyles.TryAdd((StyleScope.Content, name), element);
                }
            }
            else if (Odf.Is(element, Odf.Style, "page-layout") && name is not null)
            {
                _pageLayouts.TryAdd(name, element);
            }
        }
    }

    /// <summary>A style by family and name: the scope's automatic styles first, then the common styles.</summary>
    public XElement? Find(string family, string? name, StyleScope scope)
    {
        if (name is null)
        {
            return null;
        }

        var automatic = scope == StyleScope.Content ? _contentAutomatic : _masterAutomatic;
        return automatic.GetValueOrDefault((family, name)) ?? _common.GetValueOrDefault((family, name));
    }

    /// <summary>Whether a style is an automatic one (of the scope).</summary>
    private bool IsAutomatic(string family, string name, StyleScope scope) =>
        (scope == StyleScope.Content ? _contentAutomatic : _masterAutomatic).ContainsKey((family, name));

    /// <summary>
    /// A style's chain, root first: the family's default style (when <paramref name="withDefault"/>), its ancestors
    /// (common styles) and the style itself.
    /// </summary>
    public List<XElement> Chain(string family, string? name, StyleScope scope, bool withDefault = true)
    {
        var chain = new List<XElement>();
        var style = Find(family, name, scope);
        while (style is not null && chain.Count < MaxChain && !chain.Contains(style))
        {
            chain.Add(style);
            style = Odf.Attr(style, Odf.Style, "parent-style-name") is { } parent ? _common.GetValueOrDefault((family, parent)) : null;
        }

        if (withDefault && _defaults.TryGetValue(family, out var defaultStyle))
        {
            chain.Add(defaultStyle);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>The property elements (<c>style:table-cell-properties</c> and the like) of a style's chain, root first.</summary>
    public List<XElement> Properties(string family, string? name, StyleScope scope, string properties) =>
        [.. Chain(family, name, scope).Select(s => Odf.Child(s, Odf.Style, properties)).OfType<XElement>()];

    /// <summary>The value of a property in a chain's property elements: the last one that sets it.</summary>
    public static string? Property(List<XElement> properties, XNamespace ns, string name)
    {
        for (var i = properties.Count - 1; i >= 0; i--)
        {
            if (Odf.Attr(properties[i], ns, name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>A list style by name, or <c>null</c>.</summary>
    public XElement? ListStyle(string? name, StyleScope scope) =>
        name is null ? null : _listStyles.GetValueOrDefault((scope, name)) ?? _listStyles.GetValueOrDefault((StyleScope.Content, name));

    /// <summary>A page layout by name, or <c>null</c>.</summary>
    public XElement? PageLayout(string? name) => name is null ? null : _pageLayouts.GetValueOrDefault(name);

    /// <summary>A master page by name, or <c>null</c>.</summary>
    public XElement? MasterPage(string? name) => name is null ? null : _masterPages.GetValueOrDefault(name);

    /// <summary>The notes configuration of a note class (<c>footnote</c>, <c>endnote</c>), or <c>null</c>.</summary>
    public XElement? NotesConfiguration(string noteClass) =>
        Odf.Children(_commonRoot, Odf.Text, "notes-configuration").FirstOrDefault(c => (Odf.Attr(c, Odf.Text, "note-class") ?? "footnote") == noteClass);

    /// <summary>A paragraph style (<c>null</c> for the default style) resolved through its chain.</summary>
    public ParagraphStyle Paragraph(string? name, StyleScope scope)
    {
        if (_paragraphs.TryGetValue((scope, name), out var resolved))
        {
            return resolved;
        }

        var format = ParagraphFormat.Default;
        var text = new TextFormat(RunFormat.Default with { FontSize = 12 });
        var breakAfter = false;
        string? master = null;
        int? pageNumber = null;
        string? listStyle = null;
        int? outline = null;
        double? leading = null;
        foreach (var style in Chain("paragraph", name, scope))
        {
            text = ApplyText(text, Odf.Child(style, Odf.Style, "text-properties"));
            (format, breakAfter, pageNumber, leading) = ApplyParagraph(format, Odf.Child(style, Odf.Style, "paragraph-properties"), breakAfter, pageNumber, leading);
            master = Odf.Attr(style, Odf.Style, "master-page-name") ?? master;
            listStyle = Odf.Attr(style, Odf.Style, "list-style-name") ?? listStyle;
            outline = Odf.Int(style, Odf.Style, "default-outline-level") ?? outline;
        }

        if (leading is { } extra && format.LineRule == LineSpacingRule.Multiple && format.LineSpacing == 1)
        {
            format = format with { LineRule = LineSpacingRule.AtLeast, LineSpacing = (text.Run.FontSize * 1.15) + extra };
        }

        var own = name is not null && IsAutomatic("paragraph", name, scope) ? Odf.Child(Find("paragraph", name, scope), Odf.Style, "paragraph-properties") : null;
        resolved = new ParagraphStyle(format with { StyleId = name }, text, breakAfter, master is { Length: > 0 } ? master : null, pageNumber, listStyle is { Length: > 0 } ? listStyle : null, outline, own);
        _paragraphs[(scope, name)] = resolved;
        return resolved;
    }

    /// <summary>A text format with a text style (and its parents) applied.</summary>
    public TextFormat Text(TextFormat format, string? name, StyleScope scope)
    {
        if (name is null)
        {
            return format;
        }

        if (_texts.TryGetValue((format, scope, name), out var resolved))
        {
            return resolved;
        }

        resolved = format;
        foreach (var style in Chain("text", name, scope, withDefault: false))
        {
            resolved = ApplyText(resolved, Odf.Child(style, Odf.Style, "text-properties"));
        }

        if (_texts.Count < 100_000)
        {
            _texts[(format, scope, name)] = resolved;
        }

        return resolved;
    }

    /// <summary>The font family of a <c>style:font-name</c> (a font face declaration), or the name itself.</summary>
    public string FontFamily(string name) => _fonts.GetValueOrDefault(name) ?? name;

    /// <summary>Applies text properties (<c>style:text-properties</c>) to a text format.</summary>
    public TextFormat ApplyText(TextFormat format, XElement? properties)
    {
        if (properties is null)
        {
            return format;
        }

        var run = format.Run;
        var casing = format.Casing;
        var size = Odf.Attr(properties, Odf.Fo, "font-size");
        if (Odf.Length(size) is { } points && points > 0)
        {
            run = run with { FontSize = Math.Clamp(points, 1, 1638) };
        }
        else if (Odf.Percent(size) is { } percent && percent > 0)
        {
            run = run with { FontSize = Math.Clamp(run.FontSize * percent / 100, 1, 1638) };
        }

        if (Odf.Attr(properties, Odf.Style, "font-name") is { } fontName)
        {
            run = run with { FontFamily = FontFamily(fontName) };
        }
        else if (Odf.FamilyName(Odf.Attr(properties, Odf.Fo, "font-family")) is { } family)
        {
            run = run with { FontFamily = family };
        }

        if (Odf.Attr(properties, Odf.Fo, "font-weight") is { } weight)
        {
            run = run with { Bold = weight == "bold" || (int.TryParse(weight, out var w) && w >= 600) };
        }

        if (Odf.Attr(properties, Odf.Fo, "font-style") is { } fontStyle)
        {
            run = run with { Italic = fontStyle is "italic" or "oblique" };
        }

        if (Odf.Attr(properties, Odf.Style, "use-window-font-color") == "true")
        {
            run = run with { Color = null };
        }
        else if (Odf.Color(Odf.Attr(properties, Odf.Fo, "color")) is { } color)
        {
            run = run with { Color = color };
        }

        if (Odf.Attr(properties, Odf.Fo, "background-color") is { } background)
        {
            run = run with { Background = Odf.Color(background) };
        }

        run = Underline(run, properties);
        var through = Odf.Attr(properties, Odf.Style, "text-line-through-style");
        var throughType = Odf.Attr(properties, Odf.Style, "text-line-through-type");
        if (through is not null || throughType is not null)
        {
            run = run with { Strike = through is not ("none" or null) ? throughType != "none" : throughType is "single" or "double" };
        }

        if (Odf.Attr(properties, Odf.Style, "text-position") is { } position)
        {
            run = Position(run, position);
        }

        if (Odf.Attr(properties, Odf.Fo, "font-variant") is { } variant)
        {
            run = run with { SmallCaps = variant == "small-caps" };
        }

        switch (Odf.Attr(properties, Odf.Fo, "text-transform"))
        {
            case "uppercase":
                (run, casing) = (run with { Caps = true }, Casing.None);
                break;
            case "lowercase":
                (run, casing) = (run with { Caps = false }, Casing.Lower);
                break;
            case "capitalize":
                (run, casing) = (run with { Caps = false }, Casing.Capitalize);
                break;
            case "none":
                (run, casing) = (run with { Caps = false }, Casing.None);
                break;
        }

        if (Odf.Attr(properties, Odf.Text, "display") is { } display)
        {
            run = run with { Hidden = display == "none" };
        }

        if (Odf.Attr(properties, Odf.Fo, "letter-spacing") is { } spacing)
        {
            run = run with { LetterSpacing = Math.Clamp(Odf.Length(spacing) ?? 0, -100, 100) };
        }

        if (Odf.Attr(properties, Odf.Fo, "language") is { } language)
        {
            var country = Odf.Attr(properties, Odf.Fo, "country");
            run = run with
            {
                Language = language is "zxx" or "none" || language.Length is 0 or > 8 ? null
                    : country is { Length: > 0 and <= 3 } and not "none" ? language + "-" + country : language,
            };
        }

        return new TextFormat(run, casing);
    }

    /// <summary>The underline of <c>style:text-underline-style</c> and <c>style:text-underline-type</c>.</summary>
    private static RunFormat Underline(RunFormat run, XElement properties)
    {
        var style = Odf.Attr(properties, Odf.Style, "text-underline-style");
        var type = Odf.Attr(properties, Odf.Style, "text-underline-type");
        if (style is null && type is null)
        {
            return run;
        }

        var underline = style switch
        {
            "none" => UnderlineStyle.None,
            "dotted" => UnderlineStyle.Dotted,
            "dash" or "long-dash" or "dot-dash" or "dot-dot-dash" => UnderlineStyle.Dashed,
            "wave" => UnderlineStyle.Wavy,
            null => run.Underline == UnderlineStyle.None ? UnderlineStyle.Single : run.Underline,
            _ => UnderlineStyle.Single,
        };
        if (type == "none")
        {
            underline = UnderlineStyle.None;
        }
        else if (type == "double" && underline != UnderlineStyle.None)
        {
            underline = UnderlineStyle.Double;
        }

        return run with { Underline = underline };
    }

    /// <summary>
    /// Raised or lowered text of <c>style:text-position</c> (<c>super 58%</c>, <c>sub</c>, <c>33% 100%</c>): smaller
    /// superscript or subscript, or a baseline shift at full size.
    /// </summary>
    private static RunFormat Position(RunFormat run, string position)
    {
        var parts = position.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return run;
        }

        var shift = parts[0] switch
        {
            "super" => 33,
            "sub" => -33,
            _ => Odf.Percent(parts[0]) ?? 0,
        };
        var scale = parts.Length > 1 ? Odf.Percent(parts[1]) ?? 58 : 58;
        if (shift == 0)
        {
            return run with { Position = VerticalPosition.Baseline, BaselineShift = 0 };
        }

        if (scale >= 100)
        {
            return run with { Position = VerticalPosition.Baseline, BaselineShift = Math.Clamp(shift, -100, 100) * run.FontSize / 100 };
        }

        return run with { Position = shift > 0 ? VerticalPosition.Superscript : VerticalPosition.Subscript, BaselineShift = 0 };
    }

    /// <summary>Applies paragraph properties (<c>style:paragraph-properties</c>) to a paragraph format.</summary>
    public static (ParagraphFormat Format, bool BreakAfter, int? PageNumber, double? Leading) ApplyParagraph(
        ParagraphFormat format, XElement? properties, bool breakAfter, int? pageNumber, double? leading)
    {
        if (properties is null)
        {
            return (format, breakAfter, pageNumber, leading);
        }

        if (Odf.Length(properties, Odf.Fo, "margin") is { } margin)
        {
            format = format with { IndentStart = margin, IndentEnd = margin, SpaceBefore = Math.Max(0, margin), SpaceAfter = Math.Max(0, margin) };
        }

        format = format with
        {
            IndentStart = Odf.Length(properties, Odf.Fo, "margin-left") ?? format.IndentStart,
            IndentEnd = Odf.Length(properties, Odf.Fo, "margin-right") ?? format.IndentEnd,
            SpaceBefore = Math.Max(0, Odf.Length(properties, Odf.Fo, "margin-top") ?? format.SpaceBefore),
            SpaceAfter = Math.Max(0, Odf.Length(properties, Odf.Fo, "margin-bottom") ?? format.SpaceAfter),
            FirstLineIndent = Odf.Length(properties, Odf.Fo, "text-indent") ?? format.FirstLineIndent,
        };

        if (Odf.Attr(properties, Odf.Fo, "text-align") is { } align)
        {
            format = format with
            {
                Alignment = align switch
                {
                    "center" => WordAlignment.Center,
                    "end" or "right" => WordAlignment.End,
                    "justify" => WordAlignment.Justify,
                    _ => WordAlignment.Start,
                },
            };
        }

        var lineHeight = Odf.Attr(properties, Odf.Fo, "line-height");
        if (lineHeight == "normal")
        {
            format = format with { LineRule = LineSpacingRule.Multiple, LineSpacing = 1 };
            leading = null;
        }
        else if (Odf.Percent(lineHeight) is { } percent && percent > 0)
        {
            format = format with { LineRule = LineSpacingRule.Multiple, LineSpacing = Math.Clamp(percent / 100, 0.1, 10) };
            leading = null;
        }
        else if (Odf.Length(lineHeight) is { } exact && exact > 0)
        {
            format = format with { LineRule = LineSpacingRule.Exact, LineSpacing = Math.Min(exact, 1584) };
            leading = null;
        }

        if (Odf.Length(properties, Odf.Style, "line-height-at-least") is { } least && least >= 0)
        {
            format = format with { LineRule = LineSpacingRule.AtLeast, LineSpacing = Math.Min(least, 1584) };
            leading = null;
        }

        if (Odf.Length(properties, Odf.Style, "line-spacing") is { } spacing)
        {
            leading = Math.Clamp(spacing, 0, 1584);
        }

        format = Borders(format, properties);
        if (Odf.Attr(properties, Odf.Fo, "background-color") is { } background)
        {
            format = format with { Shading = Odf.Color(background) };
        }

        if (Odf.Attr(properties, Odf.Fo, "keep-with-next") is { } keep)
        {
            format = format with { KeepWithNext = keep == "always" };
        }

        if (Odf.Attr(properties, Odf.Fo, "keep-together") is { } together)
        {
            format = format with { KeepLines = together == "always" };
        }

        if (Odf.Attr(properties, Odf.Fo, "break-before") is { } before)
        {
            format = format with { PageBreakBefore = before is "page" or "column" or "even-page" or "odd-page" };
        }

        if (Odf.Attr(properties, Odf.Fo, "break-after") is { } after)
        {
            breakAfter = after is "page" or "column" or "even-page" or "odd-page";
        }

        var widows = Odf.Int(properties, Odf.Fo, "widows");
        var orphans = Odf.Int(properties, Odf.Fo, "orphans");
        if (widows is not null || orphans is not null)
        {
            format = format with { WidowControl = (widows ?? 0) >= 2 || (orphans ?? 0) >= 2 };
        }

        if ((Odf.Attr(properties, Odf.Style, "contextual-spacing") ?? Odf.Attr(properties, Odf.LibreOffice, "contextual-spacing")) is { } contextual)
        {
            format = format with { ContextualSpacing = contextual == "true" };
        }

        if (Odf.Attr(properties, Odf.Style, "writing-mode") is { } mode && mode != "page")
        {
            format = format with { RightToLeft = mode.StartsWith("rl", StringComparison.Ordinal) };
        }

        if (Odf.Attr(properties, Odf.Style, "page-number") is { } number)
        {
            pageNumber = int.TryParse(number, out var value) ? Math.Clamp(value, 0, 1_000_000) : null;
        }

        if (Odf.Child(properties, Odf.Style, "tab-stops") is { } stops)
        {
            format = format with { TabStops = TabStops(stops) };
        }

        return (format, breakAfter, pageNumber, leading);
    }

    /// <summary>The borders of paragraph properties (<c>fo:border</c> and its sides).</summary>
    private static ParagraphFormat Borders(ParagraphFormat format, XElement properties)
    {
        var all = Odf.Border(Odf.Attr(properties, Odf.Fo, "border"));
        var top = Odf.Border(Odf.Attr(properties, Odf.Fo, "border-top")) ?? all;
        var right = Odf.Border(Odf.Attr(properties, Odf.Fo, "border-right")) ?? all;
        var bottom = Odf.Border(Odf.Attr(properties, Odf.Fo, "border-bottom")) ?? all;
        var left = Odf.Border(Odf.Attr(properties, Odf.Fo, "border-left")) ?? all;
        if (top is null && right is null && bottom is null && left is null)
        {
            return format;
        }

        var current = format.Borders;
        static BorderLine? Visible(BorderLine? line, BorderLine? fallback) => line is null ? fallback : line.Value.IsVisible ? line : null;
        var borders = new BoxSides(Visible(top, current.Top), Visible(right, current.Right), Visible(bottom, current.Bottom), Visible(left, current.Left));
        return format with { Borders = borders, BetweenBorder = Odf.Attr(properties, Odf.Style, "join-border") == "false" ? borders.Top : null };
    }

    /// <summary>The tab stops of <c>style:tab-stops</c>, by position (relative to the paragraph's indent).</summary>
    private static List<TabStop> TabStops(XElement stops)
    {
        var result = new List<TabStop>();
        foreach (var stop in Odf.Children(stops, Odf.Style, "tab-stop"))
        {
            if (result.Count >= 64 || Odf.Length(stop, Odf.Style, "position") is not { } position)
            {
                continue;
            }

            var alignment = Odf.Attr(stop, Odf.Style, "type") switch
            {
                "center" => TabAlignment.Center,
                "right" => TabAlignment.Right,
                "char" => TabAlignment.Decimal,
                _ => TabAlignment.Left,
            };
            char? leader = Odf.Attr(stop, Odf.Style, "leader-text") is { Length: > 0 } text && text[0] != ' ' ? text[0]
                : (Odf.Attr(stop, Odf.Style, "leader-style") ?? Odf.Attr(stop, Odf.Style, "leader-type")) switch
                {
                    "dotted" => '.',
                    "dash" or "long-dash" or "dot-dash" or "dot-dot-dash" => '-',
                    "solid" or "single" => '_',
                    _ => null,
                };
            result.Add(new TabStop(Math.Clamp(position, -1584, 1584), alignment, leader));
        }

        result.Sort((a, b) => a.Position.CompareTo(b.Position));
        return result;
    }
}
