using System.Xml.Linq;
using RaiCore.Pdf.Xfa.Form;
using RaiCore.Pdf.Xfa.Formatting;
using RaiCore.Pdf.Xfa.Template;

namespace RaiCore.Pdf.Xfa.Layout;

/// <summary>Draws and fields: caption, value (text, rich text, image, line, rectangle, arc) and widget.</summary>
internal sealed partial class Measurer
{
    private const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

    private LayoutBox Widget(FormNode node, double available, double? forcedWidth, double? forcedHeight)
    {
        var t = node.Template;
        var (w, h, minW, maxW, minH, maxH) = Size(node);
        w = forcedWidth ?? w;
        h = forcedHeight ?? h;
        var margin = Props.Margin(t);
        var font = Props.ReadFont(t.Child("font"));
        var para = Props.ReadPara(t.Child("para"));
        var ui = t.Child("ui")?.Elements().FirstOrDefault(e => e.Name.LocalName is not ("picture" or "extras"));
        var uiKind = ui?.Name.LocalName ?? (node.Kind == NodeKind.Draw ? "draw" : "textEdit");
        var uiMargin = ui is null ? default : Props.Margin(ui);
        var uiBorder = ui is null || uiKind == "checkButton" ? null : Props.ReadBorder(ui.Child("border"));
        var valueContent = t.Child("value")?.Elements().FirstOrDefault();
        var valueKind = valueContent?.Name.LocalName ?? "text";

        // The caption (fields only), beside the value.
        var caption = node.Kind == NodeKind.Field ? t.Child("caption") : null;
        if (caption?.Attr("presence") is "hidden" or "inactive")
        {
            caption = null;
        }

        var placement = uiKind == "button" ? "full" : caption.Attr("placement") ?? "left";
        var captionFont = Props.ReadFont(caption.Child("font"), font);
        var captionPara = Props.ReadPara(caption.Child("para"));
        var captionMargin = Props.Margin(caption);
        var captionParagraphs = caption is null ? [] : Paragraphs(caption.Child("value")?.Elements().FirstOrDefault(), null, captionFont, captionPara, node);
        var hasCaption = captionParagraphs.Count > 0 && !(captionParagraphs.Count == 1 && captionParagraphs[0].IsEmpty && caption.MeasureOrNull("reserve") is null);
        var reserve = hasCaption ? caption.MeasureOrNull("reserve") is { } r and > 0 ? r : (double?)null : 0;
        var beside = placement is "left" or "right" or "inline";

        // The value.
        var kind = WidgetKind.Text;
        List<ParagraphSpec> paragraphs = [];
        PdfImage? image = null;
        XElement? shape = null;
        string? comb = null;
        var combCells = 0;
        double naturalValueWidth = 0, naturalValueHeight = 0;
        var wrap = true;
        var check = uiKind == "checkButton";
        var checkSize = check ? Math.Clamp(ui.Measure("size", 10, "pt"), 0, 1000) : 0;
        if (check)
        {
            kind = WidgetKind.CheckButton;
            naturalValueWidth = naturalValueHeight = checkSize;
        }
        else if (valueKind is "line" or "rectangle" or "arc" && node.Kind == NodeKind.Draw)
        {
            kind = valueKind switch { "line" => WidgetKind.Line, "rectangle" => WidgetKind.Rectangle, _ => WidgetKind.Arc };
            shape = valueContent;
        }
        else if (valueKind == "image" || uiKind == "imageEdit")
        {
            kind = WidgetKind.Image;
            image = Image(node, valueContent);
            if (image is not null)
            {
                (naturalValueWidth, naturalValueHeight) = (image.PixelWidth * 0.75, image.PixelHeight * 0.75);
            }
        }
        else if (uiKind is "signature" or "button")
        {
            kind = WidgetKind.None;
        }
        else
        {
            if (uiKind == "barcode" && node.Value is { Length: > 0 })
            {
                _report?.Invoke(XfaDiagnosticKind.UnsupportedContent, $"The barcode {node.Path} is drawn as its text.");
            }

            var display = node.Kind == NodeKind.Field ? Display(node, ui, uiKind, valueKind) : null;
            paragraphs = display is null && node.RichValue is { } rich
                ? RichText.Rich(rich, font, para, reference => Embedded(node, reference))
                : RichText.Plain(display ?? node.Value ?? string.Empty, font, para);
            wrap = node.Kind == NodeKind.Draw || node.RichValue is not null && display is null || ui.Attr("multiLine") == "1";
            if (uiKind == "textEdit" && ui.Child("comb") is { } combElement && display is { Length: > 0 })
            {
                combCells = combElement.Int("numberOfCells", 0);
                if (combCells <= 0)
                {
                    combCells = valueContent.Int("maxChars", display.Length);
                }

                comb = display;
            }

            if (paragraphs.All(p => p.IsEmpty) && node.Kind == NodeKind.Field)
            {
                naturalValueHeight = _text.Layout(paragraphs, Unbounded).Height;
            }
            else
            {
                naturalValueWidth = _text.NaturalWidth(paragraphs);
            }
        }

        // The width: fixed, or the content's.
        var captionNatural = hasCaption && beside && reserve is null ? _text.NaturalWidth(captionParagraphs) + captionMargin.Horizontal : 0;
        var captionAcross = hasCaption ? reserve ?? captionNatural : 0;
        double width;
        if (w is { } fixedWidth)
        {
            width = fixedWidth;
        }
        else
        {
            var valueWidth = naturalValueWidth + uiMargin.Horizontal;
            var content = placement switch
            {
                "full" => _text.NaturalWidth(captionParagraphs) + captionMargin.Horizontal,
                _ when beside => captionAcross + valueWidth,
                _ => Math.Max(valueWidth, hasCaption ? _text.NaturalWidth(captionParagraphs) + captionMargin.Horizontal : 0),
            };
            width = Math.Clamp(content + margin.Horizontal, minW, Math.Min(maxW, Math.Max(available, minW)));
            wrap &= content + margin.Horizontal > width + 0.01;
        }

        var inner = Math.Max(width - margin.Horizontal, 0);
        var valueArea = Math.Max(inner - (beside ? captionAcross : 0) - uiMargin.Horizontal, 1);
        var text = kind == WidgetKind.Text ? _text.Layout(paragraphs, placement == "full" ? 1 : valueArea, wrap) : null;
        var captionWidth = placement == "full" ? inner - captionMargin.Horizontal : beside ? captionAcross - captionMargin.Horizontal : inner - captionMargin.Horizontal;
        var captionBlock = hasCaption ? _text.Layout(captionParagraphs, Math.Max(captionWidth, 1), placement != "full" || true) : null;

        // The height: fixed, or the content's.
        double height;
        if (h is { } fixedHeight)
        {
            height = fixedHeight;
        }
        else
        {
            var valueHeight = (text?.Height ?? naturalValueHeight) + uiMargin.Vertical;
            var captionHeight = captionBlock is null ? 0 : captionBlock.Height + captionMargin.Vertical;
            var content = placement switch
            {
                "full" => captionHeight,
                _ when beside => Math.Max(valueHeight, captionHeight),
                _ => (hasCaption ? caption.MeasureOrNull("reserve") is { } tr and > 0 ? tr : captionHeight : 0) + valueHeight,
            };
            height = Math.Clamp(content + margin.Vertical, minH, maxH);
        }

        var mark = ui.Attr("mark") ?? "default";
        var checkShape = ui.Attr("shape") ?? "square";
        var on = check ? DataMerger.OnOff(t).On : null;
        return new LayoutBox
        {
            Node = node,
            W = width,
            H = height,
            Rotate = Rotation(node),
            Margin = margin,
            KeepNext = t.Child("keep").Attr("next") is "contentArea" or "pageArea",
            KeepPrevious = t.Child("keep").Attr("previous") is "contentArea" or "pageArea",
            Content = new WidgetContent
            {
                Margin = margin,
                Border = Props.ReadBorder(t.Child("border")),
                CaptionPlacement = placement,
                CaptionReserve = beside || placement == "full" ? captionAcross : hasCaption ? caption.MeasureOrNull("reserve") is { } cr and > 0 ? cr : (captionBlock?.Height ?? 0) + captionMargin.Vertical : 0,
                Caption = captionBlock,
                CaptionPara = captionPara,
                CaptionMargin = captionMargin,
                Kind = kind,
                Text = text,
                Para = para,
                CombText = comb,
                CombCells = combCells,
                CombStyle = comb is null ? null : _text.Style(font, RaiCore.Pdf.Layout.LineHeight.Normal),
                Font = font,
                Image = image,
                Aspect = valueContent.Attr("aspect") ?? "fit",
                Shape = shape,
                Checked = check && on is not null && string.Equals(node.Value?.Trim(), on, StringComparison.Ordinal),
                CheckShape = checkShape,
                Mark = mark == "default" ? checkShape == "round" ? "circle" : "check" : mark,
                CheckSize = checkSize,
                UiBorder = check ? Props.ReadBorder(ui.Child("border")) ?? (ui.Child("border") is null ? Props.ReadBorder(new XElement("border")) : null) : uiBorder,
                UiMargin = uiMargin,
                SplitsText = kind == WidgetKind.Text && h is null && comb is null && Rotation(node) == 0 && placement is "left" or "right" or "inline",
            },
        };
    }

