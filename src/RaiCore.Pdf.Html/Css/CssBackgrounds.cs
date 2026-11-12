using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html.Css;

/// <summary>The background longhands of an element as AngleSharp computes them (comma-separated lists, one item per layer).</summary>
internal sealed record CssBackgroundValues(string Image, string Repeat, string Position, string Size, string Origin, string Clip);

/// <summary>
/// CSS backgrounds (CSS Backgrounds 3 and the gradients of CSS Images 3). The <c>background</c> shorthand is split into
/// its longhands before the CSS is parsed (AngleSharp.Css throws on a shorthand with several layers and resets
/// <c>background-origin</c> wrongly); the longhands, which AngleSharp computes well, become layout background layers.
/// </summary>
internal static partial class CssBackgrounds
{
    private static readonly HashSet<string> RepeatKeywords = ["repeat", "repeat-x", "repeat-y", "no-repeat", "space", "round"];
    private static readonly HashSet<string> BoxKeywords = ["border-box", "padding-box", "content-box"];
    private static readonly HashSet<string> AttachmentKeywords = ["scroll", "fixed", "local"];
    private static readonly HashSet<string> PositionKeywords = ["left", "right", "center", "top", "bottom"];
    private static readonly HashSet<string> GlobalKeywords = ["inherit", "initial", "unset", "revert", "revert-layer"];

    /// <summary>
    /// Replaces every <c>background</c> shorthand declaration with the longhands it sets, and keeps linear gradients
    /// toward a corner (which AngleSharp.Css turns into 45-degree multiples, right only for squares) as marker angles.
    /// </summary>
    public static string RewriteShorthand(string css)
    {
        css = DropColorHints(css);
        css = CornerRegex().Replace(css, match =>
        {
            var words = match.Groups["sides"].Value.ToLowerInvariant();
            var (right, bottom) = (words.Contains("right", StringComparison.Ordinal), words.Contains("bottom", StringComparison.Ordinal));
            return match.Groups["head"].Value + CornerAngle(right, bottom).ToString(CultureInfo.InvariantCulture) + "deg";
        });

        var result = new StringBuilder(css.Length);
        var position = 0;
        foreach (Match match in ShorthandRegex().Matches(css))
        {
            if (match.Index < position)
            {
                continue;
            }

            var valueStart = match.Index + match.Length;
            var valueEnd = ValueEnd(css, valueStart);
            result.Append(css, position, match.Index - position);
            result.Append(Longhands(css[valueStart..valueEnd]));
            position = valueEnd;
        }

        return position == 0 ? css : result.Append(css, position, css.Length - position).ToString();
    }

