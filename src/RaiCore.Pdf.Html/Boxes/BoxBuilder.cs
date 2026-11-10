using System.Globalization;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Html.Css;
using RaiCore.Pdf.Images;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Blocks;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Html.Boxes;

/// <summary>
/// Builds layout blocks from the styled DOM, following the CSS visual formatting model: block-level elements become
/// blocks; runs of inline content inside a block become anonymous paragraph blocks; percentages and auto margins are
/// resolved against the containing block's width. Tables are stacked as blocks until table layout lands.
/// </summary>
internal sealed partial class BoxBuilder
{
    private readonly IWindow _window;
    private readonly FontCollection _fonts;
    private readonly HtmlConverterOptions _options;
    private readonly Uri? _baseUri;
    private readonly Dictionary<IElement, ComputedStyle> _styles = [];
    private readonly Dictionary<string, IReadOnlyList<PdfFont>> _fontLists = new(StringComparer.Ordinal);
    private readonly Dictionary<ComputedStyle, TextStyle> _textStyles = [];
    private readonly Dictionary<string, PdfImage?> _images = new(StringComparer.Ordinal);
    private readonly HashSet<Block> _anonymous = new(ReferenceEqualityComparer.Instance);
    private readonly double _rootFontSize;
    private readonly CssViewport _viewport;
    private readonly Paged.PagedMedia? _paged;
    private readonly WebFonts? _webFonts;
    private double _pageContentWidth;

    /// <summary>
    /// The content height of the nearest ancestor with a definite height, which percentage heights resolve against;
    /// <c>null</c> when it is auto (then percentage heights are auto too, as in CSS). The root's is the page area.
    /// </summary>
    private double? _containingHeight;

    /// <summary>
    /// The text decorations in effect (CSS: decorations propagate to all in-flow descendants' text, and descendants
    /// cannot remove them), outermost first.
    /// </summary>
    private IReadOnlyList<TextDecoration> _decorations = [];

    /// <summary>The text decorations the text being collected gets.</summary>
    public IReadOnlyList<TextDecoration> CurrentDecorations => _decorations;

    public BoxBuilder(IDocument document, HtmlConverterOptions options, FontCollection fonts, Uri? baseUri, Paged.PagedMedia? paged = null, WebFonts? webFonts = null)
    {
        _paged = paged;
        _webFonts = webFonts;
        _window = document.DefaultView ?? throw new InvalidOperationException("The document has no window for style computation.");
        _options = options;
        _fonts = fonts;
        _baseUri = baseUri;
        Document = document;
        _viewport = document.Context.GetService<AngleSharp.Css.IRenderDevice>() is { } device
            ? new CssViewport(device.ViewPortWidth * CssValues.PointsPerPixel, device.ViewPortHeight * CssValues.PointsPerPixel)
            : default;
        _rootFontSize = 12;
        if (document.DocumentElement is { } root)
        {
            _rootFontSize = Style(root).FontSize;
        }
    }

    public IDocument Document { get; }

    public List<Block> Build(double contentWidth, double? contentHeight = null)
    {
        _pageContentWidth = contentWidth;
        _containingHeight = contentHeight;
        var root = Document.DocumentElement;
        if (root is null || Style(root).Display == CssDisplay.None)
        {
            return [];
        }

        return BuildBlock(root, Style(root), contentWidth, marker: null) is { } block ? [block] : [];
    }

    public ComputedStyle Style(IElement element)
    {
        if (!_styles.TryGetValue(element, out var style))
        {
            style = ComputedStyle.From(_window.GetComputedStyle(element), _rootFontSize, _viewport);
            _styles[element] = style;
        }

        return style;
    }

    public TextStyle TextStyleFor(ComputedStyle style)
    {
        if (_textStyles.TryGetValue(style, out var cached))
        {
            return cached;
        }

        var textStyle = new TextStyle(FontsFor(style), style.FontSize)
        {
            Color = style.Color,
            LineHeight = style.LineHeight,
            LetterSpacing = style.LetterSpacing,
            WordSpacing = style.WordSpacing,
            Shaping = new ShapingOptions { Features = ParseFeatures(style.FontFeatures) },
        };
        _textStyles[style] = textStyle;
        return textStyle;
    }