    /// <summary>A caption's or draw's text as paragraphs: plain or rich text.</summary>
    private List<ParagraphSpec> Paragraphs(XElement? content, string? fallback, FontSpec font, ParaSpec para, FormNode node)
    {
        if (content is null)
        {
            return fallback is null ? [] : RichText.Plain(fallback, font, para);
        }

        if (content.Name.LocalName == "exData" && content.Attr("contentType") == "text/html" && content.Elements().FirstOrDefault() is { } body)
        {
            return RichText.Rich(body, font, para, reference => Embedded(node, reference));
        }

        return content.Name.LocalName is "image" or "line" or "rectangle" or "arc" ? [] : RichText.Plain(content.Value, font, para);
    }

    /// <summary>The display text of a field's value: formatted by its picture clause, choice list text, masked passwords.</summary>
    private string? Display(FormNode node, XElement? ui, string uiKind, string valueKind)
    {
        var value = node.Value ?? string.Empty;
        if (node.RichValue is not null && node.Template.Child("format") is null)
        {
            return null;
        }

        if (uiKind == "choiceList")
        {
            var lists = node.Template.Children("items").ToList();
            if (lists.Count == 2)
            {
                var save = lists.FindIndex(l => l.Attr("save") == "1");
                var saved = lists[save < 0 ? 1 : save].Elements().Select(e => e.Value).ToList();
                var shown = lists[save < 0 ? 0 : 1 - save].Elements().Select(e => e.Value).ToList();
                var index = saved.IndexOf(value);
                if (index >= 0 && index < shown.Count)
                {
                    return shown[index];
                }
            }

            return value;
        }

        if (uiKind == "passwordEdit")
        {
            return new string(ui.Attr("passwordChar") is { Length: > 0 } c ? c[0] : '*', value.Length);
        }

        var picture = node.Template.Child("format")?.Child("picture")?.Value.Trim();
        var kind = valueKind switch
        {
            "integer" or "decimal" or "float" or "date" or "time" or "dateTime" => valueKind,
            _ when uiKind == "numericEdit" => "decimal",
            _ when uiKind == "dateTimeEdit" => "date",
            _ => "text",
        };
        if (string.IsNullOrEmpty(picture))
        {
            if (kind == "date" && value.Length > 0)
            {
                picture = "date{}";
            }
            else
            {
                return kind is "decimal" or "float" && value.Length > 0 && Locale(node).Decimal != "." ? value.Replace(".", Locale(node).Decimal, StringComparison.Ordinal) : value;
            }
        }

        return Picture.Format(picture, value, kind, Locale(node), _locales) ?? value;
    }

