using System.Runtime.CompilerServices;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Layout;

/// <summary>
/// Results remembered while one document is laid out, so that work repeated by measuring is done once: paragraph
/// layouts by width (tables, flex items and the start heights of blocks lay a paragraph out to measure it, then again
/// to place it), paragraphs' intrinsic widths, and shaped text (spans are shaped to measure them, for their intrinsic
/// widths and for each line).
/// </summary>
/// <remarks>
/// <para>
/// A cache lives for one layout pass on one thread (<see cref="Enter"/>, e.g. around <c>Paginator.Paginate</c>), so it
/// is never shared between threads or documents and needs no locking; nested passes on the thread use the outer one.
/// Everything it hands out is immutable, so sharing a result between callers cannot change the output: a document laid
/// out with the cache is byte for byte the one laid out without it.
/// </para>
/// <para>
/// Paragraphs are keyed by instance and their <see cref="Paragraph.Version"/>, which every change to a paragraph
/// increments; layouts whose lines depend on more than the width (beside floats, see <see cref="LineSpace"/>) are never
/// cached. Shaped text is keyed by its content: text, fonts, features, language and direction.
/// </para>
/// </remarks>
internal sealed class LayoutCache
{
    /// <summary>The most shaped texts kept (a bound on the memory of very long documents); past it the cache starts over.</summary>
    private const int MaxShapedTexts = 100_000;

    [ThreadStatic]
    private static LayoutCache? t_current;

    private static readonly AsyncLocal<bool> DisabledFlag = new();

    private readonly Dictionary<Paragraph, ParagraphEntry> _paragraphs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ShapeKey, ShapedText> _shapes = [];

    /// <summary>The cache of the layout pass running on this thread, or <c>null</c> outside of one.</summary>
    public static LayoutCache? Current => t_current;

    /// <summary>
    /// Turns caching off for the current execution context (tests compare the output with and without it).
    /// </summary>
    internal static bool Disabled
    {
        get => DisabledFlag.Value;
        set => DisabledFlag.Value = value;
    }

    /// <summary>Starts a layout pass on this thread: a new cache, unless a pass is already running. Dispose to end it.</summary>
    public static Scope Enter()
    {
        if (t_current is not null || Disabled)
        {
            return default;
        }

        t_current = new LayoutCache();
        return new Scope(owner: true);
    }

    /// <summary>The paragraph laid out at <paramref name="width"/>, from the cache or by <paramref name="layout"/>.</summary>
    public ParagraphLayout Layout(Paragraph paragraph, double width, Func<Paragraph, double, ParagraphLayout> layout)
    {
        var entry = EntryFor(paragraph);
        foreach (var (cachedWidth, cached) in entry.Layouts)
        {
            if (cachedWidth.Equals(width))
            {
                return cached;
            }
        }

        var result = layout(paragraph, width);
        entry.Layouts.Add((width, result));
        return result;
    }

    /// <summary>The paragraph's intrinsic widths, from the cache or by <paramref name="measure"/>.</summary>
    public (double Min, double Max) Intrinsic(Paragraph paragraph, Func<Paragraph, (double Min, double Max)> measure)
    {
        var entry = EntryFor(paragraph);
        entry.Intrinsic ??= measure(paragraph);
        return entry.Intrinsic.Value;
    }

    /// <summary>Shaped text, from the cache or by <paramref name="shape"/>.</summary>
    public ShapedText Shape(string text, IReadOnlyList<PdfFont> fonts, ShapingOptions options, TextDirection? direction, Func<ShapedText> shape)
    {
        var key = new ShapeKey(text, fonts, options, direction);
        if (_shapes.TryGetValue(key, out var shaped))
        {
            return shaped;
        }

        if (_shapes.Count >= MaxShapedTexts)
        {
            _shapes.Clear();
        }

        shaped = shape();
        _shapes[key] = shaped;
        return shaped;
    }

    private ParagraphEntry EntryFor(Paragraph paragraph)
    {
        if (!_paragraphs.TryGetValue(paragraph, out var entry) || entry.Version != paragraph.Version)
        {
            entry = new ParagraphEntry(paragraph.Version);
            _paragraphs[paragraph] = entry;
        }

        return entry;
    }

    /// <summary>Ends the layout pass that created it (does nothing for a nested pass).</summary>
    public readonly struct Scope(bool owner) : IDisposable
    {
        public void Dispose()
        {
            if (owner)
            {
                t_current = null;
            }
        }
    }

    private sealed class ParagraphEntry(int version)
    {
        public int Version { get; } = version;

        /// <summary>Layouts by width; a paragraph is rarely laid out at more than one or two.</summary>
        public List<(double Width, ParagraphLayout Layout)> Layouts { get; } = [];

        public (double Min, double Max)? Intrinsic { get; set; }
    }

    /// <summary>What shaping depends on. Fonts are compared by instance, features and language by value.</summary>
    private readonly struct ShapeKey(string text, IReadOnlyList<PdfFont> fonts, ShapingOptions options, TextDirection? direction) : IEquatable<ShapeKey>
    {
        private readonly string _text = text;
        private readonly IReadOnlyList<PdfFont> _fonts = fonts;
        private readonly ShapingOptions _options = options;
        private readonly TextDirection? _direction = direction;

        public bool Equals(ShapeKey other)
        {
            if (!string.Equals(_text, other._text, StringComparison.Ordinal)
                || _direction != other._direction
                || !string.Equals(_options.Language, other._options.Language, StringComparison.Ordinal)
                || _fonts.Count != other._fonts.Count
                || _options.Features.Count != other._options.Features.Count)
            {
                return false;
            }

            for (var i = 0; i < _fonts.Count; i++)
            {
                if (!ReferenceEquals(_fonts[i], other._fonts[i]))
                {
                    return false;
                }
            }

            for (var i = 0; i < _options.Features.Count; i++)
            {
                if (!string.Equals(_options.Features[i], other._options.Features[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ShapeKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_text, StringComparer.Ordinal);
            hash.Add(_direction);
            foreach (var font in _fonts)
            {
                hash.Add(RuntimeHelpers.GetHashCode(font));
            }

            return hash.ToHashCode();
        }
    }
}