    private Block? BuildBlock(IElement element, ComputedStyle style, double containingWidth, string? marker, bool flexItem = false)
    {
        if (style.Display is CssDisplay.Flex or CssDisplay.InlineFlex)
        {
            return BuildFlex(element, style with { Display = CssDisplay.Flex }, containingWidth, flexItem);
        }

        var (box, contentWidth) = BoxStyleFor(style, containingWidth, flexItem, _containingHeight);
        var children = new List<Block>();
        var collector = new InlineCollector(this, style, contentWidth);
        if (marker is not null)
        {
            collector.SetMarker(marker);
        }

        var outerHeight = EnterBox(box);
        var outerDecorations = EnterDecoration(style, 0);
        AddChildren(element, style, contentWidth, children, collector);
        Flush(collector, children);
        _decorations = outerDecorations;
        _containingHeight = outerHeight;

        // A block whose only content is one anonymous paragraph becomes a paragraph block with the element's box.
        // Elements with string-set carry a marker, so running headers know which page they start on.
        var pageMarker = _paged?.MarkerFor(element);
        if (children is [ParagraphBlock only] && _anonymous.Contains(only))
        {
            return new ParagraphBlock(only.Paragraph, box) { Marker = pageMarker, Bookmark = BookmarkFor(element), Id = IdFor(element), Link = LinkFor(element) };
        }

        return new ContainerBlock(box, children) { Marker = pageMarker, Bookmark = BookmarkFor(element), Id = IdFor(element), Link = LinkFor(element) };
    }

    /// <summary>
    /// The PDF bookmark for an element's block (headings by default), unless bookmarks are off. Running elements may
    /// get one too, but it has no effect: margin boxes are drawn outside the flow, which is what the outline is made of.
    /// </summary>
    private Bookmark? BookmarkFor(IElement element) =>
        _options.CreateBookmarks ? _paged?.BookmarkFor(element, pseudo => GeneratedText(element, pseudo)) : null;