    /// <summary>The locale of an object: its own <c>locale</c>, or its nearest container's.</summary>
    private XfaLocale Locale(FormNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Template.Attr("locale") is { Length: > 0 } locale)
            {
                return _locales.Get(locale);
            }
        }

        return _locales.Get(null);
    }

    /// <summary>The text of a floating field (<c>xfa:embed="#id"</c>), found near the draw that embeds it.</summary>
    private string? Embedded(FormNode node, string reference)
    {
        var id = reference.TrimStart('#');
        for (var scope = node.Parent; scope is not null; scope = scope.Parent)
        {
            var field = Descendants(scope).FirstOrDefault(n => n.Kind == NodeKind.Field && (n.Template.Attr("id") == id || n.Name == id));
            if (field is not null)
            {
                var ui = field.Template.Child("ui")?.Elements().FirstOrDefault();
                return Display(field, ui, ui?.Name.LocalName ?? "textEdit", field.Template.Child("value")?.Elements().FirstOrDefault()?.Name.LocalName ?? "text") ?? field.Value;
            }
        }

        return null;
    }

    /// <summary>An image value: a draw's embedded image, or an image field's data (base64), or <c>null</c> (reported).</summary>
    private PdfImage? Image(FormNode node, XElement? content)
    {
        var data = node.FromData && node.Value is { Length: > 0 } ? node.Value : content?.Value;
        if (string.IsNullOrWhiteSpace(data))
        {
            if (content.Attr("href") is { Length: > 0 } href)
            {
                _report?.Invoke(XfaDiagnosticKind.ImageNotLoaded, $"The image '{href}' of {node.Path} is linked, not embedded; linked images are not loaded.");
            }

            return null;
        }

        if (_images.TryGetValue(data, out var cached))
        {
            return cached;
        }

        PdfImage? image = null;
        try
        {
            var bytes = Convert.FromBase64String(string.Concat(data.Where(c => !char.IsWhiteSpace(c))));
            image = PdfImage.Load(bytes);
            ResourceLimits.CheckImagePixels(image.PixelWidth, image.PixelHeight);
        }
        catch (FormatException)
        {
            _report?.Invoke(XfaDiagnosticKind.ImageNotLoaded, $"The image of {node.Path} is not valid base64 data.");
            image = null;
        }
        catch (ResourceLimitExceededException e)
        {
            _report?.Invoke(XfaDiagnosticKind.ImageNotLoaded, $"The image of {node.Path} is left out: it exceeds ResourceLimits.{e.LimitName}. {e.Message}");
            image = null;
        }
        catch (Exception e) when (e is PdfException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            _report?.Invoke(XfaDiagnosticKind.ImageNotLoaded, $"The image of {node.Path} cannot be read: {e.Message}");
            image = null;
        }

        if (_images.Count < 4096)
        {
            _images[data] = image;
        }

        return image;
    }
}
