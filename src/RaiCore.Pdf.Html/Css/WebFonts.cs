using System.Globalization;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Fonts.OpenType;
using RaiCore.Pdf.Html.Diagnostics;

namespace RaiCore.Pdf.Html.Css;

/// <summary>A stylesheet's CSS text and the URI its relative URLs resolve against.</summary>
internal readonly record struct StyleSheetText(string Css, Uri? BaseUri)
{
    /// <summary>The style sheet as written, before the rewrites for AngleSharp (for diagnostics); <c>null</c> for the user agent's.</summary>
    public string? Original { get; init; }
}

/// <summary>
/// Fonts declared with <c>@font-face</c> rules (CSS Fonts 4). Faces are matched by style and weight (weight ranges
/// for variable fonts), loaded on first use through the resource loader, and restricted to their
/// <c>unicode-range</c>, so a family split into per-script files (as Google Fonts serves them) works as one font.
/// </summary>
internal sealed class WebFonts
{
    /// <summary>Formats in <c>format()</c> hints that <see cref="PdfFont.Load(byte[], int)"/> can read.</summary>
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "woff", "woff2", "truetype", "opentype", "collection", "truetype-variations", "opentype-variations", "woff-variations", "woff2-variations",
    };

    private readonly List<Face> _faces;
    private readonly ResourceLoader _loader;
    private readonly FontCollection _local;

    private readonly DiagnosticCollector _diagnostics;

    private WebFonts(List<Face> faces, ResourceLoader loader, FontCollection local, DiagnosticCollector diagnostics)
    {
        _faces = faces;
        _loader = loader;
        _local = local;
        _diagnostics = diagnostics;
    }

    /// <summary>Reads the <c>@font-face</c> rules of the stylesheets; fonts are loaded later, when text needs them.</summary>
    public static WebFonts Load(IEnumerable<StyleSheetText> styleSheets, ResourceLoader loader, FontCollection local, DiagnosticCollector? diagnostics = null)
    {
        var faces = new List<Face>();
        foreach (var sheet in styleSheets)
        {
            foreach (var rule in CssRuleScanner.ScanFontFaces(sheet.Css))
            {
                if (Face.Parse(rule, sheet.BaseUri, faces.Count) is { } face)
                {
                    faces.Add(face);
                }
            }
        }

        return new WebFonts(faces, loader, local, diagnostics ?? DiagnosticCollector.None);
    }

    /// <summary>Whether a <c>@font-face</c> rule declares the family (it then hides installed fonts of that name).</summary>
    public bool Declares(string family) => _faces.Exists(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The family's faces that best match the weight and style, in the order to try them: when several faces share
    /// the best descriptors (e.g. one file per script), all of them, the last declared first. Faces that cannot be
    /// loaded are left out.
    /// </summary>
    public List<PdfFont> Find(string family, int weight, bool italic)
    {
        var candidates = _faces.Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
        var result = new List<PdfFont>();
        while (candidates.Count > 0)
        {
            // Font style first, then weight (CSS Fonts 4 section 5.2).
            var styled = candidates.Where(f => f.Italic == italic).ToList();
            if (styled.Count == 0)
            {
                styled = candidates;
            }

            var best = styled.MinBy(f => WeightDistance(f, weight))!;
            var group = styled.Where(f => f.MinWeight == best.MinWeight && f.MaxWeight == best.MaxWeight && f.Italic == best.Italic).ToList();
            foreach (var face in group.OrderByDescending(f => f.Order))
            {
                if (Load(face) is { } font)
                {
                    result.Add(font);
                }
            }

            if (result.Count > 0)
            {
                return result;
            }

            // None of the best faces could be loaded: try the next best ones.
            candidates.RemoveAll(group.Contains);
        }

        return result;
    }

    /// <summary>
    /// How far a face's weight (range) is from the desired weight, ordered like the CSS font matching algorithm:
    /// 0 inside the range; for 400-500, heavier faces up to 500, then lighter ones, then heavier ones; lighter first
    /// below 400; heavier first above 500.
    /// </summary>
    private static int WeightDistance(Face face, int weight)
    {
        if (weight >= face.MinWeight && weight <= face.MaxWeight)
        {
            return 0;
        }

        var heavier = face.MinWeight > weight;
        var gap = heavier ? face.MinWeight - weight : weight - face.MaxWeight;
        return weight switch
        {
            >= 400 and <= 500 when heavier && face.MinWeight <= 500 => gap,
            >= 400 and <= 500 => (heavier ? 2000 : 1000) + gap,
            < 400 => (heavier ? 1000 : 0) + gap,
            _ => (heavier ? 0 : 1000) + gap,
        };
    }

    private PdfFont? Load(Face face)
    {
        if (face.Loaded)
        {
            return face.Font;
        }

        face.Loaded = true;
        (HtmlDiagnosticKind Kind, string Subject, string Message)? failure = null;
        foreach (var source in face.Sources)
        {
            if (LoadSource(face, source, out var sourceFailure) is { } font)
            {
                face.Font = face.Ranges is { } ranges ? font.WithUnicodeRanges(ranges) : font;
                return face.Font;
            }

            failure = sourceFailure ?? failure;
        }

        // Only a face none of whose sources loaded is worth reporting: trying the next source is what src lists are for.
        if (failure is { } failed)
        {
            _diagnostics.Report(failed.Kind, HtmlDiagnosticSeverity.Warning, failed.Subject, $"The font-family \"{face.Family}\" falls back to another font. {failed.Message}");
        }

        return null;
    }

    private PdfFont? LoadSource(Face face, FontSource source, out (HtmlDiagnosticKind Kind, string Subject, string Message)? failure)
    {
        failure = null;
        if (source.Local is { } name)
        {
            return _local.Find(name, face.MinWeight, face.Italic); // An installed or configured font.
        }

        if (source.Format is { } format && !SupportedFormats.Contains(format))
        {
            return null; // e.g. embedded-opentype or svg: skipped without downloading.
        }

        var url = source.Url!;
        var subject = Resources.Subject(url);
        var uri = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null
            : Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute
            : face.BaseUri is not null && Uri.TryCreate(face.BaseUri, url, out var relative) ? relative
            : null;
        var data = Resources.Fetch(_loader, url, uri, "font", out var fetchFailure);
        if (data is null)
        {
            failure = fetchFailure is { } f ? (f.Kind, subject, f.Message) : null;
            return null;
        }

        try
        {
            var font = PdfFont.Load(data);

            // A font whose license forbids embedding cannot be drawn: fall back as for one that cannot be read.
            if (font is OpenTypePdfFont { Font.EmbeddingRestricted: true })
            {
                failure = (HtmlDiagnosticKind.FontNotEmbeddable, subject, $"The font {subject} may not be embedded: its license (OS/2 fsType) forbids it.");
                return null;
            }

            return font;
        }
        catch (FontFormatException ex)
        {
            // Not a font we can read, or one whose license forbids embedding.
            failure = (ex.Message.Contains("embed", StringComparison.OrdinalIgnoreCase) ? HtmlDiagnosticKind.FontNotEmbeddable : HtmlDiagnosticKind.ResourceInvalid,
                subject, $"The font {subject} cannot be used: {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            failure = (HtmlDiagnosticKind.ResourceInvalid, subject, $"The font {subject} cannot be used: {ex.Message}");
            return null;
        }
        catch (ResourceLimitExceededException ex) when (Resources.IsPerResource(ex))
        {
            // A WOFF or WOFF2 file that decompresses to too much.
            failure = (HtmlDiagnosticKind.ResourceTooLarge, subject, $"The font {subject} is too large: {ex.Message}");
            return null;
        }
    }

    private readonly record struct FontSource(string? Url, string? Local, string? Format);

    private sealed class Face
    {
        public required string Family { get; init; }

        public required int MinWeight { get; init; }

        public required int MaxWeight { get; init; }

        public required bool Italic { get; init; }

        public UnicodeRange[]? Ranges { get; init; }

        public required List<FontSource> Sources { get; init; }

        public Uri? BaseUri { get; init; }

        public required int Order { get; init; }

        public bool Loaded { get; set; }

        public PdfFont? Font { get; set; }

        public static Face? Parse(IReadOnlyList<CssDeclaration> descriptors, Uri? baseUri, int order)
        {
            string? Get(string name) => descriptors.LastOrDefault(d => d.Property == name).Value;

            var family = Unquote(Get("font-family") ?? string.Empty);
            var sources = ParseSources(Get("src") ?? string.Empty);
            if (family.Length == 0 || sources.Count == 0)
            {
                return null;
            }

            var (min, max) = ParseWeight(Get("font-weight"));
            var style = Get("font-style")?.Trim() ?? "normal";
            return new Face
            {
                Family = family,
                MinWeight = min,
                MaxWeight = max,
                Italic = style.StartsWith("italic", StringComparison.OrdinalIgnoreCase) || style.StartsWith("oblique", StringComparison.OrdinalIgnoreCase),
                Ranges = Get("unicode-range") is { } ranges ? ParseRanges(ranges) : null,
                Sources = sources,
                BaseUri = baseUri,
                Order = order,
            };
        }

        /// <summary><c>src</c>: comma-separated <c>url(...) format(...)</c> and <c>local(...)</c> entries.</summary>
        private static List<FontSource> ParseSources(string value)
        {
            var sources = new List<FontSource>();
            foreach (var entry in SplitTopLevel(value, ','))
            {
                var local = Function(entry, "local");
                var url = Function(entry, "url");
                var format = Function(entry, "format");
                if (local is not null)
                {
                    sources.Add(new FontSource(null, local, null));
                }
                else if (url is { Length: > 0 })
                {
                    sources.Add(new FontSource(url, null, format));
                }
            }

            return sources;
        }

        /// <summary>The unquoted argument of <c>name(...)</c> in a <c>src</c> entry, or <c>null</c>.</summary>
        private static string? Function(string entry, string name)
        {
            var start = entry.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || (start > 0 && (char.IsLetterOrDigit(entry[start - 1]) || entry[start - 1] == '-')))
            {
                return null;
            }

            var open = start + name.Length + 1;
            var quote = entry[open..].TrimStart() is { Length: > 0 } rest && rest[0] is '"' or '\'' ? rest[0] : '\0';
            int end;
            if (quote != '\0')
            {
                var first = entry.IndexOf(quote, open);
                end = entry.IndexOf(quote, first + 1);
                return end < 0 ? null : entry[(first + 1)..end];
            }

            end = entry.IndexOf(')', open);
            return end < 0 ? null : entry[open..end].Trim();
        }

        private static (int Min, int Max) ParseWeight(string? value)
        {
            var weights = (value ?? "normal").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => token.ToUpperInvariant() switch
            {
                "NORMAL" => 400,
                "BOLD" => 700,
                _ => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? (int)Math.Clamp(number, 1, 1000) : 400,
            }).ToList();
            return weights.Count switch
            {
                0 => (400, 400),
                1 => (weights[0], weights[0]),
                _ => (Math.Min(weights[0], weights[1]), Math.Max(weights[0], weights[1])),
            };
        }

        /// <summary><c>unicode-range</c>: <c>U+0025-00FF</c>, <c>U+4??</c> (wildcards) and single code points.</summary>
        private static UnicodeRange[]? ParseRanges(string value)
        {
            var ranges = new List<UnicodeRange>();
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var body = part[2..];
                var dash = body.IndexOf('-', StringComparison.Ordinal);
                int first, last;
                if (dash >= 0)
                {
                    if (!TryHex(body[..dash], out first) || !TryHex(body[(dash + 1)..], out last))
                    {
                        continue;
                    }
                }
                else if (body.Contains('?', StringComparison.Ordinal))
                {
                    if (!TryHex(body.Replace('?', '0'), out first) || !TryHex(body.Replace('?', 'F'), out last))
                    {
                        continue;
                    }
                }
                else if (TryHex(body, out first))
                {
                    last = first;
                }
                else
                {
                    continue;
                }

                if (first <= last)
                {
                    ranges.Add(new UnicodeRange(first, Math.Min(last, 0x10FFFF)));
                }
            }

            return ranges.Count > 0 ? [.. ranges] : null;

            static bool TryHex(string text, out int value) =>
                int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 0x10FFFF;
        }

        private static string Unquote(string value) => value.Trim().Trim('"', '\'').Trim();

        /// <summary>Splits on a separator outside quotes and parentheses.</summary>
        private static IEnumerable<string> SplitTopLevel(string value, char separator)
        {
            var depth = 0;
            var quote = '\0';
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'')
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
                    yield return value[start..i].Trim();
                    start = i + 1;
                }
            }

            if (start < value.Length)
            {
                yield return value[start..].Trim();
            }
        }
    }
}