    /// <summary>
    /// Adds the element's children, with its <c>::before</c> and <c>::after</c>: inline content goes to the collector,
    /// block-level children become blocks.
    /// </summary>
    /// <param name="element">The element whose children are added.</param>
    /// <param name="style">Its computed style.</param>
    /// <param name="contentWidth">The width of the block the inline content flows in.</param>
    /// <param name="children">Receives block-level children and finished paragraphs.</param>
    /// <param name="collector">Collects inline content.</param>
    /// <param name="inherited">
    /// The vertical alignment of the inline element whose children these are (nested inline elements' shifts add up);
    /// the baseline for a block's own content.
    /// </param>
    private void AddChildren(IElement element, ComputedStyle style, double contentWidth, List<Block> children, InlineCollector collector, InlineAlignment inherited = default)
    {
        var listIndex = ListStart(element);
        foreach (var node in WithGeneratedContent(element))
        {
            switch (node)
            {
                case IText text:
                    collector.AddText(text.Data, style, inherited);
                    break;
                case IElement child:
                    var childStyle = Style(child);
                    if (childStyle.Display == CssDisplay.None)
                    {
                        break;
                    }

                    if (_paged?.RunningName(child) is { } runningName)
                    {
                        // A running element leaves the flow; an empty marker block keeps its place for element().
                        Flush(collector, children);
                        var content = BuildRunning(child, childStyle);
                        children.Add(new ContainerBlock { Marker = new Paged.RunningMarker(runningName, content is null ? [] : [content]) });
                        break;
                    }

                    if (child.LocalName == "br")
                    {
                        collector.AddLineBreak(childStyle);
                        break;
                    }

                    if (child.LocalName is "img" or "svg")
                    {
                        AddImage(child, childStyle, contentWidth, children, collector, VerticalAlign(inherited, childStyle, style));
                        break;
                    }

                    if (childStyle.Display == CssDisplay.Inline)
                    {
                        var align = VerticalAlign(inherited, childStyle, style);
                        var box = InlineElementBox(childStyle, contentWidth);
                        var link = LinkFor(child);
                        AddAnchors(child, collector);
                        if (box is not null)
                        {
                            collector.BeginInline(box, TextStyleFor(childStyle) with { VerticalAlign = align });
                        }

                        if (link is not null)
                        {
                            collector.BeginLink(link);
                        }

                        var outerDecorations = EnterDecoration(childStyle, align.Kind == InlineAlignmentKind.Baseline ? align.Shift : 0);
                        AddChildren(child, childStyle, contentWidth, children, collector, align);
                        _decorations = outerDecorations;
                        if (link is not null)
                        {
                            collector.EndLink();
                        }

                        if (box is not null)
                        {
                            collector.EndInline();
                        }

                        break;
                    }

                    if (childStyle.Display is CssDisplay.InlineBlock or CssDisplay.InlineFlex or CssDisplay.InlineTable)
                    {
                        AddAnchors(child, collector);
                        if (BuildAtomicInline(child, childStyle, contentWidth) is { } box)
                        {
                            collector.AddBox(box, VerticalAlign(inherited, childStyle, style));
                        }

                        break;
                    }

                    // A block-level child ends the current run of inline content.
                    Flush(collector, children);
                    var blockStart = children.Count;
                    if (childStyle.Display == CssDisplay.Table)
                    {
                        children.AddRange(BuildTable(child, childStyle, contentWidth));
                        LinkBlocks(children, blockStart, collector.CurrentLink);
                        break;
                    }

                    string? marker = null;
                    if (childStyle.Display == CssDisplay.ListItem)
                    {
                        marker = NextListMarker(element, child, childStyle, ref listIndex);
                    }

                    if (BuildBlock(child, childStyle, contentWidth, marker) is { } block)
                    {
                        children.Add(block);
                    }

                    LinkBlocks(children, blockStart, collector.CurrentLink);
                    break;
            }
        }
    }

    /// <summary>
    /// The block inside an atomic inline box (CSS inline-block, inline-flex, inline-table). Like a flex item, it
    /// shrinks to fit (no width from <c>max-width</c>) and its auto margins are 0.
    /// </summary>
    private Block? BuildAtomicInline(IElement element, ComputedStyle style, double containingWidth)
    {
        // Text decorations do not propagate into atomic inline boxes.
        var outerDecorations = _decorations;
        _decorations = [];
        try
        {
            return BuildAtomicInlineCore(element, style, containingWidth);
        }
        finally
        {
            _decorations = outerDecorations;
        }
    }

    private Block? BuildAtomicInlineCore(IElement element, ComputedStyle style, double containingWidth)
    {
        if (style.Display == CssDisplay.InlineTable)
        {
            var blocks = BuildTable(element, style with { Display = CssDisplay.Table }, containingWidth);
            return blocks.Count == 1 ? blocks[0] : new ContainerBlock(null, blocks);
        }

        var display = style.Display == CssDisplay.InlineFlex ? CssDisplay.Flex : CssDisplay.Block;
        return BuildBlock(element, style with { Display = display }, containingWidth, marker: null, flexItem: true);
    }

    /// <summary>A running element is laid out as a block (or a flex container) in a page margin box.</summary>
    private Block? BuildRunning(IElement element, ComputedStyle style)
    {
        var outerHeight = _containingHeight;
        var outerDecorations = _decorations;
        _containingHeight = null; // Margin boxes have no definite height.
        _decorations = []; // A running element leaves the flow: no decorations from its ancestors.
        var display = style.Display is CssDisplay.Flex or CssDisplay.InlineFlex ? CssDisplay.Flex : CssDisplay.Block;
        var block = BuildBlock(element, style with { Display = display }, _pageContentWidth, marker: null);
        _containingHeight = outerHeight;
        _decorations = outerDecorations;
        return block;
    }

