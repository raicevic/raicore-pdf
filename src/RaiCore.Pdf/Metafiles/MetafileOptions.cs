using RaiCore.Pdf.Fonts;

namespace RaiCore.Pdf.Metafiles;

/// <summary>
/// How a converter draws WMF and EMF pictures (<see cref="PdfImage.LoadMetafile"/>): the fonts for their text and where
/// problems go.
/// </summary>
internal sealed class MetafileOptions
{
    /// <summary>Options without fonts (text in the standard PDF fonts) or diagnostics, as <see cref="PdfImage.Load(byte[])"/> uses.</summary>
    public static MetafileOptions Default { get; } = new();

    /// <summary>
    /// The fonts for a logical font's face name, bold and italic, in fallback order (at least one, or none for the
    /// standard fonts): the converter's own font resolution, so a picture's text matches the document's. <c>null</c>
    /// uses the standard PDF fonts (Helvetica, Times or Courier, by the face name).
    /// </summary>
    public Func<string, bool, bool, IReadOnlyList<PdfFont>>? Fonts { get; init; }

    /// <summary>
    /// Receives a message for each problem with a picture, once per picture: true with damage (a damaged record, which
    /// ends the drawing, a bitmap that cannot be read, a resource limit), false with a feature that is approximated or
    /// left out. <c>null</c> ignores them.
    /// </summary>
    public Action<string, bool>? Report { get; init; }
}

/// <summary>
/// The state of one picture being drawn: its options, limits, and the problems already reported. A picture nested in
/// another (an EMF+ image of a metafile) has the outer picture's context as its parent, and shares its counts.
/// </summary>
internal sealed class MetafileContext(MetafileOptions options, MetafileContext? parent)
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private long _elements;

    /// <summary>The largest number of nested pictures (an EMF+ image of a metafile, a metafile in a metafile).</summary>
    public const int MaxDepth = 4;

    public MetafileOptions Options { get; } = options;

    /// <summary>The picture this one is nested in, if any.</summary>
    public MetafileContext? Parent { get; } = parent;

    /// <summary>How deep this picture is nested in others: 0 for a picture of its own.</summary>
    public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;

    /// <summary>A context for a picture nested in this one.</summary>
    public MetafileContext Nested() => new(Options, this);

    /// <summary>The fonts for a logical font, in fallback order (at least one).</summary>
    public IReadOnlyList<PdfFont> Fonts(string face, bool bold, bool italic)
    {
        if (Options.Fonts?.Invoke(face, bold, italic) is { Count: > 0 } fonts)
        {
            return fonts;
        }

        return [PdfFont.Standard(StandardFontFor(face, bold, italic))];
    }

    /// <summary>The standard font closest to a face: Courier for fixed-pitch faces, Times for serif ones, else Helvetica.</summary>
    internal static StandardFont StandardFontFor(string face, bool bold, bool italic)
    {
        var style = (bold ? 1 : 0) + (italic ? 2 : 0);
        if (face.Contains("Courier", StringComparison.OrdinalIgnoreCase) || face.Contains("Mono", StringComparison.OrdinalIgnoreCase)
            || face.Contains("Consol", StringComparison.OrdinalIgnoreCase) || face.Contains("Fixed", StringComparison.OrdinalIgnoreCase))
        {
            return StandardFont.Courier + style;
        }

        string[] serif = ["Times", "Roman", "Serif", "Georgia", "Garamond", "Cambria", "Book", "Palatino", "Century", "Mincho"];
        if (!face.Contains("Sans", StringComparison.OrdinalIgnoreCase) && serif.Any(s => face.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return style switch { 1 => StandardFont.TimesBold, 2 => StandardFont.TimesItalic, 3 => StandardFont.TimesBoldItalic, _ => StandardFont.TimesRoman };
        }

        return StandardFont.Helvetica + style;
    }

    /// <summary>Reports a problem once per picture: <paramref name="damaged"/> for damage, else an unsupported feature.</summary>
    public void Report(string message, bool damaged = false)
    {
        if (Parent is { } parent)
        {
            parent.Report(message, damaged);
        }
        else if (_reported.Add(message))
        {
            Options.Report?.Invoke(message, damaged);
        }
    }

    /// <summary>
    /// Counts records and points against <see cref="ResourceLimits.MaxElements"/> (for the picture and those nested
    /// in it), and checks the time limit now and then.
    /// </summary>
    /// <exception cref="MetafileLimitException">The picture has more than the limit allows.</exception>
    public void Count(long elements)
    {
        if (Parent is { } parent)
        {
            parent.Count(elements);
            return;
        }

        _elements += elements;
        var limit = ResourceLimits.Current.MaxElements;
        if (_elements > limit)
        {
            throw new MetafileLimitException(
                $"it has more records and points than ResourceLimits.MaxElements allows ({limit}); the rest is left out");
        }

        if ((_elements & 0xFF) == 0 || elements > 256)
        {
            ResourceLimits.Checkpoint();
        }
    }
}

/// <summary>A damaged record: the picture is drawn up to it.</summary>
internal sealed class MetafileFormatException(string message) : Exception(message);

/// <summary>A picture that goes beyond a resource limit: it is drawn up to there.</summary>
internal sealed class MetafileLimitException(string message) : Exception(message);
