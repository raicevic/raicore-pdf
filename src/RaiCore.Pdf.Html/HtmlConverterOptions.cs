using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.Layout.Blocks;

namespace RaiCore.Pdf.Html;

/// <summary>Loads a resource (image or stylesheet) referenced by the document. Returns <c>null</c> if it cannot be loaded.</summary>
public delegate byte[]? ResourceLoader(Uri uri);

/// <summary>Options for <see cref="HtmlToPdf"/>.</summary>
public sealed class HtmlConverterOptions
{
    /// <summary>
    /// Fonts used for CSS <c>font-family</c>. Defaults to the bundled Liberation fonts only, so output is identical on
    /// every machine; add <see cref="FontCollection.AddSystemFonts"/> or your own font files for more families and scripts.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// Fonts appended to every <c>font-family</c> list, tried for characters the chosen fonts lack (e.g. a CJK or emoji font).
    /// </summary>
    public IReadOnlyList<string> FallbackFontFamilies { get; init; } = [];

    /// <summary>The page size and margins when the CSS has no <c>@page</c> rule. Defaults to A4 with 2 cm margins.</summary>
    public PageSetup Page { get; init; } = new(PdfPageSize.A4, Thickness.All(72 / 2.54 * 2));

    /// <summary>The URI that relative URLs (images, stylesheets) resolve against, e.g. the folder containing the HTML file.</summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Loads images and stylesheets. The default allows only <c>data:</c> URIs and <c>file:</c> URIs; network access
    /// is off unless you supply a loader that performs it.
    /// </summary>
    public ResourceLoader ResourceLoader { get; init; } = Resources.Default;

    /// <summary>Extra CSS applied after the document's own styles (as if it were the last stylesheet).</summary>
    public string? UserStyleSheet { get; init; }

    /// <summary>
    /// Whether to create PDF bookmarks (the document outline): from <c>h1</c>-<c>h6</c> by default, customized with the
    /// CSS properties <c>bookmark-level</c>, <c>bookmark-label</c> and <c>bookmark-state</c>. Defaults to <c>true</c>.
    /// </summary>
    public bool CreateBookmarks { get; init; } = true;

    /// <summary>The PDF's title. Overrides the document's <c>&lt;title&gt;</c>.</summary>
    public string? Title { get; init; }

    /// <summary>The PDF's author. Overrides the document's <c>&lt;meta name="author"&gt;</c>.</summary>
    public string? Author { get; init; }

    /// <summary>The PDF's subject. Overrides the document's <c>&lt;meta name="description"&gt;</c>.</summary>
    public string? Subject { get; init; }

    /// <summary>The PDF's keywords, separated by commas. Override the document's <c>&lt;meta name="keywords"&gt;</c>.</summary>
    public string? Keywords { get; init; }

    /// <summary>
    /// The application that created the content. Overrides the document's <c>&lt;meta name="generator"&gt;</c>;
    /// defaults to the converting package (<c>RaiCore.Pdf.Html</c> or <c>RaiCore.Pdf.Razor</c>).
    /// </summary>
    public string? Creator { get; init; }

    /// <summary>
    /// The document language as a BCP 47 tag, such as <c>en-US</c>, written as the PDF's <c>/Lang</c> and XMP
    /// <c>dc:language</c>. Overrides the document's <c>&lt;html lang&gt;</c> (or <c>xml:lang</c>).
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Password protection and permissions for the PDF (see <see cref="PdfEncryption"/>). <c>null</c> (the default)
    /// writes an unencrypted document. Sets <see cref="PdfDocument.Encryption"/> on the converted document.
    /// </summary>
    public PdfEncryption? Encryption { get; init; }
}

/// <summary>Built-in resource loading.</summary>
public static class Resources
{
    /// <summary>Loads <c>data:</c> and <c>file:</c> URIs; everything else returns <c>null</c>.</summary>
    public static byte[]? Default(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme == "data")
        {
            return DecodeDataUri(uri.OriginalString);
        }

        if (uri.IsFile && File.Exists(uri.LocalPath))
        {
            return File.ReadAllBytes(uri.LocalPath);
        }

        return null;
    }

    /// <summary>Decodes a <c>data:[mediatype][;base64],payload</c> URI.</summary>
    public static byte[]? DecodeDataUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
        {
            return null;
        }

        var header = uri[5..comma];
        var payload = uri[(comma + 1)..];
        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Uri.UnescapeDataString(payload).Replace(" ", string.Empty, StringComparison.Ordinal))
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