    /// <summary>
    /// Adds the element's text decoration (if it declares one) to those in effect for its content, with the element's
    /// font for the line positions and its baseline shift. Returns the previous decorations, to restore afterwards.
    /// </summary>
    private IReadOnlyList<TextDecoration> EnterDecoration(ComputedStyle style, double baselineShift)
    {
        var outer = _decorations;
        if (style.TextDecorationLine == TextDecorationLines.None)
        {
            return outer;
        }

        double? Length(CssLength length) => length.IsAuto ? null : length.Resolve(style.FontSize); // Percentages of 1em.

        var decoration = new TextDecoration(style.TextDecorationLine, style.TextDecorationColor ?? style.Color)
        {
            Style = style.TextDecorationStyle,
            Thickness = Length(style.TextDecorationThickness),
            UnderlineOffset = Length(style.TextUnderlineOffset),
            Font = TextStyleFor(style).Fonts[0],
            FontSize = style.FontSize,
            BaselineShift = baselineShift,
        };
        _decorations = [.. outer, decoration];
        return outer;
    }

    /// <summary>
    /// Makes a box's content height the containing height of its descendants (or auto when the box has none).
    /// Returns the previous value, to restore when the box is done.
    /// </summary>
    private double? EnterBox(BoxStyle box)
    {
        var outer = _containingHeight;
        _containingHeight = box.Height is { } height ? Math.Max(0, height - box.Padding.Vertical - box.Border.Widths.Vertical) : null;
        return outer;
    }

    /// <summary>
    /// The box of an inline element (a <c>span</c> with a background, border, padding or horizontal margins), or
    /// <c>null</c> when it has none. Widths and heights do not apply to inline elements.
    /// </summary>
    private static BoxStyle? InlineElementBox(ComputedStyle style, double containingWidth)
    {
        var (box, _) = BoxStyleFor(
            style with { Width = CssLength.Auto, MinWidth = CssLength.Auto, MaxWidth = CssLength.Auto, Height = CssLength.Auto, MinHeight = CssLength.Auto, MaxHeight = CssLength.Auto },
            containingWidth,
            flexItem: true);
        var visible = box.Background is not null || box.Border.Widths != Thickness.Zero || box.Padding != Thickness.Zero
            || box.Margin.Left != 0 || box.Margin.Right != 0;
        return visible ? box : null;
    }

    /// <summary>
    /// An inline element's vertical alignment (CSS <c>vertical-align</c>) given its parent's: <c>sub</c>, <c>super</c>,
    /// lengths and percentages raise or lower the parent's baseline (sub by a fifth and super by a third of the
    /// parent's font size, as browsers do); keywords align to the line, keeping the parent's shift.
    /// </summary>
    private InlineAlignment VerticalAlign(InlineAlignment parent, ComputedStyle style, ComputedStyle parentStyle)
    {
        var value = style.VerticalAlign.Trim().ToUpperInvariant();
        InlineAlignment Kind(InlineAlignmentKind kind) => new(kind, parent.Shift);
        switch (value)
        {
            case "" or "BASELINE":
                return parent;
            case "SUB":
                return parent with { Shift = parent.Shift - (parentStyle.FontSize / 5) };
            case "SUPER":
                return parent with { Shift = parent.Shift + (parentStyle.FontSize / 3) };
            case "MIDDLE":
                return Kind(InlineAlignmentKind.Middle);
            case "TEXT-TOP":
                return Kind(InlineAlignmentKind.TextTop);
            case "TEXT-BOTTOM":
                return Kind(InlineAlignmentKind.TextBottom);
            case "TOP":
                return Kind(InlineAlignmentKind.Top);
            case "BOTTOM":
                return Kind(InlineAlignmentKind.Bottom);
        }

        // Percentages (also inside calc()) refer to the element's line height.
        var length = CssValues.ParseLength(style.VerticalAlign, style.FontSize, _rootFontSize, _viewport);
        var shift = length.IsAuto ? 0
            : length.HasPercentage ? length.Resolve(TextStyleFor(style).LineHeight.Resolve(TextStyleFor(style).Fonts[0], style.FontSize))
            : length.Value;
        return parent with { Shift = parent.Shift + shift };
    }

