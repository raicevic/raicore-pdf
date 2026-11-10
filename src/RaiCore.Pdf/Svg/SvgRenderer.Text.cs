using System.Text;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Images;

namespace RaiCore.Pdf.Svg;

/// <summary>Text and image elements.</summary>
internal sealed partial class SvgRenderer
{
    private readonly Dictionary<string, PdfImage?> _images = new(StringComparer.Ordinal);

    private enum FontKind
    {
        Sans,
        Serif,
        Mono,
    }

    /// <summary>
    /// Draws a <c>text</c> element with its <c>tspan</c> children in the standard PDF fonts (Helvetica, Times or
    /// Courier, chosen from the font family), which need no shaping. Only the first value of <c>x</c>, <c>y</c>,
    /// <c>dx</c> and <c>dy</c> lists is used; <c>text-anchor</c> aligns each text chunk.
    /// </summary>
    private void RenderText(SvgElement element, SvgStyle style, RenderState state)
    {
        var placed = LayoutText(element, style, state.Viewport);
        if (placed.Count == 0)
        {
            return;
        }

        SvgBounds? Bounds() => Union(placed);
        foreach (var run in placed)
        {
            var runStyle = run.Style;
            if (!runStyle.Visible || runStyle.FontSize <= 0)
            {
                continue;
            }

            var fill = ResolvePaint(runStyle.Fill, runStyle, runStyle.FillOpacity * state.Opacity, Bounds, state);
            var stroke = runStyle.StrokeWidth > 0 ? ResolvePaint(runStyle.Stroke, runStyle, runStyle.StrokeOpacity * state.Opacity, Bounds, state) : null;
            if (fill is null && stroke is null)
            {
                continue;
            }

            _canvas.SaveState();
            SetPaints(fill, stroke, runStyle);
            if (stroke is not null)
            {
                _canvas.SetTextRenderingMode(fill is null ? 1 : 2);
            }

            // The user space points down; text is drawn upright at the baseline position.
            _canvas.Transform(1, 0, 0, -1, run.X, run.Y)
                .DrawText(run.Text, run.Font, runStyle.FontSize, 0, 0)
                .RestoreState();
        }
    }

    private static SvgBounds? TextBounds(SvgElement element, SvgStyle style, (double Width, double Height) viewport) =>
        Union(LayoutText(element, style, viewport));

    private static SvgBounds? Union(List<PlacedText> placed)
    {
        SvgBounds? result = null;
        foreach (var run in placed)
        {
            var size = run.Style.FontSize;
            var box = new SvgBounds(run.X, run.Y - (run.Font.Ascent * size), run.X + run.Width, run.Y - (run.Font.Descent * size));
            result = result is { } r ? r.Union(box) : box;
        }

        return result;
    }

    /// <summary>Positions the text runs of a <c>text</c> element: pen movement, whitespace and anchoring.</summary>
    private static List<PlacedText> LayoutText(SvgElement element, SvgStyle style, (double Width, double Height) viewport)
    {
        var runs = new List<TextRun>();
        CollectText(element, style, viewport, runs, 0);
        NormalizeWhitespace(runs, element["xml:space"] == "preserve");

        var placed = new List<PlacedText>();
        var (penX, penY) = (0.0, 0.0);
        var chunkStart = 0;
        var chunkX = 0.0;
        var anchor = style.TextAnchor;
        foreach (var run in runs)
        {
            if (run.X is { } x)
            {
                Anchor(placed, chunkStart, penX - chunkX, anchor);
                (chunkStart, chunkX, penX, anchor) = (placed.Count, x, x, run.Style.TextAnchor);
            }

            penY = run.Y ?? penY;
            penX += run.Dx;
            penY += run.Dy;
            if (run.Text.Length == 0)
            {
                continue;
            }

            var font = PdfFont.Standard(FontFor(run.Style));
            var width = font.MeasureText(run.Text, run.Style.FontSize);
            placed.Add(new PlacedText(run.Text, run.Style, font, penX, penY, width));
            penX += width;
        }

        Anchor(placed, chunkStart, penX - chunkX, anchor);
        return placed;
    }

    /// <summary>Shifts the runs of a text chunk for <c>text-anchor</c> <c>middle</c> or <c>end</c>.</summary>
    private static void Anchor(List<PlacedText> placed, int start, double width, SvgTextAnchor anchor)
    {
        var shift = anchor switch
        {
            SvgTextAnchor.Middle => -width / 2,
            SvgTextAnchor.End => -width,
            _ => 0,
        };

        for (var i = start; shift != 0 && i < placed.Count; i++)
        {
            placed[i] = placed[i] with { X = placed[i].X + shift };
        }
    }