    /// <summary>
    /// Makes the relative <c>url()</c> references of a linked stylesheet absolute against its own address, so that they
    /// still point at the right files once its text is placed in the document.
    /// </summary>
    public static string AbsolutizeUrls(string css, Uri sheetUri) =>
        UrlRegex().Replace(css, match =>
        {
            var reference = match.Groups["url"].Value.Trim();
            if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || reference.StartsWith('#')
                || Uri.TryCreate(reference, UriKind.Absolute, out _) || !Uri.TryCreate(sheetUri, reference, out var absolute))
            {
                return match.Value;
            }

            return $"url(\"{absolute.AbsoluteUri.Replace("\"", "%22", StringComparison.Ordinal)}\")";
        });

    /// <summary>
    /// The background layers of an element, the first on top: one per image in <c>background-image</c>, with the other
    /// lists repeated to match. Images that cannot be loaded (and <c>none</c>) leave no layer.
    /// </summary>
    public static IReadOnlyList<BackgroundLayer> Layers(CssBackgroundValues values, double fontSize, double rootFontSize, Func<string, PdfImage?> loadImage)
    {
        var images = Split(values.Image, ',');
        if (images.Count == 0 || images.TrueForAll(image => image.Equals("none", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var (repeats, positions, sizes, origins, clips) =
            (Split(values.Repeat, ','), Split(values.Position, ','), Split(values.Size, ','), Split(values.Origin, ','), Split(values.Clip, ','));
        static string At(List<string> list, int i, string fallback) => list.Count == 0 ? fallback : list[i % list.Count];

        var layers = new List<BackgroundLayer>();
        for (var i = 0; i < images.Count; i++)
        {
            BackgroundImage? image = ParseImage(images[i], fontSize, rootFontSize, loadImage);
            if (image is null)
            {
                continue;
            }

            var (repeatX, repeatY) = ParseRepeat(At(repeats, i, "repeat"));
            var (positionX, positionY) = ParsePosition(SplitComponents(At(positions, i, "0% 0%")), fontSize, rootFontSize);
            layers.Add(new BackgroundLayer(image)
            {
                RepeatX = repeatX,
                RepeatY = repeatY,
                PositionX = positionX,
                PositionY = positionY,
                Size = ParseSize(At(sizes, i, "auto"), fontSize, rootFontSize),
                Origin = ParseBox(At(origins, i, "padding-box"), BackgroundClip.PaddingBox),
                Clip = ParseBox(At(clips, i, "border-box"), BackgroundClip.BorderBox),
            });
        }

        return layers;
    }

    /// <summary>
    /// Removes color hints (a lone position between two stops) from gradients: AngleSharp.Css drops a gradient that has
    /// one. Without it the colors blend evenly, which is close.
    /// </summary>
    private static string DropColorHints(string css)
    {
        var index = css.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return css;
        }

        var result = new StringBuilder(css.Length);
        var position = 0;
        while (index >= 0)
        {
            var open = index + "gradient".Length;
            var close = MatchingParenthesis(css, open);
            if (close < 0)
            {
                break;
            }

            var arguments = Split(css[(open + 1)..close], ',');
            // The first argument is the direction or shape (an angle, a radius), never a hint.
            var kept = arguments.Where((a, i) => i == 0 || !(SplitComponents(a).Count == 1 && CssValues.TryParseLength(a.Trim()) is not null)).ToList();
            if (kept.Count != arguments.Count)
            {
                result.Append(css, position, open + 1 - position).Append(string.Join(", ", kept)).Append(')');
                position = close + 1;
            }

            index = css.IndexOf("gradient(", close, StringComparison.OrdinalIgnoreCase);
        }

        return result.Append(css, position, css.Length - position).ToString();
    }

    private static int MatchingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The end of a declaration's value: the <c>;</c> or <c>}</c> after it outside parentheses and strings.</summary>
    private static int ValueEnd(string css, int start)
    {
        var depth = 0;
        char? quote = null;
        for (var i = start; i < css.Length; i++)
        {
            var c = css[i];
            if (quote is { } q)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ';' or '}' when depth == 0:
                    return i;
            }
        }

        return css.Length;
    }

    /// <summary>The longhand declarations a <c>background</c> shorthand value stands for (CSS Backgrounds 3 section 3.10).</summary>
    private static string Longhands(string value)
    {
        var important = ImportantRegex().Match(value) is { Success: true } m ? m : null;
        var text = (important is null ? value : value[..important.Index]).Trim();
        var suffix = important is null ? string.Empty : " !important";
        if (GlobalKeywords.Contains(text.ToLowerInvariant()))
        {
            return Join(text, text, text, text, text, text, text);
        }

        var images = new List<string>();
        var positions = new List<string>();
        var sizes = new List<string>();
        var repeats = new List<string>();
        var origins = new List<string>();
        var clips = new List<string>();
        var color = "transparent";
        foreach (var layer in Split(text, ','))
        {
            var tokens = ShorthandTokens(layer);
            string image = "none";
            var position = new List<string>();
            var size = new List<string>();
            var repeat = new List<string>();
            var boxes = new List<string>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var lower = token.ToLowerInvariant();
                if (lower == "/")
                {
                    while (i + 1 < tokens.Count && size.Count < 2 && IsSizeToken(tokens[i + 1]))
                    {
                        size.Add(tokens[++i]);
                    }
                }
                else if (lower == "none" || lower.StartsWith("url(", StringComparison.Ordinal) || lower.Contains("gradient(", StringComparison.Ordinal))
                {
                    image = token;
                }
                else if (RepeatKeywords.Contains(lower))
                {
                    repeat.Add(lower);
                }
                else if (BoxKeywords.Contains(lower))
                {
                    boxes.Add(lower);
                }
                else if (AttachmentKeywords.Contains(lower))
                {
                    // background-attachment: fixed and local are the same as scroll on paper.
                }
                else if (IsPositionToken(lower))
                {
                    position.Add(token);
                }
                else
                {
                    color = token;
                }
            }

            images.Add(image);
            positions.Add(position.Count > 0 ? string.Join(' ', position) : "0% 0%");
            sizes.Add(size.Count > 0 ? string.Join(' ', size) : "auto");
            repeats.Add(repeat.Count > 0 ? string.Join(' ', repeat) : "repeat");
            origins.Add(boxes.Count > 0 ? boxes[0] : "padding-box");
            clips.Add(boxes.Count > 1 ? boxes[1] : boxes.Count == 1 ? boxes[0] : "border-box");
        }

        return Join(
            color, string.Join(", ", images), string.Join(", ", positions), string.Join(", ", sizes), string.Join(", ", repeats),
            string.Join(", ", origins), string.Join(", ", clips));

        string Join(string c, string image, string position, string size, string repeat, string origin, string clip) =>
            $"background-color: {c}{suffix}; background-image: {image}{suffix}; background-position: {position}{suffix}; "
            + $"background-size: {size}{suffix}; background-repeat: {repeat}{suffix}; background-origin: {origin}{suffix}; background-clip: {clip}{suffix}";
    }

    /// <summary>A shorthand layer's components: split at whitespace and around a <c>/</c>, outside parentheses and strings.</summary>
    private static List<string> ShorthandTokens(string layer)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        char? quote = null;
        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var c in layer)
        {
            if (quote is { } q)
            {
                current.Append(c);
                quote = c == q ? null : quote;
                continue;
            }

            if (depth == 0 && char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            if (depth == 0 && c == '/')
            {
                Flush();
                tokens.Add("/");
                continue;
            }

            depth += c switch { '(' => 1, ')' when depth > 0 => -1, _ => 0 };
            quote = c is '"' or '\'' ? c : null;
            current.Append(c);
        }

        Flush();
        return tokens;
    }

    private static bool IsSizeToken(string token) =>
        token.ToLowerInvariant() is "auto" or "cover" or "contain" || IsLengthToken(token);

    private static bool IsPositionToken(string lower) => PositionKeywords.Contains(lower) || IsLengthToken(lower);

    private static bool IsLengthToken(string token) =>
        token.Length > 0 && (char.IsAsciiDigit(token[0]) || token[0] is '.' or '-' or '+'
            || token.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) || token.StartsWith("min(", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("max(", StringComparison.OrdinalIgnoreCase) || token.StartsWith("clamp(", StringComparison.OrdinalIgnoreCase));

    private static BackgroundImage? ParseImage(string value, double fontSize, double rootFontSize, Func<string, PdfImage?> loadImage)
    {
        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("url(", StringComparison.Ordinal))
        {
            var reference = value[4..value.LastIndexOf(')')].Trim().Trim('"', '\'');
            return reference.Length > 0 && loadImage(reference) is { } image ? new ImageBackground(image) : null;
        }

        var open = value.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !value.EndsWith(')'))
        {
            return null;
        }

        var name = lower[..open].Trim();
        var arguments = Split(value[(open + 1)..^1], ',');
        var repeating = name.StartsWith("repeating-", StringComparison.Ordinal);
        return name.EndsWith("linear-gradient", StringComparison.Ordinal) ? ParseLinear(arguments, repeating, fontSize, rootFontSize)
            : name.EndsWith("radial-gradient", StringComparison.Ordinal) ? ParseRadial(arguments, repeating, fontSize, rootFontSize)
            : null;
    }

    private static LinearGradient? ParseLinear(List<string> arguments, bool repeating, double fontSize, double rootFontSize)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var first = arguments[0].Trim().ToLowerInvariant();
        double angle = 180;
        (bool Right, bool Bottom)? corner = null;
        var stopsFrom = 1;
        if (ParseAngle(first) is { } marked && CornerOf(marked) is { } markedCorner)
        {
            corner = markedCorner;
        }
        else if (first.StartsWith("to ", StringComparison.Ordinal))
        {
            var sides = SplitComponents(first[3..]);
            var (horizontal, vertical) = (sides.Find(s => s is "left" or "right"), sides.Find(s => s is "top" or "bottom"));
            if (horizontal is not null && vertical is not null)
            {
                corner = (horizontal == "right", vertical == "bottom");
            }
            else
            {
                angle = (horizontal ?? vertical) switch { "top" => 0, "right" => 90, "left" => 270, _ => 180 };
            }
        }
        else if (ParseAngle(first) is { } a)
        {
            angle = a;
        }
        else
        {
            stopsFrom = 0;
        }

        var stops = ParseStops(arguments.Skip(stopsFrom), fontSize, rootFontSize);
        if (stops.Count == 0)
        {
            return null;
        }

        return corner is { } c ? new LinearGradient(c.Right, c.Bottom, stops, repeating) : new LinearGradient(angle, stops, repeating);
    }

    private static RadialGradient? ParseRadial(List<string> arguments, bool repeating, double fontSize, double rootFontSize)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var shape = RadialShape.Ellipse;
        var extent = RadialExtent.FarthestCorner;
        var radii = new List<Inset>();
        var (centerX, centerY) = (BackgroundPosition.Center, BackgroundPosition.Center);
        var stopsFrom = 0;

        // The first argument configures the gradient unless it is a color stop.
        var tokens = SplitComponents(arguments[0]);
        if (tokens.Count > 0 && CssValues.ParseColor(tokens[0]) is null)
        {
            stopsFrom = 1;
            for (var i = 0; i < tokens.Count; i++)
            {
                switch (tokens[i].ToLowerInvariant())
                {
                    case "circle":
                        shape = RadialShape.Circle;
                        break;
                    case "ellipse":
                        break;
                    case "closest-side" or "contain":
                        extent = RadialExtent.ClosestSide;
                        break;
                    case "closest-corner":
                        extent = RadialExtent.ClosestCorner;
                        break;
                    case "farthest-side":
                        extent = RadialExtent.FarthestSide;
                        break;
                    case "farthest-corner" or "cover":
                        extent = RadialExtent.FarthestCorner;
                        break;
                    case "at":
                        (centerX, centerY) = ParsePosition(tokens.Skip(i + 1).ToList(), fontSize, rootFontSize);
                        i = tokens.Count;
                        break;
                    default:
                        if (ToInset(tokens[i], fontSize, rootFontSize) is { } radius)
                        {
                            radii.Add(radius);
                        }

                        break;
                }
            }
        }

        var stops = ParseStops(arguments.Skip(stopsFrom), fontSize, rootFontSize);
        if (stops.Count == 0)
        {
            return null;
        }

        if (radii.Count == 1)
        {
            shape = RadialShape.Circle; // A single length is a circle's radius.
        }

        return new RadialGradient(stops, repeating)
        {
            Shape = shape,
            Extent = extent,
            RadiusX = radii.Count > 0 ? radii[0] : null,
            RadiusY = radii.Count > 1 ? radii[1] : null,
            CenterX = centerX,
            CenterY = centerY,
        };
    }

    /// <summary>Color stops: a color with up to two positions (two make two stops); a lone position (a color hint) is skipped.</summary>
    private static List<GradientStop> ParseStops(IEnumerable<string> arguments, double fontSize, double rootFontSize)
    {
        var stops = new List<GradientStop>();
        foreach (var argument in arguments)
        {
            var tokens = SplitComponents(argument);
            var colorIndex = tokens.FindIndex(t => CssValues.ParseColor(t) is not null);
            if (colorIndex < 0)
            {
                continue; // A color hint (or invalid): the interpolation stays linear.
            }

            var color = CssValues.ParseColor(tokens[colorIndex])!.Value;
            var positions = tokens.Where((_, i) => i != colorIndex).Select(t => ToInset(t, fontSize, rootFontSize)).Where(p => p is not null).ToList();
            if (positions.Count == 0)
            {
                stops.Add(new GradientStop(color));
            }
            else
            {
                stops.AddRange(positions.Take(2).Select(p => new GradientStop(color, p)));
            }
        }

        return stops;
    }

    /// <summary>The marker angle a corner direction is kept as: its square angle plus a tiny, recognizable fraction.</summary>
    private static double CornerAngle(bool right, bool bottom) => (right, bottom) switch
    {
        (true, false) => 45.000123,
        (true, true) => 135.000123,
        (false, true) => 225.000123,
        _ => 315.000123,
    };

    private static (bool Right, bool Bottom)? CornerOf(double angle)
    {
        foreach (var (right, bottom) in new[] { (true, false), (true, true), (false, true), (false, false) })
        {
            if (Math.Abs(angle - CornerAngle(right, bottom)) < 1e-7)
            {
                return (right, bottom);
            }
        }

        return null;
    }

    private static double? ParseAngle(string value)
    {
        var match = AngleRegex().Match(value.Trim());
        if (!match.Success || !double.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return null;
        }

        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "rad" => number * 180 / Math.PI,
            "grad" => number * 0.9,
            "turn" => number * 360,
            "deg" => number,
            _ when number == 0 => 0,
            _ => null,
        };
    }

    private static (BackgroundRepeat X, BackgroundRepeat Y) ParseRepeat(string value)
    {
        var parts = SplitComponents(value.ToLowerInvariant());
        static BackgroundRepeat One(string keyword) => keyword switch
        {
            "no-repeat" => BackgroundRepeat.NoRepeat,
            "space" => BackgroundRepeat.Space,
            "round" => BackgroundRepeat.Round,
            _ => BackgroundRepeat.Repeat,
        };

        return parts switch
        {
            ["repeat-x"] => (BackgroundRepeat.Repeat, BackgroundRepeat.NoRepeat),
            ["repeat-y"] => (BackgroundRepeat.NoRepeat, BackgroundRepeat.Repeat),
            [var one] => (One(one), One(one)),
            [var x, var y, ..] => (One(x), One(y)),
            _ => (BackgroundRepeat.Repeat, BackgroundRepeat.Repeat),
        };
    }

    /// <summary>A position (CSS <c>&lt;position&gt;</c>): one or two values, or keywords with offsets (<c>right 10px top 20%</c>).</summary>
    private static (BackgroundPosition X, BackgroundPosition Y) ParsePosition(List<string> tokens, double fontSize, double rootFontSize)
    {
        var x = BackgroundPosition.Center;
        var y = BackgroundPosition.Center;
        var lower = tokens.Select(t => t.ToLowerInvariant()).ToList();
        BackgroundPosition Keyword(string keyword, Inset? offset = null) => keyword switch
        {
            "right" or "bottom" => new BackgroundPosition(offset ?? Inset.Percent(0), FromEnd: true),
            "center" => BackgroundPosition.Center,
            _ => new BackgroundPosition(offset ?? Inset.Percent(0)),
        };

        if (lower.Count >= 3)
        {
            // Keywords, each followed by an optional offset.
            for (var i = 0; i < lower.Count; i++)
            {
                var keyword = lower[i];
                Inset? offset = i + 1 < lower.Count && !PositionKeywords.Contains(lower[i + 1]) ? ToInset(tokens[++i], fontSize, rootFontSize) : null;
                if (keyword is "left" or "right")
                {
                    x = Keyword(keyword, offset);
                }
                else if (keyword is "top" or "bottom")
                {
                    y = Keyword(keyword, offset);
                }
            }

            return (x, y);
        }

        BackgroundPosition Value(int i) => PositionKeywords.Contains(lower[i]) ? Keyword(lower[i])
            : ToInset(tokens[i], fontSize, rootFontSize) is { } inset ? new BackgroundPosition(inset) : BackgroundPosition.Start;

        if (lower.Count == 1)
        {
            return lower[0] is "top" or "bottom" ? (BackgroundPosition.Center, Value(0)) : (Value(0), BackgroundPosition.Center);
        }

        if (lower.Count == 2)
        {
            var swap = lower[0] is "top" or "bottom" || lower[1] is "left" or "right";
            return swap ? (Value(1), Value(0)) : (Value(0), Value(1));
        }

        return (BackgroundPosition.Start, BackgroundPosition.Start);
    }

    private static BackgroundSize ParseSize(string value, double fontSize, double rootFontSize)
    {
        var parts = SplitComponents(value.ToLowerInvariant());
        if (parts is ["cover"])
        {
            return new BackgroundSize(BackgroundSizeKind.Cover);
        }

        if (parts is ["contain"])
        {
            return new BackgroundSize(BackgroundSizeKind.Contain);
        }

        var width = parts.Count > 0 ? ToInset(parts[0], fontSize, rootFontSize) : null;
        var height = parts.Count > 1 ? ToInset(parts[1], fontSize, rootFontSize) : null;
        return width is null && height is null ? default : new BackgroundSize(BackgroundSizeKind.Explicit, width, height);
    }

    private static BackgroundClip ParseBox(string value, BackgroundClip fallback) => value.Trim().ToLowerInvariant() switch
    {
        "border-box" => BackgroundClip.BorderBox,
        "padding-box" => BackgroundClip.PaddingBox,
        "content-box" => BackgroundClip.ContentBox,
        _ => fallback,
    };

    /// <summary>A length or percentage (calc() resolves with percentages of 0); <c>null</c> for auto and invalid values.</summary>
    private static Inset? ToInset(string token, double fontSize, double rootFontSize) =>
        CssValues.TryParseLength(token, fontSize, rootFontSize) switch
        {
            { Kind: CssLengthKind.Points } length => Inset.Points(length.Value),
            { Kind: CssLengthKind.Percent } length => Inset.Percent(length.Value),
            { Kind: CssLengthKind.Calc } length => Inset.Points(length.Resolve(0)),
            _ => null,
        };

    private static List<string> SplitComponents(string value) => CssValues.SplitComponents(value);

    /// <summary>Splits at a separator outside parentheses and strings, trimming the parts and dropping empty ones.</summary>
    private static List<string> Split(string value, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            var c = i < value.Length ? value[i] : separator;
            if (quote is { } q)
            {
                quote = c == q ? null : quote;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (c == separator && depth == 0)
            {
                var part = value[start..Math.Min(i, value.Length)].Trim();
                if (part.Length > 0)
                {
                    parts.Add(part);
                }

                start = i + 1;
            }
        }

        return parts;
    }

    // A declaration starts a block, follows a semicolon or starts a style attribute, possibly after comments.
    [GeneratedRegex(@"(?<=(?:^|[{;])(?:\s|/\*.*?\*/)*)background\s*:", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ShorthandRegex();

    [GeneratedRegex(@"url\(\s*(?:""(?<url>[^""]*)""|'(?<url>[^']*)'|(?<url>[^)""']*))\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"!\s*important\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImportantRegex();

    // "to" and two sides as the first argument of a linear gradient.
    [GeneratedRegex(@"(?<head>linear-gradient\(\s*)to\s+(?<sides>(?:top|bottom)\s+(?:left|right)|(?:left|right)\s+(?:top|bottom))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CornerRegex();

    [GeneratedRegex(@"^([+-]?(?:\d+\.?\d*|\.\d+))(deg|rad|grad|turn)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AngleRegex();
}
