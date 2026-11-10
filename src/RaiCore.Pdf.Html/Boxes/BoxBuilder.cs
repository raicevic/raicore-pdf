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
    private readonly Paged.PagedMedia? _paged;
    private double _pageContentWidth;

    public BoxBuilder(IDocument document, HtmlConverterOptions options, FontCollection fonts, Uri? baseUri, Paged.PagedMedia? paged = null)
    {
        _paged = paged;
        _window = document.DefaultView ?? throw new InvalidOperationException("The document has no window for style computation.");
        _options = options;
        _fonts = fonts;
        _baseUri = baseUri;
        Document = document;
        _rootFontSize = 12;
        if (document.DocumentElement is { } root)
        {
            _rootFontSize = Style(root).FontSize;
        }
    }

    public IDocument Document { get; }

    public List<Block> Build(double contentWidth)
    {
        _pageContentWidth = contentWidth;
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
            style = ComputedStyle.From(_window.GetComputedStyle(element), _rootFontSize);
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
            Shaping = new ShapingOptions { Features = ParseFeatures(style.FontFeatures) },
        };
        _textStyles[style] = textStyle;
        return textStyle;
    }

    private Block? BuildBlock(IElement element, ComputedStyle style, double containingWidth, string? marker, bool flexItem = false)
    {
        if (style.Display == CssDisplay.Flex)
        {
            return BuildFlex(element, style, containingWidth, flexItem);
        }

        var (box, contentWidth) = BoxStyleFor(style, containingWidth, flexItem);
        var children = new List<Block>();
        var collector = new InlineCollector(this, style, contentWidth);
        if (marker is not null)
        {
            collector.SetMarker(marker);
        }

        AddChildren(element, style, contentWidth, children, collector);
        Flush(collector, children);

        // An empty block with a fixed height keeps that height (e.g. spacers made of empty divs).
        if (children.Count == 0 && style.Height.Kind != CssLengthKind.Auto)
        {
            var height = style.Height.Resolve(0) - (style.BorderBox ? box.Padding.Vertical + box.Border.Widths.Vertical : 0);
            if (height > 0)
            {
                children.Add(new SpacerBlock(height));
            }
        }

        // A block whose only content is one anonymous paragraph becomes a paragraph block with the element's box.
        // Elements with string-set carry a marker, so running headers know which page they start on.
        var pageMarker = _paged?.MarkerFor(element);
        if (children is [ParagraphBlock only] && _anonymous.Contains(only))
        {
            return new ParagraphBlock(only.Paragraph, box) { Marker = pageMarker };
        }

        return new ContainerBlock(box, children) { Marker = pageMarker };
    }

    /// <summary>Adds the element's children: inline content goes to the collector, block-level children become blocks.</summary>
    private void AddChildren(IElement element, ComputedStyle style, double contentWidth, List<Block> children, InlineCollector collector)
    {
        var listIndex = ListStart(element);
        foreach (var node in element.ChildNodes)
        {
            switch (node)
            {
                case IText text:
                    collector.AddText(text.Data, style);
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
                        var content = BuildBlock(child, RunningStyle(childStyle), _pageContentWidth, marker: null);
                        children.Add(new ContainerBlock { Marker = new Paged.RunningMarker(runningName, content is null ? [] : [content]) });
                        break;
                    }

                    if (child.LocalName == "br")
                    {
                        collector.AddLineBreak(childStyle);
                        break;
                    }

                    if (child.LocalName == "img")
                    {
                        AddImage(child, childStyle, contentWidth, children, collector);
                        break;
                    }

                    if (childStyle.Display is CssDisplay.Inline or CssDisplay.InlineBlock)
                    {
                        AddChildren(child, childStyle, contentWidth, children, collector);
                        break;
                    }

                    // A block-level child ends the current run of inline content.
                    Flush(collector, children);
                    if (childStyle.Display == CssDisplay.Table)
                    {
                        children.AddRange(BuildTable(child, childStyle, contentWidth));
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

                    break;
            }
        }
    }

    /// <summary>A running element is laid out as a block (or a flex container) in a page margin box.</summary>
    private static ComputedStyle RunningStyle(ComputedStyle style) =>
        style.Display == CssDisplay.Flex ? style : style with { Display = CssDisplay.Block };

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
    }

    private void AddImage(IElement element, ComputedStyle style, double containingWidth, List<Block> children, InlineCollector collector)
    {
        var src = element.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src) || LoadImage(src) is not { } image)
        {
            if (element.GetAttribute("alt") is { Length: > 0 } alt)
            {
                collector.AddText(alt, style);
            }

            return;
        }

        var width = ImageDimension(style.Width, element.GetAttribute("width"), containingWidth);
        var height = ImageDimension(style.Height, element.GetAttribute("height"), 0);
        if (style.Display is CssDisplay.Inline or CssDisplay.InlineBlock)
        {
            var natural = (Width: image.PixelWidth * CssValues.PointsPerPixel, Height: image.PixelHeight * CssValues.PointsPerPixel);
            var (w, h) = (width, height) switch
            {
                ({ } fw, { } fh) => (fw, fh),
                ({ } fw, null) => (fw, fw * image.PixelHeight / image.PixelWidth),
                (null, { } fh) => (fh * image.PixelWidth / image.PixelHeight, fh),
                _ => natural,
            };
            collector.AddImage(image, w, h);
            return;
        }

        Flush(collector, children);
        var (box, _) = BoxStyleFor(style with { Width = CssLength.Auto }, containingWidth);
        var alignment = (style.Margin[3].IsAuto, style.Margin[1].IsAuto) switch
        {
            (true, true) => TextAlignment.Center,
            (true, false) => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        children.Add(new ImageBlock(image, width, height, box) { Alignment = alignment });
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

        if (css.Kind == CssLengthKind.Percent && reference > 0)
        {
            return css.Resolve(reference);
        }

        return double.TryParse(attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels) && pixels > 0
            ? pixels * CssValues.PointsPerPixel
            : null;
    }

    /// <summary>The box style and the content width, with percentages, auto margins and width/max-width resolved.</summary>
    /// <remarks>
    /// For a flex item, auto margins are left at 0 (the flex layout distributes free space to them) and max-width only
    /// caps a fixed width (the flex layout clamps the rest).
    /// </remarks>
    private static (BoxStyle Box, double ContentWidth) BoxStyleFor(ComputedStyle style, double containingWidth, bool flexItem = false)
    {
        var padding = new Thickness(
            style.Padding[0].Resolve(containingWidth), style.Padding[1].Resolve(containingWidth),
            style.Padding[2].Resolve(containingWidth), style.Padding[3].Resolve(containingWidth));
        var borders = new Borders(
            new BorderSide(style.BorderWidth[0], style.BorderColor[0]), new BorderSide(style.BorderWidth[1], style.BorderColor[1]),
            new BorderSide(style.BorderWidth[2], style.BorderColor[2]), new BorderSide(style.BorderWidth[3], style.BorderColor[3]));
        var decoration = padding.Horizontal + borders.Widths.Horizontal;

        double? borderBoxWidth = null;
        if (!style.Width.IsAuto)
        {
            var width = style.Width.Resolve(containingWidth);
            borderBoxWidth = style.BorderBox ? Math.Max(width, decoration) : width + decoration;
        }

        if (!style.MaxWidth.IsAuto && !(flexItem && borderBoxWidth is null))
        {
            var max = style.MaxWidth.Resolve(containingWidth);
            var maxBorderBox = style.BorderBox ? max : max + decoration;
            if (borderBoxWidth is null)
            {
                // max-width without width: the block is as wide as the container allows, up to the maximum.
                var natural = containingWidth - style.Margin[1].Resolve(containingWidth) - style.Margin[3].Resolve(containingWidth);
                if (natural > maxBorderBox)
                {
                    borderBoxWidth = maxBorderBox;
                }
            }
            else
            {
                borderBoxWidth = Math.Min(borderBoxWidth.Value, maxBorderBox);
            }
        }

        var left = style.Margin[3].Resolve(containingWidth);
        var right = style.Margin[1].Resolve(containingWidth);
        if (borderBoxWidth is { } fixedWidth && !flexItem)
        {
            // Auto horizontal margins share the free space (CSS 2.1 section 10.3.3), e.g. margin: 0 auto centers.
            var free = containingWidth - fixedWidth - (style.Margin[3].IsAuto ? 0 : left) - (style.Margin[1].IsAuto ? 0 : right);
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
            BreakBefore = style.BreakBefore,
            BreakAfter = style.BreakAfter,
            BreakInside = style.BreakInside,
            Orphans = style.Orphans,
            Widows = style.Widows,
        };
        var content = (borderBoxWidth ?? (containingWidth - box.Margin.Horizontal)) - decoration;
        return (box, Math.Max(1, content));
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
            if (_fonts.Find(family, style.FontWeight, style.Italic) is { } font && !fonts.Contains(font))
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