    /// <summary>
    /// Collects the character data of a text element and its <c>tspan</c> (and <c>a</c>) descendants as runs. An
    /// element's positioning attributes become an empty run before its content, so they apply to its first character.
    /// </summary>
    private static void CollectText(SvgElement element, SvgStyle style, (double Width, double Height) viewport, List<TextRun> runs, int depth)
    {
        double? First(string name, double reference)
        {
            var values = element[name]?.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            return values is { Length: > 0 } && SvgLength.Parse(values[0]) is { } length ? length.Resolve(style.FontSize, reference) : null;
        }

        var (x, y) = (First("x", viewport.Width), First("y", viewport.Height));
        var (dx, dy) = (First("dx", viewport.Width), First("dy", viewport.Height));
        if (x is not null || y is not null || dx is not null || dy is not null)
        {
            runs.Add(new TextRun(string.Empty, style, x, y, dx ?? 0, dy ?? 0));
        }

        foreach (var child in element.Children)
        {
            if (child is SvgTextNode text)
            {
                runs.Add(new TextRun(text.Text, style, null, null, 0, 0));
            }
            else if (child is SvgElement { Name: "tspan" or "a" } span && depth < MaxDepth)
            {
                var spanStyle = SvgStyle.Compute(style, span, viewport);
                if (spanStyle.Displayed)
                {
                    CollectText(span, spanStyle, viewport, runs, depth + 1);
                }
            }
        }
    }

    /// <summary>
    /// Applies SVG whitespace handling across the runs: newlines and tabs become spaces; unless
    /// <c>xml:space="preserve"</c>, consecutive spaces collapse and leading and trailing spaces are removed.
    /// </summary>
    private static void NormalizeWhitespace(List<TextRun> runs, bool preserve)
    {
        var previousSpace = true;
        var text = new StringBuilder();
        for (var i = 0; i < runs.Count; i++)
        {
            text.Clear();
            foreach (var c in runs[i].Text)
            {
                var space = c is ' ' or '\n' or '\r' or '\t';
                if (preserve)
                {
                    text.Append(space ? ' ' : c);
                }
                else if (!space)
                {
                    text.Append(c);
                    previousSpace = false;
                }
                else if (!previousSpace)
                {
                    text.Append(' ');
                    previousSpace = true;
                }
            }

            runs[i] = runs[i] with { Text = text.ToString() };
        }

        if (!preserve)
        {
            var last = runs.FindLastIndex(r => r.Text.Length > 0);
            if (last >= 0 && runs[last].Text.EndsWith(' '))
            {
                runs[last] = runs[last] with { Text = runs[last].Text[..^1] };
            }
        }
    }

    /// <summary>
    /// Maps a CSS font family list to a standard font: generic families and well-known names, else Helvetica. Exported
    /// files often name a face instead of setting the weight (<c>MyriadPro-Bold</c>), so such names count too.
    /// </summary>
    internal static StandardFont FontFor(SvgStyle style)
    {
        var kind = KindOf(style.FontFamily);
        // The style part of a face name: "MinionPro-BoldIt" -> "BOLDIT".
        var face = style.FontFamily?.Split(',')[0].Trim().Trim('"', '\'').ToUpperInvariant() ?? string.Empty;
        var dash = face.LastIndexOf('-');
        face = face[(dash + 1)..];
        var bold = style.FontWeight >= 600 || face.Contains("BOLD", StringComparison.Ordinal) || face.Contains("BLACK", StringComparison.Ordinal)
            || face.Contains("HEAVY", StringComparison.Ordinal);
        var italic = style.Italic || face.Contains("ITALIC", StringComparison.Ordinal) || face.Contains("OBLIQUE", StringComparison.Ordinal)
            || (dash >= 0 && face.EndsWith("IT", StringComparison.Ordinal));
        return kind switch
        {
            FontKind.Serif => bold ? (italic ? StandardFont.TimesBoldItalic : StandardFont.TimesBold) : italic ? StandardFont.TimesItalic : StandardFont.TimesRoman,
            FontKind.Mono => bold ? (italic ? StandardFont.CourierBoldOblique : StandardFont.CourierBold) : italic ? StandardFont.CourierOblique : StandardFont.Courier,
            _ => bold ? (italic ? StandardFont.HelveticaBoldOblique : StandardFont.HelveticaBold) : italic ? StandardFont.HelveticaOblique : StandardFont.Helvetica,
        };
    }

