using System.Globalization;
using System.Text.RegularExpressions;
using RaiCore.Pdf.Fonts;
using RaiCore.Pdf.IO;
using RaiCore.Pdf.Objects;
using RaiCore.Pdf.Xmp;

namespace RaiCore.Pdf;

// PDF/A (ISO 19005-2 and -3, levels B and A): the checks made before writing, the sRGB output intent and the XMP
// identification. Rules that concern single objects are checked where those are written (see PdfFileWriter.PdfA):
// fonts must be embedded and must not draw .notdef, images must not be CMYK, and at level A every glyph maps to Unicode.
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
    /// Writes the document as PDF/A for long-term archiving: <see cref="PdfAConformance.PdfA2B"/>,
    /// <see cref="PdfAConformance.PdfA3B"/>, or level A, <see cref="PdfAConformance.PdfA2A"/> and
    /// <see cref="PdfAConformance.PdfA3A"/>. The document then gets an sRGB output intent, XMP metadata that identifies
    /// it as PDF/A (and describes custom properties in an extension schema), and every rule the library can break is
    /// checked when it is saved, which throws <see cref="InvalidOperationException"/> naming the problem:
    /// <list type="bullet">
    /// <item>every font must be embedded: the standard fonts need a <see cref="StandardFontSubstitute"/>
    /// (<c>EmbedStandardFonts()</c> in the RaiCore.Pdf.Fonts package), and Symbol and ZapfDingbats cannot be used;</item>
    /// <item>text must not draw the .notdef glyph (characters the font lacks);</item>
    /// <item>no <see cref="Encryption"/>, CMYK images, pages imported from other PDF files, or disabled
    /// <see cref="IncludeXmpMetadata"/>.</item>
    /// </list>
    /// Level A makes the document <see cref="Tagged"/> (set it before drawing, so that the content is tagged), writes
    /// every glyph to the fonts' Unicode maps (a glyph without text of its own, such as the second glyph of a cluster
    /// drawn with actual text, maps to U+FFFD), and adds these checks:
    /// <list type="bullet">
    /// <item>a <see cref="Language"/>, and every language (the document's and the structure elements') a BCP 47 tag;</item>
    /// <item>structure elements of the standard types only (<see cref="PdfStructureTypes"/>; no role map is written);</item>
    /// <item>alternate text (or actual text) on every figure and formula;</item>
    /// <item>no page drawn on before the document was tagged, and no Private Use Area characters (icon fonts) drawn
    /// without actual text (<see cref="PdfCanvas.DrawGlyphs"/> takes it).</item>
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

    /// <summary>XMP properties that standards add (e.g. Factur-X's <c>fx:</c>), described in an extension schema under PDF/A.</summary>
    internal List<XmpExtension> XmpExtensions { get; } = [];

    /// <summary>Checks the document-wide PDF/A rules, and those of <paramref name="pages"/>, before they are written.</summary>
    private void CheckPdfA(IEnumerable<PdfPage> pages)
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

        if (pages.Any(page => page.Source is not null || page.DrawsOtherFiles))
        {
            throw new InvalidOperationException(
                "Pages imported from (or drawn from) other PDF files cannot be written as PDF/A: their content cannot be checked against its rules.");
        }

        if (PdfAConformance.IsLevelA())
        {
            CheckPdfALevelA(pages);
        }
    }

    /// <summary>The level A rules that can be checked before the pages are written: the language, and how they were drawn.</summary>
    private void CheckPdfALevelA(IEnumerable<PdfPage> pages)
    {
        if (string.IsNullOrWhiteSpace(Language))
        {
            throw new InvalidOperationException("PDF/A level A requires the document's language (PdfDocument.Language).");
        }

        CheckLanguageTag(Language, "The document's language (PdfDocument.Language)");
        foreach (var page in pages)
        {
            if (page.DrawnUntagged)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PDF/A level A requires tagged content, but page {PageNumber(page)} was drawn on before the document was tagged: set PdfAConformance (or Tagged) before drawing."));
            }

            if (page.PrivateUseText is { } text)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PDF/A level A requires actual text for Private Use Area characters (such as icon font symbols), but page {PageNumber(page)} draws \"{text}\" without it. Draw them with PdfCanvas.DrawGlyphs and its actualText."));
            }
        }
    }

    /// <summary>
    /// The level A rules of the logical structure, checked when it is complete (before the fonts and pages are
    /// finished): the elements written have standard types, figures and formulas a description, and languages are tags.
    /// </summary>
    private void CheckPdfAStructure()
    {
        if (!PdfAConformance.IsLevelA())
        {
            return;
        }

        var pending = new Stack<PdfStructureElement>([StructureRoot]);
        while (pending.TryPop(out var element))
        {
            if (!StandardStructureTypes.Contains(element.Type))
            {
                throw new InvalidOperationException(
                    $"PDF/A level A requires the standard structure types (PdfStructureTypes), but an element has the type \"{element.Type}\", and no role map is written.");
            }

            if (element.Type is PdfStructureTypes.Figure or PdfStructureTypes.Formula
                && string.IsNullOrWhiteSpace(element.AlternateText) && string.IsNullOrWhiteSpace(element.ActualText))
            {
                throw new InvalidOperationException(
                    $"PDF/A level A requires alternate text on figures and formulas, but a {element.Type} element has none: set PdfStructureElement.AlternateText "
                    + "(in HTML, the image's alt attribute; alt=\"\" marks a decorative image, which is not a figure).");
            }

            if (!string.IsNullOrWhiteSpace(element.Language))
            {
                CheckLanguageTag(element.Language, $"The language of a {element.Type} element");
            }

            foreach (var child in element.Children.Where(child => child.HasContent))
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>The page's number in the document, counting from 1.</summary>
    private int PageNumber(PdfPage page) => _pages.IndexOf(page) + 1;

    /// <summary>Throws unless <paramref name="language"/> is a language tag as PDF/A level A requires (ISO 32000-1, 14.9.2).</summary>
    private static void CheckLanguageTag(string language, string what)
    {
        if (!LanguageTag().IsMatch(language.Trim()))
        {
            throw new InvalidOperationException($"{what} is \"{language}\", which is not a BCP 47 language tag such as \"en\" or \"de-CH\", as PDF/A level A requires.");
        }
    }

    /// <summary>A language tag as veraPDF checks it: subtags of letters and digits joined by hyphens.</summary>
    [GeneratedRegex("^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();

    /// <summary>The standard structure types of ISO 32000-1, section 14.8.4.</summary>
    private static readonly HashSet<string> StandardStructureTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody",
        "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    };

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
    /// Adds the properties of <see cref="XmpExtensions"/>, and under PDF/A the identification (<c>pdfaid:part</c>,
    /// <c>pdfaid:conformance</c>) and the extension schemas that describe the custom properties (<c>pdfx:</c>, which
    /// PDF/A does not predefine) and the <see cref="XmpExtensions"/>.
    /// </summary>
    private void AddPdfAMetadata(XmpMetadata xmp)
    {
        foreach (var extension in XmpExtensions)
        {
            var schema = xmp.Schema(extension.Namespace);
            foreach (var (property, value) in extension.Properties)
            {
                schema.Set(property.Name, value);
            }
        }

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

        // PDF/A does not predefine PDF/UA's identification either (ISO 14289-1, 5).
        if (xmp.Schemas.FirstOrDefault(s => s.Namespace.Uri == XmpNamespace.PdfUAIdentification.Uri) is { Properties.Count: > 0 })
        {
            PdfAExtensionSchema.Add(
                xmp,
                XmpNamespace.PdfUAIdentification,
                "PDF/UA identification schema",
                [new PdfAExtensionProperty("part", "Integer", "internal", "Indicates, which part of ISO 14289 standard is followed")]);
        }

        foreach (var extension in XmpExtensions)
        {
            PdfAExtensionSchema.Add(xmp, extension.Namespace, extension.Description, extension.Properties.Select(p => p.Property));
        }

        xmp.Schema(XmpNamespace.PdfAId)
            .Set("part", PdfAConformance.Part().ToString(CultureInfo.InvariantCulture))
            .Set("conformance", PdfAConformance.IsLevelA() ? "A" : "B");
    }
}
