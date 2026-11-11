using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Xmp;

namespace RaiCore.Pdf;

// PDF/A (ISO 19005-2 and -3, level B): the checks made before writing, the sRGB output intent and the XMP
// identification. Rules that concern single objects are checked where those are written (see PdfFileWriter.PdfA):
// fonts must be embedded and must not draw .notdef, images must not be CMYK.
public sealed partial class PdfDocument
{
    /// <summary>The ICC profile of the output intent: sRGB IEC 61966-2.1, ICC v2 (CC0, Compact-ICC-Profiles).</summary>
    private static readonly Lazy<byte[]> SrgbProfile = new(() =>
    {
        const string Name = "RaiCore.Pdf.ColorProfiles.sRGB-v2-micro.icc";
        using var stream = typeof(PdfDocument).Assembly.GetManifestResourceStream(Name)
            ?? throw new InvalidOperationException($"The color profile '{Name}' is missing from the assembly.");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    });

    private PdfAConformance _pdfAConformance;

    /// <summary>
    /// Writes the document as PDF/A for long-term archiving: <see cref="PdfAConformance.PdfA2B"/> or
    /// <see cref="PdfAConformance.PdfA3B"/>. The document then gets an sRGB output intent, XMP metadata that identifies
    /// it as PDF/A (and describes custom properties in an extension schema), and every rule the library can break is
    /// checked when it is saved, which throws <see cref="InvalidOperationException"/> naming the problem:
    /// <list type="bullet">
    /// <item>every font must be embedded: the standard fonts need a <see cref="StandardFontSubstitute"/>
    /// (<c>EmbedStandardFonts()</c> in the RaiCore.Pdf.Fonts package), and Symbol and ZapfDingbats cannot be used;</item>
    /// <item>text must not draw the .notdef glyph (characters the font lacks);</item>
    /// <item>no <see cref="Encryption"/>, CMYK images, pages imported from other PDF files, or disabled
    /// <see cref="IncludeXmpMetadata"/>.</item>
    /// </list>
    /// Transparency, links, outlines and gradients are written as PDF/A allows. Defaults to
    /// <see cref="PdfAConformance.None"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the defined levels.</exception>
    public PdfAConformance PdfAConformance
    {
        get => _pdfAConformance;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown PDF/A conformance level.");
            }

            _pdfAConformance = value;
        }
    }

    /// <summary>
    /// The font embedded in place of a standard font (Helvetica, Times, Courier and their styles), or <c>null</c> to
    /// write the standard fonts unembedded (the default). The returned font must have TrueType outlines and should have
    /// the standard font's widths, as text was measured with those; it is embedded as a simple TrueType font with
    /// WinAnsi encoding. Return <c>null</c> for a font to leave it unembedded. Symbol and ZapfDingbats are never
    /// replaced. PDF/A requires every font to be embedded; <c>EmbedStandardFonts()</c> in the RaiCore.Pdf.Fonts package
    /// sets this to the metric-compatible Liberation fonts.
    /// </summary>
    public Func<StandardFont, PdfFont?>? StandardFontSubstitute { get; set; }

    /// <summary>Checks the document-wide PDF/A rules before anything is written.</summary>
    private void CheckPdfA()
    {
        if (PdfAConformance == PdfAConformance.None)
        {
            return;
        }

        if (Encryption is not null)
        {
            throw new InvalidOperationException("PDF/A does not allow encryption: set either PdfAConformance or Encryption, not both.");
        }

        if (!IncludeXmpMetadata)
        {
            throw new InvalidOperationException("PDF/A requires XMP metadata: IncludeXmpMetadata cannot be turned off.");
        }

        if (_pages.Any(page => page.Source is not null || page.DrawsOtherFiles))
        {
            throw new InvalidOperationException(
                "Pages imported from (or drawn from) other PDF files cannot be written as PDF/A: their content cannot be checked against its rules.");
        }
    }

    /// <summary>The PDF/A output intent: every device color is sRGB.</summary>
    private void WriteOutputIntent(PdfFileWriter writer, PdfDictionary catalog)
    {
        if (PdfAConformance == PdfAConformance.None)
        {
            return;
        }

        var profile = PdfStreamObject.CreateCompressed(SrgbProfile.Value);
        profile.Dictionary["N"] = new PdfInteger(3);
        catalog["OutputIntents"] = new PdfArray(new PdfDictionary
        {
            ["Type"] = new PdfName("OutputIntent"),
            ["S"] = new PdfName("GTS_PDFA1"),
            ["OutputConditionIdentifier"] = PdfString.FromText("sRGB IEC61966-2.1"),
            ["RegistryName"] = PdfString.FromText("http://www.color.org"),
            ["Info"] = PdfString.FromText("sRGB IEC61966-2.1"),
            ["DestOutputProfile"] = writer.Add(profile),
        });
    }

    /// <summary>
    /// Adds the PDF/A identification (<c>pdfaid:part</c>, <c>pdfaid:conformance</c>) and the extension schema that
    /// describes the custom properties (<c>pdfx:</c>, which PDF/A does not predefine).
    /// </summary>
    private void AddPdfAMetadata(XmpMetadata xmp)
    {
        if (PdfAConformance == PdfAConformance.None)
        {
            return;
        }

        if (xmp.Schemas.FirstOrDefault(s => s.Namespace.Uri == XmpNamespace.PdfExtension.Uri) is { Properties.Count: > 0 } custom)
        {
            PdfAExtensionSchema.Add(
                xmp,
                XmpNamespace.PdfExtension,
                "Custom document information properties",
                custom.Properties.Select(p => new PdfAExtensionProperty(p.Name, "Text", "external", "A custom document information entry")));
        }

        xmp.Schema(XmpNamespace.PdfAId)
            .Set("part", PdfAConformance == PdfAConformance.PdfA2B ? "2" : "3")
            .Set("conformance", "B");
    }
}
