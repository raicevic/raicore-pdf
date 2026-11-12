using System.Xml;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Xmp;

namespace RaiCore.Pdf;

// Document metadata: the Info dictionary, the XMP metadata stream and the document language. Everything that writes
// them is here, so the Info dictionary and XMP stay in sync.
public sealed partial class PdfDocument
{
    /// <summary>
    /// The natural language of the document as a BCP 47 tag, such as <c>en-US</c> or <c>da</c>: written as the
    /// catalog's <c>/Lang</c> (used by screen readers and text-to-speech) and as XMP <c>dc:language</c>. <c>null</c>
    /// (the default) writes neither.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Whether to write the <see cref="Info"/> and <see cref="Language"/> as an XMP metadata stream as well (an
    /// uncompressed <c>/Metadata</c> stream in the catalog), as PDF 2.0, PDF/A and PDF/UA expect. Defaults to
    /// <c>true</c>; it adds about 4 KB.
    /// </summary>
    public bool IncludeXmpMetadata { get; set; } = true;

    /// <summary>
    /// Writes the document's metadata: <c>/Lang</c> and the XMP <c>/Metadata</c> stream in the catalog, and the Info
    /// dictionary, whose reference is returned for the trailer. Both get the same dates.
    /// </summary>
    private PdfReference WriteMetadata(PdfFileWriter writer, PdfDictionary catalog)
    {
        var created = Info.CreationDate ?? DateTimeOffset.Now;
        var modified = Info.ModificationDate ?? created;

        var language = Language?.Trim();
        if (!string.IsNullOrEmpty(language))
        {
            catalog["Lang"] = PdfString.FromText(language);
        }

        if (IncludeXmpMetadata)
        {
            // PDF/A and PDF/UA add their identification and extension schemas to this metadata before it is written.
            var xmp = CreateXmpMetadata(created, modified);
            AddPdfAMetadata(xmp);
            catalog["Metadata"] = writer.Add(XmpWriter.CreateStream(xmp));
        }

        return writer.Add(Info.ToDictionary(created, modified));
    }

    /// <summary>
    /// Builds the XMP for the document information: the Dublin Core, XMP basic and Adobe PDF schemas with the values
    /// of the Info dictionary (as PDF/A requires them to match), and the custom properties in <c>pdfx:</c>.
    /// </summary>
    internal XmpMetadata CreateXmpMetadata(DateTimeOffset created, DateTimeOffset modified)
    {
        var xmp = new XmpMetadata();
        var info = Info;

        var dc = xmp.Schema(XmpNamespace.DublinCore);
        dc.Set("format", "application/pdf");
        if (!string.IsNullOrEmpty(info.Title))
        {
            dc.Set("title", XmpLanguageAlternative.Default(info.Title));
        }

        if (!string.IsNullOrEmpty(info.Author))
        {
            // One entry: PDF/A requires dc:creator to hold exactly the Info dictionary's /Author.
            dc.Set("creator", XmpArray.Seq(info.Author));
        }

        if (!string.IsNullOrEmpty(info.Subject))
        {
            dc.Set("description", XmpLanguageAlternative.Default(info.Subject));
        }

        if (SplitKeywords(info.Keywords) is { Count: > 0 } keywords)
        {
            dc.Set("subject", XmpArray.Bag(keywords.Select(k => (XmpValue)k)));
        }

        if (Language?.Trim() is { Length: > 0 } language)
        {
            dc.Set("language", XmpArray.Bag(language));
        }

        var basic = xmp.Schema(XmpNamespace.Basic);
        basic.Set("CreateDate", XmpMetadata.FormatDate(created));
        basic.Set("ModifyDate", XmpMetadata.FormatDate(modified));
        basic.Set("MetadataDate", XmpMetadata.FormatDate(modified));
        if (!string.IsNullOrEmpty(info.Creator))
        {
            basic.Set("CreatorTool", info.Creator);
        }

        var pdf = xmp.Schema(XmpNamespace.AdobePdf);
        if (!string.IsNullOrEmpty(info.Producer))
        {
            pdf.Set("Producer", info.Producer);
        }

        if (!string.IsNullOrEmpty(info.Keywords))
        {
            pdf.Set("Keywords", info.Keywords);
        }

        if (PdfUA)
        {
            xmp.Schema(XmpNamespace.PdfUAIdentification).Set("part", "1");
        }

        var custom = info.SortedCustomProperties().ToList();
        if (custom.Count > 0)
        {
            var pdfx = xmp.Schema(XmpNamespace.PdfExtension);
            foreach (var (key, value) in custom)
            {
                pdfx.Set(XmlConvert.EncodeLocalName(key), value);
            }
        }

        return xmp;
    }

    /// <summary>Splits keywords at commas and semicolons, trimmed, leaving out empty ones and repeats.</summary>
    internal static List<string> SplitKeywords(string? keywords) =>
        string.IsNullOrWhiteSpace(keywords)
            ? []
            : [.. keywords.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
}
