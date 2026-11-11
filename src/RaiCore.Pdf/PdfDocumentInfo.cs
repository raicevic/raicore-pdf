using System.Globalization;
using System.Text;
using RaiCore.Pdf.Objects;

namespace RaiCore.Pdf;

/// <summary>
/// Document metadata, written both to the PDF Info dictionary and to the document's XMP metadata stream, which are kept
/// in sync.
/// </summary>
public sealed class PdfDocumentInfo
{
    /// <summary>The Info dictionary keys with their own properties, which <see cref="CustomProperties"/> cannot use.</summary>
    private static readonly HashSet<string> StandardKeys = new(StringComparer.Ordinal)
    {
        "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped",
    };

    /// <summary>Document title (XMP <c>dc:title</c>).</summary>
    public string? Title { get; set; }

    /// <summary>Author (XMP <c>dc:creator</c>, as a single entry).</summary>
    public string? Author { get; set; }

    /// <summary>Subject: a description of the document (XMP <c>dc:description</c>).</summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Keywords (XMP <c>pdf:Keywords</c>). In XMP they are also written as <c>dc:subject</c>, split at commas and
    /// semicolons.
    /// </summary>
    public string? Keywords { get; set; }

    /// <summary>The application that created the original content (XMP <c>xmp:CreatorTool</c>).</summary>
    public string? Creator { get; set; }

    /// <summary>The library that produced the PDF (XMP <c>pdf:Producer</c>).</summary>
    public string Producer { get; set; } = "RaiCore.Pdf";

    /// <summary>
    /// Creation date (XMP <c>xmp:CreateDate</c>), to the second. Defaults to the time the document is saved; set it for
    /// reproducible output.
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>
    /// Modification date (XMP <c>xmp:ModifyDate</c> and <c>xmp:MetadataDate</c>), to the second. Defaults to the
    /// creation date.
    /// </summary>
    public DateTimeOffset? ModificationDate { get; set; }

    /// <summary>
    /// Custom properties, written to the Info dictionary (the key as a name, the value as a text string) and to XMP in
    /// Adobe's <c>pdfx:</c> namespace, as Acrobat does (characters a name in XML cannot hold are encoded as
    /// <c>_xHHHH_</c>). They are written in ordinal key order. Keys must not be empty or one of the standard entries
    /// (<c>Title</c>, <c>Author</c>, <c>Subject</c>, <c>Keywords</c>, <c>Creator</c>, <c>Producer</c>,
    /// <c>CreationDate</c>, <c>ModDate</c>, <c>Trapped</c>); saving the document throws if one is.
    /// </summary>
    public IDictionary<string, string> CustomProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The custom properties in the order they are written, after checking their keys.</summary>
    internal IEnumerable<KeyValuePair<string, string>> SortedCustomProperties()
    {
        foreach (var key in CustomProperties.Keys)
        {
            if (string.IsNullOrEmpty(key) || StandardKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"'{key}' cannot be a custom document property; use the PdfDocumentInfo property for the standard entries.");
            }
        }

        return CustomProperties.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal);
    }

    /// <summary>Builds the Info dictionary with the dates the document is saved with (the same ones its XMP gets).</summary>
    internal PdfDictionary ToDictionary(DateTimeOffset created, DateTimeOffset modified)
    {
        var dict = new PdfDictionary();
        AddText(dict, "Title", Title);
        AddText(dict, "Author", Author);
        AddText(dict, "Subject", Subject);
        AddText(dict, "Keywords", Keywords);
        AddText(dict, "Creator", Creator);
        AddText(dict, "Producer", Producer);
        dict["CreationDate"] = new PdfString(Encoding.ASCII.GetBytes(FormatDate(created)));
        dict["ModDate"] = new PdfString(Encoding.ASCII.GetBytes(FormatDate(modified)));
        foreach (var (key, value) in SortedCustomProperties())
        {
            dict[new PdfName(key)] = PdfString.FromText(value);
        }

        return dict;
    }

    /// <summary>Formats a date as a PDF date string, e.g. <c>D:20261102194100+01'00'</c>.</summary>
    internal static string FormatDate(DateTimeOffset date)
    {
        var offset = date.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"D:{date:yyyyMMddHHmmss}{sign}{abs.Hours:D2}'{abs.Minutes:D2}'");
    }

    private static void AddText(PdfDictionary dict, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            dict[key] = PdfString.FromText(value);
        }
    }
}