    private static FontKind KindOf(string? families)
    {
        if (string.IsNullOrWhiteSpace(families))
        {
            return FontKind.Serif; // Browsers draw SVG text in their default (serif) font.
        }

        foreach (var family in families.Split(','))
        {
            var name = family.Trim().Trim('"', '\'').Trim().ToUpperInvariant();
            string[] mono = ["MONO", "COURIER", "CONSOLAS", "MENLO", "MONACO"];
            string[] serif = ["TIMES", "GEORGIA", "GARAMOND", "CAMBRIA", "PALATINO", "BASKERVILLE"];
            string[] sans = ["SANS", "ARIAL", "HELVETICA", "VERDANA", "TAHOMA", "SEGOE", "ROBOTO", "SYSTEM-UI", "CALIBRI", "INTER"];
            if (mono.Any(m => name.Contains(m, StringComparison.Ordinal)))
            {
                return FontKind.Mono;
            }

            if (sans.Any(s => name.Contains(s, StringComparison.Ordinal)))
            {
                return FontKind.Sans;
            }

            if (name.Contains("SERIF", StringComparison.Ordinal) || serif.Any(s => name.Contains(s, StringComparison.Ordinal)))
            {
                return FontKind.Serif;
            }
        }

        return FontKind.Sans;
    }

    /// <summary>
    /// Draws an <c>image</c> element whose <c>href</c> is a <c>data:</c> URI (JPEG, PNG, GIF or SVG), fitted with
    /// <c>preserveAspectRatio</c>. Other references are not loaded: an SVG cannot fetch resources.
    /// </summary>
    private void RenderImage(SvgElement element, SvgStyle style, RenderState state)
    {
        if (!style.Visible || element["href"] is not { } href || LoadImage(href.Trim()) is not { } image)
        {
            return;
        }

        var (naturalWidth, naturalHeight) = (image.NaturalWidth * 4 / 3, image.NaturalHeight * 4 / 3); // Points to pixels.
        var (vw, vh) = state.Viewport;
        var width = SvgLength.Parse(element["width"])?.Resolve(style.FontSize, vw);
        var height = SvgLength.Parse(element["height"])?.Resolve(style.FontSize, vh);
        var (w, h) = (width, height) switch
        {
            ({ } fw, { } fh) => (fw, fh),
            ({ } fw, null) => (fw, fw * naturalHeight / naturalWidth),
            (null, { } fh) => (fh * naturalWidth / naturalHeight, fh),
            _ => (naturalWidth, naturalHeight),
        };
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var (x, y) = (Length(element, "x", vw, style.FontSize), Length(element, "y", vh, style.FontSize));
        var aspect = SvgPreserveAspectRatio.Parse(element["preserveAspectRatio"]);
        var (px, py, pw, ph) = (x, y, w, h);
        if (!aspect.None)
        {
            var scale = aspect.Slice ? Math.Max(w / naturalWidth, h / naturalHeight) : Math.Min(w / naturalWidth, h / naturalHeight);
            (pw, ph) = (naturalWidth * scale, naturalHeight * scale);
            (px, py) = (x + ((w - pw) * aspect.AlignX), y + ((h - ph) * aspect.AlignY));
            if (aspect.Slice)
            {
                _canvas.Rectangle(x, y, w, h).Clip();
            }
        }

        // The user space points down; flip it so the image is upright.
        _canvas.SaveState()
            .Transform(1, 0, 0, -1, 0, 0)
            .DrawImage(image, px, -(py + ph), pw, ph)
            .RestoreState();
    }

    private PdfImage? LoadImage(string href)
    {
        if (_images.TryGetValue(href, out var cached))
        {
            return cached;
        }

        PdfImage? image = null;
        if (DecodeDataUri(href) is { } data)
        {
            try
            {
                image = PdfImage.Load(data);
            }
            catch (ImageFormatException)
            {
                // A broken image is skipped.
            }
        }

        _images[href] = image;
        return image;
    }

    /// <summary>Decodes a <c>data:</c> URI (base64 or percent-encoded); null for anything else.</summary>
    internal static byte[]? DecodeDataUri(string uri)
    {
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
        {
            return null;
        }

        var header = uri[5..comma];
        var payload = uri[(comma + 1)..];
        try
        {
            if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            {
                var base64 = new StringBuilder(payload.Length);
                foreach (var c in Uri.UnescapeDataString(payload))
                {
                    if (!char.IsWhiteSpace(c))
                    {
                        base64.Append(c);
                    }
                }

                return Convert.FromBase64String(base64.ToString());
            }

            return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record TextRun(string Text, SvgStyle Style, double? X, double? Y, double Dx, double Dy);

    private readonly record struct PlacedText(string Text, SvgStyle Style, PdfFont Font, double X, double Y, double Width);
}
