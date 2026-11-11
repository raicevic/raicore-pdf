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

        // A document can be built more than once (to resolve page references); what one build generates starts over.
        _anonymous.Clear();
        _generated.Clear();
        _generatedText.Clear();
        _usedPages.Clear();
        _quoteDepth = 0;
        var root = Document.DocumentElement;
        if (root is null || Style(root).Display == CssDisplay.None)
        {
            return [];
        }

        return BuildInPage(root, contentWidth, width => BuildBlock(root, Style(root), width, marker: null) is { } block ? [block] : []);
    }

    public ComputedStyle Style(IElement element)
    {
        if (!_styles.TryGetValue(element, out var style))
        {
            style = ComputedStyle.From(_window.GetComputedStyle(element), _rootFontSize, _viewport) with { Language = LanguageOf(element) };
            style = style with { ContainerRtl = element.ParentElement is { } parent ? Style(parent).Rtl : style.Rtl };
            if (style.BackgroundValues is { } backgrounds)
            {
                style = style with { BackgroundLayers = CssBackgrounds.Layers(backgrounds, style.FontSize, _rootFontSize, LoadImage) };
            }

            var dir = element.GetAttribute("dir")?.Trim().ToLowerInvariant();
            if (dir == "auto" || (element.LocalName == "bdi" && dir is not ("ltr" or "rtl")))
            {
                style = style with { DirectionAuto = true };
            }

            // HTML gives bdo unicode-bidi: isolate-override, a value AngleSharp does not keep.
            if (element.LocalName == "bdo" && style.UnicodeBidi == CssUnicodeBidi.BidiOverride)
            {
                style = style with { UnicodeBidi = CssUnicodeBidi.IsolateOverride };
            }

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
            Shaping = new ShapingOptions { Features = ParseFeatures(style.FontFeatures), Language = style.Language },
            Language = style.Language,

            // Text that does not wrap is not hyphenated either, not even at soft hyphens.
            Hyphens = style.WhiteSpace is CssWhiteSpace.NoWrap or CssWhiteSpace.Pre ? Hyphens.None : style.Hyphens,
            HyphenateCharacter = style.HyphenateCharacter,
            HyphenationLimits = style.HyphenationLimits,
        };
        _textStyles[style] = textStyle;
        return textStyle;
    }

    /// <summary>The HTML <c>lang</c> (or <c>xml:lang</c>) of the element or its nearest ancestor with one; <c>null</c> when unknown or empty.</summary>
    private static string? LanguageOf(IElement element)
    {
        for (var e = element; e is not null; e = e.ParentElement)
        {
            if ((e.GetAttribute("lang") ?? e.GetAttribute("xml:lang")) is { } lang)
            {
                return string.IsNullOrWhiteSpace(lang) ? null : lang.Trim();
            }
        }

        return null;
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
        WithPageWidthFluid(!flexItem && box is { Width: null, MaxWidth: null }, () =>
        {
            AddChildren(element, style, contentWidth, children, collector);
        });
        Flush(collector, children);
        _decorations = outerDecorations;
        _containingHeight = outerHeight;

        // A block whose only content is one anonymous paragraph becomes a paragraph block with the element's box.
        // Elements with string-set carry a marker, so running headers know which page they start on.
        var pageMarker = _paged?.MarkerFor(element, item => CounterText(element, string.Empty, item));
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
        _options.CreateBookmarks ? _paged?.BookmarkFor(element, pseudo => GeneratedText(element, pseudo), item => CounterText(element, string.Empty, item)) : null;

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

                    if (child.LocalName == LeaderElement && _generated.Contains(element))
                    {
                        collector.AddLeader(child.GetAttribute("pattern")!, childStyle, inherited);
                        break;
                    }

                    if (childStyle.Float != FloatSide.None || childStyle.Position is Positioning.Absolute or Positioning.Fixed)
                    {
                        collector.AddFloat(BuildFloat(child, childStyle, contentWidth));
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
                            collector.BeginInline(box, TextStyleFor(childStyle) with { VerticalAlign = align }, childStyle.Rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight);
                        }

                        if (link is not null)
                        {
                            collector.BeginLink(link);
                        }

                        // unicode-bidi becomes the Unicode controls it stands for (CSS Writing Modes 3 section 2.4.2).
                        var (bidiOpen, bidiClose) = BidiControls(childStyle);
                        if (bidiOpen.Length > 0)
                        {
                            collector.BeginBidi(bidiOpen, bidiClose, childStyle);
                        }

                        var outerDecorations = EnterDecoration(childStyle, align.Kind == InlineAlignmentKind.Baseline ? align.Shift : 0);
                        AddChildren(child, childStyle, contentWidth, children, collector, align);
                        _decorations = outerDecorations;
                        if (bidiOpen.Length > 0)
                        {
                            collector.EndBidi();
                        }

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
                        children.AddRange(BuildInPage(child, contentWidth, width => BuildTable(child, childStyle, width)));
                        LinkBlocks(children, blockStart, collector.CurrentLink);
                        break;
                    }

                    string? marker = null;
                    if (childStyle.Display == CssDisplay.ListItem)
                    {
                        marker = NextListMarker(element, child, childStyle, ref listIndex);
                    }

                    children.AddRange(BuildInPage(child, contentWidth, width => BuildBlock(child, childStyle, width, marker) is { } block ? [block] : []));

                    LinkBlocks(children, blockStart, collector.CurrentLink);
                    break;
            }
        }
    }

    /// <summary>
    /// A positioned element's insets: auto stays null; a relative element's percentages resolve now (left and right
    /// against the containing width, top and bottom against a definite containing height, otherwise auto); an absolute
    /// element's are resolved by the layout against its containing block. calc() resolves now.
    /// </summary>
    private static BoxInsets InsetsFor(ComputedStyle style, double containingWidth, double? containingHeight)
    {
        if (style.Position == Positioning.Static)
        {
            return default;
        }

        Inset? Side(CssLength length, bool vertical)
        {
            if (length.IsAuto)
            {
                return null;
            }

            var reference = vertical ? containingHeight : containingWidth;
            return length.Kind switch
            {
                CssLengthKind.Percent when style.Position != Positioning.Relative => Inset.Percent(length.Value),
                CssLengthKind.Percent or CssLengthKind.Calc when reference is null => null,
                _ => Inset.Points(length.Resolve(reference ?? 0)),
            };
        }

        return new BoxInsets(Side(style.Insets[0], true), Side(style.Insets[1], false), Side(style.Insets[2], true), Side(style.Insets[3], false));
    }

    /// <summary>
    /// The blocks of a floated or absolutely positioned element (a block, table, flex container or image), which the
    /// layout takes out of the flow.
    /// </summary>
    private List<Block> BuildFloat(IElement element, ComputedStyle style, double containingWidth)
    {
        var blocks = new List<Block>();
        if (element.LocalName is "img" or "svg")
        {
            // Its own collector: an image that does not load shows its alt text, as a floated paragraph.
            var collector = new InlineCollector(this, style, containingWidth);
            AddImage(element, style, containingWidth, blocks, collector);
            Flush(collector, blocks);
            var (box, _) = BoxStyleFor(style, containingWidth);
            return [.. blocks.Select(b => b is ParagraphBlock p && p.Style.Float == FloatSide.None && p.Style.Position == Positioning.Static
                ? new ParagraphBlock(p.Paragraph, p.Style with { Float = box.Float, Position = box.Position, Insets = box.Insets, ZIndex = box.ZIndex })
                : b)];
        }

        if (style.Display == CssDisplay.Table)
        {
            // A table with captions floats as one block.
            var table = BuildInPage(element, containingWidth, width => BuildTable(element, style, width));
            return table.Count == 1 ? [.. table] : [new ContainerBlock(new BoxStyle { Float = style.Float, Clear = style.Clear }, table)];
        }

        blocks.AddRange(BuildInPage(element, containingWidth, width => BuildBlock(element, style, width, marker: null) is { } block ? [block] : []));
        return blocks;
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
    /// The bidi controls an inline element's <c>unicode-bidi</c> stands for, to put around its content: embeddings and
    /// overrides end with PDF, isolates with PDI; <c>plaintext</c> and an automatic direction isolate with FSI.
    /// </summary>
    private static (string Open, string Close) BidiControls(ComputedStyle style)
    {
        var (embed, overrides, isolate) = style.Rtl ? ("\u202B", "\u202E", "\u2067") : ("\u202A", "\u202D", "\u2066");
        return style.UnicodeBidi switch
        {
            CssUnicodeBidi.Embed => (embed, "\u202C"),
            CssUnicodeBidi.Isolate => (style.DirectionAuto ? "\u2068" : isolate, "\u2069"),
            CssUnicodeBidi.BidiOverride => (overrides, "\u202C"),
            CssUnicodeBidi.IsolateOverride => (isolate + overrides, "\u202C\u2069"),
            CssUnicodeBidi.Plaintext => ("\u2068", "\u2069"),
            _ => (string.Empty, string.Empty),
        };
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

    /// <summary>
    /// The marker text of a list item: its <c>list-item</c> counter (see BoxBuilder.Counters), or else the list's own
    /// count, which this advances (honoring value and reversed).
    /// </summary>
    private string? NextListMarker(IElement list, IElement item, ComputedStyle style, ref int listIndex)
    {
        listIndex = ListItemValue(item)
            ?? (item.GetAttribute("value") is { } value && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : listIndex);
        var marker = ListMarkers.Format(style.ListStyleType, listIndex);
        listIndex += IsReversed(list) ? -1 : 1;
        return marker;
    }

    private void Flush(InlineCollector collector, List<Block> children)
    {
        children.AddRange(collector.TakeFloats());
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
            collector.AddImage(image, w, h, verticalAlign, style.Opacity, RadiiFor(style, w, h));
            return;
        }

        Flush(collector, children);
        var (box, _) = BoxStyleFor(style with { Width = CssLength.Auto, Height = CssLength.Auto, MinHeight = CssLength.Auto, MaxHeight = CssLength.Auto }, containingWidth);
        var blockStart = children.Count;
        children.Add(new ImageBlock(image, width, height, box) { Alignment = ShrinkToFitAlignment(style), Id = IdFor(element) });
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
    /// Where a block that shrinks to its content (a table, an image) goes in a wider containing block: auto margins
    /// share the free space (<c>margin: 0 auto</c> centers it); otherwise it is on the containing block's start side,
    /// the right when that is right to left (CSS 2.1 section 10.3.3).
    /// </summary>
    private static TextAlignment ShrinkToFitAlignment(ComputedStyle style) => (style.Margin[3].IsAuto, style.Margin[1].IsAuto) switch
    {
        (true, true) => TextAlignment.Center,
        (true, false) => TextAlignment.Right,
        (false, true) => TextAlignment.Left,
        _ => style.ContainerRtl ? TextAlignment.Right : TextAlignment.Left,
    };

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
        if (!flexItem && style.Float == FloatSide.None)
        {
            // Auto horizontal margins share the free space (auto margins of floats are 0) (CSS 2.1 section 10.3.3), e.g. margin: 0 auto centers. An
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
            else if (style.ContainerRtl && !style.Margin[1].IsAuto)
            {
                // Over-constrained: the end margin gives way, which in a right-to-left block is the left one. A box
                // narrower than its containing block then sits on the right.
                left += free;
            }
        }

        var box = new BoxStyle
        {
            Margin = new Thickness(style.Margin[0].Resolve(containingWidth), right, style.Margin[2].Resolve(containingWidth), left),
            Padding = padding,
            Border = borders,
            Background = style.Background,
            BackgroundClip = style.BackgroundClip,
            BackgroundLayers = style.BackgroundLayers,
            BorderRadius = RadiiFor(style, usedWidth),
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
            Float = style.Float,
            Clear = style.Clear,
            Position = style.Position,
            Insets = InsetsFor(style, containingWidth, containingHeight),
            ZIndex = style.ZIndex,
            ContainsFloats = style.ContainsFloats,
            ClipOverflow = style.ClipOverflow,
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

    /// <summary>
    /// The corner radii of a box. Lengths and percentages, also combined (<c>calc(50% - 2px)</c>), resolve against the
    /// border box when it is painted; min(), max() and clamp() with percentages are evaluated here against
    /// <paramref name="width"/> (the border box's width as far as it is known) and <paramref name="height"/> (or the width).
    /// </summary>
    private static CornerRadii RadiiFor(ComputedStyle style, double width, double? height = null)
    {
        if (style.BorderRadius.Length < 4 || style.BorderRadius.All(r => r is ({ Kind: CssLengthKind.Points, Value: 0 }, _) or (_, { Kind: CssLengthKind.Points, Value: 0 })))
        {
            return CornerRadii.None;
        }

        RadiusLength Length(CssLength length, double reference)
        {
            switch (length.Kind)
            {
                case CssLengthKind.Points:
                    return RadiusLength.FromPoints(length.Value);
                case CssLengthKind.Percent:
                    return RadiusLength.FromPercent(length.Value);
                case CssLengthKind.Calc:
                    // Points plus a percentage when linear (calc() with + and -), else the value for the reference.
                    var (zero, hundred, thousand) = (length.Resolve(0), length.Resolve(100), length.Resolve(1000));
                    var percent = hundred - zero;
                    return Math.Abs(zero + (10 * percent) - thousand) < 1e-6 * Math.Max(1, Math.Abs(thousand))
                        ? new RadiusLength(zero, percent)
                        : RadiusLength.FromPoints(Math.Max(0, length.Resolve(reference)));
                default:
                    return RadiusLength.Zero;
            }
        }

        CornerRadius Corner(int corner) =>
            new(Length(style.BorderRadius[corner].Horizontal, width), Length(style.BorderRadius[corner].Vertical, height ?? width));
        return new CornerRadii(Corner(0), Corner(1), Corner(2), Corner(3));
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