    /// <summary>The marker text of a list item, advancing the list's counter (honoring value and reversed).</summary>
    private static string? NextListMarker(IElement list, IElement item, ComputedStyle style, ref int listIndex)
    {
        listIndex = item.GetAttribute("value") is { } value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : listIndex;
        var marker = ListMarkers.Format(style.ListStyleType, listIndex);
        listIndex += IsReversed(list) ? -1 : 1;
        return marker;
    }

    private void Flush(InlineCollector collector, List<Block> children)
    {
        if (collector.Flush() is { } paragraph)
        {
            _anonymous.Add(paragraph);
            children.Add(paragraph);
        }

        // Destinations in inline content without text (an empty a name) keep their place as empty blocks.
        foreach (var id in collector.DroppedAnchors)
        {
            children.Add(new ContainerBlock { Id = id });
        }
    }

    /// <summary>Adds an <c>img</c> or an inline <c>svg</c> element, which is sized and placed like an image.</summary>
    private void AddImage(IElement element, ComputedStyle style, double containingWidth, List<Block> children, InlineCollector collector, InlineAlignment verticalAlign = default)
    {
        var src = element.GetAttribute("src");
        var image = element.LocalName == "svg" ? LoadInlineSvg(element, style)
            : string.IsNullOrWhiteSpace(src) ? null
            : LoadImage(src);
        if (image is null)
        {
            AddAnchors(element, collector);
            if (element.GetAttribute("alt") is { Length: > 0 } alt)
            {
                collector.AddText(alt, style, verticalAlign);
            }

            return;
        }

        // A declared width or height (even auto, e.g. max-width: 100%; height: auto) overrides the attribute.
        var width = ImageDimension(style.Width, style.WidthDeclared ? null : element.GetAttribute("width"), containingWidth);
        var height = ImageDimension(style.Height, style.HeightDeclared ? null : element.GetAttribute("height"), 0);
        if (style.Display is CssDisplay.Inline or CssDisplay.InlineBlock)
        {
            var (w, h) = (width, height) switch
            {
                ({ } fw, { } fh) => (fw, fh),
                ({ } fw, null) => (fw, fw * image.NaturalHeight / image.NaturalWidth),
                (null, { } fh) => (fh * image.NaturalWidth / image.NaturalHeight, fh),
                _ => (image.NaturalWidth, image.NaturalHeight),
            };

            // min-width and max-width scale the image, keeping its aspect ratio unless both sizes are given.
            var stretched = width is not null && height is not null;
            void Limit(CssLength limit, bool isMax)
            {
                if (!limit.IsAuto && Math.Max(limit.Resolve(containingWidth), 1e-3) is var bound && (isMax ? w > bound : w < bound))
                {
                    h = stretched ? h : h * bound / w;
                    w = bound;
                }
            }

            Limit(style.MaxWidth, isMax: true);
            Limit(style.MinWidth, isMax: false); // The minimum wins.

            AddAnchors(element, collector);
            collector.AddImage(image, w, h, verticalAlign, style.Opacity);
            return;
        }

        Flush(collector, children);
        var (box, _) = BoxStyleFor(style with { Width = CssLength.Auto, Height = CssLength.Auto, MinHeight = CssLength.Auto, MaxHeight = CssLength.Auto }, containingWidth);
        var alignment = (style.Margin[3].IsAuto, style.Margin[1].IsAuto) switch
        {
            (true, true) => TextAlignment.Center,
            (true, false) => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        var blockStart = children.Count;
        children.Add(new ImageBlock(image, width, height, box) { Alignment = alignment, Id = IdFor(element) });
        LinkBlocks(children, blockStart, collector.CurrentLink);
    }

    private PdfImage? LoadImage(string src)
    {
        if (_images.TryGetValue(src, out var cached))
        {
            return cached;
        }

        PdfImage? image = null;
        try
        {
            var data = src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? Resources.DecodeDataUri(src)
                : ResolveUri(src) is { } uri ? _options.ResourceLoader(uri) : null;
            image = data is null ? null : PdfImage.Load(data);
        }
        catch (ImageFormatException)
        {
            // A broken image is skipped (its alt text is shown), as browsers do.
        }

        _images[src] = image;
        return image;
    }

    public Uri? ResolveUri(string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        return _baseUri is not null && Uri.TryCreate(_baseUri, reference, out var relative) ? relative : null;
    }

    private static double? ImageDimension(CssLength css, string? attribute, double reference)
    {
        if (css.Kind == CssLengthKind.Points && css.Value > 0)
        {
            return css.Value;
        }

        if (css.HasPercentage && reference > 0 && css.Resolve(reference) is var resolved && resolved > 0)
        {
            return resolved;
        }

        return double.TryParse(attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels > 0
            ? pixels * CssValues.PointsPerPixel
            : null;
    }

    /// <summary>
    /// The box style and the content width its children are built for, with percentages and auto margins resolved and
    /// the widths made border-box widths.
    /// </summary>
    /// <remarks>For a flex item, auto margins are left at 0 (the flex layout distributes free space to them).</remarks>
    private static (BoxStyle Box, double ContentWidth) BoxStyleFor(ComputedStyle style, double containingWidth, bool flexItem = false, double? containingHeight = null)
    {
        var padding = new Thickness(
            style.Padding[0].Resolve(containingWidth), style.Padding[1].Resolve(containingWidth),
            style.Padding[2].Resolve(containingWidth), style.Padding[3].Resolve(containingWidth));
        var borders = new Borders(
            new BorderSide(style.BorderWidth[0], style.BorderColor[0], style.BorderStyle[0]), new BorderSide(style.BorderWidth[1], style.BorderColor[1], style.BorderStyle[1]),
            new BorderSide(style.BorderWidth[2], style.BorderColor[2], style.BorderStyle[2]), new BorderSide(style.BorderWidth[3], style.BorderColor[3], style.BorderStyle[3]));
        var decoration = padding.Horizontal + borders.Widths.Horizontal;

        // Widths are content-box unless box-sizing: border-box; the layout engine works with border boxes and applies
        // the min and max widths itself (also to shrink-to-fit widths, which are only known there).
        double? BorderBoxWidth(CssLength length)
        {
            if (length.IsAuto)
            {
                return null;
            }

            var value = length.Resolve(containingWidth);
            return style.BorderBox ? Math.Max(value, decoration) : value + decoration;
        }

        var borderBoxWidth = BorderBoxWidth(style.Width);
        var minWidth = BorderBoxWidth(style.MinWidth);
        var maxWidth = BorderBoxWidth(style.MaxWidth);
        double Clamp(double width) => Math.Max(Math.Min(width, maxWidth ?? double.PositiveInfinity), minWidth ?? 0);

        var left = style.Margin[3].Resolve(containingWidth);
        var right = style.Margin[1].Resolve(containingWidth);
        var nonAutoMargins = (style.Margin[3].IsAuto ? 0 : left) + (style.Margin[1].IsAuto ? 0 : right);

        // The used width in a containing block of this width: an auto width fills it, within the min and max widths.
        var usedWidth = Clamp(borderBoxWidth ?? (containingWidth - nonAutoMargins));
        if (!flexItem)
        {
            // Auto horizontal margins share the free space (CSS 2.1 section 10.3.3), e.g. margin: 0 auto centers. An
            // auto width leaves none, unless the max width narrows the box.
            var free = containingWidth - usedWidth - nonAutoMargins;
            if (style.Margin[3].IsAuto && style.Margin[1].IsAuto)
            {
                left = right = Math.Max(0, free / 2);
            }
            else if (style.Margin[3].IsAuto)
            {
                left = Math.Max(0, free);
            }
        }

        var box = new BoxStyle
        {
            Margin = new Thickness(style.Margin[0].Resolve(containingWidth), right, style.Margin[2].Resolve(containingWidth), left),
            Padding = padding,
            Border = borders,
            Background = style.Background,
            Width = borderBoxWidth,
            MinWidth = minWidth,
            MaxWidth = maxWidth,
            Height = BorderBoxHeight(style.Height),
            MinHeight = BorderBoxHeight(style.MinHeight),
            MaxHeight = BorderBoxHeight(style.MaxHeight),
            BreakBefore = style.BreakBefore,
            BreakAfter = style.BreakAfter,
            BreakInside = style.BreakInside,
            Orphans = style.Orphans,
            Widows = style.Widows,
            Opacity = style.Opacity,
        };
        var content = Clamp(borderBoxWidth ?? (containingWidth - box.Margin.Horizontal)) - decoration;
        return (box, Math.Max(1, content));

        // Heights are content-box unless box-sizing: border-box; a percentage (also in calc()) needs a definite
        // containing height, otherwise the height is auto (and min/max heights have no effect).
        double? BorderBoxHeight(CssLength length)
        {
            double? value = length.Kind switch
            {
                CssLengthKind.Points => length.Value,
                CssLengthKind.Percent or CssLengthKind.Calc when containingHeight is { } reference => length.Resolve(reference),
                _ => null,
            };
            var vertical = padding.Vertical + borders.Widths.Vertical;
            return value is { } v ? Math.Max(style.BorderBox ? Math.Max(v, vertical) : v + vertical, 0) : null;
        }
    }

    private IReadOnlyList<PdfFont> FontsFor(ComputedStyle style)
    {
        var key = string.Join('\u0001', style.FontFamilies) + $"|{style.FontWeight}|{style.Italic}";
        if (_fontLists.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var fonts = new List<PdfFont>();
        foreach (var family in style.FontFamilies.Concat(_options.FallbackFontFamilies).Append("sans-serif"))
        {
            // A family declared with @font-face hides installed fonts of the same name; all its matching faces
            // (e.g. one per unicode-range) are tried in turn.
            var name = family.Trim().Trim('"', '\'');
            var candidates = _webFonts?.Declares(name) == true
                ? _webFonts.Find(name, style.FontWeight, style.Italic)
                : _fonts.Find(name, style.FontWeight, style.Italic) is { } found ? [found] : [];
            foreach (var font in candidates.Where(f => !fonts.Contains(f)))
            {
                fonts.Add(font);
            }
        }

        if (fonts.Count == 0)
        {
            throw new InvalidOperationException(
                $"No font found for '{string.Join(", ", style.FontFamilies)}' or sans-serif. Add fonts to HtmlConverterOptions.Fonts (e.g. BundledFonts).");
        }

        _fontLists[key] = fonts;
        return fonts;
    }

    private static List<string> ParseFeatures(string settings)
    {
        var features = new List<string>();
        if (settings is "normal" or "")
        {
            return features;
        }

        foreach (var part in settings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tag = tokens[0].Trim('"', '\'');
            if (tag.Length != 4)
            {
                continue;
            }

            var value = tokens.Length > 1 ? tokens[1] : "1";
            features.Add(value switch
            {
                "0" or "off" => "-" + tag,
                "1" or "on" => tag,
                _ => $"{tag}={value}",
            });
        }

        return features;
    }

    private static int ListStart(IElement list) =>
        list.LocalName == "ol" && int.TryParse(list.GetAttribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)
            ? start
            : IsReversed(list) ? list.Children.Count(c => c.LocalName == "li") : 1;

    private static bool IsReversed(IElement list) => list.LocalName == "ol" && list.HasAttribute("reversed");
}
