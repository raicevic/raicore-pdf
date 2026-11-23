using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout;
using RaiCore.Pdf.Layout.Text;

namespace RaiCore.Pdf.Html;

/// <summary>Options for <see cref="HtmlFragment"/>.</summary>
/// <remarks>
/// Options are immutable once created and can be shared by fragments parsed at the same time; a
/// <see cref="ResourceLoader"/> passed here is then called from several threads at once and must be thread-safe.
/// </remarks>
public sealed class HtmlFragmentOptions
{
    /// <summary>
    /// The text style the fragment starts from, typically the body style of the document it is placed in: its font
    /// family (the families of <see cref="TextStyle.Fonts"/>, in order, with the first font's weight and style), font
    /// size, color, line height, letter and word spacing, language and hyphenation are those of the fragment's root,
    /// which its elements inherit unless the fragment's CSS says otherwise (relative sizes such as <c>em</c>,
    /// <c>rem</c> and <c>h1</c>'s <c>2em</c> are relative to its font size). <c>null</c> (the default) starts from the
    /// defaults of HTML, as a converted document does (16 px serif text).
    /// </summary>
    /// <remarks>
    /// Bold and italic text uses the faces of the base font's family in <see cref="Fonts"/>; when <see cref="Fonts"/>
    /// has no such family, the closest of <see cref="TextStyle.Fonts"/>. The color is used with 8 bits per channel, as
    /// CSS colors are. Decorations, vertical alignment, OpenType features, <c>NoWrap</c> and the hyphenator are not
    /// carried over (hyphenation patterns come from <see cref="Hyphenators"/>).
    /// </remarks>
    public TextStyle? BaseStyle { get; init; }

    /// <summary>
    /// CSS for the fragment, as a style sheet of the document it is placed in: it applies before the fragment's own
    /// <c>&lt;style&gt;</c> elements and <c>style</c> attributes, which override it where they conflict.
    /// </summary>
    public string? Css { get; init; }

    /// <summary>
    /// The width the fragment is placed in, in points: <c>vw</c> and the percentages the converter resolves while it
    /// builds the blocks (such as <c>text-indent</c>, image sizes and table widths with <c>calc()</c>) resolve against
    /// it. Other percentages, and the lines the text wraps into, follow the width the blocks get when they are laid out.
    /// Set it to the content width of the page (<see cref="PageSetup.ContentWidth"/>) or of the container or table cell
    /// the blocks go in. Defaults to the content width of an A4 page with 2 cm margins.
    /// </summary>
    public double Width { get; init; } = DefaultWidth;

    /// <summary>The URI that relative URLs (images, stylesheets, fonts) resolve against.</summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Loads images, stylesheets and web fonts, as <see cref="HtmlConverterOptions.ResourceLoader"/> does. The default
    /// allows only <c>data:</c> URIs and <c>file:</c> URIs.
    /// </summary>
    public ResourceLoader ResourceLoader { get; init; } = ResourceLoaders.Default;

    /// <summary>
    /// Fonts used for CSS <c>font-family</c>. Defaults to the bundled Liberation fonts only (see
    /// <see cref="HtmlConverterOptions.Fonts"/>). Pass the collection the rest of the document uses so the fragment's
    /// families resolve to the same fonts.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>Fonts appended to every <c>font-family</c> list (see <see cref="HtmlConverterOptions.FallbackFontFamilies"/>).</summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>
    /// Hyphenation patterns for <c>hyphens: auto</c> by language (see <see cref="HtmlConverterOptions.Hyphenators"/>).
    /// Defaults to <c>null</c>: the bundled patterns only.
    /// </summary>
    public IReadOnlyDictionary<string, Hyphenator>? Hyphenators { get; init; }

    /// <summary>
    /// Whether headings (and elements with a CSS <c>bookmark-level</c>) become entries of the PDF outline, as in a
    /// converted document. Defaults to <c>false</c>: a fragment usually goes in a document with an outline of its own.
    /// </summary>
    public bool CreateBookmarks { get; init; }

    /// <summary>
    /// Limits on what the fragment may make parsing do (nesting depth, element and table cell counts, image pixels,
    /// resource and font sizes, time), as for a converted document (see <see cref="HtmlConverterOptions.Limits"/>).
    /// Defaults to <see cref="ResourceLimits.Default"/>.
    /// </summary>
    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>The content width of an A4 page with 2 cm margins, the default page of <see cref="HtmlConverterOptions"/>.</summary>
    internal static double DefaultWidth { get; } = new HtmlConverterOptions().Page.ContentWidth;
}
