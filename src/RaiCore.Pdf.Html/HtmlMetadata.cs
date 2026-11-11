using AngleSharp.Dom;

namespace RaiCore.Pdf.Html;

/// <summary>
/// The document metadata an HTML document declares: <c>&lt;title&gt;</c>, <c>&lt;html lang&gt;</c> and the
/// <c>author</c>, <c>description</c>, <c>keywords</c> and <c>generator</c> <c>&lt;meta&gt;</c> elements.
/// </summary>
internal sealed record HtmlMetadata(string? Title, string? Author, string? Description, string? Keywords, string? Generator, string? Language)
{
    /// <summary>The PDF creator when neither the options nor the document name one.</summary>
    public const string DefaultCreator = "RaiCore.Pdf.Html";

    /// <summary>Reads the metadata of a parsed document. Empty values count as absent.</summary>
    public static HtmlMetadata Read(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HtmlMetadata(
            Clean(document.Title),
            Meta(document, "author"),
            Meta(document, "description"),
            Meta(document, "keywords"),
            Meta(document, "generator"),
            ReadLanguage(document));
    }

    /// <summary>
    /// Writes the metadata to <paramref name="pdf"/>'s <see cref="PdfDocument.Info"/> and
    /// <see cref="PdfDocument.Language"/>; values set in <paramref name="options"/> take precedence.
    /// </summary>
    public void ApplyTo(PdfDocument pdf, HtmlConverterOptions options, string defaultCreator)
    {
        pdf.Info.Title = Given(options.Title) ?? Title;
        pdf.Info.Author = Given(options.Author) ?? Author;
        pdf.Info.Subject = Given(options.Subject) ?? Description;
        pdf.Info.Keywords = Given(options.Keywords) ?? Keywords;
        pdf.Info.Creator = Given(options.Creator) ?? Generator ?? defaultCreator;
        pdf.Language = Given(options.Language) ?? Language;
    }

    /// <summary>An option's value as given, or <c>null</c> when it is empty.</summary>
    private static string? Given(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The first non-empty <c>&lt;meta name="..." content="..."&gt;</c> with the given name (case-insensitive).</summary>
    private static string? Meta(IDocument document, string name) =>
        document.QuerySelectorAll("meta[name][content]")
            .Where(m => string.Equals(m.GetAttribute("name")?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(m => Clean(m.GetAttribute("content")))
            .FirstOrDefault(c => c is not null);

    /// <summary>
    /// The language of the root element (<c>lang</c>, or <c>xml:lang</c> in XHTML), else the first language of a
    /// <c>&lt;meta http-equiv="content-language"&gt;</c> pragma.
    /// </summary>
    private static string? ReadLanguage(IDocument document)
    {
        if (document.DocumentElement is { } root)
        {
            // An empty lang means "unknown" and wins over xml:lang, as in HTML.
            if (root.HasAttribute("lang"))
            {
                return Clean(root.GetAttribute("lang"));
            }

            if (Clean(root.GetAttribute("xml:lang") ?? root.GetAttribute("http://www.w3.org/XML/1998/namespace", "lang")) is { } xmlLang)
            {
                return xmlLang;
            }
        }

        var pragma = document.QuerySelectorAll("meta[http-equiv][content]")
            .FirstOrDefault(m => string.Equals(m.GetAttribute("http-equiv")?.Trim(), "content-language", StringComparison.OrdinalIgnoreCase));
        return Clean(pragma?.GetAttribute("content")?.Split(',')[0]);
    }

    /// <summary>Collapses whitespace runs to single spaces and trims; <c>null</c> when nothing is left.</summary>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
